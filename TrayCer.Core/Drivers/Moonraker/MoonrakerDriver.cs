using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.Moonraker;

/// <summary>
/// Klipper through Moonraker's HTTP API. Most printers on a LAN trust local clients; an API key is
/// sent only when one is configured.
/// </summary>
public sealed class MoonrakerDriver(PrinterEndpoint endpoint, string? apiKey, HttpMessageHandler handler)
    : HttpPrinterDriver(endpoint, handler)
{
    public const int DefaultPort = 7125;

    private IReadOnlyList<string>? _heaters;
    private string? _fileFor;
    private MoonrakerFileInfo? _file;

    /// <summary>Port 7125 is the API itself; Mainsail or Fluidd is normally served on port 80 beside it.</summary>
    public override Uri? WebInterface => Endpoint.Port == DefaultPort ? Endpoint.WithPort(80).BaseUri : Endpoint.BaseUri;

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        _heaters ??= MoonrakerParser.HeaterNames(await GetJsonAsync("printer/objects/list", cancellationToken).ConfigureAwait(false));
        JsonNode? query = await GetJsonAsync(MoonrakerParser.QueryPath(_heaters), cancellationToken).ConfigureAwait(false);

        string? fileName = query.NonEmpty("result", "status", "print_stats", "filename");
        MoonrakerFileInfo? file = await GetFileInfoAsync(fileName, cancellationToken).ConfigureAwait(false);
        string? thumbnail = file?.ThumbnailPath is { } path
            ? Endpoint.Resolve("server/files/gcodes/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri
            : null;

        return MoonrakerParser.Parse(query, file, thumbnail, _heaters);
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }
    }

    protected override string AuthFailureMessage => "Moonraker requires an API key, or rejected the one you entered.";

    private async Task<MoonrakerFileInfo?> GetFileInfoAsync(string? fileName, CancellationToken cancellationToken)
    {
        if (fileName is null || fileName == _fileFor)
        {
            return fileName is null ? null : _file;
        }

        _fileFor = fileName;
        _file = null;
        try
        {
            JsonNode? meta = await GetJsonAsync("server/files/metadata?filename=" + Uri.EscapeDataString(fileName), cancellationToken, missingIsNull: true).ConfigureAwait(false);
            _file = MoonrakerFileInfo.Parse(meta, fileName);
        }
        catch (Exception ex) when (ex is HttpRequestException or PrinterProtocolException)
        {
            // Metadata is optional: without it there's no slicer ETA or thumbnail, nothing more.
        }

        return _file;
    }
}
