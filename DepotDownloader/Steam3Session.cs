using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.Threading;
using System.Collections.ObjectModel;

namespace DepotDownloader
{
    
    class Steam3Session
    {
        public class Credentials
        {
            public bool HasSessionToken { get; set; }
            public ulong SessionToken { get; set; }

            public byte[] AppTicket { get; set; }

            public byte[] Steam2Ticket { get; set; }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public byte[] DepotKey { get; private set; }

        public ReadOnlyCollection<SteamApps.AppInfoCallback.AppInfo> AppInfo { get; private set; }

        SteamClient steamClient;

        SteamUser steamUser;
        SteamApps steamApps;

        Thread callbackThread;
        ManualResetEvent credentialHandle;
        bool bConnected;
        bool bKeyResponse;

        DateTime connectTime;

        // input
        uint depotId;
        uint appId; // base 
        SteamUser.LogOnDetails logonDetails;

        // output
        Credentials credentials;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds( 30 );


        public Steam3Session( SteamUser.LogOnDetails details, uint depotId, uint appId )
        {
            this.depotId = depotId;
            this.appId = appId;
            this.logonDetails = details;

            this.credentials = new Credentials();
            this.credentialHandle = new ManualResetEvent( false );
            this.bConnected = false;
            this.bKeyResponse = false;

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();

            this.callbackThread = new Thread( HandleCallbacks );
            this.callbackThread.Start();

            Console.Write( "Connecting to Steam3..." );

            Connect();
        }

        public Credentials WaitForCredentials()
        {
            this.credentialHandle.WaitOne();

            return credentials;
        }

        void Connect()
        {
            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        public void Disconnect()
        {
            steamClient.Disconnect();
        }

        void HandleCallbacks()
        {
            while ( true )
            {
                var callback = steamClient.WaitForCallback( true, TimeSpan.FromSeconds( 1 ) );

                TimeSpan diff = DateTime.Now - connectTime;

                if ( diff > STEAM3_TIMEOUT && !bConnected )
                    break;

                if ( credentials.HasSessionToken && credentials.AppTicket != null && Licenses != null && credentials.Steam2Ticket != null && AppInfo != null && bKeyResponse )
                    break;

                if ( callback == null )
                    continue;

                if ( callback.IsType<SteamClient.ConnectCallback>() )
                {
                    Console.WriteLine( " Done!" );
                    bConnected = true;
                    steamUser.LogOn( logonDetails );

                    Console.Write( "Logging '{0}' into Steam3...", logonDetails.Username );
                }

                if ( callback.IsType<SteamUser.LogOnCallback>() )
                {
                    var msg = callback as SteamUser.LogOnCallback;

                    if ( msg.Result == EResult.AccountLogonDenied )
                    {
                        Console.WriteLine( "This account is protected by Steam Guard. Please enter the authentication code sent to your email address." );
                        Console.Write( "Auth Code: " );

                        logonDetails.AuthCode = Console.ReadLine();

                        Console.Write( "Retrying Steam3 connection..." );
                        Connect();
                        continue;
                    }
                    else if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to login to Steam3: {0}", msg.Result );
                        steamUser.LogOff();
                        break;
                    }

                    Console.WriteLine( " Done!" );

                    Console.WriteLine( "Got Steam2 Ticket!" );
                    credentials.Steam2Ticket = msg.Steam2Ticket;

                    steamApps.GetAppInfo( appId );
                    steamApps.GetAppOwnershipTicket( depotId );
                    steamApps.GetDepotDecryptionKey( depotId );
                }

                if ( callback.IsType<SteamApps.AppOwnershipTicketCallback>() )
                {
                    var msg = callback as SteamApps.AppOwnershipTicketCallback;

                    if ( msg.AppID != depotId )
                        continue;

                    if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to get appticket for {0}: {1}", depotId, msg.Result );
                        steamUser.LogOff();
                        break;
                    }

                    Console.WriteLine( "Got appticket for {0}!", depotId );
                    credentials.AppTicket = msg.Ticket;

                }

                if ( callback.IsType<SteamUser.SessionTokenCallback>() )
                {
                    var msg = callback as SteamUser.SessionTokenCallback;

                    Console.WriteLine( "Got session token!" );
                    credentials.SessionToken = msg.SessionToken;
                    credentials.HasSessionToken = true;
                }

                if ( callback.IsType<SteamApps.LicenseListCallback>() )
                {
                    var msg = callback as SteamApps.LicenseListCallback;

                    if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to get license list: {0} ", msg.Result );
                        steamUser.LogOff();
                        break;
                    }

                    Console.WriteLine( "Got {0} licenses for account!", msg.LicenseList.Count );
                    Licenses = msg.LicenseList;
                }

                if ( callback.IsType<SteamApps.AppInfoCallback>() )
                {
                    var msg = callback as SteamApps.AppInfoCallback;

                    if (msg.AppsPending > 0 || msg.Apps.Count == 0 || msg.Apps[0].Status == SteamApps.AppInfoCallback.AppInfo.AppInfoStatus.Unknown)
                    {
                        Console.WriteLine("AppInfo did not contain the requested app id {0}", depotId);
                        steamUser.LogOff();
                        break;
                    }

                    Console.WriteLine("Got AppInfo for {0}", msg.Apps[0].AppID);
                    AppInfo = msg.Apps;
                }

                if (callback.IsType<SteamApps.DepotKeyCallback>())
                {
                    var msg = callback as SteamApps.DepotKeyCallback;

                    DepotKey = msg.DepotKey;

                    Console.WriteLine("Got depot key for {0} result: {1}", msg.DepotID, msg.Result);
                    bKeyResponse = true;
                }
            }

            credentialHandle.Set();
            
        }

    }
}
