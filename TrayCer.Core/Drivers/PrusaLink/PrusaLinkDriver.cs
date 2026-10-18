using System.Net;
using System.Text.Json.Nodes;
using TrayCer.Core.Drivers.OctoPrint;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers.PrusaLink;

/// <summary>
/// PrusaLink: HTTP Digest with the printer's "maker" password on current firmware, an X-Api-Key on
/// older PrusaLink. Firmware without /api/v1 gets the OctoPrint-compatible endpoints instead.
/// </summary>
public sealed class PrusaLinkDriver : HttpPrinterDriver
{
    private readonly string? _apiKey;
    private bool _legacyApi;
    private long? _jobInfoFor;
    private JsonNode? _jobInfo;

    private PrusaLinkDriver(PrinterEndpoint endpoint, string? apiKey, HttpMessageHandler handler)
        : base(endpoint, handler)
    {
        _apiKey = apiKey;
    }

    /// <param name="username">The digest user (normally "maker"); empty sends the secret as an API key instead.</param>
    public static PrusaLinkDriver Create(PrinterEndpoint endpoint, string? username, string? secret, HttpMessageHandler? handler = null)
    {
        bool digest = !string.IsNullOrWhiteSpace(username);
        handler ??= PrinterHttp.CreateHandler(
            digest && secret is not null ? new NetworkCredential(username!.Trim(), secret) : null,
            endpoint.BaseUri);
        return new PrusaLinkDriver(endpoint, digest ? null : secret, handler);
    }

    public override async Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_legacyApi)
        {
            try
            {
                JsonNode? status = await GetJsonAsync("api/v1/status", cancellationToken).ConfigureAwait(false);
                JsonNode? job = await GetJobAsync(status.Long("job", "id"), cancellationToken).ConfigureAwait(false);
                string? thumbnail = job.NonEmpty("file", "refs", "thumbnail") is { } path ? Endpoint.Resolve(path).AbsoluteUri : null;
                return PrusaLinkParser.Parse(status, job, thumbnail);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _legacyApi = true;
            }
        }

        JsonNode? legacyJob = await GetJsonAsync("api/job", cancellationToken).ConfigureAwait(false);
        JsonNode? legacyPrinter = await GetJsonAsync("api/printer", cancellationToken, missingIsNull: true).ConfigureAwait(false);
        return OctoPrintParser.Parse(legacyJob, legacyPrinter);
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        }
    }

    protected override string AuthFailureMessage => "PrusaLink rejected the user name or password.";

    // /api/v1/job carries the file name and thumbnail; they don't change during a job, so fetch once.
    private async Task<JsonNode?> GetJobAsync(long? jobId, CancellationToken cancellationToken)
    {
        if (jobId is null)
        {
            _jobInfoFor = null;
            _jobInfo = null;
            return null;
        }

        if (jobId != _jobInfoFor)
        {
            _jobInfo = await GetJsonAsync("api/v1/job", cancellationToken, missingIsNull: true).ConfigureAwait(false);
            _jobInfoFor = _jobInfo is null ? null : jobId;
        }

        return _jobInfo;
    }
}
