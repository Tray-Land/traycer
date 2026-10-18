using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Discovery;
using TrayCer.Core.Models;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// Scans the network for printers and lists them as they answer. Picking one opens the add form
/// with what discovery learned (type, address, serial number) already filled in.
/// </summary>
public sealed partial class DiscoverView : UserControl, IFlyoutView, IDisposable
{
    private readonly TrayFlyoutWindow _host;
    private readonly HashSet<string> _listed = [];
    private CancellationTokenSource? _scan;
    private bool _complete;

    internal DiscoverView(TrayFlyoutWindow host)
    {
        _host = host;
        InitializeComponent();
    }

    public void OnShown()
    {
        BackButton.Focus(FocusState.Programmatic);

        // A scan cut short by leaving the page starts over; a finished one keeps its list.
        if (!_complete)
        {
            _ = ScanAsync();
        }
    }

    public void OnHidden() => StopScan();

    public void Dispose() => StopScan();

    private async Task ScanAsync()
    {
        StopScan();
        CancellationTokenSource scan = _scan = new CancellationTokenSource();
        _complete = false;
        _listed.Clear();
        Results.Children.Clear();
        NothingFound.Visibility = Visibility.Collapsed;
        ScanRing.IsActive = true;
        ScanButton.IsEnabled = false;
        ScanText.Text = "Looking for printers on your network…";

        try
        {
            await LanScanner.ScanAsync(
                DnsSdBrowser.FindHostsAsync(LanScanner.ListenWindow, scan.Token),
                printer => DispatcherQueue.TryEnqueue(() => Add(printer, scan)),
                scan.Token);
        }
        catch (OperationCanceledException)
        {
            // Left the page.
        }

        if (scan.IsCancellationRequested)
        {
            return;
        }

        _complete = true;
        ScanRing.IsActive = false;
        ScanButton.IsEnabled = true;
        ScanText.Text = Results.Children.Count switch
        {
            0 => "",
            1 => "Found 1 printer. Pick it to add it.",
            int n => $"Found {n} printers. Pick one to add it.",
        };
        NothingFound.Visibility = Results.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add(DiscoveredPrinter printer, CancellationTokenSource scan)
    {
        if (scan.IsCancellationRequested || !_listed.Add($"{printer.Kind}|{printer.Serial ?? printer.Host}"))
        {
            return;
        }

        PrinterKindInfo info = PrinterKindInfo.For(printer.Kind);
        bool added = PrinterStore.All.Any(p => p.Kind == printer.Kind
            && (string.Equals(p.Host, printer.Host, StringComparison.OrdinalIgnoreCase) || (printer.Serial is not null && p.Serial == printer.Serial)));

        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = printer.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        string detail = string.Join(" · ", new[] { info.DisplayName, printer.Model != printer.Title ? printer.Model : null, printer.Host }.Where(s => !string.IsNullOrEmpty(s)));
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["DetailCaptionTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis });

        Grid layout = new() { ColumnSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(text);
        if (added)
        {
            TextBlock addedLabel = new() { Text = "Added", VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["DetailCaptionTextStyle"] };
            Grid.SetColumn(addedLabel, 1);
            layout.Children.Add(addedLabel);
        }

        Button button = new()
        {
            Content = layout,
            Style = (Style)Application.Current.Resources["CardButtonStyle"],
            IsEnabled = !added,
        };
        AutomationProperties.SetName(button, added ? $"{printer.Title}, {detail}, already added" : $"Add {printer.Title}, {detail}");
        button.Click += (_, _) => _host.Navigate(new EditPrinterView(_host, printer));
        Results.Children.Add(button);
    }

    private void StopScan()
    {
        _scan?.Cancel();
        _scan = null;
        ScanRing.IsActive = false;
        ScanButton.IsEnabled = true;
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.GoBack();

    private void AddByAddress_Click(object sender, RoutedEventArgs e) => _host.Navigate(new EditPrinterView(_host));
}
