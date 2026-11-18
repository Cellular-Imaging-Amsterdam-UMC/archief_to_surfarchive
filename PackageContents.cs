using System.Globalization;
using System.Net;
using System.Text;

namespace SurfArchive;

internal static class PackageContents
{
    public const string TextName = "contents.txt";
    public const string HtmlName = "contents.html";

    public static ArchivePart[] Write(string folder, string name, PackagePlan plan, ArchivePart[] volumes,
        RetentionMetadata retention, Action<PackageMeter> report, CancellationToken token)
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
                WriteHeaders(text, html, name, plan, volumes);
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
            using var input = File.OpenRead(path);
            return new(Path.GetFileName(path), input.Length, PackageFiles.Hash(input, buffer, token));
        }
    }

    private static void WriteHeaders(StreamWriter text, StreamWriter html, string name, PackagePlan plan, ArchivePart[] volumes)
    {
        int folders = plan.Entries.Count(entry => entry.IsDirectory);
        int files = plan.Entries.Count - folders;
        text.WriteLine(name);
        text.WriteLine($"{files} files, {folders} folders, {plan.Bytes} source bytes, {volumes.Length} compressed TAR parts");
        text.WriteLine("Restore: download all numbered .tar.gz parts, concatenate them in numeric order and extract the combined stream.");
        text.WriteLine("From the package directory on Linux or Git Bash: cat -- ./*.tar.gz | tar -xzf -");
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
            .restore h3{font-size:.98rem;margin:1rem 0 .35rem}
            .restore ol{padding-left:1.65rem}
            .restore ol li+li{margin-top:.25rem}
            .omitted{list-style:none;color:#9ab0c6}
            .card a{color:#96ebd1}
            code{background:#213243;border-radius:5px;padding:.14rem .36rem;color:#96ebd1;overflow-wrap:anywhere}
            pre{max-width:100%;overflow:auto;background:#101720;border-radius:6px;padding:.75rem;font-size:.84rem}
            pre code{background:none;padding:0;overflow-wrap:normal}
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
        html.WriteLine($"<h1>{Escape(name)}</h1><div class=\"metrics\"><span>{files:N0} files</span><span>{folders:N0} folders</span><span>{TransferJob.FormatBytes(plan.Bytes)} source data</span><span>{volumes.Length:N0} compressed parts</span></div></header>");
    }

    private static void WriteRestore(StreamWriter html, ArchivePart[] volumes)
    {
        bool multiple = volumes.Length > 1;
        string archive = multiple ? "combined.tar.gz" : volumes[0].Name;
        string extractCommand = $$"""
            & {
                New-Item -ItemType Directory -Path .\Restored -ErrorAction Stop
                tar.exe -xf '.\{{EscapePowerShellString(archive)}}' -C .\Restored
                if ($LASTEXITCODE -ne 0) { throw 'Extraction failed. Check the tar error above.' }
            }
            """;
        html.WriteLine("""<section class="card restore"><h2>Extract on Windows</h2>""");
        if (multiple)
        {
            html.WriteLine($$"""
                <p>Download all <strong>{{volumes.Length:N0}} numbered parts</strong> to one Windows folder. The parts form one TAR archive. Join them in number order before extracting.</p>
                <details><summary>Join the numbered parts in PowerShell</summary>
                <p>Open PowerShell in the download folder, then paste and run the entire block below. It checks that every numbered part is present and creates <code>combined.tar.gz</code>. Allow extra disk space for this file as well as the extracted data.</p>
                <pre><code>{{Escape(JoinCommand(volumes))}}</code></pre>
                </details>
                """);
        }
        else
        {
            html.WriteLine($"<p>Download <code>{Escape(archive)}</code>. It is the complete archive and is ready to extract.</p>");
        }

        html.WriteLine($$"""
            <h3>Windows File Explorer</h3>
            <ol>
              <li>In Windows 11 File Explorer, right-click <code>{{Escape(archive)}}</code> and choose <strong>Extract All…</strong> if available.</li>
              <li>Choose a destination folder and select <strong>Extract</strong>. If this produces a <code>.tar</code> file, use <strong>Extract All…</strong> on that file too.</li>
            </ol>
            <details><summary>Use the built-in Windows tar command</summary>
              <p>If File Explorer has no extraction option, run the following in PowerShell from the download folder. This creates an output folder and extracts both layers in one step.</p>
              <pre><code>{{Escape(extractCommand)}}</code></pre>
              <p><a href="https://learn.microsoft.com/en-us/windows/tar/">Microsoft's Windows tar documentation</a></p>
            </details>
            <h3>7-Zip</h3>
            <ol>
              <li>Right-click <code>{{Escape(archive)}}</code> and choose <strong>7-Zip → Extract Here</strong>. On Windows 11, you may need <strong>Show more options</strong> first. This creates a <code>.tar</code> file.</li>
              <li>Right-click the resulting <code>.tar</code> and choose <strong>7-Zip → Extract files…</strong>. Choose the destination folder and select <strong>OK</strong>. Keep enough free space for the intermediate TAR as well as the extracted data.</li>
            </ol>
            <p><a href="https://www.7-zip.org/">Official 7-Zip website</a></p>
            <details><summary>Archive parts ({{volumes.Length:N0}})</summary>
            <p>See <code>contents.txt</code> for the complete list in restore order, with SHA-256 checksums.</p>
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

    private static string JoinCommand(ArchivePart[] volumes)
    {
        string stem = volumes[0].Name[..^"-000001.tar.gz".Length];
        return $$"""
            & {
                $ErrorActionPreference = 'Stop'
                $folder = (Get-Location).ProviderPath
                $stem = '{{EscapePowerShellString(stem)}}'
                $parts = foreach ($n in 1..{{volumes.Length.ToString(CultureInfo.InvariantCulture)}}) {
                    $item = Join-Path $folder ('{0}-{1:D6}.tar.gz' -f $stem, $n)
                    if (-not [IO.File]::Exists($item)) { throw "Missing archive part: $item" }
                    $item
                }
                $path = Join-Path $folder 'combined.tar.gz'
                $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try {
                    try {
                        foreach ($part in $parts) {
                            $inputFile = [IO.File]::OpenRead($part)
                            try { $inputFile.CopyTo($output) } finally { $inputFile.Dispose() }
                        }
                    } finally { $output.Dispose() }
                } catch {
                    Remove-Item -LiteralPath $path
                    throw
                }
                Write-Output "Ready to extract: $path"
            }
            """;
    }

    private static void WriteRetention(StreamWriter html, RetentionMetadata retention)
    {
        html.WriteLine("""<section class="card retention"><h2>Archive retention</h2>""");
        WriteRetentionValues(html, retention);
        html.WriteLine("""
            <p>To change these values, double-click <code>retention.json</code> in the SURF pane of <strong>SurfDataArchiveTransfer</strong>, edit the fields and choose <strong>Save to SURF</strong>. If the file is on tape, the editor opens after it is restored.</p>
            <p>This page starts with the values recorded at archiving. To refresh the display after an edit, download the current file and select it below.</p>
            <label for="retention-file">Load current <code>retention.json</code></label><br>
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
                  if (file.name.toLowerCase() !== "retention.json" || file.size > 256 * 1024)
                    throw new Error("Select the retention.json file from this archive.");
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
                  status.textContent = "Showing the selected retention.json.";
                } catch (error) {
                  status.textContent = "Unable to load retention.json. " + error.message;
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

    private static string EscapePowerShellString(string value)
    {
        // PowerShell also parses typographic single quotes as string delimiters.
        foreach (char quote in "'\u2018\u2019\u201a\u201b")
            value = value.Replace(quote.ToString(), new string(quote, 2), StringComparison.Ordinal);
        return value;
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
