using EasyLive.Core.Streaming;

namespace EasyLive.Core.Tests;

public class ErrorTextsTests
{
    private static void AssertPlain(string text)
    {
        Assert.DoesNotContain("ffmpeg", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error -", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\d", text);
    }

    [Fact]
    public void Refused_tcp_connection_is_could_not_connect()
    {
        var t = ErrorTexts.ForConnectFailure(-10061, new[] { "Connection to tcp://127.0.0.1:1?tcp_nodelay=0 failed: Error number -10061 occurred" });
        Assert.Equal(ErrorTexts.CouldNotConnect, t);
        AssertPlain(t);
    }

    [Fact]
    public void Unresolvable_host_is_could_not_connect()
    {
        Assert.Equal(ErrorTexts.CouldNotConnect,
            ErrorTexts.ForConnectFailure(-5, new[] { "Failed to resolve hostname nowhere.invalid: No such host is known." }));
    }

    [Theory]
    [InlineData("Server error: Publishing is not allowed")]
    [InlineData("Server error: NetStream.Publish.BadName")]
    [InlineData("Server error: Unauthorized")]
    public void Server_publish_errors_are_key_rejected(string line)
    {
        var t = ErrorTexts.ForConnectFailure(-1, new[] { line });
        Assert.Equal(ErrorTexts.KeyRejected, t);
        AssertPlain(t);
        Assert.Contains("key", t);
    }

    [Fact]
    public void Connection_closed_right_after_connect_is_key_rejected()
    {
        Assert.Equal(ErrorTexts.KeyRejected, ErrorTexts.ForConnectFailure(ErrorTexts.AverrorEof, Array.Empty<string>()));
        Assert.Equal(ErrorTexts.KeyRejected, ErrorTexts.ForConnectFailure(-10054, new[] { "some unrelated warning" }));
    }

    [Fact]
    public void Timeout_or_unknown_connect_failure_is_could_not_connect()
    {
        Assert.Equal(ErrorTexts.CouldNotConnect, ErrorTexts.ForConnectFailure(ErrorTexts.AverrorExit, Array.Empty<string>()));
        Assert.Equal(ErrorTexts.CouldNotConnect, ErrorTexts.ForConnectFailure(-10060, Array.Empty<string>()));
    }

    [Fact]
    public void Send_failure_shortly_after_publish_is_key_rejected_later_it_is_lost()
    {
        Assert.Equal(ErrorTexts.KeyRejected, ErrorTexts.ForSendFailure(TimeSpan.FromSeconds(1)));
        Assert.Equal(ErrorTexts.ConnectionLost, ErrorTexts.ForSendFailure(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void All_fixed_texts_are_plain()
    {
        foreach (var t in new[] { ErrorTexts.CouldNotConnect, ErrorTexts.KeyRejected, ErrorTexts.ConnectionLost,
                     ErrorTexts.EncoderFailed, ErrorTexts.NoPicture, ErrorTexts.Unexpected, ErrorTexts.ComponentsMissing })
            AssertPlain(t);
        Assert.Equal("Nothing to stream — the screen did not provide any picture.", ErrorTexts.NoPicture);
    }
}

public class UrlMaskerTests
{
    [Theory]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/FB-1234-0-AbCd", "rtmps://live-api-s.facebook.com:443/rtmp/***", "FB-1234-0-AbCd")]
    [InlineData("rtmp://a.rtmp.youtube.com/live2/abcd-efgh-ijkl", "rtmp://a.rtmp.youtube.com/live2/***", "abcd-efgh-ijkl")]
    [InlineData("rtmp://127.0.0.1:1935/test/key?x=1", "rtmp://127.0.0.1:1935/test/***", "key?x=1")]
    [InlineData("rtmp://host/app", "rtmp://host/app", null)]
    public void Masks_everything_after_the_app_segment(string url, string masked, string? key)
    {
        Assert.Equal(masked, UrlMasker.Mask(url));
        Assert.Equal(key, UrlMasker.KeyPart(url));
    }
}

public class UrlSecretsTests
{
    [Fact]
    public void Facebook_style_key_registers_whole_key_base_key_and_long_query_values_raw_and_decoded()
    {
        var secrets = UrlMasker.SecretParts("rtmps://live-api-s.facebook.com:443/rtmp/FB-123456789-0-AbCdEf?s_bl=1&a=SECRET%2BVALUE");
        Assert.Contains("FB-123456789-0-AbCdEf?s_bl=1&a=SECRET%2BVALUE", secrets);
        Assert.Contains("FB-123456789-0-AbCdEf", secrets);
        Assert.Contains("SECRET%2BVALUE", secrets);
        Assert.Contains("SECRET+VALUE", secrets);              // URL-decoded form
        Assert.DoesNotContain("1", secrets);                     // too short to redact without mangling the log
        Assert.DoesNotContain("rtmp", secrets);
    }

    [Fact]
    public void Url_without_key_has_no_secrets() => Assert.Empty(UrlMasker.SecretParts("rtmp://host/app"));
}

public class KeyframePolicyTests
{
    [Theory]
    [InlineData(1966, 0, false)]     // 59 frames at 30 fps after the last keyframe
    [InlineData(2000, 0, true)]      // the 2 s boundary
    [InlineData(2333, 0, true)]      // ticks were skipped: still force as soon as 2 s passed
    [InlineData(4000, 2000, true)]
    [InlineData(3100, 2033, false)]  // an encoder-made keyframe at 2033 restarts the 2 s
    public void Forces_a_keyframe_once_2s_of_stream_time_passed_since_the_last(long ptsMs, long lastKeyMs, bool expected) =>
        Assert.Equal(expected, KeyframePolicy.ShouldForce(ptsMs, lastKeyMs));

    [Fact]
    public void Forces_when_there_was_no_keyframe_yet() =>
        Assert.True(KeyframePolicy.ShouldForce(0, KeyframePolicy.None));
}

public class FfmpegLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easylive-locator-" + Guid.NewGuid().ToString("N"));
    private const string Marker = "avcodec-62.dll";

    private string MakeBin(string relative)
    {
        var dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Marker), "");
        return Path.GetFullPath(dir);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void App_folder_wins_over_env_and_dev_tools()
    {
        var app = Path.Combine(_root, "app");
        var appFfmpeg = MakeBin("app/ffmpeg");
        var env = MakeBin("env");
        MakeBin("tools/ffmpeg/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1/bin");
        File.WriteAllText(Path.Combine(_root, "EasyLive.sln"), "");

        Assert.Equal(appFfmpeg, FfmpegLocator.Find(app, env, Marker));
    }

    [Fact]
    public void Env_dir_is_second()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        var env = MakeBin("env");
        MakeBin("tools/ffmpeg/x/bin");
        File.WriteAllText(Path.Combine(_root, "EasyLive.sln"), "");

        Assert.Equal(env, FfmpegLocator.Find(app, env, Marker));
    }

    [Fact]
    public void Dev_tools_folder_above_the_app_is_third()
    {
        var app = Path.Combine(_root, "src", "bin", "Debug");
        Directory.CreateDirectory(app);
        var tools = MakeBin("tools/ffmpeg/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1/bin");
        File.WriteAllText(Path.Combine(_root, "EasyLive.sln"), "");

        Assert.Equal(tools, FfmpegLocator.Find(app, Path.Combine(_root, "missing"), Marker));
        Assert.Equal(tools, FfmpegLocator.Find(app, null, Marker));
    }

    [Fact]
    public void Returns_null_when_nothing_has_the_libraries()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));   // folder without DLLs does not count
        Assert.Null(FfmpegLocator.Find(app, null, Marker));
    }
}

public class TimelineTests
{
    private const long F = 10_000_000;   // QPC frequency used in these tests

    [Fact]
    public void Video_tick_times_and_indexes_round_trip()
    {
        var c = new VideoClock(startQpc: 1_000, qpcFrequency: F, fps: 30);
        Assert.Equal(1_000, c.TickQpc(0));
        Assert.Equal(1_000 + F, c.TickQpc(30));
        Assert.Equal(0, c.TickIndexAt(1_000));
        Assert.Equal(29, c.TickIndexAt(1_000 + F - 1));
        Assert.Equal(30, c.TickIndexAt(1_000 + F));
        Assert.Equal(0, c.TickIndexAt(0));            // before start clamps to 0
        Assert.Equal(1000, c.ToMs(1_000 + F));
    }

    [Fact]
    public void Contiguous_audio_is_counted_in_samples()
    {
        var t = new AudioTimeline(startQpc: 0, qpcFrequency: F);
        var p1 = t.Place(chunkQpc: 0, frames: 480);
        Assert.Equal((0L, 0, 0), (p1.Pts, p1.SkipFrames, p1.SilenceFrames));
        var p2 = t.Place(chunkQpc: F / 100 + 300, frames: 480);   // 10 ms later + 30 us jitter
        Assert.Equal((480L, 0, 0), (p2.Pts, p2.SkipFrames, p2.SilenceFrames));
        Assert.Equal(960, t.NextPts);
    }

    [Fact]
    public void A_forward_gap_over_50ms_is_filled_with_silence()
    {
        var t = new AudioTimeline(0, F);
        t.Place(0, 480);
        var p = t.Place(F / 10, 480);   // chunk claims 100 ms, we are at 10 ms -> 90 ms = 4320 frames of silence
        Assert.Equal(480, p.Pts);
        Assert.Equal(4320, p.SilenceFrames);
        Assert.Equal(0, p.SkipFrames);
        Assert.Equal(480 + 4320 + 480, t.NextPts);
    }

    [Fact]
    public void A_backward_jump_over_50ms_drops_the_overlap()
    {
        var t = new AudioTimeline(0, F);
        t.Place(0, 4800 * 2);                 // 200 ms
        var p = t.Place(F / 10, 4800 * 2);    // claims 100 ms: 4800 frames overlap
        Assert.Equal(4800, p.SkipFrames);
        Assert.Equal(0, p.SilenceFrames);
        Assert.Equal(9600, p.Pts);
        Assert.Equal(9600 + 4800, t.NextPts);
    }

    [Fact]
    public void Pad_to_fills_silence_up_to_a_target_and_never_goes_back()
    {
        var t = new AudioTimeline(0, F);
        t.Place(0, 480);
        Assert.Equal(4320, t.PadTo(4800));
        Assert.Equal(4800, t.NextPts);
        Assert.Equal(0, t.PadTo(1000));
        Assert.Equal(4800, t.NextPts);
        var p = t.Place(F / 10, 480);         // the late chunk lands right after the padding
        Assert.Equal((4800L, 0, 0), (p.Pts, p.SkipFrames, p.SilenceFrames));
    }

    [Fact]
    public void Samples_before_the_session_start_are_dropped()
    {
        var t = new AudioTimeline(startQpc: F, qpcFrequency: F);
        var p = t.Place(F - F / 100, 960);   // first 10 ms (480 frames) are before start
        Assert.Equal(480, p.SkipFrames);
        Assert.Equal(0, p.Pts);
        Assert.Equal(480, t.NextPts);
    }
}
