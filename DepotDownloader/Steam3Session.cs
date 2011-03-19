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


            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>( SteamUser.NAME );
            this.steamApps = this.steamClient.GetHandler<SteamApps>( SteamApps.NAME );

            this.callbackThread = new Thread( HandleCallbacks );
            this.callbackThread.Start();

            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        public Credentials WaitForCredentials()
        {
            this.callbackThread.Join(); // no timespan as the thread will terminate itself

            return credentials;
        }

        void HandleCallbacks()
        {
            while ( true )
            {
                var callback = steamClient.WaitForCallback( true, TimeSpan.FromSeconds( 1 ) );

                TimeSpan diff = DateTime.Now - connectTime;

                if ( diff > STEAM3_TIMEOUT || ( credentials.HasSessionToken && credentials.AppTicket != null ) )
                    break;

                if ( callback == null )
                    continue;

                if ( callback.IsType<SteamClient.ConnectCallback>() )
                {
                    steamUser.LogOn( logonDetails );
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

                    credentials.AppTicket = msg.Ticket;

                }

                if ( callback.IsType<SteamUser.SessionTokenCallback>() )
                {
                    var msg = callback as SteamUser.SessionTokenCallback;

                    credentials.SessionToken = msg.SessionToken;
                    credentials.HasSessionToken = true;
                }
            }

            steamClient.Disconnect();
        }
    }
}
