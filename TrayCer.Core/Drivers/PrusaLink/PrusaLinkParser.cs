using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.PrusaLink;

/// <summary>Reads PrusaLink's /api/v1/status and /api/v1/job (Buddy firmware and PrusaLink 0.7+).</summary>
public static class PrusaLinkParser
{
    public static PrinterSnapshot Parse(JsonNode? status, JsonNode? job, string? thumbnailUrl)
    {
        JsonNode? printer = status.At("printer");
        PrinterState state = MapState(printer.NonEmpty("state"));

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Attention;
        bool hasJob = status.At("job") is not null || job is not null;
        hasJob &= running || state is PrinterState.Finished or PrinterState.Cancelled or PrinterState.Error;

        double? percent = status.Number("job", "progress") ?? job.Number("progress");
        string? name = job.NonEmpty("file", "display_name") ?? job.NonEmpty("file", "name");

        List<DetailItem> details = [];
        if (running)
        {
            if (printer.Int("speed") is { } speed)
            {
                details.Add(new DetailItem("Speed", speed.ToString(CultureInfo.CurrentCulture) + "%"));
            }

            if (printer.Int("flow") is { } flow)
            {
                details.Add(new DetailItem("Flow", flow.ToString(CultureInfo.CurrentCulture) + "%"));
            }

            if (printer.Number("axis_z") is { } z)
            {
                details.Add(new DetailItem("Z height", z.ToString("0.00", CultureInfo.CurrentCulture) + " mm"));
            }
        }

        List<TemperatureReading> temps = [];
        if (printer.Number("temp_nozzle") is { } nozzle)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, "Nozzle", nozzle, printer.Number("target_nozzle")));
        }

        if (printer.Number("temp_bed") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, printer.Number("target_bed")));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(percent / 100) : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(status.Number("job", "time_printing") ?? job.Number("time_printing")) : null,
            Remaining = running ? PrinterSnapshot.Seconds(status.Number("job", "time_remaining") ?? job.Number("time_remaining")) : null,
            JobName = hasJob ? JsonRead.FileName(name) : null,
            ThumbnailUrl = hasJob ? thumbnailUrl : null,
            Temperatures = temps,
            Details = details,
        };
    }

    internal static PrinterState MapState(string? state) => state?.ToUpperInvariant() switch
    {
        "PRINTING" => PrinterState.Printing,
        "PAUSED" => PrinterState.Paused,
        "FINISHED" => PrinterState.Finished,
        "STOPPED" => PrinterState.Cancelled,
        "ERROR" => PrinterState.Error,
        "ATTENTION" => PrinterState.Attention,
        "BUSY" => PrinterState.Busy,
        _ => PrinterState.Idle, // IDLE, READY
    };
}
