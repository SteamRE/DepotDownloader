using SteamKit2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    /// <summary>
    /// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed
    /// </summary>
    class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;

        private readonly Steam3Session steamSession;

        public CDNClient CDNClient { get; }

        private readonly ConcurrentBag<CDNClient.Server> activeConnectionPool;
        private readonly BlockingCollection<CDNClient.Server> availableServerEndpoints;

        private readonly AutoResetEvent populatePoolEvent;
        private readonly Task monitorTask;
        private readonly CancellationTokenSource shutdownToken;
        public CancellationTokenSource ExhaustedToken { get; set; }

        public CDNClientPool(Steam3Session steamSession)
        {
            this.steamSession = steamSession;
            CDNClient = new CDNClient(steamSession.steamClient);

            activeConnectionPool = new ConcurrentBag<CDNClient.Server>();
            availableServerEndpoints = new BlockingCollection<CDNClient.Server>();

            populatePoolEvent = new AutoResetEvent(true);
            shutdownToken = new CancellationTokenSource();

            monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
        }

        public void Shutdown()
        {
            shutdownToken.Cancel();
            monitorTask.Wait();
        }

        private async Task<IReadOnlyCollection<CDNClient.Server>> FetchBootstrapServerListAsync()
        {
            var backoffDelay = 0;

            while (!shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var cdnServers = await ContentServerDirectoryService.LoadAsync(this.steamSession.steamClient.Configuration, ContentDownloader.Config.CellID, shutdownToken.Token);
                    if (cdnServers != null)
                    {
                        return cdnServers;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to retrieve content server list: {0}", ex.Message);

                    if (ex is SteamKitWebRequestException e && e.StatusCode == (HttpStatusCode)429)
                    {
                        // If we're being throttled, add a delay to the next request
                        backoffDelay = Math.Min(5, ++backoffDelay);
                        await Task.Delay(TimeSpan.FromSeconds(backoffDelay));
                    }
                }
            }

            return null;
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            bool didPopulate = false;

            while (!shutdownToken.IsCancellationRequested)
            {
                populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

                // We want the Steam session so we can take the CellID from the session and pass it through to the ContentServer Directory Service
                if (availableServerEndpoints.Count < ServerEndpointMinimumSize && steamSession.steamClient.IsConnected)
                {
                    var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);

                    if (servers == null)
                    {
                        ExhaustedToken?.Cancel();
                        return;
                    }

                    var weightedCdnServers = servers.Select(x =>
                    {
                        AccountSettingsStore.Instance.ContentServerPenalty.TryGetValue(x.Host, out var penalty);

                        return Tuple.Create(x, penalty);
                    }).OrderBy(x => x.Item2).ThenBy(x => x.Item1.WeightedLoad);

                    foreach (var (server, weight) in weightedCdnServers)
                    {
                        for (var i = 0; i < server.NumEntries; i++)
                        {
                            availableServerEndpoints.Add(server);
                        }
                    }

                    didPopulate = true;
                }
                else if (availableServerEndpoints.Count == 0 && !steamSession.steamClient.IsConnected && didPopulate)
                {
                    ExhaustedToken?.Cancel();
                    return;
                }
            }
        }

        private async Task<string> AuthenticateConnection(uint appId, uint depotId, CDNClient.Server server)
        {
            var host = steamSession.ResolveCDNTopLevelHost(server.Host);
            var cdnKey = $"{depotId:D}:{host}";

            steamSession.RequestCDNAuthToken(appId, depotId, host, cdnKey);

            if (steamSession.CDNAuthTokens.TryGetValue(cdnKey, out var authTokenCallbackPromise))
            {
                var result = await authTokenCallbackPromise.Task;
                return result.Token;
            }
            else
            {
                throw new Exception($"Failed to retrieve CDN token for server {server.Host} depot {depotId}");
            }
        }

        private CDNClient.Server BuildConnection(CancellationToken token)
        {
            if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
            {
                populatePoolEvent.Set();
            }

            return availableServerEndpoints.Take(token);
        }

        public async Task<Tuple<CDNClient.Server, string>> GetConnectionForDepot(uint appId, uint depotId, CancellationToken token)
        {
            // Take a free connection from the connection pool
            // If there were no free connections, create a new one from the server list
            if (!activeConnectionPool.TryTake(out var server))
            {
                server = BuildConnection(token);
            }

            // If we don't have a CDN token yet for this server and depot, fetch one now
            var cdnToken = await AuthenticateConnection(appId, depotId, server);

            return Tuple.Create(server, cdnToken);
        }

        public void ReturnConnection(Tuple<CDNClient.Server, string> server)
        {
            if (server == null) return;

            activeConnectionPool.Add(server.Item1);
        }

        public void ReturnBrokenConnection(Tuple<CDNClient.Server, string> server)
        {
            if (server == null) return;

            // Broken connections are not returned to the pool
        }
    }
}
