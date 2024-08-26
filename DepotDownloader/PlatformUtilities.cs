// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Runtime.InteropServices;

namespace DepotDownloader
{
    static class PlatformUtilities
    {
        private const int ModeExecuteOwner = 0x0040;
        private const int ModeExecuteGroup = 0x0008;
        private const int ModeExecuteOther = 0x0001;
        private const int ModeExecute = ModeExecuteOwner | ModeExecuteGroup | ModeExecuteOther;

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatLinuxX64
        {
            [FieldOffset(24)] public readonly uint st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 104)]
        private readonly struct StatLinuxArm32
        {
            [FieldOffset(16)] public readonly uint st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 128)]
        private readonly struct StatLinuxArm64
        {
            [FieldOffset(16)] public readonly uint st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private readonly struct StatOSX
        {
            [FieldOffset(4)] public readonly ushort st_mode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 224)]
        private readonly struct StatFBSDX64
        {
            [FieldOffset(24)] public readonly ushort st_mode;
        }

        [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
        private static extern int statLinuxX64(int version, string path, out StatLinuxX64 statLinux);

        [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
        private static extern int statLinuxArm32(int version, string path, out StatLinuxArm32 statLinux);

        [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
        private static extern int statLinuxArm64(int version, string path, out StatLinuxArm64 statLinux);

        [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
        private static extern int statOSX(string path, out StatOSX stat);

        [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
        private static extern int statOSXCompat(string path, out StatOSX stat);

        [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
        private static extern int statFBSDX64(string path, out StatFBSDX64 stat);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

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

        private static uint GetFileMode(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                    {
                        ThrowIf(statLinuxX64(1, path, out var stat));
                        return stat.st_mode;
                    }
                    case Architecture.Arm:
                    {
                        ThrowIf(statLinuxArm32(3, path, out var stat));
                        return stat.st_mode;
                    }
                    case Architecture.Arm64:
                    {
                        ThrowIf(statLinuxArm64(0, path, out var stat));
                        return stat.st_mode;
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                    {
                        ThrowIf(statFBSDX64(path, out var stat));
                        return stat.st_mode;
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                    {
                        ThrowIf(statOSXCompat(path, out var stat));
                        return stat.st_mode;
                    }
                    case Architecture.Arm64:
                    {
                        ThrowIf(statOSX(path, out var stat));
                        return stat.st_mode;
                    }
                }
            }
            throw new PlatformNotSupportedException();
        }

        public static void SetExecutable(string path, bool value)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var mode = GetFileMode(path);
            var hasExecuteMask = (mode & ModeExecute) == ModeExecute;
            if (hasExecuteMask != value)
            {
                ThrowIf(chmod(path, (uint)(value
                    ? mode | ModeExecute
                    : mode & ~ModeExecute)));
            }
        }

        #region WIN32_CONSOLE_STUFF
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        const uint MB_OK = 0x0;
        const uint MB_ICONWARNING = 0x30;

        public static void VerifyConsoleLaunch()
        {
            // Reference: https://devblogs.microsoft.com/oldnewthing/20160125-00/?p=92922
            var processList = new uint[2];
            var processCount = GetConsoleProcessList(processList, (uint)processList.Length);

            if (processCount != 1)
            {
                return;
            }

            _ = MessageBox(
                IntPtr.Zero,
                "Depot Downloader is a console application; there is no GUI.\n\nIf you do not pass any command line parameters, it prints usage info and exits.\n\nYou must use this from a terminal/console.",
                "DepotDownloader",
                MB_OK | MB_ICONWARNING
            );
        }
        #endregion
    }
}
