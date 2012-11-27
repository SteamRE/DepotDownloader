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

            ServerCache.Build();
            CDRManager.Update();

            if (HasParameter( args, "-list" ) )
            {
                CDRManager.ListGameServers();
                return;
            }

            bool bGameserver = true;
            bool bApp = false;
            bool bListDepots = HasParameter(args, "-listdepots");
            bool bDumpManifest = HasParameter( args, "-manifest" );

            int appId = -1;
            int depotId = -1;
            string gameName = GetStringParameter( args, "-game" );

            if ( gameName == null )
            {
                appId = GetIntParameter( args, "-app" );
                bGameserver = false;

                depotId = GetIntParameter( args, "-depot" );

                if ( depotId == -1 && appId == -1 )
                {
                    Console.WriteLine( "Error: -game, -app, or -depot not specified!" );
                    return;
                }
                else if ( appId >= 0 )
                {
                    bApp = true;
                }
            }

            ContentDownloader.Config.DownloadManifestOnly = bDumpManifest;

            int cellId = GetIntParameter(args, "-cellid");

            if (cellId == -1)
            {
                cellId = 0;
                if (GetStringParameter(args, "-username") == null)
                {
                    Console.WriteLine(
                        "Warning: Using default CellID of 0! This may lead to slow downloads. " +
                        "You can specify the CellID using the -cellid parameter");
                }
            }

            ContentDownloader.Config.CellID = cellId;

            int depotVersion = GetIntParameter( args, "-version" );
            ContentDownloader.Config.PreferBetaVersions = HasParameter( args, "-beta" );

            // this is a Steam2 option
            if ( !bGameserver && !bApp && depotVersion == -1 )
            {
                int latestVer = CDRManager.GetLatestDepotVersion(depotId, ContentDownloader.Config.PreferBetaVersions);

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
                else if ( strVersion == null )
                {
                    // this could probably be combined with the above
                    Console.WriteLine( "No version specified." );
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
            bool bNoExclude = HasParameter(args, "-no-exclude");
            ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");

            if (username != null && password == null)
            {
                Console.Write("Enter account password: ");
                password = Util.ReadPassword();
                Console.WriteLine();
            }

            if (username != null)
            {
                ContentDownloader.InitializeSteam3(username, password);
            }

            if (bApp)
            {
                ContentDownloader.DownloadApp(appId, depotId, bListDepots);
            }
            else if ( !bGameserver )
            {
                ContentDownloader.DownloadDepot(depotId, depotVersion, appId);
            }
            else
            {
                if (!bNoExclude)
                {
                    ContentDownloader.Config.UsingExclusionList = true;
                }

                List<int> depotIDs = CDRManager.GetDepotIDsForGameserver( gameName, ContentDownloader.Config.DownloadAllPlatforms );

                if ( depotIDs.Count == 0 )
                {
                    Console.WriteLine( "Error: No depots for specified game '{0}'", gameName );
                    return;
                }

                if ( bListDepots )
                {
                    Console.WriteLine( "\n  '{0}' Depots:", gameName );

                    foreach ( var depot in depotIDs )
                    {
                        var depotName = CDRManager.GetDepotName( depot );
                        Console.WriteLine( "{0} - {1}", depot, depotName );
                    }
                }
                else
                {
                    foreach ( int currentDepotId in depotIDs )
                    {
                        depotVersion = CDRManager.GetLatestDepotVersion(currentDepotId, ContentDownloader.Config.PreferBetaVersions);
                        if ( depotVersion == -1 )
                        {
                            Console.WriteLine( "Error: Unable to find DepotID {0} in the CDR!", currentDepotId );
                            break;
                        }

                        ContentDownloader.DownloadDepot(currentDepotId, depotVersion);
                    }
                }
            }

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
            Console.WriteLine( "\t-depot #\t\t\t- the DepotID to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-app #\t\t\t\t- the AppID to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-game name\t\t\t- the HLDSUpdateTool game server to download." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-list\t\t\t\t- print list of game servers that can be downloaded using -game." );
            Console.WriteLine( "\t  OR" );
            Console.WriteLine( "\t-dumpcdr-xml\t\t\t- exports CDR to XML (cdrdump.xml)." );
            Console.WriteLine();
            Console.WriteLine( "\t-version [# or \"latest\"]\t- the version of the depot to download. Uses latest if none specified.\n" );

            Console.WriteLine( "Optional Parameters:" );
            Console.WriteLine( "\t-cellid #\t\t\t- the CellID of the content server to download from." );
            Console.WriteLine( "\t-username user\t\t\t- the username of the account to login to for restricted content." );
            Console.WriteLine( "\t-password pass\t\t\t- the password of the account to login to for restricted content." );
            Console.WriteLine( "\t-dir installdir\t\t\t- the directory in which to place downloaded files." );
            Console.WriteLine( "\t-filelist filename.txt\t\t- a list of files to download (from the manifest). Can optionally use regex to download only certain files." );
            Console.WriteLine( "\t-no-exclude\t\t\t- don't exclude any files when downloading depots with the -game parameter." );
            Console.WriteLine( "\t-all-platforms\t\t\t- downloads all platform-specific depots when -game or -app is used." );
            Console.WriteLine( "\t-beta\t\t\t\t- download beta version of depots if available." );
            Console.WriteLine( "\t-listdepots\t\t\t- When used with -app or -game, only list relevant depotIDs and quit." );
            Console.WriteLine( "\t-manifest\t\t\t- downloads a human readable manifest for any depots that would be downloaded." );
        }
    }
}
