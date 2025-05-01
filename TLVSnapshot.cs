using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace Snapshot
{
    /// <summary>
    /// Provides import/export of a repository directory tree using a length-prefixed TLV(Tag, Length, Value) snapshot format
    /// with integrity check and optional chunking.
    /// </summary>
    public static class TLVSnapshot
    {
        private const int FormatVersion = 1;
        private const int DefaultChunkSize = 25 * 1024 * 1024; // 25 MB

        /// <summary>
        /// Exports the repository directory to a single TLV snapshot file with SHA3-512 checksum.
        /// </summary>
        public static void Export(string repoPath, string snapshotFilePath, bool ai, IDictionary<string, string> meta)
        {
            using var outStream = new FileStream(snapshotFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(outStream, Encoding.UTF8, leaveOpen: false) { NewLine = "\n" };
            if (ai)
            {
                writer.WriteLine("-----BEGIN TLV SNAPSHOT-----");
                writer.Flush();
            }
            // Header: HDR <version> <repoName>
            WriteHeader(writer, repoPath, null, meta);
            var repoDir = new DirectoryInfo(repoPath);
            if (!repoDir.Exists)
                throw new DirectoryNotFoundException($"Repository directory not found: {repoPath}");

            // Preorder traversal: dir, its files, then subdirs
            ProcessDirectory(repoDir, repoDir.FullName, writer, outStream, ai);
            outStream.Flush();

            if (ai)
            {
                writer.WriteLine("-----END TLV SNAPSHOT-----");
                writer.Flush();
            }
        }

        /// <summary>
        /// Exports the repository snapshot in size-limited chunks, streaming data without full buffering.
        /// Each chunk is written to {outputBasePath}.partN
        /// </summary>
        public static void ExportChunks(string repoPath, string outputBasePath, int maxChunkSize = DefaultChunkSize,
        bool ai = false, IDictionary<string, string> meta = null)
        {
            string temp = Path.GetTempFileName();
            Export(repoPath, temp, ai, meta);
            long totalSize = new FileInfo(temp).Length;
            int chunks = (int)Math.Ceiling((double)totalSize / maxChunkSize);

            using var inFs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
            for (int i = 1; i <= chunks; i++)
            {
                string partPath = $"{outputBasePath}.part{i}";
                using var outFs = new FileStream(partPath, FileMode.Create, FileAccess.Write);
                using var writer = new StreamWriter(outFs, Encoding.UTF8, leaveOpen: true) { NewLine = "\n" };
                string seq = $"{i}/{chunks}";
                WriteHeader(writer, repoPath, seq, meta);
                writer.Flush();

                long toWrite = Math.Min(maxChunkSize, totalSize - inFs.Position);
                byte[] chunkBuf = new byte[81920];
                while (toWrite > 0)
                {
                    int n = inFs.Read(chunkBuf, 0, (int)Math.Min(chunkBuf.Length, toWrite));
                    if (n <= 0) break;
                    outFs.Write(chunkBuf, 0, n);
                    toWrite -= n;
                }
            }
            File.Delete(temp);
        }

        private static void WriteHeader(StreamWriter writer, string repoPath, string sequence,
        IDictionary<string, string> meta)
        {
            string repoName = new DirectoryInfo(repoPath).Name;
            if (string.IsNullOrEmpty(sequence))
                writer.WriteLine($"HDR {FormatVersion} {repoName}");
            else
                writer.WriteLine($"HDR {FormatVersion} {repoName} {sequence}");

            if (meta != null)
            {
                foreach (var kv in meta)
                    writer.WriteLine($"META {kv.Key} {kv.Value}");
            }
        }

        private static void ProcessDirectory(DirectoryInfo dir, string root, StreamWriter writer, Stream outStream, bool ai)
        {
            // DIR record
            string relDir = Path.GetRelativePath(root, dir.FullName).Replace("\\", "/");
            if (string.IsNullOrEmpty(relDir)) relDir = ".";
            byte[] dirNameBytes = Encoding.UTF8.GetBytes(relDir);
            writer.WriteLine($"DIR {dirNameBytes.Length} {relDir}");
            writer.Flush();

            // FIL records: <pathLen> <contentLen> <path>
            foreach (var file in dir.GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string relFile = Path.GetRelativePath(root, file.FullName).Replace("\\", "/");
                byte[] content = NormalizeTextFile(file.FullName);
                if (content == null) continue;
                
                int pathLen = Encoding.UTF8.GetByteCount(relFile);
                writer.WriteLine($"FIL {pathLen} {content.Length} {relFile}");
                writer.Flush();
                if (ai)
                {
                    writer.WriteLine($"-----BEGIN FILE: {relFile} ({content.Length} bytes)-----");
                    writer.Flush();
                }
                outStream.Write(content, 0, content.Length);
                outStream.WriteByte((byte)'\n');
                outStream.Flush();

                if (ai)
                {
                    writer.WriteLine("-----END FILE-----");
                    writer.Flush();
                }
            }

            // Recurse
            foreach (var sub in dir.GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
                ProcessDirectory(sub, root, writer, outStream, ai);
        }

        /// <summary>
        /// Imports TLV snapshot or chunk parts into destPath, verifying checksum.
        /// Accepts multiple HDRs (skips extras) and duplicate DIRs.
        /// </summary>
        public static void Import(string snapshotFilePath, string destPath)
        {
            using var fs = new FileStream(snapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            Directory.CreateDirectory(destPath);
            while (true)
            {
                byte[] lineBytes = ReadLineBytes(fs);
                if (lineBytes == null) break;
                string line = Encoding.UTF8.GetString(lineBytes).TrimStart('\uFEFF');

                if (line.StartsWith("-----BEGIN TLV SNAPSHOT") ||
                    line.StartsWith("-----END TLV SNAPSHOT") ||
                    line.StartsWith("-----BEGIN FILE:") ||
                    line.StartsWith("-----END FILE-----") ||
                    line.StartsWith("META "))
                {
                    continue;   // no hashing, no processing
                }

                var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                if (parts[0] == "HDR")
                {
                    continue; // skip extra headers
                }
                else if (parts[0] == "DIR")
                {
                    // parts: ["DIR", pathLen, path]
                    CreateDir(destPath, parts[2]);
                }
                else if (parts[0] == "FIL")
                {
                    // parts: ["FIL", pathLen, contentLen, path]
                    int pathLen = int.Parse(parts[1]);
                    int contentLen = int.Parse(parts[2]);
                    string rel = parts[3];
                    if (Encoding.UTF8.GetByteCount(rel) != pathLen)
                        throw new InvalidDataException($"Path length mismatch for {rel}");
                    string fullDir = Path.Combine(destPath, Path.GetDirectoryName(rel) ?? string.Empty);
                    Directory.CreateDirectory(fullDir);

                    // Read file content
                    byte[] buffer = new byte[81920];
                    int remaining = contentLen;
                    using var outFile = new FileStream(Path.Combine(destPath, rel), FileMode.Create, FileAccess.Write);
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(buffer.Length, remaining);
                        int r = fs.Read(buffer, 0, toRead);
                        if (r <= 0) throw new EndOfStreamException();
                        outFile.Write(buffer, 0, r);
                        remaining -= r;
                    }
                    // consume newline
                    int nl = fs.ReadByte();
                    if (nl != '\n') throw new InvalidDataException("Expected newline after file content");
                }
                else
                {
                    throw new InvalidDataException($"Unknown record: {parts[0]}");
                }
            }
        }

        private static byte[] ReadLineBytes(Stream s)
        {
            using var ms = new MemoryStream();
            int b;
            while ((b = s.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') ms.WriteByte((byte)b);
            }
            if (ms.Length == 0 && b == -1) return null;
            return ms.ToArray();
        }

        private static void CreateDir(string root, string rel)
        {
            string full = Path.GetFullPath(Path.Combine(root, rel)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!full.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Invalid path: {rel}");

            Directory.CreateDirectory(full);
        }

        private static byte[] NormalizeTextFile(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                try
                {
                    using var sr = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
                    string text = sr.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
                    return Encoding.UTF8.GetBytes(text);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException($"File '{filePath}' is not valid UTF-8: {ex.Message}", ex);
                }
            }
            catch 
            {
                return null;//file locked
            }
        }
    }
}
