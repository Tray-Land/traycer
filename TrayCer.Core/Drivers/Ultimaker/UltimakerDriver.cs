using System.Text.Json.Nodes;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Ultimaker;

/// <summary>UltiMaker S-series and Ultimaker 3. Reading needs no authentication.</summary>
public sealed class UltimakerDriver(PrinterEndpoint endpoint, HttpMessageHandler handler) : HttpPrinterDriver(endpoint, handler)
{
    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        JsonNode? printer = await GetJsonAsync("api/v1/printer", cancellationToken).ConfigureAwait(false);
        JsonNode? job = await GetJsonAsync("api/v1/print_job", cancellationToken, missingIsNull: true).ConfigureAwait(false);
        return UltimakerParser.Parse(printer, job);
    }
}
