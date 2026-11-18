# SURF Archive

Windows x64 file manager for the SURF Data Archive. Browse Archief or another Windows drive on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

1. Open **SurfArchive.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. Choose a destination folder and select one or more files in the source pane. Use **Upload to SURF** or **Download to Windows**. To open a different Windows drive, use the location menu. You can also type a folder or UNC path into the Windows path field and press Enter.
3. Watch **Transfers** for progress, bytes, speed, estimated time remaining and elapsed time. Select a row for its paths and details. **Cancel transfer** stops the current copy and any remaining items in the selection. Completed copies stay in place.
4. **Archive status** shows the result of `dals -l -q` for the selected SURF files. To stage files that are only on tape, use the SURF `daget` command over SSH and wait for `DUL`.

Only direct subfolders of `L:\Archief\Active` are checked in advance. The app tries to list each subfolder and includes it if the listing succeeds, including when it is empty. Deeper folders are opened on demand.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the copy again from the beginning.

**Test upload** checks SHA-256 and archive status and removes its own temporary file and folder. The transfer details show whether SURF confirmed the removal. See the [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands) and the [command reference](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920963/DAcommands+Documentation).

The username is stored in `%LOCALAPPDATA%\SurfArchive\username.txt` if you opt in. A successful sign-in with **Remember username** cleared removes the saved username. The app removes the previous version’s saved password file at startup.

## Build and sign

On Windows with the .NET 10 SDK, run:

```powershell
dotnet publish -c Release -p:RestoreLockedMode=true -o release
```

The result is the standalone `release\SurfArchive.exe`, including the runtime and the app icon. To sign it using a code signing certificate in your Windows personal store, run [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) from the Windows SDK and supply the certificate thumbprint:

```powershell
signtool sign /fd SHA256 /sha1 YOUR_CERTIFICATE_THUMBPRINT .\release\SurfArchive.exe
signtool verify /pa /v .\release\SurfArchive.exe
```

Windows will only report a trusted publisher if it trusts the signing certificate chain. The local test certificate is self-signed.

To run the same test upload from PowerShell, pass a source path and log path. The app still prompts for sign-in.

```powershell
$p = Start-Process .\release\SurfArchive.exe -ArgumentList '--probe "C:\path\file.lif" "C:\path\probe.log"' -Wait -PassThru
$p.ExitCode
```

Bundling folders into ZIP or tar volumes is planned for a later version. SURF’s preferred range is [1 to 200 GB per archive file](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management).
