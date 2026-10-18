using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;

namespace TrayCer.Tests;

[TestClass]
public sealed class PresentationTests
{
    private static PrinterStatus Status(string name, PrinterState state, double? progress = null, TimeSpan? remaining = null, AttentionLevel attention = AttentionLevel.None, bool needsMarkComplete = false) => new()
    {
        Config = new PrinterConfig(name, name, PrinterKind.Moonraker, "10.0.0.1"),
        Snapshot = new PrinterSnapshot { State = state, Progress = progress, Remaining = remaining },
        Connection = ConnectionState.Connected,
        Attention = attention,
        NeedsMarkComplete = needsMarkComplete,
    };

    [TestMethod]
    [DataRow(30, "<1m")]
    [DataRow(45 * 60, "45m")]
    [DataRow(80 * 60, "1h 20m")]
    [DataRow(120 * 60, "2h")]
    [DataRow((52 * 3600) + 60, "2d 4h")]
    public void Duration_IsCompact(int seconds, string expected) => Assert.AreEqual(expected, StatusText.Duration(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    public void Percent_RoundsDown() => Assert.AreEqual("99%", StatusText.Percent(0.996));

    [TestMethod]
    public void TimeLine_SaysWhatsLeftAndWhenItsDone()
    {
        DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        PrinterSnapshot printing = new() { State = PrinterState.Printing, Remaining = TimeSpan.FromMinutes(80) };
        StringAssert.StartsWith(StatusText.TimeLine(printing, now), "1h 20m left · done at ");

        PrinterSnapshot paused = printing with { State = PrinterState.Paused };
        Assert.AreEqual("1h 20m left", StatusText.TimeLine(paused, now), "No finish time while paused; it keeps moving.");

        PrinterSnapshot noEstimate = new() { State = PrinterState.Printing, Elapsed = TimeSpan.FromMinutes(5) };
        Assert.AreEqual("Running for 5m", StatusText.TimeLine(noEstimate, now));

        Assert.IsNull(StatusText.TimeLine(new PrinterSnapshot { State = PrinterState.Idle }, now));
    }

    [TestMethod]
    public void Indicator_PriorityIsErrorThenWarningThenDoneThenPausedThenProgress()
    {
        PrinterStatus printing = Status("A", PrinterState.Printing, 0.4, TimeSpan.FromHours(2));
        PrinterStatus paused = Status("B", PrinterState.Paused, 0.7);
        PrinterStatus done = Status("C", PrinterState.Finished, 1, needsMarkComplete: true);
        PrinterStatus failed = Status("D", PrinterState.Error, attention: AttentionLevel.Error);

        Assert.AreEqual(TrayGlyph.Error, TrayIndicator.Compute([printing, paused, done, failed], "Traycer").Glyph);
        TrayIndicator doneIcon = TrayIndicator.Compute([printing, paused, done], "Traycer");
        Assert.AreEqual(TrayGlyph.Done, doneIcon.Glyph);
        Assert.AreEqual("C done", doneIcon.Marquee);
        Assert.AreEqual(TrayGlyph.Paused, TrayIndicator.Compute([printing, paused, done with { NeedsMarkComplete = false }], "Traycer").Glyph, "Marked complete: back to normal.");
        TrayIndicator pausedIcon = TrayIndicator.Compute([printing, paused], "Traycer");
        Assert.AreEqual(TrayGlyph.Paused, pausedIcon.Glyph);
        Assert.AreEqual(0.7, pausedIcon.Progress);
        Assert.AreEqual(TrayGlyph.Progress, TrayIndicator.Compute([printing], "Traycer").Glyph);
        Assert.AreEqual(TrayGlyph.App, TrayIndicator.Compute([Status("E", PrinterState.Idle)], "Traycer").Glyph);
    }

    [TestMethod]
    public void Indicator_ShowsTheHeatersWhileAPrintWarmsUp()
    {
        TemperatureReading[] warming =
        [
            new(TemperatureKind.Nozzle, "Nozzle", 120, 215),
            new(TemperatureKind.Bed, "Bed", 60, 60),
            new(TemperatureKind.Chamber, "Chamber", 31),
        ];
        PrinterStatus cold = Status("Voron", PrinterState.Preparing);
        PrinterStatus preparing = cold with { Snapshot = cold.Snapshot! with { Temperatures = warming } };

        TrayIndicator indicator = TrayIndicator.Compute([preparing], "Traycer");
        Assert.AreEqual(TrayGlyph.Heating, indicator.Glyph);
        Assert.AreEqual((95.0 + 35) / (190 + 35), indicator.Progress!.Value, 1e-9, "The nozzle's long climb counts for more than the bed's short one; the chamber has no target.");

        Assert.AreEqual(TrayGlyph.Progress, TrayIndicator.Compute([preparing, Status("Ender", PrinterState.Printing, 0.3)], "Traycer").Glyph, "A print under way comes first.");
        Assert.AreEqual(TrayGlyph.Progress, TrayIndicator.Compute([Status("Voron", PrinterState.Preparing)], "Traycer").Glyph, "No heater targets: nothing to fill.");
    }

    [TestMethod]
    public void Indicator_KnowsAPrintIsWaitingEvenWhenOutranked()
    {
        PrinterStatus done = Status("P1S", PrinterState.Finished, 1, needsMarkComplete: true);
        PrinterStatus failed = Status("K1", PrinterState.Error, attention: AttentionLevel.Error);

        TrayIndicator outranked = TrayIndicator.Compute([done, failed], "Traycer");
        Assert.AreEqual(TrayGlyph.Error, outranked.Glyph);
        Assert.IsTrue(outranked.HasFinished, "The finished tile comes back after the error; the plate wasn't cleared.");
        Assert.IsFalse(TrayIndicator.Compute([done with { NeedsMarkComplete = false }, failed], "Traycer").HasFinished);
    }

    [TestMethod]
    public void HeatProgress_ClimbsFromRoomTemperature()
    {
        Assert.AreEqual(0.5, new TemperatureReading(TemperatureKind.Nozzle, "Nozzle", 120, 215).HeatProgress!.Value, 1e-9);
        Assert.AreEqual(0, new TemperatureReading(TemperatureKind.Bed, "Bed", 20, 60).HeatProgress, "Colder than the room is no progress.");
        Assert.AreEqual(1, new TemperatureReading(TemperatureKind.Bed, "Bed", 64, 60).HeatProgress, "Overshoot is done.");
        Assert.IsNull(new TemperatureReading(TemperatureKind.Bed, "Bed", 30, 0).HeatProgress, "Off.");
        Assert.IsNull(new PrinterSnapshot { State = PrinterState.Idle, Temperatures = [new(TemperatureKind.Bed, "Bed", 30)] }.HeatProgress);
    }

    [TestMethod]
    public void Built_GrowsLayerByLayerAndEmptiesWhenMarkedComplete()
    {
        PrinterSnapshot printing = new() { State = PrinterState.Printing, Progress = 0.5, Layer = 45, TotalLayers = 200 };
        Assert.AreEqual(0.225, BuildProgress.Built(printing, false), 1e-9, "Layers are heights; the percentage is usually time.");
        Assert.AreEqual(0.5, BuildProgress.Built(printing with { Layer = null }, false), "No layers: the percentage will do.");
        Assert.AreEqual(0, BuildProgress.Built(new PrinterSnapshot { State = PrinterState.Preparing }, false), "Nothing on the plate yet.");
        Assert.AreEqual(0.225, BuildProgress.Built(printing with { State = PrinterState.Cancelled }, false), 1e-9, "A stopped print stays as far as it got.");
        Assert.AreEqual(0.225, BuildProgress.Built(printing with { State = PrinterState.Error }, false), 1e-9, "So does a failed one.");

        PrinterSnapshot finished = printing with { State = PrinterState.Finished, Progress = 1, Layer = 200 };
        Assert.AreEqual(1, BuildProgress.Built(finished, needsMarkComplete: true), "Done, and still on the plate.");
        Assert.AreEqual(0, BuildProgress.Built(finished, needsMarkComplete: false), "Marked complete: the plate is clear.");
        Assert.AreEqual(1, BuildProgress.Built(new PrinterSnapshot { State = PrinterState.Idle }, needsMarkComplete: true), "Back to idle, not yet marked complete.");
    }

    [TestMethod]
    public void ShowsHeat_WhileClimbingAndThroughTheWarmUp()
    {
        TemperatureReading atTarget = new(TemperatureKind.Nozzle, "Nozzle", 214, 215);
        TemperatureReading climbing = new(TemperatureKind.Bed, "Bed", 48, 60);
        TemperatureReading off = new(TemperatureKind.Bed, "Bed", 30, 0);

        Assert.IsTrue(BuildProgress.ShowsHeat(atTarget, PrinterState.Preparing), "Before a print, every heater's bar stays up.");
        Assert.IsFalse(BuildProgress.ShowsHeat(atTarget, PrinterState.Printing), "Mid-print at temperature: nothing to watch.");
        Assert.IsTrue(BuildProgress.ShowsHeat(climbing, PrinterState.Printing), "A new target mid-print.");
        Assert.IsFalse(BuildProgress.ShowsHeat(off, PrinterState.Preparing));
    }

    [TestMethod]
    public void Indicator_ShowsThePrintThatFinishesSoonest()
    {
        PrinterStatus slow = Status("Slow", PrinterState.Printing, 0.9, TimeSpan.FromHours(3));
        PrinterStatus soon = Status("Soon", PrinterState.Printing, 0.2, TimeSpan.FromMinutes(10));
        PrinterStatus unknown = Status("Unknown", PrinterState.Printing, 0.95);

        Assert.AreEqual(0.2, TrayIndicator.Compute([slow, unknown, soon], "Traycer").Progress);
    }

    [TestMethod]
    public void Marquee_NamesEveryFinishedPrinter_EvenOffline()
    {
        PrinterStatus voron = Status("Voron", PrinterState.Idle, needsMarkComplete: true);
        PrinterStatus p1s = Status("P1S", PrinterState.Finished, 1, needsMarkComplete: true) with { Connection = ConnectionState.Unreachable };
        PrinterStatus other = Status("Ender", PrinterState.Printing, 0.3);

        TrayIndicator indicator = TrayIndicator.Compute([voron, other, p1s], "Traycer");

        Assert.AreEqual(TrayGlyph.Done, indicator.Glyph);
        Assert.AreEqual("Voron done   •   P1S done", indicator.Marquee, "The print is still on the plate while the printer is off.");
    }

    [TestMethod]
    public void Tooltip_ListsNewsFirstAndFitsTheShellLimit()
    {
        PrinterStatus printing = Status("Voron", PrinterState.Printing, 0.456, TimeSpan.FromMinutes(80));
        PrinterStatus done = Status("P1S", PrinterState.Finished, 1, needsMarkComplete: true);
        PrinterStatus broken = Status("K1", PrinterState.Error, attention: AttentionLevel.Error, needsMarkComplete: true);

        string tooltip = TrayIndicator.Compute([printing, done], "Traycer").Tooltip;
        Assert.AreEqual("Traycer\nP1S: Finished\nVoron: 45% · 1h 20m left", tooltip);
        Assert.AreEqual("Traycer\nK1: Error\nP1S: Finished\nVoron: 45% · 1h 20m left", TrayIndicator.Compute([printing, done, broken], "Traycer").Tooltip, "One line per printer, problems first.");

        List<PrinterStatus> many = [.. Enumerable.Range(1, 12).Select(i => Status($"Printer with a long name {i}", PrinterState.Printing, 0.5, TimeSpan.FromHours(i)))];
        string crowded = TrayIndicator.Compute(many, "Traycer").Tooltip;
        Assert.IsLessThanOrEqualTo(TrayIndicator.MaxTooltipLength, crowded.Length);
        StringAssert.EndsWith(crowded, "more");
    }

    [TestMethod]
    public void Tooltip_SummarizesAnIdleFleet()
    {
        Assert.AreEqual("Traycer\nAll printers idle", TrayIndicator.Compute([Status("A", PrinterState.Idle), Status("B", PrinterState.Idle)], "Traycer").Tooltip);
        Assert.AreEqual("Traycer\nB: Finished\nA: Idle", TrayIndicator.Compute([Status("A", PrinterState.Idle), Status("B", PrinterState.Idle, needsMarkComplete: true)], "Traycer").Tooltip, "Back to idle, but not marked complete.");
        Assert.AreEqual("Traycer\nNo printers yet", TrayIndicator.Compute([], "Traycer").Tooltip);
    }

    [TestMethod]
    public void Tooltip_ExplainsConnectionTrouble()
    {
        PrinterStatus locked = Status("X1C", PrinterState.Idle) with { Connection = ConnectionState.AuthFailed };
        Assert.AreEqual("X1C: Can't sign in", StatusText.TooltipLine(locked));

        PrinterStatus off = Status("Ender", PrinterState.Idle) with { Connection = ConnectionState.Unreachable };
        Assert.AreEqual("Ender: Offline", StatusText.TooltipLine(off));
    }

    [TestMethod]
    public void Notification_OnlyForNews()
    {
        PrintEvent finished = new(PrintEventKind.Finished, "benchy.gcode", null, DateTimeOffset.UtcNow);
        Assert.AreEqual(("Voron finished", "benchy.gcode is done."), StatusText.Notification("Voron", finished));
        Assert.IsNull(StatusText.Notification("Voron", finished with { Kind = PrintEventKind.Started }));
        Assert.IsNull(StatusText.Notification("Voron", finished with { Kind = PrintEventKind.Cancelled }));
    }
}
