using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Blob;

namespace DepotDownloader
{
    class CDR
    {
        [BlobField(1)]
        public List<App> Apps;

        [BlobField(2)]
        public List<Sub> Subs;
    }

    class App
    {
        [BlobField(2)]
        public string Name;

        [BlobField(1)]
        public int AppID;

        [BlobField(11)]
        public int CurrentVersion;
        
        [BlobField(10)]
        public List<AppVersion> Versions;
        
        [BlobField(12)]
        public List<FileSystem> FileSystems;

        [BlobField(14)]
        public Dictionary<string, string> UserDefined;

        [BlobField(16)]
        public int BetaVersion;
    }

    class Sub
    {
        [BlobField(1)]
        public int SubID;
 
        [BlobField(6)]
        public List<int> AppIDs;
    }

    class AppVersion
    {
        [BlobField(2)]
        public uint VersionID;

        [BlobField(5)]
        public string DepotEncryptionKey;

        [BlobField(6)]
        public bool IsEncryptionKeyAvailable;
    }

    class FileSystem
    {
        [BlobField(1)]
        public int AppID;

        [BlobField(2)]
        public string Name;

        [BlobField(4)]
        public string Platform;
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

            using(BlobReader reader = BlobReader.CreateFrom(new MemoryStream(cdr)))
            {
                cdrObj = (CDR)BlobTypedReader.Deserialize(reader, typeof(CDR));
            }

            Console.WriteLine( " Done!" );
        }

        static App GetAppBlob( int appID )
        {
            return cdrObj.Apps.Find( app => app.AppID == appID );
        }

        static Sub GetSubBlob( int subID )
        {
            return cdrObj.Subs.Find( sub => sub.SubID == subID );
        }

        public static string GetDepotName( int depotId )
        {
            // Match hardcoded names from hldsupdatetool for certain HL1 depots
            if ( depotId == 1 )
                return "Half-Life Base Content";
            else if ( depotId == 4 )
                return "Linux Server Engine";
            else if ( depotId == 5 )
                return "Win32 Server Engine";

            App app = GetAppBlob( depotId );

            if ( app == null )
            {
                return null;
            }

            return app.Name;
        }

        public static int GetLatestDepotVersion( int depotId, bool beta )
        {
            App app = GetAppBlob( depotId );

            if ( app == null )
            {
                return -1;
            }

            if ( beta && app.BetaVersion > app.CurrentVersion )
                return app.BetaVersion;

            return app.CurrentVersion;
        }

        public static byte[] GetDepotEncryptionKey( int depotId, int version )
        {
            App app = GetAppBlob( depotId );

            if ( app == null )
            {
                return null;
            }

            foreach ( AppVersion ver in app.Versions )
            {
                if ( ver.VersionID == version )
                {
                    if ( ver.IsEncryptionKeyAvailable )
                        return DecodeHexString( ver.DepotEncryptionKey );
                    break;
                }
            }

            return null;
        }

        static byte[] DecodeHexString( string hex )
        {
            if ( hex == null )
                return null;

            int chars = hex.Length;
            byte[] bytes = new byte[ chars / 2 ];

            for ( int i = 0 ; i < chars ; i += 2 )
                bytes[ i / 2 ] = Convert.ToByte( hex.Substring( i, 2 ), 16 );

            return bytes;
        }

        public static List<int> GetDepotIDsForApp( int appId, bool allPlatforms )
        {
            List<int> depotIDs = new List<int>();

            App appInfoBlob = GetAppBlob( appId );

            if ( appInfoBlob == null )
            {
                return null;
            }

            PlatformID platform = Environment.OSVersion.Platform;
            string platformStr = "";

            if ( platform == PlatformID.Win32NT )
                platformStr = "windows";
            else if ( Util.IsMacOSX() )
                platformStr = "macos";

            foreach ( var blobField in appInfoBlob.FileSystems )
            {
                string depotPlatform = blobField.Platform;

                if ( depotPlatform == null ||
                     depotPlatform.Contains( platformStr ) ||
                     allPlatforms )
                {
                    depotIDs.Add( blobField.AppID );
                }
            }

            return depotIDs;
        }

        public static List<int> GetDepotIDsForGameserver( string gameName, bool allPlatforms )
        {
            List<int> appIDs = new List<int>();

            App serverAppInfoBlob = GetAppBlob( 4 );

            PlatformID platform = Environment.OSVersion.Platform;
            bool goldSrc = false;

            if ( gameName.Equals( "valve", StringComparison.OrdinalIgnoreCase ) )
                goldSrc = true;
            else
            {
                string platformSuffix = "";
                int gameLen = gameName.Length;

                if (platform == PlatformID.Win32NT)
                    platformSuffix = "-win32";
                else if (platform == PlatformID.Unix && !Util.IsMacOSX())
                    platformSuffix = "-linux";

                foreach (var blobField in serverAppInfoBlob.FileSystems)
                {
                    string mountName = blobField.Name;

                    if (String.Compare(mountName, 0, gameName, 0, gameLen, true) == 0)
                    {
                        string suffix = mountName.Substring(gameLen);

                        if (suffix == "" ||
                            suffix == platformSuffix ||
                            allPlatforms && (suffix == "-win32" || suffix == "-linux"))
                        {
                            if ( blobField.AppID < 200 )
                                goldSrc = true;

                            appIDs.Add( blobField.AppID );
                        }
                    }
                }

            }

            // For HL1 server installs, this is hardcoded in hldsupdatetool
            if ( goldSrc )
            {
                // Win32 or Linux Server Engine
                if ( allPlatforms )
                {
                    appIDs.Add( 4 );
                    appIDs.Add( 5 );
                }
                else if ( platform == PlatformID.Win32NT )
                    appIDs.Add( 5 );
                else if ( platform == PlatformID.Unix && !Util.IsMacOSX() )
                    appIDs.Add( 4 );

                // Half-Life Base Content
                appIDs.Add( 1 );
            }

            return appIDs;
        }

        public static string GetDedicatedServerFolder( int depotId )
        {
            App app = GetAppBlob( depotId );

            if ( app.UserDefined == null )
                return null;

            foreach ( var entry in app.UserDefined )
            {
                if ( entry.Key.Equals( "dedicatedserverfolder", StringComparison.OrdinalIgnoreCase ) )
                {
                    return entry.Value;
                }
            }

            return null;
        }

        public static void ListGameServers()
        {
            App serverAppInfoBlob = GetAppBlob( 4 );

            List<string> sourceGames = new List<string>();
            List<string> hl1Games = new List<string>();
            List<string> thirdPartyGames = new List<string>();

            // Hardcoded in hldsupdatetool
            hl1Games.Add( "valve" );

            foreach ( var blobField in serverAppInfoBlob.FileSystems )
            {
                int id = blobField.AppID;
                string name = blobField.Name;

                int suffixPos = name.LastIndexOf( "-win32" );

                if ( suffixPos == -1 )
                    suffixPos = name.LastIndexOf( "-linux" );

                if ( suffixPos > 0 )
                    name = name.Remove( suffixPos );

                // These numbers come from hldsupdatetool
                if ( id < 1000 )
                {
                    if ( id < 200 )
                    {
                        if ( !hl1Games.Contains( name ) )
                            hl1Games.Add( name );
                    }
                    else
                    {
                        if ( !sourceGames.Contains( name ) )
                            sourceGames.Add( name );
                    }
                }
                else
                {
                    if ( !thirdPartyGames.Contains( name ) )
                        thirdPartyGames.Add( name );
                }
            }

            sourceGames.Sort( StringComparer.Ordinal );
            hl1Games.Sort( StringComparer.Ordinal );
            thirdPartyGames.Sort( StringComparer.Ordinal );

            Console.WriteLine( "** 'game' options for Source DS install:\n" );
            foreach ( string game in sourceGames )
                Console.WriteLine( "\t\"{0}\"", game );

            Console.WriteLine( "\n** 'game' options for HL1 DS install:\n");
            foreach ( string game in hl1Games )
                Console.WriteLine( "\t\"{0}\"", game );

            Console.WriteLine( "\n** 'game' options for Third-Party game servers:\n" );
            foreach ( string game in thirdPartyGames )
                Console.WriteLine( "\t\"{0}\"", game );
        }

        public static bool SubHasDepot( int subId, int depotId )
        {
            Sub sub = GetSubBlob( subId );

            if ( sub == null )
                return false;

            return sub.AppIDs.Contains( depotId );
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

                return SHAHash( cdr );
            }
            catch
            {
                return null;
            }
        }

        static byte[] SHAHash( byte[] data )
        {
            using ( SHA1Managed sha = new SHA1Managed() )
            {
                byte[] output = sha.ComputeHash( data );

                return output;
            }
        }
    }
}
