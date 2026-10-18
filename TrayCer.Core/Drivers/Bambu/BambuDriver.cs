using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;
using TrayCer.Core.Net.Mqtt;

namespace TrayCer.Core.Drivers.Bambu;

/// <summary>
/// Bambu Lab's local MQTT broker (TLS on 8883, user "bblp", the LAN access code as password).
/// After a "pushall" the printer sends a full report, then only changed fields, which are merged
/// into one document. P1 and A1 printers dislike frequent pushalls, so the connection stays open
/// while a print runs and is dropped while idle.
/// </summary>
public sealed class BambuDriver : IPrinterDriver
{
    public const int DefaultPort = 8883;

    private const string Username = "bblp";
    private const string PushAll = """{"pushing":{"sequence_id":"0","command":"pushall","version":1,"push_target":1}}""";
    private static readonly TimeSpan FirstReportTimeout = TimeSpan.FromSeconds(10);

    private readonly PrinterEndpoint _endpoint;
    private readonly string _serial;
    private readonly string _accessCode;
    private readonly Lock _lock = new();
    private readonly JsonObject _print = [];
    private MqttConnection? _connection;
    private TaskCompletionSource? _firstReport;

    public BambuDriver(PrinterEndpoint endpoint, string serial, string accessCode)
    {
        _endpoint = endpoint;
        _serial = serial.Trim().ToUpperInvariant();
        _accessCode = accessCode.Trim();
    }

    public bool IsPersistent => true;

    public Uri? WebInterface => null;

    public async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_connection is not { IsOpen: true })
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return Current(DateTimeOffset.UtcNow);
    }

    /// <summary>The merged report read as a snapshot.</summary>
    internal PrinterSnapshot Current(DateTimeOffset now)
    {
        lock (_lock)
        {
            return BambuParser.Parse(_print, _serial, now);
        }
    }

    public Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public async ValueTask SuspendAsync()
    {
        MqttConnection? connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => SuspendAsync();

    /// <summary>Folds one report into the merged state; only "print" carries what we show.</summary>
    internal void Apply(ReadOnlySpan<byte> payload)
    {
        if (JsonRead.Parse(Encoding.UTF8.GetString(payload)) is not JsonObject report || report["print"] is not JsonObject print)
        {
            return;
        }

        lock (_lock)
        {
            JsonRead.MergeInto(_print, print);
            if (_print["gcode_state"] is not null)
            {
                _firstReport?.TrySetResult();
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await SuspendAsync().ConfigureAwait(false);
        TaskCompletionSource firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _print.Clear();
            _firstReport = firstReport;
        }

        // Bambu printers present a certificate from Bambu's own CA, which Windows doesn't trust.
        // The LAN-only address and the access code are what identify the printer.
        MqttConnection connection = await MqttConnection.ConnectAsync(
            _endpoint.Host,
            _endpoint.Port,
            (_, _, _, _) => true,
            "traycer-" + Guid.NewGuid().ToString("N")[..8],
            Username,
            _accessCode,
            (_, payload) => Apply(payload.Span),
            cancellationToken).ConfigureAwait(false);
        _connection = connection;

        try
        {
            await connection.SubscribeAsync($"device/{_serial}/report", cancellationToken).ConfigureAwait(false);
            await connection.PublishAsync($"device/{_serial}/request", PushAll, cancellationToken).ConfigureAwait(false);
            Task done = await Task.WhenAny(firstReport.Task, connection.Completion, Task.Delay(FirstReportTimeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (done != firstReport.Task)
            {
                // A wrong serial subscribes fine but never hears anything.
                throw new PrinterProtocolException("The printer accepted the access code but sent no report. Check the serial number.");
            }
        }
        catch
        {
            await SuspendAsync().ConfigureAwait(false);
            throw;
        }
    }
}
