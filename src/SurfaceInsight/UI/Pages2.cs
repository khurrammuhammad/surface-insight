using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;
namespace SurfaceInsight;

public sealed partial class MainWindow
{
    // =================================================== BATTERY
    UIElement BatteryPage(out Action update)
    {
        var root = new StackPanel(); var top = new AutoGrid { MinItemWidth = 400 }; var ring = new Ring { Width = 130, Height = 130 };
        var runtime = Ui.T("—", 26, false, null, false, FontWeights.Bold); var rtSub = Ui.Dim("At the last three minutes of draw. Changes with workload.", 12);
        top.Children.Add(Ui.Card(Ui.H(20, ring, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Cap("Runtime estimate"), runtime, rtSub } })));
        var cap = Ui.T("—", 26, false, null, false, FontWeights.Bold); var bar = new ProgressBar { Height = 10, Maximum = 120, Foreground = new SolidColorBrush(Pal.Watts), Background = Pal.Grid, BorderThickness = new Thickness(0), Margin = new Thickness(0, 10, 0, 10) };
        var capSub = Ui.Dim("Capacity is a firmware estimate and can exceed design capacity.", 12);
        top.Children.Add(Ui.Card(Ui.V(2, Ui.Cap("Full-charge capacity"), cap, bar, capSub))); root.Children.Add(top);
        root.Children.Add(Section("Discharge sessions")); var sessions = new AutoGrid { MinItemWidth = 280 }; root.Children.Add(sessions);
        root.Children.Add(Section("Collection gaps")); var gaps = new StackPanel(); root.Children.Add(gaps);
        root.Children.Add(new Border { Margin = new Thickness(0, 18, 0, 0), Child = Ui.Dim("Compare sessions with the same power mode and similar brightness and workload. SleepStudy in Investigate provides Windows’ own sleep-drain report. A collection gap can mean sleep or the app being closed.", 12) });
        sessions.Children.Add(Ui.Dim("Loading sessions…", 13)); int token = loadToken;
        var (from, to, _) = Window_(); var since = from < DateTime.UtcNow.AddHours(-72) ? DateTime.UtcNow.AddHours(-72) : from;
        Task.Run(() => { try { using var h = new History(); return h.Read(since, to); } catch { return new List<Sample>(); } }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (page != "battery" || token != loadToken) return; var raw = t.Result; sessions.Children.Clear(); gaps.Children.Clear();
            var list = new List<List<Sample>>();
            foreach (var a in raw) { if (a.Ac || a.Watts == null) continue; var prev = list.LastOrDefault()?.LastOrDefault(); if (prev == null || (a.Time - prev.Time).TotalSeconds > 150 || prev.Mode != a.Mode || Math.Abs((prev.Brightness ?? 0) - (a.Brightness ?? 0)) > 15) list.Add(new()); list[^1].Add(a); }
            var shown = list.Where(x => x.Count > 2).TakeLast(12).Reverse().ToList(); double maxW = shown.Select(x => x.Average(y => y.Watts!.Value)).DefaultIfEmpty(1).Max();
            foreach (var seg in shown)
            {
                double mins = (seg[^1].Time - seg[0].Time).TotalMinutes, w = seg.Average(x => x.Watts!.Value), drop = seg[0].Percent - seg[^1].Percent;
                var head = new DockPanel(); var dur = Ui.Dim(mins >= 60 ? $"{(int)(mins / 60)} h {mins % 60:F0} m" : $"{mins:F0} min", 13, false); DockPanel.SetDock(dur, Dock.Right); head.Children.Add(dur); head.Children.Add(Ui.T(seg[0].Time.ToLocalTime().ToString("ddd d MMM, HH:mm"), 14, true));
                var meter = new ProgressBar { Height = 8, Maximum = maxW, Value = w, Foreground = Pal.Accent, Background = Pal.Grid, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
                sessions.Children.Add(Ui.Card(Ui.V(6, head, Ui.H(6, Ui.T(w.ToString("F1"), 26, false, null, false, FontWeights.Bold), new TextBlock { Text = "W average", Foreground = Pal.Text2, FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) }), Ui.Dim($"{seg[0].Mode} · brightness {N(seg[0].Brightness, "F0")}% · charge change {(double.IsFinite(drop) ? drop.ToString("F1") : "—")} points", 12), meter), 16, 18));
            }
            if (shown.Count == 0) sessions.Children.Add(Ui.Dim("No complete on-battery sessions in this interval yet.", 13));
            var gl = Analytics.SleepGaps(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Contains("gap")).ToList();
            if (gl.Count == 0) gaps.Children.Add(Ui.Dim("No measurable collection gaps in this interval.", 13));
            foreach (var g in gl.TakeLast(8).Reverse()) { var card = Ui.Card(Ui.H(14, new TextBlock { Text = "", FontFamily = Icons, FontSize = 18, Foreground = Pal.AccentText, VerticalAlignment = VerticalAlignment.Top }, new StackPanel { Children = { Ui.T(g, 13, false, null, true) } }), 16, 18); card.Margin = new Thickness(0, 0, 0, 10); gaps.Children.Add(card); }
        }));
        update = () =>
        {
            var s = latest; double pct = s != null && double.IsFinite(s.Percent) ? s.Percent : 0; ring.Set(pct / 100, Pal.Watts, s != null && double.IsFinite(s.Percent) ? pct.ToString("F0") + "%" : "—", s == null ? "" : s.Ac ? "plugged in" : "on battery");
            runtime.Text = s?.Ac == true ? "Charging / AC" : Runtime(); cap.Text = s?.Full is double f ? (f / 1000).ToString("F1") + " Wh" : "—";
            double ratio = s?.Design > 0 && s.Full is double ff ? ff / s.Design!.Value * 100 : 0; bar.Value = ratio; capSub.Text = $"Design {N(s?.Design / 1000)} Wh · reported ratio {N(ratio > 0 ? ratio : null, "F0")}%. Firmware estimates can exceed design capacity.";
        };
        return root;
    }

    // =================================================== INVESTIGATE
    UIElement InvestigatePage(out Action update)
    {
        var root = new StackPanel(); var grid = new AutoGrid { MinItemWidth = 340 };
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Foreground = Pal.Accent, Background = Pal.Grid, BorderThickness = new Thickness(0), Visibility = diagnosing ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var start = Ui.Pill(diagnosing ? "Capturing…" : "Start 60-second diagnostics", StartDiagnostics, true); start.IsEnabled = !diagnosing; var cancel = Ui.Pill("Cancel capture", () => capture?.Cancel()); cancel.IsEnabled = diagnosing;
        var tools = new WrapPanel(); foreach (var b in new[] { start, cancel, Ui.Pill("Analyze saved trace", AnalyzeSaved), Ui.Pill("Open reports folder", () => OpenFolder(history.Folder)) }) { b.Margin = new Thickness(0, 0, 8, 8); tools.Children.Add(b); }
        grid.Children.Add(Ui.Card(Ui.V(10, Ui.T("Deep capture", 16, true), Ui.Dim("A 60-second administrator capture records CPU and power activity and creates Windows battery and SleepStudy reports. Windows will ask for permission. Cancel saves the partial trace. Raw ETL files can be opened in Windows Performance Analyzer.", 13), tools, progress)));
        var marks = history.Markers(); grid.Children.Add(Ui.Card(Ui.V(8, Ui.T("Recent fan markers", 16, true), Ui.Dim(marks.Length == 0 ? "No fan events marked yet. Use “Mark loud fans” in the sidebar (or the tray menu) when you hear them." : marks, 13)))); root.Children.Add(grid);
        if (Network.NetworkSettings.IsEnabled)
        {
            try
            {
                var (nf, nt, _) = Window_(); using var ns = new Network.NetworkStore(); var tot = ns.Totals(nf, nt); var top = ns.Apps(nf, nt, new Network.NetFilter()).Where(a => a.Sent + a.Recv > 0).Take(3).ToList(); var ncard = new StackPanel();
                ncard.Children.Add(Ui.T("Network evidence for the selected interval", 16, true));
                if (tot.EnhancedCoverage > 0) { ncard.Children.Add(Ui.Dim($"Enhanced monitoring covered {(tot.EnhancedCoverage < .01 ? "<1" : (tot.EnhancedCoverage * 100).ToString("F0"))}% of this interval. Observed transport bytes: sent {Bytes(tot.Sent)}, received {Bytes(tot.Recv)}.", 13)); foreach (var a in top) ncard.Children.Add(Ui.Dim($"• {a.Name}: ↓ {Bytes(a.Recv)} ↑ {Bytes(a.Sent)}", 13)); ncard.Children.Add(Ui.Dim("Compare these periods with battery draw on the Timeline. Network activity can coincide with higher draw, but bytes are not power, and other work or power settings may explain the change.", 12)); }
                else ncard.Children.Add(Ui.Dim(tot.Apps > 0 ? $"Basic monitoring saw {tot.Apps} applications and {tot.Endpoints} remote endpoints in this interval, without byte counts. Start enhanced monitoring on the Network page to measure per-application bytes." : "No network observations in this interval.", 13));
                var nc = Ui.Card(ncard); nc.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(nc);
            }
            catch { }
        }
        root.Children.Add(Section("Last capture summary")); var summary = Path.Combine(history.Folder, "diagnostics-summary.txt");
        if (!File.Exists(summary)) root.Children.Add(Ui.Card(Ui.Dim("No capture summary yet. Run diagnostics or analyze a saved trace to see sampled CPU, driver activity, modeled energy and sleep findings here.", 13)));
        else
        {
            var blocks = File.ReadAllText(summary).Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var cards = new AutoGrid { MinItemWidth = 420 };
            foreach (var blk in blocks) { var lines = blk.Split('\n'); var body = string.Join("\n", lines.Skip(1)); cards.Children.Add(Ui.Card(Ui.V(8, Ui.T(lines[0], 15, true, null, true), body.Length > 0 ? Ui.T(body, 12.5, false, Pal.Text2, true) : new Border()))); }
            root.Children.Add(cards);
        }
        update = () => { };
        return root;
    }
    static void OpenFolder(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
    async void StartDiagnostics()
    {
        if (capture != null) return; capture = new(); diagnosing = true; string cancel = Path.Combine(history.Folder, "cancel.capture"); File.Delete(cancel); if (page == "investigate") ShowPage();
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" }; psi.ArgumentList.Add("--diagnose"); psi.ArgumentList.Add(history.Folder); using var proc = Process.Start(psi);
            Toast("Diagnostic capture running · up to 60 seconds plus report generation."); using var reg = capture.Token.Register(() => File.WriteAllText(cancel, "cancel")); if (proc != null) await proc.WaitForExitAsync();
            Toast("Diagnostics finished. See the summary below and the reports folder.");
        }
        catch (Exception ex) { Toast("Diagnostics could not start: " + ex.Message); }
        finally { capture?.Dispose(); capture = null; diagnosing = false; if (page == "investigate") ShowPage(); }
    }
    async void AnalyzeSaved()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Windows trace|*.etl" }; if (dialog.ShowDialog() != true) return;
        string result = Path.Combine(history.Folder, "imported-trace-summary.txt"); Toast("Analyzing saved trace…"); char q = (char)34;
        var parsed = await Task.Run(() => Diagnostics.Exec(Environment.ProcessPath!, "--analyze " + q + dialog.FileName + q + " " + q + result + q, 180000));
        if (parsed.Code == 0 && File.Exists(result)) { File.WriteAllText(Path.Combine(history.Folder, "diagnostics-summary.txt"), File.ReadAllText(result)); Toast("Saved trace analysis complete."); if (page == "investigate") ShowPage(); }
        else Toast("Trace analysis could not complete: " + parsed.Output);
    }

    // =================================================== SETTINGS
    UIElement SettingsPage(out Action update)
    {
        var root = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        Border Row(string title, string sub, UIElement control, bool last = false)
        {
            var d = new DockPanel(); control.SetValue(DockPanel.DockProperty, Dock.Right); if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center; d.Children.Add(control); d.Children.Add(Ui.V(2, Ui.T(title, 14, true), Ui.Dim(sub, 12)));
            return new Border { Padding = new Thickness(20, 14, 20, 14), BorderBrush = Pal.Edge, BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = d };
        }
        var startup = new System.Windows.Controls.CheckBox { Style = (Style)Application.Current.FindResource("Switch") }; using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.IsChecked = k?.GetValue("SurfaceInsight") != null;
        System.Windows.Automation.AutomationProperties.SetName(startup, "Start at sign-in");
        startup.Click += (_, _) => { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (startup.IsChecked == true) k.SetValue("SurfaceInsight", "\"" + Environment.ProcessPath + "\" --background"); else k.DeleteValue("SurfaceInsight", false); };
        var theme = Ui.Seg(new[] { ("System", "system"), ("Light", "light"), ("Dark", "dark") }, themeMode, k => { themeMode = k; SaveSettings(); Retheme(); });
        var general = Ui.Card(Ui.V(0, Row("Start at sign-in", "Off by default. Choose a permanent folder for the app before enabling it.", startup), Row("Appearance", "Follows Windows and updates live when it changes.", theme), Row("About this build", Platform.Describe() + (Platform.Emulated ? ". " + Platform.Advice : ""), new Border(), true)), 0);
        var st = CollectorStatus(); string health = lastSampleTime is DateTime t ? "Last sample " + t.ToLocalTime().ToString("G") : "No samples yet"; if (st.Error.Length > 0) health += "\n" + st.Error;
        var clear = Ui.Pill("Clear history…", () => { if (MessageBox.Show(this, "Delete all monitoring history and fan markers?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { history.Clear(); recent.Clear(); latest = null; StartLoad(); Toast("History cleared."); } });
        clear.Background = Pal.DangerBg; ((TextBlock)clear.Content).Foreground = Pal.Danger;
        var data_ = Ui.Card(Ui.V(0, Row("Local storage", "History: 30 days · detailed samples for 48 hours · older observations reduced to one per minute. All measurements stay on this device.\n" + history.Folder, Ui.Pill("Open folder", () => OpenFolder(history.Folder))), Row("Collector health", health, Ui.Pill("Open error log", () => { if (File.Exists(MonitorHost.ErrorLog)) OpenFolder(MonitorHost.ErrorLog); else Toast("No collector errors have been logged."); })), Row("Clear all history", "Deletes monitoring history and fan markers. Diagnostic captures are kept.", clear, true)), 0);
        var netSwitch = new System.Windows.Controls.CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = Network.NetworkSettings.IsEnabled }; System.Windows.Automation.AutomationProperties.SetName(netSwitch, "Network monitoring");
        netSwitch.Click += (_, _) => { if (netSwitch.IsChecked == true) { EnableNetwork(); netSwitch.IsChecked = Network.NetworkSettings.IsEnabled; } else { Network.NetworkSettings.SetEnabled(false); Toast("Network monitoring turned off. Stored history is kept until you clear it."); } };
        long netBytes = 0; try { using var ns = new Network.NetworkStore(); netBytes = ns.DbBytes(); } catch { }
        var clearNet = Ui.Pill("Clear network history…", () => { if (MessageBox.Show(this, "Delete stored network history (applications, endpoints and adapter counters)? Battery history is kept.", "Clear network history", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { try { using var ns = new Network.NetworkStore(); ns.Clear(); Toast("Network history cleared."); } catch (Exception ex) { Toast("Could not clear network history: " + ex.Message); } } });
        clearNet.Background = Pal.DangerBg; ((TextBlock)clearNet.Content).Foreground = Pal.Danger;
        var ptrSwitch = new System.Windows.Controls.CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = Network.NetworkSettings.PtrEnabled }; System.Windows.Automation.AutomationProperties.SetName(ptrSwitch, "Reverse DNS lookups"); ptrSwitch.Click += (_, _) => { TogglePtr(); ptrSwitch.IsChecked = Network.NetworkSettings.PtrEnabled; };
        var netCard = Ui.Card(Ui.V(0, Row("Network monitoring (basic)", "Off by default. Records which applications connect to which IP addresses and ports, kept up to 30 days on this device. Not encrypted by Surface Insight; it can reveal which apps and services you use, even without URLs. Per-application bytes and DNS names need an enhanced session, which you start on the Network page (administrator permission, ends when you stop it or quit).", netSwitch), Row("Network history", Bytes(netBytes) + " stored · 100 MB budget · detail 48 hours, then 15-minute summaries", Ui.Pill("Open folder", () => OpenFolder(history.Folder))), Row("Reverse DNS lookups", "Off by default. Looks up the owner name (PTR) of public addresses that have no DNS evidence; this sends DNS queries to your resolver.", ptrSwitch), Row("Clear network history", "Removes stored network rows only. Does not change battery history.", clearNet, true)), 0);        var priv = Ui.Dim("Routine history excludes window titles, command lines and browsing history. Explicit Windows diagnostic traces may include system paths and process metadata. Closing the window keeps monitoring; Quit from the tray icon stops it. No cloud requests or automatic system adjustments occur.", 12);
        root.Children.Add(general); netCard.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(netCard); data_.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(data_); priv.Margin = new Thickness(4, 14, 0, 0); root.Children.Add(priv);
        update = () => { };
        return root;
    }

    static List<(DateTime, string)> AuditNotes(Network.NetworkStore ns, DateTime from, DateTime to) => ns.AuditEvents(from, to).Select(a => (a.Time, a.Kind switch { "block-add" => "Block: " + System.IO.Path.GetFileName(a.Detail), "block-remove" => "Unblock: " + System.IO.Path.GetFileName(a.Detail), "blocks-ended" => "Blocks ended", "enhanced-start" => "Enhanced on", _ => "Enhanced off" })).ToList();

    // =================================================== SCREENSHOT (--shot, used for visual verification)
    public void PrepareShot(string pageKey, string? appName, string? theme, string? range)
    {
        if (theme != null) themeMode = theme; if (range != null && Ranges.Any(r => r.Key == range)) rangeKey = range;
        ApplyTheme(); page = pageKey; app = appName; if (pageKey == "timeline") foreach (var l in new[] { "OneDrive", "Edge", "explorer" }) layers.Add(l); Content = BuildShell(); StartLoad(); UpdateLive();
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject o) { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) { var c = VisualTreeHelper.GetChild(o, i); yield return c; foreach (var x in Descendants(c)) yield return x; } }
    public void Shot(string path)
    {
        var root = (FrameworkElement)Content; root.UpdateLayout();
        if (double.TryParse(Environment.GetEnvironmentVariable("SHOT_SCROLL"), out var scroll)) { var sv = Descendants(root).OfType<ScrollViewer>().Where(x => x.ScrollableHeight > 0).OrderByDescending(x => x.ScrollableHeight).FirstOrDefault(); sv?.ScrollToVerticalOffset(scroll); root.UpdateLayout(); }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(Pal.Dark ? new SolidColorBrush(Pal.C("#101522")) : new SolidColorBrush(Pal.C("#E9EEF8")), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(dv); bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp)); using var fs = File.Create(path); enc.Save(fs);
    }
    public double ContentHeight => scroll?.ExtentHeight ?? 0;

    // =================================================== EXPORT
    async void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "SurfaceInsight", Filter = "HTML summary|*.html|CSV samples|*.csv" }; if (dlg.ShowDialog() != true) return;
        var (from, to, _) = Window_(); string path = dlg.FileName; bool csv = dlg.FilterIndex == 2; string folder = history.Folder; string markers = history.Markers();
        Toast("Exporting…");
        try
        {
            await Task.Run(() =>
            {
                using var h = new History(); var d = h.Read(from, to); var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (csv) { var lines = new List<string> { "UTC,OnAC,Watts,ChargePercent,CPUPercent,GPUPercent,NPUPercent,Mode" }; lines.AddRange(d.Select(s => string.Join(",", s.Time.ToString("O"), s.Ac, s.Watts?.ToString(inv), s.Percent.ToString(inv), s.Cpu.ToString(inv), s.Gpu?.ToString(inv), s.Npu?.ToString(inv), s.Mode))); File.WriteAllLines(path, lines); }
                else File.WriteAllText(path, "<!doctype html><meta charset='utf-8'><title>Surface Insight</title><style>body{font:16px Segoe UI;max-width:900px;margin:50px auto;color:#172336}pre{white-space:pre-wrap;line-height:1.8}</style><h1>Surface Insight</h1><p>Local diagnostic summary</p><pre>" + WebUtility.HtmlEncode(Insights.Findings(d) + "\n" + Analytics.Correlations(d) + "\n" + Analytics.SleepGaps(d) + "\n" + (File.Exists(Path.Combine(folder, "diagnostics-summary.txt")) ? File.ReadAllText(Path.Combine(folder, "diagnostics-summary.txt")) : "") + "\nFan markers\n" + markers) + "</pre>");
            });
            Toast("Export saved: " + path);
        }
        catch (Exception ex) { Toast("Export failed: " + ex.Message); }
    }
}

