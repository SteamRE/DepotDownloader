using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.Net;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;

namespace DepotDownloader
{
    static class ContentDownloader
    {
        const string DOWNLOAD_DIR = "depots";

        public static void Download( int depotId, int depotVersion, int cellId, string username, string password )
        {
            Directory.CreateDirectory( DOWNLOAD_DIR );

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

            string depotPath = Path.Combine( DOWNLOAD_DIR, depotId.ToString() );
            Directory.CreateDirectory( depotPath );

            string downloadDir = Path.Combine( depotPath, depotVersion.ToString() );
            Directory.CreateDirectory( downloadDir );

            string manifestFile = Path.Combine( downloadDir, string.Format( "manifest.bin", depotId, depotVersion ) );

            ContentServerClient csClient = new ContentServerClient();

            csClient.Connect( contentServer );
            csClient.EnterStorageMode( ( uint )cellId );

            uint storageId = csClient.OpenStorage( ( uint )depotId, ( uint )depotVersion, credentials );

            if ( storageId == uint.MaxValue )
            {
                Console.WriteLine( "This depot requires valid user credentials and a license for this app" );
                return;
            }

            Console.Write( "Downloading depot manifest..." );

            byte[] manifestData = csClient.DownloadManifest( storageId );
            File.WriteAllBytes( manifestFile, manifestData );

            Console.WriteLine( " Done!" );

            Manifest manifest = new Manifest( manifestData );

            for ( int x = 0; x < manifest.DirEntries.Count; ++x )
            {
                Manifest.DirectoryEntry dirEntry = manifest.DirEntries[ x ];

                string downloadPath = Path.Combine( downloadDir, dirEntry.FullName );

                if ( dirEntry.FileID == -1 )
                {
                    if ( !Directory.Exists( downloadPath ) )
                    {
                        // this is a directory, so lets just create it
                        Directory.CreateDirectory( downloadPath );
                    }

                    continue;
                }

                float perc = ( ( float )x / ( float )manifest.DirEntries.Count ) * 100.0f;
                Console.WriteLine( " {0:0.00}%\t{1}", perc, dirEntry.FullName );

                FileInfo fi = new FileInfo( downloadPath );

                if ( fi.Exists && fi.Length == dirEntry.ItemSize )
                    continue;

                ContentServerClient.File file = csClient.DownloadFile( storageId, dirEntry.FileID );

                if ( file.FileMode == 1 )
                {
                    // file is compressed
                    using ( MemoryStream ms = new MemoryStream( file.Data ) )
                    using ( DeflateStream ds = new DeflateStream( ms, CompressionMode.Decompress ) )
                    {
                        // skip zlib header
                        ms.Seek( 2, SeekOrigin.Begin );

                        byte[] inflated = new byte[ dirEntry.ItemSize ];
                        ds.Read( inflated, 0, inflated.Length );

                        file.Data = inflated;
                    }
                }
                else
                {
                    Debug.Assert( false, string.Format(
                        "Got file with unexpected filemode!\n" +
                        "DepotID: {0}\nVersion: {1}\nFile: {2}\nMode: {3}\n",
                        depotId, depotVersion, dirEntry.FullName, file.FileMode
                    ) );
                }

                File.WriteAllBytes( downloadPath, file.Data );
            }

            csClient.CloseStorage( storageId );
            csClient.ExitStorageMode();

            csClient.Disconnect();

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
            Blob accountRecord;

            Console.Write( "Logging in '{0}'... ", username );
            AuthServerClient.LoginResult result = asClient.Login( username, password, out clientTgt, out serverTgt, out accountRecord );

            if ( result != AuthServerClient.LoginResult.LoggedIn )
            {
                Console.WriteLine( "Unable to login to Steam2: {0}", result );
                return null;
            }

            Steam3Session steam3 = new Steam3Session(
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
