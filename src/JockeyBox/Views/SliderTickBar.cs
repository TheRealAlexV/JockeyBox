using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace JockeyBox.Views;

/// <summary>
/// The little clickable marks under a fader.
///
/// <para><b>Why not <see cref="System.Windows.Controls.Primitives.TickBar"/>.</b>
/// It draws, but it cannot be hit: no mouse input, no hover state, no feedback.
/// The operator asked for marks they can <i>click</i> to set an exact level, so the
/// marks have to be an affordance, not decoration. This element draws the marks
/// itself and turns a click into the exact value of the mark that was clicked.</para>
///
/// <para><b>Where the marks come from.</b> No per-slider markup: the deck volume
/// already declares <c>TickFrequency</c> and the pitch faders are bounded bipolar
/// ranges, so both are recognised without touching the views that host them. A
/// slider says "these are my levels" in one of two ways:</para>
/// <list type="bullet">
///   <item><description><c>TickFrequency &gt; 0</c> - the WPF spelling of a tick
///   ladder (deck volume: 0..1.2 by 0.1). Coarsened to at most nine marks, because
///   thirteen 1 px marks under a 140 px groove read as a grid, not as a scale.</description></item>
///   <item><description>a bipolar range no wider than 100 units - a pitch-style
///   fader (tempo: -16..+16). Its midpoint is the interesting value, so it gets a
///   ladder with a mark on it.</description></item>
/// </list>
/// <para>Everything else stays clean, deliberately: a scrub bar (0..track length),
/// a karaoke mix, the lyric offset (-2000..2000). Marks on a position bar invite a
/// click that means "jump to 3 minutes", which is not a thing here.</para>
///
/// <para><b>Recentring.</b> One mark is drawn taller and in the deck's accent, and
/// it is the one that puts the fader back: 0 where the range straddles zero (tempo
/// 0% -&gt; ratio 1.000), unity gain (1.0) where the range runs past it (deck volume
/// and master, whose 0 dB reference is 100%), otherwise the middle of the range.
/// Clicking it sets that value exactly.</para>
///
/// <para><b>Height.</b> The bar lives in the bottom of the slider's existing 18 px
/// band, under the groove, so a deck row that already reserved 18 px for a slider
/// reserves exactly that afterwards. Nothing in the card grows.</para>
/// </summary>
public sealed class SliderTickBar : FrameworkElement
{
    /// <summary>Tallest ladder drawn; past this the marks stop reading as separate.</summary>
    private const int MaxMarks = 9;

    /// <summary>How close a click has to land, in px, to count as clicking a mark.</summary>
    private const double GrabRadius = 6;

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(SliderTickBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterBrushProperty = DependencyProperty.Register(
        nameof(CenterBrush), typeof(Brush), typeof(SliderTickBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(SliderTickBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Marks for values, one per tick.</summary>
    public Brush TickBrush
    {
        get => (Brush)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /// <summary>Marks for the value that recentres the fader.</summary>
    public Brush CenterBrush
    {
        get => (Brush)GetValue(CenterBrushProperty);
        set => SetValue(CenterBrushProperty, value);
    }

    /// <summary>Marks the mark under the pointer, so the bar reads as clickable.</summary>
    public Brush HoverBrush
    {
        get => (Brush)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    /// <summary>
    /// Marks a slider declares by name, when its ladder is not evenly spaced from its
    /// minimum: <c>views:SliderTickBar.Values="-50,-25,0,25,50"</c> on the mix EQ,
    /// whose recentre mark is 0 dB. On the original asymmetric -26…+6 track the
    /// derived ladder landed on -26, -18, -10, -2, +6 and put the detent at -2 dB -
    /// a detent that leaves the band filtering. No marks without this: a slider that
    /// declares nothing keeps the ladder it always had.
    /// </summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.RegisterAttached(
        "Values", typeof(string), typeof(SliderTickBar), new FrameworkPropertyMetadata(null));

    public static string? GetValues(DependencyObject element) => (string?)element.GetValue(ValuesProperty);

    public static void SetValues(DependencyObject element, string? value) =>
        element.SetValue(ValuesProperty, value);

    private Slider? _slider;
    private double[] _values = Array.Empty<double>();
    private int _center = -1;
    private int _hover = -1;
    private bool _dragging;

    /// <summary>How many marks are on the ladder; 0 when this slider has none.</summary>
    public int MarkCount => _values.Length;

    /// <summary>The value the recentre mark sets, or NaN when there is no ladder.</summary>
    public double CenterValue => _center >= 0 && _center < _values.Length ? _values[_center] : double.NaN;

    public SliderTickBar()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => _slider = null;
    }

    private void Attach()
    {
        _slider = TemplatedParent as Slider;
        if (_slider is null)
        {
            _values = Array.Empty<double>();
            return;
        }

        // The marks own the exact values now, so snapping is not just redundant but
        // wrong: it yanks the thumb to the nearest mark mid-drag, which is the thing
        // the operator asked to stop. SetCurrentValue (not a value of our own) so the
        // property is forced off without destroying a binding or a style setter
        // someone adds later; a slider that never had it is unaffected.
        if (_slider.IsSnapToTickEnabled)
        {
            _slider.SetCurrentValue(Slider.IsSnapToTickEnabledProperty, false);
        }

        Rebuild();
    }

    /// <summary>
    /// True when the slider itself declares <c>TickFrequency</c> - i.e. it means "these
    /// are my levels", rather than inheriting WPF's non-zero default.
    /// </summary>
    private static bool DeclaresTickFrequency(Slider slider) =>
        DependencyPropertyHelper.GetValueSource(slider, Slider.TickFrequencyProperty).BaseValueSource
        is not (BaseValueSource.Default or BaseValueSource.Inherited);

    private void Rebuild()
    {
        _values = Array.Empty<double>();
        _center = -1;
        if (_slider is null)
        {
            return;
        }

        double min = _slider.Minimum;
        double max = _slider.Maximum;
        if (!(max > min))
        {
            return;
        }

        // A ladder the slider states itself wins outright: it is the only way to put
        // the detent where the slider's own zero is, when that zero is not on an even
        // step from the minimum.
        if (ExplicitValues(_slider, min, max) is { Length: >= 3 } declared)
        {
            _values = declared;
            _center = NearestIndex(RecenterValue(min, max, declared));
            return;
        }

        double step = 0;
        if (DeclaresTickFrequency(_slider) && _slider.TickFrequency > 0
            && MarksFor(min, max, _slider.TickFrequency) >= 3)
        {
            // The slider says so itself (deck volume: 0..1.2 by 0.1). Requiring three
            // marks keeps WPF's default TickFrequency from putting an accidental pair
            // of marks on every knob that never asked for a scale.
            step = _slider.TickFrequency;
        }
        else if (min < 0 && max > 0 && max - min <= 100)
        {
            // Bipolar and narrow: a pitch fader (tempo: -16..+16). Eight gaps puts a
            // mark on 0 and one at each end without crowding a ~150 px groove.
            step = NiceStep((max - min) / 8);
        }
        else if (min < 1.0 && max > 1.0 && max - min <= 100)
        {
            // A level fader whose unity gain sits inside its range (volume and master:
            // 0..1.2, mic: 0..2). Eight gaps keeps the 0 dB mark on a mark of its own.
            step = NiceStep((max - min) / 8);
        }

        if (step <= 0)
        {
            return;
        }

        while (MarksFor(min, max, step) > MaxMarks)
        {
            step *= 2;
        }

        int gaps = GapsFor(min, max, step);
        var values = new double[gaps + 1];
        for (int i = 0; i <= gaps; i++)
        {
            values[i] = min + i * step;
        }

        _values = values;
        _center = NearestIndex(RecenterValue(min, max, values));
    }

    /// <summary>How many gaps fit, counting in whole steps (0..1.2 by 0.2 is 6, not 5).</summary>
    private static int GapsFor(double min, double max, double step) =>
        (int)Math.Round((max - min) / step, MidpointRounding.AwayFromZero);

    private static int MarksFor(double min, double max, double step) => GapsFor(min, max, step) + 1;

    /// <summary>Rounds a raw step up to 1, 2, 2.5, 4 or 8 times a power of ten.</summary>
    private static double NiceStep(double raw)
    {
        if (!(raw > 0))
        {
            return 0;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalised = raw / magnitude;
        double nice = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 2.5 ? 2.5 : normalised <= 4 ? 4 : 8;
        return nice * magnitude;
    }

    /// <summary>The value the recentre mark sets, snapped to a mark that exists.</summary>
    private static double RecenterValue(double min, double max, double[] values)
    {
        double wanted;
        if (min < 0 && max > 0)
        {
            wanted = 0;                       // tempo: 0% is ratio 1.000
        }
        else if (min < 1.0 && max > 1.0)
        {
            wanted = 1.0;                     // volume/master: 100% is 0 dB
        }
        else
        {
            wanted = (min + max) / 2;
        }

        int index = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (Math.Abs(values[i] - wanted) < Math.Abs(values[index] - wanted))
            {
                index = i;
            }
        }

        return values[index];
    }

    /// <summary>
    /// The marks a slider declares in <see cref="ValuesProperty"/>, or null when it
    /// declares none. Out-of-range and unparseable entries are dropped rather than
    /// drawn, so a typo cannot put a mark past the end of the groove; fewer than
    /// three surviving marks is not a ladder, so the caller falls back to the
    /// derived one.
    /// </summary>
    private static double[]? ExplicitValues(Slider slider, double min, double max)
    {
        if (GetValues(slider) is not { Length: > 0 } text)
        {
            return null;
        }

        var parsed = new System.Collections.Generic.List<double>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            if (value < min || value > max)
            {
                continue;
            }

            parsed.Add(value);
        }

        if (parsed.Count < 3)
        {
            return null;
        }

        parsed.Sort();
        return parsed.ToArray();
    }

    private int NearestIndex(double value)
    {
        int index = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _values.Length; i++)
        {
            double distance = Math.Abs(_values[i] - value);
            if (distance < best)
            {
                best = distance;
                index = i;
            }
        }

        return index;
    }

    /// <summary>Index of the mark within click range of <paramref name="x"/>, or -1.</summary>
    private int MarkAt(double x)
    {
        int index = NearestIndex(XToValue(x));
        return index >= 0 && Math.Abs(ValueToX(_values[index]) - x) <= GrabRadius ? index : -1;
    }

    private double ValueToX(double value)
    {
        if (_slider is null)
        {
            return 0;
        }

        double span = _slider.Maximum - _slider.Minimum;
        return span > 0 ? (value - _slider.Minimum) / span * ActualWidth : 0;
    }

    private double XToValue(double x) =>
        _slider is null || ActualWidth <= 0
            ? 0
            : _slider.Minimum + x / ActualWidth * (_slider.Maximum - _slider.Minimum);

    /// <summary>
    /// Hit only near a mark. Everywhere else the click falls through to the groove
    /// and its repeat buttons, so dragging the thumb and click-to-seek keep working
    /// exactly as before - the marks take nothing away from the track. A mark that
    /// happens to sit under the thumb belongs to the thumb: grabbing the knob must
    /// never turn into "jump to this value".
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        !OverThumb(hitTestParameters.HitPoint) && MarkAt(hitTestParameters.HitPoint.X) >= 0
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;

    /// <summary>True when the point lands on the thumb's own circle.</summary>
    private bool OverThumb(Point point)
    {
        if (_slider?.Template?.FindName("PART_Track", _slider) is not Track track
            || track.Thumb is not Thumb thumb
            || thumb.ActualWidth <= 0)
        {
            return false;
        }

        try
        {
            Rect bounds = thumb.TransformToVisual(this)
                .TransformBounds(new Rect(0, 0, thumb.ActualWidth, thumb.ActualHeight));
            return bounds.Contains(point);
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source yet: no thumb geometry to test.
            return false;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int index = MarkAt(e.GetPosition(this).X);
        if (index < 0)
        {
            return;
        }

        CaptureMouse();
        _dragging = true;
        Set(index);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;

        // With the button down the pointer can travel between marks without leaving
        // the capture, so follow the nearest one; otherwise stay honest about which
        // mark is actually under the pointer.
        int index = _dragging ? NearestIndex(XToValue(x)) : MarkAt(x);
        if (index >= 0 && _dragging)
        {
            Set(index);
        }

        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    private void Set(int index)
    {
        if (_slider is not null && index >= 0 && index < _values.Length)
        {
            _slider.Value = _values[index];
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_slider is null || _values.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Marks sit in the bottom of the band, under the groove (which is centred in
        // the 18 px track). One pixel wide, three tall: at 96 dpi that is a hairline
        // tick, the same weight as a hardware fader's scale, and legible against the
        // #1E222A panel without becoming a second track.
        double bottom = Math.Round(ActualHeight);
        for (int i = 0; i < _values.Length; i++)
        {
            bool isCenter = i == _center;
            bool isHover = i == _hover;
            double x = Math.Round(ValueToX(_values[i]));

            double height = isCenter ? Math.Min(ActualHeight, 6) : isHover ? 4 : 3;
            double width = isCenter || isHover ? 2 : 1;
            Brush brush = isCenter ? CenterBrush : isHover ? HoverBrush : TickBrush;

            var rect = new Rect(x - width / 2, bottom - height, width, height);
            drawingContext.DrawRectangle(brush, null, rect);
        }
    }
}
