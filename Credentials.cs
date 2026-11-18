using System.Security.Cryptography;
using System.Text.Json;

namespace SurfArchive;

internal sealed class Credentials(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;

    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SurfArchive", "account.dpapi");

    public static Credentials? Load()
    {
        if (!File.Exists(Store))
            return null;
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Credentials>(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(this);
        string temporary = Store + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, Store, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            File.Delete(temporary);
        }
    }

    public static void Forget() => File.Delete(Store);
}
