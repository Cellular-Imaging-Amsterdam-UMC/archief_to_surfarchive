# SurfDataArchiveTransfer

Windows x64 file manager for the SURF Data Archive. Browse Archief or another Windows drive on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

1. Open **SurfDataArchiveTransfer.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. Choose a destination folder and select one or more files in the source pane. Use **Upload to SURF** or **Download to Windows**. Use **Recent** in either pane to reopen a visited folder. The app keeps separate local histories for Windows and each SURF account. To open a different Windows drive, use the location menu. You can also type a folder or UNC path into the Windows path field and press Enter.
3. The **Archive status** column shows whether a SURF file is on disk, on tape, queued or restoring. Before a download the app checks the status again. It requests a tape restore with `daget` when needed and waits until SURF confirms the file is on disk (`REG` or `DUL`). SURF does not provide a byte percentage or ETA for the tape stage in this workflow.
4. **Transfers** shows the stage, transferred bytes, copy speed, ETA, copy time and total time. The total includes preparation, waiting for tape and verification as applicable. Cancelling stops the local operation and remaining selections. A recall already submitted to SURF may continue.
5. Select files or folders on either side and use **Remove…** to review the exact location and names. Removal is permanent. Removing a folder also removes its contents. **Cancel** is the default in the confirmation dialog.

Only direct subfolders of `L:\Archief\Active` are checked in advance. The app tries to list each subfolder and includes it if the listing succeeds, including when it is empty. Deeper folders are opened on demand.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the copy again from the beginning.

**Test upload** checks SHA-256 and archive status and removes its own temporary file and folder. The transfer details show whether SURF confirmed the removal. See the [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands) and the [command reference](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920963/DAcommands+Documentation).

The username is stored in `%LOCALAPPDATA%\SurfArchive\username.txt` if you opt in. A successful sign-in with **Remember username** cleared removes the saved username. The app removes the previous version’s saved password file at startup.

## Build and sign

On Windows with the .NET 10 SDK, run:

```powershell
dotnet publish -c Release -p:RestoreLockedMode=true -o release
```

The result is the standalone `release\SurfDataArchiveTransfer.exe`, including the runtime and the app icon. To sign it using a code signing certificate in your Windows personal store, run [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) from the Windows SDK and supply the certificate thumbprint:

```powershell
signtool sign /fd SHA256 /sha1 YOUR_CERTIFICATE_THUMBPRINT .\release\SurfDataArchiveTransfer.exe
signtool verify /pa /v .\release\SurfDataArchiveTransfer.exe
```

Windows will only report a trusted publisher if it trusts the signing certificate chain. The local test certificate is self-signed.

To run the same test upload from PowerShell, pass a source path and log path. The app still prompts for sign-in.

```powershell
$p = Start-Process .\release\SurfDataArchiveTransfer.exe -ArgumentList '--probe "C:\path\file.lif" "C:\path\probe.log"' -Wait -PassThru
$p.ExitCode
```

Bundling folders into ZIP or tar volumes is planned for a later version. SURF’s preferred range is [1 to 200 GB per archive file](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management).
