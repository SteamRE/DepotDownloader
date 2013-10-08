DepotDownloader
===============

Steam depot downloader utilizing the SteamKit2 library. Supports .NET and Mono

```
Use: depotdownloader <parameters> [optional parameters]

Parameters:
  -app #               - the AppID to download.

Optional Parameters:
  -depot #             - the DepotID to download.
  -cellid #            - the overridden CellID of the content server to download from.
  -username user       - the username of the account to login to for restricted content.
  -password pass       - the password of the account to login to for restricted content.
  -dir installdir      - the directory in which to place downloaded files.
  -filelist file.txt   - a list of files to download (from the manifest).
                         Can optionally use regex to download only certain files.
  -all-platforms       - downloads all platform-specific depots when -app is used.
  -manifest-only       - downloads a human readable manifest for any depots that would be downloaded.
  -beta branchname     - download from specified branch if available.
  -betapassword pass   - branch password if applicable.
  -manifest manifestid - manifest id of content to download (requires -depot).
```
