DepotDownloader
===============

Steam depot downloader utilizing the SteamKit2 library. Supports .NET Core 2.0

### Downloading one or all depots for an app
```
dotnet DepotDownloader.dll -app <id> [-depot <id> [-manifest <id>]]
    [-username <username> [-password <password>]] [other options]
```

For example: `dotnet DepotDownloader.dll -app 730 -depot 731 -manifest 7617088375292372759`

### Downloading a workshop item using pubfile id
```
dotnet DepotDownloader.dll -app <id> -pubfile <id> [-username <username> [-password <password>]]
```

For example: `dotnet DepotDownloader.dll -app 730 -pubfile 1885082371`

### Downloading a workshop item using ugc id
```
dotnet DepotDownloader.dll -app <id> -ugc <id> [-username <username> [-password <password>]]
```

For example: `dotnet DepotDownloader.dll -app 730 -ugc 770604181014286929`

## Parameters

Parameter | Description
--------- | -----------
-app \<#>				| the AppID to download.
-depot \<#>				| the DepotID to download.
-manifest \<id>			| manifest id of content to download (requires -depot, default: current for branch).
-ugc \<#>				| the UGC ID to download.
-beta \<branchname>		| download from specified branch if available (default: Public).
-betapassword \<pass>	| branch password if applicable.
-all-platforms			| downloads all platform-specific depots when -app is used.
-os \<os>				| the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)
-osarch \<arch>			| the architecture for which to download the game (32 or 64, default: the host's architecture)
-all-languages			| download all language-specific depots when -app is used.
-language \<lang>		| the language for which to download the game (default: english)
-lowviolence			| download low violence depots when -app is used.
-pubfile \<#>			| the PublishedFileId to download. (Will automatically resolve to UGC id)
-username \<user>		| the username of the account to login to for restricted content.
-password \<pass>		| the password of the account to login to for restricted content.
-remember-password		| if set, remember the password for subsequent logins of this user.
-dir \<installdir>		| the directory in which to place downloaded files.
-filelist \<file.txt>	| a list of files to download (from the manifest). Can optionally use regex to download only certain files.
-validate				| Include checksum verification of files already downloaded
-manifest-only			| downloads a human readable manifest for any depots that would be downloaded.
-cellid \<#>			| the overridden CellID of the content server to download from.
-max-servers \<#>		| maximum number of content servers to use. (default: 8).
-max-downloads \<#>		| maximum number of chunks to download concurrently. (default: 4).
-loginid \<#>			| a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.
