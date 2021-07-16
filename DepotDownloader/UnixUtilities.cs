using System;
using System.Runtime.InteropServices;

namespace DepotDownloader
{
    public static class UnixUtilities
    {
        [Flags]
        private enum FilePermissions : uint
        {
            S_ISUID = 0x0800, // Set user ID on execution
            S_ISGID = 0x0400, // Set group ID on execution
            S_ISVTX = 0x0200, // Save swapped text after use (sticky).
            S_IRUSR = 0x0100, // Read by owner
            S_IWUSR = 0x0080, // Write by owner
            S_IXUSR = 0x0040, // Execute by owner
            S_IRGRP = 0x0020, // Read by group
            S_IWGRP = 0x0010, // Write by group
            S_IXGRP = 0x0008, // Execute by group
            S_IROTH = 0x0004, // Read by other
            S_IWOTH = 0x0002, // Write by other
            S_IXOTH = 0x0001, // Execute by other

            S_IRWXU = S_IRUSR | S_IWUSR | S_IXUSR,
            S_IRWXG = S_IRGRP | S_IWGRP | S_IXGRP,
            S_IRWXO = S_IROTH | S_IWOTH | S_IXOTH,
            ACCESSPERMS = S_IRWXU | S_IRWXG | S_IRWXO, // 0777
            ALLPERMS = S_ISUID | S_ISGID | S_ISVTX | S_IRWXU | S_IRWXG | S_IRWXO, // 07777
            DEFFILEMODE = S_IRUSR | S_IWUSR | S_IRGRP | S_IWGRP | S_IROTH | S_IWOTH, // 0666
            EXECUTE = S_IXGRP | S_IXUSR | S_IXOTH,

            // Device types
            S_IFMT = 0xF000, // Bits which determine file type
            S_IFDIR = 0x4000, // Directory
            S_IFCHR = 0x2000, // Character device
            S_IFBLK = 0x6000, // Block device
            S_IFREG = 0x8000, // Regular file
            S_IFIFO = 0x1000, // FIFO
            S_IFLNK = 0xA000, // Symbolic link
            S_IFSOCK = 0xC000, // Socket
        }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct Stat
        {
            [FieldOffset(sizeof(ulong) * 3)]
            public readonly FilePermissions st_mode;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int stat(string path, out Stat stat);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, FilePermissions mode);

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

        public static void SetExecute(string path, bool value)
        {
            ThrowIf(UnixUtilities.stat(path, out var stat));

            if (stat.st_mode.HasFlag(FilePermissions.EXECUTE) != value)
            {
                ThrowIf(chmod(path, value
                    ? stat.st_mode | FilePermissions.EXECUTE
                    : stat.st_mode & ~FilePermissions.EXECUTE));
            }
        }
    }
}
