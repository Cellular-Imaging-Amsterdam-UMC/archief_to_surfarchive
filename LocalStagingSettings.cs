using System.Text.Json;

namespace SurfArchive;

internal sealed record LocalStagingSettings(bool Enabled, string Folder)
{
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfArchive", "staging.json");

    public static LocalStagingSettings Read(string? file = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<LocalStagingSettings>(File.ReadAllText(file ?? SettingsFile));
            if (settings is not null && !string.IsNullOrWhiteSpace(settings.Folder))
                return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new(false, PackageFiles.LocalStagingFolder);
    }

    public bool Save(string? file = null)
    {
        file ??= SettingsFile;
        string temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
