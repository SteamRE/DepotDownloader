using System;
using System.Collections.Generic;
using ProtoBuf;
using System.IO;
using System.IO.Compression;
using System.IO.IsolatedStorage;
using System.Linq;
using SteamKit2;
using SteamKit2.Discovery;

namespace DepotDownloader
{
    [ProtoContract]
    class AccountSettingsStore
    {
        [ProtoMember(1, IsRequired=false)]
        public Dictionary<string, byte[]> SentryData { get; private set; }

        [ProtoMember(2, IsRequired = false)]
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

        [ProtoMember(3, IsRequired = false)]
        public Dictionary<string, string> LoginKeys { get; private set; }

        string FileName = null;

        AccountSettingsStore()
        {
            SentryData = new Dictionary<string, byte[]>();
            ContentServerPenalty = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            LoginKeys = new Dictionary<string, string>();
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static AccountSettingsStore Instance = null;
        static readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                throw new Exception("Config already loaded");

            if (IsolatedStorage.FileExists(filename))
            {
                try
                {
                    using (var fs = IsolatedStorage.OpenFile(filename, FileMode.Open, FileAccess.Read))
                    using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress))
                    {
                        Instance = ProtoBuf.Serializer.Deserialize<AccountSettingsStore>(ds);
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine("Failed to load account settings: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
            }
            else
            {
                Instance = new AccountSettingsStore();
            }

            Instance.FileName = filename;
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            try
            {
                using (var fs = IsolatedStorage.OpenFile(Instance.FileName, FileMode.Create, FileAccess.Write))
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress))
                {
                    ProtoBuf.Serializer.Serialize<AccountSettingsStore>(ds, Instance);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("Failed to save account settings: {0}", ex.Message);
            }
        }
    }
}
