using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using SteamKit2;
using System.Diagnostics;
using System.Net;

namespace DepotDownloader
{
    
    class Steam3Session
    {
        public class Credentials
        {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }
            public Steam2Ticket Steam2Ticket { get; set; }

            public bool IsValid
            {
                get { return LoggedOn; }// && SessionToken > 0 && Steam2Ticket != null; }
            }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, byte[]> AppTickets { get; private set; }
        public Dictionary<uint, ulong> AppTokens { get; private set; }
        public Dictionary<uint, byte[]> DepotKeys { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; private set; }
        public Dictionary<uint, bool> AppInfoOverridesCDR { get; private set; }

        public SteamClient steamClient;
        public SteamUser steamUser;
        SteamApps steamApps;

        CallbackManager callbacks;

        bool authenticatedUser;
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

            this.authenticatedUser = details.Username != null;
            this.credentials = new Credentials();
            this.bConnected = false;
            this.bAborted = false;

            this.AppTickets = new Dictionary<uint, byte[]>();
            this.AppTokens = new Dictionary<uint, ulong>();
            this.DepotKeys = new Dictionary<uint, byte[]>();
            this.AppInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.PackageInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.AppInfoOverridesCDR = new Dictionary<uint, bool>();

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Register(new Callback<SteamClient.ConnectedCallback>(ConnectedCallback));
            this.callbacks.Register(new Callback<SteamClient.DisconnectedCallback>(DisconnectedCallback));
            this.callbacks.Register(new Callback<SteamUser.LoggedOnCallback>(LogOnCallback));
            this.callbacks.Register(new Callback<SteamUser.SessionTokenCallback>(SessionTokenCallback));
            this.callbacks.Register(new Callback<SteamApps.LicenseListCallback>(LicenseListCallback));
            this.callbacks.Register(new JobCallback<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback));

            Console.Write( "Connecting to Steam3..." );

            if ( authenticatedUser )
            {
                FileInfo fi = new FileInfo(String.Format("{0}.sentryFile", logonDetails.Username));
                if (fi.Exists && fi.Length > 0)
                {
                    logonDetails.SentryFileHash = Util.SHAHash(File.ReadAllBytes(fi.FullName));
                }
            }

            Connect();
        }

        public Credentials WaitForCredentials()
        {
            if (credentials.IsValid || bAborted)
                return credentials;

            do
            {
                WaitForCallbacks();
            }
            while (!bAborted && !credentials.IsValid);

            return credentials;
        }

        public void RequestAppInfo(uint appId)
        {
            if (AppInfo.ContainsKey(appId) || bAborted)
                return;

            Action<SteamApps.PICSTokensCallback, JobID> cbMethodTokens = (appTokens, jobId) =>
            {
                if (appTokens.AppTokensDenied.Contains(appId))
                {
                    Console.WriteLine("Insufficient privileges to get access token for app {0}", appId);
                }

                foreach (var token_dict in appTokens.AppTokens)
                {
                    this.AppTokens.Add(token_dict.Key, token_dict.Value);
                }
            };

            using (JobCallback<SteamApps.PICSTokensCallback> appTokensCallback = new JobCallback<SteamApps.PICSTokensCallback>(cbMethodTokens, callbacks, steamApps.PICSGetAccessTokens(new List<uint>() { appId }, new List<uint>() { })))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!appTokensCallback.Completed && !bAborted);
            }

            Action<SteamApps.PICSProductInfoCallback, JobID> cbMethod = (appInfo, jobId) =>
            {
                Debug.Assert( appInfo.ResponsePending == false );

                foreach (var app_value in appInfo.Apps)
                {
                    var app = app_value.Value;

                    Console.WriteLine("Got AppInfo for {0}", app.ID);
                    AppInfo.Add(app.ID, app);

                    KeyValue depots = ContentDownloader.GetSteam3AppSection((int)app.ID, EAppInfoSection.Depots);
                    if (depots != null)
                    {
                        if (depots["OverridesCDDB"].AsBoolean(false))
                        {
                            AppInfoOverridesCDR[app.ID] = true;
                        }
                    }
                }

                foreach (var app in appInfo.UnknownApps)
                {
                    AppInfo.Add(app, null);
                }
            };

            SteamApps.PICSRequest request = new SteamApps.PICSRequest(appId);
            if (AppTokens.ContainsKey(appId))
            {
                request.AccessToken = AppTokens[appId];
                request.Public = false;
            }

            using (JobCallback<SteamApps.PICSProductInfoCallback> appInfoCallback = new JobCallback<SteamApps.PICSProductInfoCallback>(cbMethod, callbacks, steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest>() { request }, new List<SteamApps.PICSRequest>() { })))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!appInfoCallback.Completed && !bAborted);
            }
        }

        public void RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            List<uint> packages = packageIds.ToList();
            packages.RemoveAll(pid => PackageInfo.ContainsKey(pid));

            if (packages.Count == 0 || bAborted)
                return;

            Action<SteamApps.PICSProductInfoCallback, JobID> cbMethod = (packageInfo, jobId) =>
            {
                Debug.Assert( packageInfo.ResponsePending == false );

                foreach (var package_value in packageInfo.Packages)
                {
                    var package = package_value.Value;
                    PackageInfo.Add(package.ID, package);
                }

                foreach (var package in packageInfo.UnknownPackages)
                {
                    PackageInfo.Add(package, null);
                }
            };

            using (JobCallback<SteamApps.PICSProductInfoCallback> packageInfoCallback = new JobCallback<SteamApps.PICSProductInfoCallback>(cbMethod, callbacks, steamApps.PICSGetProductInfo(new List<uint>(), packages)))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!packageInfoCallback.Completed && !bAborted);
            }
        }

        public void RequestAppTicket(uint appId)
        {
            if (AppTickets.ContainsKey(appId) || bAborted)
                return;


            if ( !authenticatedUser )
            {
                AppTickets[appId] = null;
                return;
            }

            Action<SteamApps.AppOwnershipTicketCallback, JobID> cbMethod = (appTicket, jobId) =>
            {
                if (appTicket.Result != EResult.OK)
                {
                    Console.WriteLine("Unable to get appticket for {0}: {1}", appTicket.AppID, appTicket.Result);
                    Abort();
                }
                else
                {
                    Console.WriteLine("Got appticket for {0}!", appTicket.AppID);
                    AppTickets[appTicket.AppID] = appTicket.Ticket;
                }
            };

            using (JobCallback<SteamApps.AppOwnershipTicketCallback> appTicketCallback = new JobCallback<SteamApps.AppOwnershipTicketCallback>(cbMethod, callbacks, steamApps.GetAppOwnershipTicket(appId)))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!appTicketCallback.Completed && !bAborted);
            }
        }

        public void RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (DepotKeys.ContainsKey(depotId) || bAborted)
                return;

            Action<SteamApps.DepotKeyCallback, JobID> cbMethod = (depotKey, jobId) =>
            {
                Console.WriteLine("Got depot key for {0} result: {1}", depotKey.DepotID, depotKey.Result);

                if (depotKey.Result != EResult.OK)
                {
                    Abort();
                    return;
                }

                DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
            };

            using ( var depotKeyCallback = new JobCallback<SteamApps.DepotKeyCallback>( cbMethod, callbacks, steamApps.GetDepotDecryptionKey( depotId, appid ) ) )
            {
                do
                {
                    WaitForCallbacks();
                }
                while ( !depotKeyCallback.Completed && !bAborted );
            }
        }

        void Connect()
        {
            bAborted = false;
            bConnected = false;
            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff=true)
        {
            Disconnect(sendLogOff);
        }
        public void Disconnect(bool sendLogOff=true)
        {
            if (sendLogOff)
            {
                steamUser.LogOff();
            }
            
            steamClient.Disconnect();
            bConnected = false;
            bAborted = true;
        }


        private void WaitForCallbacks()
        {
            callbacks.RunWaitCallbacks( TimeSpan.FromSeconds(1) );

            TimeSpan diff = DateTime.Now - connectTime;

            if (diff > STEAM3_TIMEOUT && !bConnected)
            {
                Console.WriteLine("Timeout connecting to Steam3.");
                Abort();

                return;
            }
        }

        private void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            Console.WriteLine(" Done!");
            bConnected = true;
            if ( !authenticatedUser )
            {
                Console.Write( "Logging anonymously into Steam3..." );
                steamUser.LogOnAnonymous();
            }
            else
            {
                Console.Write( "Logging '{0}' into Steam3...", logonDetails.Username );
                steamUser.LogOn( logonDetails );
            }
        }

        private static int retry_count = 0;

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            if (!bConnected || bAborted)
                return;

            Console.WriteLine("Reconnecting");

            if ( ++retry_count < 2 )
            {
                steamClient.Connect();
            }
            else
            {
                var addresses = Dns.GetHostAddresses( "cm0.steampowered.com" );
                Random random = new Random();

                var addr = addresses[ random.Next( addresses.Length ) ];

                steamClient.Connect( new IPEndPoint( addr, 27017 /* expose this as a constant someday? */ ) );
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            if (loggedOn.Result == EResult.AccountLogonDenied)
            {
                Console.WriteLine("This account is protected by Steam Guard. Please enter the authentication code sent to your email address.");
                Console.Write("Auth Code: ");

                Abort(false);

                logonDetails.AuthCode = Console.ReadLine();

                Console.Write("Retrying Steam3 connection...");
                Connect();

                return;
            }
            else if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort(false);

                return;
            }
            else if (loggedOn.Result != EResult.OK)
            {
                Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort();
                
                return;
            }

            Console.WriteLine(" Done!");

            credentials.LoggedOn = true;

            if (loggedOn.Steam2Ticket != null)
            {
                Console.WriteLine("Got Steam2 Ticket!");
                credentials.Steam2Ticket = loggedOn.Steam2Ticket;
            }

            if (ContentDownloader.Config.CellID == 0)
            {
                Console.WriteLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
                ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            }
        }

        private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken)
        {
            Console.WriteLine("Got session token!");
            credentials.SessionToken = sessionToken.SessionToken;
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Console.WriteLine("Unable to get license list: {0} ", licenseList.Result);
                Abort();

                return;
            }

            Console.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
            Licenses = licenseList.LicenseList;

            IEnumerable<uint> licenseQuery = Licenses.Select(lic =>
            {
                return lic.PackageID;
            });

            Console.WriteLine("Licenses: {0}", string.Join(", ", licenseQuery));
        }

        private void UpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback machineAuth, JobID jobId)
        {
            byte[] hash = Util.SHAHash(machineAuth.Data);
            Console.WriteLine("Got Machine Auth: {0} {1} {2} {3}", machineAuth.FileName, machineAuth.Offset, machineAuth.BytesToWrite, machineAuth.Data.Length, hash);

            File.WriteAllBytes( String.Format("{0}.sentryFile", logonDetails.Username), machineAuth.Data );
            var authResponse = new SteamUser.MachineAuthDetails
            {
                BytesWritten = machineAuth.BytesToWrite,
                FileName = machineAuth.FileName,
                FileSize = machineAuth.BytesToWrite,
                Offset = machineAuth.Offset,

                SentryFileHash = hash, // should be the sha1 hash of the sentry file we just wrote

                OneTimePassword = machineAuth.OneTimePassword, // not sure on this one yet, since we've had no examples of steam using OTPs

                LastError = 0, // result from win32 GetLastError
                Result = EResult.OK, // if everything went okay, otherwise ~who knows~

                JobID = jobId, // so we respond to the correct server job
            };

            // send off our response
            steamUser.SendMachineAuthResponse( authResponse );
        }


    }
}
