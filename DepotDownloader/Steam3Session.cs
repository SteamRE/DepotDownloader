using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SteamKit2;
using System.Threading;
using System.Collections.ObjectModel;
using System.IO;

namespace DepotDownloader
{
    
    class Steam3Session
    {
        public class Credentials
        {
            public ulong SessionToken { get; set; }
            public Steam2Ticket Steam2Ticket { get; set; }

            public bool IsValid
            {
                get { return SessionToken > 0 && Steam2Ticket != null; }
            }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, byte[]> AppTickets { get; private set; }
        public Dictionary<uint, byte[]> DepotKeys { get; private set; }
        public Dictionary<uint, SteamApps.AppInfoCallback.App> AppInfo { get; private set; }
        public Dictionary<uint, SteamApps.PackageInfoCallback.Package> PackageInfo { get; private set; }
        public Dictionary<uint, bool> AppInfoOverridesCDR { get; private set; }

        public SteamClient steamClient;

        SteamUser steamUser;
        SteamApps steamApps;

        CallbackManager callbacks;

        bool bConnected;
        bool bAborted;
        DateTime connectTime;

        // input
        SteamUser.LogOnDetails logonDetails;

        // output
        Credentials credentials;

        JobCallback<SteamApps.PackageInfoCallback> packageInfoCallback;

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
            this.PackageInfo = new Dictionary<uint, SteamApps.PackageInfoCallback.Package>();
            this.AppInfoOverridesCDR = new Dictionary<uint, bool>();

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Register(new Callback<SteamClient.ConnectedCallback>(ConnectedCallback));
            this.callbacks.Register(new Callback<SteamUser.LoggedOnCallback>(LogOnCallback));
            this.callbacks.Register(new Callback<SteamUser.SessionTokenCallback>(SessionTokenCallback));
            this.callbacks.Register(new Callback<SteamApps.LicenseListCallback>(LicenseListCallback));
            this.callbacks.Register(new JobCallback<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback));

            Console.Write( "Connecting to Steam3..." );

            FileInfo fi = new FileInfo(String.Format("{0}.sentryFile", logonDetails.Username));
            if(fi.Exists && fi.Length > 0)
            {
                logonDetails.SentryFileHash = Util.SHAHash(File.ReadAllBytes(fi.FullName));
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

            Action<SteamApps.AppInfoCallback, JobID> cbMethod = (appInfo, jobId) =>
            {
                foreach (var app in appInfo.Apps)
                {
                    Console.WriteLine("Got AppInfo for {0}: {1}", app.AppID, app.Status);
                    AppInfo.Add(app.AppID, app);

                    if (app.Status == SteamApps.AppInfoCallback.App.AppInfoStatus.Unknown)
                        continue;

                    KeyValue depots;
                    if (app.Sections.TryGetValue(EAppInfoSection.Depots, out depots))
                    {
                        if (depots[app.AppID.ToString()]["OverridesCDDB"].AsBoolean(false))
                        {
                            AppInfoOverridesCDR[app.AppID] = true;
                        }
                    }
                }
            };

            using (JobCallback<SteamApps.AppInfoCallback> appInfoCallback = new JobCallback<SteamApps.AppInfoCallback>(cbMethod, callbacks, steamApps.GetAppInfo(appId)))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!appInfoCallback.Completed && !bAborted);
            }
        }

        public void RequestPackageInfo(uint packageId)
        {
            if (PackageInfo.ContainsKey(packageId))
                return;

            if (packageInfoCallback != null)
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!packageInfoCallback.Completed && !bAborted);

                if (PackageInfo.ContainsKey(packageId))
                    return;
            }

            using (var singlePackageInfoCallback = new JobCallback<SteamApps.PackageInfoCallback>(PackageInfoCallback, callbacks, steamApps.GetPackageInfo(packageId)))
            {
                do
                {
                    WaitForCallbacks();
                }
                while (!singlePackageInfoCallback.Completed && !bAborted);
            }
        }

        public void RequestAppTicket(uint appId)
        {
            if (AppTickets.ContainsKey(appId) || bAborted)
                return;

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
            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff=true)
        {
            bAborted = true;
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
            steamUser.LogOn(logonDetails);

            Console.Write("Logging '{0}' into Steam3...", logonDetails.Username);
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            if (loggedOn.Result == EResult.AccountLogonDenied)
            {
                Console.WriteLine("This account is protected by Steam Guard. Please enter the authentication code sent to your email address.");
                Console.Write("Auth Code: ");

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

            Console.WriteLine("Got Steam2 Ticket!");
            credentials.Steam2Ticket = loggedOn.Steam2Ticket;

            if (ContentDownloader.Config.CellID == 0)
            {
                Console.WriteLine("Using Steam3 suggest CellID: " + loggedOn.CellID);
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

            packageInfoCallback = new JobCallback<SteamApps.PackageInfoCallback>(PackageInfoCallback, callbacks, steamApps.GetPackageInfo(licenseQuery));
        }

        private void PackageInfoCallback(SteamApps.PackageInfoCallback packageInfo, JobID jobid)
        {
            foreach (var package in packageInfo.Packages)
            {
                PackageInfo[package.PackageID] = package;
            }
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
