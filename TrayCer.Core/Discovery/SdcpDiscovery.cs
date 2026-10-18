using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;

namespace TrayCer.Core.Discovery;

/// <summary>An Elegoo SDCP printer's answer to the "M99999" discovery probe.</summary>
public sealed record SdcpDevice(string Host, string? Name, string? MachineName, string? MainboardId, string? FirmwareVersion);

/// <summary>
/// SDCP discovery: send "M99999" to UDP 3000 (broadcast to find printers, unicast to identify one)
/// and the mainboard answers with its name, model and mainboard ID.
/// </summary>
public static class SdcpDiscovery
{
    public const int Port = 3000;
    private static readonly byte[] Probe = "M99999"u8.ToArray();

    public static SdcpDevice? ParseReply(ReadOnlySpan<byte> data, string fromHost)
    {
        if (JsonRead.Parse(Encoding.UTF8.GetString(data)) is not JsonObject reply || reply.Object("Data") is not { } info)
        {
            return null;
        }

        return new SdcpDevice(
            info.NonEmpty("MainboardIP") ?? fromHost,
            info.NonEmpty("Name"),
            info.NonEmpty("MachineName"),
            info.NonEmpty("MainboardID"),
            info.NonEmpty("FirmwareVersion"));
    }

    /// <summary>Asks one host who it is; null if nothing answers in time.</summary>
    public static async Task<SdcpDevice?> ProbeAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IPAddress? address = await LanAddress.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            return null;
        }

        using UdpClient udp = LanAddress.CreateUdp(address.AddressFamily);
        await udp.SendAsync(Probe, new IPEndPoint(address, Port), cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (ParseReply(result.Buffer, result.RemoteEndPoint.Address.ToString()) is { } device)
                {
                    return device;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Broadcasts the probe on every IPv4 network and collects answers for <paramref name="duration"/>.</summary>
    public static async Task<IReadOnlyList<SdcpDevice>> FindAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        using UdpClient udp = LanAddress.CreateUdp(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        foreach (IPAddress broadcast in LanAddress.BroadcastAddresses())
        {
            try
            {
                await udp.SendAsync(Probe, new IPEndPoint(broadcast, Port), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // That interface can't broadcast; the others still might.
            }
        }

        Dictionary<string, SdcpDevice> found = [];
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(duration);
        try
        {
            while (true)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (ParseReply(result.Buffer, result.RemoteEndPoint.Address.ToString()) is { } device)
                {
                    found[device.MainboardId ?? device.Host] = device;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listening window closed.
        }

        return [.. found.Values];
    }
}
