using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Monitoring;
using TrayCer.Services;
using Windows.ApplicationModel;

namespace TrayCer.Views;

/// <summary>The few settings there are, as a page inside the flyout. Changes apply immediately.</summary>
public sealed partial class SettingsView : UserControl, IFlyoutView
{
    private readonly TrayFlyoutWindow _host;
    private bool _loading = true;

    internal SettingsView(TrayFlyoutWindow host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";

        IntervalBox.ItemsSource = MonitorOptions.ActiveIntervalChoices.Select(c => c < TimeSpan.FromMinutes(1) ? $"{c.TotalSeconds:0} seconds" : "1 minute").ToList();
        int index = MonitorOptions.ActiveIntervalChoices.ToList().IndexOf(SettingsService.ActiveInterval);
        IntervalBox.SelectedIndex = index >= 0 ? index : 1;
        FinishedToggle.IsOn = SettingsService.NotifyWhenFinished;
        ProblemsToggle.IsOn = SettingsService.NotifyAboutProblems;
        _loading = false;
    }

    public void OnShown()
    {
        // Re-read every time: it may have changed in Settings > Apps > Startup meanwhile.
        _ = LoadStartupStateAsync();
        BackButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden()
    {
    }

    private static string GetVersion()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "(unpackaged)";
        }
    }

    private async Task LoadStartupStateAsync()
    {
        ShowStartupState(await StartupService.GetStateAsync());
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep watching your printers after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IntervalBox.SelectedIndex >= 0)
        {
            SettingsService.ActiveInterval = MonitorOptions.ActiveIntervalChoices[IntervalBox.SelectedIndex];
        }
    }

    private void FinishedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.NotifyWhenFinished = FinishedToggle.IsOn;
        }
    }

    private void ProblemsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.NotifyAboutProblems = ProblemsToggle.IsOn;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.GoBack();
}
