using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SteamKit2;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DepotDownloader
{
    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;

        public static DownloadConfig Config = new DownloadConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

        private sealed class DepotDownloadInfo
        {
            public uint id { get; private set; }
            public string installDir { get; private set; }
            public string contentName { get; private set; }

            public ulong manifestId { get; private set; }
            public byte[] depotKey;

            public DepotDownloadInfo(uint depotid, ulong manifestId, string installDir, string contentName)
            {
                this.id = depotid;
                this.manifestId = manifestId;
                this.installDir = installDir;
                this.contentName = contentName;
            }
        }

        static bool CreateDirectories( uint depotId, uint depotVersion, out string installDir )
        {
            installDir = null;
            try
            {
                if (string.IsNullOrWhiteSpace(ContentDownloader.Config.InstallDirectory))
                {
                    Directory.CreateDirectory( DEFAULT_DOWNLOAD_DIR );

                    string depotPath = Path.Combine( DEFAULT_DOWNLOAD_DIR, depotId.ToString() );
                    Directory.CreateDirectory( depotPath );

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
                else
                {
                    Directory.CreateDirectory(ContentDownloader.Config.InstallDirectory);

                    installDir = ContentDownloader.Config.InstallDirectory;

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

            foreach (string fileListEntry in Config.FilesToDownload)
            {
                if (fileListEntry.Equals(filename, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (Regex rgx in Config.FilesToDownloadRegex)
            {
                Match m = rgx.Match(filename);

                if (m.Success)
                    return true;
            }

            return false;
        }

        static bool AccountHasAccess( uint depotId )
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if ( steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser )
            {
                licenseQuery = new List<uint>() { 17906 };
            }
            else
            {
                licenseQuery = steam3.Licenses.Select( x => x.PackageID );
            }

            steam3.RequestPackageInfo( licenseQuery );

            foreach ( var license in licenseQuery )
            {
                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if ( steam3.PackageInfo.TryGetValue( license, out package ) && package != null )
                {
                    if ( package.KeyValues["appids"].Children.Any( child => child.AsInteger() == depotId ) )
                        return true;

                    if ( package.KeyValues["depotids"].Children.Any( child => child.AsInteger() == depotId ) )
                        return true;
                }
            }

            return false;
        }

        internal static KeyValue GetSteam3AppSection( uint appId, EAppInfoSection section )
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if ( !steam3.AppInfo.TryGetValue( appId, out app ) || app == null )
            {
                return null;
            }

            KeyValue appinfo = app.KeyValues;
            string section_key;

            switch (section)
            {
                case EAppInfoSection.Common:
                    section_key = "common";
                    break;
                case EAppInfoSection.Extended:
                    section_key = "extended";
                    break;
                case EAppInfoSection.Config:
                    section_key = "config";
                    break;
                case EAppInfoSection.Depots:
                    section_key = "depots";
                    break;
                default:
                    throw new NotImplementedException();
            }
            
            KeyValue section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber(uint appId, string branch)
        {
            if (appId == INVALID_APP_ID)
                return 0;


            KeyValue depots = ContentDownloader.GetSteam3AppSection(appId, EAppInfoSection.Depots);
            KeyValue branches = depots["branches"];
            KeyValue node = branches[branch];

            if (node == KeyValue.Invalid)
                return 0;

            KeyValue buildid = node["buildid"];

            if (buildid == KeyValue.Invalid)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static ulong GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            if (Config.ManifestId != INVALID_MANIFEST_ID)
                return Config.ManifestId;

            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            KeyValue depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            if (depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                uint otherAppId = (uint)depotChild["depotfromapp"].AsInteger();
                if (otherAppId == appId)
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    Console.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                steam3.RequestAppInfo(otherAppId);

                if (AccountHasAccess(otherAppId))
                {
                    return GetSteam3DepotManifest(depotId, otherAppId, branch);
                } else {
                    string contentName = GetAppOrDepotName(INVALID_DEPOT_ID, otherAppId);
                    string contentDepotName = GetAppOrDepotName(depotId, appId);
                    Console.WriteLine("Dependent app {0} ({1}) for depot {2} ({3}) is not available from this account.", otherAppId, contentName, depotId, contentDepotName);
                }
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch];

            if (branch != "Public" && node == KeyValue.Invalid)
            {
                var node_encrypted = manifests_encrypted[branch];
                if (node_encrypted != KeyValue.Invalid)
                {
                    string password = Config.BetaPassword;
                    if (password == null)
                    {
                        Console.Write("Please enter the password for branch {0}: ", branch);
                        Config.BetaPassword = password = Console.ReadLine();
                    }

                    byte[] input = Util.DecodeHexString(node_encrypted["encrypted_gid"].Value);
                    byte[] manifest_bytes = CryptoHelper.VerifyAndDecryptPassword(input, password);

                    if (manifest_bytes == null)
                    {
                        Console.WriteLine("Password was invalid for branch {0}", branch);
                        return INVALID_MANIFEST_ID;
                    }

                    return BitConverter.ToUInt64(manifest_bytes, 0);
                }

                Console.WriteLine("Invalid branch {0} for appId {1}", branch, appId);
                return INVALID_MANIFEST_ID;
            }

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return UInt64.Parse(node.Value);
        }

        static string GetAppOrDepotName(uint depotId, uint appId)
        {
            if (depotId == INVALID_DEPOT_ID)
            {
                KeyValue info = GetSteam3AppSection(appId, EAppInfoSection.Common);

                if (info == null)
                    return String.Empty;

                return info["name"].AsString();
            }
            else
            {
                KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

                if (depots == null)
                    return String.Empty;

                KeyValue depotChild = depots[depotId.ToString()];

                if (depotChild == null)
                    return String.Empty;

                return depotChild["name"].AsString();
            }
        }

        public static void InitializeSteam3(string username, string password)
        {
            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = password,
                }
            );

            steam3Credentials = steam3.WaitForCredentials();

            if (!steam3Credentials.IsValid)
            {
                Console.WriteLine("Unable to get steam3 credentials.");
                return;
            }
        }

        public static void ShutdownSteam3()
        {
            if (steam3 == null)
                return;

            steam3.Disconnect();
        }

        public static void DownloadApp(uint appId, uint depotId, string branch, bool forceDepot = false)
        {
            if(steam3 != null)
                steam3.RequestAppInfo(appId);

            if (!AccountHasAccess(appId))
            {
                string contentName = GetAppOrDepotName(INVALID_DEPOT_ID, appId);
                Console.WriteLine("App {0} ({1}) is not available from this account.", appId, contentName);
                return;
            }

            var depotIDs = new List<uint>();
            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);


            if (forceDepot)
            {
                depotIDs.Add(depotId);
            }
            else
            {
                if (depots != null)
                {
                    foreach (var depotSection in depots.Children)
                    {
                        uint id = INVALID_DEPOT_ID;
                        if (depotSection.Children.Count == 0)
                            continue;
                    
                        if (!uint.TryParse(depotSection.Name, out id))
                            continue;

                        if (depotId != INVALID_DEPOT_ID && id != depotId)
                            continue;

                        if (!Config.DownloadAllPlatforms)
                        {
                            var depotConfig = depotSection["config"];
                            if (depotConfig != KeyValue.Invalid && depotConfig["oslist"] != KeyValue.Invalid && !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                            {
                                var oslist = depotConfig["oslist"].Value.Split(',');
                                if (Array.IndexOf(oslist, Util.GetSteamOS()) == -1)
                                    continue;
                            }
                        }

                        depotIDs.Add(id);
                    }
                }
                if (depotIDs == null || (depotIDs.Count == 0 && depotId == INVALID_DEPOT_ID))
                {
                    Console.WriteLine("Couldn't find any depots to download for app {0}", appId);
                    return;
                }
                else if (depotIDs.Count == 0)
                {
                    Console.Write("Depot {0} not listed for app {1}", depotId, appId);
                    if (!Config.DownloadAllPlatforms)
                    {
                        Console.Write(" or not available on this platform");
                    }
                    Console.WriteLine();
                    return;
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach (var depot in depotIDs)
            {
                DepotDownloadInfo info = GetDepotInfo(depot, appId, branch);
                if (info != null)
                {
                    infos.Add(info);
                }
            }

            try
            {
                DownloadSteam3(infos);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("App {0} was not completely downloaded.", appId);
            }
        }

        static DepotDownloadInfo GetDepotInfo(uint depotId, uint appId, string branch)
        {
            if(steam3 != null && appId != INVALID_APP_ID)
                steam3.RequestAppInfo((uint)appId);

            string contentName = GetAppOrDepotName(depotId, appId);

            if (!AccountHasAccess(depotId))
            {    
                Console.WriteLine("Depot {0} ({1}) is not available from this account.", depotId, contentName);

                return null;
            }

            uint uVersion = GetSteam3AppBuildNumber(appId, branch);

            string installDir;
            if (!CreateDirectories(depotId, uVersion, out installDir))
            {
                Console.WriteLine("Error: Unable to create install directories!");
                return null;
            }

            if(steam3 != null)
                steam3.RequestAppTicket((uint)depotId);

            ulong manifestID = GetSteam3DepotManifest(depotId, appId, branch);
            if (manifestID == INVALID_MANIFEST_ID)
            {
                Console.WriteLine("Depot {0} ({1}) missing public subsection or manifest section.", depotId, contentName);
                return null;
            }

            steam3.RequestDepotKey( depotId, appId );
            if (!steam3.DepotKeys.ContainsKey(depotId))
            {
                Console.WriteLine("No valid depot key for {0}, unable to download.", depotId);
                return null;
            }

            byte[] depotKey = steam3.DepotKeys[depotId];

            var info = new DepotDownloadInfo( depotId, manifestID, installDir, contentName );
            info.depotKey = depotKey;
            return info;
        }

        private class ChunkMatch
        {
            public ChunkMatch(ProtoManifest.ChunkData oldChunk, ProtoManifest.ChunkData newChunk)
            {
                OldChunk = oldChunk;
                NewChunk = newChunk;
            }
            public ProtoManifest.ChunkData OldChunk { get; private set; }
            public ProtoManifest.ChunkData NewChunk { get; private set; }
        }

        private static BlockingCollection<CDNClient> CollectCDNClientsForDepot(DepotDownloadInfo depot)
        {
            Console.WriteLine("Finding content servers...");

            var cdnClients = new BlockingCollection<CDNClient>();
            CDNClient initialClient = new CDNClient( steam3.steamClient, steam3.AppTickets[depot.id] );
            List<CDNClient.Server> cdnServers = null;

            while(true)
            {
                try
                {
                    cdnServers = initialClient.FetchServerList( cellId: (uint)Config.CellID );
                    if (cdnServers != null) break;
                }
                catch (WebException)
                {
                    Console.WriteLine("\nFailed to retrieve content server list.");
                    Thread.Sleep(500);
                }
            }

            if (cdnServers == null)
            {
                Console.WriteLine("\nUnable to query any content servers for depot {0} - {1}", depot.id, depot.contentName);
                return cdnClients;
            }

            var weightedCdnServers = cdnServers.Select(x =>
            {
                int penalty = 0;
                ConfigStore.TheConfig.ContentServerPenalty.TryGetValue(x.Host, out penalty);

                return Tuple.Create(x, penalty);
            }).OrderBy(x => x.Item2).ThenBy(x => x.Item1.WeightedLoad);

            // Grab up to the first eight server in the allegedly best-to-worst order from Steam
            Parallel.ForEach(weightedCdnServers, new ParallelOptions { MaxDegreeOfParallelism = 2 }, (serverTuple, parallelLoop) =>
            {
                var server = serverTuple.Item1;
                CDNClient c = new CDNClient( steam3.steamClient, steam3.AppTickets[ depot.id ] );

                try
                {
                    for (int i = 0; i < server.NumEntries; i++)
                    {
                        c.Connect(server);

                        string cdnAuthToken = null;
                        if (server.Type == "CDN")
                        {
                            steam3.RequestCDNAuthToken(depot.id, server.Host);
                            cdnAuthToken = steam3.CDNAuthTokens[Tuple.Create(depot.id, server.Host)].Token;
                        }

                        c.AuthenticateDepot(depot.id, depot.depotKey, cdnAuthToken);
                        cdnClients.Add(c);
                    }

                    if (cdnClients.Count >= Config.MaxServers) parallelLoop.Stop();
                }
                catch (Exception ex)
                {
                    int penalty = 0;
                    ConfigStore.TheConfig.ContentServerPenalty.TryGetValue(server.Host, out penalty);
                    ConfigStore.TheConfig.ContentServerPenalty[server.Host] = penalty + 1;

                    Console.WriteLine("\nFailed to connect to content server {0}: {1}", server, ex.Message);
                }
            });

            if (cdnClients.Count == 0)
            {
                Console.WriteLine("\nUnable to find any content servers for depot {0} - {1}", depot.id, depot.contentName);
            }

            Config.MaxDownloads = Math.Min(Config.MaxDownloads, cdnClients.Count);

            return cdnClients;
        }

        private static void DownloadSteam3( List<DepotDownloadInfo> depots )
        {
            ulong TotalBytesCompressed = 0;
            ulong TotalBytesUncompressed = 0;

            foreach (var depot in depots)
            {
                ulong DepotBytesCompressed = 0;
                ulong DepotBytesUncompressed = 0;

                Console.WriteLine("Downloading depot {0} - {1}", depot.id, depot.contentName);

                var cdnClientsLock = new Object();
                BlockingCollection<CDNClient> cdnClients = null;
                CancellationTokenSource cts = new CancellationTokenSource();

                ProtoManifest oldProtoManifest = null;
                ProtoManifest newProtoManifest = null;
                string configDir = Path.Combine(depot.installDir, CONFIG_DIR);

                ulong lastManifestId = INVALID_MANIFEST_ID;
                ConfigStore.TheConfig.LastManifests.TryGetValue(depot.id, out lastManifestId);

                // In case we have an early exit, this will force equiv of verifyall next run.
                ConfigStore.TheConfig.LastManifests[depot.id] = INVALID_MANIFEST_ID;
                ConfigStore.Save();

                if (lastManifestId != INVALID_MANIFEST_ID)
                {
                    var oldManifestFileName = Path.Combine(configDir, string.Format("{0}.bin", lastManifestId));
                    if (File.Exists(oldManifestFileName))
                        oldProtoManifest = ProtoManifest.LoadFromFile(oldManifestFileName);
                }

                if (lastManifestId == depot.manifestId && oldProtoManifest != null)
                {
                    newProtoManifest = oldProtoManifest;
                    Console.WriteLine("Already have manifest {0} for depot {1}.", depot.manifestId, depot.id);
                }
                else
                {
                    var newManifestFileName = Path.Combine(configDir, string.Format("{0}.bin", depot.manifestId));
                    if (newManifestFileName != null)
                    {
                        newProtoManifest = ProtoManifest.LoadFromFile(newManifestFileName);
                    }

                    if (newProtoManifest != null)
                    {
                        Console.WriteLine("Already have manifest {0} for depot {1}.", depot.manifestId, depot.id);
                    }
                    else
                    {
                        Console.Write("Downloading depot manifest...");

                        DepotManifest depotManifest = null;

                        cdnClients = CollectCDNClientsForDepot(depot);

                        foreach (var c in cdnClients)
                        {
                            try
                            {
                                depotManifest = c.DownloadManifest(depot.id, depot.manifestId);
                                break;
                            }
                            catch (WebException) { }
                            catch (SocketException) { }
                        }

                        if (depotManifest == null)
                        {
                            Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot.manifestId, depot.id);
                            return;
                        }

                        newProtoManifest = new ProtoManifest(depotManifest, depot.manifestId);
                        newProtoManifest.SaveToFile(newManifestFileName);

                        Console.WriteLine(" Done!");
                    }
                }

                newProtoManifest.Files.Sort((x, y) => { return x.FileName.CompareTo(y.FileName); });

                if (Config.DownloadManifestOnly)
                {
                    StringBuilder manifestBuilder = new StringBuilder();
                    string txtManifest = Path.Combine(depot.installDir, string.Format("manifest_{0}.txt", depot.id));

                    foreach (var file in newProtoManifest.Files)
                    {
                        if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                            continue;

                        manifestBuilder.Append(string.Format("{0}\n", file.FileName));
                    }

                    File.WriteAllText(txtManifest, manifestBuilder.ToString());
                    continue;
                }

                ulong complete_download_size = 0;
                ulong size_downloaded = 0;
                string stagingDir = Path.Combine(depot.installDir, STAGING_DIR);

                var filesAfterExclusions = newProtoManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
                
                // Pre-process
                filesAfterExclusions.ForEach(file =>
                {
                    var fileFinalPath = Path.Combine(depot.installDir, file.FileName);
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

                        complete_download_size += file.TotalSize;
                    }
                });
                
                filesAfterExclusions.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                    .AsParallel().WithCancellation(cts.Token).WithDegreeOfParallelism(Config.MaxDownloads)
                    .ForAll(file =>
                {
                    string fileFinalPath = Path.Combine(depot.installDir, file.FileName);
                    string fileStagingPath = Path.Combine(stagingDir, file.FileName);

                    // This may still exist if the previous run exited before cleanup
                    if (File.Exists(fileStagingPath))
                    {
                        File.Delete(fileStagingPath);
                    }

                    FileStream fs = null;
                    List<ProtoManifest.ChunkData> neededChunks;
                    FileInfo fi = new FileInfo(fileFinalPath);
                    if (!fi.Exists)
                    {
                        // create new file. need all chunks
                        fs = File.Create(fileFinalPath);
                        fs.SetLength((long)file.TotalSize);
                        neededChunks = new List<ProtoManifest.ChunkData>(file.Chunks);
                    }
                    else
                    {
                        // open existing
                        ProtoManifest.FileData oldManifestFile = null;
                        if (oldProtoManifest != null)
                        {
                            oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
                        }

                        if (oldManifestFile != null)
                        {
                            neededChunks = new List<ProtoManifest.ChunkData>();

                            if (Config.VerifyAll || !oldManifestFile.FileHash.SequenceEqual(file.FileHash))
                            {
                                // we have a version of this file, but it doesn't fully match what we want

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

                                File.Move(fileFinalPath, fileStagingPath);

                                fs = File.Open(fileFinalPath, FileMode.Create);
                                fs.SetLength((long)file.TotalSize);

                                using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                                {
                                    foreach (var match in matchingChunks)
                                    {
                                        fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                        byte[] tmp = new byte[match.OldChunk.UncompressedLength];
                                        fsOld.Read(tmp, 0, tmp.Length);

                                        byte[] adler = Util.AdlerHash(tmp);
                                        if (!adler.SequenceEqual(match.OldChunk.Checksum))
                                        {
                                            neededChunks.Add(match.NewChunk);
                                        }
                                        else
                                        {
                                            fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                            fs.Write(tmp, 0, tmp.Length);
                                        }
                                    }
                                }

                                File.Delete(fileStagingPath);
                            }
                        }
                        else
                        {
                            // No old manifest or file not in old manifest. We must validate.

                            fs = File.Open(fileFinalPath, FileMode.Open);
                            if ((ulong)fi.Length != file.TotalSize)
                            {
                                fs.SetLength((long)file.TotalSize);
                            }

                            neededChunks = Util.ValidateSteam3FileChecksums(fs, file.Chunks.OrderBy(x => x.Offset).ToArray());
                        }
    
                        if (neededChunks.Count() == 0)
                        {
                            size_downloaded += file.TotalSize;
                            Console.WriteLine("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, fileFinalPath);
                            if (fs != null)
                                fs.Close();
                            return;
                        }
                        else
                        {
                            size_downloaded += (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
                        }
                    }

                    if (neededChunks.Count > 0 && cdnClients == null)
                    {
                        // If we didn't need to connect to get manifests, connect now.
                        lock (cdnClientsLock)
                        {
                            if (cdnClients == null)
                            {
                                cdnClients = CollectCDNClientsForDepot(depot);
                            }
                        }
                    }

                    foreach (var chunk in neededChunks)
                    {
                        if (cts.IsCancellationRequested) break;

                        string chunkID = Util.EncodeHexString(chunk.ChunkID);
                        CDNClient.DepotChunk chunkData = null;

                        while (!cts.IsCancellationRequested)
                        {
                            CDNClient client;
                            try
                            {
                                client = cdnClients.Take(cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }

                            DepotManifest.ChunkData data = new DepotManifest.ChunkData();
                            data.ChunkID = chunk.ChunkID;
                            data.Checksum = chunk.Checksum;
                            data.Offset = chunk.Offset;
                            data.CompressedLength = chunk.CompressedLength;
                            data.UncompressedLength = chunk.UncompressedLength;

                            try
                            {
                                chunkData = client.DownloadDepotChunk(depot.id, data);
                                break;
                            }
                            catch (WebException e)
                            {
                                if (e.Status == WebExceptionStatus.ProtocolError)
                                {
                                    var response = e.Response as HttpWebResponse;
                                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                                    {
                                        Console.WriteLine("Encountered 401 for chunk {0}. Aborting.", chunkID);
                                        cts.Cancel();
                                        break;
                                    }
                                    else
                                    {
                                        Console.WriteLine("Encountered error downloading chunk {0}: {1}", chunkID, response.StatusCode);
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Encountered error downloading chunk {0}: {1}", chunkID, e.Status);
                                }

                                // let client "cool off" before re-adding it to the queue
                                Thread.Sleep(500);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                            }
                            finally
                            {
                                cdnClients.Add(client);
                            }
                        }

                        if (chunkData == null)
                        {
                            Console.WriteLine("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.id);
                            return;
                        }

                        TotalBytesCompressed += chunk.CompressedLength;
                        DepotBytesCompressed += chunk.CompressedLength;
                        TotalBytesUncompressed += chunk.UncompressedLength;
                        DepotBytesUncompressed += chunk.UncompressedLength;

                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                        size_downloaded += chunk.UncompressedLength;
                    }

                    fs.Close();

                    Console.WriteLine("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, fileFinalPath);
                });

                ConfigStore.TheConfig.LastManifests[depot.id] = depot.manifestId;
                ConfigStore.Save();

                Console.WriteLine("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.id, DepotBytesCompressed, DepotBytesUncompressed);
            }

            Console.WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots", TotalBytesCompressed, TotalBytesUncompressed, depots.Count);
        }
    }
}
