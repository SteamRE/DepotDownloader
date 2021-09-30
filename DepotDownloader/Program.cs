using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDownloader
{
    internal partial class Program
    {
        private static int Main(string[] args)
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task<int> MainAsync(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            DebugLog.Enabled = false;
            AccountSettingsStore.LoadFromFile("account.config");            
            
            // Use ArgumentParser to read args
            ProgramArgs arguments = null;
            try
            {
                arguments = ArgumentParser.Parse<ProgramArgs>(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                PrintUsage();
                return 2;
            }

            #region Arguments -> ContentDownload.Config

            if (arguments.Debug)
            {
                // Verbose
                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                // why?
                //var httpEventListener = new HttpDiagnosticEventListener();
            }

            ContentDownloader.Config.RememberPassword = arguments.RememberPassword;
            ContentDownloader.Config.DownloadManifestOnly = arguments.ManifestOnly;
            ContentDownloader.Config.CellID = arguments.CellID;
            ContentDownloader.Config.InstallDirectory = arguments.InstallDir;
            ContentDownloader.Config.VerifyAll = arguments.Validate;
            ContentDownloader.Config.MaxDownloads = arguments.MaxDownloads;
            ContentDownloader.Config.MaxServers = Math.Max(arguments.MaxServers, arguments.MaxDownloads);
            ContentDownloader.Config.LoginID = arguments.LoginID;
            ContentDownloader.Config.DownloadAllPlatforms = arguments.AllPlatforms;
            ContentDownloader.Config.DownloadAllLanguages = arguments.AllLanguages;
            ContentDownloader.Config.BetaPassword = arguments.BetaPassword;

            #endregion

            if (!string.IsNullOrEmpty(arguments.FileList))
            {
                await LoadFileListAsync(arguments.FileList);
            }

            if (arguments.AppID == ContentDownloader.INVALID_APP_ID)
            {
                Console.WriteLine("Error: App not specified");
                PrintUsage();
                return 1;
            }

            // PubFile download
            if (arguments.PubFile != ContentDownloader.INVALID_MANIFEST_ID)
            {
                return await DownloadPubFileAsync(arguments);
            }

            // UGC download
            if (arguments.UGC != ContentDownloader.INVALID_MANIFEST_ID)
            {
                return await DownloadUGCAsync(arguments);
            }

            // App download
            if (ContentDownloader.Config.DownloadAllPlatforms && arguments.OperatingSystem != null)
            {
                Console.WriteLine("Error: Cannot specify --os when --all-platforms is specified.");
                return 1;
            }

            if (ContentDownloader.Config.DownloadAllLanguages && arguments.Language != null)
            {
                Console.WriteLine("Error: Cannot specify --language when --all-languages is specified.");
                return 1;
            }

            var depotManifestIds = new List<(uint, ulong)>();
            if (arguments.ManifestIDs.Count > 0)
            {
                if (arguments.DepotIDs.Count != arguments.ManifestIDs.Count)
                {
                    Console.WriteLine("Error: --manifest requires one id for every --depot specified");
                    return 1;
                }

                var zippedDepotManifest = arguments.DepotIDs.Zip(arguments.ManifestIDs, (d, m) => (d, m));
                depotManifestIds.AddRange(zippedDepotManifest);
            }
            else
            {
                depotManifestIds.AddRange(arguments.DepotIDs.Select(d => (d, ContentDownloader.INVALID_MANIFEST_ID)));
            }

            if (InitializeSteam(arguments.Username, arguments.Password))
            {
                try
                {
                    await ContentDownloader.DownloadAppAsync(arguments.AppID,
                        depotManifestIds,
                        arguments.BetaBranchName,
                        arguments.OperatingSystem,
                        arguments.Architecture,
                        arguments.Language,
                        arguments.LowViolence,
                        false).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ContentDownloaderException or OperationCanceledException)
                {
                    Console.WriteLine(ex.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                    throw;
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }
            }
            else
            {
                Console.WriteLine("Error: InitializeSteam failed");
                return 1;
            }

            return 0;
        }

        private static async Task<int> DownloadPubFileAsync(ProgramArgs args)
        {
            if (InitializeSteam(args.Username, args.Password))
            {
                try
                {
                    await ContentDownloader.DownloadPubfileAsync(args.AppID, args.PubFile).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ContentDownloaderException or OperationCanceledException)
                {
                    Console.WriteLine(ex.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                    throw;
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }
            }
            else
            {
                Console.WriteLine("Error: InitializeSteam failed");
                return 1;
            }

            return 0;
        }

        private static async Task<int> DownloadUGCAsync(ProgramArgs args)
        {
            if (InitializeSteam(args.Username, args.Password))
            {
                try
                {
                    await ContentDownloader.DownloadUGCAsync(args.AppID, args.UGC).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ContentDownloaderException or OperationCanceledException)
                {
                    Console.WriteLine(ex.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                    throw;
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }
            }
            else
            {
                Console.WriteLine("Error: InitializeSteam failed");
                return 1;
            }

            return 0;
        }

        private static async Task LoadFileListAsync(string fileList)
        {
            try
            {
                var fileListData = await File.ReadAllTextAsync(fileList);
                var files = fileListData.Split(new[] {'\n', '\r'}, StringSplitOptions.RemoveEmptyEntries);

                ContentDownloader.Config.UsingFileList = true;
                ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                foreach (var fileEntry in files)
                {
                    if (fileEntry.StartsWith("regex:"))
                    {
                        var rgx = new Regex(fileEntry.Substring(6), RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                    }
                    else
                    {
                        ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace('\\', '/'));
                    }
                }

                Console.WriteLine("Using file list: '{0}'.", fileList);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: Unable to load file list: {0}", ex);
            }
        }

        private static bool InitializeSteam(string username, string password)
        {
            if (username != null && password == null && (!ContentDownloader.Config.RememberPassword ||
                                                         !AccountSettingsStore.Instance.LoginKeys
                                                             .ContainsKey(username)))
            {
                do
                {
                    Console.Write("Enter account password for \"{0}\": ", username);
                    password = Console.IsInputRedirected ? Console.ReadLine() : Util.ReadPassword();

                    Console.WriteLine();
                } while (string.Empty == password);
            }
            else if (username == null)
            {
                Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
            }

            // capture the supplied password in case we need to re-use it after checking the login key
            ContentDownloader.Config.SuppliedPassword = password;

            return ContentDownloader.InitializeSteam3(username, password);
        }

        private static void PrintUsage()
        {
            Console.WriteLine($"DepotDownloader version {typeof(Program).Assembly.GetName().Version?.ToString(3)} - copyright SteamRE Team 2021");
            Console.WriteLine();
            Console.WriteLine("  Steam depot downloader utilizing the SteamKit2 library.");
            Console.WriteLine("    https://github.com/SteamRE/DepotDownloader");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine();
            Console.WriteLine("  Downloading one or all depots for an app:");
            Console.WriteLine("    depotdownloader --app <id> [--depot <id> [--manifest <id>]]");
            Console.WriteLine("        [--username <username> [--password <password>]] [other options]");
            Console.WriteLine();
            Console.WriteLine("  Downloading a workshop item using pubfile id");
            Console.WriteLine("    depotdownloader --app <id> --pub-file <id> [--username <username> [--password <password>]]");
            Console.WriteLine();
            Console.WriteLine("  Downloading a workshop item using ugc id");
            Console.WriteLine("    depotdownloader --app <id> --ugc <id> [--username <username> [--password <password>]]");
            Console.WriteLine();
            Console.WriteLine("Parameters:");
            Console.WriteLine();
            Console.WriteLine(ArgumentParser.GetHelpList<ProgramArgs>(2));
        }
    }
}
