using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using SteamKit2;
using System.Net;

namespace DepotDownloader
{
    static class CDRManager
    {
        const string BLOB_FILENAME = "cdr.blob";

        static Blob cdrBlob;


        public static void Update()
        {
            Console.Write( "Updating CDR..." );

            byte[] cdr = GetCdr();
            byte[] cdrHash = GetHash( cdr );

            foreach ( var configServer in ServerCache.ConfigServers )
            {
                try
                {
                    ConfigServerClient csClient = new ConfigServerClient();
                    csClient.Connect( configServer );

                    byte[] tempCdr = csClient.GetContentDescriptionRecord( cdrHash );

                    if ( tempCdr == null )
                        continue;

                    if ( tempCdr.Length == 0 )
                        break;

                    cdr = tempCdr;
                    File.WriteAllBytes( BLOB_FILENAME, tempCdr );

                    break;
                }
                catch ( Exception )
                {
                    Console.WriteLine( "Warning: Unable to download CDR from config server {0}", configServer );
                }
            }

            if ( cdr == null )
            {
                Console.WriteLine( "Error: Unable to download CDR!" );
                return;
            }

            cdrBlob = new Blob( cdr );
            Console.WriteLine( " Done!" );
        }

        static Blob GetAppBlob( int appID )
        {
            Blob appsBlob = cdrBlob[ CDRFields.eFieldApplicationsRecord ].GetChildBlob();

            foreach ( var blobField in appsBlob.Fields )
            {
                Blob appBlob = blobField.GetChildBlob();
                int currentAppID = appBlob[ CDRAppRecordFields.eFieldAppId ].GetInt32Data();

                if ( appID != currentAppID )
                    continue;

                return appBlob;
            }

            return null;
        }

        public static string GetDepotName( int depotId )
        {
            Blob appBlob = GetAppBlob( depotId );

            if ( appBlob == null )
            {
                return null;
            }
            else
            {
                return appBlob[ CDRAppRecordFields.eFieldName ].GetStringData();
            }
        }

        public static int GetLatestDepotVersion( int depotId )
        {
            Blob appBlob = GetAppBlob( depotId );

            if ( appBlob == null )
            {
                return -1;
            } else {
                return appBlob[ CDRAppRecordFields.eFieldCurrentVersionId ].GetInt32Data();
            }
        }

        public static List<int> GetDepotIDsForGameserver( string gameName )
        {
            List<int> appIDs = new List<int>();

            Blob serverAppInfoBlob = GetAppBlob( 4 );
            Blob serverAppInfo = serverAppInfoBlob[ CDRAppRecordFields.eFieldFilesystemsRecord ].GetChildBlob();

            foreach ( var blobField in serverAppInfo.Fields )
            {
                Blob filesystemBlob = blobField.GetChildBlob();
                string mountName = filesystemBlob[CDRAppFilesystemFields.eFieldMountName].GetStringData();

                if ( mountName.Equals( gameName, StringComparison.OrdinalIgnoreCase) ||
                     mountName.Equals( gameName + "-win32", StringComparison.OrdinalIgnoreCase) ||
                     mountName.Equals( gameName + "-linux", StringComparison.OrdinalIgnoreCase))
                {
                    appIDs.Add( filesystemBlob[ CDRAppFilesystemFields.eFieldAppId ].GetInt32Data() );
                }
            }

            return appIDs;
        }

        static byte[] GetCdr()
        {
            try
            {
                return File.ReadAllBytes( BLOB_FILENAME );
            }
            catch
            {
                return null;
            }
        }
        static byte[] GetHash( byte[] cdr )
        {
            try
            {
                if ( cdr == null )
                    return null;

                return CryptoHelper.SHAHash( cdr );
            }
            catch
            {
                return null;
            }
        }
    }
}
