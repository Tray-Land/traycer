using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;

namespace TrayCer.Core.Net;

/// <summary>
/// One WebSocket to a printer that pushes JSON status (Creality, Elegoo SDCP). Each text frame is
/// parsed and handed to the callback on a background task; the driver merges and reads it.
/// </summary>
public sealed class JsonWebSocketSession : IAsyncDisposable
{
    // Status frames are a few KB. File listings can be huge, but nothing here asks for one.
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Func<string, string?>? _reply;
    private readonly Action<JsonObject> _onMessage;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveLoop;
    private long _lastReceived = Environment.TickCount64;

    private JsonWebSocketSession(ClientWebSocket socket, Func<string, string?>? reply, Action<JsonObject> onMessage)
    {
        _socket = socket;
        _reply = reply;
        _onMessage = onMessage;
    }

    public bool IsOpen => !_completion.Task.IsCompleted && _socket.State == WebSocketState.Open;

    public Task<Exception?> Completion => _completion.Task;

    /// <summary>Time since the printer last sent anything, heartbeats included.</summary>
    public TimeSpan Silence => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastReceived));

    /// <param name="reply">Answers non-JSON or heartbeat frames: return the text to send back, or null.</param>
    public static async Task<JsonWebSocketSession> ConnectAsync(
        Uri uri,
        string? subProtocol,
        Func<string, string?>? reply,
        Action<JsonObject> onMessage,
        CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // Printers answer app-level heartbeats, not WebSocket pings.
        if (subProtocol is not null)
        {
            socket.Options.AddSubProtocol(subProtocol);
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PrinterHttp.ConnectTimeout);
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        JsonWebSocketSession session = new(socket, reply, onMessage);
        session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
        return session;
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Closing anyway.
            }
        }

        _socket.Abort();
        _socket.Dispose();
        _completion.TrySetResult(null);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
                // Already reported through Completion.
            }
        }

        _life.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        try
        {
            while (!_life.IsCancellationRequested)
            {
                message.SetLength(0);
                bool tooLarge = false;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _life.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _completion.TrySetResult(new WebSocketException("The printer closed the connection."));
                        return;
                    }

                    if (message.Length + result.Count > MaxMessageBytes)
                    {
                        tooLarge = true;
                    }
                    else
                    {
                        message.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                if (tooLarge || result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                if (_reply?.Invoke(text) is { } answer)
                {
                    await SendAsync(answer, _life.Token).ConfigureAwait(false);
                }

                if (JsonRead.Parse(text) is JsonObject json)
                {
                    _onMessage(json);
                }
            }

            _completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _completion.TrySetResult(_life.IsCancellationRequested ? null : ex);
        }
    }
}
