using System.IO;
using System.Windows;
using System.Windows.Threading;
using EasyLive.Core;
using EasyLive.Core.Audio;
using EasyLive.Core.Capture;
using EasyLive.Core.Streaming;

namespace EasyLive.App;

public partial class App : Application
{
    private const string InstanceName = "EasyLive";
    private SessionOrchestrator? _orchestrator;
    private D3D11FrameSampler? _sampler;
    private WgcFrameSource? _frames;
    private AudioMixer? _mixer;
    private MainViewModel? _vm;
    private bool _owner;

    private sealed class WpfDispatcher(Dispatcher d) : IUiDispatcher
    {
        public void Post(Action action) => d.BeginInvoke(action);
    }

    /// <summary>Hidden option: <c>--test-url rtmp://host:1935/live</c> streams to that address instead of YouTube/Facebook.</summary>
    internal static string? ParseTestUrl(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--test-url", StringComparison.OrdinalIgnoreCase)) return args[i + 1].TrimEnd('/');
        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            MessageBox.Show("Something went wrong in EasyLive. Please close it and open it again.", "EasyLive");
        };

        if (!SingleInstance.TryAcquire(InstanceName))
        {
            MessageBox.Show("EasyLive is already open.", "EasyLive");
            Shutdown();
            return;
        }
        _owner = true;

        var testUrl = ParseTestUrl(e.Args);
        var keys = new KeyStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyLive", "keys.json"));

        _frames = new WgcFrameSource();
        _sampler = new D3D11FrameSampler();
        MainViewModel? vm = null;
        _orchestrator = new SessionOrchestrator(
            new DxgiAdapterSource(), new HttpTransport(), _frames,
            () =>
            {
                _mixer = new AudioMixer(new LoopbackAudioInput(), MicrophoneAudioInput.TryCreate());
                ApplyMute(vm);
                return _mixer;
            },
            () => new FfmpegStreamSession(), keys, _sampler, TimeProvider.System)
        {
            IngestUrlFor = testUrl is null ? Platforms.IngestUrl : (_ => testUrl),
        };

        vm = _vm = new MainViewModel(_orchestrator, keys, new WpfDispatcher(Dispatcher), CaptureTargets.List);
        vm.MuteChanged += () => ApplyMute(vm);
        new MainWindow(vm).Show();
    }

    private void ApplyMute(MainViewModel? vm)
    {
        var m = _mixer;
        if (m is null || vm is null) return;
        try { m.MuteMic = vm.MicMuted; m.MuteSystem = vm.SystemMuted; } catch (ObjectDisposedException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _orchestrator?.StopAsync().Wait(TimeSpan.FromSeconds(15)); } catch { }
        try { _frames?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { _sampler?.Dispose(); } catch { }
        if (_owner) SingleInstance.Release(InstanceName);
        base.OnExit(e);
    }
}

