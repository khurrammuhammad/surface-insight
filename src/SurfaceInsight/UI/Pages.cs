using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.IO.Path;
namespace SurfaceInsight;

public sealed partial class MainWindow
{
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static string N(double? v, string f = "F1") => v is double d && double.IsFinite(d) ? d.ToString(f) : "—";
    static double[] Tail(double[] a, int n) => a.Length <= n ? a : a[^n..];
    static void Rebuild(Panel p, Func<IEnumerable<UIElement>> make, bool force = false)
    {
        if (!force && (p.IsKeyboardFocusWithin || p.IsMouseOver)) return;
        p.Children.Clear(); foreach (var e in make()) p.Children.Add(e);
    }
    static Border Section(string text) { var b = new Border { Margin = new Thickness(0, 22, 0, 10), Child = Ui.T(text, 17, true) }; return b; }
    TimeSeriesChart NewChart(double height, string name, bool axes = true, string unit = "")
    {
        var c = new TimeSeriesChart { Height = height, Group = group, Axes = axes, AxisUnit = unit }; System.Windows.Automation.AutomationProperties.SetName(c, name); c.IntervalSelected += SetCustom; return c;
    }
    ChartSeries S(string name, double[] v, Color c, string unit, bool fill = false, bool stacked = false, bool dashed = false, bool right = false, double w = 2.2, string fmt = "F1") => new() { Name = name, Values = v, Color = c, Unit = unit, Fill = fill, Stacked = stacked, Dashed = dashed, Right = right, Width = w, Format = fmt };
    static Border ChartCard(string title, string sub, UIElement chart)
    {
        var h = new DockPanel(); var s = Ui.Dim(sub, 12, false); s.VerticalAlignment = VerticalAlignment.Bottom; DockPanel.SetDock(s, Dock.Right); h.Children.Add(s); h.Children.Add(Ui.T(title, 15, true));
        return Ui.Card(Ui.V(10, h, chart));
    }
    string Runtime()
    {
        var valid = recent.Where(a => !a.Ac && a.Watts > 0 && a.Time > DateTime.UtcNow.AddMinutes(-3)).ToList(); if (valid.Count == 0 || latest?.Remaining is not double rem || rem <= 0) return "Unavailable";
        return TimeSpan.FromHours(rem / 1000 / valid.Average(a => a.Watts!.Value)).ToString(@"h\h\ mm\m");
    }

    // =================================================== NOW
    sealed class Tile { public Border Card = null!; public TextBlock Value = null!, Sub = null!; public Sparkline Spark = null!; }
    Tile MakeTile(string cap, string unit)
    {
        var t = new Tile { Value = Ui.T("—", 36, false, null, false, FontWeights.Bold), Sub = Ui.Dim("", 12), Spark = new Sparkline { Height = 38, Margin = new Thickness(0, 8, 0, 0) } };
        var u = Ui.T(unit, 15, false, Pal.Text2); u.VerticalAlignment = VerticalAlignment.Bottom; u.Margin = new Thickness(6, 0, 0, 7);
        t.Card = Ui.Card(Ui.V(3, Ui.Cap(cap), Ui.H(0, t.Value, u), t.Sub, t.Spark)); return t;
    }
    UIElement NowPage(out Action update)
    {
        var root = new StackPanel(); var tiles = new AutoGrid { MinItemWidth = 210 };
        var draw = MakeTile("Device draw", "W"); var charge = MakeTile("Charge", "%"); var cpu = MakeTile("CPU", "%"); var gpu = MakeTile("GPU", "%"); var npuTile = data?.HasNpu == true || latest?.Npu != null ? MakeTile("NPU", "%") : null;
        var netTile = Network.NetworkSettings.IsEnabled ? MakeTile("Network", "") : null;
        foreach (var t in new[] { draw, charge, cpu, gpu }) tiles.Children.Add(t.Card); if (npuTile != null) tiles.Children.Add(npuTile.Card); if (netTile != null) tiles.Children.Add(netTile.Card); root.Children.Add(tiles);

        var grid = new AutoGrid { MinItemWidth = 270, Margin = new Thickness(0, 14, 0, 0) };
        var chart = NewChart(210, "Battery draw over the selected interval", true, "W"); var hero = ChartCard("Battery draw", "Whole device · dashed: screen brightness (right axis)", chart); AutoGrid.SetSpan(hero, 2); grid.Children.Add(hero);
        var power = new StackPanel(); var powerCard = Ui.Card(power); grid.Children.Add(powerCard); root.Children.Add(grid);
        var whyHost = new ContentControl { Margin = new Thickness(0, 14, 0, 0) }; root.Children.Add(whyHost);

        root.Children.Add(Section("What may be driving activity")); root.Children.Add(Ui.Dim("Ranked by average CPU in the selected interval. Activity alone does not establish a cause of drain; compare an interval after the work ends.", 12));
        var drivers = new AutoGrid { MinItemWidth = 250, Margin = new Thickness(0, 12, 0, 0) }; root.Children.Add(drivers);
        root.Children.Add(new Border { Margin = new Thickness(0, 22, 0, 0), Child = Ui.Dim("Fan RPM and CPU temperature are unavailable on this device. Use “Mark loud fans” in the sidebar (or the tray menu) to align what you hear with recorded activity. Watts measure the whole battery-powered device.", 12) });

        update = () =>
        {
            var d = data!; var s = latest;
            draw.Value.Text = s != null && !s.Ac ? N(s.Watts) : "—"; draw.Sub.Text = s == null ? "Waiting for a sample" : s.Ac ? "Plugged in · battery draw not measured" : "Whole device, on battery"; draw.Spark.Set(Tail(d.Watts(), 60), Pal.Watts);
            charge.Value.Text = s != null && double.IsFinite(s.Percent) ? N(s.Percent, "F0") : "—"; charge.Sub.Text = "Runtime " + Runtime() + " at recent draw (estimate)"; charge.Spark.Set(Array.Empty<double>(), Pal.CpuColor);
            cpu.Value.Text = N(s?.Cpu, "F0"); cpu.Sub.Text = $"Normalized to {Environment.ProcessorCount} logical processors"; cpu.Spark.Set(Tail(d.Cpu(), 60), Pal.CpuColor);
            gpu.Value.Text = N(s?.Gpu, "F0"); gpu.Sub.Text = "Busiest engine, not an additive total"; gpu.Spark.Set(Tail(d.Gpu(), 60), Pal.Gpu);
            if (npuTile != null) { npuTile.Value.Text = N(s?.Npu, "F0"); var top = s?.Apps.Where(x => x.Npu > 0.5).OrderByDescending(x => x.Npu).FirstOrDefault(); npuTile.Sub.Text = top != null ? "Used by " + top.Name : (GpuQuery.NpuName() ?? "Neural processing unit") + " · idle"; npuTile.Spark.Set(Tail(d.Npu(), 60), Pal.Npu); }
            if (netTile != null) { var ns = Network.NetworkSettings.ReadStatus(); bool live = ns.LastOkUtc is DateTime nt && (DateTime.UtcNow - nt).TotalSeconds < 45 && ns.Mode is Network.NetMode.Basic or Network.NetMode.Enhanced; netTile.Value.Text = live ? "↓ " + Bytes(ns.RxBps) + "/s" : "—"; netTile.Value.FontSize = 26; netTile.Sub.Text = live ? "↑ " + Bytes(ns.TxBps) + "/s · physical adapter counters" : "Waiting for the tray monitor"; netTile.Spark.Set(Array.Empty<double>(), Pal.Watts); }
            chart.Times = d.Times(); chart.Series = DrawSeries(d); chart.Markers = d.Markers.Select(m => m.Time).ToList(); chart.Notes = DrawExplainer.Events(d).Select(e => (e.Utc, e.Label)).ToList(); chart.Refresh();
            whyHost.Content = WhyCard(d, s);
            power.Children.Clear(); power.Children.Add(Ui.T("Power", 16, true));
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) }; var c1 = Ui.Chip(s == null ? "—" : s.Ac ? "Plugged in" : "On battery", Pal.AccentText, Pal.AccentBg); c1.Margin = new Thickness(0, 0, 8, 6); var c2 = Ui.Chip(s?.Mode ?? "Power mode unavailable", Pal.OkText, Pal.OkBg); chips.Children.Add(c1); chips.Children.Add(c2); power.Children.Add(chips);
            power.Children.Add(Ui.Dim($"Brightness {N(s?.Brightness, "F0")}%\nInterrupt activity {N(s?.Interrupts)}%\nCPU frequency indicator {N(s?.Frequency, "F0")} MHz\nFull-charge capacity {N(s?.Full / 1000)} Wh", 13));
            Rebuild(drivers, () => d.Ranked().Take(3).Select(DriverCard));
        };
        return root;
    }
    /// <summary>Draw with screen brightness on the right axis, so brightness-driven changes are visible.</summary>
    List<ChartSeries> DrawSeries(RangeData d)
    {
        var l = new List<ChartSeries> { S("Draw", d.Watts(), Pal.Watts, "W", fill: true, w: 2.4) }; var br = d.Brightness();
        if (br.Any(double.IsFinite)) l.Add(S("Brightness", br, Pal.C("#D4A017"), "%", dashed: true, right: true, w: 1.6, fmt: "F0")); return l;
    }
    /// <summary>"Why did draw change?" card: compares now with the quieter part of the selected period and estimates each factor's share.</summary>
    UIElement WhyCard(RangeData d, Sample? s)
    {
        var ex = DrawExplainer.Explain(d); var p = new StackPanel(); p.Children.Add(Ui.T("Why draw changed", 16, true));
        if (ex == null || !double.IsFinite(ex.NowW) || !double.IsFinite(ex.BaselineW)) { p.Children.Add(Ui.Dim(s?.Ac == true ? "Plugged in: battery draw is not measured while charging, so changes cannot be explained right now." : "Not enough on-battery measurements in this period yet. Choose a longer period or check back in a few minutes.", 13)); return Ui.Card(p); }
        double diff = ex.NowW - ex.BaselineW; string head = $"Recent draw {ex.NowW:F1} W · " + (Math.Abs(diff) < 0.3 ? "about the same as" : (diff > 0 ? $"{diff:F1} W above" : $"{-diff:F1} W below")) + $" the quieter part of this period ({ex.BaselineW:F1} W)";
        p.Children.Add(Ui.Dim(head, 13)); var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var w in new[] { new GridLength(1.3, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1.1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        int row = 0; void Cell(UIElement e, int c) { Grid.SetRow(e, row); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 5, 8, 5); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition()); Cell(Ui.Cap("Factor"), 0); Cell(Ui.Cap("Quieter → recent"), 1); Cell(Ui.Cap("Estimated effect"), 2); row++;
        double explained = 0;
        foreach (var f in ex.Factors)
        {
            g.RowDefinitions.Add(new RowDefinition()); Cell(Ui.T(f.Name, 13, true), 0); Cell(Ui.T(f.Before + " → " + f.Now, 13, false, f.Delta == 0 ? Pal.Text2 : Pal.Text), 1);
            string eff = f.EstWatts is double e ? (Math.Abs(e) < 0.05 ? "≈ 0 W" : (e > 0 ? "≈ +" : "≈ −") + Math.Abs(e).ToString("F1") + " W") : f.Note.Length > 0 ? f.Note : "not estimated";
            var et = Ui.T(eff, 13, f.EstWatts is double ee && Math.Abs(ee) >= 0.5, f.EstWatts is double e2 && e2 >= 0.5 ? Pal.WarnText : f.EstWatts == null ? Pal.Text2 : Pal.Text, true); Cell(et, 2); if (f.EstWatts is double e3) explained += e3; row++;
        }
        p.Children.Add(g);
        if (ex.ModelOk) { double rest = diff - explained; p.Children.Add(Ui.Dim("Not explained by these factors: " + (rest >= 0 ? "+" : "−") + Math.Abs(rest).ToString("F1") + " W (display panel, radios, background work, measurement noise).", 12)); }
        var note = Ui.Dim(ex.Note, 11.5); note.Margin = new Thickness(0, 6, 0, 0); p.Children.Add(note);
        return Ui.Card(p);
    }
    UIElement DriverCard(AppAgg a)
    {
        var c = Pal.AppColor(a.Name); var spark = new Sparkline { Height = 36, Margin = new Thickness(0, 6, 0, 6) }; spark.Set(data!.AppCpu(a.Name), c);
        string conf = a.AvgCpu > 3 ? "Moderate confidence: sustained CPU activity can contribute to heat and drain." : "Low confidence: activity alone does not establish a drain cause.";
        var top = new DockPanel(); var chip = Ui.ActivityChip(a.Activity); DockPanel.SetDock(chip, Dock.Right); top.Children.Add(chip); top.Children.Add(Ui.H(9, Ui.Dot(c), Ui.T(a.Name, 15, true)));
        var avg = Ui.H(6, Ui.T(a.AvgCpu.ToString("F1") + "%", 24, false, null, false, FontWeights.Bold), new TextBlock { Text = $"average · peak {a.Peak:F1}%", Foreground = Pal.Text2, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
        var foot = new DockPanel(); var go = Ui.T("Drill down  ›", 12, true, Pal.AccentText); DockPanel.SetDock(go, Dock.Right); foot.Children.Add(go); foot.Children.Add(Ui.Dim($"{a.CpuSeconds:N0} CPU-seconds", 12, false));
        var btn = Ui.RowBtn(Ui.V(6, top, avg, spark, Ui.Dim(conf, 11.5), foot), () => Go("apps", a.Name), new Thickness(18), null, $"{a.Name}, average CPU {a.AvgCpu:F1} percent, {a.Activity}. Open details");
        return Ui.Card(btn, 0, 18);
    }

    // =================================================== TIMELINE
    UIElement TimelinePage(out Action update)
    {
        var root = new StackPanel(); var chipHost = new WrapPanel(); var chipCard = Ui.Card(Ui.V(8, Ui.Cap("App layers on the CPU chart"), chipHost), 16, 18); root.Children.Add(chipCard);
        var draw = NewChart(170, "Battery draw timeline", true, "W"); var cpu = NewChart(210, "CPU by application timeline", true, "%"); var gpu = NewChart(140, "GPU timeline", true, "%"); var io = NewChart(140, "Process I/O timeline", true, "MB/s"); var netChart = NewChart(140, "Network traffic timeline", true, "KB/s"); bool netOn = Network.NetworkSettings.IsEnabled;
        var cards = new List<Border> { ChartCard("Battery draw", "Whole-device watts · dashed: screen brightness (right axis) · notes mark brightness, mode and charger changes", draw), ChartCard("CPU by application", "Stacked layers for chosen apps · grey line is total", cpu), ChartCard(data?.HasNpu == true ? "GPU and NPU" : "GPU", data?.HasNpu == true ? "Busiest engine of each, not additive · NPU dashed" : "Busiest engine, not additive", gpu), ChartCard("Process I/O", "Includes non-disk operations", io) }; if (netOn) cards.Add(ChartCard("Network", "Adapter counters (physical) · dashed: apps' observed bytes, enhanced session only", netChart));
        foreach (var c in cards) { c.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(c); }
        var marks = Ui.Dim("", 12); root.Children.Add(Section("Fan markers")); root.Children.Add(marks);
        update = () =>
        {
            var d = data!; var times = d.Times(); var ranked = d.Ranked();
            Rebuild(chipHost, () => ranked.Take(10).Select(a => a.Name).Concat(layers).Distinct(StringComparer.OrdinalIgnoreCase).Select(nm =>
            {
                bool on = layers.Contains(nm); var c = Pal.AppColor(nm); var b = Ui.Pill(nm, () => { if (!layers.Remove(nm)) layers.Add(nm); updatePage?.Invoke(); }, false, on, Ui.H(7, Ui.Dot(c, 9), Ui.T(nm, 13, true, on ? Pal.AccentText : Pal.Text)), nm + (on ? ", shown on chart" : ", hidden"));
                b.Margin = new Thickness(0, 4, 8, 4); return (UIElement)b;
            }), force: true);
            var mk = d.Markers.Select(m => m.Time).ToList();
            draw.Times = times; draw.Series = DrawSeries(d); draw.Markers = mk; var drawEvents = DrawExplainer.Events(d).Select(e => (e.Utc, e.Label)).ToList(); draw.Notes = drawEvents;
            var cs = new List<ChartSeries>(); foreach (var nm in layers.Where(l => d.App(l) != null)) cs.Add(S(nm, d.AppRaw(nm), Pal.AppColor(nm), "%", stacked: true));
            cs.Add(S("Total CPU", d.Cpu(), Pal.C("#8A94A6"), "%", w: 1.6)); cpu.Times = times; cpu.Series = cs; cpu.Markers = mk;
            gpu.Times = times; gpu.Series = new() { S("GPU", d.Gpu(), Pal.Gpu, "%", fill: true) }; if (d.HasNpu) gpu.Series.Add(S("NPU", d.Npu(), Pal.Npu, "%", dashed: true, w: 2)); io.Times = times; io.Series = new() { S("I/O", d.Io(), Pal.Io, "MB/s", fill: true) };
            if (netOn) { try { using var ns = new Network.NetworkStore(); var nb = d.Buckets.Length; var (rx, tx) = ns.AdapterSeries(d.From, d.To, nb); var (ts, tr) = ns.TrafficSeries(d.From, d.To, nb); netChart.Times = times; netChart.Series = new() { S("Download (adapter)", rx.Select(v => v / 1024).ToArray(), Pal.Watts, "KB/s", fill: true), S("Upload (adapter)", tx.Select(v => v / 1024).ToArray(), Pal.Io, "KB/s") }; if (tr.Any(double.IsFinite)) { netChart.Series.Add(S("Apps received (observed)", tr.Select(v => v / 1024).ToArray(), Pal.Watts, "KB/s", dashed: true, w: 1.8)); netChart.Series.Add(S("Apps sent (observed)", ts.Select(v => v / 1024).ToArray(), Pal.Io, "KB/s", dashed: true, w: 1.6)); } netChart.Markers = mk; netChart.Notes = AuditNotes(ns, d.From, d.To); cpu.Notes = netChart.Notes; draw.Notes = drawEvents.Concat(netChart.Notes).ToList(); netChart.Refresh(); } catch { } }
            foreach (var c in new[] { draw, cpu, gpu, io }) c.Refresh();
            var list = history.Markers(); marks.Text = list.Length == 0 ? "No fan events marked yet." : list;
        };
        return root;
    }

    // =================================================== APPS
    string appQuery = "", appFilter = "all", sortKey = "cpu"; bool sortDesc = true;
    UIElement AppsPage(out Action update)
    {
        var root = new StackPanel(); var search = new TextBox { Text = appQuery, Width = 260, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Pal.Text, CaretBrush = Pal.Text, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search applications");
        var sbox = new Border { CornerRadius = new CornerRadius(17), Background = Pal.Inset, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), Padding = new Thickness(12, 5, 12, 5), Child = Ui.H(8, new TextBlock { Text = "", FontFamily = Icons, Foreground = Pal.Text2, VerticalAlignment = VerticalAlignment.Center }, search) };
        var filterHost = new ContentControl(); var bar = new WrapPanel(); sbox.Margin = new Thickness(0, 0, 12, 8); filterHost.Margin = new Thickness(0, 0, 0, 8); bar.Children.Add(sbox); bar.Children.Add(filterHost); root.Children.Add(bar);
        void Filters() => filterHost.Content = Ui.Seg(new[] { ("All", "all"), ("Sustained", "Sustained"), ("Brief spikes", "Brief spikes"), ("Low", "Low") }, appFilter, k => { appFilter = k; Filters(); rows(); });
        var header = new Grid(); var rowsPanel = new StackPanel(); var table = Ui.V(0, header, rowsPanel); table.MinWidth = 900;
        var tableScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = table, Focusable = false };
        var card = Ui.Card(tableScroll, 6, 20); card.Margin = new Thickness(0, 4, 0, 0); root.Children.Add(card);
        root.Children.Add(new Border { Margin = new Thickness(0, 14, 0, 0), Child = Ui.Dim("CPU is normalized to all logical processors. I/O includes non-disk operations. GPU is the busiest engine, not an additive total; NPU is the app's average share of the busiest NPU engine. Apps group by executable name; protected processes may be absent.", 12) });

        void Cols(Grid g) { foreach (var w in AppColumns()) g.ColumnDefinitions.Add(w == 0 ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 } : new ColumnDefinition { Width = new GridLength(w) }); }
        Cols(header); header.Margin = new Thickness(0, 6, 0, 0);
        void Head(int col, string label, string key, bool right = true)
        {
            UIElement el;
            if (key == "") el = new Border { Child = Ui.Cap(label), Padding = new Thickness(col == 0 ? 14 : 0, 8, 0, 8) };
            else { var t = Ui.Cap(label + (sortKey == key ? (sortDesc ? " ↓" : " ↑") : "")); t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left; el = Ui.RowBtn(t, () => { if (sortKey == key) sortDesc = !sortDesc; else { sortKey = key; sortDesc = true; } Heads(); rows(); }, new Thickness(4, 8, 8, 8), null, "Sort by " + label); }
            Grid.SetColumn(el, col); header.Children.Add(el);
        }
        void Heads() { header.Children.Clear(); Head(0, "Application", "name", false); Head(1, "Trend", ""); Head(2, "Avg CPU", "cpu"); Head(3, "Peak", "peak"); Head(4, "CPU-sec", "secs"); Head(5, "Memory", "mem"); Head(6, "I/O", "io"); Head(7, "GPU", "gpu"); if (Npu()) { Head(8, "NPU", "npu"); Head(9, "Activity", "act", false); } else Head(8, "Activity", "act", false); }
        Heads(); Filters();
        void rows() => Rebuild(rowsPanel, () =>
        {
            var d = data!; var q = appQuery.Trim(); IEnumerable<AppAgg> l = d.Apps.Values.Where(a => (q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) && (appFilter == "all" || a.Activity == appFilter));
            Func<AppAgg, IComparable> key = sortKey switch { "name" => a => a.Name, "peak" => a => a.Peak, "secs" => a => a.CpuSeconds, "mem" => a => a.AvgMem, "io" => a => a.AvgIo, "gpu" => a => a.Gpu ?? -1, "npu" => a => a.AvgNpu ?? -1, "act" => a => a.Activity, _ => a => a.AvgCpu };
            var sorted = (sortDesc ? l.OrderByDescending(key) : l.OrderBy(key)).Take(250).ToList();
            if (sorted.Count == 0) return new UIElement[] { new Border { Padding = new Thickness(16), Child = Ui.Dim("No applications match this filter in the selected interval.", 13) } };
            return sorted.Select(AppRow);
        }, force: true);
        search.TextChanged += (_, _) => { appQuery = search.Text; rows(); };
        update = () => RefreshRowsIfIdle(rowsPanel, rows);
        return root;
    }
    bool Npu() => data?.HasNpu == true;
    double[] AppColumns() => Npu() ? new double[] { 0, 110, 72, 64, 88, 84, 84, 62, 62, 112 } : new double[] { 0, 110, 72, 64, 88, 84, 84, 62, 112 };
    void RefreshRowsIfIdle(Panel p, Action rows) { if (!p.IsKeyboardFocusWithin && !p.IsMouseOver && !(Keyboard.FocusedElement is TextBox)) rows(); }
    UIElement AppRow(AppAgg a)
    {
        var g = new Grid(); foreach (var w in AppColumns()) g.ColumnDefinitions.Add(w == 0 ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 } : new ColumnDefinition { Width = new GridLength(w) });
        void Put(UIElement e, int c) { Grid.SetColumn(e, c); g.Children.Add(e); }
        TextBlock R(string t, bool dim = false) { var x = Ui.T(t, 13, false, dim ? Pal.Text2 : Pal.Text); x.HorizontalAlignment = HorizontalAlignment.Right; x.VerticalAlignment = VerticalAlignment.Center; x.Margin = new Thickness(0, 0, 8, 0); return x; }
        var name = Ui.H(10, Ui.Dot(Pal.AppColor(a.Name)), Ui.T(a.Name, 14, true), Ui.Dim("×" + a.Inst.Count, 12, false)); name.VerticalAlignment = VerticalAlignment.Center; Put(name, 0);
        var sp = new Sparkline { Height = 26, Width = 92, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; sp.Set(data!.AppCpu(a.Name), Pal.AppColor(a.Name), false); Put(sp, 1);
        Put(R(a.AvgCpu.ToString("F2") + "%"), 2); Put(R(a.Peak.ToString("F1") + "%", true), 3); Put(R(a.CpuSeconds.ToString("N0")), 4); Put(R(a.AvgMem.ToString("N0") + " MB", true), 5); Put(R((a.AvgIo / 1024).ToString("N0") + " KB/s", true), 6); Put(R(a.Gpu.HasValue ? a.Gpu.Value.ToString("F0") + "%" : "—", true), 7);
        if (Npu()) Put(R(a.AvgNpu > 0 ? a.AvgNpu.Value.ToString(a.AvgNpu >= 10 ? "F0" : "F1") + "%" : "—", true), 8);
        var chip = Ui.ActivityChip(a.Activity); chip.VerticalAlignment = VerticalAlignment.Center; Put(chip, Npu() ? 9 : 8);
        return Ui.RowBtn(g, () => Go("apps", a.Name), new Thickness(14, 9, 6, 9), null, $"{a.Name}, average CPU {a.AvgCpu:F2} percent, {a.Activity}. Open details");
    }

    // =================================================== APP DETAIL
    UIElement AppDetailPage(out Action update)
    {
        var d = data!; var a = d.App(app!); var root = new StackPanel(); update = () => { };
        if (a == null) { root.Children.Add(Ui.Card(Ui.V(8, Ui.T("No data for " + app, 16, true), Ui.Dim("This application was not observed in the selected interval. Choose a longer interval or go back to all apps.", 13)))); return root; }
        var c = Pal.AppColor(a.Name); var stats = new AutoGrid { MinItemWidth = 170 };
        void Stat(string k, string v, string sub) => stats.Children.Add(Ui.Card(Ui.V(2, Ui.Cap(k), Ui.T(v, 28, false, null, false, FontWeights.Bold), Ui.Dim(sub, 12)), 16));
        Stat("Average CPU", a.AvgCpu.ToString("F2") + "%", $"Peak {a.Peak:F1}%"); Stat("CPU-seconds", a.CpuSeconds.ToString("N0"), "Weighted by observed time"); Stat("Share of CPU time", a.Share.ToString("F0") + "%", "Of all observed processes · not watts"); Stat("Memory", a.AvgMem.ToString("N0") + " MB", "Working set across instances"); Stat("General I/O", (a.AvgIo / 1048576).ToString("F2") + " MB/s", "Includes non-disk operations"); Stat("GPU", a.Gpu.HasValue ? a.Gpu.Value.ToString("F0") + "%" : "Unavailable", "Busiest engine observed"); if (d.HasNpu) Stat("NPU", (a.AvgNpu ?? 0).ToString(a.AvgNpu >= 10 ? "F0" : "F1") + "%", a.Npu > 0 ? $"Average · peak {a.Npu:F0}% of the NPU" : "Not observed using the NPU");
        root.Children.Add(stats);

        var chart = NewChart(240, a.Name + " CPU compared with device draw", true, "%"); var legend = Ui.H(18, Ui.H(6, Ui.Dot(c, 10), Ui.Dim(a.Name + " CPU (left axis)", 12, false)), Ui.H(6, Ui.Dot(Pal.Watts, 10), Ui.Dim("Device draw, watts (right axis, dashed)", 12, false)));
        var corr = d.Correlation(a.Name); string ctext = corr is var (r, n) ? $"{a.Name} CPU / device-draw correlation {r:F2} across {n} intervals. {(r > .6 ? "Activity tends to rise with device draw; compare a quiet interval to investigate." : "No strong positive relationship in this interval.")}" : "Not enough variation or on-battery data in this interval to assess a CPU/power relationship.";
        var note = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(14), Background = Pal.Inset, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), Child = Ui.V(2, Ui.T(ctext, 13, true, null, true), Ui.Dim("This is association, not proof: the display and other workloads are confounders. No per-application watts are claimed.", 12)) };
        var main = Ui.Card(Ui.V(10, Ui.T(a.Name + " CPU vs device draw", 16, true), legend, chart, note)); main.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(main);

        var mem = NewChart(130, a.Name + " memory", true, "MB"); var io = NewChart(130, a.Name + " general I/O", true, "MB/s"); var minis = new AutoGrid { MinItemWidth = 320, Margin = new Thickness(0, 14, 0, 0) };
        minis.Children.Add(ChartCard("Memory", "Working set", mem)); minis.Children.Add(ChartCard("General I/O", "Not physical disk throughput", io));
        var npu = d.HasNpu && a.Npu > 0 ? NewChart(130, a.Name + " NPU", true, "%") : null; if (npu != null) minis.Children.Add(ChartCard("NPU", "Share of the busiest NPU engine", npu)); root.Children.Add(minis);
        var netSection = AppNetworkSection(a.Name, out var netUpdate); root.Children.Add(netSection);

        var times = d.Times(); chart.Times = times; chart.Series = new() { S(a.Name + " CPU", d.AppCpu(a.Name), c, "%", fill: true, w: 2.4), S("Device draw", d.Watts(), Pal.Watts, "W", dashed: true, right: true, w: 2) }; chart.Markers = d.Markers.Select(m => m.Time).ToList(); chart.Refresh();
        mem.Times = times; mem.Series = new() { S("Memory", d.AppMemMb(a.Name), Pal.Gpu, "MB", fill: true, fmt: "F0") }; mem.Refresh(); io.Times = times; io.Series = new() { S("I/O", d.AppIoMb(a.Name), Pal.Io, "MB/s", fill: true, fmt: "F2") }; io.Refresh();
        if (npu != null) { npu.Times = times; npu.Series = new() { S("NPU", d.AppNpu(a.Name), Pal.Npu, "%", fill: true) }; npu.Refresh(); }

        var lower = new AutoGrid { MinItemWidth = 340, Margin = new Thickness(0, 14, 0, 0) };
        var inst = new StackPanel(); inst.Children.Add(Ui.T("Instances", 16, true)); inst.Children.Add(Ui.Dim("Each process with this executable name observed in the interval.", 12));
        var hg = new Grid { Margin = new Thickness(0, 10, 0, 0) }; foreach (var w in new double[] { 70, 0, 80, 74, 66 }) hg.ColumnDefinitions.Add(w == 0 ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } : new ColumnDefinition { Width = new GridLength(w) });
        void Cell(Grid g, int col, string t, bool cap, bool right) { UIElement e = cap ? Ui.Cap(t) : Ui.T(t, 13, false, col == 1 || col == 4 ? Pal.Text2 : Pal.Text); if (right && e is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(e, col); g.Children.Add(e); }
        string[] heads = { "PID", "Started", "Observed", "Avg CPU", "Peak" }; for (int i = 0; i < 5; i++) Cell(hg, i, heads[i], true, i >= 2); inst.Children.Add(hg);
        foreach (var i in a.Inst.Values.OrderByDescending(x => x.CpuPctSec).Take(12))
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) }; foreach (var w in new double[] { 70, 0, 80, 74, 66 }) g.ColumnDefinitions.Add(w == 0 ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } : new ColumnDefinition { Width = new GridLength(w) });
            var life = TimeSpan.FromSeconds(i.Sec); Cell(g, 0, i.Pid.ToString(), false, false); Cell(g, 1, new DateTime(i.Start, DateTimeKind.Utc).ToLocalTime().ToString("MMM d HH:mm"), false, false); Cell(g, 2, life.TotalHours >= 1 ? $"{(int)life.TotalHours}h {life.Minutes:D2}m" : $"{life.Minutes}m {life.Seconds:D2}s", false, true); Cell(g, 3, (i.Sec > 0 ? i.CpuPctSec / i.Sec : 0).ToString("F2") + "%", false, true); Cell(g, 4, i.Peak.ToString("F1") + "%", false, true); inst.Children.Add(g);
        }
        lower.Children.Add(Ui.Card(inst));

        var peaks = new StackPanel(); peaks.Children.Add(Ui.T("Peak moments", 16, true)); peaks.Children.Add(Ui.Dim("Highest average CPU for this app. Select one to see that time on the timeline.", 12));
        foreach (var idx in d.PeakMoments(a.Name))
        {
            var t = d.BucketTime(idx).ToLocalTime(); var v = d.AppCpu(a.Name)[idx]; int i2 = idx;
            var row = new DockPanel(); var go = Ui.T("View on timeline  ›", 12, true, Pal.AccentText); DockPanel.SetDock(go, Dock.Right); row.Children.Add(go); row.Children.Add(Ui.H(12, Ui.T(v.ToString("F1") + "%", 15, true), Ui.Dim(t.ToString("ddd d MMM, HH:mm"), 13, false)));
            var b = Ui.RowBtn(row, () => ViewMoment(a.Name, i2), new Thickness(10, 9, 10, 9), null, $"Peak {v:F1} percent at {t:g}. View on timeline"); b.Margin = new Thickness(0, 6, 0, 0); peaks.Children.Add(b);
        }
        var quiet = Ui.Pill("Show this app's quietest period", () => ShowQuietest(a.Name), true); quiet.HorizontalAlignment = HorizontalAlignment.Left; quiet.Margin = new Thickness(0, 16, 0, 0); peaks.Children.Add(quiet);
        peaks.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = Ui.Dim("Compare its quiet and busy periods on the timeline with the same layers. A difference is a lead, not proof.", 12) });
        lower.Children.Add(Ui.Card(peaks)); root.Children.Add(lower);
        update = () =>
        {
            if (data == null) return; var dd = data; times = dd.Times(); chart.Times = times; chart.Series = new() { S(a.Name + " CPU", dd.AppCpu(a.Name), c, "%", fill: true, w: 2.4), S("Device draw", dd.Watts(), Pal.Watts, "W", dashed: true, right: true, w: 2) }; chart.Markers = dd.Markers.Select(m => m.Time).ToList(); chart.Refresh();
            mem.Times = times; mem.Series = new() { S("Memory", dd.AppMemMb(a.Name), Pal.Gpu, "MB", fill: true, fmt: "F0") }; mem.Refresh(); io.Times = times; io.Series = new() { S("I/O", dd.AppIoMb(a.Name), Pal.Io, "MB/s", fill: true, fmt: "F2") }; io.Refresh();
            if (npu != null) { npu.Times = times; npu.Series = new() { S("NPU", dd.AppNpu(a.Name), Pal.Npu, "%", fill: true) }; npu.Refresh(); }
            netUpdate();
        };
        return root;
    }
    void ViewMoment(string name, int idx)
    {
        var d = data!; var t = d.BucketTime(idx); var pad = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(6).Ticks, d.Span.Ticks * 2));
        layers.Clear(); layers.Add(name); page = "timeline"; app = null; Content = BuildShell(); SetCustom(t - pad, t + pad);
    }
    void ShowQuietest(string name)
    {
        var d = data!; var raw = d.AppCpu(name); int w = Math.Max(2, raw.Length / 8); double best = double.MaxValue; int at = -1;
        for (int i = 0; i + w <= raw.Length; i++) { var seg = raw.Skip(i).Take(w).ToList(); if (seg.Any(v => !double.IsFinite(v))) continue; double m = seg.Average(); if (m < best) { best = m; at = i; } }
        if (at < 0) { Toast("There is no continuous period with data to compare in this interval."); return; }
        layers.Clear(); layers.Add(name); page = "timeline"; app = null; Content = BuildShell(); SetCustom(d.BucketTime(at) - d.Span / 2, d.BucketTime(at + w - 1) + d.Span / 2);
    }
}

