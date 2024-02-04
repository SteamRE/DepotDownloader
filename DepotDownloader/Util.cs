using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DepotDownloader
{
    static class Util
    {
        public static string GetSteamOS()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            return "unknown";
        }

        public static string GetSteamArch()
        {
            return Environment.Is64BitOperatingSystem ? "64" : "32";
        }

        public static string ReadPassword()
        {
            ConsoleKeyInfo keyInfo;
            var password = new StringBuilder();

            do
            {
                keyInfo = Console.ReadKey(true);

                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Remove(password.Length - 1, 1);
                        Console.Write("\b \b");
                    }

                    continue;
                }

                /* Printable ASCII characters only */
                var c = keyInfo.KeyChar;
                if (c >= ' ' && c <= '~')
                {
                    password.Append(c);
                    Console.Write('*');
                }
            } while (keyInfo.Key != ConsoleKey.Enter);

            return password.ToString();
        }

        // Validate a file against Steam3 Chunk data
        public static List<ProtoManifest.ChunkData> ValidateSteam3FileChecksums(FileStream fs, ProtoManifest.ChunkData[] chunkdata)
        {
            var neededChunks = new List<ProtoManifest.ChunkData>();
            int read;

            foreach (var data in chunkdata)
            {
                var chunk = new byte[data.UncompressedLength];
                fs.Seek((long)data.Offset, SeekOrigin.Begin);
                read = fs.Read(chunk, 0, (int)data.UncompressedLength);

                byte[] tempchunk;
                if (read < data.UncompressedLength)
                {
                    tempchunk = new byte[read];
                    Array.Copy(chunk, 0, tempchunk, 0, read);
                }
                else
                {
                    tempchunk = chunk;
                }

                var adler = AdlerHash(tempchunk);
                if (!adler.SequenceEqual(data.Checksum))
                {
                    neededChunks.Add(data);
                }
            }

            return neededChunks;
        }

        public static byte[] AdlerHash(byte[] input)
        {
            uint a = 0, b = 0;
            for (var i = 0; i < input.Length; i++)
            {
                a = (a + input[i]) % 65521;
                b = (b + a) % 65521;
            }

            return BitConverter.GetBytes(a | (b << 16));
        }

        public static byte[] DecodeHexString(string hex)
        {
            if (hex == null)
                return null;

            var chars = hex.Length;
            var bytes = new byte[chars / 2];

            for (var i = 0; i < chars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);

            return bytes;
        }

        public static string EncodeHexString(byte[] input)
        {
            return input.Aggregate(new StringBuilder(),
                (sb, v) => sb.Append(v.ToString("x2"))
            ).ToString();
        }

        public static async Task InvokeAsync(IEnumerable<Func<Task>> taskFactories, int maxDegreeOfParallelism)
        {
            ArgumentNullException.ThrowIfNull(taskFactories);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDegreeOfParallelism, 0);

            var queue = taskFactories.ToArray();

            if (queue.Length == 0)
            {
                return;
            }

            var tasksInFlight = new List<Task>(maxDegreeOfParallelism);
            var index = 0;

            do
            {
                while (tasksInFlight.Count < maxDegreeOfParallelism && index < queue.Length)
                {
                    var taskFactory = queue[index++];

                    tasksInFlight.Add(taskFactory());
                }

                var completedTask = await Task.WhenAny(tasksInFlight).ConfigureAwait(false);

                await completedTask.ConfigureAwait(false);

                tasksInFlight.Remove(completedTask);
            } while (index < queue.Length || tasksInFlight.Count != 0);
        }
    }
}
