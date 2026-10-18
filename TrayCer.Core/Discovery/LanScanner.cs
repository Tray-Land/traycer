namespace TrayCer.Core.Discovery;

/// <summary>
/// "Find printers": listens for the brands that announce themselves (Bambu, Elegoo, FlashForge)
/// and probes candidate hosts the app found some other way (mDNS), reporting each as it's found.
/// </summary>
public static class LanScanner
{
    public static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(6);

    public static async Task<IReadOnlyList<DiscoveredPrinter>> ScanAsync(
        IAsyncEnumerable<string> candidateHosts,
        Action<DiscoveredPrinter> found,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DiscoveredPrinter> results = new(StringComparer.OrdinalIgnoreCase);
        void Report(DiscoveredPrinter printer)
        {
            string key = $"{printer.Kind}|{printer.Serial ?? printer.Host}";
            bool added;
            lock (results)
            {
                added = results.TryAdd(key, printer);
            }

            if (added)
            {
                found(printer);
            }
        }

        async Task ListenAsync<T>(Task<IReadOnlyList<T>> scan, Func<T, DiscoveredPrinter> map)
        {
            try
            {
                foreach (T item in await scan.ConfigureAwait(false))
                {
                    Report(map(item));
                }
            }
            catch (Exception)
            {
                // That protocol couldn't listen here (port taken, no network); the others carry on.
            }
        }

        async Task ProbeHostsAsync()
        {
            List<Task> probes = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                await foreach (string host in candidateHosts.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(host))
                    {
                        probes.Add(ProbeAsync(host));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop looking for more hosts; finish the probes already running.
            }

            await Task.WhenAll(probes).ConfigureAwait(false);
        }

        async Task ProbeAsync(string host)
        {
            try
            {
                foreach (DiscoveredPrinter printer in (await PrinterProbe.DetectAsync(host, cancellationToken).ConfigureAwait(false)).Take(1))
                {
                    Report(printer);
                }
            }
            catch (Exception)
            {
                // Cancelled, or that host misbehaved; it just isn't listed.
            }
        }

        await Task.WhenAll(
            ListenAsync(BambuDiscovery.FindAsync(ListenWindow, cancellationToken), p => p),
            ListenAsync(SdcpDiscovery.FindAsync(ListenWindow, cancellationToken), d => new DiscoveredPrinter(Models.PrinterKind.Elegoo, d.Host, d.Name, Model: d.MachineName)),
            ListenAsync(FlashForgeDiscovery.FindAsync(ListenWindow, cancellationToken), p => p with { NeedsSecret = p.Kind == Models.PrinterKind.FlashForge }),
            ProbeHostsAsync()).ConfigureAwait(false);

        lock (results)
        {
            return [.. results.Values];
        }
    }
}
