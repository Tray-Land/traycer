using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TrayCer.Core.Drivers.Bambu;
using TrayCer.Core.Drivers.Creality;
using TrayCer.Core.Drivers.FlashForge;
using TrayCer.Core.Drivers.Moonraker;
using TrayCer.Core.Drivers.Repetier;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Discovery;

/// <summary>
/// Works out what kind of printer is at an address by asking each protocol a harmless question at
/// once: an endpoint only that software has, a port only that brand opens. Several can match (a
/// rooted Creality K1 speaks both its stock protocol and Moonraker); the most capable comes first.
/// </summary>
public static class PrinterProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    // Preference when more than one protocol answers: the one that reports the most.
    private static readonly PrinterKind[] Preference =
    [
        PrinterKind.BambuLab, PrinterKind.PrusaLink, PrinterKind.Moonraker, PrinterKind.OctoPrint, PrinterKind.Creality,
        PrinterKind.Elegoo, PrinterKind.FlashForge, PrinterKind.Duet, PrinterKind.Ultimaker, PrinterKind.Repetier,
        PrinterKind.FlashForgeLegacy,
    ];

    public static async Task<IReadOnlyList<DiscoveredPrinter>> DetectAsync(string input, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        if (!PrinterEndpoint.TryParse(input, 80, out PrinterEndpoint web))
        {
            return [];
        }

        string host = web.Host;
        bool explicitPort = web.Port != 80 && !(web.IsHttps && web.Port == 443);
        using HttpClient http = PrinterHttp.CreateClient(handler ?? PrinterHttp.CreateHandler());
        http.Timeout = ProbeTimeout;

        List<Task<DiscoveredPrinter?>> probes =
        [
            Guard(() => MoonrakerAsync(http, web, explicitPort, cancellationToken)),
            Guard(() => OctoPrintAsync(http, web, cancellationToken)),
            Guard(() => PrusaLinkAsync(http, web, cancellationToken)),
            Guard(() => UltimakerAsync(http, web, cancellationToken)),
            Guard(() => DuetAsync(http, web, cancellationToken)),
            Guard(() => RepetierAsync(http, web, explicitPort, cancellationToken)),
        ];

        // The other protocols only make sense on a bare host; a typed port means a web service.
        if (!explicitPort)
        {
            probes.Add(Guard(() => BambuAsync(host, cancellationToken)));
            probes.Add(Guard(() => CrealityAsync(host, cancellationToken)));
            probes.Add(Guard(() => ElegooAsync(host, cancellationToken)));
            probes.Add(Guard(() => FlashForgeAsync(host, cancellationToken)));
        }

        DiscoveredPrinter?[] results = await Task.WhenAll(probes).ConfigureAwait(false);
        return [.. results.OfType<DiscoveredPrinter>().DistinctBy(r => r.Kind).OrderBy(r => Array.IndexOf(Preference, r.Kind))];
    }

    private static async Task<DiscoveredPrinter?> Guard(Func<Task<DiscoveredPrinter?>> probe)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Whatever went wrong, that protocol isn't what's at this address.
            return null;
        }
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Json, HttpResponseMessage? Response)> GetAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, JsonRead.Parse(text), response);
    }

    private static async Task<DiscoveredPrinter?> MoonrakerAsync(HttpClient http, PrinterEndpoint web, bool explicitPort, CancellationToken cancellationToken)
    {
        // Moonraker itself on 7125, or behind Mainsail/Fluidd's proxy on the typed port.
        PrinterEndpoint api = explicitPort ? web : web.WithPort(MoonrakerDriver.DefaultPort);
        (HttpStatusCode status, JsonNode? json, HttpResponseMessage? response) = await GetAsync(http, api.Resolve("server/info"), cancellationToken).ConfigureAwait(false);
        response?.Dispose();
        if (status == HttpStatusCode.OK && json.At("result", "klippy_state") is not null)
        {
            return new DiscoveredPrinter(PrinterKind.Moonraker, HostFor(web, api.Port, MoonrakerDriver.DefaultPort));
        }

        if (!explicitPort)
        {
            // Some setups only expose Moonraker through the web UI on port 80.
            (status, json, response) = await GetAsync(http, web.Resolve("server/info"), cancellationToken).ConfigureAwait(false);
            response?.Dispose();
            if (status == HttpStatusCode.OK && json.At("result", "klippy_state") is not null)
            {
                return new DiscoveredPrinter(PrinterKind.Moonraker, HostFor(web, 80, MoonrakerDriver.DefaultPort));
            }
        }

        return status == HttpStatusCode.Unauthorized && json.At("error") is not null
            ? new DiscoveredPrinter(PrinterKind.Moonraker, HostFor(web, api.Port, MoonrakerDriver.DefaultPort), NeedsSecret: true)
            : null;
    }

    private static async Task<DiscoveredPrinter?> OctoPrintAsync(HttpClient http, PrinterEndpoint web, CancellationToken cancellationToken)
    {
        // The Application Keys plugin's probe answers 204 without a key, and only OctoPrint has it.
        using HttpResponseMessage response = await http.GetAsync(web.Resolve("plugin/appkeys/probe"), cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent
            ? new DiscoveredPrinter(PrinterKind.OctoPrint, HostFor(web, web.Port, 80), NeedsSecret: true)
            : null;
    }

    private static async Task<DiscoveredPrinter?> PrusaLinkAsync(HttpClient http, PrinterEndpoint web, CancellationToken cancellationToken)
    {
        (HttpStatusCode status, JsonNode? json, HttpResponseMessage? response) = await GetAsync(http, web.Resolve("api/version"), cancellationToken).ConfigureAwait(false);
        using (response)
        {
            bool digest = response?.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)) == true;
            bool namedPrusa = json.NonEmpty("text") is { } text && text.Contains("Prusa", StringComparison.OrdinalIgnoreCase);
            if ((status == HttpStatusCode.Unauthorized && digest) || (status == HttpStatusCode.OK && namedPrusa))
            {
                return new DiscoveredPrinter(PrinterKind.PrusaLink, HostFor(web, web.Port, 80), json.NonEmpty("hostname"), NeedsSecret: true);
            }
        }

        return null;
    }

    private static async Task<DiscoveredPrinter?> UltimakerAsync(HttpClient http, PrinterEndpoint web, CancellationToken cancellationToken)
    {
        (HttpStatusCode status, JsonNode? json, HttpResponseMessage? response) = await GetAsync(http, web.Resolve("api/v1/system/name"), cancellationToken).ConfigureAwait(false);
        response?.Dispose();
        if (status != HttpStatusCode.OK || json is not JsonValue)
        {
            return null;
        }

        (_, JsonNode? variant, HttpResponseMessage? second) = await GetAsync(http, web.Resolve("api/v1/system/variant"), cancellationToken).ConfigureAwait(false);
        second?.Dispose();
        return new DiscoveredPrinter(PrinterKind.Ultimaker, HostFor(web, web.Port, 80), json.Text(), Model: variant.Text());
    }

    private static async Task<DiscoveredPrinter?> DuetAsync(HttpClient http, PrinterEndpoint web, CancellationToken cancellationToken)
    {
        string time = Uri.EscapeDataString(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        (HttpStatusCode status, JsonNode? json, HttpResponseMessage? response) = await GetAsync(http, web.Resolve($"rr_connect?password=&time={time}"), cancellationToken).ConfigureAwait(false);
        response?.Dispose();
        if (status == HttpStatusCode.OK && json.Int("err") is { } err)
        {
            return new DiscoveredPrinter(PrinterKind.Duet, HostFor(web, web.Port, 80), NeedsSecret: err == 1);
        }

        (status, json, response) = await GetAsync(http, web.Resolve("machine/status"), cancellationToken).ConfigureAwait(false);
        response?.Dispose();
        return status == HttpStatusCode.OK && json.At("boards") is not null
            ? new DiscoveredPrinter(PrinterKind.Duet, HostFor(web, web.Port, 80), json.NonEmpty("network", "name"))
            : null;
    }

    private static async Task<DiscoveredPrinter?> RepetierAsync(HttpClient http, PrinterEndpoint web, bool explicitPort, CancellationToken cancellationToken)
    {
        PrinterEndpoint api = explicitPort ? web : web.WithPort(RepetierDriver.DefaultPort);
        (HttpStatusCode status, JsonNode? json, HttpResponseMessage? response) = await GetAsync(http, api.Resolve("printer/info"), cancellationToken).ConfigureAwait(false);
        response?.Dispose();
        bool repetier = json.NonEmpty("name")?.Contains("Repetier", StringComparison.OrdinalIgnoreCase) == true
            || (json.At("printers") is JsonArray && json.NonEmpty("version") is not null);
        return status == HttpStatusCode.OK && repetier
            ? new DiscoveredPrinter(PrinterKind.Repetier, HostFor(web, api.Port, RepetierDriver.DefaultPort), json.NonEmpty("servername"))
            : null;
    }

    /// <summary>Bambu printers present a certificate issued by Bambu's own CA, named for the serial number.</summary>
    private static async Task<DiscoveredPrinter?> BambuAsync(string host, CancellationToken cancellationToken)
    {
        using TcpClient tcp = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        await tcp.ConnectAsync(host, BambuDriver.DefaultPort, timeout.Token).ConfigureAwait(false);

        X509Certificate? certificate = null;
        await using SslStream ssl = new(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            certificate = cert is null ? null : new X509Certificate2(cert);
            return true;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token).ConfigureAwait(false);
        if (certificate is not X509Certificate2 cert2)
        {
            return null;
        }

        using (cert2)
        {
            if (!cert2.Issuer.Contains("BBL", StringComparison.OrdinalIgnoreCase) && !cert2.Issuer.Contains("Bambu", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? serial = cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            bool looksLikeSerial = serial is { Length: >= 10 and <= 20 } && serial.All(char.IsAsciiLetterOrDigit);
            return new DiscoveredPrinter(
                PrinterKind.BambuLab,
                host,
                Serial: looksLikeSerial ? serial : null,
                Model: looksLikeSerial ? BambuParser.ModelFromSerial(serial) : null,
                NeedsSecret: true);
        }
    }

    private static async Task<DiscoveredPrinter?> CrealityAsync(string host, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonObject> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        await using JsonWebSocketSession session = await JsonWebSocketSession.ConnectAsync(
            new Uri($"ws://{authority}:{CrealityDriver.DefaultPort}/"), null, text => text.Contains("heart_beat", StringComparison.Ordinal) ? "ok" : null,
            message => first.TrySetResult(message), cancellationToken).ConfigureAwait(false);
        await session.SendAsync("""{"method":"get","params":{"ReqPrinterPara":1}}""", cancellationToken).ConfigureAwait(false);
        Task done = await Task.WhenAny(first.Task, Task.Delay(ProbeTimeout, cancellationToken)).ConfigureAwait(false);
        if (done != first.Task)
        {
            return null;
        }

        JsonObject message = await first.Task.ConfigureAwait(false);
        return new DiscoveredPrinter(PrinterKind.Creality, host, message.NonEmpty("hostname"), Model: message.NonEmpty("model"));
    }

    private static async Task<DiscoveredPrinter?> ElegooAsync(string host, CancellationToken cancellationToken)
    {
        SdcpDevice? device = await SdcpDiscovery.ProbeAsync(host, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        return device is null ? null : new DiscoveredPrinter(PrinterKind.Elegoo, host, device.Name, Model: device.MachineName);
    }

    private static async Task<DiscoveredPrinter?> FlashForgeAsync(string host, CancellationToken cancellationToken)
    {
        DiscoveredPrinter? found = await FlashForgeDiscovery.ProbeAsync(host, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            return found with { Host = host, NeedsSecret = found.Kind == PrinterKind.FlashForge };
        }

        // No discovery reply: an open HTTP API port still says which generation it is.
        if (await IsOpenAsync(host, FlashForgeDriver.DefaultPort, cancellationToken).ConfigureAwait(false))
        {
            return new DiscoveredPrinter(PrinterKind.FlashForge, host, NeedsSecret: true);
        }

        return await IsOpenAsync(host, FlashForgeLegacyDriver.DefaultPort, cancellationToken).ConfigureAwait(false)
            ? new DiscoveredPrinter(PrinterKind.FlashForgeLegacy, host)
            : null;
    }

    private static async Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using TcpClient tcp = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The address to save: the host alone when the port is the kind's default, else host:port.</summary>
    private static string HostFor(PrinterEndpoint web, int port, int defaultPort)
    {
        string authority = web.Host.Contains(':', StringComparison.Ordinal) ? $"[{web.Host}]" : web.Host;
        string withPort = port == defaultPort ? authority : $"{authority}:{port.ToString(CultureInfo.InvariantCulture)}";
        string path = web.BasePath == "/" ? "" : web.BasePath;
        return web.IsHttps ? $"https://{withPort}{path}" : withPort + path;
    }
}
