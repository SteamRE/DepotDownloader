# SCPSL.DownloadFiles

Downloads specific assembly references into References path

# Example action

Download one file
```yaml
    - name: Download SCP: SL Files
      uses: killers0992/scpsl.downloadfiles@master
      with:
        branch: 'public'
        filesToDownload: 'Assembly-CSharp.dll'
```

Download multiple files.
```yaml
    - name: Download SCP: SL Files
      uses: killers0992/scpsl.downloadfiles@master
      with:
        branch: 'public'
        filesToDownload: 'Assembly-CSharp.dll,Mirror.dll'
```
