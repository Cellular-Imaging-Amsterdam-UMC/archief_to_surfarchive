using System.Globalization;
using System.Net;
using System.Text;

namespace SurfArchive;

internal static class PackageContents
{
    public const string TextName = "contents.txt";
    public const string HtmlName = "contents.html";

    public static ArchivePart[] Write(string folder, string name, PackagePlan plan, ArchivePart[] volumes,
        Action<PackageMeter> report, CancellationToken token)
    {
        long done = 0, total = plan.Entries.Count;
        report(new(PackageStep.Index, done, total, CountItems: true));
        string textPath = Path.Combine(folder, TextName);
        string htmlPath = Path.Combine(folder, HtmlName);
        using (var textFile = File.Create(textPath))
        using (var htmlFile = File.Create(htmlPath))
        {
            using (var text = new StreamWriter(textFile, new UTF8Encoding(false), leaveOpen: true))
            using (var html = new StreamWriter(htmlFile, new UTF8Encoding(false), leaveOpen: true))
            {
                WriteHeaders(text, html, name, plan, volumes);
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
        var buffer = new byte[1 << 20];
        ArchivePart[] result = [Measure(textPath), Measure(htmlPath)];
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
            .restore p{color:#bdcbdb;margin:.5rem 0}.restore li{overflow-wrap:anywhere}
            code{background:#213243;border-radius:5px;padding:.14rem .36rem;color:#96ebd1;overflow-wrap:anywhere}
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
        html.WriteLine("""
            <section class="card restore"><details><summary>Restore instructions and part order</summary>
            <p>Download all the numbered parts. Combine them in numeric order and extract the TAR. From the package directory on Linux or Git Bash:</p>
            <p><code>cat -- ./*.tar.gz | tar -xzf -</code></p><ol>
            """);
        foreach (var part in volumes)
            html.WriteLine($"<li>{Escape(part.Name)} <small>· {TransferJob.FormatBytes(part.Size)}</small></li>");
        html.WriteLine("</ol></details></section><section class=\"card\" aria-label=\"Archive tree\"><h2>Folder tree</h2><ul class=\"tree\">");
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
