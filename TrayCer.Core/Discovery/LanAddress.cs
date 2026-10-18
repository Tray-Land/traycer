using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrayCer.Core.Discovery;

/// <summary>Local network helpers for the UDP-based discovery protocols.</summary>
public static class LanAddress
{
    /// <summary>Resolves a host name or literal to one address, preferring IPv4 (printers rarely do IPv6).</summary>
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal))
        {
            return literal;
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// The directed broadcast address of every up, non-loopback IPv4 interface, plus the limited
    /// broadcast. Windows sends 255.255.255.255 out of only one interface, so a PC on Wi-Fi and
    /// Ethernet needs both.
    /// </summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        HashSet<IPAddress> addresses = [IPAddress.Broadcast];
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.PrefixLength is > 0 and < 32)
                    {
                        addresses.Add(Broadcast(unicast.Address, unicast.PrefixLength));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the limited broadcast alone.
        }

        return [.. addresses];
    }

    /// <summary>
    /// A UDP socket on Windows fails its next receive when an earlier send drew an ICMP "port
    /// unreachable". Probes send to several ports at once, so turn that off.
    /// </summary>
    public static UdpClient CreateUdp(AddressFamily family)
    {
        const int SioUdpConnReset = -1744830452; // _WSAIOW(IOC_VENDOR, 12)
        UdpClient udp = new(family);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
                // Older stacks: the receive loop treats a reset as "nothing there".
            }
        }

        return udp;
    }

    public static IPAddress Broadcast(IPAddress address, int prefixLength)
    {
        byte[] bytes = address.GetAddressBytes();
        uint value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        uint broadcast = value | ~mask;
        return new IPAddress([(byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast]);
    }
}
