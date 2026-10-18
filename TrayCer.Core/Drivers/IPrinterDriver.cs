using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers;

/// <summary>
/// Talks to one printer. HTTP drivers make a request per call; push drivers (MQTT, WebSocket)
/// hold a connection open between calls and return the latest merged report.
/// </summary>
public interface IPrinterDriver : IAsyncDisposable
{
    /// <summary>
    /// True when the driver keeps a connection open between calls. The monitor closes it with
    /// <see cref="SuspendAsync"/> while the printer is idle and checks back less often.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>Reads the printer's state. Throws on network failure, or with
    /// <see cref="Net.PrinterAuthException"/> when the credentials are rejected.</summary>
    Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a <see cref="PrinterSnapshot.ThumbnailUrl"/> with the printer's credentials; null if unavailable.</summary>
    Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken);

    /// <summary>The printer's own web interface, if it has one.</summary>
    Uri? WebInterface { get; }

    /// <summary>Closes any open connection; the next <see cref="GetSnapshotAsync"/> reconnects.</summary>
    ValueTask SuspendAsync();
}
