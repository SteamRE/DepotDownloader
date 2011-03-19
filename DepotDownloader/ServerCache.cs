using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using SteamKit2;

namespace DepotDownloader
{
    static class ServerCache
    {
        public static ServerList ConfigServers { get; private set; }
        public static ServerList CSDSServers { get; private set; }
        public static ServerList AuthServers { get; private set; }

        static ServerCache()
        {
            ConfigServers = new ServerList();
            CSDSServers = new ServerList();
            AuthServers = new ServerList();
        }


        public static void Build()
        {
            Console.Write( "\nBuilding Steam2 server cache..." );

            foreach ( IPEndPoint gdServer in GeneralDSClient.GDServers )
            {
                BuildServer( gdServer, ConfigServers, EServerType.ConfigServer );
                BuildServer( gdServer, CSDSServers, EServerType.CSDS );
            }

            Console.WriteLine( " Done!" );
        }

        public static void BuildAuthServers( string username )
        {
            foreach ( IPEndPoint gdServer in GeneralDSClient.GDServers )
            {
                try
                {
                    GeneralDSClient gdsClient = new GeneralDSClient();
                    gdsClient.Connect( gdServer );

                    IPEndPoint[] servers = gdsClient.GetAuthServerList( username );
                    AuthServers.AddRange( servers );

                    gdsClient.Disconnect();
                }
                catch
                {
                    Console.WriteLine( "Warning: Unable to connect to GDS {0} to get list of auth servers.", gdServer );
                }
            }
        }

        private static void BuildServer( IPEndPoint gdServer, ServerList list, EServerType type )
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
