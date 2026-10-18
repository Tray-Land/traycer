using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.FlashForge;

/// <summary>
/// The Adventurer 5M family's LAN API: every request is a POST carrying the serial number and the
/// check code the printer shows in LAN-only mode.
/// </summary>
public sealed class FlashForgeDriver(PrinterEndpoint endpoint, string serial, string checkCode, HttpMessageHandler handler)
    : HttpPrinterDriver(endpoint, handler)
{
    public const int DefaultPort = 8898;

    public override Uri? WebInterface => null;

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        JsonObject body = new() { ["serialNumber"] = serial.Trim(), ["checkCode"] = checkCode.Trim() };
        HttpRequestMessage request = new(HttpMethod.Post, Endpoint.Resolve("detail"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        JsonNode? response = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Int("code") is { } code and not 0)
        {
            throw new PrinterAuthException(response.NonEmpty("message") is { } why
                ? $"The printer refused the request: {why}. Check the serial number and check code."
                : "The printer refused the serial number or check code.");
        }

        return FlashForgeParser.Parse(response.At("detail"), url => Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute) ? absolute.AbsoluteUri : Endpoint.Resolve(url).AbsoluteUri);
    }

    protected override string AuthFailureMessage => "The printer refused the serial number or check code.";
}
