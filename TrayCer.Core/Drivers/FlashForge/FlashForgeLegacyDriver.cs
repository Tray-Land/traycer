using System.Net.Sockets;
using System.Text;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.FlashForge;

/// <summary>
/// FlashForge's original TCP protocol on port 8899 (Adventurer 3 and 4, Finder, Guider, Creator).
/// Each poll connects, takes control with ~M601, asks its questions, releases with ~M602 and hangs
/// up, so it doesn't hold the printer's single control slot away from FlashPrint.
/// </summary>
public sealed class FlashForgeLegacyDriver(PrinterEndpoint endpoint) : IPrinterDriver
{
    public const int DefaultPort = 8899;

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(4);

    private string? _machineInfo;

    public bool IsPersistent => false;

    public Uri? WebInterface => null;

    public async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using TcpClient tcp = new() { NoDelay = true };
        using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(PrinterHttp.ConnectTimeout);
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, connect.Token).ConfigureAwait(false);
        }

        NetworkStream stream = tcp.GetStream();
        string control = await AskAsync(stream, "~M601 S1", cancellationToken).ConfigureAwait(false);
        if (!control.Contains("CMD M601", StringComparison.OrdinalIgnoreCase))
        {
            throw new PrinterProtocolException("The printer didn't answer like a FlashForge.");
        }

        try
        {
            _machineInfo ??= await AskAsync(stream, "~M115", cancellationToken).ConfigureAwait(false);
            string status = await AskAsync(stream, "~M119", cancellationToken).ConfigureAwait(false);
            string progress = await AskAsync(stream, "~M27", cancellationToken).ConfigureAwait(false);
            string temperatures = await AskAsync(stream, "~M105", cancellationToken).ConfigureAwait(false);
            return FlashForgeLegacyParser.Parse(status, progress, temperatures, _machineInfo);
        }
        finally
        {
            try
            {
                await AskAsync(stream, "~M602", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Hanging up releases control too.
            }
        }
    }

    public Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public ValueTask SuspendAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Sends one command and reads its reply up to the closing "ok" line.</summary>
    private static async Task<string> AskAsync(NetworkStream stream, string command, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyTimeout);
        StringBuilder reply = new();
        byte[] buffer = new byte[1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The printer closed the connection.");
            }

            reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
            string text = reply.ToString();
            if (text.EndsWith("ok\r\n", StringComparison.Ordinal) || text.EndsWith("ok\n", StringComparison.Ordinal) || text.TrimEnd().EndsWith("\nok", StringComparison.Ordinal))
            {
                return text.Replace("\r", "", StringComparison.Ordinal);
            }

            if (reply.Length > 16 * 1024)
            {
                throw new PrinterProtocolException("The printer's reply was too long.");
            }
        }
    }
}
