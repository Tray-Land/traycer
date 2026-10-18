using TrayCer.Core.Models;

namespace TrayCer.Core.Monitoring;

/// <summary>
/// Turns two consecutive readings into a print event. Only changes count, so a printer that was
/// already finished when the app started never announces itself.
/// </summary>
public static class TransitionPolicy
{
    /// <summary>A print that drops back to idle this close to done most likely finished.</summary>
    public const double FinishedThreshold = 0.98;

    public static PrintEventKind? Detect(PrinterSnapshot? previous, PrinterSnapshot next)
    {
        if (previous is null || previous.State == next.State)
        {
            return null;
        }

        bool wasRunning = previous.State is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        return next.State switch
        {
            PrinterState.Finished when wasRunning => PrintEventKind.Finished,
            PrinterState.Cancelled when wasRunning => PrintEventKind.Cancelled,
            PrinterState.Error => wasRunning ? PrintEventKind.Failed : PrintEventKind.Error,
            PrinterState.Attention => PrintEventKind.NeedsAttention,
            PrinterState.Paused when previous.State is PrinterState.Printing or PrinterState.Preparing => PrintEventKind.Paused,
            PrinterState.Printing or PrinterState.Preparing when !wasRunning => PrintEventKind.Started,

            // OctoPrint, Duet, Repetier and others go straight from printing back to idle.
            PrinterState.Idle when previous.State is PrinterState.Printing or PrinterState.Paused =>
                previous.Progress >= FinishedThreshold ? PrintEventKind.Finished : PrintEventKind.Cancelled,
            _ => null,
        };
    }

    /// <summary>
    /// How an event changes what the tray flags. Finishing and problems raise a flag; starting or
    /// cancelling a print at the printer means the user is there, so the flag drops.
    /// </summary>
    public static AttentionLevel AttentionAfter(PrintEventKind? kind, AttentionLevel current) => kind switch
    {
        PrintEventKind.Finished => Max(current, AttentionLevel.Done),
        PrintEventKind.Failed or PrintEventKind.Error => AttentionLevel.Error,
        PrintEventKind.NeedsAttention => Max(current, AttentionLevel.Warning),
        PrintEventKind.Started or PrintEventKind.Cancelled => AttentionLevel.None,
        _ => current,
    };

    private static AttentionLevel Max(AttentionLevel a, AttentionLevel b) => a > b ? a : b;
}
