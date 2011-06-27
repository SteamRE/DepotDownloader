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
        const string DOWNLOAD_DIR = "depots";

        static Steam3Session steam3;

        static bool CreateDirectories( int depotId, int depotVersion, out string downloadDir )
        {
            downloadDir = null;

            try
            {
                Directory.CreateDirectory( DOWNLOAD_DIR );

                string depotPath = Path.Combine( DOWNLOAD_DIR, depotId.ToString() );
                Directory.CreateDirectory( depotPath );

                downloadDir = Path.Combine( depotPath, depotVersion.ToString() );
                Directory.CreateDirectory( downloadDir );
            }
            catch
            {
                return false;
            }

            return true;
        }

        public static void Download( int depotId, int depotVersion, int cellId, string username, string password, bool onlyManifest, string[] fileList )
        {
            string downloadDir;
            if ( !CreateDirectories( depotId, depotVersion, out downloadDir ) )
            {
                Console.WriteLine( "Error: Unable to create download directories!" );
                return;
            }

            Console.Write( "Finding content servers..." );
            IPEndPoint contentServer = GetStorageServer( depotId, depotVersion, cellId );

            if ( contentServer == null )
            {
                Console.WriteLine( "\nError: Unable to find any content servers for depot {0}, version {1}", depotId, depotVersion );
                return;
            }

            Console.WriteLine( " Done!" );

            ContentServerClient.Credentials credentials = null;

            if ( username != null )
            {
                ServerCache.BuildAuthServers( username );
                credentials = GetCredentials( ( uint )depotId, username, password );
            }

            string manifestFile = Path.Combine( downloadDir, "manifest.bin" );
            string txtManifest = Path.Combine( downloadDir, "manifest.txt" );

            ContentServerClient csClient = new ContentServerClient();

            csClient.Connect( contentServer );


            ContentServerClient.StorageSession session = null;
            try
            {
                session = csClient.OpenStorage( ( uint )depotId, ( uint )depotVersion, ( uint )cellId );
            }
            catch ( Steam2Exception ex )
            {
                Console.WriteLine( "Unable to open storage: " + ex.Message );

                if ( steam3 != null )
                    steam3.Disconnect();

                return;
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

                for ( int x = 0 ; x < manifest.Nodes.Count ; ++x )
                {
                    var dirEntry = manifest.Nodes[ x ];

                    string downloadPath = Path.Combine( downloadDir, dirEntry.FullName );

                    if ( onlyManifest )
                    {
                        if ( dirEntry.FileID == -1 )
                            continue;

                        manifestBuilder.Append( string.Format( "{0}\n", dirEntry.FullName ) );
                        continue;
                    }

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
                    Console.WriteLine( " {0:0.00}%\t{1}", perc, dirEntry.FullName );

                    FileInfo fi = new FileInfo( downloadPath );

                    if ( fi.Exists && fi.Length == dirEntry.SizeOrCount )
                        continue;

                    var file = session.DownloadFile( dirEntry, ContentServerClient.StorageSession.DownloadPriority.High );

                    File.WriteAllBytes( downloadPath, file );
                }

                if ( onlyManifest )
                    File.WriteAllText( txtManifest, manifestBuilder.ToString() );
            }

            csClient.Disconnect();

            if ( steam3 != null )
                steam3.Disconnect();

        }

        static ContentServerClient.Credentials GetCredentials( uint depotId, string username, string password )
        {
            IPEndPoint authServer = GetAuthServer();
            if ( authServer == null )
            {
                Console.WriteLine( "Error: Unable to get authserver!" );
                return null;
            }

            AuthServerClient asClient = new AuthServerClient();
            asClient.Connect( authServer );

            ClientTGT clientTgt;
            byte[] serverTgt;
            AuthBlob accountRecord;

            Console.Write( "Logging '{0}' into Steam2... ", username );
            AuthServerClient.LoginResult result = asClient.Login( username, password, out clientTgt, out serverTgt, out accountRecord );

            if ( result != AuthServerClient.LoginResult.LoggedIn )
            {
                Console.WriteLine( "Unable to login to Steam2: {0}", result );
                return null;
            }

            Console.WriteLine( " Done!" );

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = password,

                    ClientTGT = clientTgt,
                    ServerTGT = serverTgt,
                    AccRecord = accountRecord,
                },
                depotId
            );

            var steam3Credentials = steam3.WaitForCredentials();

            if ( !steam3Credentials.HasSessionToken || steam3Credentials.AppTicket == null )
            {
                Console.WriteLine( "Unable to get steam3 credentials." );
                return null;
            }

            ContentServerClient.Credentials credentials = new ContentServerClient.Credentials()
            {
                ServerTGT = serverTgt,
                AppTicket = steam3Credentials.AppTicket,
                SessionToken = steam3Credentials.SessionToken,
            };

            return credentials;
        }

        static IPEndPoint GetStorageServer( int depotId, int depotVersion, int cellId )
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

                return servers[ 0 ].StorageServer;
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
