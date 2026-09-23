// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.IsolatedStorage;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    class AccountSettingsStore
    {
        // Member 1 was a Dictionary<string, byte[]> for SentryData.

        [ProtoMember(2, IsRequired = false)]
        public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

        // Member 3 was a Dictionary<string, string> for LoginKeys.

        [ProtoMember(4, IsRequired = false)]
        public Dictionary<string, string> LoginTokens { get; private set; }

        [ProtoMember(5, IsRequired = false)]
        public Dictionary<string, string> GuardData { get; private set; }

        string FileName;

        public string DefaultUsername { get; private set; }

        public bool LoadedFromEnv { get; private set; }

        AccountSettingsStore(bool loadedFromEnv = false)
        {
            ContentServerPenalty = new ConcurrentDictionary<string, int>();
            LoginTokens = new(StringComparer.OrdinalIgnoreCase);
            GuardData = new(StringComparer.OrdinalIgnoreCase);
            LoadedFromEnv = loadedFromEnv;
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static AccountSettingsStore Instance;
        static readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                throw new Exception("Config already loaded");

            var env = System.Environment.GetEnvironmentVariable("DEPOTDOWNLOADER_TOKEN");
            if (!string.IsNullOrWhiteSpace(env))
            {
                Instance = new AccountSettingsStore(true);
                var pieces = env.Trim().Split(":", 3);
                if (pieces.Length == 2 || pieces.Length == 3)
                {
                    var username = pieces[0];
                    var token = pieces[1];
                    Instance.DefaultUsername = username;
                    Instance.LoginTokens[username] = token;
                    if (pieces.Length == 3 && !string.IsNullOrWhiteSpace(pieces[2]))
                    {
                        Instance.GuardData[username] = pieces[2];
                    }
                }
                else
                {
                    Console.Error.WriteLine("Failed to parse DEPOTDOWNLOADER_TOKEN");
                }
            }
            else if (IsolatedStorage.FileExists(filename))
            {
                try
                {
                    using var fs = IsolatedStorage.OpenFile(filename, FileMode.Open, FileAccess.Read);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    Instance = Serializer.Deserialize<AccountSettingsStore>(ds);
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine("Failed to load account settings: {0}", ex.Message);
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

            if (Instance.LoadedFromEnv)
                return; // don't save credentials loaded from env vars; the whole point is that the user is managing the storage, not the program.

            try
            {
                using var fs = IsolatedStorage.OpenFile(Instance.FileName, FileMode.Create, FileAccess.Write);
                using var ds = new DeflateStream(fs, CompressionMode.Compress);
                Serializer.Serialize(ds, Instance);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("Failed to save account settings: {0}", ex.Message);
            }
        }

        public static void PrintToConsole()
        {
            if (!Loaded)
                throw new Exception("Printed config before loading");

            foreach (var (username, token) in Instance.LoginTokens)
            {
                _ = Instance.GuardData.TryGetValue(username, out var guard);
                if (guard == null) guard = "";
                Console.WriteLine($"{username}:{token}:{guard}");
            }

            if (Instance.LoginTokens.Count == 0)
            {
                Console.Error.WriteLine("warn: no accounts saved");
            }
        }
    }
}
