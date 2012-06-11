using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DepotDownloader
{
    class DownloadConfig
    {
        public int CellID { get; set; }
        public bool DownloadAllPlatforms { get; set; }
        public bool PreferBetaVersions { get; set; }
        public bool DownloadManifestOnly { get; set; }
        public string InstallDirectory { get; set; }

        public bool UsingFileList { get; set; }
        public List<string> FilesToDownload { get; set; }
        public List<Regex> FilesToDownloadRegex { get; set; }

        public bool UsingExclusionList { get; set; }
    }
}
