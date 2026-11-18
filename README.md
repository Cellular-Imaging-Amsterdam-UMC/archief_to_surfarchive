# SurfDataArchiveTransfer

Windows x64 file manager for the SURF Data Archive. Browse Archief or another Windows drive on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

1. Open **SurfDataArchiveTransfer.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. Choose a destination folder and select one or more files in the source pane. Use **Upload to SURF** or **Download to Windows**. Use **Recent** in either pane to reopen a visited folder. The app keeps separate local histories for Windows and each SURF account. To open a different Windows drive, use the location menu. You can also type a folder or UNC path into the Windows path field and press Enter.
3. For a file on tape, click **Restore from tape**. **Activity** checks SURF every 30 seconds while you are signed in. Once the job reports **Ready to download**, select the file and click **Download**. The download rechecks that the file is on disk (`REG` or `DUL`). Stopping the monitor does not cancel the restore already submitted to SURF. SURF does not publish a fixed restore time or expose a percentage for this workflow.
4. **Activity** shows the stage, units processed, speed, ETA, step time and total time, as applicable. On an SFTP row the step time is the actual copy time. Select a row for its details.
5. Select files or folders on either side and use **Remove…** to review the exact location and names. Removal is permanent. Removing a folder also removes its contents. **Cancel** is the default in the confirmation dialog.

Only direct subfolders of `L:\Archief\Active` are checked in advance. The app tries to list each subfolder and includes it if the listing succeeds, including when it is empty. Deeper folders are opened on demand.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the copy again from the beginning.

Select Windows files or folders and click **Archive to SURF…** to review the selection, local staging location and SURF destination. The app:

1. Builds an uncompressed TAR with numbered parts of at most **200,000,000 bytes**. It uses a new folder next to the selection. It checks available space unless the source is on `L:`. If space is insufficient on another disk, it uses a temporary folder on `L:`.
2. Starts uploading each completed part while packing continues. The Activity panel tracks packing and upload separately and uses an estimated upload size until packing ends.
3. Reads the complete local TAR back and verifies the entries and hashes. After this check and the upload succeed, it rechecks each original file before permanently removing it. It removes only the now empty selected directories. Named data streams, encrypted entries and reparse points stop the job instead of being discarded. TAR preserves file contents, folder structure and modification times. Windows permissions are outside its scope.
4. Compares every uploaded part's SHA-256 on SURF and only then removes the local parts. On an interruption, any parts already uploaded can remain on SURF. If the local package contains `package.json`, use **Resume archive upload…** to continue. Resuming never deletes originals. If a remote part has a checksum mismatch, inspect and remove that specific bad copy before retrying.

The numbered parts form one TAR stream. After downloading all parts into a separate folder, on Linux or in Git Bash extract in part order with `cat data.tar.part-* | tar -xf -`. A single part can be opened directly as TAR. These 200 MB parts are smaller than [SURF's preferred 1–200 GB per archive file](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management). For tape status and restore commands, see [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands).

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

For diagnostics, the test upload is available from PowerShell with a source path and log path. The app still prompts for sign-in.

```powershell
$p = Start-Process .\release\SurfDataArchiveTransfer.exe -ArgumentList '--probe "C:\path\file.lif" "C:\path\probe.log"' -Wait -PassThru
$p.ExitCode
```
