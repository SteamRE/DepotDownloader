// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                // Return linux as freebsd steam client doesn't exist yet
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

            foreach (var data in chunkdata)
            {
                fs.Seek((long)data.Offset, SeekOrigin.Begin);

                var adler = AdlerHash(fs, (int)data.UncompressedLength);
                if (!adler.SequenceEqual(data.Checksum))
                {
                    neededChunks.Add(data);
                }
            }

            return neededChunks;
        }

        public static byte[] AdlerHash(Stream stream, int length)
        {
            uint a = 0, b = 0;
            for (var i = 0; i < length; i++)
            {
                var c = (uint)stream.ReadByte();

                a = (a + c) % 65521;
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

        /// <summary>
        /// Decrypts using AES/ECB/PKCS7
        /// </summary>
        public static byte[] SymmetricDecryptECB(byte[] input, byte[] key)
        {
            using var aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            using var aesTransform = aes.CreateDecryptor(key, null);
            var output = aesTransform.TransformFinalBlock(input, 0, input.Length);

            return output;
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
