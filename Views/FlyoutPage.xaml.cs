using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// The printer list. Statuses come from <see cref="PrinterHub"/>, which watches the printers
/// whether or not this is on screen; the page only listens while shown.
/// </summary>
public sealed partial class FlyoutPage : Page, IFlyoutView, IDisposable
{
    // Relative text ("done at", "updated") ages while the flyout sits open.
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(30);

    private readonly TrayFlyoutWindow _host;
    private readonly Dictionary<string, PrinterCard> _cards = [];
    private readonly DispatcherQueueTimer _clock;
    private bool _shown;

    internal FlyoutPage(TrayFlyoutWindow host)
    {
        _host = host;
        InitializeComponent();
        TitleText.Text = App.DisplayName;
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = ClockInterval;
        _clock.Tick += Clock_Tick;
    }

    public void OnShown()
    {
        if (_shown)
        {
            return;
        }

        _shown = true;
        App.Current.Hub.Changed += Hub_Changed;
        ConnectivityService.NetworkStatusChanged += Connectivity_Changed;
        Render();
        _clock.Start();
        if (_cards.Count == 0)
        {
            FindButton.Focus(FocusState.Programmatic);
        }
    }

    public void OnHidden()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        App.Current.Hub.Changed -= Hub_Changed;
        ConnectivityService.NetworkStatusChanged -= Connectivity_Changed;
        _clock.Stop();
        foreach (PrinterCard card in _cards.Values)
        {
            card.CancelThumbnail();
        }
    }

    public void Dispose()
    {
        OnHidden();
        _clock.Tick -= Clock_Tick; // The native timer would otherwise keep this page alive.
        foreach (PrinterCard card in _cards.Values)
        {
            card.Dispose();
        }

        _cards.Clear();
        PrinterList.Children.Clear();
    }

    private void Render()
    {
        IReadOnlyList<PrinterStatus> statuses = App.Current.Hub.Statuses;
        DateTimeOffset now = DateTimeOffset.Now;

        // Keep cards in the saved order, reusing each one so thumbnails and focus survive updates.
        foreach (string gone in _cards.Keys.Except(statuses.Select(s => s.Config.Id)).ToList())
        {
            PrinterList.Children.Remove(_cards[gone]);
            _cards[gone].Dispose();
            _cards.Remove(gone);
        }

        for (int i = 0; i < statuses.Count; i++)
        {
            PrinterStatus status = statuses[i];
            if (!_cards.TryGetValue(status.Config.Id, out PrinterCard? card))
            {
                card = new PrinterCard(status.Config.Id);
                card.Invoked += (_, id) => _host.ShowPrinter(id);
                _cards[status.Config.Id] = card;
            }

            int index = PrinterList.Children.IndexOf(card);
            if (index != i)
            {
                if (index >= 0)
                {
                    PrinterList.Children.RemoveAt(index);
                }

                PrinterList.Children.Insert(i, card);
            }

            card.Update(status, now);
        }

        bool empty = statuses.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        bool offline = !NetworkInterface.GetIsNetworkAvailable();
        StatusBar.Title = "No network connection";
        StatusBar.Message = "Printers will update when you're back on your network.";
        StatusBar.IsOpen = offline && !empty;

        LoadingRing.IsActive = statuses.Any(s => s.Connection == ConnectionState.Connecting);
        DateTimeOffset? updated = statuses.Max(s => s.LastUpdated);
        UpdatedText.Text = updated is { } at ? $"Updated {StatusText.ClockTime(at, now)}" : "";
    }

    private void Hub_Changed(object? sender, EventArgs e) => Render();

    private void Clock_Tick(DispatcherQueueTimer sender, object args) => Render();

    // Raised on a background thread.
    private void Connectivity_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_shown)
        {
            Render();
        }
    });

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => App.Current.Hub.RefreshAll();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _host.ShowSettings();

    private void FindPrinters_Click(object sender, RoutedEventArgs e) => _host.ShowDiscover();

    private void AddByAddress_Click(object sender, RoutedEventArgs e) => _host.ShowAddPrinter();
}
