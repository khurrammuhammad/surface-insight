using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
namespace SurfaceInsight;

/// <summary>A destination on the map: approximate city of one or more remote addresses.</summary>
public sealed class MapPlace
{
    public string Key = "", Label = "", Country = ""; public double Lat, Lon, Bytes; public int Endpoints; public bool Highlight; public List<string> Hosts = new();
}

/// <summary>
/// Offline world map (Web Mercator, Natural Earth outlines) with curved lines from the approximate home location to destination cities.
/// Drag to pan, wheel to zoom, click a city to select it. Locations come from the bundled DB-IP database and are approximate.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public List<MapPlace> Places = new(); public (string Label, double Lat, double Lon) Home = Geo.Home(); public string? Selected;
    public event Action<MapPlace?>? PlaceClicked;
    double scale = 1, ox, oy; bool userMoved; Point? drag; Point dragOrigin; StreamGeometry? land, borders; MapPlace? hover; readonly List<(Rect Box, MapPlace P)> hits = new();
    const double World = 1000; // projected world is World x World units

    public MapView() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand; SnapsToDevicePixels = true; }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    static Point Project(double lon, double lat) { lat = Math.Clamp(lat, -82, 84); double x = (lon + 180) / 360 * World; double s = Math.Sin(lat * Math.PI / 180); double y = (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * World; return new Point(x, y); }
    Point ToScreen(Point w) => new(w.X * scale + ox, w.Y * scale + oy);
    /// <summary>Longitude shifted by whole turns to the copy nearest home, so lines take the short way (e.g. across the Pacific) on the repeating map.</summary>
    double Near(double lon) { double h = Home.Lon; while (lon - h > 180) lon -= 360; while (lon - h < -180) lon += 360; return lon; }
    Point PlacePoint(MapPlace p) => Project(Near(p.Lon), p.Lat);
    Point ToWorld(Point s) => new((s.X - ox) / scale, (s.Y - oy) / scale);

    static StreamGeometry Build(List<List<Point>> rings, bool closed)
    {
        var g = new StreamGeometry { FillRule = FillRule.Nonzero }; using (var c = g.Open()) foreach (var r in rings)
            {
                Point prev = Project(r[0].X, r[0].Y); c.BeginFigure(prev, closed, closed);
                for (int i = 1; i < r.Count; i++) { var p = Project(r[i].X, r[i].Y); if (Math.Abs(p.X - prev.X) > World / 2) { c.BeginFigure(p, closed, closed); } else c.LineTo(p, true, false); prev = p; }
            }
        g.Freeze(); return g;
    }

    /// <summary>Sets destinations; refits the view unless the user has panned or zoomed.</summary>
    public void SetPlaces(List<MapPlace> places, bool refit) { Places = places; if (refit || !userMoved) { userMoved = false; Fit(); } InvalidateVisual(); }
    public void ResetView() { userMoved = false; Fit(); InvalidateVisual(); }
    void Fit()
    {
        double W = ActualWidth, H = ActualHeight; if (W < 20 || H < 20) return; var pts = Places.Where(p => p.Highlight || Places.All(q => !q.Highlight)).Select(PlacePoint).Append(Project(Home.Lon, Home.Lat)).ToList();
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double w = Math.Max(maxX - minX, 40), h = Math.Max(maxY - minY, 30); scale = Math.Clamp(Math.Min(W / (w * 1.35), H / (h * 1.45)), Math.Min(W, H) / World * 0.8, 60);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2; ox = W / 2 - cx * scale; oy = H / 2 - cy * scale; Clamp();
    }
    void Clamp()
    {
        double W = ActualWidth, H = ActualHeight; double minScale = Math.Min(W, H) / World * 0.8; if (scale < minScale) scale = minScale;
        // horizontally the world repeats, so keep the view within one copy either side; vertically clamp or centre
        double ww = World * scale; while (ox > W) ox -= ww; while (ox < -2 * ww + W) ox += ww;
        oy = ww <= H ? (H - ww) / 2 : Math.Min(0, Math.Max(H - ww, oy));
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo s) { base.OnRenderSizeChanged(s); if (!userMoved) Fit(); else Clamp(); }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight; bool dark = Pal.Dark;
        dc.DrawRectangle(new SolidColorBrush(dark ? Pal.C("#0E1A2A") : Pal.C("#CFE2F1")), null, new Rect(0, 0, W, H)); if (W < 20 || H < 20) return;
        land ??= Build(WorldShapes.Land, true); borders ??= Build(WorldShapes.Borders, false);
        var landBrush = new SolidColorBrush(dark ? Pal.C("#243245") : Pal.C("#EEF1E4")); var coast = new Pen(new SolidColorBrush(dark ? Pal.C("#35475E") : Pal.C("#B8C9D6")), 0.8 / scale); var border = new Pen(new SolidColorBrush(dark ? Pal.C("#3D4F66") : Pal.C("#C9C2D6")), 0.7 / scale) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
        for (int k = -2; k <= 2; k++) { double tx = ox + k * World * scale; if (tx > W || tx + World * scale < 0) continue; dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, tx, oy)); dc.DrawGeometry(landBrush, coast, land); dc.DrawGeometry(null, border, borders); dc.Pop(); }

        var home = ToScreen(Project(Home.Lon, Home.Lat)); hits.Clear(); double maxB = Places.Select(p => p.Bytes).DefaultIfEmpty(0).Max(); bool anyHi = Places.Any(p => p.Highlight);
        var lineCol = dark ? Pal.C("#7FA6D6") : Pal.C("#355F8A"); var hiCol = Pal.Accent is SolidColorBrush ab ? ab.Color : Pal.CpuColor;
        foreach (var p in Places.OrderBy(p => p.Highlight).ThenBy(p => p.Bytes))
        {
            var d = ToScreen(PlacePoint(p)); bool hi = p.Highlight || p.Key == Selected; double t = maxB > 0 ? Math.Sqrt(p.Bytes / maxB) : 0.3;
            double dx = d.X - home.X, dy = d.Y - home.Y, len = Math.Sqrt(dx * dx + dy * dy); if (len < 2) continue; var ctrl = new Point((home.X + d.X) / 2 - dy * 0.18, (home.Y + d.Y) / 2 + dx * 0.18 - len * 0.08);
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(home, false, false); c.QuadraticBezierTo(ctrl, d, true, false); } g.Freeze();
            var col = hi ? hiCol : lineCol; byte alpha = (byte)(anyHi && !hi ? 40 : 150 + t * 90); var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)), (hi ? 2.2 : 1.4) + t * 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; dc.DrawGeometry(null, pen, g);
        }
        // city nodes and label pills, largest first, skipping labels that would overlap
        var placed = new List<Rect>(); var homeBox = new Rect(home.X - 22, home.Y - 22, 44, 44); placed.Add(homeBox);
        foreach (var p in Places.OrderByDescending(p => p.Highlight || p.Key == Selected).ThenByDescending(p => p.Bytes))
        {
            var d = ToScreen(PlacePoint(p)); if (d.X < -40 || d.Y < -40 || d.X > W + 40 || d.Y > H + 40) continue; bool hi = p.Highlight || p.Key == Selected; if (anyHi && !hi && p != hover) continue;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 0.8), d, 9, 9); dc.DrawEllipse(new SolidColorBrush(hi ? hiCol : Pal.C("#9AA3AE")), null, d, 5, 5);
            var ft = ChartText.Ft(this, p.Label, 12.5, new SolidColorBrush(Pal.C("#1B2230")), true); var box = new Rect(d.X + 12, d.Y - ft.Height / 2 - 5, ft.Width + 20, ft.Height + 10);
            if (box.Right > W - 4) box.X = d.X - 12 - box.Width;
            hits.Add((new Rect(d.X - 10, d.Y - 10, 20, 20), p));
            if (placed.Any(r => r.IntersectsWith(box)) && p != hover && p.Key != Selected) continue; placed.Add(box); hits.Add((box, p));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), new Pen(new SolidColorBrush(p.Key == Selected ? hiCol : Color.FromArgb(40, 0, 0, 0)), p.Key == Selected ? 2 : 1), box, box.Height / 2, box.Height / 2); dc.DrawText(ft, new Point(box.X + 10, box.Y + 5));
        }
        // home
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 1), home, 19, 19);
        var glyph = new FormattedText("", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 18, new SolidColorBrush(Pal.C("#1B2230")), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(glyph, new Point(home.X - glyph.Width / 2, home.Y - glyph.Height / 2));
        var att = ChartText.Ft(this, Geo.Attribution + " · home: " + Home.Label + " · locations approximate", 10, new SolidColorBrush(dark ? Pal.C("#9FB0C4") : Pal.C("#4B5565"))); var ab2 = new Rect(W - att.Width - 14, H - att.Height - 8, att.Width + 10, att.Height + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(dark ? Color.FromArgb(170, 14, 26, 42) : Color.FromArgb(190, 255, 255, 255)), null, ab2, 4, 4); dc.DrawText(att, new Point(ab2.X + 5, ab2.Y + 2));
        if (!Geo.Available) { var m = ChartText.Ft(this, "Location database not found; the map cannot place destinations.", 13, Pal.Text, true); dc.DrawText(m, new Point(16, 14)); }
        else if (Places.Count == 0) { var m = ChartText.Ft(this, "No public destinations with a known location in this interval.", 13, new SolidColorBrush(dark ? Colors.White : Pal.C("#1B2230")), true); dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, dark ? (byte)14 : (byte)255, dark ? (byte)26 : (byte)255, dark ? (byte)42 : (byte)255)), null, new Rect(12, 12, m.Width + 20, m.Height + 12), 8, 8); dc.DrawText(m, new Point(22, 18)); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this); var w = ToWorld(at); double f = e.Delta > 0 ? 1.25 : 0.8; scale = Math.Clamp(scale * f, 0.1, 80); ox = at.X - w.X * scale; oy = at.Y - w.Y * scale; Clamp(); userMoved = true; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); drag = e.GetPosition(this); dragOrigin = new Point(ox, oy); CaptureMouse(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture(); var at = e.GetPosition(this); bool click = drag is Point d0 && (at - d0).Length < 4; drag = null;
        if (click) { var hit = hits.LastOrDefault(h => h.Box.Contains(at)).P; Selected = hit?.Key; PlaceClicked?.Invoke(hit); InvalidateVisual(); }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (drag is Point d0 && e.LeftButton == MouseButtonState.Pressed) { ox = dragOrigin.X + (at.X - d0.X); oy = dragOrigin.Y + (at.Y - d0.Y); Clamp(); userMoved = true; InvalidateVisual(); return; }
        var h = hits.LastOrDefault(x => x.Box.Contains(at)).P; if (h != hover) { hover = h; ToolTip = h == null ? null : h.Label + (h.Country.Length > 0 && h.Country != h.Label ? ", " + h.Country : "") + "\n" + h.Endpoints + " address" + (h.Endpoints == 1 ? "" : "es") + (h.Bytes > 0 ? " · " + MainWindow.BytesText(h.Bytes) : "") + (h.Hosts.Count > 0 ? "\n" + string.Join(", ", h.Hosts.Take(4)) + (h.Hosts.Count > 4 ? " …" : "") : "") + "\nApproximate location of the address, not of the company."; InvalidateVisual(); }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = 60; switch (e.Key) { case Key.Left: ox += step; break; case Key.Right: ox -= step; break; case Key.Up: oy += step; break; case Key.Down: oy -= step; break; case Key.Add: case Key.OemPlus: scale *= 1.25; break; case Key.Subtract: case Key.OemMinus: scale *= 0.8; break; case Key.Home: ResetView(); return; default: return; }
        userMoved = true; Clamp(); InvalidateVisual(); e.Handled = true;
    }
}
