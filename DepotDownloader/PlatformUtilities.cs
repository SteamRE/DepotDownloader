using System;
using System.Runtime.InteropServices;

namespace DepotDownloader
{
    public static class PlatformUtilities
    {
        private const int ModeExecuteOwner = 0x0040;
        private const int ModeExecuteGroup = 0x0008;
        private const int ModeExecuteOther = 0x0001;
        private const int ModeExecute = ModeExecuteOwner | ModeExecuteGroup | ModeExecuteOther;

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatLinux
        {
            [FieldOffset(24)] public readonly uint st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatOSX
        {
            [FieldOffset(4)] public readonly ushort st_mode;
        }

        [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
        private static extern int statLinux(int version, string path, out StatLinux statLinux);

        [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
        private static extern int statOSX(string path, out StatOSX stat);

        [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
        private static extern int statOSXCompat(string path, out StatOSX stat);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, ushort mode);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr strerror(int errno);

        private static void ThrowIf(int i)
        {
            if (i == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                throw new Exception(Marshal.PtrToStringAnsi(strerror(errno)));
            }
        }

        public static void SetExecutable(string path, bool value)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                ThrowIf(statLinux(1, path, out var stat));

                var hasExecuteMask = (stat.st_mode & ModeExecute) == ModeExecute;
                if (hasExecuteMask != value)
                {
                    ThrowIf(chmod(path, (uint)(value
                        ? stat.st_mode | ModeExecute
                        : stat.st_mode & ~ModeExecute)));
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                StatOSX stat;

                ThrowIf(RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? statOSX(path, out stat)
                    : statOSXCompat(path, out stat));

                var hasExecuteMask = (stat.st_mode & ModeExecute) == ModeExecute;
                if (hasExecuteMask != value)
                {
                    ThrowIf(chmod(path, (ushort)(value
                        ? stat.st_mode | ModeExecute
                        : stat.st_mode & ~ModeExecute)));
                }
            }
        }
    }
}
