using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Ultimaker;

/// <summary>Reads UltiMaker's local API: /api/v1/printer and /api/v1/print_job (404 when there's no job).</summary>
public static class UltimakerParser
{
    public static PrinterSnapshot Parse(JsonNode? printer, JsonNode? job)
    {
        string status = printer.NonEmpty("status") ?? "idle";
        string? jobState = job.NonEmpty("state");
        bool aborted = string.Equals(job.NonEmpty("result"), "Aborted", StringComparison.OrdinalIgnoreCase);

        (PrinterState state, string? message) = status switch
        {
            "error" => (PrinterState.Error, null),
            "maintenance" => (PrinterState.Busy, "Maintenance"),
            "booting" => (PrinterState.Busy, "Starting up"),
            _ => jobState switch
            {
                "pre_print" => (PrinterState.Preparing, "Getting ready"),
                "printing" or "resuming" => (PrinterState.Printing, null),
                "post_print" => (PrinterState.Printing, "Finishing"),
                "pausing" or "paused" => (PrinterState.Paused, null),
                "wait_cleanup" => (aborted ? PrinterState.Cancelled : PrinterState.Finished, "Remove the print from the build plate"),
                "wait_user_action" => (PrinterState.Attention, "Waiting for you at the printer"),
                _ => (PrinterState.Idle, (string?)null),
            },
        };

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        bool hasJob = job is not null && (running || state is PrinterState.Finished or PrinterState.Cancelled);
        double? elapsed = job.Number("time_elapsed");
        double? total = job.Number("time_total");

        List<TemperatureReading> temps = [];
        JsonArray? extruders = printer.Array("heads", "0", "extruders");
        int count = extruders?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            JsonNode? hotend = extruders![i].At("hotend", "temperature");
            if (hotend.Number("current") is { } current)
            {
                temps.Add(new TemperatureReading(TemperatureKind.Nozzle, count > 1 ? $"Nozzle {i + 1}" : "Nozzle", current, hotend.Number("target")));
            }
        }

        if (printer.Number("bed", "temperature", "current") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, printer.Number("bed", "temperature", "target")));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(job.Number("progress")) : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(elapsed) : null,
            Remaining = running && total > 0 ? PrinterSnapshot.Seconds(Math.Max(0, total.Value - (elapsed ?? 0))) : null,
            JobName = hasJob ? JsonRead.FileName(job.NonEmpty("name")) : null,
            Message = message,
            Temperatures = temps,
        };
    }
}
