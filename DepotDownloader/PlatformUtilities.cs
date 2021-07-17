using System;
using System.Runtime.InteropServices;

namespace DepotDownloader
{
    public static class PlatformUtilities
    {
        [Flags]
        private enum FilePermissions : uint
        {
            S_IXUSR = 0x0040, // Execute by owner
            S_IXGRP = 0x0008, // Execute by group
            S_IXOTH = 0x0001, // Execute by other
            EXECUTE = S_IXGRP | S_IXUSR | S_IXOTH
        }

        private enum FilePermissionsShort : ushort
        {
            S_IXUSR = 0x0040, // Execute by owner
            S_IXGRP = 0x0008, // Execute by group
            S_IXOTH = 0x0001, // Execute by other
            EXECUTE = S_IXGRP | S_IXUSR | S_IXOTH
        }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatLinux
        {
            [FieldOffset(24)] public readonly FilePermissions st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatOSX
        {
            [FieldOffset(4)] public readonly FilePermissionsShort st_mode;
        }

        [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
        private static extern int stat(int version, string path, out StatLinux statLinux);

        [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
        private static extern int stat(string path, out StatOSX stat);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, FilePermissions mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, FilePermissionsShort mode);

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
                ThrowIf(PlatformUtilities.stat(1, path, out var stat));

                if (stat.st_mode.HasFlag(FilePermissions.EXECUTE) != value)
                {
                    ThrowIf(chmod(path, value
                        ? stat.st_mode | FilePermissions.EXECUTE
                        : stat.st_mode & ~FilePermissions.EXECUTE));
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ThrowIf(PlatformUtilities.stat(path, out var stat));

                if (stat.st_mode.HasFlag(FilePermissionsShort.EXECUTE) != value)
                {
                    ThrowIf(chmod(path, value
                        ? stat.st_mode | FilePermissionsShort.EXECUTE
                        : stat.st_mode & ~FilePermissionsShort.EXECUTE));
                }
            }
        }
    }
}
