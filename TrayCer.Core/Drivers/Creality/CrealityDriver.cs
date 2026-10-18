using System.Net.WebSockets;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Creality;

/// <summary>
/// Creality's stock-firmware WebSocket on port 9999. The printer pushes changed fields as they
/// change and sends heartbeats that must be answered with "ok"; asking for ReqPrinterPara gets a
/// fuller report.
/// </summary>
public sealed class CrealityDriver(PrinterEndpoint endpoint, HttpMessageHandler thumbnailHandler) : IPrinterDriver
{
    public const int DefaultPort = 9999;

    private const string SubProtocol = "wsslicer"; // What Creality's own web UI asks for.
    private const string RequestStatus = """{"method":"get","params":{"ReqPrinterPara":1}}""";
    private static readonly TimeSpan FirstReportTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan QuietBeforeProbe = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http = PrinterHttp.CreateClient(thumbnailHandler);
    private readonly Lock _lock = new();
    private readonly JsonObject _state = [];
    private JsonWebSocketSession? _session;
    private TaskCompletionSource? _firstReport;

    public bool IsPersistent => true;

    public Uri? WebInterface => null;

    public async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_session is not { IsOpen: true } session || session.Silence > DeadAfter)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (session.Silence > QuietBeforeProbe)
        {
            await session.SendAsync(RequestStatus, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            // The stock web server keeps a preview of the current job beside the WebSocket.
            string thumbnail = endpoint.WithPort(80).Resolve("downloads/original/current_print_image.png").AbsoluteUri
                + "?job=" + Uri.EscapeDataString(_state["printFileName"]?.ToString() ?? "");
            return CrealityParser.Parse(_state, thumbnail);
        }
    }

    public async Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true
                ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            // Older firmware has no web server beside the WebSocket.
            return null;
        }
    }

    public async ValueTask SuspendAsync()
    {
        JsonWebSocketSession? session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SuspendAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    internal void Apply(JsonObject message)
    {
        lock (_lock)
        {
            JsonRead.MergeInto(_state, message);
            _firstReport?.TrySetResult();
        }
    }

    private static string? Reply(string text) =>
        text.Contains("heart_beat", StringComparison.Ordinal) ? "ok" : null;

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await SuspendAsync().ConfigureAwait(false);
        TaskCompletionSource firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _state.Clear();
            _firstReport = firstReport;
        }

        Uri uri = new($"ws://{endpoint}/");
        JsonWebSocketSession session;
        try
        {
            session = await JsonWebSocketSession.ConnectAsync(uri, SubProtocol, Reply, Apply, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // Older firmware may not offer the subprotocol; try a plain handshake.
            session = await JsonWebSocketSession.ConnectAsync(uri, null, Reply, Apply, cancellationToken).ConfigureAwait(false);
        }

        _session = session;
        try
        {
            await session.SendAsync(RequestStatus, cancellationToken).ConfigureAwait(false);
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
}
