using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

/// <summary>Slides the reel one portrait per step while rolling and pops the frame when it lands.</summary>
public partial class RandomPickView : UserControl
{
    private RandomPickViewModel? _vm;
    private CancellationTokenSource? _slide;

    public RandomPickView()
    {
        InitializeComponent();
        ReelViewport.SizeChanged += (_, _) => CenterStrip();
        ReelStrip.SizeChanged += (_, _) => CenterStrip();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ReelStepped -= OnReelStepped;
            _vm.Landed -= OnLanded;
        }
        _vm = DataContext as RandomPickViewModel;
        if (_vm is not null)
        {
            _vm.ReelStepped += OnReelStepped;
            _vm.Landed += OnLanded;
        }
    }

    /// <summary>The strip is wider than the viewport, so it sits on a canvas with its middle portrait in the middle.</summary>
    private void CenterStrip()
    {
        Canvas.SetLeft(ReelStrip, (ReelViewport.Bounds.Width - ReelStrip.Bounds.Width) / 2);
        Canvas.SetTop(ReelStrip, (ReelViewport.Bounds.Height - ReelStrip.Bounds.Height) / 2);
    }

    /// <summary>
    /// The portraits have just moved one place to the left; start the strip one place to the right and slide it back, so
    /// the reel appears to scroll.
    /// </summary>
    private void OnReelStepped(object? sender, TimeSpan duration)
    {
        if (ReelStrip.RenderTransform is not TranslateTransform || duration <= TimeSpan.Zero) return;
        _slide?.Cancel();
        _slide = new CancellationTokenSource();
        var stride = ReelStrip.Bounds.Width / RandomPickViewModel.ReelLength + 10.0 / RandomPickViewModel.ReelLength;
        var animation = new Animation
        {
            Duration = duration,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, stride) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, 0d) } },
            },
        };
        _ = animation.RunAsync(ReelStrip, _slide.Token);
    }

    private void OnLanded(object? sender, EventArgs e)
    {
        if (_vm?.ReduceMotion != false) return;
        if (ReelFrame.RenderTransform is ScaleTransform)
        {
            var pop = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(420),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(0.35), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.16), new Setter(ScaleTransform.ScaleYProperty, 1.16) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
                },
            };
            _ = pop.RunAsync(ReelFrame);
        }
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(320),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } },
            },
        };
        _ = fade.RunAsync(ResultCard);
    }
}
