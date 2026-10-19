using System;
using System.Runtime.InteropServices;
using System.Threading;
using Discphew.Native;

namespace Discphew.Audio
{
    /// <summary>
    /// Minimal waveOut output: a feeder thread keeps a handful of buffers queued and pulls PCM
    /// from a callback. Small, dependency-free and good enough for album listening.
    /// </summary>
    public sealed class WavePlayer : IDisposable
    {
        const int BufferCount = 4;
        readonly IntPtr device;
        readonly IntPtr[] headers = new IntPtr[BufferCount];
        readonly IntPtr[] data = new IntPtr[BufferCount];
        readonly byte[] scratch;
        readonly int bufferBytes, blockAlign;
        readonly Func<byte[], int, int> fill;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        readonly object gate = new object();
        readonly int headerSize = Marshal.SizeOf(typeof(Win32.WAVEHDR));
        readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(Win32.WAVEHDR), "dwFlags");
        readonly bool[] queued = new bool[BufferCount];
        volatile bool stopping, paused, drained;
        long baseFrame;

        /// <summary>Raised on the feeder thread when the source is exhausted and everything has played.</summary>
        public event Action Finished;

        public int SampleRate { get; }

        public WavePlayer(int sampleRate, int channels, Func<byte[], int, int> fill)
        {
            SampleRate = sampleRate;
            this.fill = fill;
            blockAlign = channels * 2;
            bufferBytes = sampleRate / 8 * blockAlign;     // 125 ms per buffer
            scratch = new byte[bufferBytes];

            var fmt = new Win32.WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
                nBlockAlign = (ushort)blockAlign,
                wBitsPerSample = 16,
            };
            int mm = Win32.waveOutOpen(out device, Win32.WAVE_MAPPER, ref fmt, wake.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, Win32.CALLBACK_EVENT);
            if (mm != 0) throw new InvalidOperationException($"Couldn't open the audio output (error {mm}).");

            for (int i = 0; i < BufferCount; i++)
            {
                data[i] = Marshal.AllocHGlobal(bufferBytes);
                headers[i] = Marshal.AllocHGlobal(headerSize);
                var h = new Win32.WAVEHDR { lpData = data[i], dwBufferLength = (uint)bufferBytes };
                Marshal.StructureToPtr(h, headers[i], false);
                Win32.waveOutPrepareHeader(device, headers[i], headerSize);
            }
            thread = new Thread(Feed) { IsBackground = true, Name = "discphew audio", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        bool IsDone(int i) => (Marshal.ReadInt32(headers[i], flagsOffset) & Win32.WHDR_DONE) != 0;

        void Feed()
        {
            while (!stopping)
            {
                bool anyQueued = false;
                lock (gate)
                {
                    for (int i = 0; i < BufferCount && !stopping; i++)
                    {
                        if (queued[i] && !IsDone(i)) { anyQueued = true; continue; }
                        queued[i] = false;
                        if (drained || paused) continue;
                        int n = fill(scratch, bufferBytes);
                        n -= n % blockAlign;
                        if (n <= 0) { drained = true; continue; }
                        Marshal.Copy(scratch, 0, data[i], n);
                        Marshal.WriteInt32(headers[i], (int)Marshal.OffsetOf(typeof(Win32.WAVEHDR), "dwBufferLength"), n);
                        Win32.waveOutWrite(device, headers[i], headerSize);
                        queued[i] = true;
                        anyQueued = true;
                    }
                }
                if (drained && !anyQueued)
                {
                    drained = false;
                    paused = true;
                    Finished?.Invoke();
                }
                wake.WaitOne(paused ? 200 : 50);
            }
        }

        /// <summary>Frames played since the last <see cref="Flush"/>, plus the base it was given.</summary>
        public long PositionFrames
        {
            get
            {
                var t = new Win32.MMTIME { wType = Win32.TIME_SAMPLES };
                Win32.waveOutGetPosition(device, ref t, Marshal.SizeOf(typeof(Win32.MMTIME)));
                long played = t.wType == Win32.TIME_SAMPLES ? t.value : t.value / (uint)blockAlign;
                return baseFrame + played;
            }
        }

        public bool Paused => paused;

        public void Pause()
        {
            paused = true;
            Win32.waveOutPause(device);
        }

        public void Resume()
        {
            paused = false;
            drained = false;
            Win32.waveOutRestart(device);
            wake.Set();
        }

        /// <summary>
        /// Drops everything queued. Run <paramref name="whileStopped"/> (e.g. a seek on the source)
        /// before playback refills, and report <paramref name="newBase"/> as the new position.
        /// </summary>
        public void Flush(long newBase, Action whileStopped = null)
        {
            lock (gate)
            {
                Win32.waveOutReset(device);
                for (int i = 0; i < BufferCount; i++) queued[i] = false;
                baseFrame = newBase;
                drained = false;
                whileStopped?.Invoke();
                if (paused) Win32.waveOutPause(device);
            }
            wake.Set();
        }

        public void Dispose()
        {
            stopping = true;
            wake.Set();
            thread.Join(2000);
            Win32.waveOutReset(device);
            for (int i = 0; i < BufferCount; i++)
            {
                Win32.waveOutUnprepareHeader(device, headers[i], headerSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(data[i]);
            }
            Win32.waveOutClose(device);
            wake.Dispose();
        }
    }
}
