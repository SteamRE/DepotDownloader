using System;
using System.Collections.Generic;

namespace DepotDownloader
{
    internal partial class Program
    {
        private class ProgramArgs : IArgumentContainer
        {
            public ProgramArgs()
            {
                AppID = ContentDownloader.INVALID_APP_ID;
                DepotIDs = new List<uint>();
                ManifestIDs = new List<ulong>();
                ManifestOnly = false;
                UGC = ContentDownloader.INVALID_MANIFEST_ID;
                PubFile = ContentDownloader.INVALID_MANIFEST_ID;
                Validate = false;
                CellID = 0;
                Username = "";
                Password = "";
                RememberPassword = false;
                LoginID = null;
                BetaBranchName = ContentDownloader.DEFAULT_BRANCH;
                BetaPassword = "";
                AllPlatforms = false;
                OperatingSystem = "";
                Architecture = Environment.Is64BitOperatingSystem ? "64" : "32";
                AllLanguages = false;
                Language = "";
                LowViolence = false;
                InstallDir = "";
                FileList = null;
                MaxServers = 20;
                MaxDownloads = 8;
                Debug = false;
            }

            [Option(ShortOption = 'a', LongOption = "app", ParameterName = "id", 
                Description = "The AppID to download")]
            public uint AppID { get; set; }

            [Option(ShortOption = 'd', LongOption = "depot", ParameterName = "id", AllowMultiple = true,
                Description = "The DepotID to download, separate multiple ids with whitespace")]
            public List<uint> DepotIDs { get; set;}

            [Option(ShortOption = 'm', LongOption = "manifest", ParameterName = "id", AllowMultiple = true,
                Description = "manifest id of content to download (requires -d|--depot, default: current for branch)")]
            public List<ulong> ManifestIDs { get; set;}

            [Option(LongOption = "manifest-only", 
                Description = "Downloads a human readable manifest for any depots that would be downloaded")]
            public bool ManifestOnly { get; set;}

            [Option(ShortOption = 'g', LongOption = "ugc", ParameterName = "id", 
                Description = "The UGC ID to download")]
            public ulong UGC { get; set;}

            [Option(LongOption = "pub-file", ParameterName = "id",
                Description = "The Published-File-ID to download. (Will automatically resolve to UGC id)\n")]
            public ulong PubFile { get; set;}

            [Option(LongOption = "validate", Description = "Include checksum verification of files already downloaded")]
            public bool Validate { get; set;}

            [Option(ShortOption = 'c', LongOption = "cell-id", ParameterName = "id",
                Description = "The overridden CellID of the content server to download from.")]
            public int CellID { get; set;}

            [Option(ShortOption = 'u', LongOption = "username", ParameterName = "user",
                Description = "The username of the account to login to for restricted content")]
            public string Username { get; set;}

            [Option(ShortOption = 'p', LongOption = "password", ParameterName = "pass",
                Description = "The password of the account to login to for restricted content.")]
            public string Password { get;set; }

            [Option(LongOption = "remember-password",
                Description = "If set, remember the password for subsequent logins of this user\n")]
            public bool RememberPassword { get; set;}

            [Option(LongOption = "login-id", ParameterName = "id",
                Description = "A unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently")]
            public uint? LoginID { get; set;}

            [Option(LongOption = "beta", ParameterName = "branch",
                Description = "Download from specified beta branch if available (default: Public)")]
            public string BetaBranchName { get; set;}

            [Option(LongOption = "beta-password", ParameterName = "pass",
                Description = "Beta branch password if applicable\n")]
            public string BetaPassword { get; set;}

            [Option(LongOption = "all-platforms",
                Description = "Downloads all platform-specific depots when -a|--app is used")]
            public bool AllPlatforms { get; set;}

            [Option(ShortOption = 'o', LongOption = "os", ParameterName = "os",
                Description = "The operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)")]
            public string OperatingSystem { get; set;}

            [Option(LongOption = "os-arch",
                Description =
                    "The architecture for which to download the game (32 or 64, default: the host's architecture)\n",
                ParameterName = "arch")]
            public string Architecture { get; set;}

            [Option(LongOption = "all-languages",
                Description = "Download all language-specific depots when -a|--app is used")]
            public bool AllLanguages { get; set;}

            [Option(ShortOption = 'l', LongOption = "language", ParameterName = "lang",
                Description = "The language for which to download the game (default: english)\n")]
            public string Language { get; set;}

            [Option(LongOption = "low-violence", 
                Description = "Download low violence depots when -a|--app is used")]
            public bool LowViolence { get; set;}

            [Option(LongOption = "install-dir", ParameterName = "dir", 
                Description = "The directory in which to place downloaded files")]
            public string InstallDir { get; set;}

            [Option(LongOption = "file-list", ParameterName = "file",
                Description = "a list of files to download (from the manifest). Prefix file path with 'regex:' if you want to match with regex")]
            public string FileList { get; set;}

            [Option(LongOption = "max-servers", ParameterName = "count",
                Description = "Maximum number of content servers to use (default: 20)")]
            public int MaxServers { get; set;}

            [Option(LongOption = "max-downloads", ParameterName = "count",
                Description = "Maximum number of chunks to download concurrently (default: 8)")]
            public int MaxDownloads { get; set;}

            [Option(LongOption = "verbose",
                Description = "Makes the DepotDownload more verbose/talkative. Mostly useful for debugging")]
            public bool Debug { get; set;}
        }
    }
}
