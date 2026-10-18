using TrayCer.Core.Models;

namespace TrayCer.Core.Presentation;

/// <summary>
/// How far along a print looks in the flyout: how much of its preview is built, and which heaters
/// get a bar while they climb.
/// </summary>
public static class BuildProgress
{
    /// <summary>
    /// The share of the job preview drawn solid, 0 to 1 from the bottom up; the rest is a ghost of
    /// what's still to print. A finished part stays on the plate until it's marked complete, then
    /// the plate is empty again.
    /// </summary>
    public static double Built(PrinterSnapshot snapshot, bool needsMarkComplete) => snapshot.State switch
    {
        PrinterState.Finished or PrinterState.Idle or PrinterState.Busy or PrinterState.Offline => needsMarkComplete ? 1 : 0,

        // Mid-print, failed or cancelled: what's on the plate is as far as the print got.
        _ => LayerShare(snapshot) ?? snapshot.Progress ?? 0,
    };

    /// <summary>
    /// A heater gets a bar while it's still climbing to its target, and through the whole warm-up
    /// before a print, when the bars are the thing to watch.
    /// </summary>
    public static bool ShowsHeat(TemperatureReading reading, PrinterState state) =>
        reading.IsHeating && (state == PrinterState.Preparing || reading.IsWarmingUp);

    /// <summary>Layers are heights, which suits a picture built from the bottom up better than a time-based percentage.</summary>
    private static double? LayerShare(PrinterSnapshot snapshot) =>
        snapshot.Layer is { } layer && snapshot.TotalLayers is > 0 and int total ? Math.Clamp((double)layer / total, 0, 1) : null;
}
