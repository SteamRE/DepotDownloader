// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDownloader
{
    class Program
    {
        private static bool[] consumedArgs;

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintVersion();
                PrintUsage();

                if (OperatingSystem.IsWindowsVersionAtLeast(5, 0))
                {
                    PlatformUtilities.VerifyConsoleLaunch();
                }

                return 0;
            }

            Ansi.Init();

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            // Not using HasParameter because it is case insensitive
            if (args.Length == 1 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                return 0;
            }

            consumedArgs = new bool[args.Length];

            if (HasParameter(args, "-debug"))
            {
                PrintVersion(true);

                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();
            }

            var username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
            var password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");
            ContentDownloader.Config.RememberPassword = HasParameter(args, "-remember-password");
            ContentDownloader.Config.UseQrCode = HasParameter(args, "-qr");
            ContentDownloader.Config.SkipAppConfirmation = HasParameter(args, "-no-mobile");

            if (username == null)
            {
                if (ContentDownloader.Config.RememberPassword && !ContentDownloader.Config.UseQrCode)
                {
                    Console.WriteLine("Error: -remember-password can not be used without -username or -qr.");
                    return 1;
                }
            }
            else if (ContentDownloader.Config.UseQrCode)
            {
                Console.WriteLine("Error: -qr can not be used with -username.");
                return 1;
            }

            ContentDownloader.Config.DownloadManifestOnly = HasParameter(args, "-manifest-only");

            var cellId = GetParameter(args, "-cellid", -1);
            if (cellId == -1)
            {
                cellId = 0;
            }

            ContentDownloader.Config.CellID = cellId;

            var fileList = GetParameter<string>(args, "-filelist");

            if (fileList != null)
            {
                const string RegexPrefix = "regex:";

                try
                {
                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ContentDownloader.Config.FilesToDownloadRegex = [];

                    var files = await File.ReadAllLinesAsync(fileList);

                    foreach (var fileEntry in files)
                    {
                        if (string.IsNullOrWhiteSpace(fileEntry))
                        {
                            continue;
                        }

                        if (fileEntry.StartsWith(RegexPrefix))
                        {
                            var rgx = new Regex(fileEntry[RegexPrefix.Length..], RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                        }
                        else
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace('\\', '/'));
                        }
                    }

                    Console.WriteLine("Using filelist: '{0}'.", fileList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Warning: Unable to load filelist: {0}", ex);
                }
            }

            ContentDownloader.Config.InstallDirectory = GetParameter<string>(args, "-dir");

            ContentDownloader.Config.VerifyAll = HasParameter(args, "-verify-all") || HasParameter(args, "-verify_all") || HasParameter(args, "-validate");

            if (HasParameter(args, "-use-lancache"))
            {
                await Client.DetectLancacheServerAsync();
                if (Client.UseLancacheServer)
                {
                    Console.WriteLine("Detected Lancache server! Downloads will be directed through the Lancache.");

                    // Increasing the number of concurrent downloads when the cache is detected since the downloads will likely
                    // be served much faster than over the internet.  Steam internally has this behavior as well.
                    if (!HasParameter(args, "-max-downloads"))
                    {
                        ContentDownloader.Config.MaxDownloads = 25;
                    }
                }
            }

            ContentDownloader.Config.MaxDownloads = GetParameter(args, "-max-downloads", 8);
            ContentDownloader.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            var appId = GetParameter(args, "-app", ContentDownloader.INVALID_APP_ID);
            if (appId == ContentDownloader.INVALID_APP_ID)
            {
                Console.WriteLine("Error: -app not specified!");
                return 1;
            }

            var pubFile = GetParameter(args, "-pubfile", ContentDownloader.INVALID_MANIFEST_ID);
            var ugcId = GetParameter(args, "-ugc", ContentDownloader.INVALID_MANIFEST_ID);
            if (pubFile != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region Pubfile Downloading

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadPubfileAsync(appId, pubFile).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
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

                #endregion
            }
            else if (ugcId != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region UGC Downloading

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadUGCAsync(appId, ugcId).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
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

                #endregion
            }
            else
            {
                #region App downloading

                var branch = GetParameter<string>(args, "-branch") ?? GetParameter<string>(args, "-beta") ?? ContentDownloader.DEFAULT_BRANCH;
                ContentDownloader.Config.BetaPassword = GetParameter<string>(args, "-branchpassword") ?? GetParameter<string>(args, "-betapassword");

                if (!string.IsNullOrEmpty(ContentDownloader.Config.BetaPassword) && string.IsNullOrEmpty(branch))
                {
                    Console.WriteLine("Error: Cannot specify -branchpassword when -branch is not specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");

                var os = GetParameter<string>(args, "-os");

                if (ContentDownloader.Config.DownloadAllPlatforms && !string.IsNullOrEmpty(os))
                {
                    Console.WriteLine("Error: Cannot specify -os when -all-platforms is specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllArchs = HasParameter(args, "-all-archs");

                var arch = GetParameter<string>(args, "-osarch");

                if (ContentDownloader.Config.DownloadAllArchs && !string.IsNullOrEmpty(arch))
                {
                    Console.WriteLine("Error: Cannot specify -osarch when -all-archs is specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllLanguages = HasParameter(args, "-all-languages");
                var language = GetParameter<string>(args, "-language");

                if (ContentDownloader.Config.DownloadAllLanguages && !string.IsNullOrEmpty(language))
                {
                    Console.WriteLine("Error: Cannot specify -language when -all-languages is specified.");
                    return 1;
                }

                var lv = HasParameter(args, "-lowviolence");

                var depotManifestIds = new List<(uint, ulong)>();
                var isUGC = false;

                var depotIdList = GetParameterList<uint>(args, "-depot");
                var manifestIdList = GetParameterList<ulong>(args, "-manifest");
                if (manifestIdList.Count > 0)
                {
                    if (depotIdList.Count != manifestIdList.Count)
                    {
                        Console.WriteLine("Error: -manifest requires one id for every -depot specified");
                        return 1;
                    }

                    var zippedDepotManifest = depotIdList.Zip(manifestIdList, (depotId, manifestId) => (depotId, manifestId));
                    depotManifestIds.AddRange(zippedDepotManifest);
                }
                else
                {
                    depotManifestIds.AddRange(depotIdList.Select(depotId => (depotId, ContentDownloader.INVALID_MANIFEST_ID)));
                }

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadAppAsync(appId, depotManifestIds, branch, os, arch, language, lv, isUGC).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
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

                #endregion
            }

            return 0;
        }

        static bool InitializeSteam(string username, string password)
        {
            if (!ContentDownloader.Config.UseQrCode)
            {
                if (username != null && password == null && (!ContentDownloader.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username)))
                {
                    if (AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
                    {
                        Console.WriteLine($"Account \"{username}\" has stored credentials. Did you forget to specify -remember-password?");
                    }

                    do
                    {
                        Console.Write("Enter account password for \"{0}\": ", username);
                        if (Console.IsInputRedirected)
                        {
                            password = Console.ReadLine();
                        }
                        else
                        {
                            // Avoid console echoing of password
                            password = Util.ReadPassword();
                        }

                        Console.WriteLine();
                    } while (string.Empty == password);
                }
                else if (username == null)
                {
                    Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
                }
            }

            if (!string.IsNullOrEmpty(password))
            {
                const int MAX_PASSWORD_SIZE = 64;

                if (password.Length > MAX_PASSWORD_SIZE)
                {
                    Console.Error.WriteLine($"Warning: Password is longer than {MAX_PASSWORD_SIZE} characters, which is not supported by Steam.");
                }

                if (!password.All(char.IsAscii))
                {
                    Console.Error.WriteLine("Warning: Password contains non-ASCII characters, which is not supported by Steam.");
                }
            }

            return ContentDownloader.InitializeSteam3(username, password);
        }

        static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                {
                    consumedArgs[x] = true;
                    return x;
                }
            }

            return -1;
        }

        static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        static T GetParameter<T>(string[] args, string param, T defaultValue = default)
        {
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                consumedArgs[index + 1] = true;
                return (T)converter.ConvertFromString(strParam);
            }

            return default;
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return list;

            index++;

            while (index < args.Length)
            {
                var strParam = args[index];

                if (strParam[0] == '-') break;

                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    consumedArgs[index] = true;
                    list.Add((T)converter.ConvertFromString(strParam));
                }

                index++;
            }

            return list;
        }

        static void PrintUnconsumedArgs(string[] args)
        {
            var printError = false;

            for (var index = 0; index < consumedArgs.Length; index++)
            {
                if (!consumedArgs[index])
                {
                    printError = true;
                    Console.Error.WriteLine($"Argument #{index + 1} {args[index]} was not used.");
                }
            }

            if (printError)
            {
                Console.Error.WriteLine("Make sure you specified the arguments correctly. Check --help for correct arguments.");
                Console.Error.WriteLine();
            }
        }

        static void PrintUsage()
        {
            // Do not use tabs to align parameters here because tab size may differ
            Console.WriteLine();
            Console.WriteLine("Usage: downloading one or all depots for an app:");
            Console.WriteLine("       depotdownloader -app <id> [-depot <id> [-manifest <id>]]");
            Console.WriteLine("                       [-username <username> [-password <password>]] [other options]");
            Console.WriteLine();
            Console.WriteLine("Usage: downloading a workshop item using pubfile id");
            Console.WriteLine("       depotdownloader -app <id> -pubfile <id> [-username <username> [-password <password>]]");
            Console.WriteLine("Usage: downloading a workshop item using ugc id");
            Console.WriteLine("       depotdownloader -app <id> -ugc <id> [-username <username> [-password <password>]]");
            Console.WriteLine();
            Console.WriteLine("Parameters:");
            Console.WriteLine("  -app <#>                 - the AppID to download.");
            Console.WriteLine("  -depot <#>               - the DepotID to download.");
            Console.WriteLine("  -manifest <id>           - manifest id of content to download (requires -depot, default: current for branch).");
            Console.WriteLine($"  -branch <branchname>    - download from specified branch if available (default: {ContentDownloader.DEFAULT_BRANCH}).");
            Console.WriteLine("  -branchpassword <pass>   - branch password if applicable.");
            Console.WriteLine("  -all-platforms           - downloads all platform-specific depots when -app is used.");
            Console.WriteLine("  -all-archs               - download all architecture-specific depots when -app is used.");
            Console.WriteLine("  -os <os>                 - the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)");
            Console.WriteLine("  -osarch <arch>           - the architecture for which to download the game (32 or 64, default: the host's architecture)");
            Console.WriteLine("  -all-languages           - download all language-specific depots when -app is used.");
            Console.WriteLine("  -language <lang>         - the language for which to download the game (default: english)");
            Console.WriteLine("  -lowviolence             - download low violence depots when -app is used.");
            Console.WriteLine();
            Console.WriteLine("  -ugc <#>                 - the UGC ID to download.");
            Console.WriteLine("  -pubfile <#>             - the PublishedFileId to download. (Will automatically resolve to UGC id)");
            Console.WriteLine();
            Console.WriteLine("  -username <user>         - the username of the account to login to for restricted content.");
            Console.WriteLine("  -password <pass>         - the password of the account to login to for restricted content.");
            Console.WriteLine("  -remember-password       - if set, remember the password for subsequent logins of this user.");
            Console.WriteLine("                             use -username <username> -remember-password as login credentials.");
            Console.WriteLine("  -qr                      - display a login QR code to be scanned with the Steam mobile app");
            Console.WriteLine("  -no-mobile               - prefer entering a 2FA code instead of prompting to accept in the Steam mobile app");
            Console.WriteLine();
            Console.WriteLine("  -dir <installdir>        - the directory in which to place downloaded files.");
            Console.WriteLine("  -filelist <file.txt>     - the name of a local file that contains a list of files to download (from the manifest).");
            Console.WriteLine("                             prefix file path with `regex:` if you want to match with regex. each file path should be on their own line.");
            Console.WriteLine();
            Console.WriteLine("  -validate                - include checksum verification of files already downloaded");
            Console.WriteLine("  -manifest-only           - downloads a human readable manifest for any depots that would be downloaded.");
            Console.WriteLine("  -cellid <#>              - the overridden CellID of the content server to download from.");
            Console.WriteLine("  -max-downloads <#>       - maximum number of chunks to download concurrently. (default: 8).");
            Console.WriteLine("  -loginid <#>             - a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.");
            Console.WriteLine("  -use-lancache            - forces downloads over the local network via a Lancache instance.");
            Console.WriteLine();
            Console.WriteLine("  -debug                   - enable verbose debug logging.");
            Console.WriteLine("  -V or --version          - print version and runtime.");
        }

        static void PrintVersion(bool printExtra = false)
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Console.WriteLine($"DepotDownloader v{version}");

            if (!printExtra)
            {
                return;
            }

            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        }
    }
}
