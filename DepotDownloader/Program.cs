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

namespace DepotDownloader
{
    class Program
    {
        static int Main(string[] args)
            => MainAsync(args).GetAwaiter().GetResult();

        static async Task<int> MainAsync(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            if (HasParameter(args, "-debug"))
            {
                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();

                DebugLog.WriteLine("DepotDownloader", "Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
                DebugLog.WriteLine("DepotDownloader", "Runtime: {0}", RuntimeInformation.FrameworkDescription);
            }

            var username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
            var password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");
            ContentDownloader.Config.RememberPassword = HasParameter(args, "-remember-password");

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
                try
                {
                    var fileListData = await File.ReadAllTextAsync(fileList);
                    var files = fileListData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

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

                    Console.WriteLine("Using filelist: '{0}'.", fileList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Warning: Unable to load filelist: {0}", ex);
                }
            }

            ContentDownloader.Config.InstallDirectory = GetParameter<string>(args, "-dir");

            ContentDownloader.Config.VerifyAll = HasParameter(args, "-verify-all") || HasParameter(args, "-verify_all") || HasParameter(args, "-validate");
            ContentDownloader.Config.MaxServers = GetParameter(args, "-max-servers", 20);
            ContentDownloader.Config.MaxDownloads = GetParameter(args, "-max-downloads", 8);
            ContentDownloader.Config.MaxServers = Math.Max(ContentDownloader.Config.MaxServers, ContentDownloader.Config.MaxDownloads);
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
                ContentDownloader.Config.BetaPassword = GetParameter<string>(args, "-betapassword");

                ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");
                var os = GetParameter<string>(args, "-os");

                if (ContentDownloader.Config.DownloadAllPlatforms && !String.IsNullOrEmpty(os))
                {
                    Console.WriteLine("Error: Cannot specify -os when -all-platforms is specified.");
                    return 1;
                }

                var arch = GetParameter<string>(args, "-osarch");

                ContentDownloader.Config.DownloadAllLanguages = HasParameter(args, "-all-languages");
                var language = GetParameter<string>(args, "-language");

                if (ContentDownloader.Config.DownloadAllLanguages && !String.IsNullOrEmpty(language))
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
            if (username != null && password == null && (!ContentDownloader.Config.RememberPassword || !AccountSettingsStore.Instance.LoginKeys.ContainsKey(username)))
            {
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
                } while (String.Empty == password);
            }
            else if (username == null)
            {
                Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
            }

            // capture the supplied password in case we need to re-use it after checking the login key
            ContentDownloader.Config.SuppliedPassword = password;

            return ContentDownloader.InitializeSteam3(username, password);
        }

        static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                    return x;
            }

            return -1;
        }

        static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        static T GetParameter<T>(string[] args, string param, T defaultValue = default(T))
        {
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                return (T)converter.ConvertFromString(strParam);
            }

            return default(T);
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
                    list.Add((T)converter.ConvertFromString(strParam));
                }

                index++;
            }

            return list;
        }

        static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("Usage - downloading one or all depots for an app:");
            Console.WriteLine("\tdepotdownloader -app <id> [-depot <id> [-manifest <id>]]");
            Console.WriteLine("\t\t[-username <username> [-password <password>]] [other options]");
            Console.WriteLine();
            Console.WriteLine("Usage - downloading a workshop item using pubfile id");
            Console.WriteLine("\tdepotdownloader -app <id> -pubfile <id> [-username <username> [-password <password>]]");
            Console.WriteLine("Usage - downloading a workshop item using ugc id");
            Console.WriteLine("\tdepotdownloader -app <id> -ugc <id> [-username <username> [-password <password>]]");
            Console.WriteLine();
            Console.WriteLine("Parameters:");
            Console.WriteLine("\t-app <#>\t\t\t\t- the AppID to download.");
            Console.WriteLine("\t-depot <#>\t\t\t\t- the DepotID to download.");
            Console.WriteLine("\t-manifest <id>\t\t\t- manifest id of content to download (requires -depot, default: current for branch).");
            Console.WriteLine("\t-beta <branchname>\t\t\t- download from specified branch if available (default: Public).");
            Console.WriteLine("\t-betapassword <pass>\t\t- branch password if applicable.");
            Console.WriteLine("\t-all-platforms\t\t\t- downloads all platform-specific depots when -app is used.");
            Console.WriteLine("\t-os <os>\t\t\t\t- the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)");
            Console.WriteLine("\t-osarch <arch>\t\t\t\t- the architecture for which to download the game (32 or 64, default: the host's architecture)");
            Console.WriteLine("\t-all-languages\t\t\t\t- download all language-specific depots when -app is used.");
            Console.WriteLine("\t-language <lang>\t\t\t\t- the language for which to download the game (default: english)");
            Console.WriteLine("\t-lowviolence\t\t\t\t- download low violence depots when -app is used.");
            Console.WriteLine();
            Console.WriteLine("\t-ugc <#>\t\t\t\t- the UGC ID to download.");
            Console.WriteLine("\t-pubfile <#>\t\t\t- the PublishedFileId to download. (Will automatically resolve to UGC id)");
            Console.WriteLine();
            Console.WriteLine("\t-username <user>\t\t- the username of the account to login to for restricted content.");
            Console.WriteLine("\t-password <pass>\t\t- the password of the account to login to for restricted content.");
            Console.WriteLine("\t-remember-password\t\t- if set, remember the password for subsequent logins of this user. (Use -username <username> -remember-password as login credentials)");
            Console.WriteLine();
            Console.WriteLine("\t-dir <installdir>\t\t- the directory in which to place downloaded files.");
            Console.WriteLine("\t-filelist <file.txt>\t- a list of files to download (from the manifest). Prefix file path with 'regex:' if you want to match with regex.");
            Console.WriteLine("\t-validate\t\t\t\t- Include checksum verification of files already downloaded");
            Console.WriteLine();
            Console.WriteLine("\t-manifest-only\t\t\t- downloads a human readable manifest for any depots that would be downloaded.");
            Console.WriteLine("\t-cellid <#>\t\t\t\t- the overridden CellID of the content server to download from.");
            Console.WriteLine("\t-max-servers <#>\t\t- maximum number of content servers to use. (default: 20).");
            Console.WriteLine("\t-max-downloads <#>\t\t- maximum number of chunks to download concurrently. (default: 8).");
            Console.WriteLine("\t-loginid <#>\t\t- a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.");
        }
    }
}
