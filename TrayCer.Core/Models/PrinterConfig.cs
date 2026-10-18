using System.Text.Json.Serialization;

namespace TrayCer.Core.Models;

/// <summary>
/// A printer the user added. Saved as JSON in app settings, so it holds nothing secret: the
/// access code, API key or password lives in the Windows credential vault under <see cref="Id"/>.
/// </summary>
/// <param name="Host">What the user typed: an IP address, a host name, or a URL with a port or
/// path (OctoPrint behind a reverse proxy). See <see cref="Net.PrinterEndpoint"/>.</param>
/// <param name="Serial">Bambu Lab and FlashForge serial number.</param>
/// <param name="Username">PrusaLink digest user (usually "maker"); empty means API-key auth.</param>
/// <param name="Slug">Repetier-Server printer slug; empty means the server's first printer.</param>
public sealed record PrinterConfig(
    string Id,
    string Name,
    PrinterKind Kind,
    string Host,
    string? Serial = null,
    string? Username = null,
    string? Slug = null)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    [JsonIgnore]
    public PrinterKindInfo Info => PrinterKindInfo.For(Kind);
}
