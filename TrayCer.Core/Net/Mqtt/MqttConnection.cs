using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace TrayCer.Core.Net.Mqtt;

/// <summary>
/// One MQTT session over TCP (optionally TLS). Incoming PUBLISH payloads go to the callback on a
/// background task; a keep-alive ping goes out on the half-interval. Disposing closes the socket.
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    private const int MaxPacketBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(60);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Action<string, ReadOnlyMemory<byte>> _onMessage;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoop;
    private Task? _pingLoop;
    private ushort _nextPacketId = 1;
    private long _lastReceived = Environment.TickCount64;

    private MqttConnection(TcpClient tcp, Stream stream, Action<string, ReadOnlyMemory<byte>> onMessage)
    {
        _tcp = tcp;
        _stream = stream;
        _onMessage = onMessage;
    }

    /// <summary>Set once the session ends, with the reason (null for a clean close).</summary>
    public Task<Exception?> Completion => _completion.Task;

    public bool IsOpen => !_completion.Task.IsCompleted;

    /// <param name="validateCertificate">TLS certificate check; null connects without TLS.</param>
    public static async Task<MqttConnection> ConnectAsync(
        string host,
        int port,
        RemoteCertificateValidationCallback? validateCertificate,
        string clientId,
        string? username,
        string? password,
        Action<string, ReadOnlyMemory<byte>> onMessage,
        CancellationToken cancellationToken)
    {
        TcpClient tcp = new() { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (validateCertificate is not null)
            {
                SslStream ssl = new(stream, leaveInnerStreamOpen: false, validateCertificate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken).ConfigureAwait(false);
                stream = ssl;
            }

            MqttConnection connection = new(tcp, stream, onMessage);
            await connection.HandshakeAsync(clientId, username, password, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken)
    {
        await WriteAsync(MqttCodec.Subscribe(_nextPacketId++, topic), cancellationToken).ConfigureAwait(false);
    }

    public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken) =>
        WriteAsync(MqttCodec.Publish(topic, Encoding.UTF8.GetBytes(payload)), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (IsOpen)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
                await WriteAsync(MqttCodec.Disconnect(), timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Closing anyway.
            }
        }

        _life.Cancel();
        _completion.TrySetResult(null);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
        foreach (Task? loop in new[] { _readLoop, _pingLoop })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch
                {
                    // Already reported through Completion.
                }
            }
        }

        _life.Dispose();
        _writeLock.Dispose();
    }

    private async Task HandshakeAsync(string clientId, string? username, string? password, CancellationToken cancellationToken)
    {
        await WriteAsync(MqttCodec.Connect(clientId, username, password, (ushort)KeepAlive.TotalSeconds), cancellationToken).ConfigureAwait(false);
        MqttPacket ack = await MqttCodec.ReadAsync(_stream, MaxPacketBytes, cancellationToken).ConfigureAwait(false);
        if (ack.Type != MqttPacketType.ConnAck || ack.Body.Length < 2)
        {
            throw new PrinterProtocolException("The printer didn't answer like an MQTT broker.");
        }

        switch (ack.Body[1])
        {
            case MqttCodec.ConnAckAccepted:
                break;
            case MqttCodec.ConnAckBadCredentials or MqttCodec.ConnAckNotAuthorized:
                throw new PrinterAuthException("The printer rejected the access code.");
            default:
                throw new PrinterProtocolException($"The printer refused the connection (code {ack.Body[1]}).");
        }

        _readLoop = Task.Run(ReadLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                MqttPacket packet = await MqttCodec.ReadAsync(_stream, MaxPacketBytes, _life.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                if (packet.Type != MqttPacketType.Publish)
                {
                    continue;
                }

                if (MqttCodec.QoS1PacketId(packet) is { } id)
                {
                    await WriteAsync(MqttCodec.PubAck(id), _life.Token).ConfigureAwait(false);
                }

                (string topic, ReadOnlyMemory<byte> payload) = MqttCodec.ParsePublish(packet);
                _onMessage(topic, payload);
            }

            _completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _completion.TrySetResult(_life.IsCancellationRequested ? null : ex);
        }
    }

    private async Task PingLoopAsync()
    {
        using PeriodicTimer timer = new(KeepAlive / 2);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
            {
                // Every ping gets a PINGRESP, so silence past a ping and a half means the printer is gone
                // even though writes still land in the socket buffer.
                if (Environment.TickCount64 - Volatile.Read(ref _lastReceived) > KeepAlive.TotalMilliseconds * 1.25)
                {
                    _completion.TrySetResult(new TimeoutException("The printer stopped answering."));
                    _life.Cancel();
                    return;
                }

                await WriteAsync(MqttCodec.PingReq(), _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
        catch (Exception ex)
        {
            _completion.TrySetResult(ex);
        }
    }

    private async Task WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
