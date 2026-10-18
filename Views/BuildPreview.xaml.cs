using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TrayCer.Services;

namespace TrayCer.Views;

/// <summary>
/// A job preview drawn the way the print builds up: solid to the layer being printed, a ghost of
/// the rest above it, and a line where the nozzle is. Between updates the part eases upward; when
/// it comes off the plate (marked complete, or the next print starting) it slides away rather
/// than un-printing.
/// </summary>
public sealed partial class BuildPreview : UserControl
{
    private static readonly TimeSpan GrowDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ClearDuration = TimeSpan.FromMilliseconds(320);

    private readonly Visual _built;
    private readonly Visual _head;
    private readonly InsetClip _clip;
    private double _share = double.NaN; // Until the first Show, which places without animating.
    private int _clearing;

    public BuildPreview()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(Built, true);
        ElementCompositionPreview.SetIsTranslationEnabled(Head, true);
        _built = ElementCompositionPreview.GetElementVisual(Built);
        _head = ElementCompositionPreview.GetElementVisual(Head);
        _clip = _built.Compositor.CreateInsetClip();
        _built.Clip = _clip;
        Built.SizeChanged += (_, _) => Place();
    }

    public ImageSource? Source
    {
        get => Built.Source;
        set => Ghost.Source = Built.Source = value;
    }

    /// <param name="share">How much of the part is on the plate, 0 to 1 from the bottom up.</param>
    /// <param name="printing">True while the nozzle is laying down plastic, so a line marks its layer.</param>
    public void Show(double share, bool printing)
    {
        double from = _share;
        _share = Math.Clamp(share, 0, 1);
        Head.Visibility = printing && _share is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_share == from)
        {
            return;
        }

        if (double.IsNaN(from) || Built.ActualHeight <= 0 || !MotionService.AnimationsEnabled)
        {
            Place();
        }
        else if (_share > from)
        {
            Grow();
        }
        else
        {
            ClearPlate();
        }
    }

    /// <summary>Puts the clip and the head at the current share at once, ending any animation.</summary>
    private void Place()
    {
        if (double.IsNaN(_share))
        {
            return;
        }

        _clearing++; // A slide still running has nothing left to do.
        _clip.StopAnimation("TopInset");
        _built.StopAnimation("Translation");
        _built.StopAnimation("Opacity");
        _head.StopAnimation("Translation");

        float top = Top();
        _clip.TopInset = top;
        _built.Properties.InsertVector3("Translation", Vector3.Zero);
        _built.Opacity = 1;
        _head.Properties.InsertVector3("Translation", new Vector3(0, HeadY(top), 0));
    }

    /// <summary>More layers are down: the solid part and the head ease up to them.</summary>
    private void Grow()
    {
        Compositor compositor = _built.Compositor;
        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));
        float top = Top();

        ScalarKeyFrameAnimation clip = compositor.CreateScalarKeyFrameAnimation();
        clip.InsertKeyFrame(1, top, easeOut);
        clip.Duration = GrowDuration;
        _clip.StartAnimation("TopInset", clip);

        Vector3KeyFrameAnimation head = compositor.CreateVector3KeyFrameAnimation();
        head.InsertKeyFrame(1, new Vector3(0, HeadY(top), 0), easeOut);
        head.Duration = GrowDuration;
        _head.StartAnimation("Translation", head);
    }

    /// <summary>The part comes off: it slides sideways and fades, leaving the plate at the new share.</summary>
    private void ClearPlate()
    {
        Compositor compositor = _built.Compositor;
        CubicBezierEasingFunction easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0), new Vector2(0.9f, 0.4f));
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, new Vector3((float)(Built.ActualWidth * 0.4), 0, 0), easeIn);
        slide.Duration = ClearDuration;
        _built.StartAnimation("Translation", slide);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = ClearDuration;
        _built.StartAnimation("Opacity", fade);

        batch.End();
        int clearing = ++_clearing;
        batch.Completed += (_, _) =>
        {
            if (clearing == _clearing)
            {
                Place();
            }
        };
    }

    /// <summary>How much of the picture is still to print, from the top; past the bottom when nothing is, so no sliver shows at high DPI.</summary>
    private float Top() => (float)(_share <= 0 ? Built.ActualHeight + 1 : Built.ActualHeight * (1 - _share));

    private static float HeadY(float top) => Math.Max(0, top - 1);
}
