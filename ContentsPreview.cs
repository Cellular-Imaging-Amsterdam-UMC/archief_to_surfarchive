using System.Net;

namespace SurfArchive;

internal static class ContentsPreview
{
    public static bool IsContents(string name) => name.Equals(PackageContents.HtmlName, StringComparison.OrdinalIgnoreCase);

    public static bool AddRetention(string path, RetentionMetadata? retention, string status, CancellationToken token)
    {
        string staging = Path.Combine(Path.GetDirectoryName(path)!, $"preview-{Guid.NewGuid():N}.html");
        var buffer = new char[16 * 1024];
        try
        {
            using (var original = File.OpenText(path))
            {
                int count = original.ReadBlock(buffer);
                string prefix = new(buffer, 0, count);
                const string start = "<html lang=\"en\"><head>";
                const string heading = "<div class=\"overline\">SURF Data Archive · Contents</div>";
                int head = prefix.IndexOf(start, StringComparison.Ordinal);
                int brand = prefix.IndexOf(heading, StringComparison.Ordinal);
                int headerEnd = prefix.IndexOf("</header>", StringComparison.Ordinal);
                if (head < 0 || prefix[..head].Trim() != "<!doctype html>" || brand <= head || headerEnd <= brand)
                    return false;
                head += start.Length;
                headerEnd += "</header>".Length;

                token.ThrowIfCancellationRequested();
                using var output = new StreamWriter(new FileStream(staging, FileMode.CreateNew, FileAccess.Write));
                output.Write(prefix.AsSpan(0, head));
                // The preview combines an HTML file with current metadata. Keep active content disabled.
                output.WriteLine("""
                    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
                    <style>
                    .retention:not(.current-retention){display:none!important}
                    .current-retention dl{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:.3rem 1rem}
                    .current-retention dt{color:#a8bacf}
                    .current-retention dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}
                    </style>
                    """);
                output.Write(prefix.AsSpan(head, headerEnd - head));
                output.WriteLine("""<section class="card retention current-retention"><h2>Current archive retention</h2>""");
                output.WriteLine($"<p>{WebUtility.HtmlEncode(status)}</p>");
                if (retention is not null)
                {
                    PackageContents.WriteRetentionValues(output, retention);
                    output.WriteLine($$"""
                        <p>To edit these details, double-click <code>{{RetentionMetadata.FileName}}</code> separately in the SURF pane of <strong>SurfDataArchiveTransfer</strong>.</p>
                        """);
                }
                output.WriteLine("</section>");
                output.Write(prefix.AsSpan(headerEnd));
                while ((count = original.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(staging);
        }
    }
}
