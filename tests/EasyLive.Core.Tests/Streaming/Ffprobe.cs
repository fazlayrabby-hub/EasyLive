using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EasyLive.Core.Tests.Streaming;

public sealed record ProbeStream(int Index, string CodecType, string CodecName, string? Profile, int Width, int Height,
    string? AvgFrameRate, int SampleRate, int Channels);

public sealed record ProbePacket(int StreamIndex, string CodecType, double Time, int Size, bool Key);

public sealed record ProbeFrame(string MediaType, double Time, int Width, int Height);

/// <summary>Reads what arrived at the sink with ffprobe (stream info + packet list, optionally decoded frames).</summary>
public sealed class ProbeResult
{
    public required List<ProbeStream> Streams { get; init; }
    public required List<ProbePacket> Packets { get; init; }
    public required List<ProbeFrame> Frames { get; init; }
    public required string Raw { get; init; }
    public required string Errors { get; init; }

    public ProbeStream? Video => Streams.FirstOrDefault(s => s.CodecType == "video");
    public ProbeStream? Audio => Streams.FirstOrDefault(s => s.CodecType == "audio");
    public List<ProbePacket> VideoPackets => Packets.Where(p => p.CodecType == "video").OrderBy(p => p.Time).ToList();
    public List<ProbePacket> AudioPackets => Packets.Where(p => p.CodecType == "audio").OrderBy(p => p.Time).ToList();

    public static double MaxGap(List<ProbePacket> ps) =>
        ps.Count < 2 ? double.PositiveInfinity : ps.Zip(ps.Skip(1), (a, b) => b.Time - a.Time).Max();

    public static double AverageFps(List<ProbePacket> v) => v.Count < 2 ? 0 : (v.Count - 1) / (v[^1].Time - v[0].Time);

    public static List<double> KeyframeIntervals(List<ProbePacket> v)
    {
        var keys = v.Where(p => p.Key).Select(p => p.Time).ToList();
        return keys.Zip(keys.Skip(1), (a, b) => b - a).ToList();
    }

    /// <summary>Video kbps over [from, to) in stream time.</summary>
    public static double Kbps(List<ProbePacket> v, double from, double to) =>
        v.Where(p => p.Time >= from && p.Time < to).Sum(p => (double)p.Size) * 8 / 1000 / (to - from);

    public string Summary()
    {
        var v = VideoPackets;
        var a = AudioPackets;
        var s = string.Join("; ", Streams.Select(x => x.CodecType == "video"
            ? $"#{x.Index} video {x.CodecName} {x.Profile} {x.Width}x{x.Height} avg_frame_rate={x.AvgFrameRate}"
            : $"#{x.Index} {x.CodecType} {x.CodecName} {x.Profile} {x.SampleRate} Hz {x.Channels} ch"));
        return $"{s} | video packets={v.Count} span={(v.Count > 1 ? v[^1].Time - v[0].Time : 0):F2}s fps={AverageFps(v):F2} " +
               $"maxGap={MaxGap(v):F3}s keyIntervals=[{string.Join(",", KeyframeIntervals(v).Select(x => x.ToString("F2", CultureInfo.InvariantCulture)))}] " +
               $"| audio packets={a.Count} maxGap={MaxGap(a):F3}s";
    }
}

public static class Ffprobe
{
    /// <summary>Reads <paramref name="seconds"/> of the live stream. MediaMTX only lists a path once both tracks
    /// arrived (~1.5 s after connect), so an early "no stream" is retried a few times.</summary>
    public static async Task<ProbeResult> ReadAsync(string ffprobe, string url, double seconds, bool frames = false)
    {
        ProbeResult r = await ReadOnceAsync(ffprobe, url, seconds, frames);
        for (int attempt = 0; attempt < 6 && r.Packets.Count == 0; attempt++)
        {
            await Task.Delay(500);
            r = await ReadOnceAsync(ffprobe, url, seconds, frames);
        }
        return r;
    }

    private static async Task<ProbeResult> ReadOnceAsync(string ffprobe, string url, double seconds, bool frames)
    {
        string entries = "stream=index,codec_type,codec_name,profile,width,height,avg_frame_rate,sample_rate,channels:" +
                         "packet=stream_index,codec_type,pts_time,dts_time,size,flags" +
                         (frames ? ":frame=media_type,pts_time,best_effort_timestamp_time,width,height" : "");
        string show = frames ? "-show_frames " : "";
        var psi = new ProcessStartInfo(ffprobe,
            $"-v error -rw_timeout 8000000 -read_intervals %+{seconds.ToString(CultureInfo.InvariantCulture)} " +
            $"-show_streams -show_packets {show}-show_entries {entries} -of json \"{url}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); }
        string json = await stdout;
        return Parse(json, await stderr);
    }

    private static ProbeResult Parse(string json, string errors)
    {
        var streams = new List<ProbeStream>();
        var packets = new List<ProbePacket>();
        var frames = new List<ProbeFrame>();
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var ss))
                foreach (var s in ss.EnumerateArray())
                    streams.Add(new ProbeStream(Int(s, "index"), Str(s, "codec_type") ?? "", Str(s, "codec_name") ?? "",
                        Str(s, "profile"), Int(s, "width"), Int(s, "height"), Str(s, "avg_frame_rate"),
                        int.TryParse(Str(s, "sample_rate"), out var sr) ? sr : 0, Int(s, "channels")));
            // With both -show_packets and -show_frames ffprobe emits one "packets_and_frames" array with a "type".
            var items = new List<(JsonElement E, bool IsFrame)>();
            if (doc.RootElement.TryGetProperty("packets", out var ps)) items.AddRange(ps.EnumerateArray().Select(e => (e, false)));
            if (doc.RootElement.TryGetProperty("frames", out var fs)) items.AddRange(fs.EnumerateArray().Select(e => (e, true)));
            if (doc.RootElement.TryGetProperty("packets_and_frames", out var pf))
                items.AddRange(pf.EnumerateArray().Select(e => (e, Str(e, "type") == "frame")));
            foreach (var (e, isFrame) in items)
            {
                if (isFrame)
                    frames.Add(new ProbeFrame(Str(e, "media_type") ?? "", Dbl(e, "best_effort_timestamp_time") ?? Dbl(e, "pts_time") ?? double.NaN,
                        Int(e, "width"), Int(e, "height")));
                else
                    packets.Add(new ProbePacket(Int(e, "stream_index"), Str(e, "codec_type") ?? "",
                        Dbl(e, "dts_time") ?? Dbl(e, "pts_time") ?? double.NaN, int.Parse(Str(e, "size") ?? "0"),
                        (Str(e, "flags") ?? "").Contains('K')));
            }
        }
        return new ProbeResult { Streams = streams, Packets = packets.Where(p => !double.IsNaN(p.Time)).ToList(), Frames = frames, Raw = json, Errors = errors };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static int Int(JsonElement e, string name) => int.TryParse(Str(e, name), out var i) ? i : 0;

    private static double? Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
