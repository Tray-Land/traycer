using System.Net;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers;

/// <summary>Base for drivers that poll a JSON HTTP API: one request at a time, nothing held open.</summary>
public abstract class HttpPrinterDriver : IPrinterDriver
{
    // Thumbnails are small PNGs; anything bigger than this is not a thumbnail.
    private const int MaxThumbnailBytes = 2 * 1024 * 1024;

    protected HttpPrinterDriver(PrinterEndpoint endpoint, HttpMessageHandler handler)
    {
        Endpoint = endpoint;
        Http = PrinterHttp.CreateClient(handler);
    }

    protected PrinterEndpoint Endpoint { get; }

    protected HttpClient Http { get; }

    public bool IsPersistent => false;

    public virtual Uri? WebInterface => Endpoint.BaseUri;

    public abstract Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    public virtual async Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        Authorize(request);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThumbnailBytes)
        {
            return null;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length is > 0 and <= MaxThumbnailBytes ? bytes : null;
    }

    public ValueTask SuspendAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync()
    {
        Http.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds per-request auth (an API key header). Digest auth lives in the handler.</summary>
    protected virtual void Authorize(HttpRequestMessage request)
    {
    }

    /// <summary>
    /// GETs JSON. Returns null for 204 and, when <paramref name="missingIsNull"/>, for 404 and 409
    /// (several APIs use those for "no job" or "printer not connected").
    /// </summary>
    protected Task<JsonNode?> GetJsonAsync(string relative, CancellationToken cancellationToken, bool missingIsNull = false) =>
        SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, Endpoint.Resolve(relative)), cancellationToken, missingIsNull);

    protected async Task<JsonNode?> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        using (request)
        {
            Authorize(request);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            if (missingIsNull && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PrinterAuthException(AuthFailureMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"The printer answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            }

            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonRead.Parse(text) ?? throw new PrinterProtocolException("The printer's answer wasn't JSON. Check the printer type and port.");
        }
    }

    protected virtual string AuthFailureMessage => "The printer rejected the API key.";
}
