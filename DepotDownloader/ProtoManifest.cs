// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ProtoBuf;
using SteamKit2;

namespace DepotDownloader
{
    [ProtoContract]
    class ProtoManifest
    {
        // Proto ctor
        private ProtoManifest()
        {
            Files = [];
        }

        public ProtoManifest(DepotManifest sourceManifest, ulong id) : this()
        {
            sourceManifest.Files.ForEach(f => Files.Add(new FileData(f)));
            ID = id;
            CreationTime = sourceManifest.CreationTime;
        }

        [ProtoContract]
        public class FileData
        {
            // Proto ctor
            private FileData()
            {
                Chunks = [];
            }

            public FileData(DepotManifest.FileData sourceData) : this()
            {
                FileName = sourceData.FileName;
                sourceData.Chunks.ForEach(c => Chunks.Add(new ChunkData(c)));
                Flags = sourceData.Flags;
                TotalSize = sourceData.TotalSize;
                FileHash = sourceData.FileHash;
            }

            [ProtoMember(1)]
            public string FileName { get; internal set; }

            /// <summary>
            /// Gets the chunks that this file is composed of.
            /// </summary>
            [ProtoMember(2)]
            public List<ChunkData> Chunks { get; private set; }

            /// <summary>
            /// Gets the file flags
            /// </summary>
            [ProtoMember(3)]
            public EDepotFileFlag Flags { get; private set; }

            /// <summary>
            /// Gets the total size of this file.
            /// </summary>
            [ProtoMember(4)]
            public ulong TotalSize { get; private set; }

            /// <summary>
            /// Gets the hash of this file.
            /// </summary>
            [ProtoMember(5)]
            public byte[] FileHash { get; private set; }
        }

        [ProtoContract(SkipConstructor = true)]
        public class ChunkData
        {
            public ChunkData(DepotManifest.ChunkData sourceChunk)
            {
                ChunkID = sourceChunk.ChunkID;
                Checksum = BitConverter.GetBytes(sourceChunk.Checksum);
                Offset = sourceChunk.Offset;
                CompressedLength = sourceChunk.CompressedLength;
                UncompressedLength = sourceChunk.UncompressedLength;
            }

            /// <summary>
            /// Gets the SHA-1 hash chunk id.
            /// </summary>
            [ProtoMember(1)]
            public byte[] ChunkID { get; private set; }

            /// <summary>
            /// Gets the expected Adler32 checksum of this chunk.
            /// </summary>
            [ProtoMember(2)]
            public byte[] Checksum { get; private set; }

            /// <summary>
            /// Gets the chunk offset.
            /// </summary>
            [ProtoMember(3)]
            public ulong Offset { get; private set; }

            /// <summary>
            /// Gets the compressed length of this chunk.
            /// </summary>
            [ProtoMember(4)]
            public uint CompressedLength { get; private set; }

            /// <summary>
            /// Gets the decompressed length of this chunk.
            /// </summary>
            [ProtoMember(5)]
            public uint UncompressedLength { get; private set; }
        }

        [ProtoMember(1)]
        public List<FileData> Files { get; private set; }

        [ProtoMember(2)]
        public ulong ID { get; private set; }

        [ProtoMember(3)]
        public DateTime CreationTime { get; private set; }

        public static ProtoManifest LoadFromFile(string filename, out byte[] checksum)
        {
            if (!File.Exists(filename))
            {
                checksum = null;
                return null;
            }

            using var ms = new MemoryStream();
            using (var fs = File.Open(filename, FileMode.Open))
            using (var ds = new DeflateStream(fs, CompressionMode.Decompress))
                ds.CopyTo(ms);

            checksum = SHA1.HashData(ms.ToArray());

            ms.Seek(0, SeekOrigin.Begin);
            return Serializer.Deserialize<ProtoManifest>(ms);
        }

        public void SaveToFile(string filename, out byte[] checksum)
        {
            using var ms = new MemoryStream();
            Serializer.Serialize(ms, this);

            checksum = SHA1.HashData(ms.ToArray());

            ms.Seek(0, SeekOrigin.Begin);

            using var fs = File.Open(filename, FileMode.Create);
            using var ds = new DeflateStream(fs, CompressionMode.Compress);
            ms.CopyTo(ds);
        }

        public DepotManifest ConvertToSteamManifest(uint depotId)
        {
            ulong uncompressedSize = 0, compressedSize = 0;
            var newManifest = new DepotManifest();
            newManifest.Files = new List<DepotManifest.FileData>(Files.Count);

            foreach (var file in Files)
            {
                var fileNameHash = SHA1.HashData(Encoding.UTF8.GetBytes(file.FileName.Replace('/', '\\').ToLowerInvariant()));
                var newFile = new DepotManifest.FileData(file.FileName, fileNameHash, file.Flags, file.TotalSize, file.FileHash, null, false, file.Chunks.Count);

                foreach (var chunk in file.Chunks)
                {
                    var newChunk = new DepotManifest.ChunkData(chunk.ChunkID, BitConverter.ToUInt32(chunk.Checksum, 0), chunk.Offset, chunk.CompressedLength, chunk.UncompressedLength);
                    newFile.Chunks.Add(newChunk);

                    uncompressedSize += chunk.UncompressedLength;
                    compressedSize += chunk.CompressedLength;
                }

                newManifest.Files.Add(newFile);
            }

            newManifest.FilenamesEncrypted = false;
            newManifest.DepotID = depotId;
            newManifest.ManifestGID = ID;
            newManifest.CreationTime = CreationTime;
            newManifest.TotalUncompressedSize = uncompressedSize;
            newManifest.TotalCompressedSize = compressedSize;
            newManifest.EncryptedCRC = 0;

            return newManifest;
        }
    }
}
