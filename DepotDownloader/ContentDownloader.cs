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
        const string DEFAULT_DIR = "depots";
        const int MAX_STORAGE_RETRIES = 500;
        const int MAX_CONNECT_RETRIES = 10;
        const int NUM_STEAM3_CONNECTIONS = 4;
        const int STEAM2_CONNECT_TIMEOUT_SECONDS = 5;

        public static DownloadConfig Config = new DownloadConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;

        private sealed class DepotDownloadInfo
        {
            public int id { get; private set; }
            public string installDir { get; private set; }
            public string contentName { get; private set; }

            public ulong manifestId { get; private set; }
            public byte[] depotKey;

            public DepotDownloadInfo(int depotid, ulong manifestId, string installDir, string contentName)
            {
                this.id = depotid;
                this.manifestId = manifestId;
                this.installDir = installDir;
                this.contentName = contentName;
            }
        }

        static bool CreateDirectories( int depotId, uint depotVersion, out string installDir )
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

        static bool AccountHasAccess( int depotId, bool appId=false )
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

        internal static KeyValue GetSteam3AppSection( int appId, EAppInfoSection section )
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if ( !steam3.AppInfo.TryGetValue( (uint)appId, out app ) || app == null )
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

        static uint GetSteam3AppBuildNumber(int appId, string branch)
        {
            if (appId == -1)
                return 0;


            KeyValue depots = ContentDownloader.GetSteam3AppSection(appId, EAppInfoSection.Depots);
            KeyValue branches = depots["branches"];
            KeyValue node = branches[branch];

            if (node == null)
                return 0;

            KeyValue buildid = node["buildid"];

            if (buildid == null)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static ulong GetSteam3DepotManifest(int depotId, int appId, string branch)
        {
            if (appId == -1)
                return 0;

            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            KeyValue depotChild = depots[depotId.ToString()];

            if (depotChild == null)
                return 0;

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
                return 0;

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
                        return 0;
                    }

                    return BitConverter.ToUInt64(manifest_bytes, 0);
                }

                Console.WriteLine("Invalid branch {0} for appId {1}", branch, appId);
                return 0;
            }

            if (node.Value == null)
                return 0;

            return UInt64.Parse(node.Value);
        }

        static string GetAppOrDepotName(int depotId, int appId)
        {
            if (depotId == -1)
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

                    RequestSteam2Ticket = true,
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

        public static void DownloadApp(int appId, int depotId, string branch)
        {
            if(steam3 != null)
                steam3.RequestAppInfo((uint)appId);

            if (!AccountHasAccess(appId, true))
            {
                string contentName = GetAppOrDepotName(-1, appId);
                Console.WriteLine("App {0} ({1}) is not available from this account.", appId, contentName);
                return;
            }

            var depotIDs = new List<int>();
            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

            if (depots != null)
            {
                foreach (var child in depots.Children)
                {
                    int id = -1;
                    if (int.TryParse(child.Name, out id) && child.Children.Count > 0 && (depotId == -1 || id == depotId))
                    {
                        depotIDs.Add(id);
                    }
                }
            }

            if (depotIDs == null || (depotIDs.Count == 0 && depotId == -1))
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

        static DepotDownloadInfo GetDepotInfo(int depotId, int appId, string branch)
        {
            if(steam3 != null && appId > 0)
                steam3.RequestAppInfo((uint)appId);

            string contentName = GetAppOrDepotName(depotId, appId);

            if (!AccountHasAccess(depotId, false))
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

            steam3.RequestDepotKey( ( uint )depotId, ( uint )appId );
            if (!steam3.DepotKeys.ContainsKey((uint)depotId))
            {
                Console.WriteLine("No valid depot key for {0}, unable to download.", depotId);
                return null;
            }

            byte[] depotKey = steam3.DepotKeys[(uint)depotId];

            var info = new DepotDownloadInfo( depotId, manifestID, installDir, contentName );
            info.depotKey = depotKey;
            return info;
        }

        private static void DownloadSteam3( List<DepotDownloadInfo> depots )
        {
            foreach (var depot in depots)
            {
                int depotId = depot.id;
                ulong depot_manifest = depot.manifestId;
                byte[] depotKey = depot.depotKey;
                string installDir = depot.installDir;

                Console.WriteLine("Downloading depot {0} - {1}", depot.id, depot.contentName);
                Console.Write("Finding content servers...");

                List<IPEndPoint> serverList = steam3.steamClient.GetServersOfType(EServerType.CS);

                List<CDNClient.ClientEndPoint> cdnServers = null;
                int counterDeferred = 0;

                for (int i = 0; ; i++)
                {
                    IPEndPoint endpoint = serverList[i % serverList.Count];

                    cdnServers = CDNClient.FetchServerList(new CDNClient.ClientEndPoint(endpoint.Address.ToString(), endpoint.Port), Config.CellID);

                    if (cdnServers == null) counterDeferred++;

                    if (cdnServers != null && cdnServers.Count((ep) => { return ep.Type == "CS"; }) > 0)
                        break;

                    if (((i + 1) % serverList.Count) == 0)
                    {
                        Console.WriteLine("Unable to find any Steam3 content servers");
                        return;
                    }
                }

                Console.WriteLine(" Done!");
                Console.Write("Downloading depot manifest...");

                List<CDNClient.ClientEndPoint> cdnEndpoints = cdnServers.Where((ep) => { return ep.Type == "CDN"; }).ToList();
                List<CDNClient.ClientEndPoint> csEndpoints = cdnServers.Where((ep) => { return ep.Type == "CS"; }).ToList();
                List<CDNClient> cdnClients = new List<CDNClient>();
                byte[] appTicket = steam3.AppTickets[(uint)depotId];

                foreach (var server in csEndpoints)
                {
                    CDNClient client;
                    if (appTicket == null)
                        client = new CDNClient(server, (uint)depotId, steam3.steamUser.SteamID);
                    else
                        client = new CDNClient(server, appTicket);

                    if (client.Connect())
                    {
                        cdnClients.Add(client);

                        if (cdnClients.Count >= NUM_STEAM3_CONNECTIONS)
                            break;
                    }
                }
                if (cdnClients.Count == 0)
                {
                    Console.WriteLine("\nCould not initialize connection with CDN.");
                    return;
                }

                DepotManifest depotManifest = cdnClients[0].DownloadDepotManifest( depotId, depot_manifest );

                if ( depotManifest == null )
                {
                    // TODO: check for 401s
                    for (int i = 1; i < cdnClients.Count && depotManifest == null; i++)
                    {
                        depotManifest = cdnClients[i].DownloadDepotManifest( depotId, depot_manifest );
                    }

                    if (depotManifest == null)
                    {
                        Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot_manifest, depotId);
                        return;
                    }
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
                        string chunkID = EncodeHexString(chunk.ChunkID);

                        byte[] encrypted_chunk = cdnClients[0].DownloadDepotChunk(depotId, chunkID);

                        if (encrypted_chunk == null)
                        {
                            for (int i = 1; i < cdnClients.Count && encrypted_chunk == null; i++)
                            {
                                encrypted_chunk = cdnClients[i].DownloadDepotChunk(depotId, chunkID);
                            }

                            if (encrypted_chunk == null)
                            {
                                Console.WriteLine("Unable to download chunk id {0} for depot {1}", chunkID, depotId);
                                fs.Close();
                                return;
                            }
                        }

                        byte[] chunk_data = CDNClient.ProcessChunk(encrypted_chunk, depotKey);

                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        fs.Write(chunk_data, 0, chunk_data.Length);

                        size_downloaded += chunk.UncompressedLength;

                        Console.CursorLeft = 0;
                        Console.Write("{0,6:#00.00}%", ((float)size_downloaded / (float)complete_download_size) * 100.0f);
                    }

                    fs.Close();

                    Console.WriteLine();
                }
            }
        }

        static string EncodeHexString( byte[] input )
        {
            return input.Aggregate( new StringBuilder(),
                       ( sb, v ) => sb.Append( v.ToString( "x2" ) )
                      ).ToString();
        }
    }
}
