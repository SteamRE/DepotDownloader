using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Hosting;
using DepotDownloader;
using System.ComponentModel.DataAnnotations;

await Host.CreateDefaultBuilder()
    .RunCommandLineApplicationAsync<AppCommand>(args);

[Command(Description = "Downloads SCP: SL assembly files.")]
public class AppCommand
{
    [Required]
    [Option(Description = "Files to download.", LongName = "filesToDownload")]
    public string FilesToDownload { get; set; } = "Assembly-CSharp.dll";

    [Required]
    [Option(Description = "Branch.", LongName = "branch")]
    public string Branch { get; set; } = ContentDownloader.DEFAULT_BRANCH;

    public async Task<int> OnExecute(IConsole console)
    {
        try
        {
            string refPath = Environment.GetEnvironmentVariable("SL_REFERENCES");

            if (!Directory.Exists(refPath))
                Directory.CreateDirectory(refPath);

            AccountSettingsStore.LoadFromFile("account.config");

            ContentDownloader.Config.InstallDirectory = refPath;

            ContentDownloader.Config.MaxServers = 20;
            ContentDownloader.Config.MaxDownloads = 8;

            ContentDownloader.Config.UsingFileList = true;
            ContentDownloader.Config.FilesToDownloadRegex = new();
            ContentDownloader.Config.FilesToDownload = FilesToDownload.Split(",").ToHashSet();

            if (ContentDownloader.InitializeSteam3(null, null))
            {
                Console.WriteLine("Start downloading files...");
                await ContentDownloader.DownloadAppAsync(996560, new List<(uint depotId, ulong manifestId)>(), Branch, "windows", null, null, false, false).ConfigureAwait(false);
                ContentDownloader.ShutdownSteam3();
            }

            Console.WriteLine("Files downloaded!");
            return 0;
        }
        catch (Exception ex)
        {
            console.WriteLine(ex);
            return 1;
        }
    }

}