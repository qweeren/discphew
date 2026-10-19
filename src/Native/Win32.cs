using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace Discphew.Native
{
    /// <summary>P/Invoke declarations. Kept in one place so the rest of the code reads like plain C#.</summary>
    internal static class Win32
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // CTL_CODE(FILE_DEVICE_CD_ROM, fn, method, FILE_READ_ACCESS)
        public const uint IOCTL_CDROM_READ_TOC = 0x00024000;
        public const uint IOCTL_CDROM_GET_LAST_SESSION = 0x00024038;
        public const uint IOCTL_CDROM_RAW_READ = 0x0002403E;
        public const uint IOCTL_CDROM_GET_DRIVE_GEOMETRY = 0x0002404C;
        public const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;
        public const uint IOCTL_STORAGE_CHECK_VERIFY = 0x002D4800;
        public const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        public const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;
        public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
        public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
            IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int toRead, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);

        // ---- waveOut (winmm) ----
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint CALLBACK_EVENT = 0x00050000;
        public const int WHDR_DONE = 0x00000001;
        public const uint TIME_SAMPLES = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public uint dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MMTIME
        {
            public uint wType;
            public uint value;
            public uint pad;
        }

        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr handle, uint device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveOutPause(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveOutRestart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveOutGetPosition(IntPtr handle, ref MMTIME time, int size);

        // ---- Shell / theming ----
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create,
            IntPtr template, out IStream stream);
        public const uint STGM_READ = 0x0, STGM_SHARE_DENY_WRITE = 0x20;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_CAPTION_COLOR = 35;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_DEVTYP_VOLUME = 2;
    }
}
