using System.Text;
using EasyLive.Core;

namespace EasyLive.Core.Tests;

public class KeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EasyLiveTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sub", "keys.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Normalize_trims_whitespace_and_newlines() =>
        Assert.Equal("abcd-1234-efgh-5678", KeyValidator.Normalize("  abcd-1234-efgh-5678\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_empty_returns_null(string? raw) => Assert.Null(KeyValidator.Normalize(raw!));

    [Fact]
    public void Normalize_inner_space_returns_null() => Assert.Null(KeyValidator.Normalize("hello world"));

    [Fact]
    public void Normalize_rejects_control_characters() => Assert.Null(KeyValidator.Normalize("abcd\u0001efgh"));

    [Theory]
    [InlineData("abc")]
    public void Normalize_too_short_returns_null(string raw) => Assert.Null(KeyValidator.Normalize(raw));

    [Fact]
    public void Normalize_too_long_returns_null() => Assert.Null(KeyValidator.Normalize(new string('a', 201)));

    [Fact]
    public void Normalize_full_url_extracts_key() =>
        Assert.Equal("abcd-1234", KeyValidator.Normalize("rtmps://a.rtmps.youtube.com:443/live2/abcd-1234"));

    [Fact]
    public void Normalize_full_url_with_trailing_slash_extracts_key() =>
        Assert.Equal("abcd-1234", KeyValidator.Normalize("rtmp://host/live2/abcd-1234/"));

    [Theory]
    [InlineData("FB-123456789-0-AbCdEfGhIjKl")]
    [InlineData("abcd-1234-efgh-5678-ijkl")]
    public void Normalize_accepts_real_key_shapes(string key) => Assert.Equal(key, KeyValidator.Normalize(key));

    [Fact]
    public void Ingest_urls_and_hosts()
    {
        Assert.Equal("rtmps://a.rtmps.youtube.com:443/live2", Platforms.IngestUrl(Platform.YouTube));
        Assert.Equal("rtmps://live-api-s.facebook.com:443/rtmp/", Platforms.IngestUrl(Platform.Facebook));
        Assert.Equal("a.rtmps.youtube.com", Platforms.IngestHost(Platform.YouTube));
        Assert.Equal("live-api-s.facebook.com", Platforms.IngestHost(Platform.Facebook));
    }

    [Fact]
    public void Save_then_load_round_trips_per_platform()
    {
        var store = new KeyStore(FilePath);
        store.Save(Platform.YouTube, "abcd-1234-efgh-5678");
        store.Save(Platform.Facebook, "FB-1-0-XyZ");
        Assert.Equal("abcd-1234-efgh-5678", store.Load(Platform.YouTube));
        Assert.Equal("FB-1-0-XyZ", new KeyStore(FilePath).Load(Platform.Facebook));
    }

    [Fact]
    public void Save_overwrites()
    {
        var store = new KeyStore(FilePath);
        store.Save(Platform.YouTube, "first-key-1");
        store.Save(Platform.YouTube, "second-key-2");
        Assert.Equal("second-key-2", store.Load(Platform.YouTube));
    }

    [Fact]
    public void File_does_not_contain_plain_key()
    {
        const string key = "abcd-1234-efgh-5678";
        new KeyStore(FilePath).Save(Platform.YouTube, key);
        var bytes = File.ReadAllBytes(FilePath);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(key, text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)), text);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(key)));
    }

    [Fact]
    public void Load_missing_platform_returns_null()
    {
        var store = new KeyStore(FilePath);
        Assert.Null(store.Load(Platform.YouTube));
        store.Save(Platform.Facebook, "FB-1-0-XyZ");
        Assert.Null(store.Load(Platform.YouTube));
    }

    [Fact]
    public void Clear_removes_only_that_entry()
    {
        var store = new KeyStore(FilePath);
        store.Save(Platform.YouTube, "abcd-1234");
        store.Save(Platform.Facebook, "FB-1-0-XyZ");
        store.Clear(Platform.YouTube);
        Assert.Null(store.Load(Platform.YouTube));
        Assert.Equal("FB-1-0-XyZ", store.Load(Platform.Facebook));
    }

    [Fact]
    public void Clear_without_file_does_not_throw() => new KeyStore(FilePath).Clear(Platform.YouTube);

    [Fact]
    public void Corrupt_file_loads_null_and_save_recovers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json !!");
        var store = new KeyStore(FilePath);
        Assert.Null(store.Load(Platform.YouTube));
        store.Save(Platform.YouTube, "abcd-1234");
        Assert.Equal("abcd-1234", store.Load(Platform.YouTube));
    }

    [Fact]
    public void Undecryptable_entry_loads_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var garbage = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        File.WriteAllText(FilePath, $"{{\"YouTube\":\"{garbage}\",\"Facebook\":\"%%notbase64%%\"}}");
        var store = new KeyStore(FilePath);
        Assert.Null(store.Load(Platform.YouTube));
        Assert.Null(store.Load(Platform.Facebook));
    }
}
