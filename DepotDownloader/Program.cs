#nullable enable
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDownloader
{
    internal class Program
    {
        public static Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand
            {
                new Option<bool>("--debug") { IsHidden = true },

                new Option<uint>("--app", "The AppID to download") { IsRequired = true, ArgumentHelpName = "id", Name = "AppId" },

                new Option<uint[]>("--depot", "The DepotID to download") { ArgumentHelpName = "id", Name = "Depots" },
                new Option<ulong[]>("--manifest", "Manifest id of content to download (requires --depot, default: current for branch)") { ArgumentHelpName = "id", Name = "Manifests" },

                new Option<ulong?>("--ugc", "The UGC ID to download") { ArgumentHelpName = "id" },
                new Option<ulong[]>("--pubfile", "The PublishedFileId to download (will automatically resolve to UGC id)") { ArgumentHelpName = "id", Name = "PublishedFileIds" },

                new Option<string?>(new[] { "--branch", "--beta" }, "Download from specified branch if available"),
                new Option<string?>(new[] { "--branch-password", "--betapassword" }, "Branch password if applicable"),

                new Option<string[]>("--os", () => new[] { Util.GetSteamOS() }, "The operating system for which to download the game") { Name = "OperatingSystems" }.FromAmong("all", "windows", "macos", "linux"),
                new Option<string[]>("--arch", () => new[] { Util.GetSteamArch() }, "The architecture for which to download the game") { Name = "Architectures" }.FromAmong("64", "32"),
                new Option<string[]>("--language", () => new[] { "english" }, "The language for which to download the game") { ArgumentHelpName = "language", Name = "Languages" },
                new Option<bool>("--lowviolence", "Download low violence depots"),

                new Option<string?>("--username", "The username of the account to login to for restricted content"),
                new Option<string?>("--password", "The password of the account to login to for restricted content"),
                new Option<bool>("--remember-password", "If set, remember the password for subsequent logins of this user"),
                new Option<bool>("--qr", "If set, allows logging in with a QR code generate by the Steam Mobile App"),

                new Option<DirectoryInfo>(new[] { "--directory", "--dir" }, "The directory in which to place downloaded files"),
                new Option<FileInfo>("--filelist", "A list of files to download (from the manifest). Prefix file path with 'regex:' if you want to match with regex").ExistingOnly(),
                new Option<bool>(new[] { "--validate", "--verify-all" }, "Include checksum verification of files already downloaded") { Name = "Validate" },
                new Option<bool>("--manifest-only", "Downloads a human readable manifest for any depots that would be downloaded"),

                new Option<int?>("--cellid", "The overridden CellID of the content server to download from"),
                new Option<int>("--max-servers", () => 20, "Maximum number of content servers to use"),
                new Option<int>("--max-downloads", () => 8, "Maximum number of chunks to download concurrently"),
                new Option<uint?>("--loginid", "A unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently"),
            };

            rootCommand.Handler = CommandHandler.Create<InputModel>(DownloadAsync);

            return new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .UseHelp(help =>
                {
                    // Workaround https://github.com/dotnet/command-line-api/issues/1550                    
                    foreach (var option in rootCommand.Options)
                    {
                        if (option.HasAlias(option.Name))
                        {
                            help.HelpBuilder.CustomizeSymbol(option, context => HelpBuilder.Default.GetIdentifierSymbolUsageLabel(option, context)[(option.Name.Length + 2)..]);
                        }
                    }

                    help.HelpBuilder.CustomizeLayout(GetHelpLayout);
                })
                .Build().InvokeAsync(args);
        }

        private static IEnumerable<HelpSectionDelegate> GetHelpLayout(HelpContext _)
        {
            yield return ctx => { ctx.Output.WriteLine("DepotDownloader"); };
            yield return ctx =>
            {
                ctx.Output.WriteLine(@$"Examples:
  - downloading one or all depots for an app:
    {ctx.Command.Name} --app <id> [--depot <id> [--manifest <id>]] [--username <username> [--password <password>]]

  - downloading a workshop item using pubfile id:
    {ctx.Command.Name} --app <id> --pubfile <id> [--username <username> [--password <password>]]

  - downloading a workshop item using ugc id:
    {ctx.Command.Name} --app <id> --ugc <id> [--username <username> [--password <password>]]"
                );
            };
            yield return HelpBuilder.Default.CommandArgumentsSection();
            yield return HelpBuilder.Default.OptionsSection();
            yield return HelpBuilder.Default.SubcommandsSection();
            yield return HelpBuilder.Default.AdditionalArgumentsSection();
        }

        public record InputModel(
            bool Debug,
            uint AppId,
            uint[] Depots,
            ulong[] Manifests,
            ulong? UgcId,
            ulong[] PublishedFileIds,
            string? Branch,
            string? BranchPassword,
            string[] OperatingSystems,
            string[] Architectures,
            string[] Languages,
            bool LowViolence,
            string? Username,
            string? Password,
            bool RememberPassword,
            bool Qr,
            DirectoryInfo? Directory,
            FileInfo? FileList,
            bool Validate,
            bool ManifestOnly,
            int? CellId,
            int MaxServers,
            int MaxDownloads,
            uint? LoginId
        );

        public static async Task<int> DownloadAsync(InputModel input)
        {
            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            DebugLog.Enabled = input.Debug;
            if (input.Debug)
            {
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();

                DebugLog.WriteLine("DepotDownloader", "Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
                DebugLog.WriteLine("DepotDownloader", "Runtime: {0}", RuntimeInformation.FrameworkDescription);
            }

            ContentDownloader.Config.RememberPassword = input.RememberPassword;
            ContentDownloader.Config.UseQrCode = input.Qr;
            ContentDownloader.Config.DownloadManifestOnly = input.ManifestOnly;
            ContentDownloader.Config.CellID = input.CellId ?? 0;

            if (input.FileList != null)
            {
                try
                {
                    var files = await File.ReadAllLinesAsync(input.FileList.FullName);

                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                    foreach (var fileEntry in files)
                    {
                        if (fileEntry.StartsWith("regex:"))
                        {
                            var regex = new Regex(fileEntry[6..], RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            ContentDownloader.Config.FilesToDownloadRegex.Add(regex);
                        }
                        else
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace('\\', '/'));
                        }
                    }

                    Console.WriteLine("Using filelist: '{0}'.", input.FileList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: Unable to load filelist: {0}", ex);
                    return 1;
                }
            }

            if (input.Directory != null)
            {
                ContentDownloader.Config.InstallDirectory = input.Directory.FullName;
            }

            ContentDownloader.Config.VerifyAll = input.Validate;
            ContentDownloader.Config.MaxDownloads = input.MaxDownloads;
            ContentDownloader.Config.MaxServers = Math.Max(input.MaxServers, ContentDownloader.Config.MaxDownloads);
            ContentDownloader.Config.LoginID = input.LoginId;

            #endregion

            if (InitializeSteam(input.Username, input.Password))
            {
                try
                {
                    if (input.PublishedFileIds.Any())
                    {
                        await ContentDownloader.DownloadPubfileAsync(input.AppId, input.PublishedFileIds).ConfigureAwait(false);
                    }
                    else if (input.UgcId != null)
                    {
                        await ContentDownloader.DownloadUGCAsync(input.AppId, input.UgcId.Value).ConfigureAwait(false);
                    }
                    else
                    {
                        ContentDownloader.Config.BetaPassword = input.BranchPassword;

                        ContentDownloader.Config.DownloadAllPlatforms = input.OperatingSystems.Contains("all");
                        ContentDownloader.Config.DownloadAllLanguages = input.Languages.Contains("all");

                        var depotManifestIds = new List<(uint, ulong)>();

                        if (input.Manifests.Length > 0)
                        {
                            if (input.Depots.Length != input.Manifests.Length)
                            {
                                Console.WriteLine("Error: --manifest requires one id for every --depot specified");
                                return 1;
                            }

                            var zippedDepotManifest = input.Depots.Zip(input.Manifests, (depotId, manifestId) => (depotId, manifestId));
                            depotManifestIds.AddRange(zippedDepotManifest);
                        }
                        else
                        {
                            depotManifestIds.AddRange(input.Depots.Select(depotId => (depotId, ContentDownloader.INVALID_MANIFEST_ID)));
                        }

                        await ContentDownloader.DownloadAppAsync(input.AppId, depotManifestIds, input.Branch ?? ContentDownloader.DEFAULT_BRANCH, input.OperatingSystems, input.Architectures, input.Languages, input.LowViolence, false).ConfigureAwait(false);
                    }
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

        private static bool InitializeSteam(string? username, string? password)
        {
            if (!ContentDownloader.Config.UseQrCode)
            {
                if (username != null && password == null && (!ContentDownloader.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username.ToLowerInvariant())))
                {
                    do
                    {
                        Console.Write("Enter account password for \"{0}\": ", username);
                        password = Console.IsInputRedirected
                            ? Console.ReadLine()
                            : Util.ReadPassword();

                        Console.WriteLine();
                    } while (string.Empty == password);
                }
                else if (username == null)
                {
                    Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
                }
            }

            return ContentDownloader.InitializeSteam3(username, password);
        }
    }
}
