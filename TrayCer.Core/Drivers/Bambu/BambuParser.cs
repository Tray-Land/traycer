using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Bambu;

/// <summary>Reads the merged "print" object of a Bambu Lab printer's MQTT report.</summary>
public static class BambuParser
{
    /// <summary>print_error when a print was cancelled from the printer or the app ("0300-400C").</summary>
    public const long CancelledError = 0x0300400C;

    // stg_cur values that mean "getting ready" while gcode_state is still RUNNING.
    private static readonly HashSet<int> PreparingStages = [1, 2, 3, 7, 8, 9, 11, 12, 13, 14, 15, 18, 19, 25, 29, 31];

    // stg_cur values for a pause the printer made itself because something needs fixing.
    private static readonly HashSet<int> ProblemPauseStages = [6, 17, 20, 21, 23, 26, 27, 28, 32, 33, 34, 35];

    private static readonly string?[] StageNames =
    [
        null, // 0 printing
        "Auto bed leveling",
        "Heating bed",
        "Sweeping XY mechanism",
        "Changing filament",
        "Paused (M400)",
        "Filament ran out",
        "Heating nozzle",
        "Calibrating extrusion",
        "Scanning bed surface",
        "Inspecting first layer",
        "Identifying build plate",
        "Calibrating lidar",
        "Homing",
        "Cleaning nozzle",
        "Checking nozzle temperature",
        "Paused by user",
        "Front cover fell off",
        "Calibrating lidar",
        "Calibrating flow",
        "Nozzle temperature problem",
        "Bed temperature problem",
        "Unloading filament",
        "Skipped steps",
        "Loading filament",
        "Calibrating motor noise",
        "AMS disconnected",
        "Heatbreak fan too slow",
        "Chamber temperature problem",
        "Cooling chamber",
        "Paused by G-code",
        "Motor noise test",
        "Filament wrapped around nozzle",
        "Cutter problem",
        "First layer problem",
        "Nozzle clogged",
    ];

    private static readonly Dictionary<string, string> Models = new(StringComparer.Ordinal)
    {
        ["00M"] = "X1 Carbon",
        ["03W"] = "X1E",
        ["01S"] = "P1P",
        ["01P"] = "P1S",
        ["030"] = "A1 mini",
        ["039"] = "A1",
        ["094"] = "H2D",
    };

    public static string? ModelFromSerial(string? serial) =>
        serial is { Length: >= 3 } && Models.TryGetValue(serial[..3].ToUpperInvariant(), out string? model) ? model : null;

    public static PrinterSnapshot Parse(JsonNode? print, string? serial, DateTimeOffset now)
    {
        int? stage = print.Int("stg_cur");
        long error = print.Long("print_error") ?? 0;
        string? stageName = stage is >= 0 and var s && s < StageNames.Length ? StageNames[s] : null;

        PrinterState state = (print.NonEmpty("gcode_state")?.ToUpperInvariant()) switch
        {
            "RUNNING" when stage is { } st && PreparingStages.Contains(st) => PrinterState.Preparing,
            "RUNNING" => PrinterState.Printing,
            "PREPARE" or "SLICING" or "INIT" => PrinterState.Preparing,
            "PAUSE" when stage is { } st && ProblemPauseStages.Contains(st) => PrinterState.Attention,
            "PAUSE" => PrinterState.Paused,
            "FINISH" => PrinterState.Finished,
            "FAILED" when error == CancelledError => PrinterState.Cancelled,
            "FAILED" => PrinterState.Error,
            "OFFLINE" => PrinterState.Offline,
            _ => PrinterState.Idle,
        };

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        bool hasJob = running || state is PrinterState.Finished or PrinterState.Cancelled or PrinterState.Error;
        string? model = ModelFromSerial(serial);

        string? message = state switch
        {
            PrinterState.Preparing or PrinterState.Paused or PrinterState.Attention => stageName,
            PrinterState.Printing when stage is 4 or 10 or 22 or 24 => stageName,
            PrinterState.Error => error != 0 ? $"Print failed (error {FormatCode(error)})" : "Print failed",
            _ => null,
        };

        if (HmsCodes(print.Array("hms")) is { Count: > 0 } hms && state is PrinterState.Attention or PrinterState.Error or PrinterState.Paused)
        {
            message = (message is null ? "" : message + " · ") + "HMS " + hms[0] + (hms.Count > 1 ? $" (+{hms.Count - 1})" : "");
        }

        int? total = print.Int("total_layer_num") is > 0 and var t ? t : null;
        int? layer = total is not null ? print.Int("layer_num") : null;
        double? percent = print.Number("mc_percent");

        TimeSpan? elapsed = null;
        if (hasJob && print.Long("gcode_start_time") is > 0 and var started && started <= now.ToUnixTimeSeconds())
        {
            elapsed = running ? now - DateTimeOffset.FromUnixTimeSeconds(started) : null;
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(percent / 100) : null,
            Elapsed = elapsed,
            Remaining = running ? PrinterSnapshot.Seconds(print.Number("mc_remaining_time") * 60) : null,
            JobName = hasJob ? JobName(print) : null,
            Layer = hasJob ? layer : null,
            TotalLayers = hasJob ? total : null,
            Message = message,
            Model = model,
            Temperatures = Temperatures(print, model),
            Details = Details(print, running),
        };
    }

    internal static string FormatCode(long code) =>
        $"{(code >> 16) & 0xFFFF:X4}-{code & 0xFFFF:X4}";

    /// <summary>HMS alerts as the wiki formats them: attr and code split into four hex groups.</summary>
    internal static List<string> HmsCodes(JsonArray? hms)
    {
        List<string> codes = [];
        foreach (JsonNode? entry in hms ?? [])
        {
            if (entry.Long("attr") is { } attr && entry.Long("code") is { } code)
            {
                codes.Add($"{(attr >> 16) & 0xFFFF:X4}-{attr & 0xFFFF:X4}-{(code >> 16) & 0xFFFF:X4}-{code & 0xFFFF:X4}");
            }
        }

        return codes;
    }

    private static string? JobName(JsonNode? print)
    {
        string? name = print.NonEmpty("subtask_name") ?? JsonRead.FileName(print.NonEmpty("gcode_file"));
        if (name is null)
        {
            return null;
        }

        foreach (string extension in new[] { ".gcode.3mf", ".3mf", ".gcode" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static IReadOnlyList<TemperatureReading> Temperatures(JsonNode? print, string? model)
    {
        List<TemperatureReading> temps = [];

        // The H2D reports each of its two nozzles packed as (target << 16) | current.
        JsonArray? extruders = print.Array("device", "extruder", "info");
        if (extruders is { Count: > 1 })
        {
            foreach (JsonNode? extruder in extruders)
            {
                if (extruder.Long("temp") is { } packed && (packed & 0xFFFF) < 500 && (packed >> 16) < 500)
                {
                    int id = extruder.Int("id") ?? temps.Count;
                    temps.Add(new TemperatureReading(TemperatureKind.Nozzle, id == 0 ? "Right nozzle" : "Left nozzle", packed & 0xFFFF, packed >> 16));
                }
            }
        }

        if (temps.Count == 0 && print.Number("nozzle_temper") is { } nozzle)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, "Nozzle", nozzle, print.Number("nozzle_target_temper")));
        }

        if (print.Number("bed_temper") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, print.Number("bed_target_temper")));
        }

        // Only the enclosed X1 and H2 series have a chamber sensor; the others report a fixed value.
        if (model is "X1 Carbon" or "X1E" or "H2D" && print.Number("chamber_temper") is > 0 and var chamber)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", chamber));
        }

        return temps;
    }

    private static List<DetailItem> Details(JsonNode? print, bool running)
    {
        List<DetailItem> details = [];
        if (running && print.Int("spd_lvl") is { } level)
        {
            string? speed = level switch { 1 => "Silent", 2 => "Standard", 3 => "Sport", 4 => "Ludicrous", _ => null };
            if (speed is not null)
            {
                details.Add(new DetailItem("Speed", speed));
            }
        }

        if (CurrentFilament(print) is { } filament)
        {
            details.Add(filament);
        }

        if (print.NonEmpty("nozzle_diameter") is { } diameter)
        {
            string type = print.NonEmpty("nozzle_type")?.Replace('_', ' ') ?? "";
            details.Add(new DetailItem("Nozzle", $"{diameter} mm {type}".Trim()));
        }

        if (print.NonEmpty("wifi_signal") is { } wifi)
        {
            details.Add(new DetailItem("Wi-Fi", wifi.Replace("dBm", " dBm", StringComparison.Ordinal)));
        }

        return details;
    }

    /// <summary>The loaded filament: an AMS slot (tray_now 0-15 is unit*4+slot) or the external spool (254).</summary>
    internal static DetailItem? CurrentFilament(JsonNode? print)
    {
        if (print.Int("ams", "tray_now") is not { } now || now == 255)
        {
            return null;
        }

        JsonNode? tray;
        string where;
        if (now == 254)
        {
            tray = print.At("vt_tray");
            where = "External spool";
        }
        else
        {
            int unit = now / 4;
            int slot = now % 4;
            tray = print.Array("ams", "ams")?
                .FirstOrDefault(a => a.Int("id") == unit)?
                .Array("tray")?
                .FirstOrDefault(t => t.Int("id") == slot);
            where = $"AMS {(unit + 1).ToString(CultureInfo.CurrentCulture)}, slot {(slot + 1).ToString(CultureInfo.CurrentCulture)}";
        }

        string? material = tray.NonEmpty("tray_sub_brands") ?? tray.NonEmpty("tray_type");
        if (material is null)
        {
            return null;
        }

        return new DetailItem("Filament", $"{material} · {where}", ColorFromRgba(tray.NonEmpty("tray_color")));
    }

    /// <summary>"FF8000FF" (RRGGBBAA) to "#FF8000".</summary>
    internal static string? ColorFromRgba(string? rgba) =>
        rgba is { Length: >= 6 } && rgba[..6].All(Uri.IsHexDigit) ? "#" + rgba[..6].ToUpperInvariant() : null;
}
