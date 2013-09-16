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

namespace DepotDownloader
{
    static class ContentDownloader
    {
        private const string DEFAULT_DIR = "depots";
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;

        public static DownloadConfig Config = new DownloadConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;

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
                if (ContentDownloader.Config.InstallDirectory == null || ContentDownloader.Config.InstallDirectory == "")
                {
                    Directory.CreateDirectory( DEFAULT_DIR );

                    string depotPath = Path.Combine( DEFAULT_DIR, depotId.ToString() );
                    Directory.CreateDirectory( depotPath );

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);
                }
                else
                {
                    Directory.CreateDirectory(ContentDownloader.Config.InstallDirectory);

                    installDir = ContentDownloader.Config.InstallDirectory;
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

        static bool AccountHasAccess( uint depotId, bool appId=false )
        {
            if ( steam3 == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser) )
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
                if ( steam3.PackageInfo.TryGetValue( license, out package ) || package == null )
                {
                    KeyValue root = package.KeyValues[license.ToString()];
                    KeyValue subset = (appId == true ? root["appids"] : root["depotids"]);

                    foreach ( var child in subset.Children )
                    {
                        if ( child.AsInteger() == depotId )
                            return true;
                    }
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

        static uint GetSteam3AppChangeNumber(int appId)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return 0;
            }

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if (!steam3.AppInfo.TryGetValue((uint)appId, out app) || app == null)
            {
                return 0;
            }

            return app.ChangeNumber;
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

            if (depotChild == null)
                return INVALID_MANIFEST_ID;

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

        public static void DownloadApp(uint appId, uint depotId, string branch)
        {
            if(steam3 != null)
                steam3.RequestAppInfo(appId);

            if (!AccountHasAccess(appId, true))
            {
                string contentName = GetAppOrDepotName(INVALID_DEPOT_ID, appId);
                Console.WriteLine("App {0} ({1}) is not available from this account.", appId, contentName);
                return;
            }

            var depotIDs = new List<uint>();
            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

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
                Console.WriteLine("Depot {0} not listed for app {1}", depotId, appId);
                return;
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

            if( infos.Count() > 0 )
                DownloadSteam3( infos );
        }

        static DepotDownloadInfo GetDepotInfo(uint depotId, uint appId, string branch)
        {
            if(steam3 != null && appId != INVALID_APP_ID)
                steam3.RequestAppInfo((uint)appId);

            string contentName = GetAppOrDepotName(depotId, appId);

            if (!AccountHasAccess(depotId, appId == depotId))
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
            if (manifestID == 0)
            {
                Console.WriteLine("Depot {0} ({1}) missing public subsection or manifest section.", depotId, contentName);
                return null;
            }

            steam3.RequestDepotKey( depotId, ( uint )appId );
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

        private static void DownloadSteam3( List<DepotDownloadInfo> depots )
        {
            ulong TotalBytesCompressed = 0;
            ulong TotalBytesUncompressed = 0;

            foreach (var depot in depots)
            {
                ulong DepotBytesCompressed = 0;
                ulong DepotBytesUncompressed = 0;
                uint depotId = depot.id;
                ulong depot_manifest = depot.manifestId;
                byte[] depotKey = depot.depotKey;
                string installDir = depot.installDir;

                Console.WriteLine("Downloading depot {0} - {1}", depot.id, depot.contentName);
                Console.Write("Finding content servers...");

                CDNClient client = new CDNClient(steam3.steamClient, depotId, steam3.AppTickets[depotId], depotKey);
                var cdnServers = client.FetchServerList(cellId: (uint)Config.CellID);

                if (cdnServers.Count == 0)
                {
                    Console.WriteLine("\nUnable to find any content servers for depot {0} - {1}", depotId, depot.contentName);
                    return;
                }

                Console.WriteLine(" Done!");
                Console.Write("Downloading depot manifest...");

                for (int i = 0; i < cdnServers.Count; ++i)
                {
                    var server = cdnServers[i];
                    try
                    {
                        client.Connect(server);
                        break;
                    }
                    catch
                    {
                        Console.WriteLine("\nFailed to connect to content server {0}. Remaining content servers for depot: {1}.", server, cdnServers.Count - i - 1);
                    }
                }


                DepotManifest depotManifest = client.DownloadManifest(depot_manifest);

                if ( depotManifest == null )
                {
                    Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot_manifest, depotId);
                    return;
                }

                if (!depotManifest.DecryptFilenames(depotKey))
                {
                    Console.WriteLine("\nUnable to decrypt manifest for depot {0}", depotId);
                    return;
                }

                Console.WriteLine(" Done!");

                ulong complete_download_size = 0;
                ulong size_downloaded = 0;

                depotManifest.Files.Sort((x, y) => { return x.FileName.CompareTo(y.FileName); });

                if (Config.DownloadManifestOnly)
                {
                    StringBuilder manifestBuilder = new StringBuilder();
                    string txtManifest = Path.Combine(depot.installDir, string.Format("manifest_{0}.txt", depot.id));

                    foreach (var file in depotManifest.Files)
                    {
                        if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                            continue;

                        manifestBuilder.Append(string.Format("{0}\n", file.FileName));
                    }

                    File.WriteAllText(txtManifest, manifestBuilder.ToString());
                    continue;
                }

                depotManifest.Files.RemoveAll((x) => !TestIsFileIncluded(x.FileName));

                foreach (var file in depotManifest.Files)
                {
                    complete_download_size += file.TotalSize;
                }

                foreach (var file in depotManifest.Files)
                {
                    string download_path = Path.Combine(installDir, file.FileName);

                    if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                    {
                        if (!Directory.Exists(download_path))
                            Directory.CreateDirectory(download_path);
                        continue;
                    }

                    string dir_path = Path.GetDirectoryName(download_path);

                    if (!Directory.Exists(dir_path))
                        Directory.CreateDirectory(dir_path);

                    FileStream fs;
                    DepotManifest.ChunkData[] neededChunks;
                    FileInfo fi = new FileInfo(download_path);
                    if (!fi.Exists)
                    {
                        // create new file. need all chunks
                        fs = File.Create(download_path);
                        neededChunks = file.Chunks.ToArray();
                    }
                    else
                    {
                        // open existing
                        fs = File.Open(download_path, FileMode.Open);
                        if ((ulong)fi.Length != file.TotalSize)
                        {                    
                            fs.SetLength((long)file.TotalSize);
                        }
    
                        // find which chunks we need, in order so that we aren't seeking every which way
                        neededChunks = Util.ValidateSteam3FileChecksums(fs, file.Chunks.OrderBy(x => x.Offset).ToArray());
    
                        if (neededChunks.Count() == 0)
                        {
                            size_downloaded += file.TotalSize;
                            Console.WriteLine("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, download_path);
                            fs.Close();
                            continue;
                        }
                        else
                        {
                            size_downloaded += (file.TotalSize - (ulong)neededChunks.Select(x => (int)x.UncompressedLength).Sum());
                        }
                    }

                    Console.Write("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, download_path);

                    foreach (var chunk in neededChunks)
                    {
                        string chunkID = Util.EncodeHexString(chunk.ChunkID);

                        var chunkData = client.DownloadDepotChunk(chunk);
                        TotalBytesCompressed += chunk.CompressedLength;
                        DepotBytesCompressed += chunk.CompressedLength;
                        TotalBytesUncompressed += chunk.UncompressedLength;
                        DepotBytesUncompressed += chunk.UncompressedLength;

                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                        size_downloaded += chunk.UncompressedLength;

                        Console.CursorLeft = 0;
                        Console.Write("{0,6:#00.00}%", ((float)size_downloaded / (float)complete_download_size) * 100.0f);
                    }

                    fs.Close();

                    Console.WriteLine();
                }

                Console.WriteLine("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depotId, DepotBytesCompressed, DepotBytesUncompressed);
            }

            Console.WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots", TotalBytesCompressed, TotalBytesUncompressed, depots.Count);
        }
    }
}
