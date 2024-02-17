using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamPrefill.Handlers;

namespace LancachePrefill.Common
{
    public class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;

        private readonly IAnsiConsole _ansiConsole;
        private readonly string _cdnUrl;
        private readonly ConcurrentStack<Server> _activeConnectionPool = new ConcurrentStack<Server>();
        private readonly BlockingCollection<Server> _availableServerEndpoints = new BlockingCollection<Server>();
        private readonly AutoResetEvent _populatePoolEvent = new AutoResetEvent(true);
        private readonly Task _monitorTask;
        private readonly CancellationTokenSource _shutdownToken = new CancellationTokenSource();
        public CancellationTokenSource ExhaustedToken { get; set; }

        public CDNClientPool(IAnsiConsole ansiConsole, string cdnUrl)
        {
            _ansiConsole = ansiConsole;
            _cdnUrl = cdnUrl;

            _monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync, TaskCreationOptions.LongRunning);
        }

        public void Shutdown()
        {
            _shutdownToken.Cancel();
            _monitorTask.Wait();
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            var didPopulate = false;

            while (!_shutdownToken.IsCancellationRequested)
            {
                _populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

                if (_availableServerEndpoints.Count < ServerEndpointMinimumSize)
                {
                    var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);

                    if (servers == null || servers.Count == 0)
                    {
                        ExhaustedToken?.Cancel();
                        return;
                    }

                    foreach (var server in servers)
                    {
                        var resolvedIp = await ResolveLancacheIpAsync(server.Host).ConfigureAwait(false);
                        if (resolvedIp != null && IsPrivateIp(resolvedIp))
                        {
                            var lancacheServer = new Server
                            {
                                Host = resolvedIp.ToString(),
                                Type = server.Type,
                                NumEntries = server.NumEntries,
                                WeightedLoad = server.WeightedLoad,
                                AllowedAppIds = server.AllowedAppIds.ToArray(),
                                Protocol = Server.ConnectionProtocol.HTTP // Downgrade to HTTP
                            };

                            _ansiConsole.MarkupLine($"Found Lancache Server: {_cdnUrl}. Downgrading connection to HTTP.");
                            _availableServerEndpoints.Add(lancacheServer);
                        }
                    }

                    didPopulate = true;
                }
                else if (_availableServerEndpoints.Count == 0 && didPopulate)
                {
                    ExhaustedToken?.Cancel();
                    return;
                }
            }
        }

        private async Task<IReadOnlyCollection<Server>> FetchBootstrapServerListAsync()
        {
            // Implement logic to fetch the CDN server list
            return null;
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
                _ansiConsole.MarkupLine($"Failed to resolve Lancache IP: {ex.Message}");
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

        public Server GetConnection(CancellationToken token)
        {
            if (!_activeConnectionPool.TryPop(out var connection))
            {
                connection = _availableServerEndpoints.Take(token);
            }

            return connection;
        }

        public void ReturnConnection(Server server)
        {
            if (server == null) return;

            _activeConnectionPool.Push(server);
        }

        public void ReturnBrokenConnection(Server server)
        {
            // Implement logic to handle broken connections
        }
    }
}
