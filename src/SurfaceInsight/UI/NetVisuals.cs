using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SurfaceInsight.Network;
namespace SurfaceInsight;

public sealed record GeoInfo(string City, string Region, string CountryCode, string Country, double Lat, double Lon)
{
    public string Label => City.Length > 0 ? City : Country;
}

/// <summary>Offline IP geolocation from the bundled DB-IP City Lite database. Only public addresses are looked up; results are approximate.</summary>
public static class Geo
{
    static MaxMind.Db.Reader? reader; static bool tried; static readonly ConcurrentDictionary<string, GeoInfo?> cache = new();
    public static string AssetDir => Path.Combine(AppContext.BaseDirectory, "Assets");
    public const string Attribution = "IP locations: DB-IP.com (CC BY 4.0) · Map: Natural Earth";
    public static bool Available { get { Open(); return reader != null; } }
    static void Open() { if (tried) return; tried = true; try { var f = Path.Combine(AssetDir, "dbip-city-lite.mmdb"); if (File.Exists(f)) reader = new MaxMind.Db.Reader(f, MaxMind.Db.FileAccessMode.MemoryMapped); } catch (Exception ex) { MonitorHost.Log("Geo database could not be opened: " + ex.Message); } }
    public static GeoInfo? Lookup(string ip)
    {
        if (IpClassifier.Classify(ip) != IpScope.Public) return null; Open(); if (reader == null) return null;
        return cache.GetOrAdd(ip, k =>
        {
            try
            {
                if (!IPAddress.TryParse(k, out var a)) return null; var d = reader.Find<Dictionary<string, object>>(a); if (d == null) return null;
                static string Name(object? o) => o is Dictionary<string, object> n && n.TryGetValue("names", out var nn) && nn is Dictionary<string, object> m && m.TryGetValue("en", out var en) ? en?.ToString() ?? "" : "";
                d.TryGetValue("city", out var city); d.TryGetValue("country", out var country); d.TryGetValue("location", out var loc); d.TryGetValue("subdivisions", out var subs);
                string cc = country is Dictionary<string, object> c && c.TryGetValue("iso_code", out var iso) ? iso?.ToString() ?? "" : "";
                double lat = 0, lon = 0; if (loc is Dictionary<string, object> l) { if (l.TryGetValue("latitude", out var la)) lat = Convert.ToDouble(la); if (l.TryGetValue("longitude", out var lo)) lon = Convert.ToDouble(lo); }
                string region = subs is List<object> sl && sl.Count > 0 ? Name(sl[0]) : "";
                return lat == 0 && lon == 0 ? null : new GeoInfo(Name(city), region, cc, Name(country), lat, lon);
            }
            catch { return null; }
        });
    }

    /// <summary>The map's home point. There is no location sensor use and no online lookup: it is derived from the Windows time zone, so it is approximate.</summary>
    public static (string Label, double Lat, double Lon) Home()
    {
        string id = TimeZoneInfo.Local.Id;
        var t = new Dictionary<string, (string, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUS Eastern Standard Time"] = ("Sydney", -33.87, 151.21), ["E. Australia Standard Time"] = ("Brisbane", -27.47, 153.03), ["Cen. Australia Standard Time"] = ("Adelaide", -34.93, 138.6), ["AUS Central Standard Time"] = ("Darwin", -12.46, 130.84), ["W. Australia Standard Time"] = ("Perth", -31.95, 115.86), ["Tasmania Standard Time"] = ("Hobart", -42.88, 147.33),
            ["New Zealand Standard Time"] = ("Auckland", -36.85, 174.76), ["Tokyo Standard Time"] = ("Tokyo", 35.68, 139.69), ["Korea Standard Time"] = ("Seoul", 37.57, 126.98), ["China Standard Time"] = ("Shanghai", 31.23, 121.47), ["Singapore Standard Time"] = ("Singapore", 1.35, 103.82), ["India Standard Time"] = ("Mumbai", 19.08, 72.88), ["Pakistan Standard Time"] = ("Karachi", 24.86, 67.0), ["Arabian Standard Time"] = ("Dubai", 25.2, 55.27), ["Arab Standard Time"] = ("Riyadh", 24.71, 46.68),
            ["GMT Standard Time"] = ("London", 51.51, -0.13), ["W. Europe Standard Time"] = ("Berlin", 52.52, 13.4), ["Romance Standard Time"] = ("Paris", 48.86, 2.35), ["Central Europe Standard Time"] = ("Prague", 50.08, 14.44), ["Central European Standard Time"] = ("Warsaw", 52.23, 21.01), ["E. Europe Standard Time"] = ("Bucharest", 44.43, 26.1), ["FLE Standard Time"] = ("Helsinki", 60.17, 24.94), ["Russian Standard Time"] = ("Moscow", 55.76, 37.62), ["South Africa Standard Time"] = ("Johannesburg", -26.2, 28.05),
            ["Eastern Standard Time"] = ("New York", 40.71, -74.0), ["Central Standard Time"] = ("Chicago", 41.88, -87.63), ["Mountain Standard Time"] = ("Denver", 39.74, -104.99), ["Pacific Standard Time"] = ("Los Angeles", 34.05, -118.24), ["Alaskan Standard Time"] = ("Anchorage", 61.22, -149.9), ["Hawaiian Standard Time"] = ("Honolulu", 21.31, -157.86), ["E. South America Standard Time"] = ("São Paulo", -23.55, -46.63), ["Argentina Standard Time"] = ("Buenos Aires", -34.6, -58.38), ["Canada Central Standard Time"] = ("Regina", 50.45, -104.62), ["Atlantic Standard Time"] = ("Halifax", 44.65, -63.57),
        };
        if (t.TryGetValue(id, out var v)) return (v.Item1 + " (from time zone)", v.Item2, v.Item3);
        double lon = TimeZoneInfo.Local.BaseUtcOffset.TotalHours * 15; return ("Approximate (time zone " + id + ")", 0, lon);
    }
}

/// <summary>Map outlines loaded once from the bundled Natural Earth GeoJSON, as rings of (lon, lat).</summary>
public static class WorldShapes
{
    static List<List<Point>>? land, borders;
    public static List<List<Point>> Land => land ??= Load("ne_50m_land.geojson");
    public static List<List<Point>> Borders => borders ??= Load("ne_50m_admin_0_boundary_lines_land.geojson");
    static List<List<Point>> Load(string file)
    {
        var rings = new List<List<Point>>(); try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Geo.AssetDir, file)));
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var g = f.GetProperty("geometry"); string type = g.GetProperty("type").GetString() ?? ""; var c = g.GetProperty("coordinates");
                void Ring(JsonElement r) { var l = new List<Point>(r.GetArrayLength()); foreach (var p in r.EnumerateArray()) l.Add(new Point(p[0].GetDouble(), p[1].GetDouble())); if (l.Count > 1) rings.Add(l); }
                if (type == "Polygon") foreach (var r in c.EnumerateArray()) Ring(r);
                else if (type == "MultiPolygon") foreach (var poly in c.EnumerateArray()) foreach (var r in poly.EnumerateArray()) Ring(r);
                else if (type == "LineString") Ring(c);
                else if (type == "MultiLineString") foreach (var r in c.EnumerateArray()) Ring(r);
            }
        }
        catch (Exception ex) { MonitorHost.Log("Map outlines could not be loaded: " + ex.Message); }
        return rings;
    }
}

/// <summary>Executable icons and Authenticode signer names, cached. A valid signature identifies the publisher; it is not a safety verdict.</summary>
public static class AppMeta
{
    static readonly ConcurrentDictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase); static readonly ConcurrentDictionary<string, string> sigs = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? Icon(string path)
    {
        return icons.GetOrAdd(path, p =>
        {
            try { if (!File.Exists(p)) return null; using var ic = System.Drawing.Icon.ExtractAssociatedIcon(p); if (ic == null) return null; var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32)); src.Freeze(); return src; } catch { return null; }
        });
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct FileInfo_ { public uint cbStruct; public string path; public IntPtr hFile; public IntPtr known; }
    [StructLayout(LayoutKind.Sequential)] struct TrustData { public uint cbStruct; public IntPtr policy, sip; public uint ui, revocation, choice; public IntPtr file; public uint state; public IntPtr wvt; public IntPtr urlRef; public uint provFlags, uiContext; public IntPtr sig; }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
    static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    /// <summary>"Signed by X (valid)", "Signed by X (signature not valid)" or "Not signed". Embedded signatures only; catalog-signed system files may read as not signed.</summary>
    public static string Signature(string path)
    {
        return sigs.GetOrAdd(path, p =>
        {
            if (!File.Exists(p)) return "Unavailable (file not found)";
            string signer = "";
#pragma warning disable SYSLIB0057
            try { using var c = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(p); signer = c.Subject.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("CN=", StringComparison.Ordinal))?[3..].Trim('"') ?? c.Subject; } catch { }
#pragma warning restore SYSLIB0057
            if (signer.Length == 0) return "Not signed (no embedded signature)";
            int rc = -1; var fi = new FileInfo_ { cbStruct = (uint)Marshal.SizeOf<FileInfo_>(), path = p }; IntPtr pfi = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo_>());
            try
            {
                Marshal.StructureToPtr(fi, pfi, false); var td = new TrustData { cbStruct = (uint)Marshal.SizeOf<TrustData>(), ui = 2, revocation = 0, choice = 1, file = pfi, state = 1 }; var g = GenericVerify; rc = WinVerifyTrust(new IntPtr(-1), ref g, ref td);
                td.state = 2; WinVerifyTrust(new IntPtr(-1), ref g, ref td);
            }
            catch { } finally { Marshal.DestroyStructure<FileInfo_>(pfi); Marshal.FreeHGlobal(pfi); }
            return "Signed by " + signer + (rc == 0 ? " (valid)" : " (signature not valid)");
        });
    }
}

/// <summary>Tiny two-colour activity bars for list rows: sent above, received below the middle line.</summary>
public sealed class MiniBars : FrameworkElement
{
    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(nameof(Up), typeof(object), typeof(MiniBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(nameof(Down), typeof(object), typeof(MiniBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object? Up { get => GetValue(UpProperty); set => SetValue(UpProperty, value); }
    public object? Down { get => GetValue(DownProperty); set => SetValue(DownProperty, value); }
    public static readonly DependencyProperty ActProperty = DependencyProperty.Register(nameof(Act), typeof(object), typeof(MiniBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Activity without byte counts (basic snapshots), drawn grey from the bottom.</summary>
    public object? Act { get => GetValue(ActProperty); set => SetValue(ActProperty, value); }
    public static readonly Color SentColor = Pal.C("#C46AE0"), RecvColor = Pal.C("#3B82F6");
    protected override void OnRender(DrawingContext dc)
    {
        var up = Up as double[] ?? Array.Empty<double>(); var down = Down as double[] ?? Array.Empty<double>(); var act = Act as double[] ?? Array.Empty<double>(); int n = Math.Max(Math.Max(up.Length, down.Length), act.Length); double W = ActualWidth, H = ActualHeight; if (n == 0 || W < 4) return;
        if (act.Length > 0 && up.Length == 0) { var tr = new SolidColorBrush(Pal.Alpha(Pal.Dark ? Colors.White : Colors.Black, .08)); var gb = new SolidColorBrush(Pal.Alpha(Pal.Dark ? Colors.White : Colors.Black, .38)); double bw0 = W / n; for (int i = 0; i < n; i++) { double x = i * bw0 + 0.5, w = Math.Max(1, bw0 - 1.5); dc.DrawRectangle(tr, null, new Rect(x, 1, w, H - 2)); double v = Math.Clamp(act[i], 0, 1); if (v > 0) { double h = Math.Max(1.5, v * (H - 2)); dc.DrawRectangle(gb, null, new Rect(x, H - 1 - h, w, h)); } } return; }
        double bw = W / n, mid = H / 2; var track = new SolidColorBrush(Pal.Alpha(Pal.Dark ? Colors.White : Colors.Black, .08)); var su = new SolidColorBrush(SentColor); var rd = new SolidColorBrush(RecvColor);
        for (int i = 0; i < n; i++)
        {
            double x = i * bw + 0.5, w = Math.Max(1, bw - 1.5); dc.DrawRectangle(track, null, new Rect(x, 1, w, H - 2));
            double u = i < up.Length && double.IsFinite(up[i]) ? Math.Clamp(up[i], 0, 1) : 0, d = i < down.Length && double.IsFinite(down[i]) ? Math.Clamp(down[i], 0, 1) : 0;
            if (u > 0) { double h = Math.Max(1.5, u * (mid - 1)); dc.DrawRectangle(su, null, new Rect(x, mid - h, w, h)); }
            if (d > 0) { double h = Math.Max(1.5, d * (mid - 1)); dc.DrawRectangle(rd, null, new Rect(x, mid, w, h)); }
        }
    }
}

/// <summary>Histogram with sent bars above and received bars below a centre line (Little Snitch-style traffic summary).</summary>
public sealed class UpDownBars : FrameworkElement
{
    public double[] Up = Array.Empty<double>(), Down = Array.Empty<double>(); public string[] Labels = Array.Empty<string>(); int hover = -1;
    public UpDownBars() { ToolTipService.SetInitialShowDelay(this, 100); }
    public void Set(double[] up, double[] down, string[] labels) { Up = up; Down = down; Labels = labels; InvalidateVisual(); }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e) { int n = Math.Max(Up.Length, Down.Length); if (n == 0) return; int i = (int)(e.GetPosition(this).X / Math.Max(1, ActualWidth) * n); if (i != hover) { hover = Math.Clamp(i, 0, n - 1); ToolTip = (hover < Labels.Length ? Labels[hover] + "\n" : "") + "Sent " + MainWindow.BytesText(hover < Up.Length ? Up[hover] : 0) + " · Received " + MainWindow.BytesText(hover < Down.Length ? Down[hover] : 0); InvalidateVisual(); } }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { hover = -1; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight; dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H)); int n = Math.Max(Up.Length, Down.Length); if (n == 0 || W < 10) return;
        double mu = Up.Where(double.IsFinite).DefaultIfEmpty(0).Max(), md = Down.Where(double.IsFinite).DefaultIfEmpty(0).Max(), max = Math.Max(1, Math.Max(mu, md)); double mid = H * 0.42, bw = W / n;
        var track = new SolidColorBrush(Pal.Alpha(Pal.Dark ? Colors.White : Colors.Black, .07)); var su = new SolidColorBrush(MiniBars.SentColor); var rd = new SolidColorBrush(MiniBars.RecvColor);
        for (int i = 0; i < n; i++)
        {
            double x = i * bw + 1, w = Math.Max(2, bw - 3); dc.DrawRoundedRectangle(i == hover ? new SolidColorBrush(Pal.Alpha(MiniBars.RecvColor, .18)) : track, null, new Rect(x, 0, w, H), 3, 3);
            double u = i < Up.Length && double.IsFinite(Up[i]) ? Up[i] / max : 0, d = i < Down.Length && double.IsFinite(Down[i]) ? Down[i] / max : 0;
            if (u > 0) { double h = Math.Max(2, Math.Sqrt(u) * (mid - 2)); dc.DrawRoundedRectangle(su, null, new Rect(x, mid - h, w, h), 2, 2); }
            if (d > 0) { double h = Math.Max(2, Math.Sqrt(d) * (H - mid - 2)); dc.DrawRoundedRectangle(rd, null, new Rect(x, mid, w, h), 2, 2); }
        }
    }
}
