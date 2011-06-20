using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using SteamKit2;
using System.Net;

namespace DepotDownloader
{
    class CDR
    {
        [BlobField( FieldKey = CDRFields.eFieldApplicationsRecord, Depth = 1, Complex = true )]
        public List<App> Apps { get; set; }
    }

    class App
    {
        [BlobField( FieldKey = CDRAppRecordFields.eFieldName, Depth = 1 )]
        public string Name { get; set; }

        [BlobField( FieldKey = CDRAppRecordFields.eFieldAppId, Depth = 1 )]
        public int AppID { get; set; }

        [BlobField( FieldKey = CDRAppRecordFields.eFieldCurrentVersionId, Depth = 1 )]
        public int CurrentVersion { get; set; }

        [BlobField( FieldKey = CDRAppRecordFields.eFieldFilesystemsRecord, Complex = true, Depth = 1 )]
        public List<FileSystem> FileSystems { get; set; }
    }

    class FileSystem
    {
        [BlobField( FieldKey = CDRAppFilesystemFields.eFieldAppId )]
        public int AppID { get; set; }

        [BlobField( FieldKey = CDRAppFilesystemFields.eFieldMountName )]
        public string Name { get; set; }
    }

    static class CDRManager
    {
        const string BLOB_FILENAME = "cdr.blob";

        static CDR cdrObj;


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


            using ( var reader = BlobTypedReader<CDR>.Create( new MemoryStream( cdr ) ) )
            {
                reader.Process();

                cdrObj = reader.Target;
            }

            Console.WriteLine( " Done!" );
        }

        static App GetAppBlob( int appID )
        {
            return cdrObj.Apps.Find( ( app ) => app.AppID == appID );
        }

        public static string GetDepotName( int depotId )
        {
            App app = GetAppBlob( depotId );

            if ( app == null )
            {
                return null;
            }

            return app.Name;
        }

        public static int GetLatestDepotVersion( int depotId )
        {
            App app = GetAppBlob( depotId );

            if ( app == null )
            {
                return -1;
            }

            return app.CurrentVersion;
        }

        public static List<int> GetDepotIDsForGameserver( string gameName )
        {
            List<int> appIDs = new List<int>();

            App serverAppInfoBlob = GetAppBlob( 4 );

            foreach ( var blobField in serverAppInfoBlob.FileSystems )
            {

                string mountName = blobField.Name;

                if ( mountName.Equals( gameName, StringComparison.OrdinalIgnoreCase ) ||
                     mountName.Equals( gameName + "-win32", StringComparison.OrdinalIgnoreCase ) ||
                     mountName.Equals( gameName + "-linux", StringComparison.OrdinalIgnoreCase ) )
                {
                    appIDs.Add( blobField.AppID );
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
