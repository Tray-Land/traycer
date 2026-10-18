using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;

namespace TrayCer.Views;

/// <summary>
/// One printer at a glance. Every line below the name is optional and appears only when the
/// printer reports that piece: a FlashForge on the old protocol shows a percentage, a Bambu adds
/// time left, layers and temperatures, a Moonraker printer a thumbnail too.
/// </summary>
public sealed partial class PrinterCard : UserControl, IDisposable
{
    private readonly string _printerId;
    private readonly ThumbnailLoader _thumbnail;

    public PrinterCard(string printerId)
    {
        _printerId = printerId;
        InitializeComponent();
        _thumbnail = new ThumbnailLoader(Thumbnail, ThumbnailFrame, decodeWidth: 112);
    }

    /// <summary>Raised with the printer id when the card is clicked.</summary>
    public event EventHandler<string>? Invoked;

    public void Update(PrinterStatus status, DateTimeOffset now)
    {
        NameText.Text = status.Config.Name;
        StateText.Text = StatusText.Label(status);
        VisualStateManager.GoToState(this, ToneFor(status), false);

        PrinterSnapshot? snapshot = status.HasReading ? status.Snapshot : null;
        Show(JobText, snapshot?.JobName);

        // Progress: a value when known, indeterminate while getting ready or when the printer has no percentage.
        bool running = snapshot?.State is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        ProgressRow.Visibility = running || snapshot?.State == PrinterState.Finished ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is not null)
        {
            Progress.IsIndeterminate = running && (snapshot.Progress is null || snapshot.State == PrinterState.Preparing);
            Progress.Value = (snapshot.Progress ?? 0) * 100;
            Progress.ShowPaused = snapshot.State is PrinterState.Paused or PrinterState.Attention;
            Progress.ShowError = false;
            PercentText.Text = snapshot.Progress is { } p ? StatusText.Percent(p) : "";
        }

        Show(TimeText, snapshot is null ? null : StatusText.TimeLine(snapshot, now));
        Show(MessageText, MessageFor(status, now));
        Show(FactsText, snapshot is null ? null : Facts(snapshot));
        _thumbnail.Load(_printerId, snapshot?.ThumbnailUrl);

        // Read the card the way it looks: name, state, then whatever lines are showing.
        List<string> spoken = [status.Config.Name, StateText.Text];
        if (ProgressRow.Visibility == Visibility.Visible && PercentText.Text.Length > 0)
        {
            spoken.Add(PercentText.Text);
        }

        spoken.AddRange(new[] { JobText, TimeText, MessageText }.Where(b => b.Visibility == Visibility.Visible).Select(b => b.Text));
        AutomationProperties.SetName(CardButton, string.Join(", ", spoken));
    }

    public void CancelThumbnail() => _thumbnail.Cancel();

    public void Dispose() => _thumbnail.Cancel();

    /// <summary>Layer and temperatures in one quiet line: "Layer 45 of 200 · Nozzle 215° · Bed 60°".</summary>
    internal static string? Facts(PrinterSnapshot snapshot)
    {
        List<string> parts = [];
        if (snapshot.Layer is { } layer && snapshot.TotalLayers is { } total)
        {
            parts.Add($"Layer {layer.ToString(CultureInfo.CurrentCulture)} of {total.ToString(CultureInfo.CurrentCulture)}");
        }

        foreach (TemperatureReading t in snapshot.Temperatures.Where(t => t.Kind != TemperatureKind.Other || snapshot.Temperatures.Count <= 3).Take(4))
        {
            parts.Add($"{t.Label} {Temperature(t)}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"215°", or "180° → 215°" while it's still heating toward the target.</summary>
    internal static string Temperature(TemperatureReading t)
    {
        string current = Math.Round(t.Current).ToString(CultureInfo.CurrentCulture) + "°";
        return t.Target is > 0 and var target && Math.Abs(target - t.Current) >= 3
            ? $"{current} → {Math.Round(target).ToString(CultureInfo.CurrentCulture)}°"
            : current;
    }

    private static string ToneFor(PrinterStatus status)
    {
        if (status.Connection is ConnectionState.AuthFailed or ConnectionState.Incompatible)
        {
            return "Critical";
        }

        if (status.Connection is ConnectionState.Connecting or ConnectionState.Unreachable)
        {
            return "Dim";
        }

        return status.DisplayState switch
        {
            PrinterState.Printing or PrinterState.Preparing => "Active",
            PrinterState.Paused or PrinterState.Attention => "Caution",
            PrinterState.Finished => "Success",
            PrinterState.Error => "Critical",
            PrinterState.Offline => "Dim",
            _ => "Neutral",
        };
    }

    private static string? MessageFor(PrinterStatus status, DateTimeOffset now)
    {
        if (status.Connection is not ConnectionState.Connected)
        {
            return status.Connection switch
            {
                ConnectionState.Connecting => null,
                ConnectionState.Reconnecting => "Reconnecting…",
                _ when status.NextCheck is { } next && status.Connection == ConnectionState.Unreachable =>
                    $"{status.Problem} Trying again at {StatusText.ClockTime(next, now)}.",
                _ => status.Problem,
            };
        }

        PrinterSnapshot snapshot = status.Snapshot!;
        if (snapshot.Message is { } message)
        {
            return message;
        }

        // Printers that drop straight back to idle still get to say how the last print ended.
        if (snapshot.State == PrinterState.Idle && status.LastEvent is { Kind: PrintEventKind.Finished or PrintEventKind.Cancelled or PrintEventKind.Failed } last)
        {
            string ended = last.Kind switch
            {
                PrintEventKind.Finished => "Finished",
                PrintEventKind.Cancelled => "Cancelled",
                _ => "Failed",
            };
            return $"{ended} {last.JobName ?? "last print"} at {StatusText.ClockTime(last.At, now)}";
        }

        return snapshot.State switch
        {
            PrinterState.Attention => "The printer is waiting for you.",
            PrinterState.Finished when status.LastEvent is { Kind: PrintEventKind.Finished } done => $"Done at {StatusText.ClockTime(done.At, now)}",
            _ => null,
        };
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, _printerId);
}
