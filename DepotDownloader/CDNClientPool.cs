using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.CDN;

namespace DepotDownloader
{
    /// <summary>
    /// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed
    /// </summary>
    class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;

        private readonly Steam3Session steamSession;
        private readonly uint appId;
        public Client CDNClient { get; }
        public Server ProxyServer { get; private set; }

        private readonly ConcurrentStack<Server> activeConnectionPool = new ConcurrentStack<Server>();
        private readonly BlockingCollection<Server> availableServerEndpoints = new BlockingCollection<Server>();

        private readonly AutoResetEvent populatePoolEvent = new AutoResetEvent(true);
        private readonly Task monitorTask;
        private readonly CancellationTokenSource shutdownToken = new CancellationTokenSource();
        public CancellationTokenSource ExhaustedToken { get; set; }

        public CDNClientPool(Steam3Session steamSession, uint appId)
        {
            this.steamSession = steamSession;
            this.appId = appId;
            CDNClient = new Client(steamSession.steamClient);

            monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync, TaskCreationOptions.LongRunning);
        }

        public void Shutdown()
        {
            shutdownToken.Cancel();
            monitorTask.Wait();
        }

        private async Task<IReadOnlyCollection<Server>> FetchBootstrapServerListAsync()
        {
            try
            {
                var cdnServers = await steamSession.steamContent.GetServersForSteamPipe().ConfigureAwait(false);
                return cdnServers;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to retrieve content server list: {0}", ex.Message);
                return null;
            }
        }

        private async Task<IPAddress> ResolveLancacheIpAsync(string hostname)
        {
            try
            {
                var hostEntry = await Dns.GetHostEntryAsync(hostname).ConfigureAwait(false);
                return hostEntry.AddressList.FirstOrDefault(ip => IsPrivateIp(ip));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to resolve Lancache IP: {0}", ex.Message);
                return null;
            }
        }

        private bool IsPrivateIp(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            var didPopulate = false;

            while (!shutdownToken.IsCancellationRequested)
            {
                populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

                if (availableServerEndpoints.Count < ServerEndpointMinimumSize && steamSession.steamClient.IsConnected)
                {
                    var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);

                    if (servers == null || servers.Count == 0)
                    {
                        ExhaustedToken?.Cancel();
                        return;
                    }

                    ProxyServer = servers.FirstOrDefault(x => x.UseAsProxy);

                    foreach (var server in servers)
                    {
                        if (server.Type == "SteamCache")
                        {
                            var lancacheIp = await ResolveLancacheIpAsync(server.Host).ConfigureAwait(false);
                            if (lancacheIp != null)
                            {
                                var lancacheServer = new Server
                                {
                                    Host = lancacheIp.ToString(),
                                    Type = server.Type,
                                    NumEntries = server.NumEntries,
                                    WeightedLoad = server.WeightedLoad,
                                    AllowedAppIds = server.AllowedAppIds.ToArray()
                                };

                                // Downgrade connection to HTTP if Lancache server is found
                                lancacheServer.Protocol = Server.ConnectionProtocol.HTTP;
                                Console.WriteLine($"Found Lancache Server: {lancacheServer.Host}. Downgrading connection to HTTP.");
                                availableServerEndpoints.Add(lancacheServer);
                            }
                        }
                        else
                        {
                            var isEligibleForApp = server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId);
                            if (isEligibleForApp && (server.Type == "SteamCache" || server.Type == "CDN"))
                            {
                                for (var i = 0; i < server.NumEntries; i++)
                                {
                                    availableServerEndpoints.Add(server);
                                }
                            }
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

        private Server BuildConnection(CancellationToken token)
        {
            if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
            {
                populatePoolEvent.Set();
            }

            return availableServerEndpoints.Take(token);
        }

        public Server GetConnection(CancellationToken token)
        {
            if (!activeConnectionPool.TryPop(out var connection))
            {
                connection = BuildConnection(token);
            }

            return connection;
        }

        public void ReturnConnection(Server server)
        {
            if (server == null) return;

            activeConnectionPool.Push(server);
        }

        public void ReturnBrokenConnection(Server server)
        {
            if (server == null) return;

            // Broken connections are not returned to the pool
        }
    }
}
