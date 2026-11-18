namespace SurfArchive;

internal sealed class Credentials(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;
}

internal static class LoginSettings
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfArchive");
    private static readonly string UsernameFile = Path.Combine(Folder, "username.txt");

    public static void RemoveLegacyPassword() => File.Delete(Path.Combine(Folder, "account.dpapi"));
    public static string ReadUsername() => File.Exists(UsernameFile) ? File.ReadAllText(UsernameFile).Trim() : "";

    public static void RememberUsername(string? username)
    {
        if (string.IsNullOrEmpty(username))
        {
            File.Delete(UsernameFile);
            return;
        }

        Directory.CreateDirectory(Folder);
        File.WriteAllText(UsernameFile, username);
    }
}
