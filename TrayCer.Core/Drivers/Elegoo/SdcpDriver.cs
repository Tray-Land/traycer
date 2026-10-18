using System.Text.Json.Nodes;
using TrayCer.Core.Discovery;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Elegoo;

/// <summary>
/// Elegoo's SDCP v3 over the WebSocket at :3030/websocket. Requests are addressed to the
/// mainboard ID, which the UDP discovery reply supplies (or the first pushed message, failing that).
/// </summary>
public sealed class SdcpDriver(PrinterEndpoint endpoint) : IPrinterDriver
{
    public const int DefaultPort = 3030;

    private static readonly TimeSpan FirstReportTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PingAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private readonly JsonObject _status = [];
    private JsonWebSocketSession? _session;
    private TaskCompletionSource? _firstReport;
    private string? _mainboardId;
    private string? _machineName;

    public bool IsPersistent => true;

    public Uri? WebInterface => null;

    public async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_session is not { IsOpen: true } session || session.Silence > DeadAfter)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RequestStatusAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.Silence > PingAfter)
            {
                await session.SendAsync("ping", cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            return SdcpParser.Parse(_status, _machineName);
        }
    }

    public Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public async ValueTask SuspendAsync()
    {
        JsonWebSocketSession? session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => SuspendAsync();

    internal void Apply(JsonObject message)
    {
        lock (_lock)
        {
            _mainboardId ??= message.NonEmpty("MainboardID") ?? message.NonEmpty("Data", "MainboardID");
            if ((message.Object("Attributes") ?? message.Object("Data", "Attributes")) is { } attributes)
            {
                _machineName ??= attributes.NonEmpty("MachineName");
                _mainboardId ??= attributes.NonEmpty("MainboardID");
            }

            if ((message.Object("Status") ?? message.Object("Data", "Status")) is { } status)
            {
                JsonRead.MergeInto(_status, status);
                _firstReport?.TrySetResult();
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await SuspendAsync().ConfigureAwait(false);
        if (_mainboardId is null || _machineName is null)
        {
            SdcpDevice? device = await SdcpDiscovery.ProbeAsync(endpoint.Host, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            _mainboardId ??= device?.MainboardId;
            _machineName ??= device?.MachineName;
        }

        TaskCompletionSource firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _status.Clear();
            _firstReport = firstReport;
        }

        JsonWebSocketSession session = await JsonWebSocketSession.ConnectAsync(
            new Uri($"ws://{endpoint}/websocket"), null, null, Apply, cancellationToken).ConfigureAwait(false);
        _session = session;
        try
        {
            await RequestStatusAsync(session, cancellationToken).ConfigureAwait(false);
            await session.SendAsync(Request(1), cancellationToken).ConfigureAwait(false); // attributes: model name
            Task done = await Task.WhenAny(firstReport.Task, session.Completion, Task.Delay(FirstReportTimeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (done != firstReport.Task)
            {
                throw new PrinterProtocolException("The printer accepted the connection but sent no status.");
            }
        }
        catch
        {
            await SuspendAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task RequestStatusAsync(JsonWebSocketSession session, CancellationToken cancellationToken) =>
        session.SendAsync(Request(0), cancellationToken);

    /// <summary>An SDCP request envelope: Cmd 0 asks for status, Cmd 1 for attributes.</summary>
    internal string Request(int command)
    {
        string id = _mainboardId ?? "";
        JsonObject request = new()
        {
            ["Id"] = Guid.NewGuid().ToString("N"),
            ["Data"] = new JsonObject
            {
                ["Cmd"] = command,
                ["Data"] = new JsonObject(),
                ["RequestID"] = Guid.NewGuid().ToString("N"),
                ["MainboardID"] = id,
                ["TimeStamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["From"] = 0,
            },
            ["Topic"] = "sdcp/request/" + id,
        };
        return request.ToJsonString();
    }
}
