using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;

namespace TrayCer.Tests;

[TestClass]
public sealed class PolicyTests
{
    private static PrinterSnapshot Snap(PrinterState state, double? progress = null) => new() { State = state, Progress = progress };

    [TestMethod]
    public void Detect_FirstReadingNeverAnnounces()
    {
        Assert.IsNull(TransitionPolicy.Detect(null, Snap(PrinterState.Finished)));
        Assert.IsNull(TransitionPolicy.Detect(Snap(PrinterState.Finished), Snap(PrinterState.Finished)));
    }

    [TestMethod]
    [DataRow(PrinterState.Printing, PrinterState.Finished, PrintEventKind.Finished)]
    [DataRow(PrinterState.Paused, PrinterState.Finished, PrintEventKind.Finished)]
    [DataRow(PrinterState.Printing, PrinterState.Cancelled, PrintEventKind.Cancelled)]
    [DataRow(PrinterState.Printing, PrinterState.Error, PrintEventKind.Failed)]
    [DataRow(PrinterState.Idle, PrinterState.Error, PrintEventKind.Error)]
    [DataRow(PrinterState.Printing, PrinterState.Attention, PrintEventKind.NeedsAttention)]
    [DataRow(PrinterState.Printing, PrinterState.Paused, PrintEventKind.Paused)]
    [DataRow(PrinterState.Idle, PrinterState.Preparing, PrintEventKind.Started)]
    [DataRow(PrinterState.Finished, PrinterState.Printing, PrintEventKind.Started)]
    public void Detect_Transitions(PrinterState from, PrinterState to, PrintEventKind expected)
    {
        Assert.AreEqual(expected, TransitionPolicy.Detect(Snap(from), Snap(to)));
    }

    [TestMethod]
    public void Detect_IgnoresQuietChanges()
    {
        Assert.IsNull(TransitionPolicy.Detect(Snap(PrinterState.Preparing), Snap(PrinterState.Printing)), "Warming up into printing isn't news.");
        Assert.IsNull(TransitionPolicy.Detect(Snap(PrinterState.Idle), Snap(PrinterState.Finished)), "Finished without ever printing is a stale state.");
        Assert.IsNull(TransitionPolicy.Detect(Snap(PrinterState.Paused), Snap(PrinterState.Printing)), "Resuming isn't news.");
    }

    [TestMethod]
    public void Detect_PrintingToIdle_FinishedOnlyNearTheEnd()
    {
        Assert.AreEqual(PrintEventKind.Finished, TransitionPolicy.Detect(Snap(PrinterState.Printing, 0.99), Snap(PrinterState.Idle)));
        Assert.AreEqual(PrintEventKind.Cancelled, TransitionPolicy.Detect(Snap(PrinterState.Printing, 0.40), Snap(PrinterState.Idle)));
    }

    [TestMethod]
    public void Attention_RaisesOnNewsAndDropsWhenUserIsAtThePrinter()
    {
        Assert.AreEqual(AttentionLevel.Done, TransitionPolicy.AttentionAfter(PrintEventKind.Finished, AttentionLevel.None));
        Assert.AreEqual(AttentionLevel.Error, TransitionPolicy.AttentionAfter(PrintEventKind.Finished, AttentionLevel.Error), "Finishing doesn't hide an earlier error.");
        Assert.AreEqual(AttentionLevel.Error, TransitionPolicy.AttentionAfter(PrintEventKind.Failed, AttentionLevel.Done));
        Assert.AreEqual(AttentionLevel.None, TransitionPolicy.AttentionAfter(PrintEventKind.Started, AttentionLevel.Done));
        Assert.AreEqual(AttentionLevel.Warning, TransitionPolicy.AttentionAfter(PrintEventKind.NeedsAttention, AttentionLevel.None));
        Assert.AreEqual(AttentionLevel.Done, TransitionPolicy.AttentionAfter(null, AttentionLevel.Done));
    }

    [TestMethod]
    public void NextDelay_FastWhileActiveOrWatched_SlowWhileIdle()
    {
        MonitorOptions options = new();
        Assert.AreEqual(options.ActiveInterval, PollPolicy.NextDelay(Snap(PrinterState.Printing), 0, foreground: false, persistent: false, options));
        Assert.AreEqual(options.ActiveInterval, PollPolicy.NextDelay(Snap(PrinterState.Idle), 0, foreground: true, persistent: true, options));
        Assert.AreEqual(options.IdleInterval, PollPolicy.NextDelay(Snap(PrinterState.Idle), 0, foreground: false, persistent: false, options));
        Assert.AreEqual(options.PersistentIdleInterval, PollPolicy.NextDelay(Snap(PrinterState.Finished), 0, foreground: false, persistent: true, options));
    }

    [TestMethod]
    public void NextDelay_BacksOffWhileUnreachable()
    {
        MonitorOptions options = new();
        Assert.AreEqual(TimeSpan.FromSeconds(30), PollPolicy.NextDelay(null, 1, false, false, options));
        Assert.AreEqual(TimeSpan.FromSeconds(60), PollPolicy.NextDelay(null, 2, false, false, options));
        Assert.AreEqual(TimeSpan.FromMinutes(5), PollPolicy.NextDelay(null, 9, false, false, options));
        Assert.AreEqual(options.ActiveInterval * 2, PollPolicy.NextDelay(null, 9, true, false, options), "Retry sooner while the user is watching.");
    }

    [TestMethod]
    public void ShouldSuspend_OnlyIdlePushConnectionsInTheBackground()
    {
        Assert.IsTrue(PollPolicy.ShouldSuspend(Snap(PrinterState.Idle), foreground: false, persistent: true));
        Assert.IsFalse(PollPolicy.ShouldSuspend(Snap(PrinterState.Printing), foreground: false, persistent: true));
        Assert.IsFalse(PollPolicy.ShouldSuspend(Snap(PrinterState.Idle), foreground: true, persistent: true));
        Assert.IsFalse(PollPolicy.ShouldSuspend(Snap(PrinterState.Idle), foreground: false, persistent: false));
    }

    [TestMethod]
    [DataRow(1.0, 5.0)]
    [DataRow(14.0, 15.0)]
    [DataRow(31.0, 30.0)]
    [DataRow(0.0, 5.0)]
    [DataRow(9999.0, 60.0)]
    public void SanitizeActiveInterval_SnapsToChoices(double stored, double expected)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(expected), MonitorOptions.SanitizeActiveInterval(stored));
    }
}
