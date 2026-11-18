# SurfDataArchiveTransfer

Windows x64 file manager for SURF Data Archive. Browse Archief or another Windows drive on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

The Windows pane starts at `L:\`. The location menu also has shortcuts to `L:\basic` and `L:\Archief\active`.

1. Open **SurfDataArchiveTransfer.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. **Archive to SURF** is the usual upload action. **Upload to SURF** is available for direct copies only when every selected item is a file strictly larger than 100 MB. It always asks for confirmation. Use **Recent** in either pane to reopen a visited folder. You can also enter a folder or UNC path in the Windows path field.
3. Select a file or a folder in the SURF contents list and click **Download**. An entire folder can be downloaded only if it has no subfolders. Folder status reflects the files directly inside it. For files on tape, select **Restore from tape**, or accept the restore prompt when attempting a folder download. **Activity** checks every 30 seconds. When the restore reports **Ready to download**, select the file or folder and click **Download**. The app verifies that each file is on disk before copying it. Stopping the monitor leaves previously submitted restore requests active on SURF.
4. **Activity** shows the action, progress and elapsed time for each row. Select a row to see what it measures. SFTP transfers also show their copy time. Byte rates and ETA appear where the app has enough information to calculate them.
5. Select files or folders on either side and use **Remove…** to review the exact location and names. Removal is permanent. Removing a folder also removes its contents. **Cancel** is the default.

Only direct subfolders of `L:\Archief\Active` are checked in advance. A subfolder is listed when the app can open it, including when it is empty. Deeper folders are opened on demand.

Double-click an HTML file (`.html` or `.htm`) to open it in your Windows browser, or a `.txt` file to open it in Notepad. The app checks the current SURF status, requests a restore from tape if needed, and checks again every 15 seconds until the file is on disk. The preview opens automatically after downloading a temporary Windows copy. You can cancel the preview in **Activity**. A restore already submitted to SURF may continue. The app checks for previews older than seven days at startup. Edits to a temporary copy stay local. Remote HTML files that depend on other files need those assets downloaded separately.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the direct copy again from the beginning.

## Archive workflow

Select only folders or only files on Windows. After one confirmation, selected folders are processed one by one, each into a new SURF subfolder with its original name. If you selected files, give the new SURF subfolder a name first. Existing destination folders are never overwritten or merged.

1. The app builds a ZIP in parts of at most **200,000,000 bytes**, named `Project.zip.001`, `Project.zip.002`, and so on. By default parts go to a staging folder beside the source, with a fallback on `L:\` when another drive has too little space. **Use a temporary staging folder on this PC** defaults to `%LOCALAPPDATA%\SurfArchive\Staging` and lets you choose a local folder. The app remembers the setting and folder across restarts. It shows the space needed for the largest selected archive and checks free space again before each package, using the network staging folder on `L:\` when necessary. A local package can still be used for recovery after an interruption.
2. The app uploads each completed part while packing continues. **Upload to SURF** shows **Waiting for parts** if there is nothing ready to send. The final size becomes available once all parts and metadata have been written. Temporary connection failures are retried after 10 seconds, 1 minute, 10 minutes, 30 minutes and then hourly, within a shared 12-hour retry budget per archive job. **Activity** shows the next planned attempt. Keep the app running and signed in for automatic retries. If upload stops, the local packing and verification can still finish so the recovery folder can be used to resume.
3. The app verifies the SHA-256 of every ZIP part and the decoded contents of every file in a combined read pass. It uploads `contents.txt`, `contents.html`, and `retention.json` to the same SURF subfolder as the parts. The text file includes paths, restoration instructions and part checksums. The HTML file is a self-contained, expandable folder tree.
4. The app verifies every SURF SHA-256 checksum before removing any originals. **Remove originals** is on by default. The app rechecks each original before deleting it and removes only directories that are now empty. Clear the checkbox to keep the originals. Named data streams, encrypted entries and reparse points stop the job. ZIP preserves file contents, folder structure and modification times within the ZIP time range and precision. Windows permissions are outside its scope.
5. After a successful job the app removes the local package. It can skip reading the data again when a retained read lock and the Windows file ID identify the verified file. Otherwise it checks the file contents before deleting. **Check SURF checksums** stages any parts that have moved to tape and advances after each matching file. Removal and metadata rows count items. After an interruption, **Resume archive upload…** explains which local recovery folder to select. The folder must contain `package.json`. Resuming supports older packages and never deletes originals. If a remote file has a checksum mismatch, inspect and remove that specific bad copy before retrying.

Set retention in the archive confirmation. `retention.json` stores `minimum_years` (10 by default), `notes`, `project`, `contact`, and `contact_email`. The app records `archived_on` and calculates `retain_until` from the archive date and minimum years. These fields describe your intended retention period. SURF access and storage settings remain independent of the JSON file.

Double-click `retention.json` in the SURF pane to edit it. If it is on tape, the app requests a restore and checks every 15 seconds. **Save to SURF** checks the new file and checks for changes to the copy on SURF before replacing it.

`contents.html` explains how to extract the package. Download all ZIP parts to one Windows folder and open the `.zip.001` in [7-Zip](https://www.7-zip.org/). Choose **Extract**. 7-Zip reads the remaining parts automatically. The short part list shows the first nine and the last part. The full list with checksums is in `contents.txt`.

Double-click `contents.html` in the SURF pane to display current retention values from the same folder. This read-only preview waits for `retention.json` to be staged if it is on tape, with status checks every 15 seconds in **Activity**. A standalone HTML copy shows the values recorded at archiving and can read an updated JSON selected in the browser.

Older gzip packages form one TAR stream. Download and concatenate those parts in numeric order before extracting. On Linux or in Git Bash, run `cat -- ./*.tar.gz | tar -xzf -` from the package folder. For older uncompressed packages with `data.tar.part-*`, use `cat data.tar.part-* | tar -xf -`. The app's 200 MB parts are smaller than [SURF’s preferred 1–200 GB per archive file](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management). For tape status and restore commands, see [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands).

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
