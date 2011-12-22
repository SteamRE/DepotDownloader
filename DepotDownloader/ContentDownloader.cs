using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.Net;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Threading;

namespace DepotDownloader
{
    static class ContentDownloader
    {
        const string DEFAULT_DIR = "depots";
        const int MAX_STORAGE_RETRIES = 500;
        const int MAX_CONNECT_RETRIES = 10;

        public static DownloadConfig Config = new DownloadConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;

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

                    string serverFolder = CDRManager.GetDedicatedServerFolder( depotId );
                    if ( serverFolder != null && serverFolder != "" )
                    {
                        installDir = Path.Combine(ContentDownloader.Config.InstallDirectory, serverFolder);
                        Directory.CreateDirectory(installDir);
                    }
                    else
                    {
                        installDir = ContentDownloader.Config.InstallDirectory;
                    }
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static string[] GetExcludeList( ContentServerClient.StorageSession session, Steam2Manifest manifest )
        {
            string[] excludeList = null;

            for ( int x = 0 ; x < manifest.Nodes.Count ; ++x )
            {
                var dirEntry = manifest.Nodes[ x ];
                if ( dirEntry.Name == "exclude.lst" && 
                     dirEntry.FullName.StartsWith( "reslists" + Path.DirectorySeparatorChar ) &&
                     ( dirEntry.Attributes & Steam2Manifest.Node.Attribs.EncryptedFile ) == 0 )
                {
                    string excludeFile = Encoding.UTF8.GetString( session.DownloadFile( dirEntry ) );
                    if ( Environment.OSVersion.Platform == PlatformID.Win32NT )
                        excludeFile = excludeFile.Replace( '/', Path.DirectorySeparatorChar );
                    else
                        excludeFile = excludeFile.Replace( '\\', Path.DirectorySeparatorChar );
                    excludeList = excludeFile.Split( new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries );
                    break;
                }
            }

            return excludeList;
        }

        static bool IsFileExcluded( string file, string[] excludeList )
        {
            if ( excludeList == null || file.Length < 1 )
                return false;

            foreach ( string e in excludeList )
            {
                int wildPos = e.IndexOf( "*" );

                if ( wildPos == -1 )
                {
                    if ( file.StartsWith( e ) )
                        return true;
                    continue;
                }

                if ( wildPos == 0 )
                {
                    if ( e.Length == 1 || file.EndsWith( e.Substring( 1 ) ) )
                        return true;
                    continue;
                }

                string start = e.Substring( 0, wildPos );
                string end = e.Substring( wildPos + 1, e.Length - wildPos - 1 );

                if ( file.StartsWith( start ) && file.EndsWith( end ) )
                    return true;
            }

            return false;
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

        static bool AccountHasAccess( int depotId )
        {
            if ( steam3 == null || steam3.Licenses == null )
                return CDRManager.SubHasDepot( 0, depotId );

            foreach ( var license in steam3.Licenses )
            {
                // TODO: support PackageInfoRequest/Response, this is a steam2 dependency
                if ( CDRManager.SubHasDepot( ( int )license.PackageID, depotId ) )
                    return true;
            }

            return false;
        }

        static bool AppIsSteam3(int appId)
        {
            if (steam3 == null || steam3.AppInfoOverridesCDR == null)
            {
                return false;
            }

            steam3.RequestAppInfo((uint)appId);

            bool app_override;
            if(!steam3.AppInfoOverridesCDR.TryGetValue((uint)appId, out app_override))
                return false;

            return app_override;
        }

        static KeyValue GetSteam3AppSection(int appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            SteamApps.AppInfoCallback.AppInfo app;
            if (!steam3.AppInfo.TryGetValue((uint)appId, out app))
            {
                return null;
            }

            KeyValue section_kv;
            if (!app.Sections.TryGetValue((int)section, out section_kv))
            {
                return null;
            }

            return section_kv;
        }

        enum DownloadSource
        {
            Steam2,
            Steam3
        }

        static DownloadSource GetAppDownloadSource(int appId)
        {
            if (appId == -1 || !AppIsSteam3(appId))
                return DownloadSource.Steam2;

            KeyValue config = GetSteam3AppSection(appId, EAppInfoSection.AppInfoSectionConfig);
            int contenttype = config[appId.ToString()]["contenttype"].AsInteger(0);

            // EContentDownloadSourceType?
            if (contenttype == 3)
                return DownloadSource.Steam3;

            return DownloadSource.Steam2;
        }


        static uint GetSteam3AppChangeNumber(int appId)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return 0;
            }

            SteamApps.AppInfoCallback.AppInfo app;
            if (!steam3.AppInfo.TryGetValue((uint)appId, out app))
            {
                return 0;
            }

            return app.ChangeNumber;
        }

        static ulong GetSteam3DepotManifest(int depotId, int appId)
        {
            if (appId == -1 || !AppIsSteam3(appId))
                return 0;

            KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.AppInfoSectionDepots);
            KeyValue depotChild = depots[appId.ToString()][depotId.ToString()];

            if (depotChild == null)
                return 0;

            var node = depotChild["manifests"]["Public"];

            return UInt64.Parse(node.Value);
        }

        static string GetAppOrDepotName(int depotId, int appId)
        {
            if (appId == -1 || !AppIsSteam3(appId))
            {
                return CDRManager.GetDepotName(depotId);
            }
            else if (depotId == -1)
            {
                KeyValue info = GetSteam3AppSection(appId, EAppInfoSection.AppInfoSectionCommon);

                if (info == null)
                    return String.Empty;

                return info[appId.ToString()]["name"].AsString();
            }
            else
            {
                KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.AppInfoSectionDepots);

                if (depots == null)
                    return String.Empty;

                KeyValue depotChild = depots[appId.ToString()][depotId.ToString()];

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

            if (!steam3Credentials.HasSessionToken)
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

        private static ContentServerClient.Credentials GetSteam2Credentials(uint appId)
        {
            if (steam3 == null || !steam3Credentials.HasSessionToken)
            {
                return null;
            }

            return new ContentServerClient.Credentials()
            {
                Steam2Ticket =  new Steam2Ticket(steam3Credentials.Steam2Ticket),
                AppTicket = steam3.AppTickets[appId],
                SessionToken = steam3Credentials.SessionToken,
            };
        }

        public static void DownloadApp(int appId)
        {
            if(steam3 != null)
                steam3.RequestAppInfo((uint)appId);

            if (!AccountHasAccess(appId))
            {
                string contentName = GetAppOrDepotName(-1, appId);
                Console.WriteLine("App {0} ({1}) is not available from this account.", appId, contentName);
                return;
            }

            List<int> depotIDs = null;

            if (AppIsSteam3(appId))
            {
                depotIDs = new List<int>();
                KeyValue depots = GetSteam3AppSection(appId, EAppInfoSection.AppInfoSectionDepots);

                if (depots != null)
                {
                    depots = depots[appId.ToString()];
                    foreach (var child in depots.Children)
                    {
                        if (child.Children.Count > 0)
                        {
                            depotIDs.Add(int.Parse(child.Name));
                        }
                    }
                }
            }
            else
            {
                // steam2 path
                depotIDs = CDRManager.GetDepotIDsForApp(appId, Config.DownloadAllPlatforms);
            }

            if (depotIDs == null || depotIDs.Count == 0)
            {
                Console.WriteLine("Couldn't find any depots to download for app {0}", appId);
                return;
            }

            foreach (var depot in depotIDs)
            {
                // Steam2 dependency
                int depotVersion = CDRManager.GetLatestDepotVersion(depot, Config.PreferBetaVersions);
                if (depotVersion == -1)
                {
                    Console.WriteLine("Error: Unable to find DepotID {0} in the CDR!", depot);
                    return;
                }

                DownloadDepot(depot, appId, depotVersion);
            }
        }

        public static void DownloadDepot(int depotId, int appId, int depotVersionRequested)
        {
            if(steam3 != null && appId > 0)
                steam3.RequestAppInfo((uint)appId);

            string contentName = GetAppOrDepotName(depotId, appId);

            if (!AccountHasAccess(depotId))
            {    
                Console.WriteLine("Depot {0} ({1}) is not available from this account.", depotId, contentName);

                return;
            }

            DownloadSource source = GetAppDownloadSource(appId);
            uint depotVersion = (uint)depotVersionRequested;

            if (source == DownloadSource.Steam3)
            {
                depotVersion = GetSteam3AppChangeNumber(appId);
            }

            string installDir;
            if (!CreateDirectories(depotId, depotVersion, out installDir))
            {
                Console.WriteLine("Error: Unable to create install directories!");
                return;
            }

            Console.WriteLine("Downloading \"{0}\" version {1} ...", contentName, depotVersion);

            if(steam3 != null)
                steam3.RequestAppTicket((uint)depotId);

            if (source == DownloadSource.Steam3)
            {
                ulong manifestID = GetSteam3DepotManifest(depotId, appId);
                if (manifestID == 0)
                {
                    Console.WriteLine("Depot {0} ({1}) missing Steam3 manifest.");
                    return;
                }

                steam3.RequestDepotKey((uint)depotId);
                byte[] depotKey = steam3.DepotKeys[(uint)depotId];

                DownloadSteam3(depotId, manifestID, depotKey, installDir);
            }
            else
            {
                // steam2 path
                DownloadSteam2(depotId, depotVersionRequested, installDir);
            }
        }

        private static void DownloadSteam3( int depotId, ulong depot_manifest, byte[] depotKey, string installDir )
        {
            Console.Write("Finding content servers...");

            List<IPEndPoint> serverList = steam3.steamClient.GetServersOfType(EServerType.ServerTypeCS);

            List<CDNClient.ClientEndPoint> cdnServers = null;
            int tries = 0, counterDeferred = 0, counterCDN = 0;

            for(int i = 0; ; i++ )
            {
                IPEndPoint endpoint = serverList[i % serverList.Count];

                cdnServers = CDNClient.FetchServerList(new CDNClient.ClientEndPoint(endpoint.Address.ToString(), endpoint.Port), Config.CellID);

                if (cdnServers == null) counterDeferred++;
                else if (cdnServers.Count == 0) counterCDN++;

                if (cdnServers != null && cdnServers.Count > 0)
                    break;

                if (((i+1) % serverList.Count) == 0)
                {
                    if (++tries > MAX_CONNECT_RETRIES)
                    {
                        Console.WriteLine("\nGiving up finding Steam3 content server.");
                        return;
                    }

                    Console.Write("\nSearching for content servers... (deferred: {0}, CDN: {1})", counterDeferred, counterCDN);
                    counterDeferred = 0;
                    counterCDN = 0;
                    Thread.Sleep(1000);
                }
            }

            if (cdnServers == null || cdnServers.Count == 0)
            {
                Console.WriteLine("Unable to find any Steam3 content servers");
                return;
            }

            Console.WriteLine(" Done!");
            Console.Write("Downloading depot manifest...");

            CDNClient cdnClient = null;

            foreach (var server in cdnServers)
            {
                CDNClient client = new CDNClient(cdnServers[0], steam3.AppTickets[(uint)depotId]);

                if (client.Connect())
                {
                    cdnClient = client;
                    break;
                }
            }

            if (cdnClient == null)
            {
                Console.WriteLine("\nCould not initialize connection with CDN.");
                return;
            }

            byte[] manifest = cdnClient.DownloadDepotManifest(depotId, depot_manifest);

            if (manifest == null)
            {
                Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot_manifest, depotId);
                return;
            }

            DepotManifest depotManifest = new DepotManifest(manifest);

            if (!depotManifest.DecryptFilenames(depotKey))
            {
                Console.WriteLine("\nUnable to decrypt manifest for depot {0}", depotId);
                return;
            }

            Console.WriteLine(" Done!");

            ulong complete_download_size = 0;
            ulong size_downloaded = 0;

            depotManifest.Files.RemoveAll((x) => !TestIsFileIncluded(x.FileName));
            depotManifest.Files.Sort((x, y) => { return x.FileName.CompareTo(y.FileName); });
 
            foreach (var file in depotManifest.Files)
            {
                complete_download_size += file.TotalSize;
            }

            foreach (var file in depotManifest.Files)
            {
                string download_path = Path.Combine(installDir, file.FileName);

                if (file.TotalSize == 0) // directory
                {
                    if (!Directory.Exists(download_path))
                        Directory.CreateDirectory(download_path);
                    continue;
                }

                string dir_path = Path.GetDirectoryName(download_path);

                if (!Directory.Exists(dir_path))
                    Directory.CreateDirectory(dir_path);

                // TODO: non-checksum validation
                FileInfo fi = new FileInfo(download_path);
                if (fi.Exists && (ulong)fi.Length == file.TotalSize)
                {
                    size_downloaded += file.TotalSize;
                    Console.WriteLine("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, download_path);
                    continue;
                }

                Console.Write("{0,6:#00.00}% {1}", ((float)size_downloaded / (float)complete_download_size) * 100.0f, download_path);

                FileStream fs = File.Create(download_path);
                fs.SetLength((long)file.TotalSize);

                foreach (var chunk in file.Chunks)
                {
                    string chunkID = Utils.EncodeHexString(chunk.ChunkID);

                    byte[] encrypted_chunk = cdnClient.DownloadDepotChunk(depotId, chunkID);
                    byte[] chunk_data = cdnClient.ProcessChunk(encrypted_chunk, depotKey);

                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    fs.Write(chunk_data, 0, chunk_data.Length);

                    size_downloaded += chunk.UncompressedLength;

                    Console.CursorLeft = 0;
                    Console.Write("{0,6:#00.00}%", ((float)size_downloaded / (float)complete_download_size) * 100.0f);
                }

                Console.WriteLine();
            }
        }

        private static void DownloadSteam2( int depotId, int depotVersion, string installDir )
        {
            Console.Write("Finding content servers...");
            IPEndPoint[] contentServers = GetStorageServer(depotId, depotVersion, Config.CellID);

            if (contentServers == null || contentServers.Length == 0)
            {
                Console.WriteLine("\nError: Unable to find any Steam2 content servers for depot {0}, version {1}", depotId, depotVersion);
                return;
            }

            Console.WriteLine(" Done!");
            Console.Write("Downloading depot manifest...");

            string txtManifest = Path.Combine(installDir, "manifest.txt");

            ContentServerClient csClient = new ContentServerClient();

            ContentServerClient.StorageSession session = null;
            int tries = 0;
            int counterSocket = 0, counterSteam2 = 0;

            for (int i = 0; ; i++)
            {
                IPEndPoint endpoint = contentServers[i % contentServers.Length];

                try
                {
                    csClient.Connect( endpoint );
                    session = csClient.OpenStorage( (uint)depotId, (uint)depotVersion, (uint)Config.CellID, GetSteam2Credentials( (uint)depotId ) );
                    break;
                }
                catch ( SocketException )
                {
                    counterSocket++;
                }
                catch ( Steam2Exception )
                {
                    csClient.Disconnect();
                    counterSteam2++;
                }

                if (((i + 1) % contentServers.Length) == 0)
                {
                    if (++tries > MAX_CONNECT_RETRIES)
                    {
                        Console.WriteLine("\nGiving up finding Steam2 content server.");
                        return;
                    }

                    Console.Write("\nSearching for content servers... (socket error: {0}, steam2 error: {1})", counterSocket, counterSteam2);
                    counterSocket = 0;
                    counterSteam2 = 0;
                    Thread.Sleep(1000);
                }
            }

            using ( session )
            {
                Steam2Manifest manifest = session.DownloadManifest();

                Console.WriteLine( " Done!" );

                StringBuilder manifestBuilder = new StringBuilder();

                byte[] cryptKey = CDRManager.GetDepotEncryptionKey( depotId, depotVersion );
                string[] excludeList = null;

                if ( Config.UsingExclusionList )
                    excludeList = GetExcludeList( session, manifest );

                for ( int x = 0 ; x < manifest.Nodes.Count ; ++x )
                {
                    var dirEntry = manifest.Nodes[ x ];

                    string downloadPath = Path.Combine( installDir, dirEntry.FullName.ToLower() );

                    if ( Config.DownloadManifestOnly )
                    {
                        if ( dirEntry.FileID == -1 )
                            continue;

                        manifestBuilder.Append( string.Format( "{0}\n", dirEntry.FullName ) );
                        continue;
                    }

                    if (Config.UsingExclusionList && IsFileExcluded(dirEntry.FullName, excludeList))
                        continue;

                    if (!TestIsFileIncluded(dirEntry.FullName))
                        continue;

                    string path = Path.GetDirectoryName(downloadPath);

                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);

                    if ( dirEntry.FileID == -1 )
                    {
                        if ( !Directory.Exists( downloadPath ) )
                        {
                            // this is a directory, so lets just create it
                            Directory.CreateDirectory( downloadPath );
                        }

                        continue;
                    }

                    float perc = ( ( float )x / ( float )manifest.Nodes.Count ) * 100.0f;
                    Console.WriteLine("{0,6:#00.00}%\t{1}", perc, downloadPath);

                    // TODO: non-checksum validation
                    FileInfo fi = new FileInfo( downloadPath );
                    if ( fi.Exists && fi.Length == dirEntry.SizeOrCount )
                        continue;

                    var file = session.DownloadFile( dirEntry, ContentServerClient.StorageSession.DownloadPriority.High, cryptKey );

                    File.WriteAllBytes( downloadPath, file );
                }

                if ( Config.DownloadManifestOnly )
                    File.WriteAllText( txtManifest, manifestBuilder.ToString() );
            }

            csClient.Disconnect();

        }

        static IPEndPoint[] GetStorageServer( int depotId, int depotVersion, int cellId )
        {
            foreach ( IPEndPoint csdServer in ServerCache.CSDSServers )
            {
                ContentServer[] servers;

                try
                {
                    ContentServerDSClient csdsClient = new ContentServerDSClient();
                    csdsClient.Connect( csdServer );

                    servers = csdsClient.GetContentServerList( (uint)depotId, (uint)depotVersion, (uint)cellId );
                }
                catch ( SocketException )
                {
                    servers = null;
                    continue;
                }

                if ( servers == null )
                {
                    Console.WriteLine( "Warning: CSDS {0} rejected the given depotid or version!", csdServer );
                    continue;
                }

                if ( servers.Length == 0 )
                    continue;

                return servers.OrderBy(x => x.Load).Select(x => x.StorageServer).ToArray();
            }

            return null;
        }

        static IPEndPoint GetAuthServer()
        {
            if ( ServerCache.AuthServers.Count > 0 )
                return ServerCache.AuthServers[ 0 ];

            return null;
        }
    }
}
