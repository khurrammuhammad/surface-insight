using System.Windows;
using System.Windows.Media;
namespace SurfaceInsight;

/// <summary>Design tokens: light/dark Liquid Glass palette, per-metric colours and stable per-app colours.</summary>
public static class Pal
{
    public static bool Dark;
    public static Brush Text = null!, Text2 = null!, Accent = null!, AccentText = null!, AccentBg = null!, Ok = null!, OkText = null!, OkBg = null!, Warn = null!, WarnText = null!, WarnBg = null!, Danger = null!, DangerBg = null!,
        Card = null!, Edge = null!, Inset = null!, Grid = null!, Sel = null!, Hover = null!, Side = null!, Tip = null!, TipText = null!, GapBg = null!, Shadow = null!, Window = null!;
    public static Color Watts = C("#14A58D"), CpuColor = C("#3B82F6"), Gpu = C("#8B5CF6"), Npu = C("#E0457B"), Io = C("#F08A24");
    static readonly Color[] AppPalette = { C("#3B82F6"), C("#F08A24"), C("#14A58D"), C("#8B5CF6"), C("#E5484D"), C("#D4A017"), C("#EC5BA5"), C("#6B7A90") };
    static readonly Dictionary<string, Color> appColors = new(StringComparer.OrdinalIgnoreCase);

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    public static Brush Of(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static Color Alpha(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
    public static Color AppColor(string name) { if (!appColors.TryGetValue(name, out var c)) appColors[name] = c = AppPalette[appColors.Count % AppPalette.Length]; return c; }

    public static bool SystemIsDark() { try { return (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; } catch { return false; } }

    public static void Apply(bool dark, ResourceDictionary res)
    {
        Dark = dark;
        if (!dark)
        {
            Text = B("#12161F"); Text2 = B("#4B5565"); Accent = B("#2563EB"); AccentText = B("#1D4FC4"); AccentBg = B("#212563EB"); Ok = B("#12906F"); OkText = B("#0B6B52"); OkBg = B("#2412906F"); Warn = B("#B45309"); WarnText = B("#8A3F06"); WarnBg = B("#29D97706"); Danger = B("#B4232A"); DangerBg = B("#1ADC2626");
            Card = B("#A3FFFFFF"); Edge = B("#C8FFFFFF"); Inset = B("#8CFFFFFF"); Grid = B("#24405078"); Sel = B("#242563EB"); Hover = B("#143C64C8"); Side = B("#66FFFFFF"); Tip = B("#F21B2230"); TipText = B("#F3F6FB"); GapBg = B("#2E78829A"); Shadow = B("#24233A5A");
            Window = new LinearGradientBrush(new GradientStopCollection { new(C("#66A7D4FF"), 0), new(C("#33D9C2FF"), .55), new(C("#4D9BE8D6"), 1) }, 60);
        }
        else
        {
            Text = B("#F1F4FA"); Text2 = B("#AAB3C5"); Accent = B("#3B82F6"); AccentText = B("#8CBCFF"); AccentBg = B("#2E5A96FF"); Ok = B("#34D3A6"); OkText = B("#5FE0BB"); OkBg = B("#2934D3A6"); Warn = B("#F5A524"); WarnText = B("#FBBF5C"); WarnBg = B("#2EF5A524"); Danger = B("#FF8C92"); DangerBg = B("#29FF5A64");
            Card = B("#14FFFFFF"); Edge = B("#1FFFFFFF"); Inset = B("#14FFFFFF"); Grid = B("#1FFFFFFF"); Sel = B("#385A96FF"); Hover = B("#12FFFFFF"); Side = B("#59080C18"); Tip = B("#F2EEF2FA"); TipText = B("#12161F"); GapBg = B("#12FFFFFF"); Shadow = B("#66000000");
            Window = new LinearGradientBrush(new GradientStopCollection { new(C("#8C1D3A78"), 0), new(C("#664A2A80"), .55), new(C("#800E5A52"), 1) }, 60);
        }
        Window.Freeze();
        res["TextBrush"] = Text; res["Text2Brush"] = Text2; res["AccentBrush"] = Accent; res["HoverBrush"] = Hover; res["EdgeBrush"] = Edge; res["InsetBrush"] = Inset; res["SelBrush"] = Sel; res["GridBrush"] = Grid;
    }
}
