using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SteamKit2;

namespace DepotDownloader
{
    class Program
    {
        static void Main( string[] args )
        {
            if ( args.Length == 0 )
            {
                PrintUsage();
                return;
            }

            DebugLog.Enabled = false;

            bool bDumpManifest = HasParameter( args, "-manifest" );
            int appId = GetIntParameter( args, "-app" );
            int depotId = GetIntParameter( args, "-depot" );

            if ( appId == -1 )
            {
                Console.WriteLine( "Error: -app not specified!" );
                return;
            }

            ContentDownloader.Config.DownloadManifestOnly = bDumpManifest;

            int cellId = GetIntParameter(args, "-cellid");

            if (cellId == -1)
            {
                cellId = 0;
            }

            ContentDownloader.Config.CellID = cellId;

            int depotVersion = GetIntParameter( args, "-version" );
            ContentDownloader.Config.PreferBetaVersions = HasParameter( args, "-beta" );
            ContentDownloader.Config.BetaPassword = GetStringParameter( args, "-betapassword" );

            string fileList = GetStringParameter( args, "-filelist" );
            string[] files = null;

            if ( fileList != null )
            {
                try
                {
                    string fileListData = File.ReadAllText( fileList );
                    files = fileListData.Split( new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries );

                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new List<string>();
                    ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                    foreach (var fileEntry in files)
                    {
                        try
                        {
                            Regex rgx = new Regex(fileEntry, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                        }
                        catch
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry);
                            continue;
                        }
                    }

                    Console.WriteLine( "Using filelist: '{0}'.", fileList );
                }
                catch ( Exception ex )
                {
                    Console.WriteLine( "Warning: Unable to load filelist: {0}", ex.ToString() );
                }
            }

            string username = GetStringParameter(args, "-username");
            string password = GetStringParameter(args, "-password");
            ContentDownloader.Config.InstallDirectory = GetStringParameter(args, "-dir");
            ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");
            string branch = GetStringParameter(args, "-branch") ?? GetStringParameter(args, "-beta") ?? "Public";

            if (username != null && password == null)
            {
                Console.Write("Enter account password: ");
                password = Util.ReadPassword();
                Console.WriteLine();
            }

            ContentDownloader.InitializeSteam3(username, password);
            ContentDownloader.DownloadApp(appId, depotId, branch);
            ContentDownloader.ShutdownSteam3();
        }

        static int IndexOfParam( string[] args, string param )
        {
            for ( int x = 0 ; x < args.Length ; ++x )
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
        static int GetIntParameter( string[] args, string param )
        {
            string strParam = GetStringParameter( args, param );

            if ( strParam == null )
                return -1;

            int intParam = -1;
            if ( !int.TryParse( strParam, out intParam ) )
                return -1;

            return intParam;
        }
        static string GetStringParameter( string[] args, string param )
        {
            int index = IndexOfParam( args, param );

            if ( index == -1 || index == ( args.Length - 1 ) )
                return null;

            return args[ index + 1 ];
        }

        static void PrintUsage()
        {
            Console.WriteLine( "\nUse: depotdownloader <parameters> [optional parameters]\n" );

            Console.WriteLine( "Parameters:" );
            Console.WriteLine("\t-app #\t\t\t\t- the AppID to download.");            
            Console.WriteLine();

            Console.WriteLine( "Optional Parameters:" );
            Console.WriteLine( "\t-depot #\t\t\t- the DepotID to download." );
            Console.WriteLine( "\t-cellid #\t\t\t- the CellID of the content server to download from." );
            Console.WriteLine( "\t-username user\t\t\t- the username of the account to login to for restricted content." );
            Console.WriteLine( "\t-password pass\t\t\t- the password of the account to login to for restricted content." );
            Console.WriteLine( "\t-dir installdir\t\t\t- the directory in which to place downloaded files." );
            Console.WriteLine( "\t-filelist filename.txt\t\t- a list of files to download (from the manifest). Can optionally use regex to download only certain files." );
            Console.WriteLine( "\t-all-platforms\t\t\t- downloads all platform-specific depots when -app is used." );
            Console.WriteLine( "\t-beta\t\t\t\t- download beta version of depots if available." );
            Console.WriteLine( "\t-manifest\t\t\t- downloads a human readable manifest for any depots that would be downloaded." );
        }
    }
}
