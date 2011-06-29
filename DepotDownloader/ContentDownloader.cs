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

        public static void Download( int depotId, int depotVersion, int cellId, string username, string password, bool onlyManifest, bool gameServer, string installDir, string[] fileList )
        {
            if ( !CreateDirectories( depotId, depotVersion, ref installDir ) )
            {
                Console.WriteLine( "Error: Unable to create install directories!" );
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

            string manifestFile = Path.Combine( installDir, "manifest.bin" );
            string txtManifest = Path.Combine( installDir, "manifest.txt" );

            ContentServerClient csClient = new ContentServerClient();

            csClient.Connect( contentServer );


            ContentServerClient.StorageSession session = null;
            try
            {
                session = csClient.OpenStorage( ( uint )depotId, ( uint )depotVersion, ( uint )cellId, credentials );
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

                byte[] cryptKey = CDRManager.GetDepotEncryptionKey( depotId, depotVersion );
                string[] excludeList = null;

                if ( gameServer )
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

                    if ( gameServer && IsFileExcluded( dirEntry.FullName, excludeList ) )
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

                return servers.Aggregate((bestmin, x) => (bestmin == null || (x.Load <= bestmin.Load)) ? x :bestmin).StorageServer;
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
