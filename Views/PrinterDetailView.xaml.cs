using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;
using TrayCer.Services;
using Windows.UI;

namespace TrayCer.Views;

/// <summary>Everything one printer reports, section by section; a section appears only when it has rows.</summary>
public sealed partial class PrinterDetailView : UserControl, IFlyoutView, IDisposable
{
    private readonly TrayFlyoutWindow _host;
    private readonly string _printerId;
    private readonly ThumbnailLoader _thumbnail;
    private readonly List<(TextBlock Value, ProgressBar Bar)> _heaters = [];
    private string[] _heaterLabels = [];
    private bool _shown;

    internal PrinterDetailView(TrayFlyoutWindow host, string printerId)
    {
        _host = host;
        _printerId = printerId;
        InitializeComponent();
        _thumbnail = new ThumbnailLoader(Thumbnail, ThumbnailFrame, decodeWidth: 320);
    }

    public void OnShown()
    {
        if (_shown)
        {
            return;
        }

        _shown = true;
        App.Current.Hub.Changed += Hub_Changed;
        Render();
        BackButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        App.Current.Hub.Changed -= Hub_Changed;
        _thumbnail.Cancel();
        Progress.Stop();
    }

    public void Dispose() => OnHidden();

    private void Render()
    {
        if (App.Current.Hub.Get(_printerId) is not { } status)
        {
            _host.GoBack(); // Removed meanwhile.
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        PrinterConfig config = status.Config;
        TitleText.Text = config.Name;
        RemoveQuestion.Text = $"Remove {config.Name}?";
        WebButton.Visibility = config.Info.HasWebInterface && App.Current.Hub.WebInterface(_printerId) is not null ? Visibility.Visible : Visibility.Collapsed;

        PrinterSnapshot? snapshot = status.HasReading ? status.Snapshot : null;
        StateText.Text = StatusText.Label(status);
        SetText(JobText, snapshot?.JobName);

        bool running = snapshot?.State is PrinterState.Printing or PrinterState.Paused or PrinterState.Preparing or PrinterState.Attention;
        ProgressRow.Visibility = running || snapshot?.State == PrinterState.Finished ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is not null)
        {
            Progress.Update(snapshot, running);
            PercentText.Text = snapshot.Progress is { } p ? StatusText.Percent(p) : "";
        }

        SetText(TimeText, snapshot is null ? null : TimeLine(snapshot, now));
        ShowMessage(status);
        CompleteButton.Visibility = status.NeedsMarkComplete ? Visibility.Visible : Visibility.Collapsed;
        _thumbnail.Load(_printerId, snapshot?.ThumbnailUrl);
        if (snapshot is not null)
        {
            Thumbnail.Show(BuildProgress.Built(snapshot, status.NeedsMarkComplete), snapshot.State == PrinterState.Printing);
        }

        FillTemperatures(snapshot);
        Fill(DetailSection, DetailRows, snapshot?.Details.Select(d => (d.Label, d.Value, d.Color)) ?? []);

        List<(string, string, string?)> printer =
        [
            ("Type", config.Info.DisplayName, null),
            ("Address", config.Host, null),
        ];
        if (snapshot?.Model is { } model)
        {
            printer.Add(("Model", model, null));
        }

        if (config.Serial is { Length: > 0 } serial)
        {
            printer.Add(("Serial number", serial, null));
        }

        if (status.LastUpdated is { } updated)
        {
            printer.Add(("Last update", StatusText.ClockTime(updated, now), null));
        }

        Fill(null, PrinterRows, printer);
    }

    /// <summary>Time left plus elapsed, which the card has no room for.</summary>
    private static string? TimeLine(PrinterSnapshot snapshot, DateTimeOffset now)
    {
        string? line = StatusText.TimeLine(snapshot, now);
        if (snapshot.Remaining is not null && snapshot.Elapsed is { } elapsed && elapsed > TimeSpan.Zero)
        {
            line += $" · {StatusText.Duration(elapsed)} so far";
        }

        return line;
    }

    private static string TemperatureText(TemperatureReading t)
    {
        string current = t.Current.ToString("0.#", CultureInfo.CurrentCulture) + " °C";
        return t.IsHeating ? $"{current} of {t.Target!.Value.ToString("0", CultureInfo.CurrentCulture)} °C" : current;
    }

    private void ShowMessage(PrinterStatus status)
    {
        (InfoBarSeverity severity, string? title, string? message) = status.Connection switch
        {
            ConnectionState.AuthFailed => (InfoBarSeverity.Error, "Can't sign in", status.Problem),
            ConnectionState.Incompatible => (InfoBarSeverity.Error, "Not recognized", status.Problem),
            ConnectionState.Unreachable => (InfoBarSeverity.Warning, "Can't reach the printer", status.Problem),
            ConnectionState.Reconnecting => (InfoBarSeverity.Informational, "Reconnecting", status.Problem),
            _ when status.Snapshot?.Message is { } text => (
                status.Snapshot.State switch
                {
                    PrinterState.Error => InfoBarSeverity.Error,
                    PrinterState.Attention or PrinterState.Paused => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational,
                },
                (string?)null,
                text),
            _ => (InfoBarSeverity.Informational, null, null),
        };

        MessageBar.Severity = severity;
        MessageBar.Title = title ?? "";
        MessageBar.Message = message ?? "";
        MessageBar.IsOpen = title is not null || message is not null;
    }

    /// <summary>
    /// The temperature rows, each with a bar while its heater climbs to its target. The rows are
    /// kept from one update to the next, so a bar eases along instead of starting over.
    /// </summary>
    private void FillTemperatures(PrinterSnapshot? snapshot)
    {
        IReadOnlyList<TemperatureReading> readings = snapshot?.Temperatures ?? [];
        if (!readings.Select(t => t.Label).SequenceEqual(_heaterLabels))
        {
            _heaterLabels = [.. readings.Select(t => t.Label)];
            _heaters.Clear();
            TemperatureRows.Children.Clear();
            foreach (TemperatureReading reading in readings)
            {
                (Grid row, TextBlock value) = Row(reading.Label, null);
                ProgressBar bar = new() { Style = (Style)Application.Current.Resources["HeaterBarStyle"] };
                TemperatureRows.Children.Add(new StackPanel { Spacing = 4, Children = { row, bar } });
                _heaters.Add((value, bar));
            }
        }

        for (int i = 0; i < readings.Count; i++)
        {
            TemperatureReading reading = readings[i];
            (TextBlock value, ProgressBar bar) = _heaters[i];
            value.Text = TemperatureText(reading);
            bar.Value = reading.HeatProgress ?? 0;
            bar.Visibility = BuildProgress.ShowsHeat(reading, snapshot!.State) ? Visibility.Visible : Visibility.Collapsed;
        }

        TemperatureSection.Visibility = readings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Replaces a section's rows: label on the left, value on the right, an optional color swatch.</summary>
    private static void Fill(FrameworkElement? section, StackPanel rows, IEnumerable<(string Label, string Value, string? Color)> items)
    {
        rows.Children.Clear();
        foreach ((string label, string text, string? color) in items)
        {
            (Grid row, TextBlock value) = Row(label, color);
            value.Text = text;
            rows.Children.Add(row);
        }

        if (section is not null)
        {
            section.Visibility = rows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>One row: the label on the left, an empty value on the right with an optional color swatch before it.</summary>
    private static (Grid Row, TextBlock Value) Row(string label, string? color)
    {
        Grid row = new() { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TextBlock name = new() { Text = label, Style = (Style)Application.Current.Resources["DetailLabelTextStyle"] };
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (ParseColor(color) is { } swatch)
        {
            right.Children.Add(new Ellipse
            {
                Style = (Style)Application.Current.Resources["SwatchStyle"],
                Fill = new SolidColorBrush(swatch),
            });
        }

        TextBlock value = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, IsTextSelectionEnabled = true };
        right.Children.Add(value);
        Grid.SetColumn(right, 1);
        row.Children.Add(name);
        row.Children.Add(right);
        return (row, value);
    }

    private static Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;

    private static void SetText(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Hub_Changed(object? sender, EventArgs e) => Render();

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.GoBack();

    private void CompleteButton_Click(object sender, RoutedEventArgs e)
    {
        // The button hides with the flag; keep focus in the page instead of losing it.
        BackButton.Focus(FocusState.Programmatic);
        Wipe.Play();
        App.Current.Hub.MarkComplete(_printerId);
    }

    private async void WebButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.Hub.WebInterface(_printerId) is { } uri)
        {
            _host.HidePopup();
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterStore.Find(_printerId) is { } config)
        {
            _host.Navigate(new EditPrinterView(_host, config));
        }
    }

    private void ConfirmRemove_Click(object sender, RoutedEventArgs e)
    {
        RemoveFlyout.Hide();
        PrinterStore.Remove(_printerId);
        _host.GoHome();
    }
}
