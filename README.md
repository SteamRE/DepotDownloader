DepotDownloader
===============

Steam depot downloader utilizing the SteamKit2 library. Supports .NET and Mono

```
Usage: depotdownloader <parameters> [optional parameters]

Parameters:
  -app <#>               - the AppID to download.

Optional Parameters:
  -depot <#>             - the DepotID to download.
  -cellid <#>            - the overridden CellID of the content server to download from.
  -username <user>       - the username of the account to login to for restricted content.
  -password <pass>       - the password of the account to login to for restricted content.
  -remember-password     - if set, remember the password for subsequent logins of this user.
  -dir <installdir>      - the directory in which to place downloaded files.
  -os <os>               - the operating system for which to download the game (windows, macos or linux, default: OS the programm is currently running on)
  -filelist <file.txt>   - a list of files to download (from the manifest).
                           Can optionally use regex to download only certain files.
  -all-platforms         - downloads all platform-specific depots when -app is used.
  -manifest-only         - downloads a human readable manifest for any depots that would be downloaded.
  -beta <branchname>     - download from specified branch if available (default: Public).
  -betapassword <pass>   - branch password if applicable.
  -manifest <id>         - manifest id of content to download (requires -depot, default: latest for branch).
  -max-servers <#>       - maximum number of content servers to use. (default: 8).
  -max-downloads <#>     - maximum number of chunks to download concurrently. (default: 4).
```
