using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
namespace SurfaceInsight;

public sealed class ChartSeries
{
    public string Name = ""; public double[] Values = Array.Empty<double>(); public Color Color; public string Unit = ""; public string Format = "F1";
    public bool Fill, Stacked, Dashed, Right; public double Width = 2;
}

/// <summary>Shares the hover position between charts so one crosshair scrubs all of them.</summary>
public sealed class ChartGroup
{
    public double? Frac; public event Action? Changed;
    public void Set(double? f) { if (Frac == f) return; Frac = f; Changed?.Invoke(); }
}

static class ChartText
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static FormattedText Ft(Visual v, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);
    public static double Nice(double v)
    {
        if (!(v > 0) || !double.IsFinite(v)) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / p;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
    }
}

/// <summary>Custom time-series chart: gradient areas, stacked layers, gaps, dual axis, shared crosshair, markers and drag-to-select.</summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    public double[] Times = Array.Empty<double>(); public List<ChartSeries> Series = new(); public List<DateTime> Markers = new(); public List<(DateTime Time, string Label)> Notes = new();
    public ChartGroup? Group; public double? FixedMax; public bool Axes = true; public string AxisUnit = "";
    public event Action<DateTime, DateTime>? IntervalSelected;
    double? dragFrom, dragTo; ChartGroup? attached;
    int keyboardIndex; int? keyboardAnchor;

    public TimeSeriesChart() { Cursor = Cursors.Cross; SnapsToDevicePixels = true; Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "Time series chart");
        AutomationProperties.SetHelpText(this, "Left and right inspect samples; Home and End jump to the edges. Hold Shift to select an interval, then Enter to zoom. Escape cancels selection.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite); }
    public void Refresh() { Reattach(); InvalidateVisual(); }
    void Reattach() { if (attached == Group) return; if (attached != null) attached.Changed -= OnGroup; attached = Group; if (attached != null) attached.Changed += OnGroup; }
    void OnGroup() => InvalidateVisual();
    protected override void OnVisualParentChanged(DependencyObject oldParent) { base.OnVisualParentChanged(oldParent); if (Parent == null && attached != null) { attached.Changed -= OnGroup; attached = null; } else Reattach(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);
    public string AccessibleValue
    {
        get
        {
            if (Times.Length == 0) return "No chart data.";
            int i = Math.Clamp(keyboardIndex, 0, Times.Length - 1);
            return DateTime.FromOADate(Times[i]).ToString("g") + "; " + string.Join("; ", Series.Select(s => s.Name + ": " +
                (i < s.Values.Length && double.IsFinite(s.Values[i]) ? s.Values[i].ToString(s.Format, CultureInfo.CurrentCulture) + " " + s.Unit : "unavailable")));
        }
    }
    sealed class ChartPeer(TimeSeriesChart chart) : FrameworkElementAutomationPeer(chart), IValueProvider
    {
        public bool IsReadOnly => true;
        public string Value => chart.AccessibleValue;
        public void SetValue(string value) => throw new InvalidOperationException("Chart values are read-only.");
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override string GetClassNameCore() => nameof(TimeSeriesChart);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InspectKeyboard();
    }
    void InspectKeyboard()
    {
        if (Times.Length == 0) return;
        keyboardIndex = Math.Clamp(keyboardIndex, 0, Times.Length - 1);
        Group?.Set(Times.Length > 1 ? keyboardIndex / (double)(Times.Length - 1) : 0);
        ToolTip = AccessibleValue;
        InvalidateVisual();
        UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Times.Length == 0) return;
        int before = keyboardIndex;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: keyboardIndex--; break;
            case Key.Right: keyboardIndex++; break;
            case Key.Home: keyboardIndex = 0; break;
            case Key.End: keyboardIndex = Times.Length - 1; break;
            case Key.Enter:
                if (keyboardAnchor is int a && a != keyboardIndex)
                    IntervalSelected?.Invoke(DateTime.FromOADate(Times[Math.Min(a, keyboardIndex)]).ToUniversalTime(), DateTime.FromOADate(Times[Math.Max(a, keyboardIndex)]).ToUniversalTime());
                keyboardAnchor = null; dragFrom = dragTo = null; break;
            case Key.Escape: keyboardAnchor = null; dragFrom = dragTo = null; Group?.Set(null); break;
            default: return;
        }
        keyboardIndex = Math.Clamp(keyboardIndex, 0, Times.Length - 1);
        if (shift && e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            keyboardAnchor ??= Math.Clamp(before, 0, Times.Length - 1);
            dragFrom = keyboardAnchor.Value / (double)Math.Max(1, Times.Length - 1);
            dragTo = keyboardIndex / (double)Math.Max(1, Times.Length - 1);
        }
        InspectKeyboard(); e.Handled = true;
    }

    (double x0, double x1, double y0, double y1) Plot(bool right) => (Axes ? 42 : 0, ActualWidth - (right ? 44 : Axes ? 6 : 0), AxisUnit.Length > 0 ? 18 : 10, ActualHeight - (Axes ? 22 : 2));
    double XAt(int i, double x0, double x1) { double t0 = Times[0], t1 = Times[^1]; return x0 + (t1 > t0 ? (Times[i] - t0) / (t1 - t0) : 0) * (x1 - x0); }
    double[] Stack(IEnumerable<ChartSeries> st, List<double[]> lowers)
    {
        var cum = new double[Times.Length];
        foreach (var s in st) { lowers.Add((double[])cum.Clone()); for (int i = 0; i < cum.Length; i++) cum[i] += double.IsFinite(s.Values[i]) ? s.Values[i] : 0; }
        return cum;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight; dc.DrawRectangle(Brushes.Transparent, IsKeyboardFocusWithin ? new Pen(SystemColors.HighlightBrush, 2) : null, new Rect(1, 1, Math.Max(0, W - 2), Math.Max(0, H - 2)));
        if (W < 30 || H < 30) return;
        if (Times.Length < 2 || Series.Count == 0 || Series.All(s => s.Values.All(v => !double.IsFinite(v))))
        { var t = ChartText.Ft(this, "No data in this range yet", 12, Pal.Text2); dc.DrawText(t, new Point((W - t.Width) / 2, (H - t.Height) / 2)); return; }

        bool hasRight = Series.Any(s => s.Right); var (x0, x1, y0, y1) = Plot(hasRight);
        var stacked = Series.Where(s => s.Stacked).ToList(); var stackLowers = new List<double[]>(); double[] stackTop = stacked.Count > 0 ? Stack(stacked, stackLowers) : Array.Empty<double>();
        double Max(bool right)
        {
            double m = 0;
            foreach (var s in Series.Where(s => !s.Stacked && s.Right == right)) foreach (var v in s.Values) if (double.IsFinite(v)) m = Math.Max(m, v);
            if (!right && stacked.Count > 0) m = Math.Max(m, stackTop.Max());
            if (!right && FixedMax is double f) m = f;
            return m <= 0 ? 1 : m;
        }
        double maxL = Max(false), maxR = Max(true); if (FixedMax == null) { maxL = ChartText.Nice(maxL * 1.05); maxR = ChartText.Nice(maxR * 1.05); }
        double YAt(double v, double max) => y1 - Math.Min(Math.Max(v, 0) / max, 1) * (y1 - y0);

        // grid and axes
        var gridPen = new Pen(Pal.Grid, 1); gridPen.Freeze();
        for (int k = 0; k <= 4; k++)
        {
            double y = y1 - (y1 - y0) * k / 4; dc.DrawLine(gridPen, new Point(x0, Math.Round(y) + .5), new Point(x1, Math.Round(y) + .5));
            if (Axes) { var l = ChartText.Ft(this, Fmt(maxL * k / 4), 10.5, Pal.Text2); dc.DrawText(l, new Point(x0 - 6 - l.Width, y - l.Height / 2)); }
            if (Axes && hasRight) { var r = ChartText.Ft(this, Fmt(maxR * k / 4), 10.5, Pal.Text2); dc.DrawText(r, new Point(x1 + 6, y - r.Height / 2)); }
        }
        if (Axes && AxisUnit.Length > 0) { var ul = ChartText.Ft(this, AxisUnit, 10.5, Pal.Text2, true); dc.DrawText(ul, new Point(x0 - 6 - ul.Width > 0 ? x0 - 6 - ul.Width : 0, 0)); }
        if (Axes)
        {
            double t0 = Times[0], t1 = Times[^1], span = t1 - t0;
            for (int k = 0; k <= 4; k++)
            {
                var d = DateTime.FromOADate(t0 + span * k / 4); string text = span <= 2 ? d.ToString("HH:mm") : span <= 9 ? d.ToString("ddd HH:mm") : d.ToString("MMM d");
                var l = ChartText.Ft(this, text, 10.5, Pal.Text2); double x = x0 + (x1 - x0) * k / 4; dc.DrawText(l, new Point(Math.Clamp(x - l.Width / 2, 0, W - l.Width), y1 + 5));
            }
        }

        // stacked bands
        for (int si = 0; si < stacked.Count; si++)
        {
            var s = stacked[si]; var lower = stackLowers[si]; var upper = new double[lower.Length]; for (int i = 0; i < upper.Length; i++) upper[i] = lower[i] + (double.IsFinite(s.Values[i]) ? s.Values[i] : 0);
            var g = new StreamGeometry(); using (var c = g.Open())
            {
                c.BeginFigure(new Point(XAt(0, x0, x1), YAt(upper[0], maxL)), true, true);
                for (int i = 1; i < upper.Length; i++) c.LineTo(new Point(XAt(i, x0, x1), YAt(upper[i], maxL)), true, true);
                for (int i = lower.Length - 1; i >= 0; i--) c.LineTo(new Point(XAt(i, x0, x1), YAt(lower[i], maxL)), true, true);
            }
            g.Freeze(); var fill = new SolidColorBrush(Pal.Alpha(s.Color, .78)); fill.Freeze(); var edge = new Pen(new SolidColorBrush(Pal.Alpha(s.Color, 1)), 1.2); edge.Freeze(); dc.DrawGeometry(fill, edge, g);
        }

        // lines and areas
        foreach (var s in Series.Where(s => !s.Stacked))
        {
            double max = s.Right ? maxR : maxL; var line = new StreamGeometry(); var area = new StreamGeometry(); bool open = false; int segStart = 0;
            using (var lc = line.Open()) using (var ac = area.Open())
            {
                void CloseArea(int last) { if (s.Fill && last >= segStart) { ac.LineTo(new Point(XAt(last, x0, x1), y1), false, false); ac.LineTo(new Point(XAt(segStart, x0, x1), y1), false, false); } }
                for (int i = 0; i < s.Values.Length; i++)
                {
                    double v = s.Values[i]; if (!double.IsFinite(v)) { if (open) { CloseArea(i - 1); open = false; } continue; }
                    var p = new Point(XAt(i, x0, x1), YAt(v, max));
                    bool alone = (i == 0 || !double.IsFinite(s.Values[i - 1])) && (i == s.Values.Length - 1 || !double.IsFinite(s.Values[i + 1])); if (alone) dc.DrawEllipse(new SolidColorBrush(s.Color), null, p, 2.5, 2.5);
                    if (!open) { lc.BeginFigure(p, false, false); if (s.Fill) ac.BeginFigure(new Point(p.X, y1), true, true); if (s.Fill) ac.LineTo(p, false, false); open = true; segStart = i; }
                    else { lc.LineTo(p, true, true); if (s.Fill) ac.LineTo(p, false, false); }
                }
                if (open) CloseArea(s.Values.Length - 1);
            }
            line.Freeze(); area.Freeze();
            if (s.Fill) { var gb = new LinearGradientBrush(Pal.Alpha(s.Color, .34), Pal.Alpha(s.Color, .02), 90); gb.Freeze(); dc.DrawGeometry(gb, null, area); }
            var pen = new Pen(new SolidColorBrush(s.Color), s.Width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; if (s.Dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0); pen.Freeze(); dc.DrawGeometry(null, pen, line);
        }

        // markers
        double ts0 = Times[0], ts1 = Times[^1];
        foreach (var m in Markers) { double o = m.ToLocalTime().ToOADate(); if (o < ts0 || o > ts1) continue; double x = x0 + (o - ts0) / (ts1 - ts0) * (x1 - x0); var pen = new Pen(Pal.Warn, 1.3) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }; dc.DrawLine(pen, new Point(x, y0), new Point(x, y1)); var l = ChartText.Ft(this, "Fans loud", 10, Pal.Warn, true); dc.DrawText(l, new Point(Math.Min(x + 4, x1 - l.Width), y0 - 2)); }

        // labelled notes (block and session events)
        { var npen = new Pen(Pal.Accent, 1.3) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }; var used = new List<Rect>(); foreach (var (nt, label) in Notes) { double o = nt.ToLocalTime().ToOADate(); if (o < ts0 || o > ts1) continue; double x = x0 + (o - ts0) / (ts1 - ts0) * (x1 - x0); dc.DrawLine(npen, new Point(x, y0), new Point(x, y1)); var l = ChartText.Ft(this, label, 10, Pal.AccentText, true); var at = new Point(Math.Min(x + 4, x1 - l.Width), y1 - l.Height - 2);
                // stack labels that would overlap; drop the label (the line stays) when there is no room
                int tries = 0; while (used.Any(r => r.IntersectsWith(new Rect(at, new Size(l.Width, l.Height)))) && tries++ < 4) at.Y -= l.Height + 2;
                var box = new Rect(at, new Size(l.Width, l.Height)); if (tries > 4 || at.Y < y0) continue; used.Add(box);
                dc.DrawRoundedRectangle(new SolidColorBrush(Pal.Alpha(Pal.Dark ? Pal.C("#101522") : Colors.White, .85)), null, new Rect(box.X - 2, box.Y, box.Width + 4, box.Height), 3, 3); dc.DrawText(l, at); } }
        // drag selection
        if (dragFrom is double a && dragTo is double b) { double xa = x0 + Math.Min(a, b) * (x1 - x0), xb = x0 + Math.Max(a, b) * (x1 - x0); dc.DrawRectangle(Pal.AccentBg, new Pen(Pal.Accent, 1), new Rect(xa, y0, Math.Max(1, xb - xa), y1 - y0)); }

        // crosshair and tooltip
        if (Group?.Frac is double fr && dragFrom == null)
        {
            int idx = (int)Math.Round(fr * (Times.Length - 1)); double x = XAt(idx, x0, x1);
            dc.DrawLine(new Pen(Pal.Text2, 1), new Point(x, y0), new Point(x, y1));
            var rows = new List<(Color c, FormattedText name, FormattedText val)>(); int st = 0;
            foreach (var s in Series) { double v = s.Values[idx]; if (!double.IsFinite(v)) continue; if (!s.Stacked) { double max = s.Right ? maxR : maxL; dc.DrawEllipse(new SolidColorBrush(s.Color), new Pen(Brushes.White, 1.5), new Point(x, YAt(v, max)), 4, 4); } else st++; rows.Add((s.Color, ChartText.Ft(this, s.Name, 11.5, Pal.TipText), ChartText.Ft(this, v.ToString(s.Format, CultureInfo.CurrentCulture) + (s.Unit.Length > 0 ? " " + s.Unit : ""), 11.5, Pal.TipText, true))); }
            var head = ChartText.Ft(this, DateTime.FromOADate(Times[idx]).ToString(ToSpan() <= 2 ? "HH:mm:ss" : "ddd d MMM HH:mm"), 11.5, Pal.TipText, true);
            double nameW = rows.Count == 0 ? 0 : rows.Max(r => r.name.Width), valW = rows.Count == 0 ? 0 : rows.Max(r => r.val.Width), w = Math.Max(head.Width, 16 + nameW + 14 + valW) + 22, h = 14 + head.Height + rows.Count * 17 + 6;
            double bx = x + 14 + w > W ? x - 14 - w : x + 14; var box = new Rect(Math.Max(2, bx), y0 + 4, w, h);
            dc.DrawRoundedRectangle(Pal.Tip, null, box, 10, 10); dc.DrawText(head, new Point(box.X + 11, box.Y + 8));
            double ry = box.Y + 10 + head.Height;
            foreach (var r in rows) { dc.DrawRoundedRectangle(new SolidColorBrush(r.c), null, new Rect(box.X + 11, ry + 4, 8, 8), 2, 2); dc.DrawText(r.name, new Point(box.X + 25, ry)); dc.DrawText(r.val, new Point(box.Right - 11 - r.val.Width, ry)); ry += 17; }
        }
    }
    double ToSpan() => Times.Length > 1 ? Times[^1] - Times[0] : 0;
    static string Fmt(double v) => v >= 100 ? v.ToString("F0") : v >= 10 ? v.ToString("0.#") : v.ToString("0.##");

    double Frac(MouseEventArgs e) { var (x0, x1, _, _) = Plot(Series.Any(s => s.Right)); return Math.Clamp((e.GetPosition(this).X - x0) / Math.Max(1, x1 - x0), 0, 1); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); double f = Frac(e); if (dragFrom != null) { dragTo = f; InvalidateVisual(); } if (Group != null) Group.Set(f); else InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (dragFrom == null) { if (Group != null) Group.Set(null); else InvalidateVisual(); } }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); Focus(); if (IntervalSelected == null || Times.Length < 2) return; dragFrom = Frac(e); dragTo = dragFrom; CaptureMouse(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (dragFrom == null) return; ReleaseMouseCapture();
        double a = Math.Min(dragFrom.Value, dragTo ?? dragFrom.Value), b = Math.Max(dragFrom.Value, dragTo ?? dragFrom.Value); dragFrom = dragTo = null;
        var (x0, x1, _, _) = Plot(Series.Any(s => s.Right)); InvalidateVisual();
        if ((b - a) * (x1 - x0) > 8) { double t0 = Times[0], span = Times[^1] - Times[0]; IntervalSelected?.Invoke(DateTime.FromOADate(t0 + a * span).ToUniversalTime(), DateTime.FromOADate(t0 + b * span).ToUniversalTime()); }
    }
}

/// <summary>Tiny trend line for tiles and list rows.</summary>
public sealed class Sparkline : FrameworkElement
{
    public double[] Values = Array.Empty<double>(); public Color Color; public bool Fill = true;
    public Sparkline() { IsHitTestVisible = false; SnapsToDevicePixels = true; }
    public void Set(double[] v, Color c, bool fill = true) { Values = v; Color = c; Fill = fill; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight; var fin = Values.Where(double.IsFinite).ToList(); if (fin.Count < 2 || W < 4) return;
        double max = fin.Max(), min = Math.Min(0, fin.Min()), rng = Math.Max(1e-9, max - min); int n = Values.Length;
        var line = new StreamGeometry(); var area = new StreamGeometry(); bool open = false; int first = 0;
        using (var lc = line.Open()) using (var ac = area.Open())
            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(Values[i])) { if (open) { ac.LineTo(new Point((i - 1) * W / (n - 1), H), false, false); ac.LineTo(new Point(first * W / (n - 1), H), false, false); open = false; } continue; }
                var p = new Point(i * W / (n - 1), 2 + (H - 4) * (1 - (Values[i] - min) / rng));
                if (!open) { lc.BeginFigure(p, false, false); ac.BeginFigure(new Point(p.X, H), true, true); ac.LineTo(p, false, false); open = true; first = i; } else { lc.LineTo(p, true, true); ac.LineTo(p, false, false); }
            }
        line.Freeze(); area.Freeze();
        if (Fill) { var gb = new LinearGradientBrush(Pal.Alpha(Color, .3), Pal.Alpha(Color, 0), 90); gb.Freeze(); dc.DrawGeometry(gb, null, area); }
        var pen = new Pen(new SolidColorBrush(Color), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze(); dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Circular gauge with a centre value.</summary>
public sealed class Ring : FrameworkElement
{
    public double Fraction; public Color Color; public string Value = "", Caption = "";
    public Ring() { IsHitTestVisible = false; }
    public void Set(double f, Color c, string value, string caption) { Fraction = Math.Clamp(f, 0, 1); Color = c; Value = value; Caption = caption; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight), r = s / 2 - 8; if (r < 8) return; var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Pal.Grid, 12), c, r, r);
        if (Fraction > 0.001)
        {
            double ang = Fraction * 359.99 * Math.PI / 180, sx = c.X, sy = c.Y - r, ex = c.X + r * Math.Sin(ang), ey = c.Y - r * Math.Cos(ang);
            var g = new StreamGeometry(); using (var gc = g.Open()) { gc.BeginFigure(new Point(sx, sy), false, false); gc.ArcTo(new Point(ex, ey), new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true, false); }
            g.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        var v = ChartText.Ft(this, Value, s * .22, Pal.Text, true); var k = ChartText.Ft(this, Caption, s * .09, Pal.Text2);
        dc.DrawText(v, new Point(c.X - v.Width / 2, c.Y - v.Height / 2 - 6)); dc.DrawText(k, new Point(c.X - k.Width / 2, c.Y + v.Height / 2 - 8));
    }
}
