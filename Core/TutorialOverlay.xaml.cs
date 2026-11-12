using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SifuMovesetEditor;

/// <summary>
/// Spotlight overlay for the interactive walkthroughs. It dims the window except for one
/// rounded hole, rings the spotlighted control, and shows a bubble with the step text.
///
/// The dimming layer is a single Path whose geometry is the window rectangle minus the
/// spotlight. WPF only hit-tests the filled part of a Path, so the hole is a genuine input
/// hole: the control under the spotlight stays usable while everything else is blocked.
/// </summary>
public partial class TutorialOverlay : UserControl
{
    private const double EdgeMargin = 16;
    private const double BubbleGap = 14;
    private const double RingInflate = 6;
    private const double BubbleWidth = 366;

    public event EventHandler? NextRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? ExitRequested;

    private readonly List<FrameworkElement> _targets = new();
    private FrameworkElement? _ringTarget;
    private DispatcherTimer? _nudgeTimer;

    public TutorialOverlay()
    {
        InitializeComponent();
        host.SizeChanged += (_, _) => Relayout();
    }

    public bool IsShowing => Visibility == Visibility.Visible;

    /// <summary>
    /// Renders one step. <paramref name="targets"/> are the controls to keep usable; every
    /// one becomes its own hole so a card and the graph can be live at the same time.
    /// <paramref name="ringTarget"/> is the single control the ring points at (usually the
    /// card, not the whole graph). Null targets centre the bubble over a full dim.
    /// </summary>
    public void ShowStep(TutorialScenario scenario, int index,
        IReadOnlyList<FrameworkElement>? targets, FrameworkElement? ringTarget = null)
    {
        var step = scenario.Steps[index];

        txtStep.Text = $"WALKTHROUGH · STEP {index + 1} OF {scenario.Steps.Count}";
        txtTitle.Text = step.Title;
        txtBody.Text = step.Body;
        txtCounter.Text = $"{index + 1} / {scenario.Steps.Count}";
        stepBar.Value = (double)(index + 1) / scenario.Steps.Count;

        hintBox.Visibility = step.HasHint ? Visibility.Visible : Visibility.Collapsed;
        txtHint.Text = step.EffectiveHint ?? "";

        btnBack.IsEnabled = index > 0;
        btnNext.Content = index == scenario.Steps.Count - 1 ? "Finish" : "Next";
        // Required actions have no Next: the step only ends when the editor event fires.
        btnNext.Visibility = step.IsRequiredAction ? Visibility.Collapsed : Visibility.Visible;
        txtWaiting.Visibility = step.IsRequiredAction ? Visibility.Visible : Visibility.Collapsed;

        _nudgeTimer?.Stop();
        _nudgeTimer = null;
        nudgeBox.Visibility = Visibility.Collapsed;

        // Keep the elements, not their bounds: the graph can pan and the library can scroll
        // while the overlay is up, and the holes must follow.
        _targets.Clear();
        if (targets != null)
        {
            foreach (var target in targets)
                if (target != null) _targets.Add(target);
        }

        _ringTarget = ringTarget;

        Visibility = Visibility.Visible;
        bubble.Visibility = Visibility.Visible;
        Relayout();
        StartPulse();

        // Keep keyboard focus off the editor behind the dim; the window also swallows keys.
        Focus();
    }

    public void HideOverlay()
    {
        ring.BeginAnimation(OpacityProperty, null);
        ringGlow.BeginAnimation(OpacityProperty, null);
        bubble.Visibility = Visibility.Collapsed;
        ring.Visibility = Visibility.Collapsed;
        ringGlow.Visibility = Visibility.Collapsed;
        nudgeBox.Visibility = Visibility.Collapsed;
        txtWaiting.Visibility = Visibility.Collapsed;
        _nudgeTimer?.Stop();
        _nudgeTimer = null;
        _targets.Clear();
        _ringTarget = null;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows a short warning without ending the step (for example a drop on an edited node).</summary>
    public void ShowNudge(string message)
    {
        txtNudge.Text = message;
        nudgeBox.Visibility = Visibility.Visible;
        Relayout();

        _nudgeTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            nudgeBox.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_nudgeTimer, timer)) _nudgeTimer = null;
            Relayout();
        };
        _nudgeTimer = timer;
        timer.Start();
    }

    /// <summary>Recomputes the holes and bubble position (window resize, layout pass, dialog closing).</summary>
    public void Relayout()
    {
        if (Visibility != Visibility.Visible) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var viewport = new Rect(0, 0, w, h);

        // Every spotlight gets its own hole: dropping a card needs the card and the graph
        // usable at once, but the ring must still point at just the card.
        var holes = new List<Rect>();
        foreach (var target in _targets)
        {
            var spot = SpotlightRect(BoundsOf(target), viewport);
            if (!spot.IsEmpty) holes.Add(spot);
        }

        dimPath.Data = holes.Count == 0
            ? new RectangleGeometry(viewport)
            : new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(viewport),
                BuildHoleGroup(holes));

        var ringRect = SpotlightRect(BoundsOf(_ringTarget), viewport);
        if (ringRect.IsEmpty)
        {
            ring.Visibility = Visibility.Collapsed;
            ringGlow.Visibility = Visibility.Collapsed;
        }
        else
        {
            Place(ring, ringRect);
            Place(ringGlow, ringRect);
            ring.Visibility = Visibility.Visible;
            ringGlow.Visibility = Visibility.Visible;
        }

        double bubbleWidth = Math.Min(BubbleWidth, Math.Max(180, w - EdgeMargin * 2));
        bubble.Width = bubbleWidth;
        bubble.Measure(new Size(bubbleWidth, double.PositiveInfinity));
        var desired = bubble.DesiredSize;

        // Anchor the bubble to the ringed control so it never covers the thing to click;
        // fall back to the union of the holes when nothing is ringed.
        var anchor = ringRect.IsEmpty ? UnionOf(holes) : ringRect;
        var area = PlaceBubble(anchor, desired, new Size(w, h));
        Canvas.SetLeft(bubble, area.X);
        Canvas.SetTop(bubble, area.Y);
    }

    private static Geometry BuildHoleGroup(IReadOnlyList<Rect> holes)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var hole in holes)
            group.Children.Add(new RectangleGeometry(hole, 9, 9));
        return group;
    }

    /// <summary>
    /// Bubble placement. Prefers below the spotlight, then above, then beside it, and never
    /// leaves the viewport. Pure geometry so the smoke test can pin the behaviour down.
    /// </summary>
    public static Rect PlaceBubble(Rect? spotlight, Size bubble, Size viewport,
        double gap = BubbleGap, double margin = EdgeMargin)
    {
        double bw = Math.Min(bubble.Width, Math.Max(120, viewport.Width - margin * 2));
        double bh = Math.Min(bubble.Height, Math.Max(80, viewport.Height - margin * 2));
        double maxY = Math.Max(margin, viewport.Height - margin - bh);
        double maxX = Math.Max(margin, viewport.Width - margin - bw);

        if (spotlight is not Rect s || s.IsEmpty)
            return new Rect((viewport.Width - bw) / 2, (viewport.Height - bh) / 2, bw, bh);

        double x = Clamp(s.X + (s.Width - bw) / 2, margin, maxX);
        double below = s.Bottom + gap;
        double above = s.Top - gap - bh;

        if (below + bh <= viewport.Height - margin)
            return new Rect(x, below, bw, bh);
        if (above >= margin)
            return new Rect(x, above, bw, bh);

        // Sideways: only when neither above nor below fits.
        double y = Clamp(s.Y + (s.Height - bh) / 2, margin, maxY);
        double right = s.Right + gap;
        if (right + bw <= viewport.Width - margin)
            return new Rect(right, y, bw, bh);
        double left = s.X - gap - bw;
        if (left >= margin)
            return new Rect(left, y, bw, bh);

        // Nothing fits cleanly (spotlight fills the window): sit at the bottom edge.
        return new Rect(x, maxY, bw, bh);
    }

    /// <summary>Inflates the target rect for the ring and clips it to the viewport.</summary>
    public static Rect SpotlightRect(Rect? target, Rect viewport)
    {
        if (target is not Rect t || t.IsEmpty) return Rect.Empty;
        var hole = t;
        hole.Inflate(RingInflate, RingInflate);
        hole.Intersect(viewport);
        return hole.Width < 2 || hole.Height < 2 ? Rect.Empty : hole;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    private static void Place(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = rect.Width;
        element.Height = rect.Height;
    }

    /// <summary>
    /// Bounds of the target in overlay coordinates. Returns null when the element is missing,
    /// collapsed, or lives in a different presentation source.
    /// </summary>
    private Rect? BoundsOf(FrameworkElement? target)
    {
        if (target == null || !IsShown(target)) return null;
        try
        {
            var topLeft = target.TranslatePoint(new Point(0, 0), this);
            var size = new Size(target.ActualWidth, target.ActualHeight);
            if (size.Width < 1 || size.Height < 1) return null;
            return new Rect(topLeft, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the visual tree for an explicit collapsed/hidden ancestor. Cheaper and more
    /// predictable than IsVisible, which is also false when the control isn't in a window
    /// (for example the offscreen render used by the smoke test).
    /// </summary>
    private static bool IsShown(DependencyObject? node)
    {
        while (node is FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible) return false;
            node = VisualTreeHelper.GetParent(element);
        }
        return true;
    }

    /// <summary>Bounds covering every hole, used to place the bubble when nothing is ringed.</summary>
    private static Rect? UnionOf(IReadOnlyList<Rect> holes)
    {
        Rect? union = null;
        foreach (var rect in holes)
            union = union is Rect current ? Rect.Union(current, rect) : rect;
        return union;
    }

    private void StartPulse()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });
        ringGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });
    }

    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void DimPath_Click(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the dimmed area are swallowed so they can't reach the editor behind it.
        e.Handled = true;
    }
}
