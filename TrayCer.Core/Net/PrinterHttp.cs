using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace TrayCer.Core.Net;

/// <summary>
/// HTTP plumbing shared by the HTTP drivers. Each printer gets its own client (credentials differ
/// per printer) with short timeouts, and idle connections close quickly so a printer between polls
/// costs no socket.
/// </summary>
public static class PrinterHttp
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static readonly ProductInfoHeaderValue UserAgent = new("Traycer", "1.0");

    /// <param name="digest">Credentials offered when the printer challenges for HTTP Digest (PrusaLink).</param>
    public static HttpMessageHandler CreateHandler(NetworkCredential? digest = null, Uri? digestUri = null)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectTimeout = ConnectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = AcceptLanCertificate },
        };

        if (digest is not null && digestUri is not null)
        {
            CredentialCache cache = new() { { digestUri, "Digest", digest } };
            handler.Credentials = cache;
            handler.PreAuthenticate = true;
        }

        return handler;
    }

    public static HttpClient CreateClient(HttpMessageHandler handler)
    {
        HttpClient client = new(handler, disposeHandler: true) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
    }

    /// <summary>
    /// Printers that offer HTTPS at all use self-signed certificates, so a trusted chain never
    /// happens. Accept those only for addresses that can't be on the internet (private and
    /// link-local IPs, .local/.lan names); anything public must validate normally.
    /// </summary>
    public static bool AcceptLanCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        string? host = sender switch
        {
            SslStream stream => stream.TargetHostName,
            HttpRequestMessage request => request.RequestUri?.Host,
            _ => null,
        };
        return host is not null && IsLocalHost(host);
    }

    public static bool IsLocalHost(string host)
    {
        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return IsPrivate(ip);
        }

        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        byte[] b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT, used by Tailscale
    }
}
