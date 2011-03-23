using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.Threading;

namespace DepotDownloader
{
    
    class Steam3Session
    {
        public class Credentials
        {
            public bool HasSessionToken { get; set; }
            public ulong SessionToken { get; set; }

            public byte[] AppTicket { get; set; }
        }

        SteamClient steamClient;

        SteamUser steamUser;
        SteamApps steamApps;

        Thread callbackThread;
        ManualResetEvent credentialHandle;
        bool bConnected;

        DateTime connectTime;

        // input
        uint depotId;
        SteamUser.LogOnDetails logonDetails;

        // output
        Credentials credentials;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds( 30 );


        public Steam3Session( SteamUser.LogOnDetails details, uint depotId )
        {
            this.depotId = depotId;
            this.logonDetails = details;

            this.credentials = new Credentials();
            this.credentialHandle = new ManualResetEvent( false );
            this.bConnected = false;

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>( SteamUser.NAME );
            this.steamApps = this.steamClient.GetHandler<SteamApps>( SteamApps.NAME );

            this.callbackThread = new Thread( HandleCallbacks );
            this.callbackThread.Start();

            Console.Write( "Connecting to Steam3..." );

            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        public Credentials WaitForCredentials()
        {
            this.credentialHandle.WaitOne();

            return credentials;
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

                if ( credentials.HasSessionToken && credentials.AppTicket != null )
                    break;

                if ( callback == null )
                    continue;

                if ( callback.IsType<SteamClient.ConnectCallback>() )
                {
                    Console.WriteLine( " Done!" );
                    bConnected = true;
                    steamUser.LogOn( logonDetails );

                    SteamID steamId = new SteamID();
                    steamId.SetFromSteam2( logonDetails.ClientTGT.UserID, steamClient.ConnectedUniverse );

                    Console.Write( "Logging '{0}' into Steam3...", steamId.Render() );
                }

                if ( callback.IsType<SteamUser.LogOnCallback>() )
                {
                    var msg = callback as SteamUser.LogOnCallback;

                    if ( msg.Result != EResult.OK )
                    {
                        Console.WriteLine( "Unable to login to Steam3: {0}", msg.Result );
                        steamUser.LogOff();
                        break;
                    }

                    Console.WriteLine( " Done!" );

                    steamApps.GetAppOwnershipTicket( depotId );
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
            }

            credentialHandle.Set();
            
        }

    }
}
