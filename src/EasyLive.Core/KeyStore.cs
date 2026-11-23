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
        if (!ReadEntries().TryGetValue(p.ToString(), out var b64)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Different Windows user or damaged entry: treat as no key stored.
            return null;
        }
    }

    public void Clear(Platform p)
    {
        var entries = ReadEntries();
        if (entries.Remove(p.ToString()))
            WriteEntries(entries);
    }

    private Dictionary<string, string> ReadEntries()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            return parsed ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    private void WriteEntries(Dictionary<string, string> entries)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(entries));
    }
}
