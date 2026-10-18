using System.Globalization;
using System.Text.Json.Nodes;
using TrayCer.Core.Json;
using TrayCer.Core.Models;

namespace TrayCer.Core.Drivers.Elegoo;

/// <summary>
/// Reads Elegoo's SDCP v3 status. Resin printers follow the published spec (ticks in ms); the
/// Centauri Carbon reuses the protocol with its own sub-status table (from Elegoo's elegoo-link
/// adapter) and reports ticks in seconds.
/// </summary>
public static class SdcpParser
{
    /// <summary>FDM machines use the Centauri code table; resin machines the SDCP spec's.</summary>
    public static bool IsFdm(string? machineName, JsonNode? status) =>
        machineName is not null
            ? machineName.Contains("Centauri", StringComparison.OrdinalIgnoreCase) || machineName.Contains("Neptune", StringComparison.OrdinalIgnoreCase)
            : status.At("TempOfNozzle") is not null;

    public static PrinterSnapshot Parse(JsonNode? status, string? machineName)
    {
        bool fdm = IsFdm(machineName, status);
        JsonNode? info = status.At("PrintInfo");
        int code = info.Int("Status") ?? 0;
        int machine = status.At("CurrentStatus") is JsonArray list ? list.FirstOrDefault().Int() ?? 0 : status.Int("CurrentStatus") ?? 0;

        (PrinterState state, string? message) = fdm ? MapFdm(code, machine) : MapResin(code, machine);
        if (info.Int("ErrorNumber") is { } error and not 0)
        {
            message = ErrorText(error);
        }

        bool running = state is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        string? fileName = JsonRead.FileName(info.NonEmpty("Filename"));
        bool hasJob = fileName is not null && (running || state is PrinterState.Finished or PrinterState.Cancelled or PrinterState.Error);

        int? layer = info.Int("CurrentLayer");
        int? total = info.Int("TotalLayer") is > 0 and var t ? t : null;
        double? percent = info.Number("Progress");
        double? progress = percent is not null ? percent / 100
            : layer is not null && total is not null ? (double)layer.Value / total.Value
            : null;

        // Resin firmware counts milliseconds, the Centauri Carbon seconds.
        double unit = fdm ? 1 : 1000;
        double? current = info.Number("CurrentTicks") / unit;
        double? totalTicks = info.Number("TotalTicks") / unit;

        List<DetailItem> details = [];
        if (fdm && running && info.Int("PrintSpeedPct") is > 0 and var speed)
        {
            details.Add(new DetailItem("Speed", speed.ToString(CultureInfo.CurrentCulture) + "%"));
        }

        return new PrinterSnapshot
        {
            State = state,
            Progress = state == PrinterState.Finished ? 1 : hasJob ? PrinterSnapshot.ClampProgress(progress) : null,
            Elapsed = hasJob ? PrinterSnapshot.Seconds(current) : null,
            Remaining = running && totalTicks > 0 ? PrinterSnapshot.Seconds(Math.Max(0, totalTicks.Value - (current ?? 0))) : null,
            JobName = hasJob ? fileName : null,
            Layer = hasJob && total is not null ? layer : null,
            TotalLayers = hasJob ? total : null,
            Message = message,
            Model = machineName,
            Temperatures = fdm ? FdmTemperatures(status) : ResinTemperatures(status),
            Details = details,
        };
    }

    internal static (PrinterState, string?) MapFdm(int code, int machine) => code switch
    {
        1 => (PrinterState.Preparing, "Homing"),
        10 or 11 => (PrinterState.Preparing, "Checking"),
        15 => (PrinterState.Preparing, "Leveling"),
        16 => (PrinterState.Preparing, "Heating"),
        17 => (PrinterState.Preparing, "Resonance test"),
        23 => (PrinterState.Preparing, "Loading filament"),
        5 or 6 => (PrinterState.Paused, null),
        25 => (PrinterState.Attention, "Filament unload problem"),
        26 => (PrinterState.Attention, "Paused while unloading filament"),
        7 => (PrinterState.Busy, "Stopping"),
        24 => (PrinterState.Busy, "Unloading filament"),
        8 => (PrinterState.Cancelled, null),
        9 => (PrinterState.Finished, null),
        14 => (PrinterState.Error, null),
        12 or 13 or 18 or 19 or 20 or 21 or 22 => (PrinterState.Printing, null),
        _ => MachineOnly(machine),
    };

    internal static (PrinterState, string?) MapResin(int code, int machine) => code switch
    {
        1 => (PrinterState.Preparing, "Homing"),
        10 => (PrinterState.Preparing, "Checking file"),
        15 or 18 or 19 or 21 => (PrinterState.Preparing, "Loading"),
        16 => (PrinterState.Preparing, "Preheating"),
        20 => (PrinterState.Preparing, "Leveling"),
        2 or 3 or 4 or 13 => (PrinterState.Printing, null),
        5 or 6 => (PrinterState.Paused, null),
        7 => (PrinterState.Busy, "Stopping"),
        12 => (PrinterState.Busy, "Recovering"),
        8 => (PrinterState.Cancelled, null),
        9 => (PrinterState.Finished, null),
        _ => MachineOnly(machine),
    };

    // With no print sub-status, the machine-level status still says whether it's busy.
    private static (PrinterState, string?) MachineOnly(int machine) => machine switch
    {
        1 => (PrinterState.Printing, null),
        2 => (PrinterState.Busy, "Receiving a file"),
        3 => (PrinterState.Busy, "Exposure test"),
        4 => (PrinterState.Busy, "Self-test"),
        5 => (PrinterState.Busy, "Leveling"),
        6 => (PrinterState.Busy, "Input shaping"),
        9 => (PrinterState.Busy, "Homing"),
        10 => (PrinterState.Busy, "Loading filament"),
        11 => (PrinterState.Busy, "PID tuning"),
        _ => (PrinterState.Idle, null),
    };

    private static string ErrorText(int error) => error switch
    {
        1 => "File check failed",
        2 => "Couldn't read the file",
        3 => "File resolution doesn't match the printer",
        4 => "Unknown file format",
        5 => "File is for a different printer model",
        _ => $"Printer error {error.ToString(CultureInfo.InvariantCulture)}",
    };

    private static List<TemperatureReading> FdmTemperatures(JsonNode? status)
    {
        List<TemperatureReading> temps = [];
        if (status.Number("TempOfNozzle") is { } nozzle)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Nozzle, "Nozzle", nozzle, status.Number("TempTargetNozzle")));
        }

        if (status.Number("TempOfHotbed") is { } bed)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Bed, "Bed", bed, status.Number("TempTargetHotbed")));
        }

        if (status.Number("TempOfBox") is > 0 and var box)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Chamber", box, status.Number("TempTargetBox")));
        }

        return temps;
    }

    private static List<TemperatureReading> ResinTemperatures(JsonNode? status)
    {
        List<TemperatureReading> temps = [];
        if (status.Number("TempOfUVLED") is > 0 and var led)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Other, "UV LED", led));
        }

        if (status.Number("TempOfTank") is > 0 and var tank)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Other, "Resin vat", tank, status.Number("TempTargetTank")));
        }

        if (status.Number("TempOfBox") is > 0 and var box)
        {
            temps.Add(new TemperatureReading(TemperatureKind.Chamber, "Enclosure", box, status.Number("TempTargetBox")));
        }

        return temps;
    }
}
