using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Discphew.Audio
{
    public interface IAudioReader : IDisposable
    {
        int SampleRate { get; }
        int Channels { get; }
        int BitsPerSample { get; }
        /// <summary>Length in sample frames, or -1 when unknown.</summary>
        long TotalFrames { get; }
        Dictionary<string, string> Tags { get; }
        byte[] Picture { get; }
        bool CanSeek { get; }
        /// <summary>Reads interleaved integer samples at <see cref="BitsPerSample"/>. Returns frames read, 0 at the end.</summary>
        int Read(int[] dest, int frames);
        void Seek(long frame);
    }

    public static class AudioFile
    {
        public static readonly string[] NativeExtensions = { ".flac", ".wav", ".wave" };
        public static readonly string[] FfmpegExtensions = { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".ape", ".wv", ".alac", ".mka", ".ac3", ".dts", ".tta" };

        public static bool IsAudioFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return NativeExtensions.Contains(ext) || FfmpegExtensions.Contains(ext);
        }

        public static IAudioReader Open(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".flac") return new FlacReader(path);
                if (ext == ".wav" || ext == ".wave") return new WavReader(path);
            }
            catch (Exception) when (Ffmpeg.Available)
            {
                return new FfmpegReader(path);
            }
            if (Ffmpeg.Available) return new FfmpegReader(path);
            throw new NotSupportedException($"{Path.GetFileName(path)}: {ext} files need ffmpeg (FLAC and WAV work without it).");
        }

        public static TimeSpan Duration(string path)
        {
            using var r = Open(path);
            if (r.TotalFrames > 0) return TimeSpan.FromSeconds(r.TotalFrames / (double)r.SampleRate);
            return Ffmpeg.ProbeDuration(path) ?? TimeSpan.Zero;
        }

        public static string Tag(this IAudioReader r, string key) =>
            r.Tags != null && r.Tags.TryGetValue(key, out var v) ? v : "";
    }

    // ------------------------------------------------------------------ WAV

    public sealed class WavReader : IAudioReader
    {
        readonly FileStream fs;
        readonly long dataStart, dataLength;
        readonly int format, blockAlign, containerBits;
        byte[] raw = new byte[0];

        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample { get; }
        public long TotalFrames { get; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Picture => null;
        public bool CanSeek => true;

        public WavReader(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var r = new BinaryReader(fs);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
            r.ReadUInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
            bool haveFmt = false;
            while (fs.Position + 8 <= fs.Length)
            {
                string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                long size = r.ReadUInt32();
                long next = fs.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = r.ReadUInt16();
                    Channels = r.ReadUInt16();
                    SampleRate = (int)r.ReadUInt32();
                    r.ReadUInt32();
                    blockAlign = r.ReadUInt16();
                    containerBits = r.ReadUInt16();
                    if (format == 0xFFFE && size >= 40)
                    {
                        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32();
                        format = r.ReadUInt16();
                    }
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    dataStart = fs.Position;
                    dataLength = size == 0 || size == 0xFFFFFFFF || dataStart + size > fs.Length ? fs.Length - dataStart : size;
                    break;
                }
                fs.Position = next;
            }
            if (!haveFmt || dataStart == 0) throw new InvalidDataException("WAV file has no audio.");
            if (format != 1 && format != 3) throw new InvalidDataException("Compressed WAV files aren't supported.");
            if (format == 3 && containerBits != 32 && containerBits != 64) throw new InvalidDataException("Unsupported float WAV.");
            BitsPerSample = format == 3 ? 24 : containerBits;
            if (BitsPerSample != 8 && BitsPerSample != 16 && BitsPerSample != 24 && BitsPerSample != 32) throw new InvalidDataException("Unsupported WAV bit depth.");
            TotalFrames = dataLength / blockAlign;
            fs.Position = dataStart;
        }

        public int Read(int[] dest, int frames)
        {
            long remaining = (dataStart + dataLength - fs.Position) / blockAlign;
            frames = (int)Math.Min(frames, remaining);
            if (frames <= 0) return 0;
            int bytes = frames * blockAlign;
            if (raw.Length < bytes) raw = new byte[bytes];
            int got = 0;
            while (got < bytes)
            {
                int n = fs.Read(raw, got, bytes - got);
                if (n <= 0) break;
                got += n;
            }
            frames = got / blockAlign;
            int samples = frames * Channels;
            int bytesPer = containerBits / 8;
            for (int i = 0, p = 0; i < samples; i++, p += bytesPer)
            {
                if (format == 3)
                {
                    double v = containerBits == 32 ? BitConverter.ToSingle(raw, p) : BitConverter.ToDouble(raw, p);
                    v = Math.Max(-1, Math.Min(1, v));
                    dest[i] = (int)Math.Round(v * 8388607);
                }
                else dest[i] = containerBits switch
                {
                    8 => raw[p] - 128,
                    16 => (short)(raw[p] | (raw[p + 1] << 8)),
                    24 => (raw[p] | (raw[p + 1] << 8) | (raw[p + 2] << 16)) << 8 >> 8,
                    _ => BitConverter.ToInt32(raw, p),
                };
            }
            return frames;
        }

        public void Seek(long frame) => fs.Position = dataStart + Math.Max(0, Math.Min(TotalFrames, frame)) * blockAlign;
        public void Dispose() => fs.Dispose();
    }

    public sealed class WavWriter : IDisposable
    {
        readonly FileStream fs;
        long dataBytes;

        public WavWriter(string path, int sampleRate = 44100, int channels = 2, int bits = 16)
        {
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var w = new BinaryWriter(fs);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((ushort)1); w.Write((ushort)channels); w.Write(sampleRate);
            w.Write(sampleRate * channels * bits / 8); w.Write((ushort)(channels * bits / 8)); w.Write((ushort)bits);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(0);
            w.Flush();
        }

        public void Write(byte[] b, int offset, int count) { fs.Write(b, offset, count); dataBytes += count; }

        public void Dispose()
        {
            if ((dataBytes & 1) != 0) fs.WriteByte(0);
            var w = new BinaryWriter(fs);
            fs.Position = 4; w.Write((uint)(36 + dataBytes + (dataBytes & 1)));
            fs.Position = 40; w.Write((uint)dataBytes);
            fs.Dispose();
        }
    }

    // ------------------------------------------------------------------ ffmpeg (optional)

    /// <summary>
    /// discphew never ships ffmpeg. If the user has it (PATH, next to the exe, or a path in
    /// settings) we use it for the formats we don't implement ourselves.
    /// </summary>
    public static class Ffmpeg
    {
        static string cached;
        static bool searched;

        public static string ExePath
        {
            get
            {
                if (!searched) { cached = Locate(); searched = true; }
                return cached;
            }
        }

        public static bool Available => ExePath != null;
        public static void Refresh() => searched = false;

        static string Locate()
        {
            var configured = Settings.Get("ffmpeg");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            var candidates = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe") };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"));
            candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"));
            foreach (var c in candidates)
                try { if (File.Exists(c)) return c; } catch { }
            return null;
        }

        public static Process Start(string arguments, bool stdin, bool stdout)
        {
            if (!Available) throw new NotSupportedException("ffmpeg isn't installed.");
            var psi = new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin,
                RedirectStandardOutput = stdout,
                RedirectStandardError = true,
            };
            return Process.Start(psi);
        }

        public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        public static TimeSpan? ProbeDuration(string path)
        {
            if (!Available) return null;
            try
            {
                using var p = Start($"-hide_banner -i {Quote(path)}", false, false);
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (!m.Success) return null;
                return TimeSpan.FromHours(int.Parse(m.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value))
                       + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }
    }

    /// <summary>Decodes anything ffmpeg understands to 44.1 kHz / 16-bit / stereo.</summary>
    public sealed class FfmpegReader : IAudioReader
    {
        readonly string path;
        Process proc;
        Stream stdout;
        byte[] raw = new byte[0];
        readonly StringBuilder errors = new StringBuilder();

        public int SampleRate => 44100;
        public int Channels => 2;
        public int BitsPerSample => 16;
        public long TotalFrames { get; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Picture => null;
        public bool CanSeek => true;

        public FfmpegReader(string path)
        {
            this.path = path;
            var d = Ffmpeg.ProbeDuration(path);
            TotalFrames = d.HasValue ? (long)(d.Value.TotalSeconds * 44100) : -1;
            Launch(0);
        }

        void Launch(double seconds)
        {
            Kill();
            string ss = seconds > 0 ? $"-ss {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " : "";
            proc = Ffmpeg.Start($"-hide_banner -v error {ss}-i {Ffmpeg.Quote(path)} -vn -f s16le -acodec pcm_s16le -ac 2 -ar 44100 -", false, true);
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            stdout = proc.StandardOutput.BaseStream;
        }

        public int Read(int[] dest, int frames)
        {
            int bytes = frames * 4;
            if (raw.Length < bytes) raw = new byte[bytes];
            int got = 0;
            while (got < bytes)
            {
                int n = stdout.Read(raw, got, bytes - got);
                if (n <= 0) break;
                got += n;
            }
            int samples = got / 2;
            for (int i = 0; i < samples; i++) dest[i] = (short)(raw[2 * i] | (raw[2 * i + 1] << 8));
            if (got == 0 && proc.HasExited && proc.ExitCode != 0)
                lock (errors) throw new IOException("ffmpeg: " + errors.ToString().Trim());
            return got / 4;
        }

        public void Seek(long frame) => Launch(frame / 44100.0);

        void Kill()
        {
            if (proc == null) return;
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
            proc = null;
        }

        public void Dispose() => Kill();
    }

    /// <summary>Feeds 44.1 kHz / 16-bit stereo PCM into ffmpeg to produce MP3, AAC, Opus, ...</summary>
    public sealed class FfmpegEncoder : IDisposable
    {
        readonly Process proc;
        readonly Stream stdin;
        readonly StringBuilder errors = new StringBuilder();
        bool finished;

        public FfmpegEncoder(string outPath, string codecArgs, IEnumerable<KeyValuePair<string, string>> tags, string coverPath)
        {
            var sb = new StringBuilder("-hide_banner -v error -y -f s16le -ar 44100 -ac 2 -i - ");
            bool cover = !string.IsNullOrEmpty(coverPath) && File.Exists(coverPath);
            if (cover) sb.Append($"-i {Ffmpeg.Quote(coverPath)} -map 0:a -map 1:v -c:v copy -disposition:v attached_pic ");
            sb.Append(codecArgs).Append(' ');
            if (tags != null)
                foreach (var kv in tags)
                    if (!string.IsNullOrEmpty(kv.Value)) sb.Append($"-metadata {kv.Key}={Ffmpeg.Quote(kv.Value)} ");
            sb.Append(Ffmpeg.Quote(outPath));
            proc = Ffmpeg.Start(sb.ToString(), true, false);
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            stdin = proc.StandardInput.BaseStream;
        }

        public void Write(byte[] b, int offset, int count) => stdin.Write(b, offset, count);

        public void Finish()
        {
            if (finished) return;
            finished = true;
            stdin.Close();
            proc.WaitForExit();
            if (proc.ExitCode != 0) lock (errors) throw new IOException("ffmpeg failed: " + errors.ToString().Trim());
        }

        public void Dispose()
        {
            try { if (!finished) { finished = true; stdin.Close(); if (!proc.WaitForExit(5000)) proc.Kill(); } } catch { }
            proc.Dispose();
        }
    }

    // ------------------------------------------------------------------ conversion to CD audio

    /// <summary>
    /// Turns any reader into 16-bit stereo little-endian PCM at a fixed rate (44.1 kHz — Red Book —
    /// by default). 16-bit sources at the target rate pass through bit-exact; everything else is
    /// resampled and/or dithered.
    /// </summary>
    public sealed class CdPcmStream
    {
        readonly IAudioReader src;
        readonly bool exact;
        readonly Resampler resampler;
        readonly int[] inBuf;
        readonly float[][] chans = { new float[4096], new float[4096] };
        readonly float[][] outChans = { new float[16384], new float[16384] };
        readonly Random rng = new Random(1234);
        readonly Queue<short> pending = new Queue<short>();
        bool srcDone;
        const int Chunk = 4096;

        readonly int targetRate;

        public CdPcmStream(IAudioReader reader, int targetRate = 44100)
        {
            src = reader;
            this.targetRate = targetRate;
            exact = reader.SampleRate == targetRate && reader.BitsPerSample == 16 && reader.Channels <= 2;
            inBuf = new int[Chunk * Math.Max(1, reader.Channels)];
            if (reader.SampleRate != targetRate) resampler = new Resampler(reader.SampleRate, targetRate);
        }

        /// <summary>Estimated length in output frames (samples per channel at the target rate).</summary>
        public long EstimatedFrames => src.TotalFrames < 0 ? -1 : (long)Math.Ceiling(src.TotalFrames * (double)targetRate / src.SampleRate);

        /// <summary>Fills dest with whole 4-byte frames. Returns bytes written; 0 at the end.</summary>
        public int Read(byte[] dest, int offset, int count)
        {
            count &= ~3;
            int written = 0;
            while (written < count)
            {
                if (pending.Count > 0)
                {
                    while (pending.Count > 0 && written < count)
                    {
                        short v = pending.Dequeue();
                        dest[offset + written++] = (byte)v;
                        dest[offset + written++] = (byte)(v >> 8);
                    }
                    continue;
                }
                if (srcDone) break;
                Produce(count - written);
            }
            return written;
        }

        void Produce(int bytesWanted)
        {
            int ch = src.Channels;
            int frames = src.Read(inBuf, Math.Min(Chunk, Math.Max(256, bytesWanted / 4)));
            if (frames == 0)
            {
                srcDone = true;
                if (resampler != null) Emit(resampler.Flush(outChans), true);
                return;
            }
            if (exact)
            {
                for (int i = 0; i < frames; i++)
                {
                    int l = inBuf[i * ch];
                    int r = ch > 1 ? inBuf[i * ch + 1] : l;
                    pending.Enqueue((short)l); pending.Enqueue((short)r);
                }
                return;
            }
            float scale = 1f / (1 << (src.BitsPerSample - 1));
            for (int i = 0; i < frames; i++)
            {
                chans[0][i] = inBuf[i * ch] * scale;
                chans[1][i] = ch > 1 ? inBuf[i * ch + 1] * scale : chans[0][i];
            }
            if (resampler != null)
            {
                resampler.Push(chans, frames);
                Emit(resampler.Pull(outChans), true);
            }
            else
            {
                Array.Copy(chans[0], outChans[0], frames);
                Array.Copy(chans[1], outChans[1], frames);
                Emit(frames, true);
            }
        }

        void Emit(int frames, bool dither)
        {
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < 2; c++)
                {
                    double v = outChans[c][i] * 32767.0;
                    if (dither) v += rng.NextDouble() - rng.NextDouble();     // TPDF, ±1 LSB
                    int q = (int)Math.Round(v);
                    pending.Enqueue((short)Math.Max(-32768, Math.Min(32767, q)));
                }
        }
    }

    /// <summary>Streaming windowed-sinc resampler (Blackman window, 48 taps, interpolated phases).</summary>
    public sealed class Resampler
    {
        const int Half = 24, Taps = Half * 2, Phases = 256;
        readonly double step;
        readonly float[] table = new float[(Phases + 1) * Taps];
        float[][] buf = { new float[8192], new float[8192] };
        int len;
        double t;
        readonly float[] weights = new float[Taps];

        public Resampler(int inRate, int outRate)
        {
            step = inRate / (double)outRate;
            double cutoff = Math.Min(1.0, outRate / (double)inRate) * 0.96;
            for (int p = 0; p <= Phases; p++)
            {
                double f = p / (double)Phases;
                double sum = 0;
                for (int j = 0; j < Taps; j++)
                {
                    double d = f + Half - 1 - j;
                    double x = Math.PI * cutoff * d;
                    double sinc = Math.Abs(d) < 1e-9 ? 1 : Math.Sin(x) / x;
                    double w = Math.Abs(d) >= Half ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * d / Half) + 0.08 * Math.Cos(2 * Math.PI * d / Half);
                    double k = cutoff * sinc * w;
                    table[p * Taps + j] = (float)k;
                    sum += k;
                }
                for (int j = 0; j < Taps; j++) table[p * Taps + j] = (float)(table[p * Taps + j] / sum);
            }
            len = Half - 1;      // zero history so the first output lines up with input sample 0
            t = Half - 1;
        }

        public void Push(float[][] input, int frames)
        {
            if (len + frames > buf[0].Length)
                for (int c = 0; c < 2; c++) Array.Resize(ref buf[c], Math.Max(buf[c].Length * 2, len + frames));
            for (int c = 0; c < 2; c++) Array.Copy(input[c], 0, buf[c], len, frames);
            len += frames;
        }

        public int Pull(float[][] output)
        {
            int n = 0;
            while (true)
            {
                int baseIdx = (int)t;
                if (baseIdx + Half >= len) break;
                if (n >= output[0].Length) for (int c = 0; c < 2; c++) Array.Resize(ref output[c], output[c].Length * 2);
                double frac = (t - baseIdx) * Phases;
                int p0 = (int)frac;
                float a = (float)(frac - p0);
                int r0 = p0 * Taps, r1 = r0 + Taps;
                for (int j = 0; j < Taps; j++) weights[j] = table[r0 + j] + a * (table[r1 + j] - table[r0 + j]);
                int start = baseIdx - Half + 1;
                float l = 0, r = 0;
                var bl = buf[0]; var br = buf[1];
                for (int j = 0; j < Taps; j++)
                {
                    l += bl[start + j] * weights[j];
                    r += br[start + j] * weights[j];
                }
                output[0][n] = l; output[1][n] = r;
                n++;
                t += step;
            }
            // Drop samples no future output needs.
            int keepFrom = Math.Max(0, (int)t - Half + 1);
            if (keepFrom > 0)
            {
                for (int c = 0; c < 2; c++) Array.Copy(buf[c], keepFrom, buf[c], 0, len - keepFrom);
                len -= keepFrom;
                t -= keepFrom;
            }
            return n;
        }

        public int Flush(float[][] output)
        {
            var zeros = new[] { new float[Half + 1], new float[Half + 1] };
            Push(zeros, Half + 1);
            return Pull(output);
        }
    }
}
