using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Discphew.Audio
{
    static class Crc
    {
        static readonly byte[] crc8 = new byte[256];
        static readonly ushort[] crc16 = new ushort[256];

        static Crc()
        {
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int j = 0; j < 8; j++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
                crc8[i] = (byte)c;
                int d = i << 8;
                for (int j = 0; j < 8; j++) d = (d & 0x8000) != 0 ? (d << 1) ^ 0x8005 : d << 1;
                crc16[i] = (ushort)d;
            }
        }

        public static byte Crc8(byte[] b, int offset, int count)
        {
            byte c = 0;
            for (int i = 0; i < count; i++) c = crc8[c ^ b[offset + i]];
            return c;
        }

        public static ushort Crc16(byte[] b, int offset, int count)
        {
            int c = 0;
            for (int i = 0; i < count; i++) c = ((c << 8) ^ crc16[((c >> 8) ^ b[offset + i]) & 0xFF]) & 0xFFFF;
            return (ushort)c;
        }
    }

    sealed class BitWriter
    {
        byte[] buf = new byte[1 << 16];
        int len;
        ulong acc;
        int bits;

        public int ByteLength => len;
        public byte[] Buffer => buf;
        public long BitLength => (long)len * 8 + bits;

        public void Reset() { len = 0; acc = 0; bits = 0; }

        void Put(byte b)
        {
            if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            buf[len++] = b;
        }

        public void Write(uint value, int n)
        {
            if (n == 0) return;
            if (n == 32) { Write(value >> 16, 16); Write(value & 0xFFFF, 16); return; }
            acc = (acc << n) | (value & ((1u << n) - 1));
            bits += n;
            while (bits >= 8) { bits -= 8; Put((byte)(acc >> bits)); }
        }

        public void WriteSigned(int value, int n) => Write((uint)value, n);

        public void WriteUnary(uint zeros)
        {
            while (zeros >= 31) { Write(0, 31); zeros -= 31; }
            Write(1, (int)zeros + 1);
        }

        public void WriteRice(int k, uint u)
        {
            uint q = u >> k;
            if (q + 1 + k <= 31)
            {
                Write((1u << k) | (u & ((1u << k) - 1)), (int)q + 1 + k);
                return;
            }
            WriteUnary(q);
            if (k > 0) Write(u & ((1u << k) - 1), k);
        }

        public void AlignToByte() { if (bits > 0) Write(0, 8 - bits); }

        public void WriteUtf8(long v)
        {
            if (v < 0x80) { Write((uint)v, 8); return; }
            int extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : v < 0x80000000L ? 5 : 6;
            uint lead = extra switch { 1 => 0xC0u, 2 => 0xE0u, 3 => 0xF0u, 4 => 0xF8u, 5 => 0xFCu, _ => 0xFEu };
            Write(lead | (uint)(v >> (6 * extra)), 8);
            for (int i = extra - 1; i >= 0; i--) Write(0x80u | (uint)((v >> (6 * i)) & 0x3F), 8);
        }
    }

    /// <summary>
    /// A self-contained FLAC encoder (fixed and LPC predictors, partitioned Rice coding,
    /// stereo decorrelation). Output is a "streamable subset" file that every FLAC player reads.
    /// </summary>
    public sealed class FlacEncoder : IDisposable
    {
        const int BlockSize = 4096;
        const int MaxLpcOrder = 12;
        const int LpcPrecision = 12;
        const int MaxPartitionOrder = 8;

        readonly Stream output;
        readonly int sampleRate, channels, bps;
        readonly int[][] block;
        int blockFill;
        long totalSamples;
        long frameNumber;
        int minFrame = int.MaxValue, maxFrame;
        readonly long streamInfoOffset;
        readonly MD5 md5 = MD5.Create();
        readonly BitWriter frame = new BitWriter();
        readonly double[] window = new double[BlockSize];
        int windowLen = -1;
        bool finished;

        public FlacEncoder(Stream output, int sampleRate, int channels, int bitsPerSample,
            IEnumerable<KeyValuePair<string, string>> tags = null, byte[] picture = null, string pictureMime = "image/jpeg")
        {
            if (channels < 1 || channels > 8) throw new ArgumentException("FLAC supports 1-8 channels.");
            if (bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 8) throw new ArgumentException("Unsupported bit depth.");
            this.output = output;
            this.sampleRate = sampleRate;
            this.channels = channels;
            bps = bitsPerSample;
            block = new int[channels][];
            for (int c = 0; c < channels; c++) block[c] = new int[BlockSize];

            var w = new BinaryWriter(output, Encoding.UTF8);
            w.Write(Encoding.ASCII.GetBytes("fLaC"));
            streamInfoOffset = output.Position + 4;
            WriteBlockHeader(output, 0, false, 34);
            output.Write(new byte[34], 0, 34);

            var comments = new MemoryStream();
            var vendor = Encoding.UTF8.GetBytes("discphew");
            var list = new List<byte[]>();
            if (tags != null)
                foreach (var kv in tags)
                    if (!string.IsNullOrEmpty(kv.Value)) list.Add(Encoding.UTF8.GetBytes(kv.Key.ToUpperInvariant() + "=" + kv.Value));
            WriteLE(comments, vendor.Length); comments.Write(vendor, 0, vendor.Length);
            WriteLE(comments, list.Count);
            foreach (var c in list) { WriteLE(comments, c.Length); comments.Write(c, 0, c.Length); }
            WriteBlockHeader(output, 4, false, (int)comments.Length);
            comments.WriteTo(output);

            if (picture != null && picture.Length > 0)
            {
                var pic = new MemoryStream();
                int width = 0, height = 0;
                try
                {
                    using var img = System.Drawing.Image.FromStream(new MemoryStream(picture));
                    width = img.Width; height = img.Height;
                }
                catch { }
                var mime = Encoding.ASCII.GetBytes(pictureMime);
                WriteBE(pic, 3);                       // front cover
                WriteBE(pic, mime.Length); pic.Write(mime, 0, mime.Length);
                WriteBE(pic, 0);                       // description
                WriteBE(pic, width); WriteBE(pic, height); WriteBE(pic, 24); WriteBE(pic, 0);
                WriteBE(pic, picture.Length); pic.Write(picture, 0, picture.Length);
                if (pic.Length < (1 << 24))
                {
                    WriteBlockHeader(output, 6, false, (int)pic.Length);
                    pic.WriteTo(output);
                }
            }

            const int padding = 4096;
            WriteBlockHeader(output, 1, true, padding);
            output.Write(new byte[padding], 0, padding);
        }

        static void WriteBlockHeader(Stream s, int type, bool last, int length)
        {
            s.WriteByte((byte)((last ? 0x80 : 0) | type));
            s.WriteByte((byte)(length >> 16)); s.WriteByte((byte)(length >> 8)); s.WriteByte((byte)length);
        }

        static void WriteLE(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }
        static void WriteBE(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        /// <summary>Appends interleaved little-endian 16-bit PCM.</summary>
        public void Write16(byte[] pcm, int offset, int byteCount)
        {
            if (bps != 16) throw new InvalidOperationException();
            md5.TransformBlock(pcm, offset, byteCount, null, 0);
            int frames = byteCount / (2 * channels);
            int p = offset;
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < channels; c++, p += 2)
                    block[c][blockFill] = (short)(pcm[p] | (pcm[p + 1] << 8));
                if (++blockFill == BlockSize) FlushBlock();
            }
        }

        /// <summary>Appends interleaved samples at the encoder's bit depth.</summary>
        public void Write(int[] interleaved, int frames)
        {
            int bytesPer = bps / 8;
            var raw = new byte[frames * channels * bytesPer];
            int r = 0;
            for (int i = 0; i < frames * channels; i++)
            {
                int v = interleaved[i];
                for (int b = 0; b < bytesPer; b++) raw[r++] = (byte)(v >> (8 * b));
            }
            md5.TransformBlock(raw, 0, raw.Length, null, 0);
            int p = 0;
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < channels; c++) block[c][blockFill] = interleaved[p++];
                if (++blockFill == BlockSize) FlushBlock();
            }
        }

        public void Finish()
        {
            if (finished) return;
            finished = true;
            if (blockFill > 0) FlushBlock();
            md5.TransformFinalBlock(new byte[0], 0, 0);

            var si = new BitWriter();
            si.Write(BlockSize, 16);
            si.Write(BlockSize, 16);
            si.Write(minFrame == int.MaxValue ? 0u : (uint)minFrame, 24);
            si.Write((uint)maxFrame, 24);
            si.Write((uint)sampleRate, 20);
            si.Write((uint)(channels - 1), 3);
            si.Write((uint)(bps - 1), 5);
            si.Write((uint)(totalSamples >> 32), 4);
            si.Write((uint)totalSamples, 32);
            long end = output.Position;
            output.Position = streamInfoOffset;
            output.Write(si.Buffer, 0, si.ByteLength);
            output.Write(md5.Hash, 0, 16);
            output.Position = end;
            output.Flush();
        }

        public void Dispose()
        {
            Finish();
            md5.Dispose();
        }

        // ------------------------------------------------------------------ frames

        void FlushBlock()
        {
            int n = blockFill;
            blockFill = 0;
            totalSamples += n;

            SubframePlan[] chosen;
            int assignment;
            if (channels == 2)
            {
                var l = block[0]; var r = block[1];
                var side = new int[n]; var mid = new int[n];
                for (int i = 0; i < n; i++) { side[i] = l[i] - r[i]; mid[i] = (l[i] + r[i]) >> 1; }
                var pl = Plan(l, n, bps); var pr = Plan(r, n, bps);
                var ps = Plan(side, n, bps + 1); var pm = Plan(mid, n, bps);
                long indep = pl.Bits + pr.Bits, ls = pl.Bits + ps.Bits, sr = ps.Bits + pr.Bits, ms = pm.Bits + ps.Bits;
                long best = Math.Min(Math.Min(indep, ls), Math.Min(sr, ms));
                if (best == indep) { assignment = 1; chosen = new[] { pl, pr }; }
                else if (best == ms) { assignment = 10; chosen = new[] { pm, ps }; }
                else if (best == ls) { assignment = 8; chosen = new[] { pl, ps }; }
                else { assignment = 9; chosen = new[] { ps, pr }; }
            }
            else
            {
                assignment = channels - 1;
                chosen = new SubframePlan[channels];
                for (int c = 0; c < channels; c++) chosen[c] = Plan(block[c], n, bps);
            }

            frame.Reset();
            frame.Write(0x3FFE, 14);
            frame.Write(0, 1);
            frame.Write(0, 1);                         // fixed block size
            int bsCode = n == 4096 ? 12 : n <= 256 ? 6 : 7;
            frame.Write((uint)bsCode, 4);
            int srCode = sampleRate switch { 88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11, _ => 0 };
            frame.Write((uint)srCode, 4);
            frame.Write((uint)assignment, 4);
            frame.Write(bps switch { 8 => 1u, 16 => 4u, 24 => 6u, _ => 0u }, 3);
            frame.Write(0, 1);
            frame.WriteUtf8(frameNumber++);
            if (bsCode == 6) frame.Write((uint)(n - 1), 8);
            else if (bsCode == 7) frame.Write((uint)(n - 1), 16);
            frame.Write(Crc.Crc8(frame.Buffer, 0, frame.ByteLength), 8);

            foreach (var p in chosen) WriteSubframe(p, n);
            frame.AlignToByte();
            frame.Write(Crc.Crc16(frame.Buffer, 0, frame.ByteLength), 16);

            output.Write(frame.Buffer, 0, frame.ByteLength);
            minFrame = Math.Min(minFrame, frame.ByteLength);
            maxFrame = Math.Max(maxFrame, frame.ByteLength);
        }

        sealed class SubframePlan
        {
            public int Type;           // 0 constant, 1 verbatim, 2 fixed, 3 lpc
            public int Order;
            public int Bps;
            public int[] Samples;
            public int[] Residual;
            public int[] Coefs;
            public int Shift;
            public int PartitionOrder;
            public int[] RiceParams;
            public long Bits;
        }

        SubframePlan Plan(int[] x, int n, int sampleBits)
        {
            bool constant = true;
            for (int i = 1; i < n && constant; i++) constant = x[i] == x[0];
            if (constant) return new SubframePlan { Type = 0, Bps = sampleBits, Samples = x, Bits = 8 + sampleBits };

            var best = new SubframePlan { Type = 1, Bps = sampleBits, Samples = x, Bits = 8 + (long)n * sampleBits };

            // Fixed predictors, orders 0..4.
            var res = new int[n];
            for (int order = 0; order <= 4 && order < n; order++)
            {
                FixedResidual(x, n, order, res);
                var rp = new int[1 << MaxPartitionOrder];
                long cost = 8 + (long)order * sampleBits + RiceCost(res, n, order, out int porder, rp);
                if (cost < best.Bits)
                {
                    best = new SubframePlan { Type = 2, Order = order, Bps = sampleBits, Samples = x, Residual = (int[])res.Clone(), PartitionOrder = porder, RiceParams = rp, Bits = cost };
                }
            }

            // LPC, with the order picked from the Levinson-Durbin error estimate.
            if (n > MaxLpcOrder * 2)
            {
                var lpc = TryLpc(x, n, sampleBits, res, out var coefs, out int shift, out int order);
                if (lpc)
                {
                    var rp = new int[1 << MaxPartitionOrder];
                    long cost = 8 + (long)order * sampleBits + 4 + 5 + (long)order * LpcPrecision + RiceCost(res, n, order, out int porder, rp);
                    if (cost < best.Bits)
                        best = new SubframePlan { Type = 3, Order = order, Bps = sampleBits, Samples = x, Residual = (int[])res.Clone(), Coefs = coefs, Shift = shift, PartitionOrder = porder, RiceParams = rp, Bits = cost };
                }
            }
            return best;
        }

        static void FixedResidual(int[] x, int n, int order, int[] r)
        {
            switch (order)
            {
                case 0: for (int i = 0; i < n; i++) r[i] = x[i]; break;
                case 1: for (int i = 1; i < n; i++) r[i] = x[i] - x[i - 1]; break;
                case 2: for (int i = 2; i < n; i++) r[i] = x[i] - 2 * x[i - 1] + x[i - 2]; break;
                case 3: for (int i = 3; i < n; i++) r[i] = x[i] - 3 * x[i - 1] + 3 * x[i - 2] - x[i - 3]; break;
                case 4: for (int i = 4; i < n; i++) r[i] = x[i] - 4 * x[i - 1] + 6 * x[i - 2] - 4 * x[i - 3] + x[i - 4]; break;
            }
        }

        bool TryLpc(int[] x, int n, int sampleBits, int[] res, out int[] qcoefs, out int shift, out int order)
        {
            qcoefs = null; shift = 0; order = 0;
            if (windowLen != n)
            {
                // Tukey(0.5) window, as used by the reference encoder's default preset.
                windowLen = n;
                double p = 0.5;
                int np = (int)(p / 2 * n);
                for (int i = 0; i < n; i++)
                {
                    if (i < np) window[i] = 0.5 - 0.5 * Math.Cos(Math.PI * i / np);
                    else if (i >= n - np) window[i] = 0.5 - 0.5 * Math.Cos(Math.PI * (n - 1 - i) / np);
                    else window[i] = 1.0;
                }
            }
            var autoc = new double[MaxLpcOrder + 1];
            var xw = new double[n];
            for (int i = 0; i < n; i++) xw[i] = x[i] * window[i];
            for (int lag = 0; lag <= MaxLpcOrder; lag++)
            {
                double s = 0;
                for (int i = lag; i < n; i++) s += xw[i] * xw[i - lag];
                autoc[lag] = s;
            }
            if (autoc[0] <= 0) return false;

            // Levinson-Durbin recursion.
            int maxOrder = MaxLpcOrder;
            var lpc = new double[maxOrder];
            var coefs = new double[maxOrder][];
            var errors = new double[maxOrder];
            double err = autoc[0];
            for (int i = 0; i < maxOrder; i++)
            {
                double r = -autoc[i + 1];
                for (int j = 0; j < i; j++) r -= lpc[j] * autoc[i - j];
                r /= err;
                lpc[i] = r;
                int jj;
                for (jj = 0; jj < (i >> 1); jj++)
                {
                    double tmp = lpc[jj];
                    lpc[jj] += r * lpc[i - 1 - jj];
                    lpc[i - 1 - jj] += r * tmp;
                }
                if ((i & 1) != 0) lpc[jj] += lpc[jj] * r;
                err *= 1.0 - r * r;
                coefs[i] = new double[i + 1];
                for (int j = 0; j <= i; j++) coefs[i][j] = -lpc[j];
                errors[i] = err;
                if (err <= 0) { maxOrder = i + 1; break; }
            }

            // Pick the order with the smallest estimated size.
            double errorScale = 0.5 / n;
            double bestBits = double.MaxValue;
            for (int o = 1; o <= maxOrder; o++)
            {
                double e = errors[o - 1];
                double perSample = e > 0 ? Math.Max(0, 0.5 * Math.Log(errorScale * e) / Math.Log(2)) : 0;
                double bits = perSample * (n - o) + o * (sampleBits + LpcPrecision);
                if (bits < bestBits) { bestBits = bits; order = o; }
            }
            if (order == 0) return false;

            // Quantize with error feedback.
            var c = coefs[order - 1];
            double cmax = 0;
            foreach (var v in c) cmax = Math.Max(cmax, Math.Abs(v));
            if (cmax <= 0 || double.IsNaN(cmax) || double.IsInfinity(cmax)) return false;
            int log2cmax = (int)Math.Floor(Math.Log(cmax, 2)) + 1;      // frexp exponent
            shift = LpcPrecision - 1 - log2cmax;
            if (shift > 15) shift = 15;
            if (shift < 0) return false;
            int qmax = (1 << (LpcPrecision - 1)) - 1, qmin = -(1 << (LpcPrecision - 1));
            qcoefs = new int[order];
            double carry = 0;
            for (int j = 0; j < order; j++)
            {
                carry += c[j] * (1 << shift);
                int q = (int)Math.Round(carry);
                if (q > qmax) q = qmax; else if (q < qmin) q = qmin;
                carry -= q;
                qcoefs[j] = q;
            }

            for (int i = order; i < n; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++) sum += (long)qcoefs[j] * x[i - j - 1];
                long rr = x[i] - (sum >> shift);
                if (rr > int.MaxValue || rr < int.MinValue) return false;
                res[i] = (int)rr;
            }
            return true;
        }

        static long RiceCost(int[] res, int n, int predOrder, out int bestOrder, int[] bestParams)
        {
            int pmax = MaxPartitionOrder;
            while (pmax > 0 && ((n & ((1 << pmax) - 1)) != 0 || (n >> pmax) <= predOrder)) pmax--;

            int parts = 1 << pmax;
            var sums = new ulong[parts];
            var counts = new int[parts];
            int psize = n >> pmax;
            for (int p = 0; p < parts; p++)
            {
                int start = p == 0 ? predOrder : p * psize;
                int end = (p + 1) * psize;
                ulong s = 0;
                for (int i = start; i < end; i++)
                {
                    int r = res[i];
                    s += (uint)((r << 1) ^ (r >> 31));
                }
                sums[p] = s;
                counts[p] = end - start;
            }

            long best = long.MaxValue;
            bestOrder = 0;
            var paramsAt = new int[parts];
            for (int order = pmax; order >= 0; order--)
            {
                int pc = 1 << order;
                long bits = 6;
                bool needs5 = false;
                for (int p = 0; p < pc; p++)
                {
                    int k = BestRiceParam(sums[p], counts[p], out long cost);
                    paramsAt[p] = k;
                    if (k > 14) needs5 = true;
                    bits += cost;
                }
                bits += pc * (needs5 ? 5 : 4);
                if (bits < best)
                {
                    best = bits;
                    bestOrder = order;
                    Array.Copy(paramsAt, bestParams, pc);
                }
                if (order > 0)
                    for (int p = 0; p < pc / 2; p++)
                    {
                        sums[p] = sums[2 * p] + sums[2 * p + 1];
                        counts[p] = counts[2 * p] + counts[2 * p + 1];
                    }
            }
            return best;
        }

        static int BestRiceParam(ulong sum, int count, out long cost)
        {
            if (count == 0) { cost = 0; return 0; }
            int k0 = 0;
            ulong mean = sum / (ulong)count;
            while (k0 < 30 && (1UL << (k0 + 1)) <= mean) k0++;
            int best = k0;
            cost = long.MaxValue;
            for (int k = Math.Max(0, k0 - 1); k <= Math.Min(30, k0 + 1); k++)
            {
                long c = (long)count * (k + 1) + (long)(sum >> k);
                if (c < cost) { cost = c; best = k; }
            }
            return best;
        }

        void WriteSubframe(SubframePlan p, int n)
        {
            frame.Write(0, 1);
            switch (p.Type)
            {
                case 0:
                    frame.Write(0, 6); frame.Write(0, 1);
                    frame.WriteSigned(p.Samples[0], p.Bps);
                    return;
                case 1:
                    frame.Write(1, 6); frame.Write(0, 1);
                    for (int i = 0; i < n; i++) frame.WriteSigned(p.Samples[i], p.Bps);
                    return;
                case 2:
                    frame.Write((uint)(8 | p.Order), 6); frame.Write(0, 1);
                    for (int i = 0; i < p.Order; i++) frame.WriteSigned(p.Samples[i], p.Bps);
                    break;
                case 3:
                    frame.Write((uint)(32 | (p.Order - 1)), 6); frame.Write(0, 1);
                    for (int i = 0; i < p.Order; i++) frame.WriteSigned(p.Samples[i], p.Bps);
                    frame.Write(LpcPrecision - 1, 4);
                    frame.WriteSigned(p.Shift, 5);
                    for (int i = 0; i < p.Order; i++) frame.WriteSigned(p.Coefs[i], LpcPrecision);
                    break;
            }

            int pc = 1 << p.PartitionOrder;
            bool five = false;
            for (int i = 0; i < pc; i++) five |= p.RiceParams[i] > 14;
            frame.Write(five ? 1u : 0u, 2);
            frame.Write((uint)p.PartitionOrder, 4);
            int psize = n >> p.PartitionOrder;
            for (int part = 0; part < pc; part++)
            {
                int k = p.RiceParams[part];
                frame.Write((uint)k, five ? 5 : 4);
                int start = part == 0 ? p.Order : part * psize;
                int end = (part + 1) * psize;
                for (int i = start; i < end; i++)
                {
                    int r = p.Residual[i];
                    frame.WriteRice(k, (uint)((r << 1) ^ (r >> 31)));
                }
            }
        }
    }
}
