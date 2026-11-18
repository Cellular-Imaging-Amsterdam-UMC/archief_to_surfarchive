# SurfDataArchiveTransfer

Windows x64 file manager for SURF Data Archive. Browse Archief or another Windows drive on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

1. Open **SurfDataArchiveTransfer.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. **Archive to SURF** is the usual upload action. **Upload to SURF** is available for direct copies only when every selected item is a file strictly larger than 100 MB. It always asks for confirmation. Use **Recent** in either pane to reopen a visited folder. You can also enter a folder or UNC path in the Windows path field.
3. For a file on tape, click **Restore from tape**. **Activity** checks SURF every 30 seconds while you are signed in. Once the job reports **Ready to download**, select the file and click **Download**. The download rechecks that the file is on disk (`REG` or `DUL`). Stopping the monitor does not cancel a restore already submitted to SURF. SURF does not publish a fixed restore time or expose a percentage for this workflow.
4. **Activity** shows the stage, units processed, speed, ETA, step time and total time, as applicable. On an SFTP row the step time is the actual copy time. Select a row for its details.
5. Select files or folders on either side and use **Remove…** to review the exact location and names. Removal is permanent. Removing a folder also removes its contents. **Cancel** is the default.

Only direct subfolders of `L:\Archief\Active` are checked in advance. A subfolder is listed when the app can open it, including when it is empty. Deeper folders are opened on demand.

Double-click an HTML file (`.html` or `.htm`) to open it in your Windows browser, or a `.txt` file to open it in Notepad. On SURF, the file must be available on disk. The app opens a temporary Windows copy and checks for previews older than seven days at startup. Edits to a temporary copy stay local. Remote HTML files that depend on other files need those assets downloaded separately.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the direct copy again from the beginning.

## Archive workflow

Select only folders or only files on Windows. After one confirmation, selected folders are processed one by one, each into a new SURF subfolder with its original name. If you selected files, give the new SURF subfolder a name first. Existing destination folders are never overwritten or merged.

1. The app builds a TAR with fast gzip compression. Compressed parts are at most **200,000,000 bytes** each and are named like `Project-000001.tar.gz`. Parts go to a new staging folder beside the selected data. The app checks available space unless the source is on `L:\`. If there is not enough space on another drive, it uses a temporary folder on `L:\`.
2. The app uploads each completed part while packing continues. **Activity** tracks both steps separately. The upload size is estimated until the package is built.
3. The app reads the local archive back, verifies its entries and hashes, and uploads `contents.txt` and `contents.html` to the same SURF subfolder as the parts. The text file includes paths, restoration instructions and part checksums. The HTML file is a self-contained, expandable folder tree.
4. **Remove originals** is on by default. After local verification and upload succeed, the app rechecks matching files before permanently removing them. Clear the checkbox to keep the originals. When removing directories, the app removes only the now empty selected directories. Named data streams, encrypted entries and reparse points stop the job instead of being discarded. TAR preserves file contents, folder structure and modification times. Windows permissions are outside its scope.
5. The app checks the SHA-256 of every uploaded part and contents file before removing the local package. After an interruption, use **Resume archive upload…** if the local package has `package.json`. Resuming supports older 1.3 packages and never deletes originals. If a remote file has a checksum mismatch, inspect and remove that specific bad copy before retrying.

The gzip parts form one TAR stream. Download all parts and concatenate them in numeric order before extracting. On Linux or in Git Bash, run `cat -- ./*.tar.gz | tar -xzf -` from the package folder. A single part can be opened directly if it is the entire package. For older uncompressed packages with `data.tar.part-*`, use `cat data.tar.part-* | tar -xf -`. These 200 MB parts are smaller than [SURF’s preferred 1–200 GB per archive file](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management). For tape status and restore commands, see [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands).

The app stores only the username in `%LOCALAPPDATA%\SurfArchive\username.txt` when you opt in. A successful sign-in with **Remember username** cleared removes the saved username. A legacy password file is removed at startup.

## Build and sign

On Windows with the .NET 10 SDK:

```powershell
dotnet publish -c Release -p:RestoreLockedMode=true -o release
```

The result is the standalone `release\SurfDataArchiveTransfer.exe`, including the runtime and app icon. To sign it using a code signing certificate in your Windows personal store, use [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) from the Windows SDK:

```powershell
signtool sign /fd SHA256 /sha1 YOUR_CERTIFICATE_THUMBPRINT .\release\SurfDataArchiveTransfer.exe
signtool verify /pa /v .\release\SurfDataArchiveTransfer.exe
```

Windows will only report a trusted publisher if it trusts the signing certificate chain. The local test certificate is self-signed.

For diagnostics, use `--probe` with a source path and log path. The app will prompt for sign-in.
