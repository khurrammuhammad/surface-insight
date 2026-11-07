using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Path = System.IO.Path;
namespace SurfaceInsight;

public sealed partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly History history = new(); readonly EventWaitHandle showSignal = new(false, EventResetMode.AutoReset, "Local\\SurfaceInsightShow");
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) }; readonly ChartGroup group = new();
    readonly HashSet<string> layers = new(StringComparer.OrdinalIgnoreCase);
    RangeData? data; Sample? latest; List<Sample> recent = new(); DateTime? lastSampleTime; DateTime lastFullLoad;
    string page = "now"; string? app; string rangeKey = "1h"; DateTime? customFrom, customTo; string themeMode = "system";
    int loadToken; bool busy; Action? updatePage; double? savedScroll;
    ContentControl headerHost = new(), bodyHost = new(); ScrollViewer? scroll; Border? liveBadge; TextBlock? liveText, sideHealth, sideDetail; Ellipse? liveDot, sideDot;
    Border toast = new(); TextBlock toastText = new(); readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    Grid? shell; CancellationTokenSource? capture; bool diagnosing;

    static readonly (string Key, string Label, TimeSpan Span, int Buckets)[] Ranges =
    {
        ("15m", "15m", TimeSpan.FromMinutes(15), 90), ("1h", "1h", TimeSpan.FromHours(1), 120), ("24h", "24h", TimeSpan.FromHours(24), 144), ("7d", "7d", TimeSpan.FromDays(7), 168), ("30d", "30d", TimeSpan.FromDays(30), 180)
    };
    static string SettingsPath => Path.Combine(MonitorHost.Folder, "ui.settings");

    public MainWindow()
    {
        Title = "Surface Insight"; if (Brand.WindowIcon() is BitmapFrame ic) Icon = ic; Width = 1220; Height = 820; MinWidth = 900; MinHeight = 600; FontFamily = Ui.Font; FontSize = 14; Background = Brushes.Transparent; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = true, CornerRadius = new CornerRadius(8) });
        LoadSettings(); ApplyTheme(); Content = BuildShell();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; };
        timer.Tick += (_, _) => Tick(); timer.Start();
        Loaded += (_, _) => { Tick(); StartLoad(); EmulationNotice(); ConsumeOpenFlag(); if (Updater.TakeResult() is string upd) Toast(upd); };
        Closing += (_, _) => { timer.Stop(); try { File.Delete(MonitorHost.Heartbeat); } catch { } history.Dispose(); };
        StateChanged += (_, _) => { if (shell != null) shell.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Dispatcher.Invoke(timer.Stop); if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.Invoke(() => { timer.Start(); Tick(); StartLoad(); }); };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (themeMode == "system" && Pal.SystemIsDark() != Pal.Dark) Retheme(); });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); var src = (HwndSource)PresentationSource.FromVisual(this)!; src.CompositionTarget.BackgroundColor = Colors.Transparent; ApplyDwm();
    }
    void ApplyDwm()
    {
        var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
        int dark = Pal.Dark ? 1 : 0, mica = 2, corner = 2; DwmSetWindowAttribute(h, 20, ref dark, 4); DwmSetWindowAttribute(h, 38, ref mica, 4); DwmSetWindowAttribute(h, 33, ref corner, 4);
    }

    // ---------- settings / theme ----------
    void LoadSettings() { try { foreach (var l in File.ReadAllLines(SettingsPath)) { var kv = l.Split('=', 2); if (kv.Length == 2 && kv[0] == "theme" && kv[1] is "system" or "light" or "dark") themeMode = kv[1]; if (kv.Length == 2 && kv[0] == "range" && Ranges.Any(r => r.Key == kv[1])) rangeKey = kv[1]; } } catch { } }
    void SaveSettings() { try { Directory.CreateDirectory(MonitorHost.Folder); File.WriteAllLines(SettingsPath, new[] { "theme=" + themeMode, "range=" + rangeKey }); } catch { } }
    void ApplyTheme() { Pal.Apply(themeMode == "system" ? Pal.SystemIsDark() : themeMode == "dark", Application.Current.Resources); }
    void Retheme() { ApplyTheme(); ApplyDwm(); Content = BuildShell(); ShowPage(); UpdateLive(); }

    // ---------- shell ----------
    UIElement BuildShell()
    {
        shell = new Grid { Background = Pal.Window, Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0) };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        var side = BuildSidebar(); Grid.SetColumn(side, 0); shell.Children.Add(side);
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) }); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(content, 1); shell.Children.Add(content);
        headerHost = new ContentControl { Margin = new Thickness(28, 0, 28, 14) }; Grid.SetRow(headerHost, 1); content.Children.Add(headerHost);
        scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = bodyHost, Padding = new Thickness(28, 0, 22, 24), Focusable = false }; bodyHost.Margin = new Thickness(0, 0, 6, 0); Grid.SetRow(scroll, 2); content.Children.Add(scroll);
        toastText = new TextBlock { Foreground = Pal.TipText, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(toastText, System.Windows.Automation.AutomationLiveSetting.Polite);
        toast = new Border { Background = Pal.Tip, CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 11, 18, 11), Child = toastText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22), Visibility = Visibility.Collapsed };
        Grid.SetColumn(toast, 1); shell.Children.Add(toast);
        return shell;
    }

    UIElement BuildSidebar()
    {
        var p = new StackPanel { Margin = new Thickness(14, 14, 14, 16) };
        var logo = new Image { Source = Brand.RenderMark(68), Width = 34, Height = 34 }; System.Windows.Automation.AutomationProperties.SetName(logo, "Surface Insight");
        p.Children.Add(new Border { Child = Ui.H(10, logo, Ui.V(0, Ui.T("Surface Insight", 15, true), Ui.Dim(Environment.MachineName, 12, false))), Padding = new Thickness(8, 2, 0, 18) });
        foreach (var (key, label, glyph) in new[] { ("now", "Now", ""), ("timeline", "Timeline", ""), ("apps", "Apps", ""), ("network", "Network", ""), ("battery", "Battery", ""), ("investigate", "Investigate", ""), ("settings", "Settings", "") })
        {
            bool on = page == key; var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 22, Foreground = on ? Pal.AccentText : Pal.Text, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Pal.AccentText : Pal.Text, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = Ui.RowBtn(row, () => Go(key), new Thickness(12, 10, 12, 10), on ? Pal.Sel : null, label + (on ? ", current page" : "")); b.Margin = new Thickness(0, 0, 0, 3); p.Children.Add(b);
        }
        sideDot = new Ellipse { Width = 8, Height = 8, Fill = Pal.Ok, VerticalAlignment = VerticalAlignment.Center }; sideHealth = Ui.T("Collector healthy", 13, true); sideDetail = Ui.Dim("Waiting for first sample…", 12);
        var health = Ui.V(6, Ui.H(8, sideDot, sideHealth), sideDetail);
        var card = new Border { Background = Pal.Inset, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(14), Child = health, Margin = new Thickness(14, 0, 14, 16), VerticalAlignment = VerticalAlignment.Bottom };
        card.Margin = new Thickness(0);
        var fanRow = new StackPanel { Orientation = Orientation.Horizontal }; fanRow.Children.Add(new TextBlock { Text = "\uE767", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = Pal.Text2, VerticalAlignment = VerticalAlignment.Center, Width = 22 }); fanRow.Children.Add(new TextBlock { Text = "Mark loud fans", FontSize = 12.5, Foreground = Pal.Text2, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var fan = Ui.RowBtn(fanRow, () => { history.Mark(); Toast("Marked: fans are loud now. The marker appears on the timeline."); }, new Thickness(12, 7, 12, 7), null, "Mark that the fans are loud now"); fan.ToolTip = "Optional: note when you hear the fans, so it can be matched with recorded activity on the timeline."; fan.Margin = new Thickness(0, 0, 0, 8);
        var bottomSide = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 16) }; bottomSide.Children.Add(fan); bottomSide.Children.Add(card);
        var outer = new Grid { Background = Pal.Side }; outer.Children.Add(p); outer.Children.Add(bottomSide);
        return new Border { Child = outer, BorderBrush = Pal.Edge, BorderThickness = new Thickness(0, 0, 1, 0) };
    }

    // ---------- tray -> Settings (update check) ----------
    bool runUpdateCheck;
    /// <summary>The tray monitor asked to open Settings, and (for "Check for updates…") to run the update check right away.</summary>
    void ConsumeOpenFlag()
    {
        string cmd; try { if (!File.Exists(Updater.OpenSettingsFlag)) return; cmd = File.ReadAllText(Updater.OpenSettingsFlag).Trim(); File.Delete(Updater.OpenSettingsFlag); } catch { return; }
        Show(); WindowState = WindowState.Normal; Activate(); runUpdateCheck = cmd == "check"; if (page == "settings") page = "now"; Go("settings");
    }

    // ---------- navigation / range ----------
    void Go(string key, string? appName = null)
    {
        if (page == key && app == appName) return;
        page = key; app = appName; savedScroll = null; group.Set(null); Content = BuildShell(); ShowPage(); UpdateLive();
    }
    void SetRange(string key) { rangeKey = key; customFrom = customTo = null; SaveSettings(); StartLoad(); RebuildHeader(); }
    void SetCustom(DateTime fromUtc, DateTime toUtc) { customFrom = fromUtc; customTo = toUtc; StartLoad(); RebuildHeader(); Toast("Interval selected: " + fromUtc.ToLocalTime().ToString("g") + " – " + toUtc.ToLocalTime().ToString("g") + ". Applications and exports use it."); }
    (DateTime From, DateTime To, int Buckets) Window_()
    {
        if (customFrom is DateTime f && customTo is DateTime t) return (f, t, 120);
        var r = Ranges.First(x => x.Key == rangeKey); var now = DateTime.UtcNow; return (now - r.Span, now, r.Buckets);
    }
    void StartLoad()
    {
        int token = ++loadToken; var (from, to, buckets) = Window_(); data = null; ShowPage();
        Task.Run(() => { try { return RangeData.Load(from, to, buckets); } catch (Exception ex) { MonitorHost.Log("Dashboard load failed: " + ex.Message); return null; } }).ContinueWith(t => Dispatcher.BeginInvoke(() => { if (token != loadToken) return; data = t.Result; lastFullLoad = DateTime.UtcNow; ShowPage(); }));
    }

    // ---------- live tick ----------
    void Tick()
    {
        if (busy) return; busy = true;
        try
        {
            if (showSignal.WaitOne(0)) { Show(); WindowState = WindowState.Normal; Activate(); }
            if (File.Exists(Updater.OpenSettingsFlag)) ConsumeOpenFlag();
            if (File.Exists(Path.Combine(history.Folder, "host.stopped"))) { Close(); return; }
            if (IsVisible && WindowState != WindowState.Minimized) File.WriteAllText(MonitorHost.Heartbeat, "active");
            recent = history.Read(DateTime.UtcNow.AddMinutes(-4)); latest = recent.LastOrDefault(); lastSampleTime = history.LastTime(); UpdateLive();
            if (page == "network" && IsVisible && WindowState != WindowState.Minimized && !(Keyboard.FocusedElement is TextBox)) updatePage?.Invoke();
            if (data != null && customFrom == null && IsVisible && WindowState != WindowState.Minimized)
            {
                if ((DateTime.UtcNow - lastFullLoad).TotalMinutes > 5) { lastFullLoad = DateTime.UtcNow; StartLoad(); return; }
                var since = (data.LastSample ?? data.From).AddMilliseconds(1); var d = data; history.ReadEach(since, null, d.Add); d.Finish();
                if (group.Frac == null && !(bodyHost.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox)) updatePage?.Invoke();
            }
        }
        catch (Exception ex) { MonitorHost.Log("Dashboard tick: " + ex.Message); }
        finally { busy = false; }
    }

    (string Ok, string Stage, string Error) CollectorStatus()
    {
        try { var l = File.ReadAllLines(MonitorHost.StatusFile); string Get(string k) => l.FirstOrDefault(x => x.StartsWith(k + "="))?[(k.Length + 1)..] ?? ""; return (Get("ok"), Get("stage"), Get("error")); } catch { return ("", "", ""); }
    }
    void UpdateLive()
    {
        if (liveText == null && sideHealth == null) return;
        var st = CollectorStatus(); double age = lastSampleTime is DateTime t ? (DateTime.UtcNow - t).TotalSeconds : double.PositiveInfinity; bool live = age <= 25;
        string text = live ? $"Live · {Math.Max(0, (int)age)} s ago" : lastSampleTime is DateTime lt ? "Stale · last " + lt.ToLocalTime().ToString("t") : "No samples yet";
        if (liveText != null) { liveText.Text = text; liveText.Foreground = live ? Pal.OkText : Pal.WarnText; liveBadge!.Background = live ? Pal.OkBg : Pal.WarnBg; liveDot!.Fill = live ? Pal.Ok : Pal.Warn; liveBadge.ToolTip = live ? "The background collector is writing fresh samples." : "Old data is shown. " + (st.Error.Length > 0 ? st.Error : "The background collector has not written a sample recently; it may be asleep or stopped."); System.Windows.Automation.AutomationProperties.SetName(liveBadge, text); }
        if (sideHealth != null) { sideHealth.Text = live ? "Collector healthy" : "Data is stale"; sideDot!.Fill = live ? Pal.Ok : Pal.Warn; sideDetail!.Text = live ? "Last sample " + Math.Max(0, (int)age) + " s ago" : (st.Error.Length > 0 ? st.Error : lastSampleTime is DateTime l2 ? "Last sample " + l2.ToLocalTime().ToString("t") + ". Sleep, or the collector is stopped." : "Start monitoring from the tray."); }
    }

    // ---------- header ----------
    (string Title, string Sub) PageTitle() => page switch
    {
        "now" => ("Now", "Live view of this Surface · refreshed every 5 seconds"),
        "timeline" => ("Timeline", "Synchronized charts for the selected interval · hover to compare, drag to zoom"),
        "apps" => app == null ? ("Apps", "Grouped by executable name · select one to drill down") : (app, data?.App(app) is AppAgg a ? $"{a.Activity} · {a.Inst.Count} instance{(a.Inst.Count == 1 ? "" : "s")} observed" : "No data for this app in the selected interval"),
        "network" => ("Network", "Applications, endpoints and traffic · history stays on this device"),
        "battery" => ("Battery", "Reported health, discharge sessions and collection gaps"),
        "investigate" => ("Investigate", "Administrator captures and Windows battery reports"),
        _ => ("Settings", "Local storage, appearance and startup")
    };
    void RebuildHeader() => headerHost.Content = BuildHeader();
    UIElement BuildHeader()
    {
        var (title, sub) = PageTitle(); var col = new StackPanel();
        if (page == "apps" && app != null) { var back = Ui.Pill("", () => Go("apps"), false, false, Ui.H(6, Ui.T("", 12, true, Pal.AccentText), Ui.T("All apps", 13, true, Pal.AccentText))); back.Background = Brushes.Transparent; back.BorderBrush = Brushes.Transparent; back.HorizontalAlignment = HorizontalAlignment.Left; back.Padding = new Thickness(0, 4, 8, 4); col.Children.Add(back); ((TextBlock)((StackPanel)back.Content).Children[0]).FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); }
        var tt = Ui.T(title, 34, true, null, false, FontWeights.Bold); col.Children.Add(tt); col.Children.Add(Ui.Dim(sub, 13, true));
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Add(UIElement e) { if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 10, 8); bar.Children.Add(e); }
        bool scoped = page is not ("settings" or "investigate"); string sel = customFrom != null ? "" : rangeKey; if (scoped) Add(Ui.Seg(Ranges.Select(r => (r.Label, r.Key)), sel, SetRange));
        var customBtn = Ui.Pill(customFrom != null ? "Custom ✓" : "Custom…", () => { }, false, customFrom != null); customBtn.Click += (_, _) => ShowCustomPopup(customBtn); if (scoped) Add(customBtn);
        liveDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = Pal.Ok }; liveText = Ui.T("Live", 13, true, Pal.OkText);
        liveBadge = new Border { CornerRadius = new CornerRadius(17), Padding = new Thickness(13, 7, 13, 7), Background = Pal.OkBg, Child = Ui.H(8, liveDot, liveText), Focusable = true }; Add(liveBadge);
        Add(Ui.Pill("Export", Export));
        Add(Ui.Pill("", ToggleTheme, false, false, Ui.T("", 14, false, Pal.Text), "Change appearance")); ((TextBlock)bar.Children[^1].GetValue(ContentProperty)!).FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        col.Children.Add(bar); UpdateLive(); return col;
    }
    void ToggleTheme() { themeMode = Pal.Dark ? "light" : "dark"; SaveSettings(); Retheme(); }

    void ShowCustomPopup(FrameworkElement target)
    {
        var from = new TextBox { Width = 170, Text = (customFrom?.ToLocalTime() ?? DateTime.Now.AddHours(-1)).ToString("yyyy-MM-dd HH:mm") }; var to = new TextBox { Width = 170, Text = (customTo?.ToLocalTime() ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm") };
        foreach (var tb in new[] { from, to }) { tb.Background = Pal.Inset; tb.Foreground = Pal.Text; tb.BorderBrush = Pal.Edge; tb.Padding = new Thickness(8, 6, 8, 6); tb.CaretBrush = Pal.Text; }
        System.Windows.Automation.AutomationProperties.SetName(from, "Interval start, local date and time"); System.Windows.Automation.AutomationProperties.SetName(to, "Interval end, local date and time");
        var pop = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, IsOpen = false };
        var err = Ui.T("", 12, false, Pal.Danger, true);
        var apply = Ui.Pill("Apply interval", () => { if (DateTime.TryParse(from.Text, out var a) && DateTime.TryParse(to.Text, out var b) && a < b) { pop.IsOpen = false; SetCustom(a.ToUniversalTime(), b.ToUniversalTime()); } else err.Text = "Enter valid local dates and times, with start before end."; }, true);
        var reset = Ui.Pill("Reset", () => { pop.IsOpen = false; SetRange(rangeKey); });
        pop.Child = new Border { Margin = new Thickness(12), Padding = new Thickness(18), CornerRadius = new CornerRadius(18), Background = Pal.Dark ? new SolidColorBrush(Pal.C("#FF1E2434")) : Brushes.White, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = .3, ShadowDepth = 6 }, Child = Ui.V(10, Ui.Cap("From"), from, Ui.Cap("To"), to, err, Ui.H(8, apply, reset)) };
        pop.IsOpen = true; from.Focus();
    }

    // ---------- toast ----------
    /// <summary>Once per machine: an x64 build on Arm64 Windows runs through emulation, so point to the native download.</summary>
    void EmulationNotice()
    {
        if (!Platform.Emulated) return;
        try { var f = System.IO.Path.Combine(MonitorHost.Folder, "emulation-notice.txt"); if (File.Exists(f)) return; Directory.CreateDirectory(MonitorHost.Folder); File.WriteAllText(f, DateTime.UtcNow.ToString("O")); Toast(Platform.Advice); } catch { }
    }
    public void Toast(string text) { toastText.Text = text; toast.Visibility = Visibility.Visible; toastTimer.Stop(); toastTimer.Start(); }

    // ---------- page host ----------
    void ShowPage()
    {
        if (shell == null) return;
        double? keep = savedScroll ?? (scroll != null && IsLoaded ? scroll.VerticalOffset : null); updatePage = null; RebuildHeader();
        UIElement body;
        if (data == null && page != "network") body = new StackPanel { Margin = new Thickness(0, 40, 0, 0), Children = { Ui.T("Collecting the first measurements…", 16, true, Pal.Text2), new ProgressBar { IsIndeterminate = true, Height = 4, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Foreground = Pal.Accent, Background = Pal.Grid, BorderThickness = new Thickness(0) } } };
        else
        {
            Action upd = () => { };
            body = page switch { "now" => NowPage(out upd), "timeline" => TimelinePage(out upd), "network" => NetworkPage(out upd), "apps" => app == null ? AppsPage(out upd) : AppDetailPage(out upd), "battery" => BatteryPage(out upd), "investigate" => InvestigatePage(out upd), _ => SettingsPage(out upd) };
            updatePage = upd; upd();
        }
        bodyHost.Content = body; savedScroll = null;
        if (keep is double k && scroll != null) scroll.Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(k), DispatcherPriority.Loaded);
    }
}

