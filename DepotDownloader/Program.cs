using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SteamKit2;
using System.ComponentModel;
using System.Threading.Tasks;

namespace DepotDownloader
{
    class Program
    {
        static int Main( string[] args )
            => MainAsync( args ).GetAwaiter().GetResult();

        static async Task<int> MainAsync( string[] args )
        {
            if ( args.Length == 0 )
            {
                PrintUsage();
                return 1;
            }

            DebugLog.Enabled = false;

            ConfigStore.LoadFromFile( Path.Combine( Directory.GetCurrentDirectory(), "DepotDownloader.config" ) );

            #region Common Options

            string username = GetParameter<string>( args, "-username" ) ?? GetParameter<string>( args, "-user" );
            string password = GetParameter<string>( args, "-password" ) ?? GetParameter<string>( args, "-pass" );
            ContentDownloader.Config.RememberPassword = HasParameter( args, "-remember-password" );

            ContentDownloader.Config.DownloadManifestOnly = HasParameter( args, "-manifest-only" );

            int cellId = GetParameter<int>( args, "-cellid", -1 );
            if ( cellId == -1 )
            {
                cellId = 0;
            }

            ContentDownloader.Config.CellID = cellId;

            string fileList = GetParameter<string>( args, "-filelist" );
            string[] files = null;

            if ( fileList != null )
            {
                try
                {
                    string fileListData = File.ReadAllText(fileList);
                    files = fileListData.Split( new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries );

                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new List<string>();
                    ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                    foreach ( var fileEntry in files )
                    {
                        try
                        {
                            Regex rgx = new Regex( fileEntry, RegexOptions.Compiled | RegexOptions.IgnoreCase );
                            ContentDownloader.Config.FilesToDownloadRegex.Add( rgx );
                        }
                        catch
                        {
                            ContentDownloader.Config.FilesToDownload.Add( fileEntry );
                            continue;
                        }
                    }

                    Console.WriteLine( "Using filelist: '{0}'.", fileList );
                }
                catch (Exception ex)
                {
                    Console.WriteLine( "Warning: Unable to load filelist: {0}", ex.ToString() );
                }
            }

            ContentDownloader.Config.InstallDirectory = GetParameter<string>( args, "-dir" );

            ContentDownloader.Config.VerifyAll = HasParameter( args, "-verify-all" ) || HasParameter( args, "-verify_all" ) || HasParameter( args, "-validate" );
            ContentDownloader.Config.MaxServers = GetParameter<int>( args, "-max-servers", 20 );
            ContentDownloader.Config.MaxDownloads = GetParameter<int>( args, "-max-downloads", 4 );
            ContentDownloader.Config.MaxServers = Math.Max( ContentDownloader.Config.MaxServers, ContentDownloader.Config.MaxDownloads );

            #endregion

            ulong pubFile = GetParameter<ulong>( args, "-pubfile", ContentDownloader.INVALID_MANIFEST_ID );
            if ( pubFile != ContentDownloader.INVALID_MANIFEST_ID )
            {
                #region Pubfile Downloading

                if ( InitializeSteam( username, password ) )
                {
                    try
                    {
                        await ContentDownloader.DownloadPubfileAsync( pubFile ).ConfigureAwait( false );
                    }
                    catch ( Exception ex ) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException )
                    {
                        Console.WriteLine( ex.Message );
                        return 1;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine( "Error: InitializeSteam failed" );
                    return 1;
                }

                #endregion
            }
            else
            {
                #region App downloading

                string branch = GetParameter<string>( args, "-branch" ) ?? GetParameter<string>( args, "-beta" ) ?? ContentDownloader.DEFAULT_BRANCH;
                ContentDownloader.Config.BetaPassword = GetParameter<string>( args, "-betapassword" );

                ContentDownloader.Config.DownloadAllPlatforms = HasParameter( args, "-all-platforms" );
                string os = GetParameter<string>( args, "-os", null );

                if ( ContentDownloader.Config.DownloadAllPlatforms && !String.IsNullOrEmpty( os ) )
                {
                    Console.WriteLine("Error: Cannot specify -os when -all-platforms is specified.");
                    return 1;
                }

                uint appId = GetParameter<uint>( args, "-app", ContentDownloader.INVALID_APP_ID );
                if ( appId == ContentDownloader.INVALID_APP_ID )
                {
                    Console.WriteLine( "Error: -app not specified!" );
                    return 1;
                }

                uint depotId;
                bool isUGC = false;

                ulong manifestId = GetParameter<ulong>( args, "-ugc", ContentDownloader.INVALID_MANIFEST_ID );
                if ( manifestId != ContentDownloader.INVALID_MANIFEST_ID )
                {
                    depotId = appId;
                    isUGC = true;
                }
                else
                {
                    depotId = GetParameter<uint>( args, "-depot", ContentDownloader.INVALID_DEPOT_ID );
                    manifestId = GetParameter<ulong>( args, "-manifest", ContentDownloader.INVALID_MANIFEST_ID );
                    if ( depotId == ContentDownloader.INVALID_DEPOT_ID && manifestId != ContentDownloader.INVALID_MANIFEST_ID )
                    {
                        Console.WriteLine( "Error: -manifest requires -depot to be specified" );
                        return 1;
                    }
                }

                if ( InitializeSteam( username, password ) )
                {
                    try
                    {
                        await ContentDownloader.DownloadAppAsync( appId, depotId, manifestId, branch, os, isUGC ).ConfigureAwait( false );
                    }
                    catch ( Exception ex ) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException )
                    {
                        Console.WriteLine( ex.Message );
                        return 1;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine( "Error: InitializeSteam failed" );
                    return 1;
                }

                #endregion
            }
        return 0;
        }

        static bool InitializeSteam( string username, string password )
        {
            if ( username != null && password == null && ( !ContentDownloader.Config.RememberPassword || !ConfigStore.TheConfig.LoginKeys.ContainsKey( username ) ) )
            {
                do
                {
                    Console.Write( "Enter account password for \"{0}\": ", username );
                    if ( Console.IsInputRedirected )
                    {
                        password = Console.ReadLine();
                    }
                    else
                    {
                        // Avoid console echoing of password
                        password = Util.ReadPassword();
                    }
                    Console.WriteLine();
                } while ( String.Empty == password );
            }
            else if ( username == null )
            {
                Console.WriteLine( "No username given. Using anonymous account with dedicated server subscription." );
            }

            // capture the supplied password in case we need to re-use it after checking the login key
            ContentDownloader.Config.SuppliedPassword = password;

            return ContentDownloader.InitializeSteam3( username, password );
        }

        static int IndexOfParam( string[] args, string param )
        {
            for ( int x = 0; x < args.Length; ++x )
            {
                if ( args[ x ].Equals( param, StringComparison.OrdinalIgnoreCase ) )
                    return x;
            }
            return -1;
        }
        static bool HasParameter( string[] args, string param )
        {
            return IndexOfParam( args, param ) > -1;
        }

        static T GetParameter<T>( string[] args, string param, T defaultValue = default( T ) )
        {
            int index = IndexOfParam( args, param );

            if ( index == -1 || index == ( args.Length - 1 ) )
                return defaultValue;

            string strParam = args[ index + 1 ];

            var converter = TypeDescriptor.GetConverter( typeof( T ) );
            if ( converter != null )
            {
                return ( T )converter.ConvertFromString( strParam );
            }

            return default( T );
        }

        static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine( "Usage - downloading one or all depots for an app:" );
            Console.WriteLine( "\tdepotdownloader -app <id> [-depot <id> [-manifest <id>] | [-ugc <id>]]" );
            Console.WriteLine( "\t\t[-username <username> [-password <password>]] [other options]" );
            Console.WriteLine();
            Console.WriteLine( "Usage - downloading a Workshop item published via SteamUGC" );
            Console.WriteLine( "\tdepotdownloader -pubfile <id> [-username <username> [-password <password>]]" );
            Console.WriteLine();
            Console.WriteLine( "Parameters:" );
            Console.WriteLine( "\t-app <#>\t\t\t\t- the AppID to download." );
            Console.WriteLine( "\t-depot <#>\t\t\t\t- the DepotID to download." );
            Console.WriteLine( "\t-manifest <id>\t\t\t- manifest id of content to download (requires -depot, default: current for branch)." );
            Console.WriteLine( "\t-ugc <#>\t\t\t\t- the UGC ID to download." );
            Console.WriteLine( "\t-beta <branchname>\t\t\t- download from specified branch if available (default: Public)." );
            Console.WriteLine( "\t-betapassword <pass>\t\t- branch password if applicable." );
            Console.WriteLine( "\t-all-platforms\t\t\t- downloads all platform-specific depots when -app is used." );
            Console.WriteLine( "\t-os <os>\t\t\t\t- the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)" );
            Console.WriteLine();
            Console.WriteLine( "\t-pubfile <#>\t\t\t- the PublishedFileId to download. (Will automatically resolve to UGC id)" );
            Console.WriteLine();
            Console.WriteLine( "\t-username <user>\t\t- the username of the account to login to for restricted content.");
            Console.WriteLine( "\t-password <pass>\t\t- the password of the account to login to for restricted content." );
            Console.WriteLine( "\t-remember-password\t\t- if set, remember the password for subsequent logins of this user." );
            Console.WriteLine();
            Console.WriteLine( "\t-dir <installdir>\t\t- the directory in which to place downloaded files." );
            Console.WriteLine( "\t-filelist <file.txt>\t- a list of files to download (from the manifest). Can optionally use regex to download only certain files." );
            Console.WriteLine( "\t-validate\t\t\t\t- Include checksum verification of files already downloaded" );
            Console.WriteLine();
            Console.WriteLine( "\t-manifest-only\t\t\t- downloads a human readable manifest for any depots that would be downloaded." );
            Console.WriteLine( "\t-cellid <#>\t\t\t\t- the overridden CellID of the content server to download from." );
            Console.WriteLine( "\t-max-servers <#>\t\t- maximum number of content servers to use. (default: 8)." );
            Console.WriteLine( "\t-max-downloads <#>\t\t- maximum number of chunks to download concurrently. (default: 4)." );
        }
    }
}
