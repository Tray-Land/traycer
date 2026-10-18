using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Windows.Devices.Enumeration;

namespace TrayCer.Services;

/// <summary>
/// Finds printer hosts that advertise over mDNS (OctoPrint, Moonraker with zeroconf, UltiMaker)
/// through Windows' own DNS-SD support, so no multicast code or library is needed.
/// </summary>
internal static class DnsSdBrowser
{
    private const string DnsSdProtocol = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";
    private const string HostNameProperty = "System.Devices.Dnssd.HostName";
    private const string PortProperty = "System.Devices.Dnssd.PortNumber";
    private const string AddressProperty = "System.Devices.IpAddress";

    private static readonly string[] Services = ["_octoprint._tcp", "_moonraker._tcp", "_ultimaker._tcp"];

    /// <summary>Hosts as "address" or "address:port" (when the service isn't on port 80), as they're found.</summary>
    public static async IAsyncEnumerable<string> FindHostsAsync(TimeSpan duration, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<string> found = Channel.CreateUnbounded<string>();
        List<DeviceWatcher> watchers = [];
        foreach (string service in Services)
        {
            try
            {
                string aqs = $"System.Devices.AepService.ProtocolId:={DnsSdProtocol} AND System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"{service}\"";
                DeviceWatcher watcher = DeviceInformation.CreateWatcher(aqs, [HostNameProperty, PortProperty, AddressProperty], DeviceInformationKind.AssociationEndpointService);
                watcher.Added += (_, info) =>
                {
                    if (HostOf(info) is { } host)
                    {
                        found.Writer.TryWrite(host);
                    }
                };
                watcher.Start();
                watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                // DNS-SD unavailable (policy, service stopped); the UDP discovery still runs.
                System.Diagnostics.Debug.WriteLine($"DNS-SD browse for {service} failed: {ex.Message}");
            }
        }

        _ = Task.Delay(duration, cancellationToken).ContinueWith(_ =>
        {
            foreach (DeviceWatcher watcher in watchers)
            {
                try
                {
                    watcher.Stop();
                }
                catch (InvalidOperationException)
                {
                    // Already stopped or aborted.
                }
            }

            found.Writer.TryComplete();
        }, TaskScheduler.Default);

        await foreach (string host in found.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            yield return host;
        }
    }

    private static string? HostOf(DeviceInformation info)
    {
        string? address = info.Properties.TryGetValue(AddressProperty, out object? value) && value is string[] addresses
            ? addresses.FirstOrDefault(a => IPAddress.TryParse(a, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            : null;
        address ??= info.Properties.TryGetValue(HostNameProperty, out object? name) ? (name as string)?.TrimEnd('.') : null;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (address.Contains(':', StringComparison.Ordinal))
        {
            address = $"[{address}]";
        }

        int port = info.Properties.TryGetValue(PortProperty, out object? p) && p is ushort number ? number : 80;
        return port == 80 ? address : $"{address}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
