using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Moonraker;

/// <summary>Slicer metadata Moonraker extracts from a G-code file (server/files/metadata).</summary>
public sealed record MoonrakerFileInfo(
    double? EstimatedSeconds,
    double? LayerHeight,
    double? FirstLayerHeight,
    double? ObjectHeight,
    int? LayerCount,
    string? Slicer,
    string? ThumbnailPath)
{
    public static MoonrakerFileInfo? Parse(JsonNode? response, string fileName)
    {
        JsonNode? meta = response.At("result");
        if (meta is null)
        {
            return null;
        }

        return new MoonrakerFileInfo(
            meta.Number("estimated_time"),
            meta.Number("layer_height"),
            meta.Number("first_layer_height"),
            meta.Number("object_height"),
            meta.Int("layer_count"),
            meta.NonEmpty("slicer"),
            PickThumbnail(meta.Array("thumbnails"), fileName));
    }

    /// <summary>The largest thumbnail up to 400 px wide, as a path under the gcodes root.</summary>
    internal static string? PickThumbnail(JsonArray? thumbnails, string fileName)
    {
        JsonNode? best = thumbnails?
            .Where(t => t.NonEmpty("relative_path") is not null)
            .OrderBy(t => (t.Number("width") ?? 0) > 400 ? 1 : 0)
            .ThenByDescending(t => t.Number("width") ?? 0)
            .FirstOrDefault();
        if (best.NonEmpty("relative_path") is not { } relative)
        {
            return null;
        }

        // relative_path is relative to the folder the G-code file is in.
        int slash = fileName.LastIndexOf('/');
        return slash >= 0 ? fileName[..(slash + 1)] + relative : relative;
    }
}

/// <summary>Reads Klipper's printer objects as Moonraker reports them.</summary>
public static class MoonrakerParser
{
    public static PrinterSnapshot Parse(JsonNode? query, MoonrakerFileInfo? file, string? thumbnailUrl, IReadOnlyList<string> heaterNames)
    {
        JsonNode? status = query.At("result", "status");
        JsonNode? stats = status.At("print_stats");

        string? klippy = status.NonEmpty("webhooks", "state");
        if (klippy is not null and not "ready")
        {
            return new PrinterSnapshot
            {
                State = klippy == "startup" ? PrinterState.Busy : PrinterState.Error,
                Message = status.NonEmpty("webhooks", "state_message") is { } why ? FirstLine(why) : klippy == "startup" ? "Klipper is starting" : "Klipper isn't ready",
                Temperatures = Temperatures(status, heaterNames),
            };
        }

        double printDuration = stats.Number("print_duration") ?? 0;
        double? progress = BestProgress(status.Number("display_status", "progress"), status.Number("virtual_sdcard", "progress"));
        PrinterState state = stats.NonEmpty("state") switch
        {
            "printing" when printDuration <= 0 => PrinterState.Preparing,
            "printing" => PrinterState.Printing,
            "paused" => PrinterState.Paused,
            "complete" => PrinterState.Finished,
            "cancelled" => PrinterState.Cancelled,
            "error" => PrinterState.Error,
            _ => PrinterState.Idle,
        };

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing;
        bool hasJob = running || state is PrinterState.Finished or PrinterState.Cancelled or PrinterState.Error;
        string? fileName = stats.NonEmpty("filename");
        hasJob &= fileName is not null;

        (int? layer, int? totalLayers) = Layers(
            stats.Int("info", "current_layer"),
            stats.Int("info", "total_layer"),
            file,
            status.Number("gcode_move", "gcode_position", "2"),
            running);

        string? message = state switch
        {
            PrinterState.Error => stats.NonEmpty("message"),
            PrinterState.Preparing => IsHeating(status) ? "Heating" : "Getting ready",
            _ => status.NonEmpty("display_status", "message"),
        };

        List<DetailItem> details = [];
        if (running && status.Number("gcode_move", "speed_factor") is { } speed)
        {
            details.Add(new DetailItem("Speed", Math.Round(speed * 100).ToString(CultureInfo.CurrentCulture) + "%"));
        }

        if (hasJob && stats.Number("filament_used") is > 0 and var used)
        {
            details.Add(new DetailItem("Filament used", (used / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " m"));
        }

        if (hasJob && file?.Slicer is { } slicer)
        {
            details.Add(new DetailItem("Slicer", slicer));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? progress : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(printDuration) : null,
            Remaining = running ? Remaining(printDuration, progress, file?.EstimatedSeconds) : null,
            JobName = hasJob ? JsonRead.FileName(fileName) : null,
            Layer = hasJob ? layer : null,
            TotalLayers = hasJob ? totalLayers : null,
            Message = message,
            ThumbnailUrl = hasJob ? thumbnailUrl : null,
            Temperatures = Temperatures(status, heaterNames),
            Details = details,
        };
    }

    /// <summary>
    /// The slicer's M73 progress (display_status) is time-based and matches the ETA, so prefer it
    /// once a slicer sends any; otherwise fall back to the file position (virtual_sdcard).
    /// </summary>
    internal static double? BestProgress(double? display, double? file) =>
        PrinterSnapshot.ClampProgress(display is > 0 ? display : file);

    /// <summary>
    /// Mainsail-style ETA: early on only the slicer's estimate is meaningful; later, average it
    /// with the file-progress extrapolation so a slow or fast print corrects the estimate.
    /// </summary>
    public static TimeSpan? Remaining(double printDuration, double? progress, double? slicerEstimate)
    {
        double? fromFile = progress is > 0.01 and <= 1 ? (printDuration / progress.Value) - printDuration : null;
        double? fromSlicer = slicerEstimate is > 0 ? Math.Max(0, slicerEstimate.Value - printDuration) : null;

        double? seconds = (fromFile, fromSlicer) switch
        {
            ({ } f, { } s) when progress >= 0.1 => (f + s) / 2,
            (_, { } s) => s,
            ({ } f, null) => f,
            _ => null,
        };
        return PrinterSnapshot.Seconds(seconds);
    }

    /// <summary>
    /// Layers from SET_PRINT_STATS_INFO when the slicer sends it, else derived from the file's layer
    /// heights and the current Z the way Mainsail does.
    /// </summary>
    public static (int? Layer, int? Total) Layers(int? reportedLayer, int? reportedTotal, MoonrakerFileInfo? file, double? z, bool running)
    {
        int? total = reportedTotal is > 0 ? reportedTotal : file?.LayerCount is > 0 ? file.LayerCount : null;
        double? layerHeight = file?.LayerHeight;
        double? first = file?.FirstLayerHeight ?? layerHeight;
        if (total is null && layerHeight is > 0 && first is > 0 && file?.ObjectHeight is > 0 and var height)
        {
            total = (int)Math.Ceiling(((height - first.Value) / layerHeight.Value) + 1 - 1e-6);
        }

        int? layer = reportedLayer is > 0 ? reportedLayer : null;
        if (layer is null && running && z is > 0 && layerHeight is > 0 && first is > 0)
        {
            layer = z < first ? 1 : (int)Math.Ceiling(((z.Value - first.Value) / layerHeight.Value) + 1 - 1e-6);
        }

        if (layer is not null && total is not null)
        {
            layer = Math.Min(layer.Value, total.Value);
        }

        return (layer, total);
    }

    internal static IReadOnlyList<TemperatureReading> Temperatures(JsonNode? status, IReadOnlyList<string> heaterNames)
    {
        List<TemperatureReading> readings = [];
        int extruders = heaterNames.Count(IsExtruder);
        foreach (string name in heaterNames)
        {
            JsonNode? heater = status.At(name);
            if (heater.Number("temperature") is not { } current)
            {
                continue;
            }

            double? target = heater.Number("target");
            if (IsExtruder(name))
            {
                // Klipper names them extruder, extruder1, extruder2...
                int index = name.Length > 8 && int.TryParse(name.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
                string label = extruders > 1 ? $"Nozzle {index + 1}" : "Nozzle";
                readings.Add(new TemperatureReading(TemperatureKind.Nozzle, label, current, target));
            }
            else if (name == "heater_bed")
            {
                readings.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", current, target));
            }
            else if (name.Contains("chamber", StringComparison.OrdinalIgnoreCase))
            {
                readings.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", current, target));
            }
        }

        return readings;
    }

    /// <summary>Picks the heaters and the chamber sensor out of printer/objects/list.</summary>
    public static IReadOnlyList<string> HeaterNames(JsonNode? objectList)
    {
        JsonArray? objects = objectList.Array("result", "objects");
        if (objects is null)
        {
            return ["extruder", "heater_bed"];
        }

        List<string> names = [];
        foreach (string? name in objects.Select(o => o.Text()))
        {
            if (name is null)
            {
                continue;
            }

            if (IsExtruder(name) || name == "heater_bed")
            {
                names.Add(name);
            }
            else if ((name.StartsWith("temperature_sensor ", StringComparison.Ordinal) || name.StartsWith("heater_generic ", StringComparison.Ordinal))
                && name.Contains("chamber", StringComparison.OrdinalIgnoreCase)
                && !names.Any(n => n.Contains("chamber", StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The objects to query for a printer with these heaters.</summary>
    public static string QueryPath(IReadOnlyList<string> heaterNames) =>
        "printer/objects/query?webhooks&print_stats&virtual_sdcard&display_status&gcode_move&"
        + string.Join('&', heaterNames.Select(n => Uri.EscapeDataString(n)));

    private static bool IsExtruder(string name) =>
        name == "extruder" || (name.StartsWith("extruder", StringComparison.Ordinal) && name.Length > 8 && char.IsDigit(name[8]));

    private static bool IsHeating(JsonNode? status)
    {
        foreach (string name in new[] { "extruder", "heater_bed" })
        {
            if (status.Number(name, "target") is > 0 and var target && status.Number(name, "temperature") is { } current && current < target - 3)
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        return (newline > 0 ? text[..newline] : text).Trim();
    }
}
