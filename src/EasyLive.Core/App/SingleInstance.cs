namespace EasyLive.Core;

/// <summary>Allows only one running copy of the app (named mutex, held until <see cref="Release"/> or process exit).</summary>
public static class SingleInstance
{
    private static readonly Dictionary<string, Mutex> Held = new();

    /// <summary>True for the first caller of a name; false while someone (another process or an earlier call) holds it.</summary>
    public static bool TryAcquire(string name)
    {
        var mutex = new Mutex(false, @"Local\" + name, out var createdNew);
        if (!createdNew) { mutex.Dispose(); return false; }
        lock (Held) Held[name] = mutex;
        return true;
    }

    public static void Release(string name)
    {
        Mutex? m;
        lock (Held) { if (!Held.Remove(name, out m)) return; }
        m.Dispose();
    }
}
