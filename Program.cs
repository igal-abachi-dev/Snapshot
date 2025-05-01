using System;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.IO;
using System.Collections.Generic;

namespace Snapshot
{
    class Program
    {
        static int Main(string[] args)
        {
            var exportCommand = new Command("export", "Export a repository to a TLV snapshot file")
            {
                new Argument<string>("repoPath", "Path to the root of the repository"),
                new Argument<string>("snapshotFile", "Path to write the TLV snapshot file"),
                new Option<bool>(new[] { "--b64", "-eml" }, "Export as base64-encoded string for email"),
                new Option<bool>("--ai", "ai prompt friendly snapshot"),
                new Option<string[]>("--meta", description: "Metadata entries as key=value")
            };
            exportCommand.Handler = CommandHandler.Create((string repoPath, string snapshotFile, bool b64, bool ai, string[] meta) =>
            {
                if (b64 && !ai)
                {
                    string base64Content = EmailExport.ExportAsBase64Email(repoPath);
                    File.WriteAllText(snapshotFile, base64Content);
                    Console.WriteLine($"Snapshot exported as base64 to '{snapshotFile}'");
                }
                else
                {
                    var metaDict = new Dictionary<string, string>();
                    if (meta != null)
                    {
                        foreach (var m in meta)
                        {
                            var split = m.Split('=', 2);
                            if (split.Length != 2)
                            {
                                Console.Error.WriteLine($"Invalid meta entry: '{m}', expected key=value");
                                return 1;
                            }
                            metaDict[split[0].Trim()] = split[1].Trim();
                        }
                    }
                    TLVSnapshot.Export(repoPath, snapshotFile, ai, metaDict);
                    Console.WriteLine($"Snapshot exported to '{snapshotFile}'");
                }
                return 0;
            });

            var importCommand = new Command("import", "Import a TLV snapshot file into a directory")
            {
                new Argument<string>("snapshotFile", "Path to the TLV snapshot file"),
                new Argument<string>("destPath", "Destination directory to reconstruct the repo"),
                new Option<bool>(new[] { "--b64", "-eml" }, "Import from base64-encoded string for email")
            };
            importCommand.Handler = CommandHandler.Create((string snapshotFile, string destPath, bool b64) =>
            {
                if (b64)
                {
                    string base64Content = File.ReadAllText(snapshotFile);
                    EmailExport.ImportFromBase64Email(base64Content, destPath);
                    Console.WriteLine($"Snapshot imported from base64 into '{destPath}'");
                }
                else
                {
                    TLVSnapshot.Import(snapshotFile, destPath);
                    Console.WriteLine($"Snapshot imported into '{destPath}'");
                }
                return 0;
            });

            var chunkCommand = new Command("export-chunks", "Export the repository snapshot in size-limited chunks")
            {
                new Argument<string>("repoPath", "Path to the root of the repository"),
                new Argument<string>("outputBasePath", "Base path for chunk files"),
                new Option<int>(new[] { "--chunk-size", "-sz" }, () => 25 * 1024 * 1024, "Max size in bytes per chunk file")
            };
            chunkCommand.Handler = CommandHandler.Create((string repoPath, string outputBasePath, int chunkSize) =>
            {
                TLVSnapshot.ExportChunks(repoPath, outputBasePath, chunkSize);
                Console.WriteLine($"Snapshot chunks written to '{outputBasePath}.part*'");
                return 0;
            });

            var rootCommand = new RootCommand("Snapshot - CLI tool for TLV Snapshot Repository");
            rootCommand.AddCommand(exportCommand);
            rootCommand.AddCommand(importCommand);
            rootCommand.AddCommand(chunkCommand);

            return rootCommand.InvokeAsync(args).Result;
        }
    }
}
