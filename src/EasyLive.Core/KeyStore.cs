using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyLive.Core;

/// <summary>Persists stream keys per platform, encrypted with Windows DPAPI (current user).</summary>
public sealed class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EasyLive.v1");

    private readonly string _path;

    public KeyStore(string path) => _path = path;

    public void Save(Platform p, string key)
    {
        var entries = ReadEntries();
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        entries[p.ToString()] = Convert.ToBase64String(cipher);
        WriteEntries(entries);
    }

    public string? Load(Platform p)
    {
        try
        {
            if (!ReadEntries().TryGetValue(p.ToString(), out var b64) || b64 is null) return null;
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Different Windows user, damaged entry or unreadable file: treat as no key stored.
            return null;
        }
    }

    public void Clear(Platform p)
    {
        var entries = ReadEntries();
        if (entries.Remove(p.ToString()))
            WriteEntries(entries);
    }

    private Dictionary<string, string?> ReadEntries()
    {
        // Only a corrupt file is treated as empty; IO errors propagate so a transient
        // read failure can never lead to the other platform's key being overwritten.
        if (!File.Exists(_path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void WriteEntries(Dictionary<string, string?> entries)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, _path, overwrite: true);
    }
}
