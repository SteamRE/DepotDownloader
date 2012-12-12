using System;
using System.Collections.Generic;
using System.Net;
using SteamKit2;

namespace DepotDownloader
{
    static class ServerCache
    {
        public static ServerList ConfigServers { get; private set; }
        public static ServerList CSDSServers { get; private set; }

        static ServerCache()
        {
            ConfigServers = new ServerList();
            CSDSServers = new ServerList();
        }


        public static void Build()
        {
            Console.Write( "\nBuilding Steam2 server cache..." );

            if (DateTime.Now > ConfigCache.Instance.ServerCacheTime)
            {
                foreach (IPEndPoint gdServer in GeneralDSClient.GDServers)
                {
                    BuildServer(gdServer, ConfigServers, ESteam2ServerType.ConfigServer);
                    BuildServer(gdServer, CSDSServers, ESteam2ServerType.CSDS);
                }

                if (ConfigServers.Count > 0 && CSDSServers.Count > 0)
                {
                    ConfigCache.Instance.ConfigServers = ConfigServers;
                    ConfigCache.Instance.CSDSServers = CSDSServers;
                    ConfigCache.Instance.ServerCacheTime = DateTime.Now.AddDays(30);
                    ConfigCache.Instance.Save(ConfigCache.CONFIG_FILENAME);

                    Console.WriteLine(" Done!");
                    return;
                } else if(ConfigCache.Instance.CSDSServers == null || ConfigCache.Instance.ConfigServers == null)
                {
                    Console.WriteLine(" Unable to get server list");
                    return;
                }
            }

            ConfigServers = ConfigCache.Instance.ConfigServers;
            CSDSServers = ConfigCache.Instance.CSDSServers;

            Console.WriteLine( " Done!" );
        }

        private static void BuildServer( IPEndPoint gdServer, ServerList list, ESteam2ServerType type )
        {
            try
            {
                GeneralDSClient gdsClient = new GeneralDSClient();
                gdsClient.Connect( gdServer );

                IPEndPoint[] servers = gdsClient.GetServerList( type );
                list.AddRange( servers );

                gdsClient.Disconnect();
            }
            catch(Exception)
            {
                Console.WriteLine( "Warning: Unable to connect to GDS {0} to get list of {1} servers.", gdServer, type );
            }
        }
    }

    class ServerList : List<IPEndPoint>
    {
        public new void AddRange( IEnumerable<IPEndPoint> endPoints )
        {
            foreach ( IPEndPoint endPoint in endPoints )
                Add( endPoint );
        }

        public new void Add( IPEndPoint endPoint )
        {
            if ( this.HasServer( endPoint ) )
                return;

            base.Add( endPoint );
        }

        public bool HasServer( IPEndPoint endPoint )
        {
            foreach ( var server in this )
            {
                if ( server.Equals( endPoint ) )
                    return true;
            }
            return false;
        }
    }
}
