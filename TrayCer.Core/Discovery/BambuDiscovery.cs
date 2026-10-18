using System.Net;
using System.Net.Sockets;
using System.Text;
using TrayCer.Core.Drivers.Bambu;
using TrayCer.Core.Models;

namespace TrayCer.Core.Discovery;

/// <summary>
/// Bambu Lab printers announce themselves with SSDP-style NOTIFY datagrams on UDP 2021 every few
/// seconds; Bambu Studio finds them the same way. Listening a few seconds hears every printer.
/// </summary>
public static class BambuDiscovery
{
    public const int NotifyPort = 2021;

    private static readonly Dictionary<string, string> ModelCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BL-P001"] = "X1 Carbon",
        ["BL-P002"] = "X1",
        ["C11"] = "P1P",
        ["C12"] = "P1S",
        ["C13"] = "X1E",
        ["N1"] = "A1 mini",
        ["N2S"] = "A1",
        ["O1D"] = "H2D",
    };

    private const string SearchRequest =
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: urn:bambulab-com:device:3dprinter:1\r\n\r\n";

    public static DiscoveredPrinter? ParseNotify(string text, string fromHost)
    {
        if (!text.Contains("bambulab", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        string? serial = headers.GetValueOrDefault("USN");
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        string host = headers.GetValueOrDefault("Location") is { Length: > 0 } location && IPAddress.TryParse(location, out _) ? location : fromHost;
        string? code = headers.GetValueOrDefault("DevModel.bambu.com");
        string? model = (code is not null ? ModelCodes.GetValueOrDefault(code) : null) ?? BambuParser.ModelFromSerial(serial);
        return new DiscoveredPrinter(PrinterKind.BambuLab, host, headers.GetValueOrDefault("DevName.bambu.com"), serial.Trim(), model);
    }

    /// <summary>Listens for announcements for <paramref name="duration"/>, nudging with an M-SEARCH.</summary>
    public static async Task<IReadOnlyList<DiscoveredPrinter>> FindAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Dictionary<string, DiscoveredPrinter> found = new(StringComparer.OrdinalIgnoreCase);
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(duration);

        async Task ListenAsync(UdpClient udp)
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                    if (ParseNotify(Encoding.UTF8.GetString(result.Buffer), result.RemoteEndPoint.Address.ToString()) is { } printer)
                    {
                        lock (found)
                        {
                            found[printer.Serial!] = printer;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed.
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The socket went away; whatever it heard stands.
            }
        }

        List<Task> listeners = [];
        List<UdpClient> sockets = [];
        try
        {
            // The announcements go to port 2021; share it with Bambu Studio or Orca if they're running.
            UdpClient notify = new(AddressFamily.InterNetwork);
            sockets.Add(notify);
            notify.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            notify.Client.Bind(new IPEndPoint(IPAddress.Any, NotifyPort));
            listeners.Add(ListenAsync(notify));
        }
        catch (SocketException)
        {
            // Port unavailable; the search below may still get answers.
        }

        // Bound up front: receiving on a socket that hasn't sent yet needs a local port.
        UdpClient search = LanAddress.CreateUdp(AddressFamily.InterNetwork);
        search.EnableBroadcast = true;
        search.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        sockets.Add(search);
        listeners.Add(ListenAsync(search));
        try
        {
            byte[] request = Encoding.ASCII.GetBytes(SearchRequest);
            await search.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1990), cancellationToken).ConfigureAwait(false);
            foreach (IPAddress broadcast in LanAddress.BroadcastAddresses())
            {
                await search.SendAsync(request, new IPEndPoint(broadcast, NotifyPort), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            // Listening alone is enough; the printers announce on their own.
        }

        await Task.WhenAll(listeners).ConfigureAwait(false);
        foreach (UdpClient socket in sockets)
        {
            socket.Dispose();
        }

        return [.. found.Values];
    }
}
