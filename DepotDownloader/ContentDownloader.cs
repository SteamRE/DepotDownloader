using SteamKit2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    public class ContentDownloaderException : System.Exception
    {
        public ContentDownloaderException( String value ) : base( value ) {}
    }

    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "Public";

        public static DownloadConfig Config = new DownloadConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine( CONFIG_DIR, "staging" );

        private sealed class DepotDownloadInfo
        {
            public uint id { get; private set; }
            public string installDir { get; private set; }
            public string contentName { get; private set; }

            public ulong manifestId { get; private set; }
            public byte[] depotKey;

            public DepotDownloadInfo( uint depotid, ulong manifestId, string installDir, string contentName )
            {
                this.id = depotid;
                this.manifestId = manifestId;
                this.installDir = installDir;
                this.contentName = contentName;
            }
        }

        static bool CreateDirectories( uint depotId, uint depotVersion, out string installDir )
        {
            installDir = null;
            try
            {
                if ( string.IsNullOrWhiteSpace( ContentDownloader.Config.InstallDirectory ) )
                {
                    Directory.CreateDirectory( DEFAULT_DOWNLOAD_DIR );

                    string depotPath = Path.Combine( DEFAULT_DOWNLOAD_DIR, depotId.ToString() );
                    Directory.CreateDirectory( depotPath );

                    installDir = Path.Combine( depotPath, depotVersion.ToString() );
                    Directory.CreateDirectory( installDir );

                    Directory.CreateDirectory( Path.Combine( installDir, CONFIG_DIR ) );
                    Directory.CreateDirectory( Path.Combine( installDir, STAGING_DIR ) );
                }
                else
                {
                    Directory.CreateDirectory( ContentDownloader.Config.InstallDirectory );

                    installDir = ContentDownloader.Config.InstallDirectory;

                    Directory.CreateDirectory( Path.Combine( installDir, CONFIG_DIR ) );
                    Directory.CreateDirectory( Path.Combine( installDir, STAGING_DIR ) );
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static bool TestIsFileIncluded( string filename )
        {
            if ( !Config.UsingFileList )
                return true;

            foreach ( string fileListEntry in Config.FilesToDownload )
            {
                if ( fileListEntry.Equals( filename, StringComparison.OrdinalIgnoreCase ) )
                    return true;
            }

            foreach ( Regex rgx in Config.FilesToDownloadRegex )
            {
                Match m = rgx.Match( filename );

                if ( m.Success )
                    return true;
            }

            return false;
        }

        static bool AccountHasAccess( uint depotId )
        {
            if ( steam3 == null || steam3.steamUser.SteamID == null || ( steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser ) )
                return false;

            IEnumerable<uint> licenseQuery;
            if ( steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser )
            {
                licenseQuery = new List<uint>() { 17906 };
            }
            else
            {
                licenseQuery = steam3.Licenses.Select( x => x.PackageID ).Distinct();
            }

            steam3.RequestPackageInfo( licenseQuery );

            foreach ( var license in licenseQuery )
            {
                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if ( steam3.PackageInfo.TryGetValue( license, out package ) && package != null )
                {
                    if ( package.KeyValues[ "appids" ].Children.Any( child => child.AsUnsignedInteger() == depotId ) )
                        return true;

                    if ( package.KeyValues[ "depotids" ].Children.Any( child => child.AsUnsignedInteger() == depotId ) )
                        return true;
                }
            }

            return false;
        }

        internal static KeyValue GetSteam3AppSection( uint appId, EAppInfoSection section )
        {
            if ( steam3 == null || steam3.AppInfo == null )
            {
                return null;
            }

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if ( !steam3.AppInfo.TryGetValue( appId, out app ) || app == null )
            {
                return null;
            }

            KeyValue appinfo = app.KeyValues;
            string section_key;

            switch ( section )
            {
                case EAppInfoSection.Common:
                    section_key = "common";
                    break;
                case EAppInfoSection.Extended:
                    section_key = "extended";
                    break;
                case EAppInfoSection.Config:
                    section_key = "config";
                    break;
                case EAppInfoSection.Depots:
                    section_key = "depots";
                    break;
                default:
                    throw new NotImplementedException();
            }

            KeyValue section_kv = appinfo.Children.Where( c => c.Name == section_key ).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber( uint appId, string branch )
        {
            if ( appId == INVALID_APP_ID )
                return 0;


            KeyValue depots = ContentDownloader.GetSteam3AppSection( appId, EAppInfoSection.Depots );
            KeyValue branches = depots[ "branches" ];
            KeyValue node = branches[ branch ];

            if ( node == KeyValue.Invalid )
                return 0;

            KeyValue buildid = node[ "buildid" ];

            if ( buildid == KeyValue.Invalid )
                return 0;

            return uint.Parse( buildid.Value );
        }

        static ulong GetSteam3DepotManifest( uint depotId, uint appId, string branch )
        {
            KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );
            KeyValue depotChild = depots[ depotId.ToString() ];

            if ( depotChild == KeyValue.Invalid )
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
            if ( depotChild[ "manifests" ] == KeyValue.Invalid && depotChild[ "depotfromapp" ] != KeyValue.Invalid )
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if ( otherAppId == appId )
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    Console.WriteLine( "App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId );
                    return INVALID_MANIFEST_ID;
                }

                steam3.RequestAppInfo( otherAppId );

                return GetSteam3DepotManifest( depotId, otherAppId, branch );
            }

            var manifests = depotChild[ "manifests" ];
            var manifests_encrypted = depotChild[ "encryptedmanifests" ];

            if ( manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0 )
                return INVALID_MANIFEST_ID;

            var node = manifests[ branch ];

            if ( branch != "Public" && node == KeyValue.Invalid )
            {
                var node_encrypted = manifests_encrypted[ branch ];
                if ( node_encrypted != KeyValue.Invalid )
                {
                    string password = Config.BetaPassword;
                    if ( password == null )
                    {
                        Console.Write( "Please enter the password for branch {0}: ", branch );
                        Config.BetaPassword = password = Console.ReadLine();
                    }

                    var encrypted_v1 = node_encrypted[ "encrypted_gid" ];
                    var encrypted_v2 = node_encrypted[ "encrypted_gid_2" ];

                    if ( encrypted_v1 != KeyValue.Invalid )
                    {
                        byte[] input = Util.DecodeHexString( encrypted_v1.Value );
                        byte[] manifest_bytes = CryptoHelper.VerifyAndDecryptPassword( input, password );

                        if ( manifest_bytes == null )
                        {
                            Console.WriteLine( "Password was invalid for branch {0}", branch );
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64( manifest_bytes, 0 );
                    }
                    else if ( encrypted_v2 != KeyValue.Invalid )
                    {
                        // Submit the password to Steam now to get encryption keys
                        steam3.CheckAppBetaPassword( appId, Config.BetaPassword );

                        if ( !steam3.AppBetaPasswords.ContainsKey( branch ) )
                        {
                            Console.WriteLine( "Password was invalid for branch {0}", branch );
                            return INVALID_MANIFEST_ID;
                        }

                        byte[] input = Util.DecodeHexString( encrypted_v2.Value );
                        byte[] manifest_bytes;
                        try
                        {
                            manifest_bytes = CryptoHelper.SymmetricDecryptECB( input, steam3.AppBetaPasswords[ branch ] );
                        }
                        catch ( Exception e )
                        {
                            Console.WriteLine( "Failed to decrypt branch {0}: {1}", branch, e.Message );
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64( manifest_bytes, 0 );
                    }
                    else
                    {
                        Console.WriteLine( "Unhandled depot encryption for depotId {0}", depotId );
                        return INVALID_MANIFEST_ID;
                    }

                }

                return INVALID_MANIFEST_ID;
            }

            if ( node.Value == null )
                return INVALID_MANIFEST_ID;

            return UInt64.Parse( node.Value );
        }

        static string GetAppOrDepotName( uint depotId, uint appId )
        {
            if ( depotId == INVALID_DEPOT_ID )
            {
                KeyValue info = GetSteam3AppSection( appId, EAppInfoSection.Common );

                if ( info == null )
                    return String.Empty;

                return info[ "name" ].AsString();
            }
            else
            {
                KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );

                if ( depots == null )
                    return String.Empty;

                KeyValue depotChild = depots[ depotId.ToString() ];

                if ( depotChild == null )
                    return String.Empty;

                return depotChild[ "name" ].AsString();
            }
        }

        public static bool InitializeSteam3( string username, string password )
        {
            string loginKey = null;

            if ( username != null && Config.RememberPassword )
            {
                _ = AccountSettingsStore.Instance.LoginKeys.TryGetValue( username, out loginKey );
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = loginKey == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    LoginKey = loginKey,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            steam3Credentials = steam3.WaitForCredentials();

            if ( !steam3Credentials.IsValid )
            {
                Console.WriteLine( "Unable to get steam3 credentials." );
                return false;
            }

            cdnPool = new CDNClientPool( steam3 );
            return true;
        }

        public static void ShutdownSteam3()
        {
            if (cdnPool != null)
            {
                cdnPool.Shutdown();
                cdnPool = null;
            }

            if ( steam3 == null )
                return;

            steam3.TryWaitForLoginKey();
            steam3.Disconnect();
        }

        public static async Task DownloadPubfileAsync( uint appId, ulong publishedFileId )
        {
            var details = steam3.GetPubfileItemInfo( appId, publishedFileId );

            if ( details?.manifest_id > 0 )
            {
                await DownloadAppAsync( appId, appId, details.manifest_id, DEFAULT_BRANCH, null, null, null, false, true );
            }
            else
            {
                Console.WriteLine( "Unable to locate manifest ID for published file {0}", publishedFileId );
            }
        }

        public static async Task DownloadAppAsync( uint appId, uint depotId, ulong manifestId, string branch, string os, string arch, string language, bool lv, bool isUgc )
        {
            // Load our configuration data containing the depots currently installed
            string configPath = ContentDownloader.Config.InstallDirectory;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                configPath = DEFAULT_DOWNLOAD_DIR;
            }

            Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
            DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));

            if ( steam3 != null )
                steam3.RequestAppInfo( appId );

            if ( !AccountHasAccess( appId ) )
            {
                if ( steam3.RequestFreeAppLicense( appId ) )
                {
                    Console.WriteLine( "Obtained FreeOnDemand license for app {0}", appId );
                }
                else
                {
                    string contentName = GetAppOrDepotName( INVALID_DEPOT_ID, appId );
                    throw new ContentDownloaderException( String.Format( "App {0} ({1}) is not available from this account.", appId, contentName ) );
                }
            }

            var depotIDs = new List<uint>();
            KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );

            if ( isUgc )
            {
                var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                if (workshopDepot != 0)
                    depotId = workshopDepot;

                depotIDs.Add( depotId );
            }
            else
            {
                Console.WriteLine( "Using app branch: '{0}'.", branch );

                if ( depots != null )
                {
                    foreach ( var depotSection in depots.Children )
                    {
                        uint id = INVALID_DEPOT_ID;
                        if ( depotSection.Children.Count == 0 )
                            continue;

                        if ( !uint.TryParse( depotSection.Name, out id ) )
                            continue;

                        if ( depotId != INVALID_DEPOT_ID && id != depotId )
                            continue;

                        if ( depotId == INVALID_DEPOT_ID )
                        {
                            var depotConfig = depotSection[ "config" ];
                            if ( depotConfig != KeyValue.Invalid )
                            {
                                if ( !Config.DownloadAllPlatforms &&
                                    depotConfig["oslist"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace( depotConfig["oslist"].Value ) )
                                {
                                    var oslist = depotConfig["oslist"].Value.Split( ',' );
                                    if ( Array.IndexOf( oslist, os ?? Util.GetSteamOS() ) == -1 )
                                        continue;
                                }

                                if ( depotConfig["osarch"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace( depotConfig["osarch"].Value ) )
                                {
                                    var depotArch = depotConfig["osarch"].Value;
                                    if ( depotArch != ( arch ?? Util.GetSteamArch() ) )
                                        continue;
                                }

                                if ( !Config.DownloadAllLanguages &&
                                    depotConfig["language"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace( depotConfig["language"].Value ) )
                                {
                                    var depotLang = depotConfig["language"].Value;
                                    if ( depotLang != ( language ?? "english" ) )
                                        continue;
                                }

                                if ( !lv &&
                                    depotConfig["lowviolence"] != KeyValue.Invalid &&
                                    depotConfig["lowviolence"].AsBoolean() )
                                    continue;
                            }
                        }

                        depotIDs.Add( id );
                    }
                }
                if ( depotIDs == null || ( depotIDs.Count == 0 && depotId == INVALID_DEPOT_ID ) )
                {
                    throw new ContentDownloaderException( String.Format( "Couldn't find any depots to download for app {0}", appId ) );
                }
                else if ( depotIDs.Count == 0 )
                {
                    throw new ContentDownloaderException( String.Format( "Depot {0} not listed for app {1}", depotId, appId ) );
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach ( var depot in depotIDs )
            {
                var info = GetDepotInfo( depot, appId, manifestId, branch );
                if ( info != null )
                {
                    infos.Add( info );
                }
            }

            try
            {
                await DownloadSteam3Async( appId, infos ).ConfigureAwait( false );
            }
            catch ( OperationCanceledException )
            {
                Console.WriteLine( "App {0} was not completely downloaded.", appId );
                throw;
            }
        }

        static DepotDownloadInfo GetDepotInfo( uint depotId, uint appId, ulong manifestId, string branch )
        {
            if ( steam3 != null && appId != INVALID_APP_ID )
                steam3.RequestAppInfo( ( uint )appId );

            string contentName = GetAppOrDepotName( depotId, appId );

            if ( !AccountHasAccess( depotId ) )
            {
                Console.WriteLine( "Depot {0} ({1}) is not available from this account.", depotId, contentName );

                return null;
            }

            // Skip requesting an app ticket
            steam3.AppTickets[ depotId ] = null;

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && branch != "public")
                {
                    Console.WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying public branch.", depotId, branch);
                    branch = "public";
                    manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    Console.WriteLine("Depot {0} ({1}) missing public subsection or manifest section.", depotId, contentName);
                    return null;
                }
            }

            uint uVersion = GetSteam3AppBuildNumber( appId, branch );

            string installDir;
            if ( !CreateDirectories( depotId, uVersion, out installDir ) )
            {
                Console.WriteLine( "Error: Unable to create install directories!" );
                return null;
            }

            steam3.RequestDepotKey( depotId, appId );
            if ( !steam3.DepotKeys.ContainsKey( depotId ) )
            {
                Console.WriteLine( "No valid depot key for {0}, unable to download.", depotId );
                return null;
            }

            byte[] depotKey = steam3.DepotKeys[ depotId ];

            var info = new DepotDownloadInfo( depotId, manifestId, installDir, contentName );
            info.depotKey = depotKey;
            return info;
        }

        private class ChunkMatch
        {
            public ChunkMatch( ProtoManifest.ChunkData oldChunk, ProtoManifest.ChunkData newChunk )
            {
                OldChunk = oldChunk;
                NewChunk = newChunk;
            }
            public ProtoManifest.ChunkData OldChunk { get; private set; }
            public ProtoManifest.ChunkData NewChunk { get; private set; }
        }

        private static async Task DownloadSteam3Async( uint appId, List<DepotDownloadInfo> depots )
        {
            ulong TotalBytesCompressed = 0;
            ulong TotalBytesUncompressed = 0;

            foreach ( var depot in depots )
            {
                ulong DepotBytesCompressed = 0;
                ulong DepotBytesUncompressed = 0;

                Console.WriteLine( "Downloading depot {0} - {1}", depot.id, depot.contentName );

                CancellationTokenSource cts = new CancellationTokenSource();
                cdnPool.ExhaustedToken = cts;

                ProtoManifest oldProtoManifest = null;
                ProtoManifest newProtoManifest = null;
                string configDir = Path.Combine( depot.installDir, CONFIG_DIR );

                ulong lastManifestId = INVALID_MANIFEST_ID;
                DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue( depot.id, out lastManifestId );

                // In case we have an early exit, this will force equiv of verifyall next run.
                DepotConfigStore.Instance.InstalledManifestIDs[ depot.id ] = INVALID_MANIFEST_ID;
                DepotConfigStore.Save();

                if ( lastManifestId != INVALID_MANIFEST_ID )
                {
                    var oldManifestFileName = Path.Combine( configDir, string.Format( "{0}.bin", lastManifestId ) );

                    if (File.Exists(oldManifestFileName))
                    {
                        byte[] expectedChecksum, currentChecksum;

                        try
                        {
                            expectedChecksum = File.ReadAllBytes(oldManifestFileName + ".sha");
                        }
                        catch (IOException)
                        {
                            expectedChecksum = null;
                        }

                        oldProtoManifest = ProtoManifest.LoadFromFile(oldManifestFileName, out currentChecksum);

                        if (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum))
                        {
                            // We only have to show this warning if the old manifest ID was different
                            if (lastManifestId != depot.manifestId)
                                Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", lastManifestId);
                            oldProtoManifest = null;
                        }
                    }
                }

                if ( lastManifestId == depot.manifestId && oldProtoManifest != null )
                {
                    newProtoManifest = oldProtoManifest;
                    Console.WriteLine( "Already have manifest {0} for depot {1}.", depot.manifestId, depot.id );
                }
                else
                {
                    var newManifestFileName = Path.Combine( configDir, string.Format( "{0}.bin", depot.manifestId ) );
                    if ( newManifestFileName != null )
                    {
                        byte[] expectedChecksum, currentChecksum;

                        try
                        {
                            expectedChecksum = File.ReadAllBytes(newManifestFileName + ".sha");
                        }
                        catch (IOException)
                        {
                            expectedChecksum = null;
                        }

                        newProtoManifest = ProtoManifest.LoadFromFile(newManifestFileName, out currentChecksum);

                        if (newProtoManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
                        {
                            Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", depot.manifestId);
                            newProtoManifest = null;
                        }
                    }

                    if ( newProtoManifest != null )
                    {
                        Console.WriteLine( "Already have manifest {0} for depot {1}.", depot.manifestId, depot.id );
                    }
                    else
                    {
                        Console.Write( "Downloading depot manifest..." );

                        DepotManifest depotManifest = null;

                        while ( depotManifest == null )
                        {
                            Tuple<CDNClient.Server, string> connection = null;
                            try
                            {
                                connection = await cdnPool.GetConnectionForDepot( appId, depot.id, CancellationToken.None );

                                depotManifest = await cdnPool.CDNClient.DownloadManifestAsync( depot.id, depot.manifestId,
                                    connection.Item1, connection.Item2, depot.depotKey ).ConfigureAwait(false);

                                cdnPool.ReturnConnection( connection );
                            }
                            catch ( SteamKitWebRequestException e )
                            {
                                cdnPool.ReturnBrokenConnection( connection );

                                if ( e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden )
                                {
                                    Console.WriteLine( "Encountered 401 for depot manifest {0} {1}. Aborting.", depot.id, depot.manifestId );
                                    break;
                                }
                                else
                                {
                                    Console.WriteLine( "Encountered error downloading depot manifest {0} {1}: {2}", depot.id, depot.manifestId, e.StatusCode );
                                }
                            }
                            catch ( Exception e )
                            {
                                cdnPool.ReturnBrokenConnection( connection );
                                Console.WriteLine( "Encountered error downloading manifest for depot {0} {1}: {2}", depot.id, depot.manifestId, e.Message );
                            }
                        }

                        if ( depotManifest == null )
                        {
                            Console.WriteLine( "\nUnable to download manifest {0} for depot {1}", depot.manifestId, depot.id );
                            return;
                        }

                        byte[] checksum;

                        newProtoManifest = new ProtoManifest( depotManifest, depot.manifestId );
                        newProtoManifest.SaveToFile( newManifestFileName, out checksum );
                        File.WriteAllBytes( newManifestFileName + ".sha", checksum );

                        Console.WriteLine( " Done!" );
                    }
                }

                newProtoManifest.Files.Sort( ( x, y ) => string.Compare( x.FileName, y.FileName, StringComparison.Ordinal ) );

                if ( Config.DownloadManifestOnly )
                {
                    StringBuilder manifestBuilder = new StringBuilder();
                    string txtManifest = Path.Combine( depot.installDir, string.Format( "manifest_{0}.txt", depot.id ) );

                    foreach ( var file in newProtoManifest.Files )
                    {
                        if ( file.Flags.HasFlag( EDepotFileFlag.Directory ) )
                            continue;

                        manifestBuilder.Append( string.Format( "{0}\n", file.FileName ) );
                    }

                    File.WriteAllText( txtManifest, manifestBuilder.ToString() );
                    continue;
                }

                ulong complete_download_size = 0;
                ulong size_downloaded = 0;
                string stagingDir = Path.Combine( depot.installDir, STAGING_DIR );

                var filesAfterExclusions = newProtoManifest.Files.AsParallel().Where( f => TestIsFileIncluded( f.FileName ) ).ToList();

                // Pre-process
                filesAfterExclusions.ForEach( file =>
                {
                    var fileFinalPath = Path.Combine( depot.installDir, file.FileName );
                    var fileStagingPath = Path.Combine( stagingDir, file.FileName );

                    if ( file.Flags.HasFlag( EDepotFileFlag.Directory ) )
                    {
                        Directory.CreateDirectory( fileFinalPath );
                        Directory.CreateDirectory( fileStagingPath );
                    }
                    else
                    {
                        // Some manifests don't explicitly include all necessary directories
                        Directory.CreateDirectory( Path.GetDirectoryName( fileFinalPath ) );
                        Directory.CreateDirectory( Path.GetDirectoryName( fileStagingPath ) );

                        complete_download_size += file.TotalSize;
                    }
                } );

                var semaphore = new SemaphoreSlim( Config.MaxDownloads );
                var files = filesAfterExclusions.Where( f => !f.Flags.HasFlag( EDepotFileFlag.Directory ) ).ToArray();
                var tasks = new Task[ files.Length ];
                for ( var i = 0; i < files.Length; i++ )
                {
                    var file = files[ i ];
                    var task = Task.Run( async () =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        
                        try
                        {
                            await semaphore.WaitAsync().ConfigureAwait( false );
                            cts.Token.ThrowIfCancellationRequested();

                            string fileFinalPath = Path.Combine( depot.installDir, file.FileName );
                            string fileStagingPath = Path.Combine( stagingDir, file.FileName );

                            // This may still exist if the previous run exited before cleanup
                            if ( File.Exists( fileStagingPath ) )
                            {
                                File.Delete( fileStagingPath );
                            }

                            FileStream fs = null;
                            List<ProtoManifest.ChunkData> neededChunks;
                            FileInfo fi = new FileInfo( fileFinalPath );
                            if ( !fi.Exists )
                            {
                                // create new file. need all chunks
                                fs = File.Create( fileFinalPath );
                                fs.SetLength( ( long )file.TotalSize );
                                neededChunks = new List<ProtoManifest.ChunkData>( file.Chunks );
                            }
                            else
                            {
                                // open existing
                                ProtoManifest.FileData oldManifestFile = null;
                                if ( oldProtoManifest != null )
                                {
                                    oldManifestFile = oldProtoManifest.Files.SingleOrDefault( f => f.FileName == file.FileName );
                                }

                                if ( oldManifestFile != null )
                                {
                                    neededChunks = new List<ProtoManifest.ChunkData>();

                                    if ( Config.VerifyAll || !oldManifestFile.FileHash.SequenceEqual( file.FileHash ) )
                                    {
                                        // we have a version of this file, but it doesn't fully match what we want

                                        var matchingChunks = new List<ChunkMatch>();

                                        foreach ( var chunk in file.Chunks )
                                        {
                                            var oldChunk = oldManifestFile.Chunks.FirstOrDefault( c => c.ChunkID.SequenceEqual( chunk.ChunkID ) );
                                            if ( oldChunk != null )
                                            {
                                                matchingChunks.Add( new ChunkMatch( oldChunk, chunk ) );
                                            }
                                            else
                                            {
                                                neededChunks.Add( chunk );
                                            }
                                        }

                                        File.Move( fileFinalPath, fileStagingPath );

                                        fs = File.Open( fileFinalPath, FileMode.Create );
                                        fs.SetLength( ( long )file.TotalSize );

                                        using ( var fsOld = File.Open( fileStagingPath, FileMode.Open ) )
                                        {
                                            foreach ( var match in matchingChunks )
                                            {
                                                fsOld.Seek( ( long )match.OldChunk.Offset, SeekOrigin.Begin );

                                                byte[] tmp = new byte[ match.OldChunk.UncompressedLength ];
                                                fsOld.Read( tmp, 0, tmp.Length );

                                                byte[] adler = Util.AdlerHash( tmp );
                                                if ( !adler.SequenceEqual( match.OldChunk.Checksum ) )
                                                {
                                                    neededChunks.Add( match.NewChunk );
                                                }
                                                else
                                                {
                                                    fs.Seek( ( long )match.NewChunk.Offset, SeekOrigin.Begin );
                                                    fs.Write( tmp, 0, tmp.Length );
                                                }
                                            }
                                        }

                                        File.Delete( fileStagingPath );
                                    }
                                }
                                else
                                {
                                    // No old manifest or file not in old manifest. We must validate.

                                    fs = File.Open( fileFinalPath, FileMode.Open );
                                    if ( ( ulong )fi.Length != file.TotalSize )
                                    {
                                        fs.SetLength( ( long )file.TotalSize );
                                    }

                                    neededChunks = Util.ValidateSteam3FileChecksums( fs, file.Chunks.OrderBy( x => x.Offset ).ToArray() );
                                }

                                if ( neededChunks.Count() == 0 )
                                {
                                    size_downloaded += file.TotalSize;
                                    Console.WriteLine( "{0,6:#00.00}% {1}", ( ( float )size_downloaded / ( float )complete_download_size ) * 100.0f, fileFinalPath );
                                    if ( fs != null )
                                        fs.Dispose();
                                    return;
                                }
                                else
                                {
                                    size_downloaded += ( file.TotalSize - ( ulong )neededChunks.Select( x => ( long )x.UncompressedLength ).Sum() );
                                }
                            }

                            foreach ( var chunk in neededChunks )
                            {
                                if ( cts.IsCancellationRequested ) break;

                                string chunkID = Util.EncodeHexString( chunk.ChunkID );
                                CDNClient.DepotChunk chunkData = null;

                                while ( !cts.IsCancellationRequested )
                                {
                                    Tuple<CDNClient.Server, string> connection;
                                    try
                                    {
                                        connection = await cdnPool.GetConnectionForDepot( appId, depot.id, cts.Token );
                                    }
                                    catch ( OperationCanceledException )
                                    {
                                        break;
                                    }

                                    DepotManifest.ChunkData data = new DepotManifest.ChunkData();
                                    data.ChunkID = chunk.ChunkID;
                                    data.Checksum = chunk.Checksum;
                                    data.Offset = chunk.Offset;
                                    data.CompressedLength = chunk.CompressedLength;
                                    data.UncompressedLength = chunk.UncompressedLength;

                                    try
                                    {
                                        chunkData = await cdnPool.CDNClient.DownloadDepotChunkAsync( depot.id, data, 
                                            connection.Item1, connection.Item2, depot.depotKey ).ConfigureAwait( false );
                                        cdnPool.ReturnConnection( connection );
                                        break;
                                    }
                                    catch ( SteamKitWebRequestException e )
                                    {
                                        cdnPool.ReturnBrokenConnection( connection );

                                        if ( e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden )
                                        {
                                            Console.WriteLine( "Encountered 401 for chunk {0}. Aborting.", chunkID );
                                            cts.Cancel();
                                            break;
                                        }
                                        else
                                        {
                                            Console.WriteLine( "Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode );
                                        }
                                    }
                                    catch ( Exception e )
                                    {
                                        cdnPool.ReturnBrokenConnection( connection );
                                        Console.WriteLine( "Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message );
                                    }
                                }

                                if ( chunkData == null )
                                {
                                    Console.WriteLine( "Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.id );
                                    cts.Cancel();
                                }

                                // Throw the cancellation exception if requested so that this task is marked failed
                                cts.Token.ThrowIfCancellationRequested();

                                TotalBytesCompressed += chunk.CompressedLength;
                                DepotBytesCompressed += chunk.CompressedLength;
                                TotalBytesUncompressed += chunk.UncompressedLength;
                                DepotBytesUncompressed += chunk.UncompressedLength;

                                fs.Seek( ( long )chunk.Offset, SeekOrigin.Begin );
                                fs.Write( chunkData.Data, 0, chunkData.Data.Length );

                                size_downloaded += chunk.UncompressedLength;
                            }

                            fs.Dispose();

                            Console.WriteLine( "{0,6:#00.00}% {1}", ( ( float )size_downloaded / ( float )complete_download_size ) * 100.0f, fileFinalPath );
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    } );

                    tasks[ i ] = task;
                }

                await Task.WhenAll( tasks ).ConfigureAwait( false );

                DepotConfigStore.Instance.InstalledManifestIDs[ depot.id ] = depot.manifestId;
                DepotConfigStore.Save();

                Console.WriteLine( "Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.id, DepotBytesCompressed, DepotBytesUncompressed );
            }

            Console.WriteLine( "Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots", TotalBytesCompressed, TotalBytesUncompressed, depots.Count );
        }
    }
}
