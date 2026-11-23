using EasyLive.Core.Capture;
using Xunit.Abstractions;

namespace EasyLive.Core.Tests;

internal static class NativeMethodsForTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}

public class WgcFrameSourceManualTests
{
    private readonly ITestOutputHelper _out;
    public WgcFrameSourceManualTests(ITestOutputHelper o) => _out = o;

    [Fact(Skip = "manual: needs desktop")]
    public async Task Captures_primary_monitor_for_3s()
    {
        var targets = CaptureTargets.List();
        foreach (var t in targets) _out.WriteLine($"target: {t.Title} hwnd={t.Hwnd} mon={t.Monitor}");
        var primary = targets.First(t => t.Monitor != 0 && t.Title.EndsWith("primary"));
        var m = System.Text.RegularExpressions.Regex.Match(primary.Title, @"\((\d+)x(\d+)\)");
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);

        int count = 0, bad = 0;
        await using var src = new WgcFrameSource();
        src.FrameArrived += f =>
        {
            Interlocked.Increment(ref count);
            if (f.Texture == 0 || f.Width != w || f.Height != h) Interlocked.Increment(ref bad);
        };
        // WGC only delivers frames when the screen changes; wiggle the (captured) cursor to guarantee activity.
        using var cts = new CancellationTokenSource();
        var wiggle = Task.Run(async () =>
        {
            for (int i = 0; !cts.IsCancellationRequested; i++)
            {
                NativeMethodsForTests.SetCursorPos(200 + (i % 50) * 4, 200 + (i % 30) * 4);
                await Task.Delay(8);
            }
        });
        src.Start(primary);
        await Task.Delay(3000);
        src.Stop();
        cts.Cancel(); await wiggle;

        _out.WriteLine($"frames={count} size={w}x{h} mismatched={bad}");
        Assert.True(count >= 50, $"only {count} frames");
        Assert.Equal(0, bad);
    }
}
