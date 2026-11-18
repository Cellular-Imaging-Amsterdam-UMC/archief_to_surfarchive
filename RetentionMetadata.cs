using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SurfArchive;

internal sealed record RetentionFields(int Years = 10, string Notes = "", string Project = "",
    string Contact = "", string ContactEmail = "");

internal sealed record RetentionMetadata(DateOnly? ArchivedOn, RetentionFields Fields)
{
    public const string FileName = "retention.json";
    public const int MaximumYears = 999;
    public const int MaximumFileSize = 256 * 1024;
    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerOptions.Default) { WriteIndented = true };
    private JsonObject? original;

    public static bool IsFileName(string name) => name.Equals(FileName, StringComparison.OrdinalIgnoreCase);
    public DateOnly? RetainUntil => ArchivedOn?.AddYears(Fields.Years);

    public static (RetentionMetadata Metadata, string Sha256) Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumFileSize)
            throw new IOException("The retention file is too large to edit.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject data ||
                data["minimum_years"]?.GetValue<int>() is not int years || years is < 1 or > MaximumYears)
                throw new JsonException("Expected a JSON object with minimum_years between 1 and 999.");
            DateOnly? date = data["archived_on"]?.Deserialize<DateOnly>();
            if (date is { } day && day.Year + years > 9999)
                throw new JsonException("The retention end date is outside the supported range.");
            var fields = new RetentionFields(years, ReadText("notes"), ReadText("project"), ReadText("contact"),
                ReadText("contact_email"));
            return (new RetentionMetadata(date, fields) { original = data }, Convert.ToHexString(SHA256.HashData(bytes)));

            string ReadText(string key) => data[key]?.GetValue<string>() ?? "";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new IOException("Could not read retention.json. " + error.Message, error);
        }
    }

    public byte[] ToJson()
    {
        var data = original is null ? new JsonObject() : (JsonObject)original.DeepClone();
        data["minimum_years"] = Fields.Years;
        data["notes"] = Fields.Notes;
        data["project"] = Fields.Project;
        data["contact"] = Fields.Contact;
        data["contact_email"] = Fields.ContactEmail;
        data["archived_on"] = JsonSerializer.SerializeToNode(ArchivedOn);
        data["retain_until"] = JsonSerializer.SerializeToNode(RetainUntil);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Pretty);
        return bytes.Length <= MaximumFileSize ? bytes : throw new IOException("The retention file is too large to save.");
    }
}
