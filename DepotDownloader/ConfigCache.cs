using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace DepotDownloader
{
    [ProtoContract]
    class ConfigCache
    {
        public const string CONFIG_FILENAME = "config.proto";
        public static ConfigCache Instance = Load(CONFIG_FILENAME);

        [ProtoContract]
        class IPEndpointSurrogate
        {
            [ProtoMember(1)]
            private byte[] address;
            [ProtoMember(2)]
            private int port;

            public IPEndpointSurrogate()
            {
            }

            public IPEndpointSurrogate(byte[] address, int port)
            {
                this.address = address;
                this.port = port;
            }

            public static implicit operator IPEndPoint(IPEndpointSurrogate ip)
            {
                if (ip == null || ip.address == null)
                    return null;

                return new IPEndPoint(new IPAddress(ip.address), ip.port);
            }

            public static implicit operator IPEndpointSurrogate(IPEndPoint ip)
            {
                if (ip == null)
                    return null;

                return new IPEndpointSurrogate(ip.Address.GetAddressBytes(), ip.Port);
            }
        }

        [ProtoMember(1)]
        public byte[] CDRHash { get; set; }
        [ProtoMember(2)]
        public DateTime CDRCacheTime { get; set; }

        [ProtoMember(3)]
        public ServerList ConfigServers { get; set; }
        [ProtoMember(4)]
        public ServerList CSDSServers { get; set; }
        [ProtoMember(5)]
        public DateTime ServerCacheTime { get; set; }

        public static ConfigCache Load(string filename)
        {
            ProtoBuf.Meta.RuntimeTypeModel.Default.Add(typeof(IPEndPoint), true).SetSurrogate(typeof(IPEndpointSurrogate));

            if(!File.Exists(filename))
                return new ConfigCache();

            try
            {
                using (FileStream fs = File.Open(filename, FileMode.Open))
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress))
                    return ProtoBuf.Serializer.Deserialize<ConfigCache>(ds);
            }
            catch (IOException)
            {
                File.Delete(filename);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to load config cache: {0}", e.Message);
            }

            return new ConfigCache();
        }

        public void Save(string filename)
        {
            try
            {
                using (FileStream fs = File.Open(filename, FileMode.Create))
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress))
                    ProtoBuf.Serializer.Serialize<ConfigCache>(ds, this);
            }
            catch (IOException)
            {
            }
        }
    }
}
