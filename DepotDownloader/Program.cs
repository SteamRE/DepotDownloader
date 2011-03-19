using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

            int depotId = GetIntParameter( args, "-depot" );
            int depotVersion = GetIntParameter( args, "-version" );

            if ( depotVersion == -1 )
            {
                int latestVer = CDRManager.GetLatestDepotVersion( depotId );

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

            string username = GetStringParameter( args, "-username" );
            string password = GetStringParameter( args, "-password" );

            ContentDownloader.Download( depotId, depotVersion, cellId, username, password );

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
            Console.WriteLine( "\nUse: depotdownloader <parameters>\n" );

            Console.WriteLine( "Parameters:" );
            Console.WriteLine( "\t-depot #\t\t\t- the DepotID to download." );
            Console.WriteLine( "\t-version [# or \"latest\"]\t- the version of the depot to download.\n" );

            Console.WriteLine( "Optional Parameters:" );
            Console.WriteLine( "\t-cellid #\t\t\t- the CellID of the content server to download from." );
            Console.WriteLine( "\t-username user\t\t\t- the username of the account to login to for restricted content." );
            Console.WriteLine( "\t-password pass\t\t\t- the password of the account to login to for restricted content.\n" );
        }
    }
}
