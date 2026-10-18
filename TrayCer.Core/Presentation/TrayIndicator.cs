using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;

namespace TrayCer.Core.Presentation;

public enum TrayGlyph
{
    /// <summary>Nothing to report: the app icon.</summary>
    App,

    /// <summary>A print's percentage over a progress bar.</summary>
    Progress,

    /// <summary>Pause bars over a progress bar.</summary>
    Paused,

    /// <summary>A green check: a print finished and hasn't been marked complete.</summary>
    Done,

    /// <summary>An amber "!": a printer is waiting on the user.</summary>
    Warning,

    /// <summary>A red "!": a print failed or a printer has an error.</summary>
    Error,
}

/// <summary>What the tray icon shows for the whole fleet, and the tooltip under it.</summary>
public sealed record TrayIndicator(TrayGlyph Glyph, double? Progress, string Tooltip)
{
    public const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip

    /// <summary>
    /// One icon for many printers, most urgent first: errors, then waiting-on-you, then finished
    /// and not yet marked complete, then a paused print, then the print that will finish soonest.
    /// </summary>
    public static TrayIndicator Compute(IReadOnlyList<PrinterStatus> printers, string appName)
    {
        string tooltip = BuildTooltip(printers, appName);

        if (printers.Any(p => p.Attention == AttentionLevel.Error))
        {
            return new TrayIndicator(TrayGlyph.Error, null, tooltip);
        }

        if (printers.Any(p => p.Attention == AttentionLevel.Warning))
        {
            return new TrayIndicator(TrayGlyph.Warning, null, tooltip);
        }

        if (printers.Any(p => p.NeedsMarkComplete))
        {
            return new TrayIndicator(TrayGlyph.Done, null, tooltip);
        }

        PrinterStatus? paused = printers.FirstOrDefault(p => p.HasReading && p.Snapshot!.State is PrinterState.Paused or PrinterState.Attention);
        if (paused is not null)
        {
            return new TrayIndicator(TrayGlyph.Paused, paused.Snapshot!.Progress, tooltip);
        }

        PrinterStatus? next = Printing(printers).FirstOrDefault();
        return next is not null
            ? new TrayIndicator(TrayGlyph.Progress, next.Snapshot!.Progress, tooltip)
            : new TrayIndicator(TrayGlyph.App, null, tooltip);
    }

    /// <summary>Printing printers, soonest to finish first; those without an estimate go by progress.</summary>
    internal static IEnumerable<PrinterStatus> Printing(IEnumerable<PrinterStatus> printers) => printers
        .Where(p => p.HasReading && p.Snapshot!.State is PrinterState.Printing or PrinterState.Preparing)
        .OrderBy(p => p.Snapshot!.State == PrinterState.Preparing ? 1 : 0)
        .ThenBy(p => p.Snapshot!.Remaining ?? TimeSpan.MaxValue)
        .ThenByDescending(p => p.Snapshot!.Progress ?? 0);

    /// <summary>The app name, then one line per printer worth mentioning, within the shell's 127 characters.</summary>
    internal static string BuildTooltip(IReadOnlyList<PrinterStatus> printers, string appName)
    {
        if (printers.Count == 0)
        {
            return $"{appName}\nNo printers yet";
        }

        if (printers.Count > 1 && printers.All(p => p.Attention == AttentionLevel.None && !p.NeedsMarkComplete && p.HasReading && p.Snapshot!.State == PrinterState.Idle))
        {
            return $"{appName}\nAll printers idle";
        }

        // Each printer in at most one group: problems, then finished, then printing, then paused.
        List<PrinterStatus> ordered =
        [
            .. printers.Where(p => p.Attention is AttentionLevel.Error or AttentionLevel.Warning),
            .. printers.Where(p => p.Attention == AttentionLevel.None && p.NeedsMarkComplete),
            .. Printing(printers).Where(p => p.Attention == AttentionLevel.None && !p.NeedsMarkComplete),
            .. printers.Where(p => p.Attention == AttentionLevel.None && !p.NeedsMarkComplete && p.HasReading && p.Snapshot!.State is PrinterState.Paused or PrinterState.Attention),
        ];
        ordered.AddRange(printers.Except(ordered));

        // Each line must leave room for a "+n more" line after it, unless it's the last.
        const int moreReserve = 9; // "\n+99 more"
        string text = appName;
        for (int i = 0; i < ordered.Count; i++)
        {
            string line = StatusText.TooltipLine(ordered[i]);
            bool last = i == ordered.Count - 1;
            int room = MaxTooltipLength - text.Length - 1 - (last ? 0 : moreReserve);
            if (line.Length > room)
            {
                if (i > 0)
                {
                    return text + $"\n+{ordered.Count - i} more";
                }

                line = line[..Math.Max(0, room - 1)] + "…";
            }

            text += "\n" + line;
        }

        return text;
    }
}
