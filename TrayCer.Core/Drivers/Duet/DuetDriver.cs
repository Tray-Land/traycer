using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Duet;

/// <summary>
/// RepRapFirmware. A standalone board answers rr_model requests; a board run by a Raspberry Pi
/// (DSF) answers /machine/status instead, and the driver switches on the first 404.
/// </summary>
public sealed class DuetDriver(PrinterEndpoint endpoint, string? password, HttpMessageHandler handler)
    : HttpPrinterDriver(endpoint, handler)
{
    private bool _sbc;
    private bool _connected;
    private string? _sessionKey;

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PrinterAuthException) when (_connected)
        {
            // Sessions time out after a while without requests; log in again once.
            _connected = false;
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (_sessionKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Session-Key", _sessionKey);
        }
    }

    protected override string AuthFailureMessage => "The board rejected the password.";

    private async Task<PrinterSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_sbc)
        {
            JsonNode? model = await GetJsonAsync("machine/status", cancellationToken).ConfigureAwait(false);
            return DuetParser.Parse(model.At("state"), model.At("job"), model.At("heat"), model.At("tools"));
        }

        JsonNode? state = await GetKeyAsync("state", cancellationToken).ConfigureAwait(false);
        JsonNode? job = await GetKeyAsync("job", cancellationToken).ConfigureAwait(false);
        JsonNode? heat = await GetKeyAsync("heat", cancellationToken).ConfigureAwait(false);
        JsonNode? tools = await GetKeyAsync("tools", cancellationToken).ConfigureAwait(false);
        return DuetParser.Parse(state, job, heat, tools);
    }

    private async Task<JsonNode?> GetKeyAsync(string key, CancellationToken cancellationToken) =>
        (await GetJsonAsync($"rr_model?key={key}&flags=d99vn", cancellationToken).ConfigureAwait(false)).At("result");

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _sessionKey = null;
        string time = Uri.EscapeDataString(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        string query = $"password={Uri.EscapeDataString(password ?? "")}&time={time}";
        try
        {
            JsonNode? reply = await GetJsonAsync($"rr_connect?{query}", cancellationToken).ConfigureAwait(false);
            switch (reply.Int("err"))
            {
                case 1:
                    throw new PrinterAuthException(AuthFailureMessage);
                case 2:
                    throw new PrinterProtocolException("The board has no free sessions. Close a Duet Web Control tab and try again.");
            }

            _sessionKey = reply.Text("sessionKey");
            _sbc = false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // DSF on a Raspberry Pi: /machine/connect hands out a session key header.
            JsonNode? reply = await GetJsonAsync($"machine/connect?password={Uri.EscapeDataString(password ?? "")}", cancellationToken, missingIsNull: true).ConfigureAwait(false);
            _sessionKey = reply.Text("sessionKey");
            _sbc = true;
        }

        _connected = true;
    }
}
