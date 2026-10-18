using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.FlashForge;

/// <summary>Reads the "detail" object of the Adventurer 5M family's HTTP API (POST :8898/detail).</summary>
public static class FlashForgeParser
{
    public static PrinterSnapshot Parse(JsonNode? detail, Func<string, string> resolve)
    {
        PrinterState state = MapState(detail.NonEmpty("status"));
        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing;
        string? fileName = JsonRead.FileName(detail.NonEmpty("printFileName"));
        bool hasJob = fileName is not null && (running || state is PrinterState.Finished or PrinterState.Cancelled or PrinterState.Error);

        int? total = detail.Int("targetPrintLayer") is > 0 and var t ? t : null;
        string? thumbnail = detail.NonEmpty("printFileThumbUrl") is { } url ? resolve(url) : null;

        string? message = detail.NonEmpty("errorCode") is { } error && error != "0"
            ? $"Printer error {error}"
            : state switch
            {
                PrinterState.Preparing => "Heating",
                PrinterState.Busy when detail.NonEmpty("status") == "calibrate_doing" => "Calibrating",
                PrinterState.Busy when detail.NonEmpty("status") == "downloading" => "Receiving a file",
                _ => null,
            };

        List<TemperatureReading> temps = [];
        bool dual = detail.Int("nozzleCnt") > 1;
        if (detail.Number("leftTemp") is { } left)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, dual ? "Left nozzle" : "Nozzle", left, detail.Number("leftTargetTemp")));
        }

        if (dual && detail.Number("rightTemp") is { } right)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, "Right nozzle", right, detail.Number("rightTargetTemp")));
        }

        if (detail.Number("platTemp") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, detail.Number("platTargetTemp")));
        }

        if (detail.Number("chamberTemp") is > 0 and var chamber)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", chamber, detail.Number("chamberTargetTemp")));
        }

        List<DetailItem> details = [];
        if (running && detail.Int("printSpeedAdjust") is > 0 and var speed)
        {
            details.Add(new DetailItem("Speed", speed.ToString(CultureInfo.CurrentCulture) + "%"));
        }

        if (StationFilament(detail) is { } slot)
        {
            details.Add(slot);
        }
        else if (detail.NonEmpty("leftFilamentType") is { } filament)
        {
            details.Add(new DetailItem("Filament", filament));
        }

        if (detail.NonEmpty("nozzleModel") is { } nozzle)
        {
            details.Add(new DetailItem("Nozzle", nozzle));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(detail.Number("printProgress")) : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(detail.Number("printDuration")) : null,
            Remaining = running ? PrinterSnapshot.Seconds(detail.Number("estimatedTime")) : null,
            JobName = hasJob ? fileName : null,
            Layer = hasJob && total is not null ? detail.Int("printLayer") : null,
            TotalLayers = hasJob ? total : null,
            Message = message,
            Model = detail.NonEmpty("model"),
            ThumbnailUrl = hasJob ? thumbnail : null,
            Temperatures = temps,
            Details = details,
        };
    }

    internal static PrinterState MapState(string? status) => status?.ToLowerInvariant() switch
    {
        "printing" => PrinterState.Printing,
        "heating" => PrinterState.Preparing,
        "pausing" or "pause" or "paused" => PrinterState.Paused,
        "completed" => PrinterState.Finished,
        "cancel" or "cancelled" => PrinterState.Cancelled,
        "error" => PrinterState.Error,
        "busy" or "calibrate_doing" or "downloading" => PrinterState.Busy,
        _ => PrinterState.Idle, // ready
    };

    /// <summary>The AD5X material station's active slot, with its color.</summary>
    private static DetailItem? StationFilament(JsonNode? detail)
    {
        JsonNode? station = detail.At("matlStationInfo");
        if (station.Int("currentSlot") is not { } slot || slot <= 0)
        {
            return null;
        }

        JsonNode? info = station.Array("slotInfos")?.FirstOrDefault(s => s.Int("slotId") == slot);
        if (info.NonEmpty("materialName") is not { } material)
        {
            return null;
        }

        string? color = info.NonEmpty("materialColor") is { Length: 7 } hex && hex[0] == '#' ? hex.ToUpperInvariant() : null;
        return new DetailItem("Filament", $"{material} · slot {slot.ToString(CultureInfo.CurrentCulture)}", color);
    }
}
