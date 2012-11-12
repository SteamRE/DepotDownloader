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

            foreach ( IPEndPoint gdServer in GeneralDSClient.GDServers )
            {
                BuildServer( gdServer, ConfigServers, ESteam2ServerType.ConfigServer );
                BuildServer( gdServer, CSDSServers, ESteam2ServerType.CSDS );
            }

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
            catch
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
