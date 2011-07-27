using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.IO;

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

            ServerCache.Build();
            CDRManager.Update();

            if (HasParameter( args, "-list" ) )
            {
                CDRManager.ListGameServers();
                return;
            }

            bool bDebot = true;
            bool bGameserver = true;
            bool bApp = true;

            int depotId = -1;
            string gameName = GetStringParameter( args, "-game" );

            if ( gameName == null )
            {
                depotId = GetIntParameter( args, "-app" );
                bGameserver = false;
                if ( depotId == -1 )
                {
                    depotId = GetIntParameter( args, "-depot" );
                    bApp = false;
                    if ( depotId == -1 )
                    {
                        depotId = GetIntParameter( args, "-manifest" );
                        bDebot = false;

                        if ( depotId == -1 )
                        {
                            Console.WriteLine( "Error: -game, -app, -depot or -manifest not specified!" );
                            return;
                        }
                    }
                }
            }

            int depotVersion = GetIntParameter( args, "-version" );
            bool bBeta = HasParameter( args, "-beta" );

            if ( !bGameserver && !bApp && depotVersion == -1 )
            {
                int latestVer = CDRManager.GetLatestDepotVersion( depotId, bBeta );

                if ( latestVer == -1 )
                {
                    Console.WriteLine( "Error: Unable to find DepotID {0} in the CDR!", depotId );
                    return;
                }

                string strVersion = GetStringParameter( args, "-version" );
                if ( strVersion != null && strVersion.Equals( "latest", StringComparison.OrdinalIgnoreCase ) )
                {
                    Console.WriteLine( "Using latest version: {0}", latestVer );
                    depotVersion = latestVer;
                }
                else
                {
                    Console.WriteLine( "Available depot versions:" );
                    Console.WriteLine( "  Oldest: 0" );
                    Console.WriteLine( "  Newest: {0}", latestVer );
                    return;
                }
            }

            int cellId = GetIntParameter( args, "-cellid" );

            if ( cellId == -1 )
            {
                cellId = 0;
                Console.WriteLine(
                    "Warning: Using default CellID of 0! This may lead to slow downloads. " +
                    "You can specify the CellID using the -cellid parameter" );
            }

            string fileList = GetStringParameter( args, "-filelist" );
            string[] files = null;

            if ( fileList != null )
            {
                try
                {
                    string fileListData = File.ReadAllText( fileList );
                    files = fileListData.Split( new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries );

                    Console.WriteLine( "Using filelist: '{0}'.", fileList );
                }
                catch ( Exception ex )
                {
                    Console.WriteLine( "Warning: Unable to load filelist: {0}", ex.ToString() );
                }
            }

            string username = GetStringParameter( args, "-username" );
            string password = GetStringParameter( args, "-password" );
            string installDir = GetStringParameter( args, "-dir" );
            bool bNoExclude = HasParameter( args, "-no-exclude" );
            bool bAllPlatforms = HasParameter( args, "-all-platforms" );

            if (username != null && password == null)
            {
                Console.Write( "Enter account password: " );
                password = Util.ReadPassword();
                Console.WriteLine();
            }

            if ( !bGameserver && !bApp )
            {
                ContentDownloader.Download( depotId, depotVersion, cellId, username, password, !bDebot, false, false, installDir, files );
            }
            else
            {
                List<int> depotIDs;

                if ( bGameserver )
                    depotIDs = CDRManager.GetDepotIDsForGameserver( gameName, bAllPlatforms );
                else
                    depotIDs = CDRManager.GetDepotIDsForApp( depotId, bAllPlatforms );

                foreach ( int currentDepotId in depotIDs )
                {
                    depotVersion = CDRManager.GetLatestDepotVersion( currentDepotId, bBeta );
                    if ( depotVersion == -1 )
                    {
                        Console.WriteLine( "Error: Unable to find DepotID {0} in the CDR!", currentDepotId );
                        return;
                    }

                    string depotName = CDRManager.GetDepotName( currentDepotId );
                    Console.WriteLine( "Downloading \"{0}\" version {1} ...", depotName, depotVersion );

                    ContentDownloader.Download( currentDepotId, depotVersion, cellId, username, password, false, bGameserver, !bNoExclude, installDir, files );
                }
            }
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
            Console.WriteLine( "\t-depot #\t\t\t- the DepotID to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-manifest #\t\t\t- downloads a human readable manifest for the depot." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-app #\t\t\t\t- the AppID to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-game name\t\t\t- the HLDSUpdateTool game server to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-list\t\t\t\t- print list of game servers that can be downloaded using -game." );
            Console.WriteLine( "\t-version [# or \"latest\"]\t- the version of the depot to download.\n" );

            Console.WriteLine( "Optional Parameters:" );
            Console.WriteLine( "\t-cellid #\t\t\t- the CellID of the content server to download from." );
            Console.WriteLine( "\t-username user\t\t\t- the username of the account to login to for restricted content." );
            Console.WriteLine( "\t-password pass\t\t\t- the password of the account to login to for restricted content." );
            Console.WriteLine( "\t-dir installdir\t\t\t- the directory in which to place downloaded files." );
            Console.WriteLine( "\t-filelist filename.txt\t\t- a list of files to download (from the manifest). Can optionally use regex to download only certain files." );
            Console.WriteLine( "\t-no-exclude\t\t\t- don't exclude any files when downloading depots with the -game parameter." );
            Console.WriteLine( "\t-all-platforms\t\t\t- downloads all platform-specific depots when -game or -app is used." );
            Console.WriteLine( "\t-beta\t\t\t\t- download beta version of depots if available." );
        }
    }
}
