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

namespace DepotDownloader
{
    static class ContentDownloader
    {
        const string DEFAULT_DIR = "depots";
        const int MAX_STORAGE_RETRIES = 500;

        static Steam3Session steam3;

        static bool CreateDirectories( int depotId, int depotVersion, ref string installDir )
        {
            try
            {
                if ( installDir == null || installDir == "" )
                {
                    Directory.CreateDirectory( DEFAULT_DIR );

                    string depotPath = Path.Combine( DEFAULT_DIR, depotId.ToString() );
                    Directory.CreateDirectory( depotPath );

                    installDir = Path.Combine( depotPath, depotVersion.ToString() );
                    Directory.CreateDirectory( installDir );
                }
                else
                {
                    Directory.CreateDirectory( installDir );

                    string serverFolder = CDRManager.GetDedicatedServerFolder( depotId );
                    if ( serverFolder != null && serverFolder != "" )
                    {
                        installDir = Path.Combine( installDir, serverFolder );
                        Directory.CreateDirectory( installDir );
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

        static bool AccountHasAccess( int depotId )
        {
            if ( steam3 == null || steam3.Licenses == null )
                return CDRManager.SubHasDepot( 0, depotId );

            foreach ( var license in steam3.Licenses )
            {
                if ( CDRManager.SubHasDepot( ( int )license.PackageID, depotId ) )
                    return true;
            }

            return false;
        }

        static bool DepotHasSteam3Manifest( int depotId, out ulong manifest_id )
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                manifest_id = 0;
                return false;
            }

            foreach (var app in steam3.AppInfo)
            {
                KeyValue depots;
                if (app.AppID == depotId && app.Sections.TryGetValue((int)EAppInfoSection.AppInfoSectionDepots, out depots))
                {
                    string key = depotId.ToString();

                    // check depots for app
                    foreach (var kv in depots[key].Children)
                    {
                        var node = kv.Children
                            .Where(c => c.Name == "manifests").First().Children
                            .Where(d => d.Name == "Public").First();

                        manifest_id = UInt64.Parse(node.Value);
                        return true;
                    }
                }
            }

            manifest_id = 0;
            return false;
        }

        public static void Download( int depotId, int depotVersion, int cellId, string username, string password, bool onlyManifest, bool gameServer, bool exclude, string installDir, string[] fileList )
        {
            if ( !CreateDirectories( depotId, depotVersion, ref installDir ) )
            {
                Console.WriteLine( "Error: Unable to create install directories!" );
                return;
            }

            ContentServerClient.Credentials credentials = null;

            if (username != null)
            {
                // ServerCache.BuildAuthServers( username );
                credentials = GetCredentials((uint)depotId, username, password);
            }

            if (!AccountHasAccess(depotId))
            {
                string contentName = CDRManager.GetDepotName(depotId);
                Console.WriteLine("Depot {0} ({1}) is not available from this account.", depotId, contentName);

                if (steam3 != null)
                    steam3.Disconnect();

                return;
            }

            ulong steam3_manifest;
            if ( DepotHasSteam3Manifest( depotId, out steam3_manifest ) )
            {
                DownloadSteam3( credentials, depotId, depotVersion, cellId, steam3_manifest, installDir );
            }
            else
            {
                DownloadSteam2( credentials, depotId, depotVersion, cellId, username, password, onlyManifest, gameServer, exclude, installDir, fileList );
            }

            if ( steam3 != null )
                steam3.Disconnect();
        }

        private static void DownloadSteam3( ContentServerClient.Credentials credentials, int depotId, int depotVersion, int cellId, ulong depot_manifest, string installDir )
        {
            Console.Write("Finding content servers...");
/*            IPEndPoint contentServer = GetAnyStorageServer();

            if (contentServer == null)
            {
                Console.WriteLine("\nError: Unable to find any content servers");
                return;
            }
*/

            // find a proper bootstrap...
            CDNClient.ClientEndPoint contentServer1 = new CDNClient.ClientEndPoint("63.237.208.106", 80);
            List<CDNClient.ClientEndPoint> cdnServers = CDNClient.FetchServerList(contentServer1, cellId);

            if (cdnServers.Count == 0)
            {
                Console.WriteLine("CS server returned 0 servers, not sure why this happens.");
                cdnServers.Add(contentServer1);
//                return;
            }

            Console.WriteLine(" Done!");
            Console.Write("Downloading depot manifest...");

            CDNClient cdnClient = new CDNClient(cdnServers[0], credentials.AppTicket);

            if (!cdnClient.Connect())
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

            string manifestFile = Path.Combine(installDir, "manifest.bin");
            string keyFile = Path.Combine(installDir, "depotkey.bin");
            File.WriteAllBytes(manifestFile, manifest);
            File.WriteAllBytes(keyFile, steam3.DepotKey);

            DepotManifest depotManifest = new DepotManifest(manifest);

            if (!depotManifest.DecryptFilenames(steam3.DepotKey))
            {
                Console.WriteLine("\nUnable to decrypt manifest for depot {0}", depotId);
                return;
            }

            Console.WriteLine(" Done!");

            ulong complete_download_size = 0;
            ulong size_downloaded = 0;

            foreach (var file in depotManifest.Files)
            {
                complete_download_size += file.TotalSize;
            }

            foreach (var file in depotManifest.Files)
            {
                string download_path = Path.Combine(installDir, file.FileName);
                string dir_path = Path.GetDirectoryName(download_path);

                int top = Console.CursorTop;
                Console.WriteLine("00.00% Downloading {0}", download_path);
                int top_post = Console.CursorTop;
                Console.CursorTop = top;

                if (!Directory.Exists(dir_path))
                    Directory.CreateDirectory(dir_path);

                FileStream fs = File.Create(download_path);
                fs.SetLength((long)file.TotalSize);

                foreach (var chunk in file.Chunks)
                {
                    string chunkID = Utils.BinToHex(chunk.ChunkID);

                    byte[] encrypted_chunk = cdnClient.DownloadDepotChunk(depotId, chunkID);
                    byte[] chunk_data = cdnClient.ProcessChunk(encrypted_chunk, steam3.DepotKey);

                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    fs.Write(chunk_data, 0, chunk_data.Length);

                    size_downloaded += chunk.UncompressedLength;

                    Console.CursorLeft = 0;
                    Console.Write("{0:00.00}", ((float)size_downloaded / (float)complete_download_size) * 100.0f);
                }

                Console.CursorTop = top_post;
            }
        }

        private static void DownloadSteam2( ContentServerClient.Credentials credentials, int depotId, int depotVersion, int cellId, string username, string password, bool onlyManifest, bool gameServer, bool exclude, string installDir, string[] fileList )
        {
            Console.Write("Finding content servers...");
            IPEndPoint[] contentServers = GetStorageServer(depotId, depotVersion, cellId);

            if (contentServers.Length == 0)
            {
                Console.WriteLine("\nError: Unable to find any content servers for depot {0}, version {1}", depotId, depotVersion);
                return;
            }

            Console.WriteLine(" Done!");

            string manifestFile = Path.Combine(installDir, "manifest.bin");
            string txtManifest = Path.Combine(installDir, "manifest.txt");

            ContentServerClient csClient = new ContentServerClient();

            ContentServerClient.StorageSession session = null;
            int retryCount = 0;
            int server = 0;

            while ( session == null )
            {
                try
                {
                    csClient.Connect( contentServers[server] );
                    session = csClient.OpenStorage( ( uint )depotId, ( uint )depotVersion, ( uint )cellId, credentials );
                }
                catch ( Steam2Exception ex )
                {
                    csClient.Disconnect();
                    retryCount++;
                    server++;
                    if (server >= contentServers.Length)
                        server = 0;

                    if ( retryCount > MAX_STORAGE_RETRIES )
                    {
                        Console.WriteLine( "Unable to open storage: " + ex.Message );

                        if (steam3 != null)
                            steam3.Disconnect();
                        return;
                    }
                }
            }

            using ( session )
            {
                Console.Write( "Downloading depot manifest..." );

                Steam2Manifest manifest = session.DownloadManifest();
                byte[] manifestData = manifest.RawData;

                File.WriteAllBytes( manifestFile, manifestData );

                Console.WriteLine( " Done!" );

                if ( onlyManifest )
                    File.Delete( txtManifest );

                StringBuilder manifestBuilder = new StringBuilder();
                List<Regex> rgxList = new List<Regex>();

                if ( fileList != null )
                {
                    foreach ( string fileListentry in fileList )
                    {
                        try
                        {
                            Regex rgx = new Regex( fileListentry, RegexOptions.Compiled | RegexOptions.IgnoreCase );
                            rgxList.Add( rgx );
                        }
                        catch { continue; }
                    }
                }

                byte[] cryptKey = CDRManager.GetDepotEncryptionKey( depotId, depotVersion );
                string[] excludeList = null;

                if ( gameServer && exclude )
                    excludeList = GetExcludeList( session, manifest );

                for ( int x = 0 ; x < manifest.Nodes.Count ; ++x )
                {
                    var dirEntry = manifest.Nodes[ x ];

                    string downloadPath = Path.Combine( installDir, dirEntry.FullName.ToLower() );

                    if ( onlyManifest )
                    {
                        if ( dirEntry.FileID == -1 )
                            continue;

                        manifestBuilder.Append( string.Format( "{0}\n", dirEntry.FullName ) );
                        continue;
                    }

                    if ( gameServer && exclude && IsFileExcluded( dirEntry.FullName, excludeList ) )
                        continue;

                    if ( fileList != null )
                    {
                        bool bMatched = false;

                        foreach ( string fileListEntry in fileList )
                        {
                            if ( fileListEntry.Equals( dirEntry.FullName, StringComparison.OrdinalIgnoreCase ) )
                            {
                                bMatched = true;
                                break;
                            }
                        }

                        if ( !bMatched )
                        {
                            foreach ( Regex rgx in rgxList )
                            {
                                Match m = rgx.Match( dirEntry.FullName );

                                if ( m.Success )
                                {
                                    bMatched = true;
                                    break;
                                }
                            }
                        }

                        if ( !bMatched )
                            continue;

                        string path = Path.GetDirectoryName( downloadPath );

                        if ( !Directory.Exists( path ) )
                            Directory.CreateDirectory( path );
                    }

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
                    Console.WriteLine( " {0:0.00}%\t{1}", perc, downloadPath );

                    FileInfo fi = new FileInfo( downloadPath );

                    if ( fi.Exists && fi.Length == dirEntry.SizeOrCount )
                        continue;

                    var file = session.DownloadFile( dirEntry, ContentServerClient.StorageSession.DownloadPriority.High, cryptKey );

                    File.WriteAllBytes( downloadPath, file );
                }

                if ( onlyManifest )
                    File.WriteAllText( txtManifest, manifestBuilder.ToString() );
            }

            csClient.Disconnect();

        }

        static ContentServerClient.Credentials GetCredentials( uint depotId, string username, string password )
        {

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = password,

                },
                depotId
            );

            var steam3Credentials = steam3.WaitForCredentials();

            if ( !steam3Credentials.HasSessionToken || steam3Credentials.AppTicket == null || steam3Credentials.Steam2Ticket == null )
            {
                Console.WriteLine( "Unable to get steam3 credentials." );
                return null;
            }

            Steam2Ticket s2Ticket = new Steam2Ticket( steam3Credentials.Steam2Ticket );

            ContentServerClient.Credentials credentials = new ContentServerClient.Credentials()
            {
                Steam2Ticket = s2Ticket,
                AppTicket = steam3Credentials.AppTicket,
                SessionToken = steam3Credentials.SessionToken,
            };

            return credentials;
        }

        static IPEndPoint[] GetStorageServer( int depotId, int depotVersion, int cellId )
        {
            foreach ( IPEndPoint csdServer in ServerCache.CSDSServers )
            {
                ContentServerDSClient csdsClient = new ContentServerDSClient();
                csdsClient.Connect( csdServer );

                ContentServer[] servers = csdsClient.GetContentServerList( ( uint )depotId, ( uint )depotVersion, ( uint )cellId );

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

        static IPEndPoint GetAnyStorageServer()
        {
            foreach (IPEndPoint csdServer in ServerCache.CSDSServers)
            {
                ContentServerDSClient csdsClient = new ContentServerDSClient();
                csdsClient.Connect(csdServer);

                IPEndPoint[] servers = csdsClient.GetContentServerList();

                if (servers == null)
                {
                    Console.WriteLine("Warning: CSDS {0} returned empty server list.", csdServer);
                    continue;
                }

                if (servers.Length == 0)
                    continue;

                return servers[PsuedoRandom.GetRandomInt(0, servers.Length - 1)];
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
