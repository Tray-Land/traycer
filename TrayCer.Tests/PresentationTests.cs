using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;

namespace TrayCer.Tests;

[TestClass]
public sealed class PresentationTests
{
    private static PrinterStatus Status(string name, PrinterState state, double? progress = null, TimeSpan? remaining = null, AttentionLevel attention = AttentionLevel.None) => new()
    {
        Config = new PrinterConfig(name, name, PrinterKind.Moonraker, "10.0.0.1"),
        Snapshot = new PrinterSnapshot { State = state, Progress = progress, Remaining = remaining },
        Connection = ConnectionState.Connected,
        Attention = attention,
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
        PrinterStatus done = Status("C", PrinterState.Finished, 1, attention: AttentionLevel.Done);
        PrinterStatus failed = Status("D", PrinterState.Error, attention: AttentionLevel.Error);

        Assert.AreEqual(TrayGlyph.Error, TrayIndicator.Compute([printing, paused, done, failed], "Traycer").Glyph);
        Assert.AreEqual(TrayGlyph.Done, TrayIndicator.Compute([printing, paused, done], "Traycer").Glyph);
        TrayIndicator pausedIcon = TrayIndicator.Compute([printing, paused], "Traycer");
        Assert.AreEqual(TrayGlyph.Paused, pausedIcon.Glyph);
        Assert.AreEqual(0.7, pausedIcon.Progress);
        Assert.AreEqual(TrayGlyph.Progress, TrayIndicator.Compute([printing], "Traycer").Glyph);
        Assert.AreEqual(TrayGlyph.App, TrayIndicator.Compute([Status("E", PrinterState.Idle)], "Traycer").Glyph);
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
    public void Tooltip_ListsNewsFirstAndFitsTheShellLimit()
    {
        PrinterStatus printing = Status("Voron", PrinterState.Printing, 0.456, TimeSpan.FromMinutes(80));
        PrinterStatus done = Status("P1S", PrinterState.Finished, 1, attention: AttentionLevel.Done);

        string tooltip = TrayIndicator.Compute([printing, done], "Traycer").Tooltip;
        Assert.AreEqual("Traycer\nP1S: Finished\nVoron: 45% · 1h 20m left", tooltip);

        List<PrinterStatus> many = [.. Enumerable.Range(1, 12).Select(i => Status($"Printer with a long name {i}", PrinterState.Printing, 0.5, TimeSpan.FromHours(i)))];
        string crowded = TrayIndicator.Compute(many, "Traycer").Tooltip;
        Assert.IsLessThanOrEqualTo(TrayIndicator.MaxTooltipLength, crowded.Length);
        StringAssert.EndsWith(crowded, "more");
    }

    [TestMethod]
    public void Tooltip_SummarizesAnIdleFleet()
    {
        Assert.AreEqual("Traycer\nAll printers idle", TrayIndicator.Compute([Status("A", PrinterState.Idle), Status("B", PrinterState.Idle)], "Traycer").Tooltip);
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
