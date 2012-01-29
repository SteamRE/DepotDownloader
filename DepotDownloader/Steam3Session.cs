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
            public byte[] Steam2Ticket { get; set; }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, byte[]> AppTickets { get; private set; }
        public Dictionary<uint, byte[]> DepotKeys { get; private set; }
        public Dictionary<uint, SteamApps.AppInfoCallback.App> AppInfo { get; private set; }
        public Dictionary<uint, bool> AppInfoOverridesCDR { get; private set; }

        public SteamClient steamClient;

        SteamUser steamUser;
        SteamApps steamApps;

        bool bConnected;
        bool bAborted;
        DateTime connectTime;

        // input
        SteamUser.LogOnDetails logonDetails;

        // output
        Credentials credentials;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds( 30 );


        public Steam3Session( SteamUser.LogOnDetails details )
        {
            this.logonDetails = details;

            this.credentials = new Credentials();
            this.bConnected = false;
            this.bAborted = false;

            this.AppTickets = new Dictionary<uint, byte[]>();
            this.DepotKeys = new Dictionary<uint, byte[]>();
            this.AppInfo = new Dictionary<uint, SteamApps.AppInfoCallback.App>();
            this.AppInfoOverridesCDR = new Dictionary<uint, bool>();

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();

            Console.Write( "Connecting to Steam3..." );

            Connect();
        }

        public Credentials WaitForCredentials()
        {
            do
            {
                HandleCallbacks();
            }
            while (!bAborted && (credentials.SessionToken == 0 || credentials.Steam2Ticket == null));

            return credentials;
        }

        public void RequestAppInfo(uint appId)
        {
            if (bAborted || AppInfo.ContainsKey(appId))
                return;

            steamApps.GetAppInfo( new uint[] { appId } );

            do
            {
                HandleCallbacks();
            }
            while (!bAborted && !AppInfo.ContainsKey(appId));
        }

        public void RequestAppTicket(uint appId)
        {
            if (bAborted || AppTickets.ContainsKey(appId))
                return;

            steamApps.GetAppOwnershipTicket(appId);

            do
            {
                HandleCallbacks();
            }
            while (!bAborted && !AppTickets.ContainsKey(appId));
        }

        public void RequestDepotKey(uint depotId)
        {
            if (bAborted || DepotKeys.ContainsKey(depotId))
                return;

            steamApps.GetDepotDecryptionKey(depotId);

            do
            {
                HandleCallbacks();
            }
            while (!bAborted && !DepotKeys.ContainsKey(depotId));
        }

        void Connect()
        {
            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort()
        {
            bAborted = true;
            Disconnect();
        }

        public void Disconnect()
        {
            steamUser.LogOff();
            steamClient.Disconnect();
            bConnected = false;
        }

        void HandleCallbacks()
        {
            while ( true )
            {
                var callback = steamClient.WaitForCallback( true, TimeSpan.FromSeconds( 1 ) );

                TimeSpan diff = DateTime.Now - connectTime;

                if (diff > STEAM3_TIMEOUT && !bConnected)
                {
                    Abort();
                    break;
                }

                if ( callback == null )
                    break;

                if ( callback.IsType<SteamClient.ConnectedCallback>() )
                {
                    Console.WriteLine( " Done!" );
                    bConnected = true;
                    steamUser.LogOn( logonDetails );

                    Console.Write( "Logging '{0}' into Steam3...", logonDetails.Username );
                }
                else if ( callback.IsType<SteamUser.LoggedOnCallback>() )
                {
                    var msg = callback as SteamUser.LoggedOnCallback;

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
                        Abort();
                        break;
                    }

                    Console.WriteLine( " Done!" );

                    Console.WriteLine( "Got Steam2 Ticket!" );
                    credentials.Steam2Ticket = msg.Steam2Ticket;

                    if (ContentDownloader.Config.CellID == 0)
                    {
                        Console.WriteLine( "Using Steam3 suggest CellID: " + msg.CellID );
                        ContentDownloader.Config.CellID = (int)msg.CellID;
                    }
                }
                else if (callback.IsType<SteamApps.AppOwnershipTicketCallback>())
                {
                    var msg = callback as SteamApps.AppOwnershipTicketCallback;

                    if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to get appticket for {0}: {1}", msg.AppID, msg.Result );
                        Abort();
                        break;
                    }

                    Console.WriteLine( "Got appticket for {0}!", msg.AppID );
                    AppTickets[msg.AppID] = msg.Ticket;

                }
                else if (callback.IsType<SteamUser.SessionTokenCallback>())
                {
                    var msg = callback as SteamUser.SessionTokenCallback;

                    Console.WriteLine( "Got session token!" );
                    credentials.SessionToken = msg.SessionToken;
                    credentials.HasSessionToken = true;
                }
                else if (callback.IsType<SteamApps.LicenseListCallback>())
                {
                    var msg = callback as SteamApps.LicenseListCallback;

                    if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to get license list: {0} ", msg.Result );
                        Abort();
                        break;
                    }

                    Console.WriteLine( "Got {0} licenses for account!", msg.LicenseList.Count );
                    Licenses = msg.LicenseList;
                }
                else if (callback.IsType<SteamClient.JobCallback<SteamApps.AppInfoCallback>>())
                {
                    var msg = callback as SteamClient.JobCallback<SteamApps.AppInfoCallback>;

                    foreach (var app in msg.Callback.Apps)
                    {
                        Console.WriteLine("Got AppInfo for {0}: {1}", app.AppID, app.Status);
                        AppInfo.Add(app.AppID, app);

                        KeyValue depots;
                        if ( app.Sections.TryGetValue( EAppInfoSection.Depots, out depots ) )
                        {
                            if ( depots[ app.AppID.ToString() ][ "OverridesCDDB" ].AsBoolean( false ) )
                            {
                                AppInfoOverridesCDR[ app.AppID ] = true;
                            }
                        }
                    }
                }
                else if (callback.IsType<SteamApps.DepotKeyCallback>())
                {
                    var msg = callback as SteamApps.DepotKeyCallback;

                    Console.WriteLine("Got depot key for {0} result: {1}", msg.DepotID, msg.Result);
                    DepotKeys[msg.DepotID] = msg.DepotKey;
                }
            }
        }

    }
}
