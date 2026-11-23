# SurfDataArchiveTransfer

Windows x64 file manager for SURF Data Archive. Browse local drives and network shares in **Local / NAS** on the left and your personal SURF archive on the right. Each side has a resizable folder tree and a sortable, filterable file list.

[Quick start for new users](https://cellular-imaging-amsterdam-umc.github.io/archief_to_surfarchive/) · [Illustrated step-by-step guide](https://cellular-imaging-amsterdam-umc.github.io/archief_to_surfarchive/step-by-step.html)

After sign-in, the main window opens centered at about 75% of the available screen width and height. The layout follows that monitor's Windows display scaling. Both panes reopen their last visited folders when available. Otherwise, Local / NAS starts at `L:\` and the SURF pane opens your archive home. The location menu also has shortcuts to `L:\basic` and `L:\Archief\active`.

Right-click a file selection or a folder in either pane to use the same actions as the buttons under that pane. Right-click a folder in the SURF tree and choose **New folder** to create a subfolder there. Archive and editing dialogs follow display scaling and scroll when space is limited.

1. Open **SurfDataArchiveTransfer.exe** and sign in with a SURF Data Archive account. **Remember username** saves the username locally. Enter the password each time you sign in.
2. **Archive to SURF** is the usual upload action. **Upload to SURF** is available for direct copies only when every selected item is a file strictly larger than 100 MB. It always asks for confirmation. Use **Recent** in either pane to reopen a visited folder. You can also enter a folder or UNC path in the Local / NAS path field.
3. Select a file or a folder in the SURF contents list and click **Download**. An entire folder can be downloaded only if it has no subfolders. Folder status reflects the files directly inside it. For files on tape, select **Restore from tape**, or accept the restore prompt when attempting a folder download. **Activity** checks every 30 seconds. When the restore reports **Ready to download**, select the file or folder and click **Download**. The app verifies that each file is on disk before copying it. Stopping the monitor leaves previously submitted restore requests active on SURF.
4. **Activity** shows the action, progress and elapsed time for each row. It shows four rows at a time and scrolls when more are available. SFTP transfers also show their copy time. Byte rates and ETA appear where the app has enough information to calculate them.
5. Select files or folders on either side and use **Remove…** to review the exact location and names. Removal is permanent. Removing a folder also removes its contents. **Cancel** is the default.
6. Open **Logs** in the bottom-right corner for session logs in `%LOCALAPPDATA%\SurfArchive\Logs`. The newest log opens first. Choose an older log from the list on the left. When Windows reports a nonempty directory, the app records what it can see there, including hidden entries and whether each path was found in the initial archive scan.

You can remove unrelated files or folders on either side during a transfer, and create folders on SURF. The app protects paths used by the transfer and local sources reserved by queued jobs. It refreshes the SURF listing when it creates an archive destination in the open folder, and refreshes the visible Local / NAS source after removing archived originals.

Both browsers stay available during transfers, including **Recent**, folder navigation, refresh, filters and previews. A transfer keeps the source files and destination it was started with.

Only direct subfolders of `L:\Archief\Active` are checked in advance. A subfolder is listed when the app can open it, including when it is empty. Deeper folders are opened on demand.

Double-click an HTML file (`.html` or `.htm`) to open it in your browser, or a `.txt` file to open it in Notepad. The app checks the current SURF status, requests a restore from tape if needed, and checks again every 15 seconds until the file is on disk. The preview opens automatically after downloading a temporary local copy. You can cancel the preview in **Activity**. A restore already submitted to SURF may continue. The app checks for previews older than seven days at startup. Edits to a temporary copy stay local. Remote HTML files that depend on other files need those assets downloaded separately.

The app uses SFTP on port 22 and verifies the [host keys published by SURF](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normal copies verify file size before receiving their final names and refuse to overwrite an existing destination. After an interruption, start the direct copy again from the beginning.

## Archive workflow

Select only folders or only files in **Local / NAS**. Use **Archive to SURF** to confirm and queue the selection. Each selected folder becomes a separate queue entry and keeps its name on SURF. Selected files form one archive with the name you enter. Existing destination folders are never overwritten or merged.

The **Queue** above **Activity** saves archive and direct upload jobs locally for each SURF account. **Upload to SURF** adds one job per selected file, even while a transfer is running. After sign-in, waiting jobs start automatically in queue order. Paused jobs stay paused until you use the resume icon. The app scans each selected folder when its job starts, so the archive contains the files found at that point. Selected loose files must still match their listed sizes and modification times.

You can keep browsing and use **Archive to SURF** to add another selection while a scan or transfer runs. **Scan source** is the first step in **Activity**. It counts files and folders, checks staging space and reports elapsed time. The bottom status bar also shows the size of the files found so far. To stop a scan, use **Cancel activity**.

The size shown during a scan is the combined size of the files found so far. Deep folder trees can take time to enumerate on a NAS even when their early files are small.

The queue displays the running job first and lists the other jobs newest first. Waiting jobs still run in the order they were added. Queue progress advances as archive steps complete. The total running time includes waits while the job is active and stays visible after it finishes, including after an app restart. The queue shows up to four rows before it scrolls.

Finished jobs stay visible across restarts. Double-click **Failed** in the queue or in an archive activity row to read the error. Use the trash icon to remove one entry or **Remove all finished** to clear all successful jobs from the queue. Both actions leave the archived data on SURF. Use **Cancel activity** to stop the running job. A job that was running when the app closed appears as **Interrupted**. Its **Resume archive upload** icon uses the saved recovery folder and asks you to confirm the SURF destination. The same icon is available for failed or cancelled jobs with a saved staging path.

1. The app builds a ZIP in parts of at most **200,000,000 bytes**, named `Project.zip.001`, `Project.zip.002`, and so on. Files of at least 8 MiB use parallel DEFLATE with up to eight workers. By default parts go to a staging folder beside the source, with a fallback on `L:\` when another drive has too little space. **Use a temporary staging folder on this PC** defaults to `%LOCALAPPDATA%\SurfArchive\Staging` and lets you choose a local folder. The app remembers the setting and folder across restarts. It calculates the required staging space during **Scan source** and uses the network staging folder on `L:\` when necessary. A local package can still be used for recovery after an interruption.
2. The app uploads each completed part while packing continues. **Upload to SURF** shows **Waiting for parts** if there is nothing ready to send. The final size becomes available once all parts and metadata have been written. Temporary connection failures are retried after 10 seconds, 1 minute, 10 minutes, 30 minutes and then hourly, within a shared 12-hour retry budget per archive job. **Activity** shows the next planned attempt. Keep the app running and signed in for automatic retries. If upload stops, the local packing and verification can still finish so the recovery folder can be used to resume.
3. In a combined read pass, the app verifies the SHA-256 of every ZIP part and compares the SHA-256 of every decoded file with the source. It decodes large files in parallel and hashes their bytes in file order. It uploads `__sda_contents.txt`, `__sda_contents.html`, and `__sda_retention.json` to the same SURF subfolder as the parts. The text and HTML files show the original Local / NAS source paths and the SURF destination. The text file includes restoration instructions and part checksums. The HTML file is a self-contained, expandable folder tree.
4. After verifying every SURF SHA-256 checksum, the app copies those three files directly from the verified package into the local reference folder. For example, an archive of `Experiment` leaves `__sda_Experiment` in the same parent. **Remove originals** is on by default. The app rechecks each original before deleting it and removes only directories that are empty. Clear the checkbox to keep the originals alongside the reference. Named data streams, encrypted entries and reparse points stop the job. ZIP preserves file contents, folder structure and modification times within the ZIP time range and precision. Windows permissions are outside its scope.
5. After a successful job the app removes the local package. It can skip reading the data again when a retained read lock and the Windows file ID identify the verified file. Otherwise it checks the file contents before deleting. **Check SURF checksums** stages any parts that have moved to tape and advances after each matching file. Removal and metadata rows count items. Resuming requires a verified `package.json` and leaves the originals in place. When the saved queue item and package identify a folder reference, resuming recreates or verifies it. For a package without a queue entry, right-click **Queue** and choose **Choose recovery folder…**. This also supports older packages. If a remote file has a checksum mismatch, inspect and remove that specific bad copy before retrying.

Set retention in the archive confirmation. `__sda_retention.json` stores `minimum_years` (10 by default), `notes`, `project`, `contact`, and `contact_email`. The app records `archived_on` and calculates `retain_until` from the archive date and minimum years. These fields describe your intended retention period. SURF access and storage settings remain independent of the JSON file.

Double-click `__sda_retention.json` in the SURF pane to edit it. If it is on tape, the app requests a restore and checks every 15 seconds. **Save to SURF** checks the new file and checks for changes to the copy on SURF before replacing it.

`__sda_contents.html` explains how to extract the package. Download all ZIP parts to one Windows folder and open the `.zip.001` in [7-Zip](https://www.7-zip.org/). Choose **Extract**. 7-Zip reads the remaining parts automatically. The short part list shows the first nine and the last part. The full list with checksums is in `__sda_contents.txt`.

Double-click `__sda_contents.html` in the SURF pane to display current retention values from the same folder. This read-only preview waits for `__sda_retention.json` to be staged if it is on tape, with status checks every 15 seconds in **Activity**. A standalone HTML copy shows the values recorded at archiving and can read an updated JSON selected in the browser.

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
