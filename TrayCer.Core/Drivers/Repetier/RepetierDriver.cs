using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Repetier;

/// <summary>Repetier-Server's REST API. The API key goes in a header, never the URL.</summary>
public sealed class RepetierDriver(PrinterEndpoint endpoint, string? apiKey, string? slug, HttpMessageHandler handler)
    : HttpPrinterDriver(endpoint, handler)
{
    public const int DefaultPort = 3344;

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        JsonNode? list = await GetJsonAsync("printer/api/?a=listPrinter", cancellationToken).ConfigureAwait(false);
        JsonNode? printer = RepetierParser.FindPrinter(list, slug);

        JsonNode? state = null;
        if (printer.NonEmpty("slug") is { } found)
        {
            JsonNode? states = await GetJsonAsync($"printer/api/{Uri.EscapeDataString(found)}?a=stateList", cancellationToken, missingIsNull: true).ConfigureAwait(false);
            state = states.At(found);
        }

        return RepetierParser.Parse(printer, state);
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }
    }

    protected override string AuthFailureMessage => "Repetier-Server rejected the API key.";
}
