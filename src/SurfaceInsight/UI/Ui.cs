using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
namespace SurfaceInsight;

/// <summary>Lays children out in as many equal columns as fit (min width each); a child may span several columns via AutoGrid.Span.</summary>
public sealed class AutoGrid : Panel
{
    public double MinItemWidth { get; set; } = 220; public double Gap { get; set; } = 14;
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached("Span", typeof(int), typeof(AutoGrid), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static int GetSpan(DependencyObject o) => (int)o.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject o, int v) => o.SetValue(SpanProperty, v);
    int Cols(double w) => Math.Max(1, (int)Math.Floor((double.IsInfinity(w) ? 1000 : w + Gap) / (MinItemWidth + Gap)));
    protected override Size MeasureOverride(Size a)
    {
        int cols = Cols(a.Width); double cw = double.IsInfinity(a.Width) ? MinItemWidth : (a.Width - Gap * (cols - 1)) / cols; double y = 0, rowH = 0; int col = 0;
        foreach (UIElement c in InternalChildren)
        {
            int span = Math.Min(cols, Math.Max(1, GetSpan(c))); if (col + span > cols) { y += rowH + Gap; rowH = 0; col = 0; }
            c.Measure(new Size(cw * span + Gap * (span - 1), double.PositiveInfinity)); rowH = Math.Max(rowH, c.DesiredSize.Height); col += span; if (col >= cols) { y += rowH + Gap; rowH = 0; col = 0; }
        }
        if (col > 0) y += rowH; else if (y > 0) y -= Gap;
        return new Size(double.IsInfinity(a.Width) ? MinItemWidth : a.Width, Math.Max(0, y));
    }
    protected override Size ArrangeOverride(Size f)
    {
        int cols = Cols(f.Width); double cw = (f.Width - Gap * (cols - 1)) / cols; double y = 0, rowH = 0; int col = 0; var row = new List<(UIElement c, double x, double w)>();
        void Flush() { foreach (var (c, x, w) in row) c.Arrange(new Rect(x, y, w, rowH)); row.Clear(); y += rowH + Gap; rowH = 0; col = 0; }
        foreach (UIElement c in InternalChildren)
        {
            int span = Math.Min(cols, Math.Max(1, GetSpan(c))); if (col + span > cols) Flush();
            double w = cw * span + Gap * (span - 1); row.Add((c, col * (cw + Gap), w)); rowH = Math.Max(rowH, c.DesiredSize.Height); col += span; if (col >= cols) Flush();
        }
        if (row.Count > 0) Flush();
        return f;
    }
}

static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static TextBlock T(string text, double size = 14, bool bold = false, Brush? fg = null, bool wrap = false, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, FontWeight = weight ?? (bold ? FontWeights.SemiBold : FontWeights.Normal), Foreground = fg ?? Pal.Text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, FontFamily = Font };
    public static TextBlock Cap(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Pal.Text2, FontFamily = Font };
    public static TextBlock Dim(string text, double size = 12, bool wrap = true) => T(text, size, false, Pal.Text2, wrap);

    public static Border Card(UIElement child, double pad = 18, double radius = 20)
    {
        var b = new Border { Child = child, Padding = new Thickness(pad), CornerRadius = new CornerRadius(radius), Background = Pal.Card, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
        b.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = Pal.Dark ? .5 : .16, Color = Colors.Black }; return b;
    }
    public static Border Chip(string text, Brush fg, Brush bg) => new() { Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Child = T(text, 11.5, true, fg), HorizontalAlignment = HorizontalAlignment.Left };
    public static Border ActivityChip(string activity) => activity == "Sustained" ? Chip(activity, Pal.WarnText, Pal.WarnBg) : activity == "Brief spikes" ? Chip(activity, Pal.AccentText, Pal.AccentBg) : Chip(activity, Pal.OkText, Pal.OkBg);
    public static Border Dot(Color c, double size = 12) => new() { Width = size, Height = size, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };

    public static Button Pill(string text, Action click, bool primary = false, bool selected = false, UIElement? content = null, string? automationName = null)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("PillButton"), Content = content ?? T(text, 13, true, primary ? Brushes.White : selected ? Pal.AccentText : Pal.Text) };
        b.Background = primary ? Pal.Accent : selected ? Pal.Sel : Pal.Inset; b.BorderBrush = primary ? Brushes.Transparent : selected ? Pal.Accent : Pal.Edge; b.BorderThickness = new Thickness(1);
        b.Click += (_, _) => click(); System.Windows.Automation.AutomationProperties.SetName(b, automationName ?? text); return b;
    }
    public static Button RowBtn(UIElement content, Action click, Thickness? pad = null, Brush? bg = null, string? name = null)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("RowButton"), Content = content, Padding = pad ?? new Thickness(14, 10, 14, 10), Background = bg ?? Brushes.Transparent };
        b.Click += (_, _) => click(); if (name != null) System.Windows.Automation.AutomationProperties.SetName(b, name); return b;
    }
    public static StackPanel V(double gap = 0, params UIElement[] kids) { var p = new StackPanel(); for (int i = 0; i < kids.Length; i++) { if (i > 0 && gap > 0 && kids[i] is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, gap, 0, 0); p.Children.Add(kids[i]); } return p; }
    public static StackPanel H(double gap = 0, params UIElement[] kids) { var p = new StackPanel { Orientation = Orientation.Horizontal }; for (int i = 0; i < kids.Length; i++) { if (i > 0 && gap > 0 && kids[i] is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(gap, 0, 0, 0); p.Children.Add(kids[i]); } return p; }
    public static Segmented Seg(IEnumerable<(string Label, string Key)> items, string selected, Action<string> pick) => new(items, selected, pick);
}

/// <summary>Capsule segmented control (macOS style).</summary>
public sealed class Segmented : Border
{
    public Segmented(IEnumerable<(string Label, string Key)> items, string selected, Action<string> pick)
    {
        Background = Pal.Inset; BorderBrush = Pal.Edge; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(19); Padding = new Thickness(3);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, key) in items)
        {
            bool on = key == selected; var btn = Ui.Pill(label, () => pick(key), on, false); btn.Padding = new Thickness(12, 5, 12, 5); btn.Background = on ? Pal.Accent : Brushes.Transparent; btn.BorderBrush = Brushes.Transparent; btn.BorderThickness = new Thickness(0);
            System.Windows.Automation.AutomationProperties.SetName(btn, label + (on ? ", selected" : "")); row.Children.Add(btn);
        }
        Child = row;
    }
}

