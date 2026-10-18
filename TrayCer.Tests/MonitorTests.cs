using System.Net;
using System.Text;
using TrayCer.Core.Drivers;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Net;

namespace TrayCer.Tests;

[TestClass]
public sealed class MonitorTests
{
    private static readonly PrinterConfig Config = new("id", "Voron", PrinterKind.Moonraker, "10.0.0.9");

    [TestMethod]
    public async Task Poll_ReportsEventsAndRaisesAttentionOnFinish()
    {
        ScriptedDriver driver = new(
            new PrinterSnapshot { State = PrinterState.Printing, Progress = 0.9, JobName = "part.gcode" },
            new PrinterSnapshot { State = PrinterState.Finished, Progress = 1 });
        await using PrinterMonitor monitor = new(Config, driver, new MonitorOptions());
        List<PrinterUpdate> updates = [];
        monitor.Updated += (_, u) => updates.Add(u);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.IsNull(updates[0].Event, "The first reading is a baseline, not news.");
        Assert.AreEqual(PrintEventKind.Finished, updates[1].Event!.Kind);
        Assert.AreEqual("part.gcode", updates[1].Event!.JobName, "The job name carries over from the previous reading.");
        Assert.AreEqual(AttentionLevel.Done, monitor.Status.Attention);
        Assert.AreEqual(ConnectionState.Connected, monitor.Status.Connection);

        Assert.AreEqual(AttentionLevel.None, monitor.Acknowledge().Attention);
    }

    [TestMethod]
    public async Task Poll_KeepsTheLastReadingThroughABriefOutage()
    {
        ScriptedDriver driver = new(new PrinterSnapshot { State = PrinterState.Printing, Progress = 0.5 });
        await using PrinterMonitor monitor = new(Config, driver, new MonitorOptions());

        await monitor.PollOnceAsync();
        driver.Fail = new HttpRequestException("down", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable));
        await monitor.PollOnceAsync();

        Assert.AreEqual(ConnectionState.Reconnecting, monitor.Status.Connection);
        Assert.AreEqual(PrinterState.Printing, monitor.Status.DisplayState, "Still shows the print while it reconnects.");
        Assert.AreEqual("The printer isn't answering. It may be off.", monitor.Status.Problem);
        Assert.AreEqual(1, driver.Suspends, "A failed poll drops any half-open connection.");
    }

    [TestMethod]
    public async Task Poll_RefusedAfterWorkingMeansOff_NotMisconfigured()
    {
        System.Net.Sockets.SocketException refused = new((int)System.Net.Sockets.SocketError.ConnectionRefused);
        ScriptedDriver driver = new(new PrinterSnapshot { State = PrinterState.Idle }) { Fail = new HttpRequestException("refused", refused) };
        await using PrinterMonitor monitor = new(Config, driver, new MonitorOptions());

        await monitor.PollOnceAsync();
        StringAssert.Contains(monitor.Status.Problem, "Check the printer type and port", "Never connected: maybe the wrong type or port.");

        driver.Fail = null;
        await monitor.PollOnceAsync();
        driver.Fail = new HttpRequestException("refused", refused);
        await monitor.PollOnceAsync();
        Assert.AreEqual("The printer isn't accepting connections. It may be off or restarting.", monitor.Status.Problem);
    }

    [TestMethod]
    public async Task Poll_NeverReachedIsUnreachable_AndAuthFailuresSayWhy()
    {
        ScriptedDriver driver = new() { Fail = new PrinterAuthException("The printer rejected the access code.") };
        await using PrinterMonitor monitor = new(Config, driver, new MonitorOptions());

        await monitor.PollOnceAsync();
        Assert.AreEqual(ConnectionState.AuthFailed, monitor.Status.Connection);
        Assert.AreEqual("The printer rejected the access code.", monitor.Status.Problem);
        Assert.AreEqual(PrinterState.Offline, monitor.Status.DisplayState);

        driver.Fail = new TimeoutException();
        await monitor.PollOnceAsync();
        Assert.AreEqual(ConnectionState.Unreachable, monitor.Status.Connection);
    }

    [TestMethod]
    public async Task Loop_RunsUntilDisposed()
    {
        ScriptedDriver driver = new(new PrinterSnapshot { State = PrinterState.Idle });
        PrinterMonitor monitor = new(Config, driver, new MonitorOptions { IdleInterval = TimeSpan.FromMilliseconds(10) });
        TaskCompletionSource threePolls = new();
        int polls = 0;
        monitor.Updated += (_, _) =>
        {
            if (Interlocked.Increment(ref polls) == 3)
            {
                threePolls.TrySetResult();
            }
        };

        monitor.Start();
        await threePolls.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.DisposeAsync();

        Assert.IsTrue(driver.Disposed);
        int after = polls;
        await Task.Delay(50);
        Assert.AreEqual(after, polls, "No polls after dispose.");
    }

    [TestMethod]
    public async Task OctoPrintDriver_SendsTheApiKeyAndParses()
    {
        FakeHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/job" => Json(Sample.Text("octoprint-job-printing.json")),
            "/api/printer" => Json(Sample.Text("octoprint-printer.json")),
            "/api/files/local/boats/benchy_0.2mm_pla.gcode" => Json("""{"thumbnail":"plugin/prusaslicerthumbnails/thumbnail/boats/benchy.png?20241017"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using IPrinterDriver driver = PrinterDriverFactory.Create(new PrinterConfig("o", "Octo", PrinterKind.OctoPrint, "octopi.local"), "KEY123", handler);

        PrinterSnapshot s = await driver.GetSnapshotAsync(CancellationToken.None);
        await driver.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual("http://octopi.local/plugin/prusaslicerthumbnails/thumbnail/boats/benchy.png?20241017", s.ThumbnailUrl);
        Assert.IsTrue(handler.Requests.All(r => r.Headers.GetValues("X-Api-Key").Single() == "KEY123"));
        Assert.AreEqual(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith("/api/files", StringComparison.Ordinal)), "File metadata is fetched once per job.");
    }

    [TestMethod]
    public async Task OctoPrintDriver_RejectedKeyIsAnAuthFailure()
    {
        FakeHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await using IPrinterDriver driver = PrinterDriverFactory.Create(new PrinterConfig("o", "Octo", PrinterKind.OctoPrint, "octopi.local"), "bad", handler);

        await Assert.ThrowsExactlyAsync<PrinterAuthException>(() => driver.GetSnapshotAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task PrusaLinkDriver_FallsBackToTheLegacyApi()
    {
        FakeHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/job" => Json(Sample.Text("octoprint-job-printing.json")),
            "/api/printer" => Json(Sample.Text("octoprint-printer.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using IPrinterDriver driver = PrinterDriverFactory.Create(new PrinterConfig("p", "MK3S", PrinterKind.PrusaLink, "mk3s.local"), "apikey", handler);

        PrinterSnapshot s = await driver.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual("apikey", handler.Requests.Last().Headers.GetValues("X-Api-Key").Single(), "No user name means API-key auth.");
    }

    [TestMethod]
    public async Task MoonrakerDriver_QueriesTheHeatersItFound()
    {
        FakeHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/printer/objects/list" => Json(Sample.Text("moonraker-objects-list.json")),
            "/printer/objects/query" => Json(Sample.Text("moonraker-query-printing.json")),
            "/server/files/metadata" => Json(Sample.Text("moonraker-metadata.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using IPrinterDriver driver = PrinterDriverFactory.Create(Config, null, handler);

        PrinterSnapshot s = await driver.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual("http://10.0.0.9:7125/server/files/gcodes/parts/.thumbs/clip_0.2mm_ABS_1h30m-300x300.png", s.ThumbnailUrl);
        Assert.AreEqual(new Uri("http://10.0.0.9/"), driver.WebInterface, "The web UI is on port 80, beside the API.");
        StringAssert.Contains(handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/printer/objects/query").RequestUri!.Query, "temperature_sensor%20chamber");
    }

    [TestMethod]
    public void Factory_ExplainsWhatsMissing()
    {
        PrinterConfig bambu = new("b", "X1C", PrinterKind.BambuLab, "10.0.0.2");
        Assert.AreEqual("Enter the serial number.", PrinterDriverFactory.MissingField(bambu, "1234"));
        Assert.AreEqual("Enter the access code.", PrinterDriverFactory.MissingField(bambu with { Serial = "00M" }, null));
        Assert.IsNull(PrinterDriverFactory.MissingField(bambu with { Serial = "00M" }, "1234"));
        Assert.AreEqual("Enter the printer's IP address or host name.", PrinterDriverFactory.MissingField(bambu with { Host = " " }, "1"));
        Assert.IsNull(PrinterDriverFactory.MissingField(new PrinterConfig("c", "K1", PrinterKind.Creality, "10.0.0.3"), null), "Creality needs only an address.");
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(respond(request));
        }
    }

    private sealed class ScriptedDriver(params PrinterSnapshot[] script) : IPrinterDriver
    {
        private int _next;

        public Exception? Fail { get; set; }

        public int Suspends { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsPersistent => false;

        public Uri? WebInterface => null;

        public Task<PrinterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                return Task.FromException<PrinterSnapshot>(Fail);
            }

            PrinterSnapshot snapshot = script[Math.Min(_next, script.Length - 1)];
            _next++;
            return Task.FromResult(snapshot);
        }

        public Task<byte[]?> GetThumbnailAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public ValueTask SuspendAsync()
        {
            Suspends++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
