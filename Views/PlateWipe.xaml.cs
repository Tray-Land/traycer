using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// Mark as complete's flourish, laid over a card: a green wash that's scraped off left to right,
/// like a spatula clearing the plate. Invisible the rest of the time.
/// </summary>
public sealed partial class PlateWipe : UserControl
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(450);

    private readonly InsetClip _clip;

    public PlateWipe()
    {
        InitializeComponent();
        Visual wash = ElementCompositionPreview.GetElementVisual(Wash);
        _clip = wash.Compositor.CreateInsetClip();
        _clip.LeftInset = 1e6f; // Clipped from the left past any width: nothing shows.
        wash.Clip = _clip;
    }

    /// <summary>Covers the card in green and scrapes it clean. Does nothing with Windows' animation effects off.</summary>
    public void Play()
    {
        float width = (float)Wash.ActualWidth;
        if (!MotionService.AnimationsEnabled || width <= 0)
        {
            return;
        }

        Compositor compositor = _clip.Compositor;
        ScalarKeyFrameAnimation scrape = compositor.CreateScalarKeyFrameAnimation();
        scrape.InsertKeyFrame(0, 0);
        scrape.InsertKeyFrame(1, width + 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0), new Vector2(0.3f, 1)));
        scrape.Duration = Duration;
        _clip.StartAnimation("LeftInset", scrape);
    }
}
