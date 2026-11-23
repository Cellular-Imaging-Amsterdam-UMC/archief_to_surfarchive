using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SurfArchive;

internal static class PackageContents
{
    public const string Prefix = "__sda_";
    public const string TextName = Prefix + "contents.txt";
    public const string HtmlName = Prefix + "contents.html";

    public static string? LocalReferencePath(string source, IReadOnlyList<ArchiveEntry> selected) =>
        selected is [{ IsDirectory: true } folder] ? Path.Combine(source, Prefix + folder.Name) : null;

    public static ArchivePart[] ReferenceParts(PackageManifest manifest) =>
        manifest.Parts.Where(part => part.Name is TextName or HtmlName or RetentionMetadata.FileName).ToArray();

    public static void WriteLocalReference(string reference, ArchivePart[] contents,
        Func<string, SafeFileHandle> openVerified, Action<PackageMeter> report, CancellationToken token,
        bool verifyExisting = false)
    {
        report(new(PackageStep.LocalReference, 0, contents.Length, CountItems: true));
        var delays = new[] { 200, 600, 1500 };
        int done = 0;
        if (verifyExisting && Directory.Exists(reference))
        {
            TryLocal("verify the existing local reference", reference, () =>
            {
                PackageFiles.GetRegularAttributes(reference);
                foreach (var part in contents)
                {
                    string path = Path.Combine(reference, part.Name);
                    PackageFiles.GetRegularAttributes(path);
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var input = new HashingFileStream(handle, null, token);
                    input.CopyTo(Stream.Null);
                    CheckMetadata(input, part);
                }
                return true;
            });
            report(new(PackageStep.LocalReference, contents.Length, contents.Length, CountItems: true,
                Finished: true, Message: $"Existing local archive reference verified at {reference}."));
            return;
        }

        TryLocal("create the local reference folder", reference, () =>
        {
            PackageFiles.CreateDirectoryExclusive(reference);
            return true;
        });
        var written = new List<(string Path, FileStream Output)>(contents.Length);
        try
        {
            foreach (var part in contents)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(reference, part.Name);
                using var input = new HashingFileStream(openVerified(part.Name), null, token);
                var output = TryLocal("create the metadata file", path,
                    () => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                written.Add((path, output));
                input.CopyTo(output);
                CheckMetadata(input, part);
                output.Flush(flushToDisk: true);
                report(new(PackageStep.LocalReference, ++done, contents.Length, CountItems: true));
            }
            token.ThrowIfCancellationRequested();
            foreach (var (_, output) in written)
                output.Dispose();
        }
        catch
        {
            foreach (var (path, output) in written)
            {
                CleanUp(output.Dispose, path);
                CleanUp(() => File.Delete(path), path);
            }
            CleanUp(() => Directory.Delete(reference, recursive: false), reference);
            throw;
        }
        report(new(PackageStep.LocalReference, contents.Length, contents.Length, CountItems: true,
            Finished: true, Message: $"Local archive reference copied directly to {reference}."));

        T TryLocal<T>(string operation, string path, Func<T> run)
        {
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { return run(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    int code = error.HResult & 0xFFFF;
                    string message = $"Could not {operation} at {path}. {error.Message}";
                    if (attempt >= delays.Length || code is not (5 or 32 or 33))
                        throw new IOException(message, error);
                    int delay = delays[attempt];
                    report(new(PackageStep.LocalReference, done, contents.Length, CountItems: true,
                        Phase: TransferState.Retrying, Message: $"{message} Retrying in {delay} ms."));
                    token.WaitHandle.WaitOne(delay);
                }
            }
        }

        void CleanUp(Action remove, string path)
        {
            try { remove(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report(new(PackageStep.LocalReference, done, contents.Length, CountItems: true,
                    Message: $"Could not clean up the incomplete reference entry {path}. {error.Message}"));
            }
        }

        static void CheckMetadata(HashingFileStream input, ArchivePart part)
        {
            if (input.ReadBytes != part.Size || !input.Finish().Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The local copy of {part.Name} does not match the verified metadata.");
        }
    }

    public static ArchivePart[] Write(string folder, string name, PackagePlan plan, ArchivePart[] volumes,
        RetentionMetadata retention, Action<PackageMeter> report, CancellationToken token,
        Action<string, SafeFileHandle>? holdVerified = null, string? remotePath = null)
    {
        long done = 0, total = plan.Entries.Count;
        report(new(PackageStep.Index, done, total, CountItems: true));
        string textPath = Path.Combine(folder, TextName);
        string htmlPath = Path.Combine(folder, HtmlName);
        string retentionPath = Path.Combine(folder, RetentionMetadata.FileName);
        using (var textFile = File.Create(textPath))
        using (var htmlFile = File.Create(htmlPath))
        {
            using (var text = new StreamWriter(textFile, new UTF8Encoding(false), leaveOpen: true))
            using (var html = new StreamWriter(htmlFile, new UTF8Encoding(false), leaveOpen: true))
            {
                WriteHeaders(text, html, name, plan, volumes, remotePath);
                WriteRestore(html, volumes);
                WriteRetention(html, retention);
                html.WriteLine("""<section class="card" aria-label="Archive tree"><h2>Folder tree</h2><ul class="tree">""");
                var opened = new Stack<string>();
                foreach (var entry in plan.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string type = entry.IsDirectory ? "Folder" : "File";
                    text.WriteLine($"{type}\t{entry.Size.ToString(CultureInfo.InvariantCulture)}\t{entry.Name}");

                    string path = entry.Name.TrimEnd('/');
                    int slash = path.LastIndexOf('/');
                    string parent = slash < 0 ? "" : path[..slash];
                    while (opened.TryPeek(out string? last) && last != parent)
                    {
                        html.WriteLine("</ul></details></li>");
                        opened.Pop();
                    }
                    string label = Escape(path[(slash + 1)..]);
                    if (entry.IsDirectory)
                    {
                        string expand = opened.Count == 0 ? " open" : "";
                        html.WriteLine($"<li><details{expand}><summary><i class=\"folder\" aria-hidden=\"true\"></i><span>{label}</span></summary><ul>");
                        opened.Push(path);
                    }
                    else
                    {
                        html.WriteLine($"<li class=\"file\"><i class=\"document\" aria-hidden=\"true\"></i><span>{label}</span><small>{TransferJob.FormatBytes(entry.Size)}</small></li>");
                    }
                    report(new(PackageStep.Index, ++done, total, CountItems: true));
                }
                while (opened.TryPop(out _))
                    html.WriteLine("</ul></details></li>");
                html.WriteLine("</ul></section></main></body></html>");
            }
            textFile.Flush(flushToDisk: true);
            htmlFile.Flush(flushToDisk: true);
        }
        token.ThrowIfCancellationRequested();
        using (var file = new FileStream(retentionPath, FileMode.CreateNew, FileAccess.Write))
        {
            file.Write(retention.ToJson());
            file.Flush(flushToDisk: true);
        }
        var buffer = new byte[1 << 20];
        ArchivePart[] result = [Measure(textPath), Measure(htmlPath), Measure(retentionPath)];
        report(new(PackageStep.Index, total, total, CountItems: true, Finished: true));
        return result;

        ArchivePart Measure(string path)
        {
            string filename = Path.GetFileName(path);
            SafeFileHandle? handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                using var input = new HashingFileStream(handle, _ => { }, token);
                while (input.Read(buffer) > 0) { }
                var part = new ArchivePart(filename, input.ReadBytes, input.Finish());
                if (holdVerified is not null)
                {
                    holdVerified(filename, handle);
                    handle = null;
                }
                return part;
            }
            finally { handle?.Dispose(); }
        }
    }

    private static void WriteHeaders(StreamWriter text, StreamWriter html, string name, PackagePlan plan, ArchivePart[] volumes,
        string? remotePath)
    {
        int folders = plan.Entries.Count(entry => entry.IsDirectory);
        int files = plan.Entries.Count - folders;
        text.WriteLine(name);
        if (remotePath is not null)
            text.WriteLine($"SURF Data Archive   {remotePath}");
        text.WriteLine($"{files} files, {folders} folders, {plan.Bytes} source bytes, {volumes.Length} ZIP parts");
        text.WriteLine($"Restore: download all parts to one folder and open {volumes[0].Name} in 7-Zip. Select Extract.");
        text.WriteLine();
        text.WriteLine("Parts in restore order (SHA-256, filename):");
        foreach (var part in volumes)
            text.WriteLine($"{part.Sha256.ToLowerInvariant()}  {part.Name}");
        text.WriteLine();
        text.WriteLine("Type\tBytes\tPath");

        html.WriteLine("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="dark">
            """);
        html.WriteLine($"<title>{Escape(name)} · Archive contents</title>");
        html.WriteLine("""
            <style>
            *{box-sizing:border-box}
            body{margin:0;background:#101720;color:#e7eff7;font:15px/1.55 system-ui,sans-serif}
            main{max-width:1040px;margin:0 auto;padding:clamp(20px,5vw,64px)}
            header{border-bottom:1px solid #2d3c4e;padding-bottom:1.45rem}
            .overline{font-size:.77rem;letter-spacing:.12em;text-transform:uppercase;color:#7ee3c3}
            h1{font-size:clamp(1.65rem,4vw,2.7rem);line-height:1.1;margin:.48rem 0 .9rem;overflow-wrap:anywhere}
            h2{font-size:1.1rem;margin:0 0 .8rem}
            .metrics{display:flex;flex-wrap:wrap;gap:.45rem}
            .metrics span{background:#1c2a39;border:1px solid #34475b;color:#bfcee0;border-radius:100px;padding:.22rem .68rem;font-size:.83rem}
            .card{margin-top:1.05rem;border:1px solid #2d3c4e;border-radius:14px;background:#151f2c;padding:1.15rem}
            .card p{color:#bdcbdb;margin:.5rem 0}.restore li{overflow-wrap:anywhere}
            .restore ol{padding-left:1.65rem}
            .restore ol li+li{margin-top:.25rem}
            .omitted{list-style:none;color:#9ab0c6}
            .card a{color:#96ebd1}
            code{background:#213243;border-radius:5px;padding:.14rem .36rem;color:#96ebd1;overflow-wrap:anywhere}
            .retention dl{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:.3rem 1rem;margin:.65rem 0}
            .retention dt{color:#a8bacf}
            .retention dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}
            .retention input{max-width:100%;margin-top:.3rem}
            ul.tree,.tree ul{padding:0;margin:0;list-style:none}
            .tree ul{margin-left:.79rem;padding-left:.7rem;border-left:1px solid #34465a}
            summary,.file{min-height:2.1rem;display:flex;align-items:center;gap:.67rem;padding:.30rem .53rem;border-radius:6px}
            summary{cursor:pointer;color:#eef5fe;font-weight:600;list-style:none}
            summary::-webkit-details-marker{display:none}
            summary::before{content:"▸";color:#89a0b9;font-size:.81rem;width:.65rem}
            details[open]>summary::before{content:"▾"}
            summary:focus-visible{outline:2px solid #7ee3c3;outline-offset:2px}
            summary:hover,.file:hover{background:#1c2b3b}
            summary span,.file span{min-width:0;overflow-wrap:anywhere}
            .file{padding-left:1.85rem;color:#cbd7e5}
            .file small{color:#9ab0c6;margin-left:auto;white-space:nowrap;font-variant-numeric:tabular-nums}
            i{display:inline-block;flex:none;position:relative}
            .folder{width:1.05rem;height:.69rem;background:#e9b769;border-radius:2px}
            .folder::before{content:"";position:absolute;top:-.15rem;left:1px;width:.48rem;height:.23rem;background:#ffce82;border-radius:2px}
            .document{width:.69rem;height:.86rem;border:1px solid #88a4c2;border-radius:2px}
            .document::after{content:"";position:absolute;left:2px;right:2px;top:.35rem;border-top:1px solid #88a4c2;box-shadow:0 2px #88a4c2}
            </style></head><body><main><header><div class="overline">SURF Data Archive · Contents</div>
            """);
        html.WriteLine($"<h1>{Escape(name)}</h1><div class=\"metrics\"><span>{files:N0} files</span><span>{folders:N0} folders</span><span>{TransferJob.FormatBytes(plan.Bytes)} source data</span><span>{volumes.Length:N0} compressed parts</span></div>");
        if (remotePath is not null)
            html.WriteLine($"<p>SURF Data Archive <code>{Escape(remotePath)}</code></p>");
        html.WriteLine("</header>");
    }

    private static void WriteRestore(StreamWriter html, ArchivePart[] volumes)
    {
        html.WriteLine($$"""
            <section class="card restore"><h2>Extract with 7-Zip on Windows</h2>
            <ol>
              <li>Download the SURF folder so all <strong>{{volumes.Length:N0}} ZIP parts</strong> are together in the same destination folder. Keep the original filenames.</li>
              <li>Right-click <code>{{Escape(volumes[0].Name)}}</code> and choose <strong>7-Zip → Extract files…</strong>. On Windows 11, use <strong>Show more options</strong> if needed. You can also select the first part in 7-Zip File Manager and click <strong>Extract</strong>.</li>
              <li>Choose the destination folder and click <strong>OK</strong>. 7-Zip reads the remaining numbered parts automatically.</li>
            </ol>
            <p><a href="https://www.7-zip.org/">Official 7-Zip website</a></p>
            <details><summary>Archive parts ({{volumes.Length:N0}})</summary>
            <p>See <code>{{TextName}}</code> for the complete list in restore order, with SHA-256 checksums.</p>
            <ol>
            """);
        foreach (var part in volumes.Take(9))
            WritePart(part);
        if (volumes.Length > 10)
            html.WriteLine($"<li class=\"omitted\">… {volumes.Length - 10:N0} more parts …</li>");
        if (volumes.Length > 9)
            WritePart(volumes[^1], volumes.Length);
        html.WriteLine("</ol></details></section>");

        void WritePart(ArchivePart part, int? number = null)
        {
            string attributes = number is { } index ? $" value=\"{index.ToString(CultureInfo.InvariantCulture)}\"" : "";
            html.WriteLine($"<li{attributes}>{Escape(part.Name)} <small>· {TransferJob.FormatBytes(part.Size)}</small></li>");
        }
    }

    private static void WriteRetention(StreamWriter html, RetentionMetadata retention)
    {
        html.WriteLine("""<section class="card retention"><h2>Archive retention</h2>""");
        WriteRetentionValues(html, retention);
        html.WriteLine($$"""
            <p>To change these values, double-click <code>{{RetentionMetadata.FileName}}</code> in the SURF pane of <strong>SurfDataArchiveTransfer</strong>, edit the fields and choose <strong>Save to SURF</strong>. If the file is on tape, the editor opens after it is restored.</p>
            <p>This page starts with the values recorded at archiving. To refresh the display after an edit, download the current file and select it below.</p>
            <label for="retention-file">Load current <code>{{RetentionMetadata.FileName}}</code></label><br>
            <input id="retention-file" type="file" accept=".json,application/json">
            <p id="retention-status" role="status"></p>
            </section>
            <script>
            (() => {
              const input = document.getElementById("retention-file");
              const status = document.getElementById("retention-status");
              const fields = document.querySelectorAll("[data-retention-field]");
              input.addEventListener("change", async () => {
                const file = input.files[0];
                if (!file) return;
                try {
                  if (file.name.toLowerCase() !== "{{RetentionMetadata.FileName}}" || file.size > 256 * 1024)
                    throw new Error("Select the {{RetentionMetadata.FileName}} file from this archive.");
                  const data = JSON.parse((await file.text()).replace(/^\uFEFF/, ""));
                  if (!data || !Number.isInteger(data.minimum_years) || data.minimum_years < 1 || data.minimum_years > 999)
                    throw new Error("This file does not contain a valid retention period.");
                  const values = Array.from(fields, field => {
                    const value = data[field.dataset.retentionField];
                    if (field.dataset.retentionField === "minimum_years") return String(value);
                    if (value != null && typeof value !== "string") throw new Error("The retention fields are invalid.");
                    return value || "—";
                  });
                  fields.forEach((field, index) => { field.textContent = values[index]; });
                  status.textContent = "Showing the selected {{RetentionMetadata.FileName}}.";
                } catch (error) {
                  status.textContent = "Unable to load {{RetentionMetadata.FileName}}. " + error.message;
                } finally {
                  input.value = "";
                }
              });
            })();
            </script>
            """);
    }

    public static void WriteRetentionValues(TextWriter html, RetentionMetadata retention)
    {
        html.WriteLine("<dl>");
        WriteField("Minimum years to keep", "minimum_years", retention.Fields.Years.ToString(CultureInfo.InvariantCulture));
        WriteField("Archived on", "archived_on", retention.ArchivedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WriteField("Keep until", "retain_until", retention.RetainUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WriteField("Project", "project", retention.Fields.Project);
        WriteField("Contact", "contact", retention.Fields.Contact);
        WriteField("Contact email", "contact_email", retention.Fields.ContactEmail);
        WriteField("Notes", "notes", retention.Fields.Notes);
        html.WriteLine("</dl>");

        void WriteField(string label, string key, string? value) =>
            html.WriteLine($"<dt>{label}</dt><dd data-retention-field=\"{key}\">{Escape(string.IsNullOrEmpty(value) ? "—" : value)}</dd>");
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
