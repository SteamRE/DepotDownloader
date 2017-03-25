using SteamKit2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    /// <summary>
    /// CDNClientPool provides a pool of CDNClients to CDN endpoints
    /// CDNClients that get re-used will be initialized for the correct depots
    /// </summary>
    class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;

        private Steam3Session steamSession;

        private ConcurrentBag<CDNClient> activeClientPool;
        private ConcurrentDictionary<CDNClient, Tuple<uint, CDNClient.Server>> activeClientAuthed;
        private BlockingCollection<CDNClient.Server> availableServerEndpoints;

        private AutoResetEvent populatePoolEvent;
        private Task monitorTask;

        public CDNClientPool(Steam3Session steamSession)
        {
            this.steamSession = steamSession;

            activeClientPool = new ConcurrentBag<CDNClient>();
            activeClientAuthed = new ConcurrentDictionary<CDNClient, Tuple<uint, CDNClient.Server>>();
            availableServerEndpoints = new BlockingCollection<CDNClient.Server>();

            populatePoolEvent = new AutoResetEvent(true);

            monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
        }

        private async Task<IList<CDNClient.Server>> FetchBootstrapServerListAsync()
        {
            CDNClient bootstrap = new CDNClient(steamSession.steamClient);

            while (true)
            {
                try
                {
                    var cdnServers = await bootstrap.FetchServerListAsync(cellId: (uint)ContentDownloader.Config.CellID).ConfigureAwait(false);
                    if (cdnServers != null)
                    {
                        return cdnServers;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to retrieve content server list: {0}", ex.Message);
                }
            }
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            while(true)
            {
                populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

                // peek ahead into steam session to see if we have servers
                if (availableServerEndpoints.Count < ServerEndpointMinimumSize && 
                    steamSession.steamClient.IsConnected &&
                    steamSession.steamClient.GetServersOfType(EServerType.CS).Count > 0)
                {
                    var servers = await FetchBootstrapServerListAsync();

                    var weightedCdnServers = servers.Select(x =>
                    {
                        int penalty = 0;
                        ConfigStore.TheConfig.ContentServerPenalty.TryGetValue(x.Host, out penalty);

                        return Tuple.Create(x, penalty);
                    }).OrderBy(x => x.Item2).ThenBy(x => x.Item1.WeightedLoad);

                    foreach (var endpoint in weightedCdnServers)
                    {
                        for (var i = 0; i < endpoint.Item1.NumEntries; i++) {
                            availableServerEndpoints.Add(endpoint.Item1);
                        }
                    }
                }
            }
        }

        private void ReleaseConnection(CDNClient client)
        {
            Tuple<uint, CDNClient.Server> authData;
            activeClientAuthed.TryRemove(client, out authData);
        }

        private async Task<CDNClient> BuildConnectionAsync(uint appId, uint depotId, byte[] depotKey, CDNClient.Server serverSeed, CancellationToken token)
        {
            CDNClient.Server server = null;
            CDNClient client = null;

            while (client == null)
            {
                // if we want to re-initialize a specific content server, try that one first
                if (serverSeed != null)
                {
                    server = serverSeed;
                    serverSeed = null;
                }
                else
                {
                    if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
                    {
                        populatePoolEvent.Set();
                    }

                    server = availableServerEndpoints.Take(token);
                }

                client = new CDNClient(steamSession.steamClient, steamSession.AppTickets[depotId]);

                string cdnAuthToken = null;

                try
                {
                    if (server.Type == "CDN")
                    {
                        steamSession.RequestCDNAuthToken(appId, depotId, server.Host);

                        var cdnKey = string.Format("{0:D}:{1}", depotId, server.Host);
                        SteamApps.CDNAuthTokenCallback authTokenCallback;

                        if (steamSession.CDNAuthTokens.TryGetValue(cdnKey, out authTokenCallback))
                        {
                            cdnAuthToken = authTokenCallback.Token;
                        }
                        else
                        {
                            throw new Exception(String.Format("Failed to retrieve CDN token for server {0} depot {1}", server.Host, depotId));
                        }
                    }

                    await client.ConnectAsync(server).ConfigureAwait(false);
                    await client.AuthenticateDepotAsync(depotId, depotKey, cdnAuthToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    client = null;

                    Console.WriteLine("Failed to connect to content server {0}: {1}", server, ex.Message);

                    int penalty = 0;
                    ConfigStore.TheConfig.ContentServerPenalty.TryGetValue(server.Host, out penalty);
                    ConfigStore.TheConfig.ContentServerPenalty[server.Host] = penalty + 1;
                }
            }

            Console.WriteLine("Initialized connection to content server {0} with depot id {1}", server, depotId);

            activeClientAuthed[client] = Tuple.Create(depotId, server);
            return client;
        }

        private async Task<bool> ReauthConnectionAsync(CDNClient client, CDNClient.Server server, uint appId, uint depotId, byte[] depotKey)
        {
            DebugLog.Assert(server.Type == "CDN" || steamSession.AppTickets[depotId] == null, "CDNClientPool", "Re-authing a CDN or anonymous connection");

            String cdnAuthToken = null;

            try
            {
                if (server.Type == "CDN")
                {
                    steamSession.RequestCDNAuthToken(appId, depotId, server.Host);

                    var cdnKey = string.Format("{0:D}:{1}", depotId, server.Host);
                    SteamApps.CDNAuthTokenCallback authTokenCallback;

                    if (steamSession.CDNAuthTokens.TryGetValue(cdnKey, out authTokenCallback))
                    {
                        cdnAuthToken = authTokenCallback.Token;
                    }
                    else
                    {
                        throw new Exception(String.Format("Failed to retrieve CDN token for server {0} depot {1}", server.Host, depotId));
                    }
                }

                await client.AuthenticateDepotAsync(depotId, depotKey, cdnAuthToken).ConfigureAwait(false);
                activeClientAuthed[client] = Tuple.Create(depotId, server);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to reauth to content server {0}: {1}", server, ex.Message);
            }

            return false;
        }

        public async Task<CDNClient> GetConnectionForDepotAsync(uint appId, uint depotId, byte[] depotKey, CancellationToken token)
        {
            CDNClient client = null;

            Tuple<uint, CDNClient.Server> authData;

            activeClientPool.TryTake(out client);

            // if we couldn't find a connection, make one now
            if (client == null)
            {
                client = await BuildConnectionAsync(appId, depotId, depotKey, null, token).ConfigureAwait(false);
            }

            // if we couldn't find the authorization data or it's not authed to this depotid, re-initialize
            if (!activeClientAuthed.TryGetValue(client, out authData) || authData.Item1 != depotId)
            {
                if (authData.Item2.Type == "CDN" && await ReauthConnectionAsync(client, authData.Item2, appId, depotId, depotKey).ConfigureAwait(false))
                {
                    Console.WriteLine("Re-authed CDN connection to content server {0} from {1} to {2}", authData.Item2, authData.Item1, depotId);
                }                
                else if (authData.Item2.Type == "CS" && steamSession.AppTickets[depotId] == null && await ReauthConnectionAsync(client, authData.Item2, appId, depotId, depotKey).ConfigureAwait(false))
                {
                    Console.WriteLine("Re-authed anonymous connection to content server {0} from {1} to {2}", authData.Item2, authData.Item1, depotId);
                }
                else
                {
                    ReleaseConnection(client);
                    client = await BuildConnectionAsync(appId, depotId, depotKey, authData.Item2, token).ConfigureAwait(false);
                }
            }

            return client;
        }
        
        public void ReturnConnection(CDNClient client)
        {
            if (client == null) return;

            activeClientPool.Add(client);
        }

        public void ReturnBrokenConnection(CDNClient client)
        {
            if (client == null) return;

            ReleaseConnection(client);
        }
    }
}
