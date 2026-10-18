using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.OctoPrint;

/// <summary>
/// Reads OctoPrint's /api/job and /api/printer. PrusaLink's legacy API returns the same shapes,
/// so its driver falls back to this parser on firmware without /api/v1.
/// </summary>
public static class OctoPrintParser
{
    public static PrinterSnapshot Parse(JsonNode? job, JsonNode? printer, string? thumbnailUrl = null)
    {
        string text = job.NonEmpty("state") ?? printer.NonEmpty("state", "text") ?? "";
        JsonObject? flags = printer.Object("state", "flags");
        double? completion = job.Number("progress", "completion");
        string? fileName = job.NonEmpty("job", "file", "display") ?? job.NonEmpty("job", "file", "name");

        PrinterState state = MapState(text, flags, completion, fileName is not null);
        string? message = state switch
        {
            PrinterState.Error => job.NonEmpty("error") ?? text,
            PrinterState.Offline => "OctoPrint isn't connected to the printer.",
            PrinterState.Busy => text,
            PrinterState.Preparing => text,
            _ => null,
        };

        double? progress = state == PrinterState.Finished ? 1 : PrinterSnapshot.ClampProgress(completion / 100);
        bool hasJob = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Finished
            or PrinterState.Busy;

        List<DetailItem> details = [];
        if (FilamentMeters(job.Object("job", "filament")) is { } meters && hasJob)
        {
            details.Add(new DetailItem("Filament", meters.ToString("0.0", CultureInfo.CurrentCulture) + " m"));
        }

        if (job.NonEmpty("progress", "printTimeLeftOrigin") is { } origin && state is PrinterState.Printing or PrinterState.Paused)
        {
            details.Add(new DetailItem("Estimate", origin switch
            {
                "linear" => "From progress",
                "analysis" => "From file analysis",
                "average" => "From past prints",
                "estimate" => "From slicer",
                "genius" => "PrintTimeGenius",
                "mixed-analysis" or "mixed-average" => "Mixed",
                _ => origin,
            }));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = hasJob ? progress : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(job.Number("progress", "printTime")) : null,
            Remaining = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing
                ? PrinterSnapshot.Seconds(job.Number("progress", "printTimeLeft"))
                : null,
            JobName = hasJob ? JsonRead.FileName(fileName) : null,
            Message = message,
            ThumbnailUrl = hasJob ? thumbnailUrl : null,
            Temperatures = Temperatures(printer.Object("temperature")),
            Details = details,
        };
    }

    internal static PrinterState MapState(string text, JsonObject? flags, double? completion, bool hasFile)
    {
        bool Is(string flag) => flags.Flag(flag) == true;
        bool Starts(string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        if (Is("error") || text.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.Error;
        }

        if (Starts("Offline") || Starts("Closed") || (Is("closedOrError") && !Is("operational")))
        {
            return PrinterState.Offline;
        }

        if (Is("cancelling") || Starts("Cancelling"))
        {
            return PrinterState.Busy;
        }

        if (Is("paused") || Is("pausing") || Starts("Paus"))
        {
            return PrinterState.Paused;
        }

        if (Starts("Starting"))
        {
            return PrinterState.Preparing;
        }

        if (Is("printing") || Starts("Printing") || Starts("Resuming") || Starts("Finishing"))
        {
            return PrinterState.Printing;
        }

        if (Starts("Opening") || Starts("Detecting") || Starts("Connecting") || Starts("Sending") || Starts("Transferring"))
        {
            return PrinterState.Busy;
        }

        // After a print OctoPrint goes back to "Operational" with the job still loaded at 100%.
        return hasFile && completion >= 99.95 ? PrinterState.Finished : PrinterState.Idle;
    }

    internal static IReadOnlyList<TemperatureReading> Temperatures(JsonObject? temperature)
    {
        if (temperature is null)
        {
            return [];
        }

        int tools = temperature.Count(p => p.Key.StartsWith("tool", StringComparison.Ordinal));
        List<TemperatureReading> readings = [];
        foreach ((string key, JsonNode? value) in temperature)
        {
            if (value.Number("actual") is not { } actual)
            {
                continue;
            }

            double? target = value.Number("target");
            if (key.StartsWith("tool", StringComparison.Ordinal))
            {
                string label = tools > 1 && int.TryParse(key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    ? $"Nozzle {index + 1}"
                    : "Nozzle";
                readings.Add(new TemperatureReading(TemperatureKind.Nozzle, label, actual, target));
            }
            else if (key == "bed")
            {
                readings.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", actual, target));
            }
            else if (key == "chamber")
            {
                readings.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", actual, target));
            }
        }

        return readings;
    }

    private static double? FilamentMeters(JsonObject? filament)
    {
        if (filament is null)
        {
            return null;
        }

        double total = 0;
        foreach ((_, JsonNode? tool) in filament)
        {
            total += tool.Number("length") ?? 0;
        }

        return total > 0 ? total / 1000 : null;
    }
}
