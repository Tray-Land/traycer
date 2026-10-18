using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TrayCer.Core.Models;

namespace TrayCer.Core.Discovery;

/// <summary>
/// FlashForge's UDP discovery: any datagram to 8899/19000 (multicast 225.0.0.9) or 48899
/// (broadcast) gets a fixed-layout reply. The 276-byte reply of the 5M family carries the serial
/// number; the 140-byte one of older models only the name.
/// </summary>
public static class FlashForgeDiscovery
{
    public const int ModernReplySize = 276;
    public const int LegacyReplySize = 140;

    private static readonly IPAddress Multicast = IPAddress.Parse("225.0.0.9");
    private static readonly int[] Ports = [8899, 19000, 48899];

    public static DiscoveredPrinter? ParseReply(ReadOnlySpan<byte> reply, string fromHost)
    {
        if (reply.Length >= ModernReplySize)
        {
            string name = CString(reply[..0x84]);
            string serial = CString(reply.Slice(0x92, 128));
            return new DiscoveredPrinter(PrinterKind.FlashForge, fromHost, NullIfEmpty(name), NullIfEmpty(serial), ModernModel(BinaryPrimitives.ReadUInt16BigEndian(reply[0x88..])));
        }

        if (reply.Length >= LegacyReplySize)
        {
            return new DiscoveredPrinter(PrinterKind.FlashForgeLegacy, fromHost, NullIfEmpty(CString(reply[..0x80])));
        }

        return null;
    }

    /// <summary>Asks one host; null if it doesn't answer like a FlashForge.</summary>
    public static async Task<DiscoveredPrinter?> ProbeAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IPAddress? address = await LanAddress.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            return null;
        }

        using UdpClient udp = LanAddress.CreateUdp(address.AddressFamily);
        foreach (int port in Ports)
        {
            await udp.SendAsync(Array.Empty<byte>(), new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<DiscoveredPrinter> replies = await CollectAsync(udp, timeout, stopAfterFirst: true, cancellationToken).ConfigureAwait(false);
        return replies.FirstOrDefault();
    }

    public static async Task<IReadOnlyList<DiscoveredPrinter>> FindAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        using UdpClient udp = LanAddress.CreateUdp(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        try
        {
            udp.JoinMulticastGroup(Multicast);
            await udp.SendAsync(Array.Empty<byte>(), new IPEndPoint(Multicast, 8899), cancellationToken).ConfigureAwait(false);
            await udp.SendAsync(Array.Empty<byte>(), new IPEndPoint(Multicast, 19000), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // No multicast route; broadcast below still reaches the 5M family.
        }

        foreach (IPAddress broadcast in LanAddress.BroadcastAddresses())
        {
            foreach (int port in Ports)
            {
                try
                {
                    await udp.SendAsync(Array.Empty<byte>(), new IPEndPoint(broadcast, port), cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // That interface can't broadcast.
                }
            }
        }

        return await CollectAsync(udp, duration, stopAfterFirst: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DiscoveredPrinter>> CollectAsync(UdpClient udp, TimeSpan duration, bool stopAfterFirst, CancellationToken cancellationToken)
    {
        Dictionary<string, DiscoveredPrinter> found = [];
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(duration);
        try
        {
            while (true)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (ParseReply(result.Buffer, result.RemoteEndPoint.Address.ToString()) is { } printer)
                {
                    found[printer.Host] = printer;
                    if (stopAfterFirst)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listening window closed.
        }
        catch (SocketException)
        {
            // ICMP port unreachable surfaces here on Windows: nothing listening at that host.
        }

        return [.. found.Values];
    }

    private static string? ModernModel(ushort productId) => productId switch
    {
        0x0023 => "Adventurer 5M",
        0x0024 => "Adventurer 5M Pro",
        0x0026 => "AD5X",
        0x0028 => "Creator 5",
        0x0029 => "Creator 5 Pro",
        _ => null,
    };

    private static string CString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
