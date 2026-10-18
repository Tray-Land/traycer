using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Discovery;
using TrayCer.Core.Drivers;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// Adds or edits a printer. The form grows with the printer type: an address alone for Creality
/// or Elegoo, plus a serial number and access code for Bambu Lab, a user name and password for
/// PrusaLink. "Detect" fills in the type (and serial, when it can) from the address.
/// </summary>
public sealed partial class EditPrinterView : UserControl, IFlyoutView, IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private readonly TrayFlyoutWindow _host;
    private readonly PrinterConfig? _editing;
    private readonly bool _hasSavedSecret;
    private CancellationTokenSource? _work;

    // Everything the last Detect found, so switching the type picks up that protocol's serial and name.
    private IReadOnlyList<DiscoveredPrinter> _detections = [];
    private DiscoveredPrinter? _detected;

    /// <summary>A new printer, optionally prefilled from discovery.</summary>
    internal EditPrinterView(TrayFlyoutWindow host, DiscoveredPrinter? found = null)
    {
        _host = host;
        InitializeComponent();
        KindBox.ItemsSource = PrinterKindInfo.All;
        if (found is not null)
        {
            _detections = [found];
            ApplyDetection(found);
            DetectText.Text = DetectedSummary(found, []);
        }

        DetectButton.IsEnabled = !string.IsNullOrWhiteSpace(HostBox.Text);
    }

    /// <summary>An existing printer. Its secret stays unless a new one is typed.</summary>
    internal EditPrinterView(TrayFlyoutWindow host, PrinterConfig editing)
        : this(host)
    {
        _editing = editing;
        _hasSavedSecret = PrinterStore.SecretFor(editing.Id) is not null;
        TitleText.Text = "Edit printer";
        HostBox.Text = editing.Host;
        KindBox.SelectedItem = editing.Info;
        NameBox.Text = editing.Name;
        SerialBox.Text = editing.Serial ?? "";
        UsernameBox.Text = editing.Username ?? "";
        SlugBox.Text = editing.Slug ?? "";
        DetectButton.IsEnabled = true;
        UpdateFields();
    }

    private PrinterKindInfo? Kind => KindBox.SelectedItem as PrinterKindInfo;

    public void OnShown() => (string.IsNullOrEmpty(HostBox.Text) ? HostBox : (Control)BackButton).Focus(FocusState.Programmatic);

    public void OnHidden() => CancelWork();

    public void Dispose() => CancelWork();

    private void UpdateFields()
    {
        PrinterKindInfo? info = Kind;
        KindFields.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        CoversText.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is null)
        {
            return;
        }

        CoversText.Text = $"For {info.Covers}. Add :port to the address if it isn't {info.DefaultPort}.";

        SerialPanel.Visibility = info.SerialLabel is null ? Visibility.Collapsed : Visibility.Visible;
        SerialBox.Header = info.SerialLabel;
        SerialHelp.Text = info.SerialHelp ?? "";

        UsernamePanel.Visibility = info.AsksForUsername ? Visibility.Visible : Visibility.Collapsed;
        SlugPanel.Visibility = info.AsksForSlug ? Visibility.Visible : Visibility.Collapsed;

        SecretPanel.Visibility = info.SecretLabel is null ? Visibility.Collapsed : Visibility.Visible;
        SecretBox.Header = info.SecretRequired ? info.SecretLabel : $"{info.SecretLabel} (optional)";
        SecretBox.PlaceholderText = _hasSavedSecret ? "Saved; type to replace it" : "";
        SecretHelp.Text = info.SecretHelp ?? "";
    }

    private PrinterConfig BuildConfig()
    {
        PrinterKindInfo info = Kind!;
        string host = HostBox.Text.Trim();
        string name = NameBox.Text.Trim() is { Length: > 0 } typed ? typed
            : _detected?.Kind == info.Kind && _detected.Title != _detected.Host ? _detected.Title
            : info.DisplayName;
        return new PrinterConfig(
            _editing?.Id ?? PrinterConfig.NewId(),
            name,
            info.Kind,
            host,
            info.SerialLabel is null ? null : NullIfEmpty(SerialBox.Text),
            info.AsksForUsername ? NullIfEmpty(UsernameBox.Text) : null,
            info.AsksForSlug ? NullIfEmpty(SlugBox.Text) : null);
    }

    /// <summary>The secret to use: what's typed, else the saved one when editing.</summary>
    private string? EffectiveSecret() =>
        SecretBox.Password is { Length: > 0 } typed ? typed
        : _editing is not null ? PrinterStore.SecretFor(_editing.Id)
        : null;

    /// <summary>
    /// Fills the form from a detection. Fields that a previous detection filled are replaced;
    /// anything the user typed stays.
    /// </summary>
    private void ApplyDetection(DiscoveredPrinter found)
    {
        DiscoveredPrinter? previous = _detected;
        _detected = found;
        HostBox.Text = found.Host;
        if (Kind?.Kind != found.Kind)
        {
            KindBox.SelectedItem = PrinterKindInfo.For(found.Kind); // Raises SelectionChanged, which calls back here harmlessly.
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == previous?.Title)
        {
            NameBox.Text = found.Title != found.Host ? found.Title : "";
        }

        if (string.IsNullOrWhiteSpace(SerialBox.Text) || SerialBox.Text == previous?.Serial)
        {
            SerialBox.Text = found.Serial ?? "";
        }

        UpdateFields();
    }

    private static string DetectedSummary(DiscoveredPrinter found, IReadOnlyList<DiscoveredPrinter> others)
    {
        string model = found.Model is null ? "" : $" ({found.Model})";
        string also = others.Count > 0
            ? $" It also answers as {JoinNames([.. others.Select(o => PrinterKindInfo.For(o.Kind).DisplayName)])}; pick another type to use that instead."
            : "";
        return $"Found {PrinterKindInfo.For(found.Kind).DisplayName}{model}.{also}";
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private void ShowResult(InfoBarSeverity severity, string title, string? message = null)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message ?? "";
        ResultBar.IsOpen = true;

        // It sits below the form; don't leave the answer out of sight.
        DispatcherQueue.TryEnqueue(() => ResultBar.StartBringIntoView());
    }

    private CancellationToken StartWork()
    {
        CancelWork();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private void CancelWork()
    {
        _work?.Cancel();
        _work = null;
        SetBusy(DetectButton, DetectLabel, DetectRing, false);
        SetBusy(TestButton, TestLabel, TestRing, false);
    }

    private static void SetBusy(Button button, TextBlock label, ProgressRing ring, bool busy)
    {
        label.Opacity = busy ? 0 : 1;
        ring.IsActive = busy;
        button.IsEnabled = !busy;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void HostBox_TextChanged(object sender, TextChangedEventArgs e) => DetectButton.IsEnabled = !string.IsNullOrWhiteSpace(HostBox.Text);

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Detect may have found this protocol too (a rooted K1 answers as Creality and Klipper).
        if (Kind is { } info && _detected?.Kind != info.Kind && _detections.FirstOrDefault(d => d.Kind == info.Kind) is { } match)
        {
            ApplyDetection(match);
        }

        UpdateFields();
        ResultBar.IsOpen = false;
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        CancellationToken cancellationToken = StartWork();
        SetBusy(DetectButton, DetectLabel, DetectRing, true);
        DetectText.Text = "Looking…";
        try
        {
            IReadOnlyList<DiscoveredPrinter> found = await PrinterProbe.DetectAsync(HostBox.Text, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _detections = found;
            if (found.Count == 0)
            {
                DetectText.Text = "Couldn't identify a printer there. Check the address, or choose its type below.";
            }
            else
            {
                ApplyDetection(found[0]);
                DetectText.Text = DetectedSummary(found[0], [.. found.Skip(1)]);
            }
        }
        catch (OperationCanceledException)
        {
            // Left the page.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetBusy(DetectButton, DetectLabel, DetectRing, false);
            }
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (Validate() is not { } config)
        {
            return;
        }

        CancellationToken cancellationToken = StartWork();
        SetBusy(TestButton, TestLabel, TestRing, true);
        ResultBar.IsOpen = false;
        try
        {
            await using IPrinterDriver driver = PrinterDriverFactory.Create(config, EffectiveSecret());
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestTimeout);
            PrinterSnapshot snapshot = await driver.GetSnapshotAsync(timeout.Token);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string state = StatusText.StateLabel(snapshot.State);
            string progress = snapshot.Progress is { } p && snapshot.IsActive ? $", {StatusText.Percent(p)}" : "";
            string model = snapshot.Model is null ? "" : $" ({snapshot.Model})";
            ShowResult(InfoBarSeverity.Success, "Connected", $"The printer{model} says: {state}{progress}.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ShowResult(InfoBarSeverity.Error, "Couldn't connect", PrinterMonitor.Describe(ex));
        }
        catch (OperationCanceledException)
        {
            // Left the page.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetBusy(TestButton, TestLabel, TestRing, false);
            }
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (Validate() is not { } config)
        {
            return;
        }

        // An empty box while editing keeps the saved secret; on a new printer there is none to keep.
        string? secret = SecretBox.Password is { Length: > 0 } typed ? typed : _editing is null ? "" : null;
        PrinterStore.Save(config, Kind!.SecretLabel is null ? "" : secret);
        if (_editing is null)
        {
            _host.GoHome();
        }
        else
        {
            _host.GoBack();
        }
    }

    /// <summary>The config to test or save, or null after explaining what's missing.</summary>
    private PrinterConfig? Validate()
    {
        if (Kind is null)
        {
            ShowResult(InfoBarSeverity.Warning, "Choose the printer type", "Or use Detect to work it out from the address.");
            return null;
        }

        PrinterConfig config = BuildConfig();
        if (PrinterDriverFactory.MissingField(config, EffectiveSecret()) is { } missing)
        {
            ShowResult(InfoBarSeverity.Warning, missing);
            return null;
        }

        return config;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.GoBack();
}
