using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Creality;

/// <summary>
/// Reads the merged status of Creality's stock-firmware WebSocket (port 9999), used by the K1
/// family, K2, Hi and Ender-3 V3. The state table follows the ha_creality_ws integration, which
/// worked it out from captures: the file name decides whether a job exists at all.
/// </summary>
public static class CrealityParser
{
    public static PrinterSnapshot Parse(JsonNode? data, string? thumbnailUrl = null)
    {
        double errorCode = data.Number("err", "errcode") ?? data.Number("err") ?? 0;
        double selfTest = data.Number("withSelfTest") ?? 0;
        int? code = data.Int("state");
        string? fileName = JsonRead.FileName(data.NonEmpty("printFileName"));
        double? percent = data.Number("printProgress") ?? data.Number("dProgress");

        PrinterState state;
        if (selfTest is >= 1 and <= 99)
        {
            state = PrinterState.Busy;
        }
        else if (fileName is null)
        {
            state = PrinterState.Idle;
        }
        else
        {
            state = code switch
            {
                _ when percent >= 100 => PrinterState.Finished,
                5 => PrinterState.Paused,
                4 => PrinterState.Cancelled,
                1 => PrinterState.Printing,
                2 => PrinterState.Finished,
                0 or 7 => PrinterState.Preparing,
                _ => PrinterState.Idle,
            };
        }

        // Some firmware never clears an error code, so a running print stays a print and shows
        // the code as a message instead of turning the whole job into an error.
        string? message = null;
        if (errorCode != 0)
        {
            message = $"Printer error {errorCode.ToString(CultureInfo.InvariantCulture)}";
            if (state is not (PrinterState.Printing or PrinterState.Preparing or PrinterState.Paused))
            {
                state = PrinterState.Error;
            }
        }
        else if (state == PrinterState.Busy)
        {
            message = "Self-test";
        }

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing;
        bool hasJob = fileName is not null && state is not PrinterState.Idle and not PrinterState.Busy;
        int? total = data.Int("TotalLayer") is > 0 and var t ? t : null;

        List<TemperatureReading> temps = [];
        if (data.Number("nozzleTemp") is { } nozzle)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, "Nozzle", nozzle, data.Number("targetNozzleTemp")));
        }

        if (data.Number("bedTemp0") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, data.Number("targetBedTemp0")));
        }

        if (data.Number("boxTemp") is > 0 and var box)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", box, data.Number("targetBoxTemp")));
        }

        List<DetailItem> details = [];
        if (running && data.Int("curFeedratePct") is > 0 and var feed)
        {
            details.Add(new DetailItem("Speed", feed.ToString(CultureInfo.CurrentCulture) + "%"));
        }

        if (hasJob && data.Number("usedMaterialLength") is > 0 and var used)
        {
            details.Add(new DetailItem("Filament used", (used / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " m"));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(percent / 100) : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(data.Number("printJobTime")) : null,
            Remaining = running ? PrinterSnapshot.Seconds(data.Number("printLeftTime")) : null,
            JobName = hasJob ? fileName : null,
            Layer = hasJob && total is not null ? data.Int("layer") : null,
            TotalLayers = hasJob ? total : null,
            Message = message,
            Model = data.NonEmpty("model"),
            ThumbnailUrl = hasJob ? thumbnailUrl : null,
            Temperatures = temps,
            Details = details,
        };
    }
}
