using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Models;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// A print's progress bar with the hot nozzle riding its leading edge while it prints. The bar
/// eases to each new value on its own (the stock template does that) and the nozzle glides along
/// with it. Its glow runs only while the page is shown: call <see cref="Stop"/> when it hides.
/// </summary>
public sealed partial class PrintProgressBar : UserControl
{
    // About as long as the bar's own reposition animation, so the nozzle keeps up with the fill.
    private readonly Vector3Transition _glide = new() { Duration = TimeSpan.FromMilliseconds(400) };
    private bool _glowing;

    public PrintProgressBar() => InitializeComponent();

    /// <param name="running">A job is under way, getting ready or waiting: with no percentage, the bar spins rather than sitting empty.</param>
    public void Update(PrinterSnapshot snapshot, bool running)
    {
        Bar.IsIndeterminate = running && (snapshot.Progress is null || snapshot.State == PrinterState.Preparing);
        Bar.Value = (snapshot.Progress ?? 0) * 100;
        Bar.ShowPaused = snapshot.State is PrinterState.Paused or PrinterState.Attention;
        Bar.ShowError = false;

        bool hot = snapshot.State == PrinterState.Printing && !Bar.IsIndeterminate;
        Nozzle.Visibility = hot ? Visibility.Visible : Visibility.Collapsed;
        Place(glide: true);
        if (hot && MotionService.AnimationsEnabled)
        {
            StartGlow();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>Stops the glow, for a page going off screen; the next <see cref="Update"/> starts it again.</summary>
    public void Stop()
    {
        if (_glowing)
        {
            Glow.Stop();
            _glowing = false;
        }
    }

    private void StartGlow()
    {
        // Begin restarts a running storyboard, which would hiccup on every update.
        if (!_glowing)
        {
            Glow.Begin();
            _glowing = true;
        }
    }

    private void Place(bool glide)
    {
        Nozzle.TranslationTransition = glide && MotionService.AnimationsEnabled ? _glide : null;
        Nozzle.Translation = new Vector3((float)(Bar.ActualWidth * Bar.Value / Bar.Maximum), 0, 0);
    }

    // A new width isn't progress: the nozzle jumps to its spot rather than gliding there.
    private void Bar_SizeChanged(object sender, SizeChangedEventArgs e) => Place(glide: false);
}
