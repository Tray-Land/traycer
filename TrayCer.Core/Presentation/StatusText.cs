using System.Globalization;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;

namespace TrayCer.Core.Presentation;

/// <summary>The words for a printer's state, shared by the cards, the tooltip and notifications.</summary>
public static class StatusText
{
    /// <summary>Between printers in the tray marquee: a full bullet, spaced wide, since a middle dot is a single pixel at 16 px.</summary>
    public const string MarqueeSeparator = "   •   ";

    public static string StateLabel(PrinterState state) => state switch
    {
        PrinterState.Offline => "Offline",
        PrinterState.Idle => "Idle",
        PrinterState.Busy => "Busy",
        PrinterState.Preparing => "Preparing",
        PrinterState.Printing => "Printing",
        PrinterState.Paused => "Paused",
        PrinterState.Attention => "Needs attention",
        PrinterState.Finished => "Finished",
        PrinterState.Cancelled => "Cancelled",
        PrinterState.Error => "Error",
        _ => state.ToString(),
    };

    /// <summary>The label for the card's status pill, which also covers connection trouble.</summary>
    public static string Label(PrinterStatus status) => status.Connection switch
    {
        ConnectionState.Connecting => "Connecting",
        ConnectionState.AuthFailed => "Can't sign in",
        ConnectionState.Incompatible => "Not recognized",
        ConnectionState.Unreachable => "Offline",
        _ => StateLabel(status.DisplayState),
    };

    /// <summary>Whole percent, rounded down so a print at 99.6% doesn't claim 100.</summary>
    public static string Percent(double progress) =>
        Math.Floor(Math.Clamp(progress, 0, 1) * 100).ToString(CultureInfo.CurrentCulture) + "%";

    /// <summary>"<1m", "45m", "1h 20m", "2d 4h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes}m";
        }

        if (span < TimeSpan.FromDays(1))
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.Hours == 0 ? $"{span.Days}d" : $"{span.Days}d {span.Hours}h";
    }

    /// <summary>"3:45 PM", "tomorrow 9:10 AM" or "Fri 9:10 AM", for when a print should finish.</summary>
    public static string ClockTime(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime local = at.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        string time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return time;
        }

        if (local.Date == today.AddDays(1))
        {
            return "tomorrow " + time;
        }

        return local.ToString("ddd", CultureInfo.CurrentCulture) + " " + time;
    }

    /// <summary>"1h 20m left · done at 3:45 PM", or the elapsed time when there's no estimate.</summary>
    public static string? TimeLine(PrinterSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.State is not (PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention))
        {
            return null;
        }

        if (snapshot.Remaining is { } remaining)
        {
            string left = $"{Duration(remaining)} left";
            return snapshot.State == PrinterState.Printing ? $"{left} · done at {ClockTime(now + remaining, now)}" : left;
        }

        return snapshot.Elapsed is { } elapsed && elapsed > TimeSpan.Zero ? $"Running for {Duration(elapsed)}" : null;
    }

    /// <summary>One line for the tray tooltip: "Voron: 45% · 1h 20m left".</summary>
    public static string TooltipLine(PrinterStatus status)
    {
        string name = status.Config.Name;
        if (status.NeedsMarkComplete && status.DisplayState is PrinterState.Finished or PrinterState.Idle)
        {
            return $"{name}: Finished";
        }

        if (!status.HasReading)
        {
            return $"{name}: {Label(status)}";
        }

        PrinterSnapshot snapshot = status.Snapshot!;
        string? percent = snapshot.Progress is { } p ? Percent(p) : null;
        return snapshot.State switch
        {
            PrinterState.Printing when snapshot.Remaining is { } r => $"{name}: {percent ?? "Printing"} · {Duration(r)} left",
            PrinterState.Printing => $"{name}: {percent ?? "Printing"}",
            PrinterState.Paused or PrinterState.Attention when percent is not null => $"{name}: {StateLabel(snapshot.State)} at {percent}",
            _ => $"{name}: {StateLabel(snapshot.State)}",
        };
    }

    /// <summary>What scrolls across the green tray icon: "Voron done   •   P1S done".</summary>
    public static string Marquee(IEnumerable<PrinterStatus> finished) =>
        string.Join(MarqueeSeparator, finished.Select(p => $"{p.Config.Name} done"));

    /// <summary>Title and body for a notification about <paramref name="printEvent"/>; null for events not worth one.</summary>
    public static (string Title, string Body)? Notification(string printerName, PrintEvent printEvent)
    {
        string job = printEvent.JobName ?? "The print";
        return printEvent.Kind switch
        {
            PrintEventKind.Finished => ($"{printerName} finished", $"{job} is done."),
            PrintEventKind.Failed => ($"{printerName}: print failed", printEvent.Message ?? $"{job} stopped with an error."),
            PrintEventKind.Error => ($"{printerName} has a problem", printEvent.Message ?? "The printer reported an error."),
            PrintEventKind.NeedsAttention => ($"{printerName} needs attention", printEvent.Message ?? $"{job} is waiting for you."),
            PrintEventKind.Paused => ($"{printerName} paused", printEvent.Message ?? $"{job} is paused."),
            _ => null,
        };
    }
}
