using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Duet;

/// <summary>
/// Reads RepRapFirmware's object model: the "state", "job", "heat" and "tools" keys, either from
/// separate rr_model requests (standalone boards) or one /machine/status document (SBC mode).
/// </summary>
public static class DuetParser
{
    public static PrinterSnapshot Parse(JsonNode? stateKey, JsonNode? job, JsonNode? heat, JsonNode? tools)
    {
        string status = stateKey.NonEmpty("status") ?? "idle";
        PrinterState state = status switch
        {
            "processing" or "resuming" or "changingTool" => PrinterState.Printing,
            "paused" or "pausing" => PrinterState.Paused,
            "simulating" or "busy" or "starting" or "updating" or "cancelling" => PrinterState.Busy,
            "halted" => PrinterState.Error,
            "off" or "disconnected" => PrinterState.Offline,
            _ => PrinterState.Idle,
        };

        bool running = state is PrinterState.Printing or PrinterState.Paused;
        string? fileName = JsonRead.FileName(job.NonEmpty("file", "fileName"));
        double? size = job.Number("file", "size");
        double? position = job.Number("filePosition");
        double? progress = size > 0 && position is not null ? position / size : null;

        // A print that's started but not yet heated has no layer yet; call it preparing.
        if (state == PrinterState.Printing && (job.Int("layer") ?? 0) == 0 && (job.Number("duration") ?? 0) < 600 && progress < 0.01)
        {
            state = PrinterState.Preparing;
            running = true;
        }

        string? message = state switch
        {
            PrinterState.Error => "Emergency stop",
            PrinterState.Offline => "Printer power is off",
            PrinterState.Busy => status switch
            {
                "simulating" => "Simulating",
                "cancelling" => "Cancelling",
                "updating" => "Updating firmware",
                _ => null,
            },
            _ => stateKey.NonEmpty("displayMessage"),
        };

        TimeSpan? remaining = running
            ? PrinterSnapshot.Seconds(job.Number("timesLeft", "slicer") ?? job.Number("timesLeft", "file") ?? job.Number("timesLeft", "filament"))
            : null;

        return new PrinterSnapshot
        {
            State = state,
            Progress = running ? PrinterSnapshot.ClampProgress(progress) : null,
            Elapsed = running ? PrinterSnapshot.Seconds(job.Number("duration")) : null,
            Remaining = remaining,
            JobName = running ? fileName : null,
            Layer = running && job.Int("file", "numLayers") is > 0 ? job.Int("layer") : null,
            TotalLayers = running && job.Int("file", "numLayers") is > 0 and var layers ? layers : null,
            Message = message,
            Temperatures = Temperatures(heat, tools),
        };
    }

    internal static IReadOnlyList<TemperatureReading> Temperatures(JsonNode? heat, JsonNode? tools)
    {
        JsonArray? heaters = heat.Array("heaters");
        if (heaters is null)
        {
            return [];
        }

        TemperatureReading? Reading(int index, TemperatureKind kind, string label)
        {
            JsonNode? heater = index >= 0 && index < heaters.Count ? heaters[index] : null;
            if (heater.Number("current") is not { } current || current < -270)
            {
                return null;
            }

            // An "off" heater still reports its last active setpoint; only show targets it's using.
            double? target = heater.NonEmpty("state") is "active" or "tuning" ? heater.Number("active") : 0;
            return new TemperatureReading(kind, label, current, target);
        }

        List<TemperatureReading> temps = [];
        List<int> toolHeaters = (tools as JsonArray ?? [])
            .SelectMany(t => t.Array("heaters") ?? [])
            .Select(h => h.Int() ?? -1)
            .Where(h => h >= 0)
            .Distinct()
            .ToList();
        if (toolHeaters.Count == 0)
        {
            toolHeaters.Add(1); // RRF's default: heater 0 is the bed, heater 1 the first hot end.
        }

        for (int i = 0; i < toolHeaters.Count; i++)
        {
            if (Reading(toolHeaters[i], TemperatureKind.Nozzle, toolHeaters.Count > 1 ? $"Nozzle {i + 1}" : "Nozzle") is { } reading)
            {
                temps.Add(reading);
            }
        }

        if (heat.Int("bedHeaters", "0") is { } bed && Reading(bed, TemperatureKind.Bed, "Bed") is { } bedReading)
        {
            temps.Add(bedReading);
        }

        if (heat.Int("chamberHeaters", "0") is { } chamber && Reading(chamber, TemperatureKind.Chamber, "Chamber") is { } chamberReading)
        {
            temps.Add(chamberReading);
        }

        return temps;
    }
}
