using System.Globalization;

namespace TrayCer.Core.Net;

/// <summary>
/// A printer address parsed from what the user typed: "192.168.1.40", "voron.local:7125",
/// "http://octopi.local/octoprint/" or "[fe80::1]:80". The kind's port fills in when none is given.
/// </summary>
public sealed record PrinterEndpoint(string Scheme, string Host, int Port, string BasePath)
{
    /// <summary>The HTTP base URI, always ending in a slash so relative paths append to it.</summary>
    public Uri BaseUri
    {
        get
        {
            UriBuilder builder = new(Scheme, Host, Port, BasePath);
            return builder.Uri;
        }
    }

    public bool IsHttps => Scheme == Uri.UriSchemeHttps;

    /// <summary>Parses <paramref name="input"/>; false when it isn't a usable host.</summary>
    public static bool TryParse(string? input, int defaultPort, out PrinterEndpoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string text = input.Trim();
        bool hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? text : "http://" + text, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Uri reports the scheme's default port when none was typed; only an explicit one counts.
        bool explicitPort = HasExplicitPort(text, hasScheme);
        int port = explicitPort ? uri.Port
            : uri.Scheme == Uri.UriSchemeHttps && defaultPort == 80 ? 443
            : defaultPort;

        string path = uri.AbsolutePath;
        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        endpoint = new PrinterEndpoint(uri.Scheme, uri.IdnHost, port, path);
        return true;
    }

    public static PrinterEndpoint Parse(string input, int defaultPort) =>
        TryParse(input, defaultPort, out PrinterEndpoint endpoint)
            ? endpoint
            : throw new FormatException($"'{input}' isn't a printer address.");

    public Uri Resolve(string relative) => new(BaseUri, relative.TrimStart('/'));

    /// <summary>Same host on another port, for protocols beside the main one (a web UI on 80).</summary>
    public PrinterEndpoint WithPort(int port) => this with { Port = port, BasePath = "/" };

    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}" : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    private static bool HasExplicitPort(string text, bool hasScheme)
    {
        string rest = hasScheme ? text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..] : text;
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end >= 0 ? rest[..end] : rest;
        int at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        // [v6]:port, or host:port with exactly one colon (a bare IPv6 address has several).
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']', StringComparison.Ordinal);
            return close >= 0 && close + 1 < authority.Length && authority[close + 1] == ':';
        }

        return authority.Count(c => c == ':') == 1;
    }
}
