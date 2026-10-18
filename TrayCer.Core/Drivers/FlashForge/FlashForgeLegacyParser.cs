using System.Globalization;
using System.Text.RegularExpressions;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.FlashForge;

/// <summary>
/// Reads the text replies of FlashForge's older TCP protocol (port 8899): ~M119 for the machine
/// state, ~M27 for progress, ~M105 for temperatures and ~M115 for the model.
/// </summary>
public static partial class FlashForgeLegacyParser
{
    public static PrinterSnapshot Parse(string? m119, string? m27, string? m105, string? m115)
    {
        Dictionary<string, string> status = Fields(m119);
        string machine = status.GetValueOrDefault("MachineStatus", "").ToUpperInvariant();
        string move = status.GetValueOrDefault("MoveMode", "").ToUpperInvariant();

        PrinterState state = machine switch
        {
            _ when machine.Contains("BUILDING_FROM_SD", StringComparison.Ordinal) => move == "PAUSED" ? PrinterState.Paused : PrinterState.Printing,
            _ when machine.Contains("BUILDING_COMPLETED", StringComparison.Ordinal) => PrinterState.Finished,
            _ when machine.Contains("PAUSED", StringComparison.Ordinal) => PrinterState.Paused,
            _ when machine.Contains("BUSY", StringComparison.Ordinal) => PrinterState.Busy,
            _ => PrinterState.Idle,
        };

        bool running = state is PrinterState.Printing or PrinterState.Paused;
        bool hasJob = running || state == PrinterState.Finished;

        double? progress = null;
        if (SdProgress().Match(m27 ?? "") is { Success: true } sd && double.TryParse(sd.Groups[2].Value, CultureInfo.InvariantCulture, out double sdTotal) && sdTotal > 0)
        {
            progress = double.Parse(sd.Groups[1].Value, CultureInfo.InvariantCulture) / sdTotal;
        }

        int? layer = null;
        int? totalLayers = null;
        if (LayerProgress().Match(m27 ?? "") is { Success: true } layers && int.TryParse(layers.Groups[2].Value, CultureInfo.InvariantCulture, out int total) && total > 0)
        {
            layer = int.Parse(layers.Groups[1].Value, CultureInfo.InvariantCulture);
            totalLayers = total;
        }

        Dictionary<string, string> info = Fields(m115);
        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(progress) : null,
            JobName = hasJob ? JsonRead.FileName(status.GetValueOrDefault("CurrentFile")) : null,
            Layer = hasJob ? layer : null,
            TotalLayers = hasJob ? totalLayers : null,
            Model = info.GetValueOrDefault("Machine Type")?.Replace("FlashForge ", "", StringComparison.OrdinalIgnoreCase),
            Temperatures = Temperatures(m105),
        };
    }

    internal static IReadOnlyList<TemperatureReading> Temperatures(string? m105)
    {
        List<TemperatureReading> temps = [];
        MatchCollection matches = Temperature().Matches(m105 ?? "");
        int nozzles = matches.Count(m => m.Groups[1].Value.StartsWith('T'));
        foreach (Match match in matches)
        {
            string key = match.Groups[1].Value;
            double current = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            double? target = match.Groups[3].Success ? double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null;
            if (key == "B")
            {
                temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", current, target));
            }
            else
            {
                int index = key.Length > 1 ? int.Parse(key.AsSpan(1), CultureInfo.InvariantCulture) : 0;
                temps.Add(new TemperatureReading(TemperatureKind.Nozzle, nozzles > 1 ? $"Nozzle {index + 1}" : "Nozzle", current, target));
            }
        }

        return temps;
    }

    /// <summary>"Key: value" lines, first occurrence wins.</summary>
    internal static Dictionary<string, string> Fields(string? reply)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (reply ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        return fields;
    }

    [GeneratedRegex(@"SD printing byte\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SdProgress();

    [GeneratedRegex(@"Layer:\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LayerProgress();

    [GeneratedRegex(@"\b(T\d?|B):\s*(-?\d+(?:\.\d+)?)(?:\s*/\s*(-?\d+(?:\.\d+)?))?")]
    private static partial Regex Temperature();
}
