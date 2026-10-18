using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Repetier;

/// <summary>Reads Repetier-Server's listPrinter (one entry per printer) and stateList (temperatures).</summary>
public static class RepetierParser
{
    /// <summary>The configured printer by slug, or the first active one when no slug is set.</summary>
    public static JsonNode? FindPrinter(JsonNode? list, string? slug)
    {
        JsonArray? printers = list as JsonArray ?? list.Array("data");
        if (printers is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(slug)
            ? printers.FirstOrDefault(p => p.Flag("active") != false) ?? printers.FirstOrDefault()
            : printers.FirstOrDefault(p => string.Equals(p.NonEmpty("slug"), slug.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static PrinterSnapshot Parse(JsonNode? printer, JsonNode? state)
    {
        if (printer is null)
        {
            return new PrinterSnapshot { State = PrinterState.Offline, Message = "Repetier-Server has no printer with that slug." };
        }

        string? job = printer.NonEmpty("job");
        bool hasJob = job is not null && !string.Equals(job, "none", StringComparison.OrdinalIgnoreCase);
        bool online = printer.Flag("online") ?? true;
        bool active = printer.Flag("active") ?? true;

        PrinterState status = !active || !online ? PrinterState.Offline
            : hasJob && printer.Flag("paused") == true ? PrinterState.Paused
            : hasJob ? PrinterState.Printing
            : PrinterState.Idle;
        bool running = status is PrinterState.Printing or PrinterState.Paused;

        double? total = printer.Number("printTime");
        double? elapsed = printer.Number("printedTimeComp");

        return new PrinterSnapshot
        {
            State = status,
            Progress = running ? PrinterSnapshot.ClampProgress(printer.Number("done") / 100) : null,
            Elapsed = running ? PrinterSnapshot.Seconds(elapsed) : null,
            Remaining = running && total > 0 && elapsed is not null ? PrinterSnapshot.Seconds(Math.Max(0, total.Value - elapsed.Value)) : null,
            JobName = running ? JsonRead.FileName(job) : null,
            Layer = running && state.Int("layer") is > 0 ? state.Int("layer") : null,
            Message = !active ? "Turned off in Repetier-Server" : !online ? "Repetier-Server isn't connected to the printer" : null,
            Temperatures = Temperatures(state),
        };
    }

    internal static IReadOnlyList<TemperatureReading> Temperatures(JsonNode? state)
    {
        List<TemperatureReading> temps = [];
        JsonArray? extruders = state.Array("extruder");
        int count = extruders?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (extruders![i].Number("tempRead") is { } current)
            {
                temps.Add(new TemperatureReading(TemperatureKind.Nozzle, count > 1 ? $"Nozzle {i + 1}" : "Nozzle", current, extruders[i].Number("tempSet")));
            }
        }

        // Older servers report one heatedBed object, newer ones a heatedBeds array.
        JsonNode? bed = state.At("heatedBed") ?? state.At("heatedBeds", "0");
        if (bed.Number("tempRead") is { } bedTemp)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bedTemp, bed.Number("tempSet")));
        }

        JsonNode? chamber = state.At("heatedChambers", "0");
        if (chamber.Number("tempRead") is { } chamberTemp)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", chamberTemp, chamber.Number("tempSet")));
        }

        return temps;
    }
}
