using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Discphew.Audio
{
    sealed class BitReader
    {
        readonly Stream s;
        readonly byte[] buf = new byte[1 << 16];
        int pos, len;
        long bufStart;      // stream offset of buf[0]
        ulong cache;
        int cacheBits;

        public BitReader(Stream s) { this.s = s; bufStart = s.Position; }

        public long BytePosition => bufStart + pos - cacheBits / 8;

        public void SeekTo(long offset)
        {
            s.Position = offset;
            bufStart = offset; pos = len = 0; cache = 0; cacheBits = 0;
        }

        bool Fill()
        {
            bufStart += len;
            len = s.Read(buf, 0, buf.Length);
            pos = 0;
            return len > 0;
        }

        public int ReadByteOrEof()
        {
            if (cacheBits >= 8) return (int)ReadBits(8);
            if (pos >= len && !Fill()) return -1;
            return ReadByteAligned();
        }

        int ReadByteAligned()
        {
            // only valid when cacheBits == 0 (byte aligned and drained)
            if (cacheBits != 0) return (int)ReadBits(8);
            return buf[pos++];
        }

        void Refill()
        {
            while (cacheBits <= 48)
            {
                if (pos >= len && !Fill())
                {
                    if (cacheBits == 0) throw new EndOfStreamException();
                    return;
                }
                cache = (cache << 8) | buf[pos++];
                cacheBits += 8;
            }
        }

        public uint ReadBits(int n)
        {
            if (n == 0) return 0;
            if (cacheBits < n) { Refill(); if (cacheBits < n) throw new EndOfStreamException(); }
            cacheBits -= n;
            return (uint)((cache >> cacheBits) & ((1UL << n) - 1));
        }

        public int ReadSigned(int n)
        {
            if (n == 0) return 0;
            uint v = ReadBits(n);
            int shift = 32 - n;
            return (int)(v << shift) >> shift;
        }

        public uint ReadUnary()
        {
            uint q = 0;
            while (true)
            {
                if (cacheBits == 0) Refill();
                ulong window = cache & ((1UL << cacheBits) - 1);
                if (window == 0) { q += (uint)cacheBits; cacheBits = 0; continue; }
                int msb = 63;
                while ((window >> msb) == 0) msb--;
                int zeros = cacheBits - 1 - msb;
                q += (uint)zeros;
                cacheBits -= zeros + 1;
                return q;
            }
        }

        public void AlignToByte() => cacheBits -= cacheBits % 8;
    }

    /// <summary>Decoder for native FLAC files (all predictor types, any block size, 4-32 bits).</summary>
    public sealed class FlacReader : IAudioReader
    {
        readonly Stream stream;
        readonly BitReader br;
        readonly bool ownsStream;
        long firstFrame;
        int minBlock, maxBlock;
        int[][] samples;
        int blockLength, blockPos;
        long nextSample;     // sample index of samples[.][0] after the current frame is decoded
        long position;       // sample index the caller will get next

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public long TotalFrames { get; private set; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Picture { get; private set; }
        public bool CanSeek => stream.CanSeek;

        public FlacReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), true) { }

        public FlacReader(Stream s, bool ownsStream)
        {
            stream = s;
            this.ownsStream = ownsStream;
            SkipId3();
            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) != 4 || Encoding.ASCII.GetString(magic) != "fLaC") throw new InvalidDataException("Not a FLAC file.");
            ReadMetadata();
            firstFrame = stream.Position;
            br = new BitReader(stream);
            samples = new int[Channels][];
            for (int c = 0; c < Channels; c++) samples[c] = new int[Math.Max(maxBlock, 16)];
        }

        void SkipId3()
        {
            var h = new byte[10];
            long start = stream.Position;
            if (stream.Read(h, 0, 10) == 10 && h[0] == 'I' && h[1] == 'D' && h[2] == '3')
            {
                int size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
                stream.Position = start + 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
            }
            else stream.Position = start;
        }

        void ReadMetadata()
        {
            var r = new BinaryReader(stream);
            bool last = false;
            while (!last)
            {
                byte head = r.ReadByte();
                last = (head & 0x80) != 0;
                int type = head & 0x7F;
                int length = (r.ReadByte() << 16) | (r.ReadByte() << 8) | r.ReadByte();
                byte[] data = r.ReadBytes(length);
                if (data.Length < length) throw new InvalidDataException("Truncated FLAC metadata.");
                if (type == 0) ParseStreamInfo(data);
                else if (type == 4) ParseComments(data);
                else if (type == 6 && Picture == null) ParsePicture(data);
            }
            if (SampleRate == 0) throw new InvalidDataException("FLAC file has no STREAMINFO.");
        }

        void ParseStreamInfo(byte[] d)
        {
            minBlock = (d[0] << 8) | d[1];
            maxBlock = (d[2] << 8) | d[3];
            SampleRate = (d[10] << 12) | (d[11] << 4) | (d[12] >> 4);
            Channels = ((d[12] >> 1) & 0x07) + 1;
            BitsPerSample = (((d[12] & 1) << 4) | (d[13] >> 4)) + 1;
            TotalFrames = ((long)(d[13] & 0x0F) << 32) | ((long)d[14] << 24) | ((long)d[15] << 16) | ((long)d[16] << 8) | d[17];
            if (TotalFrames == 0) TotalFrames = -1;
            if (maxBlock == 0) maxBlock = 65535;
        }

        void ParseComments(byte[] d)
        {
            try
            {
                int p = 0;
                int vlen = BitConverter.ToInt32(d, p); p += 4 + vlen;
                int count = BitConverter.ToInt32(d, p); p += 4;
                for (int i = 0; i < count && p < d.Length; i++)
                {
                    int l = BitConverter.ToInt32(d, p); p += 4;
                    string s = Encoding.UTF8.GetString(d, p, l); p += l;
                    int eq = s.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = s.Substring(0, eq), v = s.Substring(eq + 1);
                        Tags[k] = Tags.TryGetValue(k, out var prev) ? prev + "; " + v : v;
                    }
                }
            }
            catch { }
        }

        static int BE(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

        void ParsePicture(byte[] d)
        {
            try
            {
                int p = 4;
                int mime = BE(d, p); p += 4 + mime;
                int desc = BE(d, p); p += 4 + desc;
                p += 16;
                int len = BE(d, p); p += 4;
                Picture = new byte[len];
                Array.Copy(d, p, Picture, 0, len);
            }
            catch { Picture = null; }
        }

        public void Dispose() { if (ownsStream) stream.Dispose(); }

        // ------------------------------------------------------------------ reading

        public int Read(int[] dest, int frames)
        {
            int written = 0;
            while (written < frames)
            {
                if (blockPos >= blockLength)
                {
                    if (!DecodeFrame()) break;
                    continue;
                }
                int n = Math.Min(frames - written, blockLength - blockPos);
                int o = written * Channels;
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < Channels; c++)
                        dest[o++] = samples[c][blockPos + i];
                blockPos += n;
                written += n;
            }
            position += written;
            return written;
        }

        public void Seek(long frame)
        {
            if (frame < 0) frame = 0;
            if (TotalFrames > 0 && frame >= TotalFrames) { blockPos = blockLength = 0; br.SeekTo(stream.Length); position = TotalFrames; return; }

            long length = stream.Length;
            long guess = firstFrame;
            if (TotalFrames > 0)
                guess = firstFrame + (long)((double)frame / TotalFrames * (length - firstFrame)) - (1 << 17);
            guess = Math.Max(firstFrame, guess);

            FrameLocation loc = null;
            while (true)
            {
                loc = FindFrame(guess);
                if (loc != null && loc.FirstSample <= frame) break;
                if (guess == firstFrame) { loc = FindFrame(firstFrame); break; }
                guess = Math.Max(firstFrame, guess - (1 << 19));
            }
            if (loc == null) throw new InvalidDataException("Couldn't find a FLAC frame to seek to.");
            while (true)
            {
                var next = FindFrame(loc.Offset + 2);
                if (next == null || next.FirstSample > frame) break;
                loc = next;
            }
            br.SeekTo(loc.Offset);
            blockPos = blockLength = 0;
            if (!DecodeFrame()) return;
            blockPos = (int)Math.Min(blockLength, Math.Max(0, frame - (nextSample - blockLength)));
            position = frame;
        }

        sealed class FrameLocation { public long Offset; public long FirstSample; }

        FrameLocation FindFrame(long from)
        {
            var buffer = new byte[1 << 16];
            long offset = from;
            while (offset < stream.Length)
            {
                stream.Position = offset;
                int got = stream.Read(buffer, 0, buffer.Length);
                if (got < 16) return null;
                for (int i = 0; i + 16 <= got; i++)
                {
                    if (buffer[i] != 0xFF || (buffer[i + 1] & 0xFE) != 0xF8) continue;
                    int idx = i;
                    if (TryParseHeader(() => idx - i < 16 ? buffer[idx++] : -1, out var h))
                        return new FrameLocation { Offset = offset + i, FirstSample = h.FirstSample };
                }
                offset += got - 15;
            }
            return null;
        }

        struct FrameHeader
        {
            public int BlockSize, SampleRate, Assignment, Channels, Bps;
            public long FirstSample;
        }

        bool TryParseHeader(Func<int> next, out FrameHeader h)
        {
            h = default;
            var raw = new byte[16];
            int n = 0;
            int Next()
            {
                int b = next();
                if (b < 0) throw new EndOfStreamException();
                if (n < raw.Length) raw[n] = (byte)b;
                n++;
                return b;
            }
            try
            {
                int b0 = Next(), b1 = Next();
                if (b0 != 0xFF || (b1 & 0xFE) != 0xF8) return false;
                bool variable = (b1 & 1) != 0;
                int b2 = Next(), b3 = Next();
                int bsCode = b2 >> 4, srCode = b2 & 0x0F, ch = b3 >> 4, ssCode = (b3 >> 1) & 7;
                if (bsCode == 0 || srCode == 15 || ch > 10 || ssCode == 3 || (b3 & 1) != 0) return false;

                int first = Next();
                long value;
                int extra;
                if ((first & 0x80) == 0) { value = first; extra = 0; }
                else if ((first & 0xE0) == 0xC0) { value = first & 0x1F; extra = 1; }
                else if ((first & 0xF0) == 0xE0) { value = first & 0x0F; extra = 2; }
                else if ((first & 0xF8) == 0xF0) { value = first & 0x07; extra = 3; }
                else if ((first & 0xFC) == 0xF8) { value = first & 0x03; extra = 4; }
                else if ((first & 0xFE) == 0xFC) { value = first & 0x01; extra = 5; }
                else if (first == 0xFE) { value = 0; extra = 6; }
                else return false;
                for (int i = 0; i < extra; i++)
                {
                    int c = Next();
                    if ((c & 0xC0) != 0x80) return false;
                    value = (value << 6) | (uint)(c & 0x3F);
                }

                int bs = bsCode switch
                {
                    1 => 192,
                    >= 2 and <= 5 => 576 << (bsCode - 2),
                    6 => Next() + 1,
                    7 => ((Next() << 8) | Next()) + 1,
                    _ => 256 << (bsCode - 8),
                };
                int sr = srCode switch
                {
                    0 => SampleRate, 1 => 88200, 2 => 176400, 3 => 192000, 4 => 8000, 5 => 16000, 6 => 22050,
                    7 => 24000, 8 => 32000, 9 => 44100, 10 => 48000, 11 => 96000,
                    12 => Next() * 1000,
                    13 => (Next() << 8) | Next(),
                    _ => ((Next() << 8) | Next()) * 10,
                };
                int count = n;
                int crc = Next();
                if (count > raw.Length || Crc.Crc8(raw, 0, count) != crc) return false;

                h.BlockSize = bs;
                h.SampleRate = sr;
                h.Assignment = ch;
                h.Channels = ch < 8 ? ch + 1 : 2;
                h.Bps = ssCode switch { 0 => BitsPerSample, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
                h.FirstSample = variable ? value : value * (minBlock > 0 ? minBlock : bs);
                return h.Channels == Channels;
            }
            catch (EndOfStreamException) { return false; }
        }

        bool DecodeFrame()
        {
            br.AlignToByte();
            FrameHeader h;
            try
            {
                int first = br.ReadByteOrEof();
                if (first < 0) return false;
                bool given = false;
                if (!TryParseHeader(() =>
                {
                    if (!given) { given = true; return first; }
                    return br.ReadByteOrEof();
                }, out h))
                {
                    // Lost sync (or trailing garbage): resynchronize on the next valid header.
                    var loc = FindFrame(br.BytePosition);
                    if (loc == null) return false;
                    br.SeekTo(loc.Offset);
                    return DecodeFrame();
                }
            }
            catch (EndOfStreamException) { return false; }

            int n = h.BlockSize;
            if (samples[0].Length < n)
                for (int c = 0; c < Channels; c++) samples[c] = new int[n];

            for (int c = 0; c < h.Channels; c++)
            {
                int bps = h.Bps;
                if ((h.Assignment == 8 && c == 1) || (h.Assignment == 9 && c == 0) || (h.Assignment == 10 && c == 1)) bps++;
                DecodeSubframe(samples[c], n, bps);
            }
            br.AlignToByte();
            br.ReadBits(16);   // frame CRC-16

            var a = samples[0]; var b = Channels > 1 ? samples[1] : null;
            switch (h.Assignment)
            {
                case 8: for (int i = 0; i < n; i++) b[i] = a[i] - b[i]; break;
                case 9: for (int i = 0; i < n; i++) a[i] += b[i]; break;
                case 10:
                    for (int i = 0; i < n; i++)
                    {
                        int side = b[i];
                        int mid = (a[i] << 1) | (side & 1);
                        a[i] = (mid + side) >> 1;
                        b[i] = (mid - side) >> 1;
                    }
                    break;
            }
            blockLength = n;
            blockPos = 0;
            nextSample = h.FirstSample + n;
            return true;
        }

        void DecodeSubframe(int[] x, int n, int bps)
        {
            if (br.ReadBits(1) != 0) throw new InvalidDataException("Corrupt FLAC subframe.");
            int type = (int)br.ReadBits(6);
            int wasted = 0;
            if (br.ReadBits(1) != 0) wasted = (int)br.ReadUnary() + 1;
            bps -= wasted;

            if (type == 0)
            {
                int v = br.ReadSigned(bps);
                for (int i = 0; i < n; i++) x[i] = v;
            }
            else if (type == 1)
            {
                for (int i = 0; i < n; i++) x[i] = br.ReadSigned(bps);
            }
            else if (type >= 8 && type <= 12)
            {
                int order = type - 8;
                for (int i = 0; i < order; i++) x[i] = br.ReadSigned(bps);
                DecodeResidual(x, n, order);
                switch (order)
                {
                    case 1: for (int i = 1; i < n; i++) x[i] += x[i - 1]; break;
                    case 2: for (int i = 2; i < n; i++) x[i] += 2 * x[i - 1] - x[i - 2]; break;
                    case 3: for (int i = 3; i < n; i++) x[i] += 3 * x[i - 1] - 3 * x[i - 2] + x[i - 3]; break;
                    case 4: for (int i = 4; i < n; i++) x[i] += 4 * x[i - 1] - 6 * x[i - 2] + 4 * x[i - 3] - x[i - 4]; break;
                }
            }
            else if (type >= 32)
            {
                int order = type - 31;
                for (int i = 0; i < order; i++) x[i] = br.ReadSigned(bps);
                int precision = (int)br.ReadBits(4) + 1;
                if (precision == 16) throw new InvalidDataException("Corrupt FLAC LPC precision.");
                int shift = br.ReadSigned(5);
                var coefs = new int[order];
                for (int i = 0; i < order; i++) coefs[i] = br.ReadSigned(precision);
                DecodeResidual(x, n, order);
                for (int i = order; i < n; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++) sum += (long)coefs[j] * x[i - j - 1];
                    x[i] += (int)(shift >= 0 ? sum >> shift : sum << -shift);
                }
            }
            else throw new InvalidDataException("Reserved FLAC subframe type.");

            if (wasted > 0)
                for (int i = 0; i < n; i++) x[i] <<= wasted;
        }

        void DecodeResidual(int[] x, int n, int order)
        {
            int method = (int)br.ReadBits(2);
            if (method > 1) throw new InvalidDataException("Reserved FLAC residual coding.");
            int paramBits = method == 0 ? 4 : 5;
            int escape = method == 0 ? 15 : 31;
            int porder = (int)br.ReadBits(4);
            int parts = 1 << porder;
            int psize = n >> porder;
            int i = order;
            for (int p = 0; p < parts; p++)
            {
                int count = p == 0 ? psize - order : psize;
                int k = (int)br.ReadBits(paramBits);
                if (k == escape)
                {
                    int raw = (int)br.ReadBits(5);
                    for (int j = 0; j < count; j++) x[i++] = br.ReadSigned(raw);
                }
                else
                {
                    for (int j = 0; j < count; j++)
                    {
                        uint q = br.ReadUnary();
                        uint u = (q << k) | br.ReadBits(k);
                        x[i++] = (int)(u >> 1) ^ -(int)(u & 1);
                    }
                }
            }
        }
    }
}
