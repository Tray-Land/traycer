#:sdk Microsoft.NET.Sdk.Web
#:project ../TrayCer.Core/TrayCer.Core.csproj

// Fake network printers for developing and testing Traycer without real hardware.
//
//   dotnet run --file scripts/FakePrinters.cs     (from the repo root; --file, or it runs the app)
//
// Every printer runs a scripted print on localhost (about 8 s heating, then printing), each at
// its own pace so they finish at different times. Add them in Traycer by address:
//
//   Bambu Lab        127.0.0.1          serial 01P00A391800123, access code 12345678
//   Klipper          127.0.0.1:7125
//   OctoPrint        127.0.0.1:5000     API key TESTKEY
//   Creality         127.0.0.1
//   Elegoo           127.0.0.1
//   FlashForge       127.0.0.1          serial SNMOMC9900123, check code 12345678
//
// "Find printers" sees the Bambu (NOTIFY to UDP 2021), Elegoo (UDP 3000) and FlashForge
// (UDP 48899) printers. Control the scripts at http://localhost:7999:
//   /status, /reset?p=all, /pause?p=bambu, /finish?p=octoprint, /error?p=klipper, /idle?p=creality
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Net.Mqtt;

const string BambuSerial = "01P00A391800123";
const string AccessCode = "12345678";
const string OctoKey = "TESTKEY";
const string FlashForgeSerial = "SNMOMC9900123";

Dictionary<string, Job> jobs = new()
{
    ["bambu"] = new Job("Phone stand", printSeconds: 240, startOffset: 30),
    ["klipper"] = new Job("parts/clip_0.2mm_ABS_4m.gcode", printSeconds: 240, startOffset: 120),
    ["octoprint"] = new Job("Benchy 0.2mm PLA.gcode", printSeconds: 300, startOffset: 60),
    ["creality"] = new Job("/usr/data/printer_data/gcodes/Cable clip_PLA.gcode", printSeconds: 420, startOffset: 20),
    ["elegoo"] = new Job("ECC_Vase_PLA0.2.gcode", printSeconds: 360, startOffset: 90),
    ["flashforge"] = new Job("gear_set.gcode", printSeconds: 600, startOffset: 200),
};

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://localhost:7125", "http://localhost:5000", "http://localhost:9999", "http://localhost:3030", "http://localhost:8898", "http://localhost:7999");
WebApplication app = builder.Build();
app.UseWebSockets();

// ---------------------------------------------------------------------------------------- Klipper
app.MapGet("/server/info", () => Json(new JsonObject { ["result"] = new JsonObject { ["klippy_state"] = "ready", ["moonraker_version"] = "fake" } })).RequireHost("*:7125");
app.MapGet("/printer/objects/list", () => Json(JsonNode.Parse("""{"result":{"objects":["webhooks","print_stats","virtual_sdcard","display_status","gcode_move","extruder","heater_bed","temperature_sensor chamber"]}}""")!)).RequireHost("*:7125");
app.MapGet("/printer/objects/query", () =>
{
    Job job = jobs["klipper"];
    string state = job.Phase switch { "idle" => "standby", "paused" => "paused", "done" => "complete", "error" => "error", _ => "printing" };
    bool error = job.Phase == "error";
    JsonObject status = new()
    {
        ["webhooks"] = new JsonObject { ["state"] = error ? "shutdown" : "ready", ["state_message"] = error ? "MCU 'mcu' shutdown: Timer too close\nOnce the underlying issue is corrected, use FIRMWARE_RESTART" : "Printer is ready" },
        ["print_stats"] = new JsonObject
        {
            ["filename"] = job.Phase == "idle" ? "" : job.File, ["state"] = state, ["print_duration"] = job.Elapsed, ["total_duration"] = job.T,
            ["filament_used"] = job.Progress * 3800, ["message"] = "", ["info"] = new JsonObject { ["total_layer"] = null, ["current_layer"] = null },
        },
        ["virtual_sdcard"] = new JsonObject { ["progress"] = job.Progress, ["is_active"] = state == "printing" },
        ["display_status"] = new JsonObject { ["progress"] = job.Progress, ["message"] = null },
        ["gcode_move"] = new JsonObject { ["speed_factor"] = 1.0, ["gcode_position"] = new JsonArray(100, 100, 0.2 + (job.Progress * 11.8), 0) },
        ["extruder"] = new JsonObject { ["temperature"] = job.Nozzle(250), ["target"] = job.Heating ? 250 : 0 },
        ["heater_bed"] = new JsonObject { ["temperature"] = job.Bed(100), ["target"] = job.Heating ? 100 : 0 },
        ["temperature_sensor chamber"] = new JsonObject { ["temperature"] = 38.5 },
    };
    return Json(new JsonObject { ["result"] = new JsonObject { ["eventtime"] = job.T, ["status"] = status } });
}).RequireHost("*:7125");
app.MapGet("/server/files/metadata", () => Json(JsonNode.Parse($$$"""
    {"result":{"filename":"{{{jobs["klipper"].File}}}","slicer":"OrcaSlicer","estimated_time":{{{jobs["klipper"].PrintSeconds}}},"layer_height":0.2,"first_layer_height":0.2,"object_height":12.0,
     "thumbnails":[{"width":32,"height":32,"relative_path":".thumbs/clip-32x32.png"},{"width":300,"height":300,"relative_path":".thumbs/clip-300x300.png"}]}}
    """)!)).RequireHost("*:7125");
app.MapGet("/server/files/gcodes/{**path}", () => Results.Bytes(Thumbnail.Png(0x2850E0), "image/png")).RequireHost("*:7125");

// -------------------------------------------------------------------------------------- OctoPrint
app.MapGet("/plugin/appkeys/probe", () => Results.NoContent()).RequireHost("*:5000");
app.MapGet("/api/job", (HttpRequest request) =>
{
    if (request.Headers["X-Api-Key"] != OctoKey)
    {
        return Results.StatusCode(403);
    }

    Job job = jobs["octoprint"];
    string state = job.Phase switch { "idle" or "done" => "Operational", "paused" => "Paused", "error" => "Offline after error", _ => "Printing" };
    return Json(new JsonObject
    {
        ["job"] = new JsonObject
        {
            ["file"] = job.Phase == "idle" ? new JsonObject() : new JsonObject { ["name"] = "benchy.gcode", ["display"] = job.File, ["origin"] = "local", ["path"] = "boats/benchy.gcode" },
            ["estimatedPrintTime"] = job.PrintSeconds,
            ["filament"] = new JsonObject { ["tool0"] = new JsonObject { ["length"] = 5300.0 } },
        },
        ["progress"] = new JsonObject
        {
            ["completion"] = job.Phase == "idle" ? null : job.Progress * 100, ["printTime"] = job.Elapsed, ["printTimeLeft"] = job.Remaining, ["printTimeLeftOrigin"] = "estimate",
        },
        ["state"] = state,
        ["error"] = job.Phase == "error" ? "Thermal runaway on heater_bed" : "",
    });
}).RequireHost("*:5000");
app.MapGet("/api/printer", (HttpRequest request) =>
{
    if (request.Headers["X-Api-Key"] != OctoKey)
    {
        return Results.StatusCode(403);
    }

    Job job = jobs["octoprint"];
    return Json(new JsonObject
    {
        ["temperature"] = new JsonObject
        {
            ["tool0"] = new JsonObject { ["actual"] = job.Nozzle(215), ["target"] = job.Heating ? 215.0 : 0.0 },
            ["bed"] = new JsonObject { ["actual"] = job.Bed(60), ["target"] = job.Heating ? 60.0 : 0.0 },
        },
        ["state"] = new JsonObject
        {
            ["text"] = job.Phase == "printing" ? "Printing" : "Operational",
            ["flags"] = new JsonObject { ["operational"] = true, ["printing"] = job.Phase is "printing" or "heating", ["paused"] = job.Phase == "paused", ["error"] = job.Phase == "error" },
        },
    });
}).RequireHost("*:5000");
app.MapGet("/api/files/local/{**path}", () => Json(new JsonObject { ["thumbnail"] = "plugin/prusaslicerthumbnails/thumbnail/boats/benchy.png?1729" })).RequireHost("*:5000");
app.MapGet("/plugin/prusaslicerthumbnails/thumbnail/{**path}", () => Results.Bytes(Thumbnail.Png(0xFF6A13), "image/png")).RequireHost("*:5000");

// ------------------------------------------------------------------------------- Creality (K1C)
app.Map("/", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest();
    }

    string? subProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains("wsslicer") ? "wsslicer" : null;
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
    await CrealitySession(socket, jobs["creality"], context.RequestAborted);
    return Results.Empty;
}).RequireHost("*:9999");

// ------------------------------------------------------------------------- Elegoo Centauri Carbon
app.Map("/websocket", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest();
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await SdcpSession(socket, jobs["elegoo"], context.RequestAborted);
    return Results.Empty;
}).RequireHost("*:3030");

// ------------------------------------------------------------------------- FlashForge 5M Pro
app.MapPost("/detail", async (HttpRequest request) =>
{
    JsonNode? body = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync());
    if (body?["serialNumber"]?.ToString() != FlashForgeSerial || body?["checkCode"]?.ToString() != AccessCode)
    {
        return Json(new JsonObject { ["code"] = 1, ["message"] = "Check code error" });
    }

    Job job = jobs["flashforge"];
    string status = job.Phase switch { "heating" => "heating", "printing" => "printing", "paused" => "paused", "done" => "completed", "error" => "error", _ => "ready" };
    return Json(new JsonObject
    {
        ["code"] = 0,
        ["message"] = "Success",
        ["detail"] = new JsonObject
        {
            ["status"] = status, ["model"] = "Adventurer 5M Pro", ["name"] = "Workshop 5M", ["printFileName"] = job.Phase == "idle" ? "" : job.File,
            ["printProgress"] = job.Progress, ["printDuration"] = job.Elapsed, ["estimatedTime"] = job.Remaining,
            ["printLayer"] = (int)(job.Progress * 103), ["targetPrintLayer"] = 103, ["leftTemp"] = job.Nozzle(220), ["leftTargetTemp"] = job.Heating ? 220 : 0,
            ["platTemp"] = job.Bed(55), ["platTargetTemp"] = job.Heating ? 55 : 0, ["chamberTemp"] = 0, ["nozzleCnt"] = 1, ["nozzleModel"] = "0.4mm",
            ["leftFilamentType"] = "PLA", ["printSpeedAdjust"] = 100, ["errorCode"] = job.Phase == "error" ? "E0016" : "",
        },
    });
}).RequireHost("*:8898");

// ------------------------------------------------------------------------------------ Control
app.MapGet("/status", () => string.Join('\n', jobs.Select(j => $"{j.Key,-10} {j.Value.Phase,-9} {j.Value.Progress:P0}"))).RequireHost("*:7999");
app.MapGet("/{action}", (string action, string? p) =>
{
    foreach ((string name, Job job) in jobs.Where(j => p is null or "all" || j.Key == p))
    {
        switch (action)
        {
            case "reset": job.Reset(); break;
            case "pause": job.TogglePause(); break;
            case "finish": job.FinishSoon(); break;
            case "error": job.Error = !job.Error; break;
            case "idle": job.Idle = !job.Idle; break;
            default: return Results.NotFound();
        }
    }

    return Results.Text(string.Join('\n', jobs.Select(j => $"{j.Key,-10} {j.Value.Phase,-9} {j.Value.Progress:P0}")));
}).RequireHost("*:7999");

CancellationToken stopping = app.Lifetime.ApplicationStopping;
Background("Bambu broker", () => BambuBroker(jobs["bambu"], stopping));
Background("Bambu announcer", () => BambuAnnouncer(stopping));
Background("Elegoo discovery", () => UdpResponder(3000, SdcpDiscoveryReply, stopping));
Background("FlashForge discovery", () => UdpResponder(48899, FlashForgeDiscoveryReply, stopping));

Console.WriteLine("Fake printers running on 127.0.0.1. Control: http://localhost:7999/status");
app.Run();

static IResult Json(JsonNode node) => Results.Text(node.ToJsonString(), "application/json");

static void Background(string name, Func<Task> work) => _ = Task.Run(async () =>
{
    try
    {
        await work();
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{name} stopped: {ex}");
    }
});

// Creality pushes changed fields every couple of seconds, heartbeats every 10, and answers ReqPrinterPara with everything.
static async Task CrealitySession(WebSocket socket, Job job, CancellationToken cancellationToken)
{
    JsonObject Full() => new()
    {
        ["model"] = "K1C", ["hostname"] = "K1C-7A21", ["state"] = job.Phase switch { "printing" => 1, "heating" => 0, "paused" => 5, "done" => 2, _ => 0 },
        ["printFileName"] = job.Phase == "idle" ? "" : job.File, ["printProgress"] = (int)(job.Progress * 100), ["printJobTime"] = (int)job.Elapsed,
        ["printLeftTime"] = ((int)job.Remaining).ToString(), ["layer"] = (int)(job.Progress * 60), ["TotalLayer"] = 60,
        ["nozzleTemp"] = job.Nozzle(220).ToString("0.00"), ["targetNozzleTemp"] = job.Heating ? 220 : 0, ["bedTemp0"] = job.Bed(60).ToString("0.00"),
        ["targetBedTemp0"] = job.Heating ? 60 : 0, ["boxTemp"] = 0, ["curFeedratePct"] = 100, ["usedMaterialLength"] = job.Progress * 2600,
        ["err"] = new JsonObject { ["errcode"] = job.Phase == "error" ? 2001 : 0, ["key"] = 0 },
    };

    Task receive = Task.Run(async () =>
    {
        byte[] buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("ReqPrinterPara", StringComparison.Ordinal))
            {
                await Send(socket, Full().ToJsonString(), cancellationToken);
            }
        }
    }, cancellationToken);

    int tick = 0;
    while (socket.State == WebSocketState.Open && !receive.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        JsonObject full = Full();
        JsonObject delta = new();
        foreach (string key in new[] { "state", "printProgress", "printJobTime", "printLeftTime", "layer", "nozzleTemp", "bedTemp0", "targetNozzleTemp", "printFileName", "err" })
        {
            delta[key] = full[key]?.DeepClone();
        }

        await Send(socket, delta.ToJsonString(), cancellationToken);
        if (++tick % 5 == 0)
        {
            await Send(socket, """{"ModeCode":"heart_beat","msg":"2026-10-18 12:00:00"}""", cancellationToken);
        }
    }
}

// SDCP: answers Cmd 0 (status) and Cmd 1 (attributes), "ping" with "pong", and pushes status every 3 s.
static async Task SdcpSession(WebSocket socket, Job job, CancellationToken cancellationToken)
{
    const string id = "8e3e4f0d0a090f0a";
    string Status()
    {
        int code = job.Phase switch { "heating" => 16, "printing" => 13, "paused" => 6, "done" => 9, "error" => 14, _ => 0 };
        JsonObject message = new()
        {
            ["Status"] = new JsonObject
            {
                ["CurrentStatus"] = new JsonArray(job.Phase is "idle" or "done" ? 0 : 1),
                ["TempOfNozzle"] = job.Nozzle(220), ["TempTargetNozzle"] = job.Heating ? 220 : 0, ["TempOfHotbed"] = job.Bed(60), ["TempTargetHotbed"] = job.Heating ? 60 : 0,
                ["TempOfBox"] = 31.2, ["TempTargetBox"] = 0,
                ["PrintInfo"] = new JsonObject
                {
                    ["Status"] = code, ["CurrentLayer"] = (int)(job.Progress * 80), ["TotalLayer"] = 80, ["CurrentTicks"] = (int)job.Elapsed,
                    ["TotalTicks"] = (int)job.PrintSeconds, ["Filename"] = job.Phase == "idle" ? "" : job.File, ["ErrorNumber"] = 0,
                    ["TaskId"] = "fake-task", ["PrintSpeedPct"] = 100, ["Progress"] = (int)(job.Progress * 100),
                },
            },
            ["MainboardID"] = id, ["TimeStamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["Topic"] = "sdcp/status/" + id,
        };
        return message.ToJsonString();
    }

    string Attributes() => new JsonObject
    {
        ["Attributes"] = new JsonObject { ["Name"] = "Centauri Carbon", ["MachineName"] = "Elegoo Centauri Carbon", ["BrandName"] = "ELEGOO", ["MainboardID"] = id, ["FirmwareVersion"] = "V1.1.29" },
        ["MainboardID"] = id, ["TimeStamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["Topic"] = "sdcp/attributes/" + id,
    }.ToJsonString();

    Task receive = Task.Run(async () =>
    {
        byte[] buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (text == "ping")
            {
                await Send(socket, "pong", cancellationToken);
            }
            else if (JsonNode.Parse(text)?["Data"]?["Cmd"]?.GetValue<int>() is { } cmd)
            {
                await Send(socket, cmd == 1 ? Attributes() : Status(), cancellationToken);
            }
        }
    }, cancellationToken);

    while (socket.State == WebSocketState.Open && !receive.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await Send(socket, Status(), cancellationToken);
    }
}

static Task Send(WebSocket socket, string text, CancellationToken cancellationToken) => Ws.Send(socket, text, cancellationToken);

// A Bambu-style MQTT broker over TLS: checks bblp + access code, answers pushall with a full report, then sends deltas.
static async Task BambuBroker(Job job, CancellationToken stopping)
{
    X509Certificate2 certificate = BambuCertificate(BambuSerial);
    TcpListener listener = new(IPAddress.Loopback, 8883);
    listener.Start();
    while (!stopping.IsCancellationRequested)
    {
        TcpClient client = await listener.AcceptTcpClientAsync(stopping);
        _ = Task.Run(async () =>
        {
            using (client)
            {
                try
                {
                    await using SslStream ssl = new(client.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(certificate);
                    await BambuSession(ssl, job, stopping);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                {
                    // Client went away.
                }
            }
        }, stopping);
    }
}

static async Task BambuSession(Stream stream, Job job, CancellationToken stopping)
{
    SemaphoreSlim writeLock = new(1, 1);
    async Task Write(byte[] packet)
    {
        await writeLock.WaitAsync(stopping);
        try { await stream.WriteAsync(packet, stopping); } finally { writeLock.Release(); }
    }

    JsonObject Report(bool full)
    {
        string state = job.Phase switch { "heating" => "RUNNING", "printing" => "RUNNING", "paused" => "PAUSE", "done" => "FINISH", "error" => "FAILED", _ => "IDLE" };
        JsonObject print = new()
        {
            ["command"] = "push_status", ["msg"] = full ? 0 : 1, ["sequence_id"] = "1",
            ["gcode_state"] = state, ["stg_cur"] = job.Phase switch { "heating" => 2, "paused" => 6, "printing" => 0, _ => 255 },
            ["mc_percent"] = (int)(job.Progress * 100), ["mc_remaining_time"] = (int)Math.Ceiling(job.Remaining / 60),
            ["layer_num"] = (int)(job.Progress * 180), ["nozzle_temper"] = job.Nozzle(220), ["bed_temper"] = job.Bed(55),
            ["print_error"] = job.Phase == "error" ? 117473286 : 0,
        };
        if (full)
        {
            print["total_layer_num"] = 180;
            print["subtask_name"] = job.Phase == "idle" ? "" : job.File;
            print["gcode_start_time"] = job.Start.ToUnixTimeSeconds().ToString();
            print["nozzle_target_temper"] = job.Heating ? 220 : 0;
            print["bed_target_temper"] = job.Heating ? 55 : 0;
            print["spd_lvl"] = 2;
            print["nozzle_diameter"] = "0.4";
            print["nozzle_type"] = "hardened_steel";
            print["wifi_signal"] = "-48dBm";
            print["hms"] = new JsonArray();
            print["ams"] = JsonNode.Parse("""
                {"tray_now":"1","ams":[{"id":"0","humidity":"4","temp":"26.1","tray":[
                  {"id":"0","tray_type":"PLA","tray_sub_brands":"PLA Basic","tray_color":"FFFFFFFF"},
                  {"id":"1","tray_type":"PLA","tray_sub_brands":"PLA Matte","tray_color":"FF6A13FF"}]}]}
                """);
        }

        return new JsonObject { ["print"] = print };
    }

    MqttPacket connect = await MqttCodec.ReadAsync(stream, 65536, stopping);
    bool authorized = Encoding.UTF8.GetString(connect.Body).EndsWith(AccessCode, StringComparison.Ordinal) && Encoding.UTF8.GetString(connect.Body).Contains("bblp", StringComparison.Ordinal);
    await Write([0x20, 2, 0, authorized ? (byte)0 : (byte)5]);
    if (!authorized)
    {
        return;
    }

    bool subscribed = false;
    using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    Task deltas = Task.Run(async () =>
    {
        while (!session.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), session.Token);
            if (subscribed)
            {
                await Write(MqttCodec.Publish($"device/{BambuSerial}/report", Encoding.UTF8.GetBytes(Report(full: false).ToJsonString())));
            }
        }
    }, session.Token);

    try
    {
        while (true)
        {
            MqttPacket packet = await MqttCodec.ReadAsync(stream, 65536, stopping);
            switch (packet.Type)
            {
                case MqttPacketType.Subscribe:
                    subscribed = Encoding.UTF8.GetString(packet.Body).Contains($"device/{BambuSerial}/report", StringComparison.Ordinal);
                    await Write([0x90, 3, packet.Body[0], packet.Body[1], subscribed ? (byte)0 : (byte)0x80]);
                    break;
                case MqttPacketType.Publish when Encoding.UTF8.GetString(MqttCodec.ParsePublish(packet).Payload.Span).Contains("pushall", StringComparison.Ordinal):
                    await Write(MqttCodec.Publish($"device/{BambuSerial}/report", Encoding.UTF8.GetBytes(Report(full: true).ToJsonString())));
                    break;
                case MqttPacketType.PingReq:
                    await Write([0xD0, 0]);
                    break;
                case MqttPacketType.Disconnect:
                    return;
            }
        }
    }
    finally
    {
        session.Cancel();
    }
}

// Bambu's CA issues each printer a certificate named for its serial number.
static X509Certificate2 BambuCertificate(string serial)
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    using RSA caKey = RSA.Create(2048);
    CertificateRequest caRequest = new("CN=BBL CA, O=\"BBL Technologies Co., Ltd\", C=CN", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    using X509Certificate2 ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

    using RSA key = RSA.Create(2048);
    CertificateRequest request = new($"CN={serial}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using X509Certificate2 leaf = request.Create(ca, now.AddDays(-1), now.AddMonths(6), RandomNumberGenerator.GetBytes(8));
    using X509Certificate2 withKey = leaf.CopyWithPrivateKey(key);

    // SChannel can't use an ephemeral key; a round trip through PKCS#12 gives it a real one.
    return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
}

// Bambu printers announce themselves every few seconds on UDP 2021.
static async Task BambuAnnouncer(CancellationToken stopping)
{
    using UdpClient udp = new(AddressFamily.InterNetwork);
    udp.Client.IOControl(-1744830452, [0, 0, 0, 0], null); // Ignore ICMP "port unreachable" while nobody listens.
    byte[] notify = Encoding.ASCII.GetBytes(
        "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nServer: UPnP/1.0\r\nLocation: 127.0.0.1\r\n"
        + "NT: urn:bambulab-com:device:3dprinter:1\r\nUSN: " + BambuSerial + "\r\nCache-Control: max-age=1800\r\n"
        + "DevModel.bambu.com: C12\r\nDevName.bambu.com: Fake P1S\r\nDevSignal.bambu.com: -48\r\nDevConnect.bambu.com: lan\r\n\r\n");
    while (!stopping.IsCancellationRequested)
    {
        try
        {
            await udp.SendAsync(notify, new IPEndPoint(IPAddress.Loopback, 2021), stopping);
        }
        catch (SocketException)
        {
            // Nobody listening yet.
        }

        await Task.Delay(TimeSpan.FromSeconds(2), stopping);
    }
}

static async Task UdpResponder(int port, Func<byte[]> reply, CancellationToken stopping)
{
    using UdpClient udp = new(AddressFamily.InterNetwork);
    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    udp.Client.IOControl(-1744830452, [0, 0, 0, 0], null);
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    while (!stopping.IsCancellationRequested)
    {
        UdpReceiveResult request = await udp.ReceiveAsync(stopping);
        await udp.SendAsync(reply(), request.RemoteEndPoint, stopping);
    }
}

static byte[] SdcpDiscoveryReply() => Encoding.UTF8.GetBytes("""
    {"Id":"979d4C788A4a78bC777A870F1A02867A","Data":{"Name":"Centauri Carbon","MachineName":"Elegoo Centauri Carbon","BrandName":"ELEGOO",
     "MainboardIP":"127.0.0.1","MainboardID":"8e3e4f0d0a090f0a","ProtocolVersion":"V3.0.0","FirmwareVersion":"V1.1.29"}}
    """);

static byte[] FlashForgeDiscoveryReply()
{
    byte[] reply = new byte[276];
    Encoding.ASCII.GetBytes("Workshop 5M").CopyTo(reply, 0);
    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0x84), 8899);
    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0x86), 0x2B71);
    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0x88), 0x0024);
    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0x8C), 0x5A02);
    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0x8E), 8898);
    Encoding.ASCII.GetBytes("SNMOMC9900123").CopyTo(reply, 0x92);
    return reply;
}

/// <summary>A scripted print: heating, then printing at a steady rate, then done; pause, error and idle on demand.</summary>
sealed class Job(string file, double printSeconds, double startOffset)
{
    private const double HeatSeconds = 8;

    public string File { get; } = file;

    public double PrintSeconds { get; } = printSeconds;

    public DateTimeOffset Start { get; private set; } = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(startOffset);

    public bool Error { get; set; }

    public bool Idle { get; set; }

    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedTotal;

    public double T => ((_pausedAt ?? DateTimeOffset.UtcNow) - Start - _pausedTotal).TotalSeconds;

    public string Phase => Idle ? "idle" : Error ? "error" : _pausedAt is not null ? "paused" : T < HeatSeconds ? "heating" : T < HeatSeconds + PrintSeconds ? "printing" : "done";

    public bool Heating => Phase is "heating" or "printing" or "paused";

    public double Progress => Phase == "idle" ? 0 : Math.Clamp((T - HeatSeconds) / PrintSeconds, 0, 1);

    public double Elapsed => Math.Clamp(T - HeatSeconds, 0, PrintSeconds);

    public double Remaining => PrintSeconds - Elapsed;

    public double Nozzle(double target) => Heating ? Math.Min(target, 25 + (Math.Max(0, T) * (target - 25) / HeatSeconds)) : 32;

    public double Bed(double target) => Heating ? Math.Min(target, 25 + (Math.Max(0, T) * (target - 25) / HeatSeconds)) : 28;

    public void Reset()
    {
        Start = DateTimeOffset.UtcNow;
        _pausedAt = null;
        _pausedTotal = TimeSpan.Zero;
        Error = false;
        Idle = false;
    }

    public void TogglePause()
    {
        if (_pausedAt is { } at)
        {
            _pausedTotal += DateTimeOffset.UtcNow - at;
            _pausedAt = null;
        }
        else
        {
            _pausedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Jumps to 5 seconds before the end.</summary>
    public void FinishSoon()
    {
        Reset();
        Start = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(HeatSeconds + PrintSeconds - 5);
    }
}

/// <summary>A tiny solid-color PNG with a lighter "object" in the middle, standing in for a slicer preview.</summary>
static class Thumbnail
{
    public static byte[] Png(int rgb)
    {
        const int size = 96;
        byte[] raw = new byte[size * ((size * 3) + 1)];
        for (int y = 0; y < size; y++)
        {
            int row = y * ((size * 3) + 1);
            for (int x = 0; x < size; x++)
            {
                bool inside = Math.Abs(x - (size / 2)) < 28 - (y / 6) && y > 18 && y < 80;
                int color = inside ? rgb : 0x2B2B2B;
                raw[row + 1 + (x * 3)] = (byte)(color >> 16);
                raw[row + 2 + (x * 3)] = (byte)(color >> 8);
                raw[row + 3 + (x * 3)] = (byte)color;
            }
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolor
        Chunk(png, "IHDR", header);
        using MemoryStream compressed = new();
        using (System.IO.Compression.ZLibStream zlib = new(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typeAndData);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>WebSocket sends from the push loop and the reply loop must not overlap.</summary>
static class Ws
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> Locks = [];

    public static async Task Send(WebSocket socket, string text, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetValue(socket, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
