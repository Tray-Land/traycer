using System.Runtime;
using Windows.Win32;

namespace TrayCer.Services;

/// <summary>
/// Gives memory back to Windows once the app is idle, the state a tray app spends nearly all its
/// time in. An idle tray app allocates almost nothing, so the GC won't run on its own and a closed
/// window's memory would otherwise never be returned.
/// </summary>
internal static class MemoryService
{
    /// <summary>Default wait for WinUI to finish tearing a closed window down.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Further passes while a closed window is still alive. A pass that runs while something still
    /// holds the window only promotes it to gen2, where nothing in an idle app would ever collect
    /// it, and its native XAML tree stays with it. Traced with a weak reference: most closed
    /// flyouts go at the first pass, but some are still held at 35 seconds and go at the next.
    /// Once it's gone the passes stop.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private static int _generation;

    /// <summary>
    /// After <paramref name="delay"/>, collects everything (running finalizers, which free native
    /// resources such as Direct3D devices) and trims the working set. Runs off the UI thread
    /// because some finalizers need to reach it.
    /// </summary>
    /// <param name="closedWindow">A window that just closed: passes repeat, further apart, until it's collected. A newer call takes over.</param>
    public static void ReleaseIdle(TimeSpan? delay = null, object? closedWindow = null)
    {
        WeakReference? window = closedWindow is null ? null : new WeakReference(closedWindow);
        int generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            // Collect too early and the window's objects are still referenced and just get promoted.
            await Task.Delay(delay ?? SettleDelay);
            Collect();

            foreach (TimeSpan retry in RetryDelays)
            {
                if (window is not { IsAlive: true } || generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                await Task.Delay(retry);
                Collect();
            }
        });
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Aggressive mode compacts every generation, including the LOH, and decommits free space
        // instead of keeping it for reuse. The heap is a few MB, so this is cheap.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Most of a WinUI app's working set is native (XAML, composition, font caches) that nothing
        // touches while hidden; it pages back in cheaply from the standby list on the next open.
        PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
    }
}
