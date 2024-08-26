// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using Spectre.Console;

namespace DepotDownloader;

static class Ansi
{
    // https://conemu.github.io/en/AnsiEscapeCodes.html#ConEmu_specific_OSC
    // https://learn.microsoft.com/en-us/windows/terminal/tutorials/progress-bar-sequences
    public enum ProgressState
    {
        Hidden = 0,
        Default = 1,
        Error = 2,
        Indeterminate = 3,
        Warning = 4,
    }

    const char ESC = (char)0x1B;
    const char BEL = (char)0x07;

    private static bool useProgress;

    public static void Init()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return;
        }

        var (supportsAnsi, legacyConsole) = AnsiDetector.Detect(stdError: false, upgrade: true);

        useProgress = supportsAnsi && !legacyConsole;
    }

    public static void Progress(ulong downloaded, ulong total)
    {
        var progress = (byte)MathF.Round(downloaded / (float)total * 100.0f);
        Progress(ProgressState.Default, progress);
    }

    public static void Progress(ProgressState state, byte progress = 0)
    {
        if (!useProgress)
        {
            return;
        }

        Console.Write($"{ESC}]9;4;{(byte)state};{progress}{BEL}");
    }
}
