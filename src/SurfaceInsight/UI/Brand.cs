using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SurfaceInsight;

/// <summary>
/// The Surface Insight mark, drawn in code so every size is crisp: a blue-to-violet glass tile with a white pulse line.
/// <c>SurfaceInsight.exe --brand &lt;folder&gt;</c> renders the PNG set, the multi-size .ico and the wordmarks.
/// </summary>
public static class Brand
{
    public static readonly Color Sky = Pal.C("#59B7FF"), Blue = Pal.C("#2F6BFF"), Violet = Pal.C("#7A5CFF"), Ink = Pal.C("#12161F"), Paper = Pal.C("#F4F7FC");

    /// <summary>Draws the mark into a square of side <paramref name="s"/>. Small sizes get a heavier line and no highlight so they stay legible.</summary>
    public static void DrawMark(DrawingContext dc, double s)
    {
        bool tiny = s <= 24, small = s <= 48; double pad = tiny ? 0 : s * 0.04, side = s - 2 * pad, r = side * (tiny ? 0.24 : 0.235);
        var tile = new Rect(pad, pad, side, side);
        var grad = new LinearGradientBrush(new GradientStopCollection { new(Sky, 0), new(Blue, 0.55), new(Violet, 1) }, new Point(0, 0), new Point(1, 1)); grad.Freeze();
        if (!small) { var shadow = new SolidColorBrush(Color.FromArgb(40, 20, 40, 120)); dc.DrawRoundedRectangle(shadow, null, new Rect(tile.X, tile.Y + s * 0.012, tile.Width, tile.Height), r, r); }
        dc.DrawRoundedRectangle(grad, null, tile, r, r);
        if (!tiny)
        {
            // glass: soft top highlight and a thin inner edge
            var hi = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(110, 255, 255, 255), 0), new(Color.FromArgb(0, 255, 255, 255), 0.55) }, 90); hi.Freeze();
            dc.PushClip(new RectangleGeometry(tile, r, r)); dc.DrawEllipse(hi, null, new Point(s * 0.42, pad + side * 0.02), side * 0.78, side * 0.46); dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), Math.Max(1, s * 0.008)), new Rect(tile.X + s * 0.006, tile.Y + s * 0.006, tile.Width - s * 0.012, tile.Height - s * 0.012), r, r);
        }
        // pulse line: flat, small rise, deep dip, tall peak, return to flat
        double u = side / 100, ox = pad, oy = pad; Point P(double x, double y) => new(ox + x * u, oy + y * u);
        var g = new StreamGeometry(); using (var c = g.Open())
        {
            if (tiny) { c.BeginFigure(P(14, 54), false, false); c.LineTo(P(34, 54), true, true); c.LineTo(P(44, 30), true, true); c.LineTo(P(56, 74), true, true); c.LineTo(P(66, 46), true, true); c.LineTo(P(86, 46), true, true); }
            else { c.BeginFigure(P(16, 53), false, false); c.LineTo(P(33, 53), true, true); c.LineTo(P(41, 33), true, true); c.LineTo(P(53, 72), true, true); c.LineTo(P(62, 44), true, true); c.LineTo(P(68, 53), true, true); c.LineTo(P(84, 53), true, true); }
        }
        g.Freeze(); double w = side * (tiny ? 0.125 : small ? 0.095 : 0.08);
        var pen = new Pen(Brushes.White, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); dc.DrawGeometry(null, pen, g);
        if (!tiny) dc.DrawEllipse(Brushes.White, null, P(84, 53), w * 0.95, w * 0.95);
    }

    /// <summary>The embedded multi-size icon (taskbar, title bar, tray). Null only if the resource is missing.</summary>
    public static Stream? IconStream() => typeof(Brand).Assembly.GetManifestResourceStream("AppIcon.ico");
    public static BitmapFrame? WindowIcon() { try { using var s = IconStream(); if (s == null) return null; var d = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); return d.Frames.OrderByDescending(f => f.PixelWidth).First(); } catch { return null; } }
    public static System.Drawing.Icon TrayIcon() { try { using var s = IconStream(); if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize); } catch { } return System.Drawing.SystemIcons.Application; }
    public static BitmapSource Render(int px, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv); bmp.Freeze(); return bmp;
    }
    public static BitmapSource RenderMark(int px) => Render(px, dc => DrawMark(dc, px));
    static byte[] Png(BitmapSource b) { var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b)); using var ms = new MemoryStream(); e.Save(ms); return ms.ToArray(); }

    /// <summary>Writes a Windows .ico with PNG-compressed frames (supported since Windows Vista).</summary>
    public static void WriteIco(string path, int[] sizes)
    {
        var images = sizes.Select(sz => (sz, Png(RenderMark(sz)))).ToList(); using var fs = File.Create(path); using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count); int offset = 6 + 16 * images.Count;
        foreach (var (sz, data) in images) { w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32); w.Write(data.Length); w.Write(offset); offset += data.Length; }
        foreach (var (_, data) in images) w.Write(data);
    }

    public static BitmapSource Wordmark(bool dark, int height = 400, bool withTagline = false)
    {
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText("Surface Insight", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, height * 0.42, new SolidColorBrush(dark ? Paper : Ink), 1.0);
        var tag = new FormattedText("Battery, CPU and network insight for Windows", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), height * 0.13, new SolidColorBrush(dark ? Pal.C("#AAB3C5") : Pal.C("#4B5565")), 1.0);
        double mark = height * 0.8, gap = height * 0.18, w = Math.Ceiling(height * 0.1 + mark + gap + Math.Max(text.Width, withTagline ? tag.Width : 0) + height * 0.1);
        var dv = new DrawingVisual(); using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(height * 0.1, height * 0.1)); DrawMark(dc, mark); dc.Pop();
            double tx = height * 0.1 + mark + gap; double ty = withTagline ? height * 0.12 : (height - text.Height) / 2; dc.DrawText(text, new Point(tx, ty)); if (withTagline) dc.DrawText(tag, new Point(tx + height * 0.01, ty + text.Height * 0.98));
        }
        var bmp = new RenderTargetBitmap((int)w, height, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv); bmp.Freeze(); return bmp;
    }

    public static void Export(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var px in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 }) File.WriteAllBytes(Path.Combine(dir, $"surface-insight-icon-{px}.png"), Png(RenderMark(px)));
        WriteIco(Path.Combine(dir, "AppIcon.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 256 });
        File.WriteAllBytes(Path.Combine(dir, "surface-insight-wordmark-light.png"), Png(Wordmark(false)));
        File.WriteAllBytes(Path.Combine(dir, "surface-insight-wordmark-dark.png"), Png(Wordmark(true)));
        File.WriteAllBytes(Path.Combine(dir, "surface-insight-lockup-tagline-light.png"), Png(Wordmark(false, 500, true)));
        File.WriteAllBytes(Path.Combine(dir, "surface-insight-lockup-tagline-dark.png"), Png(Wordmark(true, 500, true)));
        // a preview sheet: the mark at every icon size on light and dark backgrounds
        var dv = new DrawingVisual(); int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Paper), null, new Rect(0, 0, 900, 300)); dc.DrawRectangle(new SolidColorBrush(Pal.C("#101522")), null, new Rect(0, 300, 900, 300));
            double x = 30; foreach (var sz in sizes) { foreach (var y0 in new[] { 0.0, 300.0 }) { dc.PushTransform(new TranslateTransform(x, y0 + 150 - sz / 2.0)); dc.DrawImage(RenderMark(sz), new Rect(0, 0, sz, sz)); dc.Pop(); } x += sz + 26; }
        }
        var sheet = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); sheet.Render(dv); File.WriteAllBytes(Path.Combine(dir, "icon-preview-sheet.png"), Png(sheet));
    }
}
