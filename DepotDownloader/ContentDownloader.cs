using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDownloader
{
    class ContentDownloaderException(string value) : Exception(value)
    {
    }

    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";

        public static DownloadConfig Config = new();

        private static Steam3Session steam3;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

        private sealed class DepotDownloadInfo(
            uint depotid, uint appId, ulong manifestId, string branch,
            string installDir, byte[] depotKey)
        {
            public uint DepotId { get; } = depotid;
            public uint AppId { get; } = appId;
            public ulong ManifestId { get; } = manifestId;
            public string Branch { get; } = branch;
            public string InstallDir { get; } = installDir;
            public byte[] DepotKey { get; } = depotKey;
        }

        static bool CreateDirectories(uint depotId, uint depotVersion, out string installDir)
        {
            installDir = null;
            try
            {
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                    var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                    Directory.CreateDirectory(depotPath);

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
                else
                {
                    Directory.CreateDirectory(Config.InstallDirectory);

                    installDir = Config.InstallDirectory;

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static bool TestIsFileIncluded(string filename)
        {
            if (!Config.UsingFileList)
                return true;

            filename = filename.Replace('\\', '/');

            if (Config.FilesToDownload.Contains(filename))
            {
                return true;
            }

            foreach (var rgx in Config.FilesToDownloadRegex)
            {
                var m = rgx.Match(filename);

                if (m.Success)
                    return true;
            }

            return false;
        }

        static bool AccountHasAccess(uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = new List<uint> { 17906 };
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            return false;
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber(uint appId, string branch)
        {
            if (appId == INVALID_APP_ID)
                return 0;


            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            var branches = depots["branches"];
            var node = branches[branch];

            if (node == KeyValue.Invalid)
                return 0;

            var buildid = node["buildid"];

            if (buildid == KeyValue.Invalid)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static ulong GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    Console.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                steam3.RequestAppInfo(otherAppId);

                return GetSteam3DepotManifest(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch]["gid"];

            if (node == KeyValue.Invalid && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
            {
                var node_encrypted = manifests_encrypted[branch];
                if (node_encrypted != KeyValue.Invalid)
                {
                    var password = Config.BetaPassword;
                    while (string.IsNullOrEmpty(password))
                    {
                        Console.Write("Please enter the password for branch {0}: ", branch);
                        Config.BetaPassword = password = Console.ReadLine();
                    }

                    var encrypted_gid = node_encrypted["gid"];

                    if (encrypted_gid != KeyValue.Invalid)
                    {
                        // Submit the password to Steam now to get encryption keys
                        steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

                        if (!steam3.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword))
                        {
                            Console.WriteLine("Password was invalid for branch {0}", branch);
                            return INVALID_MANIFEST_ID;
                        }

                        var input = Util.DecodeHexString(encrypted_gid.Value);
                        byte[] manifest_bytes;
                        try
                        {
                            manifest_bytes = CryptoHelper.SymmetricDecryptECB(input, appBetaPassword);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Failed to decrypt branch {0}: {1}", branch, e.Message);
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64(manifest_bytes, 0);
                    }

                    Console.WriteLine("Unhandled depot encryption for depotId {0}", depotId);
                    return INVALID_MANIFEST_ID;
                }

                return INVALID_MANIFEST_ID;
            }

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return ulong.Parse(node.Value);
        }

        static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info == null)
                return string.Empty;

            return info["name"].AsString();
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;

            if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginToken == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    AccessToken = loginToken,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            if (!steam3.WaitForCredentials())
            {
                Console.WriteLine("Unable to get steam3 credentials.");
                return false;
            }

            return true;
        }

        public static void ShutdownSteam3()
        {
            if (cdnPool != null)
            {
                cdnPool.Shutdown();
                cdnPool = null;
            }

            if (steam3 == null)
                return;

            steam3.Disconnect();
        }

        public static async Task DownloadPubfileAsync(uint appId, ulong publishedFileId)
        {
            var details = steam3.GetPublishedFileDetails(appId, publishedFileId);

            if (!string.IsNullOrEmpty(details?.file_url))
            {
                await DownloadWebFile(appId, details.filename, details.file_url);
            }
            else if (details?.hcontent_file > 0)
            {
                await DownloadAppAsync(appId, new List<(uint, ulong)> { (appId, details.hcontent_file) }, DEFAULT_BRANCH, null, null, null, false, true);
            }
            else
            {
                Console.WriteLine("Unable to locate manifest ID for published file {0}", publishedFileId);
            }
        }

        public static async Task DownloadUGCAsync(uint appId, ulong ugcId)
        {
            SteamCloud.UGCDetailsCallback details = null;

            if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
            {
                details = steam3.GetUGCDetails(ugcId);
            }
            else
            {
                Console.WriteLine($"Unable to query UGC details for {ugcId} from an anonymous account");
            }

            if (!string.IsNullOrEmpty(details?.URL))
            {
                await DownloadWebFile(appId, details.FileName, details.URL);
            }
            else
            {
                await DownloadAppAsync(appId, new List<(uint, ulong)> { (appId, ugcId) }, DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        private static async Task DownloadWebFile(uint appId, string fileName, string url)
        {
            if (!CreateDirectories(appId, 0, out var installDir))
            {
                Console.WriteLine("Error: Unable to create install directories!");
                return;
            }

            var stagingDir = Path.Combine(installDir, STAGING_DIR);
            var fileStagingPath = Path.Combine(stagingDir, fileName);
            var fileFinalPath = Path.Combine(installDir, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

            using (var file = File.OpenWrite(fileStagingPath))
            using (var client = HttpClientFactory.CreateHttpClient())
            {
                Console.WriteLine("Downloading {0}", fileName);
                var responseStream = await client.GetStreamAsync(url);
                await responseStream.CopyToAsync(file);
            }

            if (File.Exists(fileFinalPath))
            {
                File.Delete(fileFinalPath);
            }

            File.Move(fileStagingPath, fileFinalPath);
        }

        public static async Task DownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc)
        {
            cdnPool = new CDNClientPool(steam3, appId);

            // Load our configuration data containing the depots currently installed
            var configPath = Config.InstallDirectory;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                configPath = DEFAULT_DOWNLOAD_DIR;
            }

            Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
            DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));

            steam3?.RequestAppInfo(appId);

            if (!AccountHasAccess(appId))
            {
                if (steam3.RequestFreeAppLicense(appId))
                {
                    Console.WriteLine("Obtained FreeOnDemand license for app {0}", appId);

                    // Fetch app info again in case we didn't get it fully without a license.
                    steam3.RequestAppInfo(appId, true);
                }
                else
                {
                    var contentName = GetAppName(appId);
                    throw new ContentDownloaderException(string.Format("App {0} ({1}) is not available from this account.", appId, contentName));
                }
            }

            var hasSpecificDepots = depotManifestIds.Count > 0;
            var depotIdsFound = new List<uint>();
            var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

            if (isUgc)
            {
                var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
                {
                    depotIdsExpected.Add(workshopDepot);
                    depotManifestIds = depotManifestIds.Select(pair => (workshopDepot, pair.manifestId)).ToList();
                }

                depotIdsFound.AddRange(depotIdsExpected);
            }
            else
            {
                Console.WriteLine("Using app branch: '{0}'.", branch);

                if (depots != null)
                {
                    foreach (var depotSection in depots.Children)
                    {
                        var id = INVALID_DEPOT_ID;
                        if (depotSection.Children.Count == 0)
                            continue;

                        if (!uint.TryParse(depotSection.Name, out id))
                            continue;

                        if (hasSpecificDepots && !depotIdsExpected.Contains(id))
                            continue;

                        if (!hasSpecificDepots)
                        {
                            var depotConfig = depotSection["config"];
                            if (depotConfig != KeyValue.Invalid)
                            {
                                if (!Config.DownloadAllPlatforms &&
                                    depotConfig["oslist"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                                {
                                    var oslist = depotConfig["oslist"].Value.Split(',');
                                    if (Array.IndexOf(oslist, os ?? Util.GetSteamOS()) == -1)
                                        continue;
                                }

                                if (depotConfig["osarch"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                                {
                                    var depotArch = depotConfig["osarch"].Value;
                                    if (depotArch != (arch ?? Util.GetSteamArch()))
                                        continue;
                                }

                                if (!Config.DownloadAllLanguages &&
                                    depotConfig["language"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                                {
                                    var depotLang = depotConfig["language"].Value;
                                    if (depotLang != (language ?? "english"))
                                        continue;
                                }

                                if (!lv &&
                                    depotConfig["lowviolence"] != KeyValue.Invalid &&
                                    depotConfig["lowviolence"].AsBoolean())
                                    continue;
                            }
                        }

                        depotIdsFound.Add(id);

                        if (!hasSpecificDepots)
                            depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                    }
                }

                if (depotManifestIds.Count == 0 && !hasSpecificDepots)
                {
                    throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId));
                }

                if (depotIdsFound.Count < depotIdsExpected.Count)
                {
                    var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
                    throw new ContentDownloaderException(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach (var (depotId, manifestId) in depotManifestIds)
            {
                var info = GetDepotInfo(depotId, appId, manifestId, branch);
                if (info != null)
                {
                    infos.Add(info);
                }
            }

            try
            {
                await DownloadSteam3Async(infos).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("App {0} was not completely downloaded.", appId);
                throw;
            }
        }

        static DepotDownloadInfo GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (steam3 != null && appId != INVALID_APP_ID)
                steam3.RequestAppInfo(appId);

            if (!AccountHasAccess(depotId))
            {
                Console.WriteLine("Depot {0} is not available from this account.", depotId);

                return null;
            }

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, DEFAULT_BRANCH);
                    branch = DEFAULT_BRANCH;
                    manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    Console.WriteLine("Depot {0} missing public subsection or manifest section.", depotId);
                    return null;
                }
            }

            steam3.RequestDepotKey(depotId, appId);
            if (!steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
            {
                Console.WriteLine("No valid depot key for {0}, unable to download.", depotId);
                return null;
            }

            var uVersion = GetSteam3AppBuildNumber(appId, branch);

            if (!CreateDirectories(depotId, uVersion, out var installDir))
            {
                Console.WriteLine("Error: Unable to create install directories!");
                return null;
            }

            return new DepotDownloadInfo(depotId, appId, manifestId, branch, installDir, depotKey);
        }

        private class ChunkMatch(ProtoManifest.ChunkData oldChunk, ProtoManifest.ChunkData newChunk)
        {
            public ProtoManifest.ChunkData OldChunk { get; } = oldChunk;
            public ProtoManifest.ChunkData NewChunk { get; } = newChunk;
        }

        private class DepotFilesData
        {
            public DepotDownloadInfo depotDownloadInfo;
            public DepotDownloadCounter depotCounter;
            public string stagingDir;
            public ProtoManifest manifest;
            public ProtoManifest previousManifest;
            public List<ProtoManifest.FileData> filteredFiles;
            public HashSet<string> allFileNames;
        }

        private class FileStreamData
        {
            public FileStream fileStream;
            public SemaphoreSlim fileLock;
            public int chunksToDownload;
        }

        private class GlobalDownloadCounter
        {
            public ulong TotalBytesCompressed;
            public ulong TotalBytesUncompressed;
        }

        private class DepotDownloadCounter
        {
            public ulong CompleteDownloadSize;
            public ulong SizeDownloaded;
            public ulong DepotBytesCompressed;
            public ulong DepotBytesUncompressed;
        }

        private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots)
        {
            var cts = new CancellationTokenSource();
            cdnPool.ExhaustedToken = cts;

            var downloadCounter = new GlobalDownloadCounter();
            var depotsToDownload = new List<DepotFilesData>(depots.Count);
            var allFileNamesAllDepots = new HashSet<string>();

            // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
            foreach (var depot in depots)
            {
                var depotFileData = await ProcessDepotManifestAndFiles(cts, depot);

                if (depotFileData != null)
                {
                    depotsToDownload.Add(depotFileData);
                    allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
                }

                cts.Token.ThrowIfCancellationRequested();
            }

            // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
            // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
            if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
            {
                var claimedFileNames = new HashSet<string>();

                for (var i = depotsToDownload.Count - 1; i >= 0; i--)
                {
                    // For each depot, remove all files from the list that have been claimed by a later depot
                    depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));

                    claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
                }
            }

            foreach (var depotFileData in depotsToDownload)
            {
                await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
            }

            Console.WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
                downloadCounter.TotalBytesCompressed, downloadCounter.TotalBytesUncompressed, depots.Count);
        }

        private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot)
        {
            var depotCounter = new DepotDownloadCounter();

            Console.WriteLine("Processing depot {0}", depot.DepotId);

            ProtoManifest oldProtoManifest = null;
            ProtoManifest newProtoManifest = null;
            var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

            var lastManifestId = INVALID_MANIFEST_ID;
            DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

            // In case we have an early exit, this will force equiv of verifyall next run.
            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
            DepotConfigStore.Save();

            if (lastManifestId != INVALID_MANIFEST_ID)
            {
                var oldManifestFileName = Path.Combine(configDir, string.Format("{0}_{1}.bin", depot.DepotId, lastManifestId));

                if (File.Exists(oldManifestFileName))
                {
                    byte[] expectedChecksum;

                    try
                    {
                        expectedChecksum = File.ReadAllBytes(oldManifestFileName + ".sha");
                    }
                    catch (IOException)
                    {
                        expectedChecksum = null;
                    }

                    oldProtoManifest = ProtoManifest.LoadFromFile(oldManifestFileName, out var currentChecksum);

                    if (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum))
                    {
                        // We only have to show this warning if the old manifest ID was different
                        if (lastManifestId != depot.ManifestId)
                            Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", lastManifestId);
                        oldProtoManifest = null;
                    }
                }
            }

            if (lastManifestId == depot.ManifestId && oldProtoManifest != null)
            {
                newProtoManifest = oldProtoManifest;
                Console.WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
            }
            else
            {
                var newManifestFileName = Path.Combine(configDir, string.Format("{0}_{1}.bin", depot.DepotId, depot.ManifestId));
                if (newManifestFileName != null)
                {
                    byte[] expectedChecksum;

                    try
                    {
                        expectedChecksum = File.ReadAllBytes(newManifestFileName + ".sha");
                    }
                    catch (IOException)
                    {
                        expectedChecksum = null;
                    }

                    newProtoManifest = ProtoManifest.LoadFromFile(newManifestFileName, out var currentChecksum);

                    if (newProtoManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
                    {
                        Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", depot.ManifestId);
                        newProtoManifest = null;
                    }
                }

                if (newProtoManifest != null)
                {
                    Console.WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
                }
                else
                {
                    Console.Write("Downloading depot manifest...");

                    DepotManifest depotManifest = null;
                    ulong manifestRequestCode = 0;
                    var manifestRequestCodeExpiration = DateTime.MinValue;

                    do
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        Server connection = null;

                        try
                        {
                            connection = cdnPool.GetConnection(cts.Token);

                            var now = DateTime.Now;

                            // In order to download this manifest, we need the current manifest request code
                            // The manifest request code is only valid for a specific period in time
                            if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                            {
                                manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                    depot.DepotId,
                                    depot.AppId,
                                    depot.ManifestId,
                                    depot.Branch);
                                // This code will hopefully be valid for one period following the issuing period
                                manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                                // If we could not get the manifest code, this is a fatal error
                                if (manifestRequestCode == 0)
                                {
                                    Console.WriteLine("No manifest request code was returned for {0} {1}", depot.DepotId, depot.ManifestId);
                                    cts.Cancel();
                                }
                            }

                            DebugLog.WriteLine("ContentDownloader",
                                "Downloading manifest {0} from {1} with {2}",
                                depot.ManifestId,
                                connection,
                                cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                            depotManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                                depot.DepotId,
                                depot.ManifestId,
                                manifestRequestCode,
                                connection,
                                depot.DepotKey,
                                cdnPool.ProxyServer).ConfigureAwait(false);

                            cdnPool.ReturnConnection(connection);
                        }
                        catch (TaskCanceledException)
                        {
                            Console.WriteLine("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
                        }
                        catch (SteamKitWebRequestException e)
                        {
                            cdnPool.ReturnBrokenConnection(connection);

                            if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                            {
                                Console.WriteLine("Encountered 401 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                                break;
                            }

                            if (e.StatusCode == HttpStatusCode.NotFound)
                            {
                                Console.WriteLine("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                                break;
                            }

                            Console.WriteLine("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            cdnPool.ReturnBrokenConnection(connection);
                            Console.WriteLine("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
                        }
                    } while (depotManifest == null);

                    if (depotManifest == null)
                    {
                        Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
                        cts.Cancel();
                    }

                    // Throw the cancellation exception if requested so that this task is marked failed
                    cts.Token.ThrowIfCancellationRequested();


                    newProtoManifest = new ProtoManifest(depotManifest, depot.ManifestId);
                    newProtoManifest.SaveToFile(newManifestFileName, out var checksum);
                    File.WriteAllBytes(newManifestFileName + ".sha", checksum);

                    Console.WriteLine(" Done!");
                }
            }

            newProtoManifest.Files.Sort((x, y) => string.Compare(x.FileName, y.FileName, StringComparison.Ordinal));

            Console.WriteLine("Manifest {0} ({1})", depot.ManifestId, newProtoManifest.CreationTime);

            if (Config.DownloadManifestOnly)
            {
                DumpManifestToTextFile(depot, newProtoManifest);
                return null;
            }

            var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

            var filesAfterExclusions = newProtoManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
            var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

            // Pre-process
            filesAfterExclusions.ForEach(file =>
            {
                allFileNames.Add(file.FileName);

                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                var fileStagingPath = Path.Combine(stagingDir, file.FileName);

                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    Directory.CreateDirectory(fileFinalPath);
                    Directory.CreateDirectory(fileStagingPath);
                }
                else
                {
                    // Some manifests don't explicitly include all necessary directories
                    Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

                    depotCounter.CompleteDownloadSize += file.TotalSize;
                }
            });

            return new DepotFilesData
            {
                depotDownloadInfo = depot,
                depotCounter = depotCounter,
                stagingDir = stagingDir,
                manifest = newProtoManifest,
                previousManifest = oldProtoManifest,
                filteredFiles = filesAfterExclusions,
                allFileNames = allFileNames
            };
        }

        private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
        {
            var depot = depotFilesData.depotDownloadInfo;
            var depotCounter = depotFilesData.depotCounter;

            Console.WriteLine("Downloading depot {0}", depot.DepotId);

            var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
            var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, ProtoManifest.FileData fileData, ProtoManifest.ChunkData chunk)>();

            await Util.InvokeAsync(
                files.Select(file => new Func<Task>(async () =>
                    await Task.Run(() => DownloadSteam3AsyncDepotFile(cts, depotFilesData, file, networkChunkQueue)))),
                maxDegreeOfParallelism: Config.MaxDownloads
            );

            await Util.InvokeAsync(
                networkChunkQueue.Select(q => new Func<Task>(async () =>
                    await Task.Run(() => DownloadSteam3AsyncDepotFileChunk(cts, downloadCounter, depotFilesData,
                        q.fileData, q.fileStreamData, q.chunk)))),
                maxDegreeOfParallelism: Config.MaxDownloads
            );

            // Check for deleted files if updating the depot.
            if (depotFilesData.previousManifest != null)
            {
                var previousFilteredFiles = depotFilesData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

                // Check if we are writing to a single output directory. If not, each depot folder is managed independently
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
                    previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
                }
                else
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
                    previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
                }

                foreach (var existingFileName in previousFilteredFiles)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

                    if (!File.Exists(fileFinalPath))
                        continue;

                    File.Delete(fileFinalPath);
                    Console.WriteLine("Deleted {0}", fileFinalPath);
                }
            }

            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
            DepotConfigStore.Save();

            Console.WriteLine("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.DepotBytesCompressed, depotCounter.DepotBytesUncompressed);
        }

        private static void DownloadSteam3AsyncDepotFile(
            CancellationTokenSource cts,
            DepotFilesData depotFilesData,
            ProtoManifest.FileData file,
            ConcurrentQueue<(FileStreamData, ProtoManifest.FileData, ProtoManifest.ChunkData)> networkChunkQueue)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var stagingDir = depotFilesData.stagingDir;
            var depotDownloadCounter = depotFilesData.depotCounter;
            var oldProtoManifest = depotFilesData.previousManifest;
            ProtoManifest.FileData oldManifestFile = null;
            if (oldProtoManifest != null)
            {
                oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
            }

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            // This may still exist if the previous run exited before cleanup
            if (File.Exists(fileStagingPath))
            {
                File.Delete(fileStagingPath);
            }

            List<ProtoManifest.ChunkData> neededChunks;
            var fi = new FileInfo(fileFinalPath);
            var fileDidExist = fi.Exists;
            if (!fileDidExist)
            {
                Console.WriteLine("Pre-allocating {0}", fileFinalPath);

                // create new file. need all chunks
                using var fs = File.Create(fileFinalPath);
                try
                {
                    fs.SetLength((long)file.TotalSize);
                }
                catch (IOException ex)
                {
                    throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                }

                neededChunks = new List<ProtoManifest.ChunkData>(file.Chunks);
            }
            else
            {
                // open existing
                if (oldManifestFile != null)
                {
                    neededChunks = [];

                    var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                    if (Config.VerifyAll || !hashMatches)
                    {
                        // we have a version of this file, but it doesn't fully match what we want
                        if (Config.VerifyAll)
                        {
                            Console.WriteLine("Validating {0}", fileFinalPath);
                        }

                        var matchingChunks = new List<ChunkMatch>();

                        foreach (var chunk in file.Chunks)
                        {
                            var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                            if (oldChunk != null)
                            {
                                matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                            }
                            else
                            {
                                neededChunks.Add(chunk);
                            }
                        }

                        var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                        var copyChunks = new List<ChunkMatch>();

                        using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                        {
                            foreach (var match in orderedChunks)
                            {
                                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                var tmp = new byte[match.OldChunk.UncompressedLength];
                                fsOld.Read(tmp, 0, tmp.Length);

                                var adler = Util.AdlerHash(tmp);
                                if (!adler.SequenceEqual(match.OldChunk.Checksum))
                                {
                                    neededChunks.Add(match.NewChunk);
                                }
                                else
                                {
                                    copyChunks.Add(match);
                                }
                            }
                        }

                        if (!hashMatches || neededChunks.Count > 0)
                        {
                            File.Move(fileFinalPath, fileStagingPath);

                            using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                            {
                                using var fs = File.Open(fileFinalPath, FileMode.Create);
                                try
                                {
                                    fs.SetLength((long)file.TotalSize);
                                }
                                catch (IOException ex)
                                {
                                    throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                                }

                                foreach (var match in copyChunks)
                                {
                                    fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                    var tmp = new byte[match.OldChunk.UncompressedLength];
                                    fsOld.Read(tmp, 0, tmp.Length);

                                    fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                    fs.Write(tmp, 0, tmp.Length);
                                }
                            }

                            File.Delete(fileStagingPath);
                        }
                    }
                }
                else
                {
                    // No old manifest or file not in old manifest. We must validate.

                    using var fs = File.Open(fileFinalPath, FileMode.Open);
                    if ((ulong)fi.Length != file.TotalSize)
                    {
                        try
                        {
                            fs.SetLength((long)file.TotalSize);
                        }
                        catch (IOException ex)
                        {
                            throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                        }
                    }

                    Console.WriteLine("Validating {0}", fileFinalPath);
                    neededChunks = Util.ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
                }

                if (neededChunks.Count == 0)
                {
                    lock (depotDownloadCounter)
                    {
                        depotDownloadCounter.SizeDownloaded += file.TotalSize;
                        Console.WriteLine("{0,6:#00.00}% {1}", (depotDownloadCounter.SizeDownloaded / (float)depotDownloadCounter.CompleteDownloadSize) * 100.0f, fileFinalPath);
                    }

                    return;
                }

                var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
                lock (depotDownloadCounter)
                {
                    depotDownloadCounter.SizeDownloaded += sizeOnDisk;
                }
            }

            var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
            if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, true);
            }
            else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, false);
            }

            var fileStreamData = new FileStreamData
            {
                fileStream = null,
                fileLock = new SemaphoreSlim(1),
                chunksToDownload = neededChunks.Count
            };

            foreach (var chunk in neededChunks)
            {
                networkChunkQueue.Enqueue((fileStreamData, file, chunk));
            }
        }

        private static async Task DownloadSteam3AsyncDepotFileChunk(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            ProtoManifest.FileData file,
            FileStreamData fileStreamData,
            ProtoManifest.ChunkData chunk)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var depotDownloadCounter = depotFilesData.depotCounter;

            var chunkID = Util.EncodeHexString(chunk.ChunkID);

            var data = new DepotManifest.ChunkData
            {
                ChunkID = chunk.ChunkID,
                Checksum = chunk.Checksum,
                Offset = chunk.Offset,
                CompressedLength = chunk.CompressedLength,
                UncompressedLength = chunk.UncompressedLength
            };

            DepotChunk chunkData = null;

            do
            {
                cts.Token.ThrowIfCancellationRequested();

                Server connection = null;

                try
                {
                    connection = cdnPool.GetConnection(cts.Token);

                    DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                    chunkData = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                        depot.DepotId,
                        data,
                        connection,
                        depot.DepotKey,
                        cdnPool.ProxyServer).ConfigureAwait(false);

                    cdnPool.ReturnConnection(connection);
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("Connection timeout downloading chunk {0}", chunkID);
                }
                catch (SteamKitWebRequestException e)
                {
                    cdnPool.ReturnBrokenConnection(connection);

                    if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine("Encountered 401 for chunk {0}. Aborting.", chunkID);
                        break;
                    }

                    Console.WriteLine("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    cdnPool.ReturnBrokenConnection(connection);
                    Console.WriteLine("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                }
            } while (chunkData == null);

            if (chunkData == null)
            {
                Console.WriteLine("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
                cts.Cancel();
            }

            // Throw the cancellation exception if requested so that this task is marked failed
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

                if (fileStreamData.fileStream == null)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                    fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
                }

                fileStreamData.fileStream.Seek((long)chunkData.ChunkInfo.Offset, SeekOrigin.Begin);
                await fileStreamData.fileStream.WriteAsync(chunkData.Data.AsMemory(0, chunkData.Data.Length), cts.Token);
            }
            finally
            {
                fileStreamData.fileLock.Release();
            }

            var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
            if (remainingChunks == 0)
            {
                fileStreamData.fileStream?.Dispose();
                fileStreamData.fileLock.Dispose();
            }

            ulong sizeDownloaded = 0;
            lock (depotDownloadCounter)
            {
                sizeDownloaded = depotDownloadCounter.SizeDownloaded + (ulong)chunkData.Data.Length;
                depotDownloadCounter.SizeDownloaded = sizeDownloaded;
                depotDownloadCounter.DepotBytesCompressed += chunk.CompressedLength;
                depotDownloadCounter.DepotBytesUncompressed += chunk.UncompressedLength;
            }

            lock (downloadCounter)
            {
                downloadCounter.TotalBytesCompressed += chunk.CompressedLength;
                downloadCounter.TotalBytesUncompressed += chunk.UncompressedLength;
            }

            if (remainingChunks == 0)
            {
                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                Console.WriteLine("{0,6:#00.00}% {1}", (sizeDownloaded / (float)depotDownloadCounter.CompleteDownloadSize) * 100.0f, fileFinalPath);
            }
        }

        static void DumpManifestToTextFile(DepotDownloadInfo depot, ProtoManifest manifest)
        {
            var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
            using var sw = new StreamWriter(txtManifest);

            sw.WriteLine($"Content Manifest for Depot {depot.DepotId}");
            sw.WriteLine();
            sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime}");

            int numFiles = 0, numChunks = 0;
            ulong uncompressedSize = 0, compressedSize = 0;

            foreach (var file in manifest.Files)
            {
                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                    continue;

                numFiles++;
                numChunks += file.Chunks.Count;

                foreach (var chunk in file.Chunks)
                {
                    uncompressedSize += chunk.UncompressedLength;
                    compressedSize += chunk.CompressedLength;
                }
            }

            sw.WriteLine($"Total number of files  : {numFiles}");
            sw.WriteLine($"Total number of chunks : {numChunks}");
            sw.WriteLine($"Total bytes on disk    : {uncompressedSize}");
            sw.WriteLine($"Total bytes compressed : {compressedSize}");
            sw.WriteLine();
            sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

            foreach (var file in manifest.Files)
            {
                var sha1Hash = BitConverter.ToString(file.FileHash).Replace("-", "");
                sw.WriteLine($"{file.TotalSize,14} {file.Chunks.Count,6} {sha1Hash} {file.Flags,5:D} {file.FileName}");
            }
        }
    }
}
