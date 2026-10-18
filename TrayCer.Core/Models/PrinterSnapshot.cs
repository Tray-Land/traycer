namespace TrayCer.Core.Models;

/// <summary>What a printer is doing, normalized across every protocol.</summary>
public enum PrinterState
{
    /// <summary>The host answered but the printer behind it is off or disconnected (OctoPrint with no serial link).</summary>
    Offline,
    Idle,

    /// <summary>Doing something that isn't a print: calibrating, transferring a file, self-testing.</summary>
    Busy,

    /// <summary>A print has started but isn't laying down plastic yet: heating, homing, leveling.</summary>
    Preparing,
    Printing,
    Paused,

    /// <summary>The printer is waiting on the user (PrusaLink ATTENTION, UltiMaker "wait_user_action").</summary>
    Attention,
    Finished,
    Cancelled,
    Error,
}

public enum TemperatureKind
{
    Nozzle,
    Bed,
    Chamber,
    Other,
}

public sealed record TemperatureReading(TemperatureKind Kind, string Label, double Current, double? Target = null)
{
    /// <summary>A target of 0 means the heater is off; only a positive target is worth showing.</summary>
    public bool IsHeating => Target is > 0;
}

/// <summary>A protocol-specific fact the generic layout has no slot for: speed, filament, Wi-Fi signal.</summary>
/// <param name="Color">An optional "#RRGGBB" swatch shown beside the value (a filament color).</param>
public sealed record DetailItem(string Label, string Value, string? Color = null);

/// <summary>
/// One reading of a printer. Every field but <see cref="State"/> is optional: a driver fills in
/// what its printer reports and leaves the rest null, and the UI shows only what's there. That is
/// how one card layout serves a Bambu with AMS data and a FlashForge that only knows a percentage.
/// </summary>
public sealed record PrinterSnapshot
{
    public required PrinterState State { get; init; }

    /// <summary>0 to 1.</summary>
    public double? Progress { get; init; }

    public TimeSpan? Elapsed { get; init; }

    public TimeSpan? Remaining { get; init; }

    /// <summary>The file or job name, without folders.</summary>
    public string? JobName { get; init; }

    public int? Layer { get; init; }

    public int? TotalLayers { get; init; }

    /// <summary>A short status line from the printer: "Heating bed", "Filament runout", an error.</summary>
    public string? Message { get; init; }

    /// <summary>The model the printer reports about itself, when it does ("K1C", "Centauri Carbon").</summary>
    public string? Model { get; init; }

    /// <summary>An absolute URL to a preview of the current job, fetched through the driver (it may need auth).</summary>
    public string? ThumbnailUrl { get; init; }

    public IReadOnlyList<TemperatureReading> Temperatures { get; init; } = [];

    public IReadOnlyList<DetailItem> Details { get; init; } = [];

    /// <summary>True while a job occupies the printer, so it's worth watching closely.</summary>
    public bool IsActive => State is PrinterState.Preparing or PrinterState.Printing or PrinterState.Paused
        or PrinterState.Attention or PrinterState.Busy;

    public static double? ClampProgress(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;

    public static TimeSpan? Seconds(double? seconds) =>
        seconds is { } s && double.IsFinite(s) && s >= 0 && s < TimeSpan.FromDays(60).TotalSeconds
            ? TimeSpan.FromSeconds(Math.Round(s))
            : null;
}
