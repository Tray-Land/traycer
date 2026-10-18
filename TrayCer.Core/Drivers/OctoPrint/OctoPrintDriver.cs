using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.OctoPrint;

/// <summary>OctoPrint's REST API, authenticated with an application key in X-Api-Key.</summary>
public sealed class OctoPrintDriver(PrinterEndpoint endpoint, string? apiKey, HttpMessageHandler handler)
    : HttpPrinterDriver(endpoint, handler)
{
    // The thumbnail plugins (PrusaSlicer Thumbnails, Cura Thumbnails) add a "thumbnail" field to
    // the file's metadata. Look it up once per file, not on every poll.
    private string? _thumbnailFor;
    private string? _thumbnailUrl;

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        JsonNode? job = await GetJsonAsync("api/job", cancellationToken).ConfigureAwait(false);

        // 409 means OctoPrint is up but not connected to the printer; the job's state says so too.
        JsonNode? printer = await GetJsonAsync("api/printer?exclude=sd,history", cancellationToken, missingIsNull: true).ConfigureAwait(false);

        string? thumbnail = await GetThumbnailUrlAsync(job, cancellationToken).ConfigureAwait(false);
        return OctoPrintParser.Parse(job, printer, thumbnail);
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }
    }

    protected override string AuthFailureMessage => "OctoPrint rejected the API key.";

    private async Task<string?> GetThumbnailUrlAsync(JsonNode? job, CancellationToken cancellationToken)
    {
        string? origin = job.NonEmpty("job", "file", "origin");
        string? path = job.NonEmpty("job", "file", "path");
        if (origin is null || path is null)
        {
            return null;
        }

        string key = origin + "/" + path;
        if (key == _thumbnailFor)
        {
            return _thumbnailUrl;
        }

        _thumbnailFor = key;
        _thumbnailUrl = null;
        try
        {
            string encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            JsonNode? info = await GetJsonAsync($"api/files/{Uri.EscapeDataString(origin)}/{encoded}", cancellationToken, missingIsNull: true).ConfigureAwait(false);
            if (info.NonEmpty("thumbnail") is { } relative)
            {
                _thumbnailUrl = Endpoint.Resolve(relative).AbsoluteUri;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or PrinterProtocolException)
        {
            // No thumbnail plugin, or the file vanished: the card just has no picture.
        }

        return _thumbnailUrl;
    }
}
