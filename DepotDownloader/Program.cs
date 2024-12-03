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

namespace DepotDownloader
{
    class Program
    {
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
            ContentDownloader.Config.MaxServers = GetParameter(args, "-max-servers", 20);
            ContentDownloader.Config.MaxDownloads = GetParameter(args, "-max-downloads", 8);
            ContentDownloader.Config.MaxServers = Math.Max(ContentDownloader.Config.MaxServers, ContentDownloader.Config.MaxDownloads);
            ContentDownloader.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            var appIdList = GetParameterList<uint>(args, "-app");
            if (appIdList.Count == 0)
            {
                Console.WriteLine("Error: -app not specified!");
                return 1;
            }


            var pubFileList = GetParameterList<ulong>(args, "-pubfile");
            var ugcIdList = GetParameterList<ulong>(args, "-ugc");

            if (pubFileList.Count > 0)
            {
                if (pubFileList.Count != appIdList.Count)
                {
                    Console.WriteLine("Error: Number of -pubfile arguments does not match number of -app arguments.");
                    return 1;
                }

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        for (var i = 0; i < appIdList.Count; i++)
                        {
                            var appId = appIdList[i];
                            var pubFile = pubFileList[i];

                            await ContentDownloader.DownloadPubfileAsync(appId, pubFile).ConfigureAwait(false);
                        }
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
                        Console.WriteLine("Download failed due to an unhandled exception: {0}", e.Message);
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
            }

            else if (ugcIdList.Count > 0)
            {
                if (ugcIdList.Count != appIdList.Count)
                {
                    Console.WriteLine("Error: Number of -ugc arguments does not match number of -app arguments.");
                    return 1;
                }

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        for (var i = 0; i < appIdList.Count; i++)
                        {
                            var appId = appIdList[i];
                            var ugcId = ugcIdList[i];

                            await ContentDownloader.DownloadUGCAsync(appId, ugcId).ConfigureAwait(false);
                        }
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
                        Console.WriteLine("Download failed due to an unhandled exception: {0}", e.Message);
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
            }

            else
            {
                #region App downloading

                var branch = GetParameter<string>(args, "-branch") ?? GetParameter<string>(args, "-beta") ?? ContentDownloader.DEFAULT_BRANCH;
                ContentDownloader.Config.BetaPassword = GetParameter<string>(args, "-betapassword");

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


                var depotIdList = GetParameterList<uint>(args, "-depot");
                var manifestIdList = GetParameterList<ulong>(args, "-manifest");


                if (InitializeSteam(username, password))
                {
                    try
                    {
                        foreach (var appId in appIdList)

                        {

                            var depotManifestIds = new List<(uint, ulong)>();

                            var isUGC = false;



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



                            await ContentDownloader.DownloadAppAsync(appId, depotManifestIds, branch, os, arch, language, lv, isUGC).ConfigureAwait(false);

                        }
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

        static T GetParameter<T>(string[] args, string param, T defaultValue = default)
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

            return default;
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter == null)
            {
                Console.WriteLine($"Warning: No type converter available for type {typeof(T)}");
                return list;
            }

            int index = 0;
            while (index < args.Length)
            {
                // Find the next occurrence of the parameter
                if (args[index].Equals(param, StringComparison.OrdinalIgnoreCase))
                {
                    index++; // Move to the value(s) after the parameter

                    // Process values following the parameter
                    while (index < args.Length && !args[index].StartsWith("-"))
                    {
                        var strParam = args[index];

                        // Handle space-separated values within a single argument
                        if (strParam.Contains(" "))
                        {
                            var values = strParam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var val in values)
                            {
                                try
                                {
                                    var convertedValue = converter.ConvertFromString(val);
                                    if (convertedValue != null)
                                    {
                                        list.Add((T)convertedValue);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Warning: Unable to convert value '{val}' to type {typeof(T)}. Exception: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            try
                            {
                                var convertedValue = converter.ConvertFromString(strParam);
                                if (convertedValue != null)
                                {
                                    list.Add((T)convertedValue);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Warning: Unable to convert value '{strParam}' to type {typeof(T)}. Exception: {ex.Message}");
                            }
                        }

                        index++;
                    }
                }
                else
                {
                    index++;
                }
            }

            return list;
        }


        static void PrintUsage()
        {
            // Do not use tabs to align parameters here because tab size may differ
            Console.WriteLine();
            Console.WriteLine("Usage: downloading depots for one or more apps:");
            Console.WriteLine("       depotdownloader -app <id(s)> [-depot <id(s)> [-manifest <id(s)>]]");
            Console.WriteLine("                       [-username <username> [-password <password>]] [other options]");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  Single list format:");
            Console.WriteLine("    depotdownloader -app \"10 20 30\" -depot \"11 22 33\" -manifest \"1111 2222 3333\"");
            Console.WriteLine();
            Console.WriteLine("  Group list format:");
            Console.WriteLine("    depotdownloader -app 10 -depot 11 -manifest 1111 -app 20 -depot 22 -manifest 2222");
            Console.WriteLine("                     -app 30 -depot 33 -manifest 3333");
            Console.WriteLine();
            Console.WriteLine("Usage: downloading a workshop item using pubfile id:");
            Console.WriteLine("       depotdownloader -app <id(s)> -pubfile <id(s)> [-username <username> [-password <password>]]");
            Console.WriteLine("Usage: downloading a workshop item using ugc id:");
            Console.WriteLine("       depotdownloader -app <id(s)> -ugc <id(s)> [-username <username> [-password <password>]]");
            Console.WriteLine();
            Console.WriteLine("Parameters:");
            Console.WriteLine("  -app <# or \"# # ...\">      - the AppID(s) to download. Provide multiple IDs separated by spaces.");
            Console.WriteLine("  -depot <# or \"# # ...\">    - the DepotID(s) to download. Must correspond to the provided AppIDs.");
            Console.WriteLine("  -manifest <# or \"# # ...\"> - manifest ID(s) of content to download (requires -depot). Must correspond to the provided DepotIDs.");
            Console.WriteLine($"  -beta <branchname>          - download from specified branch if available (default: {ContentDownloader.DEFAULT_BRANCH}).");
            Console.WriteLine("  -betapassword <pass>        - branch password if applicable.");
            Console.WriteLine("  -all-platforms              - downloads all platform-specific depots when -app is used.");
            Console.WriteLine("  -all-archs                  - download all architecture-specific depots when -app is used.");
            Console.WriteLine("  -os <os>                    - the operating system for which to download the game (windows, macos, or linux).");
            Console.WriteLine("                                (default: OS the program is currently running on)");
            Console.WriteLine("  -osarch <arch>              - the architecture for which to download the game (32 or 64).");
            Console.WriteLine("                                (default: the host's architecture)");
            Console.WriteLine("  -all-languages              - download all language-specific depots when -app is used.");
            Console.WriteLine("  -language <lang>            - the language for which to download the game (default: english)");
            Console.WriteLine("  -lowviolence                - download low violence depots when -app is used.");
            Console.WriteLine();
            Console.WriteLine("  -ugc <# or \"# # ...\">      - the UGC ID(s) to download. Must correspond to the provided AppIDs.");
            Console.WriteLine("  -pubfile <# or \"# # ...\">  - the PublishedFileId(s) to download. Will automatically resolve to UGC IDs.");
            Console.WriteLine();
            Console.WriteLine("  -username <user>            - the username of the account to login to for restricted content.");
            Console.WriteLine("  -password <pass>            - the password of the account to login to for restricted content.");
            Console.WriteLine("  -remember-password          - if set, remember the password for subsequent logins of this user.");
            Console.WriteLine("                                Use -username <username> -remember-password as login credentials.");
            Console.WriteLine();
            Console.WriteLine("  -dir <installdir>           - the directory in which to place downloaded files.");
            Console.WriteLine("  -filelist <file.txt>        - the name of a local file that contains a list of files to download (from the manifest).");
            Console.WriteLine("                                Prefix file path with `regex:` if you want to match with regex.");
            Console.WriteLine("                                Each file path should be on its own line.");
            Console.WriteLine();
            Console.WriteLine("  -validate                   - include checksum verification of files already downloaded.");
            Console.WriteLine("  -manifest-only              - downloads a human-readable manifest for any depots that would be downloaded.");
            Console.WriteLine("  -cellid <#>                 - the overridden CellID of the content server to download from.");
            Console.WriteLine("  -max-servers <#>            - maximum number of content servers to use (default: 20).");
            Console.WriteLine("  -max-downloads <#>          - maximum number of chunks to download concurrently (default: 8).");
            Console.WriteLine("  -loginid <#>                - a unique 32-bit integer Steam LogonID in decimal, required if running");
            Console.WriteLine("                                multiple instances of DepotDownloader concurrently.");
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
