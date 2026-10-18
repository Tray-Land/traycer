using System.Text.Json.Nodes;
using TrayCer.Core.Drivers.Bambu;
using TrayCer.Core.Drivers.Creality;
using TrayCer.Core.Drivers.Duet;
using TrayCer.Core.Drivers.Elegoo;
using TrayCer.Core.Drivers.FlashForge;
using TrayCer.Core.Drivers.Moonraker;
using TrayCer.Core.Drivers.OctoPrint;
using TrayCer.Core.Drivers.PrusaLink;
using TrayCer.Core.Drivers.Repetier;
using TrayCer.Core.Drivers.Ultimaker;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Tests;

[TestClass]
public sealed class ParserTests
{
    [TestMethod]
    public void OctoPrint_Printing_ReadsJobProgressAndTemperatures()
    {
        PrinterSnapshot s = OctoPrintParser.Parse(Sample.Json("octoprint-job-printing.json"), Sample.Json("octoprint-printer.json"), "http://octopi/thumb.png");

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.45678, s.Progress!.Value, 1e-9);
        Assert.AreEqual(TimeSpan.FromSeconds(3600), s.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(4800), s.Remaining);
        Assert.AreEqual("Benchy 0.2mm PLA.gcode", s.JobName);
        Assert.AreEqual("http://octopi/thumb.png", s.ThumbnailUrl);
        Assert.HasCount(2, s.Temperatures);
        TemperatureReading nozzle = s.Temperatures.Single(t => t.Kind == TemperatureKind.Nozzle);
        Assert.AreEqual("Nozzle", nozzle.Label);
        Assert.AreEqual(214.6, nozzle.Current);
        Assert.AreEqual(215.0, nozzle.Target);
        Assert.IsTrue(s.Details.Any(d => d.Label == "Filament" && d.Value.StartsWith("8", StringComparison.Ordinal)));
        Assert.IsTrue(s.Details.Any(d => d.Label == "Estimate" && d.Value == "PrintTimeGenius"));
    }

    [TestMethod]
    public void OctoPrint_OperationalAtFullCompletion_IsFinished()
    {
        PrinterSnapshot s = OctoPrintParser.Parse(Sample.Json("octoprint-job-finished.json"), null);

        Assert.AreEqual(PrinterState.Finished, s.State);
        Assert.AreEqual(1.0, s.Progress);
        Assert.IsNull(s.Remaining);
        Assert.AreEqual("benchy.gcode", s.JobName);
    }

    [TestMethod]
    [DataRow("Offline", PrinterState.Offline)]
    [DataRow("Offline after error", PrinterState.Error)]
    [DataRow("Paused", PrinterState.Paused)]
    [DataRow("Cancelling", PrinterState.Busy)]
    [DataRow("Starting print from SD", PrinterState.Preparing)]
    [DataRow("Printing from SD", PrinterState.Printing)]
    [DataRow("Operational", PrinterState.Idle)]
    public void OctoPrint_MapsStateText(string text, PrinterState expected)
    {
        Assert.AreEqual(expected, OctoPrintParser.MapState(text, null, null, hasFile: false));
    }

    [TestMethod]
    public void Moonraker_Printing_UsesSlicerProgressMetadataAndChamberSensor()
    {
        IReadOnlyList<string> heaters = MoonrakerParser.HeaterNames(Sample.Json("moonraker-objects-list.json"));
        CollectionAssert.AreEqual(new[] { "extruder", "heater_bed", "temperature_sensor chamber" }, heaters.ToArray());

        const string fileName = "parts/clip_0.2mm_ABS_1h30m.gcode";
        MoonrakerFileInfo file = MoonrakerFileInfo.Parse(Sample.Json("moonraker-metadata.json"), fileName)!;
        Assert.AreEqual("parts/.thumbs/clip_0.2mm_ABS_1h30m-300x300.png", file.ThumbnailPath, "Largest thumbnail up to 400 px, under the file's folder.");

        PrinterSnapshot s = MoonrakerParser.Parse(Sample.Json("moonraker-query-printing.json"), file, "http://voron/thumb.png", heaters);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.5, s.Progress, "display_status (M73) wins over the file position.");
        Assert.AreEqual("clip_0.2mm_ABS_1h30m.gcode", s.JobName);
        Assert.AreEqual(TimeSpan.FromSeconds(2700), s.Elapsed);

        // File ETA: 2700.4/0.5 - 2700.4 = 2700.4; slicer: 5400 - 2700.4 = 2699.6; averaged.
        Assert.AreEqual(2700, s.Remaining!.Value.TotalSeconds, 1);

        // Layers derived from heights: total = (12 - 0.2)/0.2 + 1 = 60; z 6.2 is layer 31.
        Assert.AreEqual(31, s.Layer);
        Assert.AreEqual(60, s.TotalLayers);

        Assert.HasCount(3, s.Temperatures);
        Assert.AreEqual(48.2, s.Temperatures.Single(t => t.Kind == TemperatureKind.Chamber).Current);
        Assert.IsTrue(s.Details.Any(d => d.Label == "Speed" && d.Value.StartsWith("110", StringComparison.Ordinal)));
        Assert.IsTrue(s.Details.Any(d => d.Label == "Slicer" && d.Value == "OrcaSlicer"));
    }

    [TestMethod]
    public void Moonraker_KlippyShutdown_IsErrorWithFirstLineOfMessage()
    {
        PrinterSnapshot s = MoonrakerParser.Parse(Sample.Json("moonraker-query-shutdown.json"), null, null, ["extruder", "heater_bed"]);

        Assert.AreEqual(PrinterState.Error, s.State);
        Assert.AreEqual("MCU 'mcu' shutdown: Timer too close", s.Message);
        Assert.HasCount(2, s.Temperatures);
    }

    [TestMethod]
    public void Moonraker_PrintingBeforeFirstExtrusion_IsPreparing()
    {
        JsonNode query = JsonNode.Parse("""
            {"result":{"status":{"webhooks":{"state":"ready"},"print_stats":{"state":"printing","filename":"a.gcode","print_duration":0},
             "extruder":{"temperature":120,"target":250},"heater_bed":{"temperature":60,"target":60}}}}
            """)!;

        PrinterSnapshot s = MoonrakerParser.Parse(query, null, null, ["extruder", "heater_bed"]);

        Assert.AreEqual(PrinterState.Preparing, s.State);
        Assert.AreEqual("Heating", s.Message);
    }

    [TestMethod]
    public void Moonraker_Eta_UsesSlicerEarlyAndFileLater()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3500), MoonrakerParser.Remaining(100, 0.05, 3600), "Below 10% only the slicer estimate counts.");
        Assert.AreEqual(TimeSpan.FromSeconds(900), MoonrakerParser.Remaining(900, 0.5, null), "Without metadata, extrapolate from progress.");
        Assert.IsNull(MoonrakerParser.Remaining(0, 0, null));
    }

    [TestMethod]
    public void PrusaLink_Printing_ReadsStatusAndJobFile()
    {
        PrinterSnapshot s = PrusaLinkParser.Parse(Sample.Json("prusalink-status.json"), Sample.Json("prusalink-job.json"), "http://mk4/thumb/l/usb/BENCHY~1.BGC");

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.42, s.Progress!.Value, 1e-9);
        Assert.AreEqual(TimeSpan.FromSeconds(3240), s.Remaining);
        Assert.AreEqual(TimeSpan.FromSeconds(1800), s.Elapsed);
        Assert.AreEqual("benchy_0.4n_0.2mm_PLA_COREONE_1h2m.bgcode", s.JobName);
        Assert.IsTrue(s.Details.Any(d => d.Label == "Speed" && d.Value.StartsWith("120", StringComparison.Ordinal)));
        Assert.HasCount(2, s.Temperatures);
    }

    [TestMethod]
    [DataRow("ATTENTION", PrinterState.Attention)]
    [DataRow("STOPPED", PrinterState.Cancelled)]
    [DataRow("FINISHED", PrinterState.Finished)]
    [DataRow("READY", PrinterState.Idle)]
    [DataRow("BUSY", PrinterState.Busy)]
    public void PrusaLink_MapsStates(string state, PrinterState expected) => Assert.AreEqual(expected, PrusaLinkParser.MapState(state));

    [TestMethod]
    public void Bambu_Running_ReadsProgressAmsAndDetails()
    {
        PrinterSnapshot s = BambuParser.Parse(Sample.Json("bambu-pushall.json")["print"], "00M09A123456789", DateTimeOffset.FromUnixTimeSeconds(1729403600));

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.63, s.Progress!.Value, 1e-9);
        Assert.AreEqual(TimeSpan.FromMinutes(47), s.Remaining);
        Assert.AreEqual(TimeSpan.FromHours(1), s.Elapsed);
        Assert.AreEqual(112, s.Layer);
        Assert.AreEqual(180, s.TotalLayers);
        Assert.AreEqual("Phone stand", s.JobName);
        Assert.AreEqual("X1 Carbon", s.Model);
        Assert.IsTrue(s.Temperatures.Any(t => t.Kind == TemperatureKind.Chamber), "X1 Carbon has a chamber sensor.");

        DetailItem filament = s.Details.Single(d => d.Label == "Filament");
        Assert.AreEqual("PLA Silk · AMS 2, slot 2", filament.Value, "tray_now 5 is unit 1, slot 1.");
        Assert.AreEqual("#2850E0", filament.Color);
        Assert.IsTrue(s.Details.Any(d => d.Label == "Speed" && d.Value == "Standard"));
        Assert.IsTrue(s.Details.Any(d => d.Label == "Wi-Fi" && d.Value == "-48 dBm"));
    }

    [TestMethod]
    public void Bambu_StagesAndErrors_MapToStates()
    {
        PrinterSnapshot heating = BambuParser.Parse(JsonNode.Parse("""{"gcode_state":"RUNNING","stg_cur":2,"mc_percent":0}"""), "01P00A000000000", DateTimeOffset.UtcNow);
        Assert.AreEqual(PrinterState.Preparing, heating.State);
        Assert.AreEqual("Heating bed", heating.Message);
        Assert.AreEqual("P1S", heating.Model);
        Assert.IsFalse(heating.Temperatures.Any(t => t.Kind == TemperatureKind.Chamber));

        PrinterSnapshot runout = BambuParser.Parse(JsonNode.Parse("""{"gcode_state":"PAUSE","stg_cur":6}"""), null, DateTimeOffset.UtcNow);
        Assert.AreEqual(PrinterState.Attention, runout.State);
        Assert.AreEqual("Filament ran out", runout.Message);

        PrinterSnapshot cancelled = BambuParser.Parse(JsonNode.Parse("""{"gcode_state":"FAILED","print_error":50348044}"""), null, DateTimeOffset.UtcNow);
        Assert.AreEqual(PrinterState.Cancelled, cancelled.State);

        PrinterSnapshot failed = BambuParser.Parse(JsonNode.Parse("""{"gcode_state":"FAILED","print_error":117473286,"hms":[{"attr":50336256,"code":131073}]}"""), null, DateTimeOffset.UtcNow);
        Assert.AreEqual(PrinterState.Error, failed.State);
        Assert.AreEqual("Print failed (error 0700-8006) · HMS 0300-1200-0002-0001", failed.Message);
    }

    [TestMethod]
    public void Bambu_DeltaMergesIntoFullReport()
    {
        BambuDriver driver = new(Core.Net.PrinterEndpoint.Parse("10.0.0.5", 8883), "01S00C123456789", "12345678");
        driver.Apply(System.Text.Encoding.UTF8.GetBytes(Sample.Text("bambu-pushall.json")));
        driver.Apply("""{"print":{"mc_percent":64,"command":"push_status","msg":1}}"""u8);

        PrinterSnapshot s = driver.Current(DateTimeOffset.UtcNow);

        Assert.AreEqual(0.64, s.Progress!.Value, 1e-9);
        Assert.AreEqual("Phone stand", s.JobName, "Fields not in the delta survive.");
    }

    [TestMethod]
    public void Creality_Printing_ReadsStringNumbersAndFileName()
    {
        PrinterSnapshot s = CrealityParser.Parse(Sample.Json("creality-status.json"), "http://k1c/preview.png");

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.37, s.Progress!.Value, 1e-9);
        Assert.AreEqual(TimeSpan.FromSeconds(1260), s.Remaining, "printLeftTime arrived as a string.");
        Assert.AreEqual("Cable clip_PLA_32m.gcode", s.JobName);
        Assert.AreEqual(18, s.Layer);
        Assert.AreEqual(60, s.TotalLayers);
        Assert.AreEqual("K1C", s.Model);
        Assert.AreEqual(220.12, s.Temperatures.Single(t => t.Kind == TemperatureKind.Nozzle).Current);
        Assert.IsFalse(s.Temperatures.Any(t => t.Kind == TemperatureKind.Chamber), "boxTemp 0 means no sensor.");
    }

    [TestMethod]
    public void Creality_StateTable_FollowsFileAndProgress()
    {
        Assert.AreEqual(PrinterState.Idle, CrealityParser.Parse(JsonNode.Parse("""{"state":1,"printFileName":""}""")).State);
        Assert.AreEqual(PrinterState.Finished, CrealityParser.Parse(JsonNode.Parse("""{"state":0,"printFileName":"a.gcode","printProgress":100}""")).State);
        Assert.AreEqual(PrinterState.Paused, CrealityParser.Parse(JsonNode.Parse("""{"state":5,"printFileName":"a.gcode","printProgress":40}""")).State);
        Assert.AreEqual(PrinterState.Cancelled, CrealityParser.Parse(JsonNode.Parse("""{"state":4,"printFileName":"a.gcode","printProgress":40}""")).State);
        Assert.AreEqual(PrinterState.Preparing, CrealityParser.Parse(JsonNode.Parse("""{"state":0,"printFileName":"a.gcode","printProgress":0}""")).State);

        PrinterSnapshot stuck = CrealityParser.Parse(JsonNode.Parse("""{"state":1,"printFileName":"a.gcode","printProgress":10,"err":{"errcode":2000}}"""));
        Assert.AreEqual(PrinterState.Printing, stuck.State, "A stale error code doesn't end a running print.");
        Assert.AreEqual("Printer error 2000", stuck.Message);
    }

    [TestMethod]
    public void Sdcp_CentauriCarbon_UsesFdmTableAndSeconds()
    {
        JsonNode status = Sample.Json("sdcp-centauri-status.json")["Status"]!;
        PrinterSnapshot s = SdcpParser.Parse(status, "Centauri Carbon");

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.25, s.Progress, "Progress field wins over layers.");
        Assert.AreEqual(TimeSpan.FromSeconds(3600), s.Remaining, "Centauri ticks are seconds.");
        Assert.AreEqual(16, s.Layer);
        Assert.AreEqual("Centauri Carbon", s.Model);
        Assert.HasCount(3, s.Temperatures);
    }

    [TestMethod]
    public void Sdcp_Resin_UsesSpecTableAndMilliseconds()
    {
        JsonNode status = Sample.Json("sdcp-resin-status.json")["Status"]!;
        Assert.IsFalse(SdcpParser.IsFdm("Saturn 4 Ultra", status));

        PrinterSnapshot s = SdcpParser.Parse(status, "Saturn 4 Ultra");

        Assert.AreEqual(PrinterState.Printing, s.State, "Status 3 is exposing.");
        Assert.AreEqual(0.25, s.Progress, "300 of 1200 layers.");
        Assert.AreEqual(TimeSpan.FromMinutes(90), s.Remaining, "Resin ticks are milliseconds.");
        Assert.IsTrue(s.Temperatures.Any(t => t.Label == "UV LED"));
    }

    [TestMethod]
    public void FlashForge_Detail_ReadsFields()
    {
        JsonNode response = Sample.Json("flashforge-detail.json");
        PrinterSnapshot s = FlashForgeParser.Parse(response["detail"], url => url);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.4, s.Progress);
        Assert.AreEqual(TimeSpan.FromSeconds(2280), s.Remaining);
        Assert.AreEqual(41, s.Layer);
        Assert.AreEqual(103, s.TotalLayers);
        Assert.AreEqual("Adventurer 5M Pro", s.Model);
        Assert.AreEqual("http://192.168.1.61:8898/getThum?fileName=gear_set.gcode", s.ThumbnailUrl);
        Assert.IsTrue(s.Details.Any(d => d.Label == "Filament" && d.Value == "PLA"));
    }

    [TestMethod]
    public void FlashForgeLegacy_ParsesTextReplies()
    {
        const string m119 = "CMD M119 Received.\nEndstop: X-max:0 Y-max:0 Z-min:1\nMachineStatus: BUILDING_FROM_SD\nMoveMode: MOVING\nStatus: S:1 L:0 J:0 F:0\nLED: 1\nCurrentFile: dragon.gx\nok\n";
        const string m27 = "CMD M27 Received.\nSD printing byte 46/100\nLayer: 120/260\nok\n";
        const string m105 = "CMD M105 Received.\nT0:210 /210 B:50/50\nok\n";
        const string m115 = "CMD M115 Received.\nMachine Type: FlashForge Adventurer III\nMachine Name: Garage\nFirmware: v1.3.7\nok\n";

        PrinterSnapshot s = FlashForgeLegacyParser.Parse(m119, m27, m105, m115);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.46, s.Progress);
        Assert.AreEqual(120, s.Layer);
        Assert.AreEqual(260, s.TotalLayers);
        Assert.AreEqual("dragon.gx", s.JobName);
        Assert.AreEqual("Adventurer III", s.Model);
        Assert.HasCount(2, s.Temperatures);
        Assert.AreEqual(210, s.Temperatures[0].Target);
    }

    [TestMethod]
    public void Duet_Processing_ReadsJobAndHeaters()
    {
        PrinterSnapshot s = DuetParser.Parse(
            Sample.Json("duet-state.json")["result"],
            Sample.Json("duet-job.json")["result"],
            Sample.Json("duet-heat.json")["result"],
            Sample.Json("duet-tools.json")["result"]);

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.35, s.Progress);
        Assert.AreEqual(TimeSpan.FromSeconds(4800), s.Remaining, "Slicer estimate first.");
        Assert.AreEqual("bracket.gcode", s.JobName);
        Assert.AreEqual(40, s.Layer);
        Assert.AreEqual(120, s.TotalLayers);
        Assert.AreEqual("Printing part 2 of 3", s.Message);
        Assert.HasCount(2, s.Temperatures);
        Assert.AreEqual(240, s.Temperatures.Single(t => t.Kind == TemperatureKind.Nozzle).Target);
    }

    [TestMethod]
    public void Ultimaker_Printing_ComputesRemaining()
    {
        PrinterSnapshot s = UltimakerParser.Parse(Sample.Json("ultimaker-printer.json"), Sample.Json("ultimaker-job.json"));

        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.8, s.Progress);
        Assert.AreEqual(TimeSpan.FromSeconds(2000), s.Remaining);
        Assert.AreEqual("UMS5_enclosure_hinge", s.JobName);
        Assert.HasCount(3, s.Temperatures);
        Assert.AreEqual("Nozzle 2", s.Temperatures[1].Label);
    }

    [TestMethod]
    public void Ultimaker_WaitCleanup_IsFinishedOrCancelled()
    {
        JsonNode printer = JsonNode.Parse("""{"status":"printing"}""")!;
        Assert.AreEqual(PrinterState.Finished, UltimakerParser.Parse(printer, JsonNode.Parse("""{"state":"wait_cleanup","result":"Finished","name":"a"}""")).State);
        Assert.AreEqual(PrinterState.Cancelled, UltimakerParser.Parse(printer, JsonNode.Parse("""{"state":"wait_cleanup","result":"Aborted","name":"a"}""")).State);
        Assert.AreEqual(PrinterState.Idle, UltimakerParser.Parse(JsonNode.Parse("""{"status":"idle"}"""), null).State);
    }

    [TestMethod]
    public void Repetier_FindsPrinterBySlugOrFirstActive()
    {
        JsonNode list = Sample.Json("repetier-list.json");

        Assert.AreEqual("Prusa_i3", RepetierParser.FindPrinter(list, "prusa_i3").Text("slug"));
        Assert.AreEqual("delta", RepetierParser.FindPrinter(list, null).Text("slug"));

        PrinterSnapshot s = RepetierParser.Parse(RepetierParser.FindPrinter(list, "Prusa_i3"), Sample.Json("repetier-state.json")["Prusa_i3"]);
        Assert.AreEqual(PrinterState.Printing, s.State);
        Assert.AreEqual(0.715, s.Progress!.Value, 1e-9);
        Assert.AreEqual(TimeSpan.FromSeconds(3000), s.Remaining);
        Assert.AreEqual(77, s.Layer);
        Assert.HasCount(2, s.Temperatures);

        PrinterSnapshot offline = RepetierParser.Parse(RepetierParser.FindPrinter(list, "delta"), null);
        Assert.AreEqual(PrinterState.Offline, offline.State);
    }
}
