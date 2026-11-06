using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SurfaceInsight.Network;
namespace SurfaceInsight;

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ---- rows of the application > hostname tree
public abstract class NetNode : Notify { public long AppId { get; init; } public string Key { get; init; } = ""; }
public sealed class AppNode : NetNode
{
    string name = "", sub = "", chevron = "", toggleTip = "", toggleName = ""; ImageSource? icon; double[]? up, down, act; bool allowed = true, canToggle, expanded; Visibility chevronVis = Visibility.Visible;
    public AppRow Row = null!;
    public string Name { get => name; set => Set(ref name, value); } public string Sub { get => sub; set => Set(ref sub, value); }
    public ImageSource? Icon { get => icon; set { Set(ref icon, value); Raise(nameof(GlyphVisibility)); } } public Visibility GlyphVisibility => icon == null ? Visibility.Visible : Visibility.Collapsed;
    public double[]? Up { get => up; set => Set(ref up, value); } public double[]? Down { get => down; set => Set(ref down, value); } public double[]? Act { get => act; set => Set(ref act, value); }
    public bool Allowed { get => allowed; set => Set(ref allowed, value); } public bool CanToggle { get => canToggle; set => Set(ref canToggle, value); } public string ToggleTip { get => toggleTip; set => Set(ref toggleTip, value); } public string ToggleName { get => toggleName; set => Set(ref toggleName, value); }
    public bool Expanded { get => expanded; set { Set(ref expanded, value); Chevron = value ? "" : ""; } } public string Chevron { get => chevron; set => Set(ref chevron, value); }
    public Visibility ChevronVisibility { get => chevronVis; set => Set(ref chevronVis, value); }
}
public sealed class HostNode : NetNode
{
    string host = "", sub = "", sentText = "", recvText = "", glyph = "";
    public HostGroup Group = null!;
    public string Host { get => host; set => Set(ref host, value); } public string Sub { get => sub; set => Set(ref sub, value); } public string Glyph { get => glyph; set => Set(ref glyph, value); }
    public string SentText { get => sentText; set => Set(ref sentText, value); } public string RecvText { get => recvText; set => Set(ref recvText, value); }
}
public sealed class MoreNode : NetNode { string text = ""; public string Text { get => text; set => Set(ref text, value); } }
public sealed class NetTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (DataTemplate)Application.Current.FindResource(item switch { AppNode => "NetAppNode", HostNode => "NetHostNode", _ => "NetMoreNode" });
}
/// <summary>Endpoints of one application grouped under the hostname chosen by <see cref="NameCorrelator"/> (or the bare IP). Each endpoint belongs to exactly one group, so bytes are never counted twice.</summary>
public sealed record HostGroup(string Key, string Display, NameResult Name, List<DestRow> Dests, long Sent, long Recv, long LastMin, long Obs);

public sealed partial class MainWindow
{
    sealed record NetData(NetTotals Totals, List<AppRow> Apps, Dictionary<long, (double[]? Up, double[]? Down, double[]? Act)> Bars, Dictionary<long, List<HostGroup>> Hosts, List<DestRow> Dests, List<NameRow> Names, List<MapPlace> Places, double[] HistUp, double[] HistDown, string[] HistLabels, bool HistAdapter, List<LiveRow> Live, (DateTime Time, string Kind, string Detail)[] Notes, DateTime From, DateTime To, string? Error);

    long? netAppId; string? netHostKey, netPlaceKey; readonly NetFilter netFilter = new(); int netToken; bool netBlockedOnly; string? netPreselect;
    readonly HashSet<long> netExpanded = new(); readonly HashSet<long> netShowAll = new(); readonly HashSet<string> netCollapsed = new(); NetData? netLast;
    readonly ObservableCollection<NetNode> netRows = new(); bool shotPicked;

    static string Bytes(double v) => v >= 1073741824 ? (v / 1073741824).ToString("F2") + " GB" : v >= 1048576 ? (v / 1048576).ToString("F1") + " MB" : v >= 1024 ? (v / 1024).ToString("F0") + " KB" : v.ToString("F0") + " B";
    public static string BytesText(double v) => Bytes(v);
    static string Local(long minute) => NetworkStore.FromMinute(minute).ToLocalTime().ToString("MMM d HH:mm");
    static string ProtoName(int p) => p == 6 ? "TCP" : p == 17 ? "UDP" : p.ToString();
    static string Service(int proto, int port) => (proto, port) switch { (6, 443) => "https (443)", (6, 80) => "http (80)", (_, 53) => "dns (53)", (6, 853) => "dns-over-tls (853)", (6, 993) => "imaps (993)", (6, 587) => "smtp (587)", (6, 22) => "ssh (22)", (6, 3389) => "rdp (3389)", (17, 123) => "ntp (123)", (17, 5353) => "mdns (5353)", (17, 1900) => "ssdp (1900)", _ => port.ToString() };
    static List<long> ParseIds(string s) => s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : 0).ToList();
    static void WriteNetCommand(string cmd) { try { Directory.CreateDirectory(MonitorHost.Folder); File.WriteAllText(System.IO.Path.Combine(MonitorHost.Folder, "network.cmd"), cmd); } catch { } }

    static List<HostGroup> GroupHosts(IEnumerable<DestRow> dests, List<NameRow> names)
    {
        var groups = new Dictionary<string, (string Display, NameResult Name, List<DestRow> D)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dests)
        {
            var nr = NameCorrelator.Resolve(d.Ip, ParseIds(d.AppIds), d.FirstMin, d.LastMin, names); string key = nr.Name ?? d.Ip;
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (key, nr, new List<DestRow>()); g.D.Add(d);
        }
        return groups.Select(kv => new HostGroup(kv.Key, kv.Value.Display, kv.Value.Name, kv.Value.D, kv.Value.D.Sum(x => x.Sent), kv.Value.D.Sum(x => x.Recv), kv.Value.D.Max(x => x.LastMin), kv.Value.D.Sum(x => x.Observations)))
            .OrderByDescending(g => g.Sent + g.Recv).ThenByDescending(g => g.Name.Name != null).ThenByDescending(g => g.Obs).ToList();
    }

    // ===================================================================== page
    UIElement NetworkPage(out Action update)
    {
        if (!NetworkSettings.IsEnabled && !HasNetworkHistory()) { update = () => { }; return EnableCard(); }
        var outer = new Grid(); var status = new ContentControl(); var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(body, 1); outer.Children.Add(status); outer.Children.Add(body);
        void FitHeight() { if (scroll != null && outer.IsLoaded) outer.Height = Math.Max(620, scroll.ViewportHeight - 6); }
        outer.Loaded += (_, _) => FitHeight(); if (scroll != null) scroll.SizeChanged += (_, _) => FitHeight();

        // ---------- left: search, tree, totals, histogram, time navigation
        var search = new TextBox { Text = netFilter.Search, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Pal.Text, CaretBrush = Pal.Text, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search applications, hostnames, IP addresses and ports");
        var filterBtn = Ui.Pill("", () => { }, false, false, new TextBlock { Text = "", FontFamily = Icons, FontSize = 14, Foreground = Pal.Text }, "Filters"); filterBtn.Padding = new Thickness(9, 6, 9, 6);
        var searchRow = new DockPanel(); DockPanel.SetDock(filterBtn, Dock.Right); searchRow.Children.Add(filterBtn);
        var glass = new TextBlock { Text = "", FontFamily = Icons, Foreground = Pal.Text2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; var sdock = new DockPanel(); DockPanel.SetDock(glass, Dock.Left); sdock.Children.Add(glass); sdock.Children.Add(search);
        searchRow.Children.Add(new Border { CornerRadius = new CornerRadius(15), Background = Pal.Inset, BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), Child = sdock });
        var tree = new ListBox { Style = (Style)Application.Current.FindResource("NetList"), ItemsSource = netRows, ItemTemplateSelector = new NetTemplateSelector(), ItemContainerStyle = (Style)Application.Current.FindResource("NetTreeItem") };
        System.Windows.Automation.AutomationProperties.SetName(tree, "Applications and the hostnames they connected to");
        var sentPill = Pill2("sent", MiniBars.SentColor, out var sentVal); var recvPill = Pill2("received", MiniBars.RecvColor, out var recvVal);
        var pills = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 8) }; pills.Children.Add(sentPill); pills.Children.Add(recvPill);
        var hist = new UpDownBars { Height = 110 }; var histNote = Ui.Dim("", 11, true); histNote.Margin = new Thickness(0, 4, 0, 0);
        var navLabel = Ui.T("", 12.5, true); navLabel.HorizontalAlignment = HorizontalAlignment.Center; navLabel.VerticalAlignment = VerticalAlignment.Center;
        Button NavBtn(string glyph, string name, Action a) { var b = Ui.Pill("", a, false, false, new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = Pal.Text }, name); b.Padding = new Thickness(9, 5, 9, 5); return b; }
        var nav = new Grid { Margin = new Thickness(0, 8, 0, 0) }; foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) nav.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var bPrev = NavBtn("", "Previous period", () => ShiftWindow(-1)); var bOut = NavBtn("", "Zoom out to a longer period", () => ZoomRange(+1)); var bIn = NavBtn("", "Zoom in to a shorter period", () => ZoomRange(-1)); var bNext = NavBtn("", "Next period", () => ShiftWindow(+1));
        bOut.Margin = new Thickness(4, 0, 4, 0); bIn.Margin = new Thickness(4, 0, 4, 0); Grid.SetColumn(bOut, 1); Grid.SetColumn(navLabel, 2); Grid.SetColumn(bIn, 3); Grid.SetColumn(bNext, 4); nav.Children.Add(bPrev); nav.Children.Add(bOut); nav.Children.Add(navLabel); nav.Children.Add(bIn); nav.Children.Add(bNext);
        var bottom = Ui.V(0, pills, hist, histNote, nav);
        var left = new DockPanel { LastChildFill = true }; DockPanel.SetDock(searchRow, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom); searchRow.Margin = new Thickness(0, 0, 0, 8); left.Children.Add(searchRow); left.Children.Add(bottom); left.Children.Add(tree);
        var leftCard = Ui.Card(left, 12, 20);

        // ---------- centre: map
        var map = new MapView(); System.Windows.Automation.AutomationProperties.SetName(map, "Map of approximate destination locations. Arrow keys pan, plus and minus zoom, Home resets.");
        var resetMap = Ui.Pill("Fit", () => map.ResetView(), false, false, null, "Fit map to destinations"); resetMap.HorizontalAlignment = HorizontalAlignment.Left; resetMap.VerticalAlignment = VerticalAlignment.Top; resetMap.Margin = new Thickness(12); resetMap.Padding = new Thickness(12, 5, 12, 5);
        var mapGrid = new Grid(); mapGrid.Children.Add(map); mapGrid.Children.Add(resetMap);
        var mapCard = new Border { CornerRadius = new CornerRadius(20), BorderBrush = Pal.Edge, BorderThickness = new Thickness(1), Child = mapGrid };
        mapCard.SizeChanged += (_, e) => mapGrid.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), 19, 19);

        // ---------- right: inspector
        var inspectorHost = new ContentControl(); var inspector = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = inspectorHost, Padding = new Thickness(0, 0, 4, 0) };
        var rightCard = Ui.Card(inspector, 16, 20);

        // ---------- responsive three-pane layout
        void Layout(double w)
        {
            body.Children.Clear(); body.ColumnDefinitions.Clear(); body.RowDefinitions.Clear();
            void Place(FrameworkElement e, int r, int c, Thickness m, int rowSpan = 1) { Grid.SetRow(e, r); Grid.SetColumn(e, c); Grid.SetRowSpan(e, rowSpan); e.Margin = m; body.Children.Add(e); }
            if (w >= 1180) { body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) }); Place(leftCard, 0, 0, new Thickness(0)); Place(mapCard, 0, 1, new Thickness(12, 0, 12, 0)); Place(rightCard, 0, 2, new Thickness(0)); }
            else if (w >= 820) { body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); Place(leftCard, 0, 0, new Thickness(0), 2); Place(mapCard, 0, 1, new Thickness(12, 0, 0, 6)); Place(rightCard, 1, 1, new Thickness(12, 6, 0, 0)); }
            else { body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); Place(leftCard, 0, 0, new Thickness(0)); Place(mapCard, 1, 0, new Thickness(0, 12, 0, 6)); Place(rightCard, 2, 0, new Thickness(0, 6, 0, 0)); }
        }
        double lastW = 0; outer.SizeChanged += (_, e) => { double w = e.NewSize.Width; if (lastW == 0 || (lastW >= 1180) != (w >= 1180) || (lastW >= 820) != (w >= 820)) Layout(w); lastW = w; };

        Action refresh = null!;
        // ---------- filters menu
        var menu = new ContextMenu();
        void MenuItems()
        {
            menu.Items.Clear();
            void Item(string text, bool on, Action toggle) { var mi = new MenuItem { Header = text, IsCheckable = true, IsChecked = on }; mi.Click += (_, _) => { toggle(); refresh(); }; menu.Items.Add(mi); }
            Item("TCP only", netFilter.Proto == 6, () => netFilter.Proto = netFilter.Proto == 6 ? 0 : 6); Item("UDP only", netFilter.Proto == 17, () => netFilter.Proto = netFilter.Proto == 17 ? 0 : 17); menu.Items.Add(new Separator());
            Item("IPv4 only", netFilter.Family == 4, () => netFilter.Family = netFilter.Family == 4 ? 0 : 4); Item("IPv6 only", netFilter.Family == 6, () => netFilter.Family = netFilter.Family == 6 ? 0 : 6); menu.Items.Add(new Separator());
            foreach (var (label, scopes) in new[] { ("Public addresses", new[] { IpScope.Public }), ("Private addresses", new[] { IpScope.Private, IpScope.CarrierGrade }), ("Loopback", new[] { IpScope.Loopback }), ("Link-local", new[] { IpScope.LinkLocal }), ("Multicast and special", new[] { IpScope.Multicast, IpScope.Special }) })
            { bool on = scopes.All(x => netFilter.Scopes.Contains((int)x)); Item(label, on, () => { foreach (var x in scopes) { if (on) netFilter.Scopes.Remove((int)x); else netFilter.Scopes.Add((int)x); } }); }
            menu.Items.Add(new Separator()); Item("Blocked applications only", netBlockedOnly, () => netBlockedOnly = !netBlockedOnly);
            var clear = new MenuItem { Header = "Clear filters" }; clear.Click += (_, _) => { netFilter.Proto = netFilter.Family = 0; netFilter.Scopes.Clear(); netBlockedOnly = false; refresh(); }; menu.Items.Add(clear);
        }
        filterBtn.Click += (_, _) => { MenuItems(); menu.PlacementTarget = filterBtn; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };

        // ---------- apply data to the tree, map, histogram and inspector
        bool suppress = false;
        void Apply(NetData d)
        {
            netLast = d; status.Content = NetStatusStrip(d); var bs = NetworkSettings.ReadBlocks(); var blocked = new HashSet<string>(bs.Blocks.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var st = NetworkSettings.ReadStatus(); bool enhanced = st.Enhanced == "Active" && st.EnhancedOkUtc is DateTime eo && (DateTime.UtcNow - eo).TotalSeconds < 20; string q = netFilter.Search.Trim();
            // flatten the tree, reusing row objects so selection, focus and scroll survive refreshes
            var old = netRows.ToDictionary(r => r.Key); var rows = new List<NetNode>();
            foreach (var a in d.Apps)
            {
                string key = "a" + a.Id; var n = old.TryGetValue(key, out var o) && o is AppNode on ? on : new AppNode { AppId = a.Id, Key = key }; n.Row = a; n.Name = a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a.Name[..^4] : a.Name;
                bool isBlocked = blocked.Contains(a.Path); string bytes = a.Sent + a.Recv > 0 ? "↑ " + Bytes(a.Sent) + "   ↓ " + Bytes(a.Recv) : a.LiveConns > 0 ? a.LiveConns + " live connection" + (a.LiveConns == 1 ? "" : "s") : a.Endpoints + " endpoint" + (a.Endpoints == 1 ? "" : "s");
                n.Sub = (isBlocked ? "Blocked · " : "") + bytes; if (n.Icon == null && a.Resolved) n.Icon = AppMeta.Icon(a.Path);
                if (d.Bars.TryGetValue(a.Id, out var bar)) { n.Up = bar.Up; n.Down = bar.Down; n.Act = bar.Act; } else { n.Up = n.Down = n.Act = null; }
                string? why = PolicyRules.Reject(a.Resolved ? a.Path : null, new[] { Environment.ProcessPath! }); n.Allowed = !isBlocked; n.CanToggle = isBlocked ? enhanced : enhanced && why == null;
                n.ToggleTip = isBlocked ? "Temporarily blocked by Surface Insight. Switch on to remove the block." : !enhanced ? "Turn on “Show hostnames + bytes” (enhanced monitoring) to block applications for the session." : why ?? "On = not blocked by Surface Insight. Switch off to block this program for this session.";
                n.ToggleName = n.Name + (isBlocked ? ", temporarily blocked" : ", not blocked by Surface Insight");
                bool hasHosts = d.Hosts.TryGetValue(a.Id, out var hosts); bool autoOpen = q.Length > 0 && hasHosts && hosts!.Any(h => h.Display.Contains(q, StringComparison.OrdinalIgnoreCase));
                n.Expanded = (netExpanded.Contains(a.Id) || autoOpen) && hasHosts; n.ChevronVisibility = a.Endpoints > 0 ? Visibility.Visible : Visibility.Hidden; rows.Add(n);
                if (!n.Expanded) continue;
                var shown = q.Length > 0 && !a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? hosts!.Where(h => h.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || h.Dests.Any(x => x.Ip.Contains(q))).ToList() : hosts!;
                int limit = netShowAll.Contains(a.Id) ? 400 : 6;
                foreach (var h in shown.Take(limit))
                {
                    string hk = "h" + a.Id + "|" + h.Key; var hn = old.TryGetValue(hk, out var oh) && oh is HostNode ohn ? ohn : new HostNode { AppId = a.Id, Key = hk }; hn.Group = h; hn.Host = h.Display;
                    var ips = h.Dests.Select(x => x.Ip).Distinct().ToList(); var place = ips.Select(Geo.Lookup).FirstOrDefault(g => g != null);
                    hn.Sub = (h.Name.Name != null ? (ips.Count == 1 ? ips[0] : ips.Count + " addresses") + " · " + h.Name.Label : IpClassifier.Label(h.Dests[0].Scope) + " · " + h.Name.Label) + (place != null ? " · " + place.Label : "");
                    hn.Glyph = h.Name.Name != null ? "" : ""; bool hb = h.Sent + h.Recv > 0; hn.SentText = hb ? "↑ " + Bytes(h.Sent) : ""; hn.RecvText = hb ? "↓ " + Bytes(h.Recv) : h.Obs + " seen"; rows.Add(hn);
                }
                if (shown.Count > limit) { string mk = "m" + a.Id; var mn = old.TryGetValue(mk, out var om) && om is MoreNode omn ? omn : new MoreNode { AppId = a.Id, Key = mk }; mn.Text = (shown.Count - limit) + " more…"; rows.Add(mn); }
            }
            var selKey = (tree.SelectedItem as NetNode)?.Key ?? (netHostKey ?? (netAppId is long sid ? "a" + sid : null)); suppress = true;
            for (int i = 0; i < rows.Count; i++) { if (i < netRows.Count) { if (!ReferenceEquals(netRows[i], rows[i])) { int at = netRows.IndexOf(rows[i]); if (at > i) netRows.Move(at, i); else netRows.Insert(i, rows[i]); } } else netRows.Add(rows[i]); }
            while (netRows.Count > rows.Count) netRows.RemoveAt(netRows.Count - 1);
            tree.SelectedItem = selKey == null ? null : netRows.FirstOrDefault(r => r.Key == selKey); suppress = false;

            // totals, histogram, navigation
            var app = d.Apps.FirstOrDefault(x => x.Id == netAppId); long sent = app?.Sent ?? d.Totals.Sent, recv = app?.Recv ?? d.Totals.Recv; bool hasBytes = d.Totals.EnhancedCoverage > 0;
            sentVal.Text = hasBytes ? Bytes(sent) : "—"; recvVal.Text = hasBytes ? Bytes(recv) : "—";
            hist.Set(d.HistUp, d.HistDown, d.HistLabels);
            histNote.Text = d.HistAdapter ? "Adapter counters (all traffic). Per-app bytes need “Show hostnames + bytes”." : hasBytes ? (app != null ? app.Name + " · " : "") + "observed transport bytes · enhanced monitoring covered " + (d.Totals.EnhancedCoverage < .01 ? "<1" : (d.Totals.EnhancedCoverage * 100).ToString("F0")) + "% of this period" : "No byte data in this period.";
            navLabel.Text = RangeLabel(); bNext.IsEnabled = customFrom != null;
            // map
            var oldKeys = map.Places.Select(p => p.Key).ToHashSet(); bool refit = !oldKeys.SetEquals(d.Places.Select(p => p.Key)) || d.Places.Any(p => p.Highlight) != map.Places.Any(p => p.Highlight);
            map.Selected = netPlaceKey; map.SetPlaces(d.Places, refit && netPlaceKey == null);
            inspectorHost.Content = Inspector(d);
            // screenshot hook (--shot with SHOT_SELECT=<app name>[|<host>]): select an application and expand it
            if (Environment.GetEnvironmentVariable("SHOT_SELECT") is { Length: > 0 } pick && !shotPicked && d.Apps.FirstOrDefault(a => a.Name.StartsWith(pick.Split('|')[0], StringComparison.OrdinalIgnoreCase)) is AppRow pa)
            { shotPicked = true; netAppId = pa.Id; netExpanded.Add(pa.Id); var parts = pick.Split('|'); if (parts.Length > 1) netHostKey = "h" + pa.Id + "|" + parts[1]; refresh(); }
        }
        refresh = () =>
        {
            int token = ++netToken; var (from, to, _) = Window_(); string q = netFilter.Search.Trim().ToLowerInvariant();
            var f = new NetFilter { Search = netFilter.Search, Proto = netFilter.Proto, Family = netFilter.Family, Scopes = new(netFilter.Scopes), OnlyPaths = netBlockedOnly ? new HashSet<string>(NetworkSettings.ReadBlocks().Blocks.Select(x => x.Path), StringComparer.OrdinalIgnoreCase) : null };
            var noSearch = new NetFilter { Proto = f.Proto, Family = f.Family, Scopes = f.Scopes, OnlyPaths = f.OnlyPaths };
            long? app = netAppId; var expanded = netExpanded.ToList(); string? hostKey = netHostKey;
            Task.Run(() =>
            {
                try
                {
                    using var s = new NetworkStore(); var apps = s.Apps(from, to, f); var tot = s.Totals(from, to); var bars = s.AppBars(from, to, 8);
                    var hostApps = expanded.Concat(app is long a0 ? new[] { a0 } : Array.Empty<long>()).Concat(q.Length > 0 ? apps.Take(15).Select(x => x.Id) : Array.Empty<long>()).Distinct().Where(id => apps.Any(x => x.Id == id)).Take(20).ToList();
                    var perApp = hostApps.ToDictionary(id => id, id => s.Destinations(from, to, id, noSearch, "bytes", null, 400));
                    var all = s.Destinations(from, to, null, noSearch, "bytes", null, 600);
                    var names = s.NameRows(perApp.Values.SelectMany(x => x).Concat(all).Select(x => x.Ip), from, to);
                    var hosts = perApp.ToDictionary(kv => kv.Key, kv => GroupHosts(kv.Value, names));
                    // map places: all destinations, highlighted for the selected app or hostname
                    var hiIps = new HashSet<string>(); if (app is long sa && perApp.TryGetValue(sa, out var sd)) { var sel = hostKey != null ? hosts[sa].Where(h => "h" + sa + "|" + h.Key == hostKey).SelectMany(h => h.Dests) : sd; foreach (var x in sel) hiIps.Add(x.Ip); }
                    var allForMap = app is long sa2 && perApp.TryGetValue(sa2, out var sd2) ? all.Concat(sd2).GroupBy(x => x.Gid).Select(g => g.First()).ToList() : all;
                    var places = new Dictionary<string, MapPlace>();
                    foreach (var dst in allForMap)
                    {
                        var g = Geo.Lookup(dst.Ip); if (g == null) continue; string key = g.CountryCode + "|" + g.City + "|" + Math.Round(g.Lat, 1) + "|" + Math.Round(g.Lon, 1);
                        if (!places.TryGetValue(key, out var p)) places[key] = p = new MapPlace { Key = key, Label = g.Label, Country = g.Country, Lat = g.Lat, Lon = g.Lon };
                        p.Bytes += dst.Sent + dst.Recv; p.Endpoints++; if (hiIps.Contains(dst.Ip)) p.Highlight = true; var nr = NameCorrelator.Resolve(dst.Ip, ParseIds(dst.AppIds), dst.FirstMin, dst.LastMin, names); var label = nr.Name ?? dst.Ip; if (!p.Hosts.Contains(label) && p.Hosts.Count < 20) p.Hosts.Add(label);
                    }
                    // histogram: observed bytes per slice, or adapter counters when no enhanced data
                    const int nb = 24; double sliceSec = Math.Max(1, (to - from).TotalSeconds / nb); var (ts, tr) = s.TrafficSeries(from, to, nb, app); bool adapter = false;
                    double[] hu = ts.Select(v => double.IsFinite(v) ? v * sliceSec : double.NaN).ToArray(), hd = tr.Select(v => double.IsFinite(v) ? v * sliceSec : double.NaN).ToArray();
                    if (!hu.Any(double.IsFinite) && app == null) { var (rx, tx) = s.AdapterSeries(from, to, nb); hu = tx.Select(v => double.IsFinite(v) ? v * sliceSec : 0).ToArray(); hd = rx.Select(v => double.IsFinite(v) ? v * sliceSec : 0).ToArray(); adapter = true; }
                    var labels = Enumerable.Range(0, nb).Select(i => { var a = (from + TimeSpan.FromSeconds(sliceSec * i)).ToLocalTime(); var b = a.AddSeconds(sliceSec); return a.ToString(sliceSec >= 86400 ? "MMM d" : "MMM d HH:mm") + " – " + b.ToString(sliceSec >= 86400 ? "MMM d" : "HH:mm"); }).ToArray();
                    var live = app is long la ? s.Live(la, new NetFilter()) : new List<LiveRow>();
                    return new NetData(tot, apps, bars, hosts, app is long sa3 && perApp.TryGetValue(sa3, out var mine) ? mine : all, names, places.Values.ToList(), hu, hd, labels, adapter, live, s.CoverageNotes(from, to), from, to, null);
                }
                catch (Exception ex) { MonitorHost.Log("Network query failed: " + ex.Message); return new NetData(new NetTotals(0, 0, 0, 0, 0, 0), new(), new(), new(), new(), new(), new(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<string>(), false, new(), Array.Empty<(DateTime, string, string)>(), from, to, ex.Message); }
            }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (token != netToken || page != "network") return; var d = t.Result;
                if (d.Error != null) { status.Content = Ui.Card(Ui.V(6, Ui.T("Network history could not be read", 16, true), Ui.Dim(d.Error, 13))); return; }
                Apply(d);
            }));
        };

        // ---------- interaction
        tree.SelectionChanged += (_, _) =>
        {
            if (suppress) return;
            switch (tree.SelectedItem)
            {
                case AppNode an: netAppId = an.AppId; netHostKey = null; netPlaceKey = null; break;
                case HostNode hn: netAppId = hn.AppId; netHostKey = hn.Key; netPlaceKey = null; break;
                default: return;
            }
            refresh();
        };
        tree.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not NetNode node) return;
            switch (fe.Tag as string)
            {
                case "expand": if (!netExpanded.Remove(node.AppId)) netExpanded.Add(node.AppId); refresh(); e.Handled = true; break;
                case "more": netShowAll.Add(node.AppId); refresh(); e.Handled = true; break;
                case "toggle":
                    // The switch only shows confirmed state. Undo the click's visual flip without breaking the binding
                    // (assigning IsChecked directly replaced the binding, so later confirmations never reached the switch).
                    if (node is AppNode a && fe is CheckBox cb) { cb.SetCurrentValue(ToggleButton.IsCheckedProperty, a.Allowed); if (a.Allowed) ConfirmBlock(a.Row); else RunBlock("block-remove", a.Row); }
                    e.Handled = true; break;
            }
        }));
        tree.PreviewKeyDown += (_, e) =>
        {
            if (tree.SelectedItem is not AppNode a) return;
            if (e.Key == Key.Right && !netExpanded.Contains(a.AppId)) { netExpanded.Add(a.AppId); refresh(); e.Handled = true; }
            else if (e.Key == Key.Left && netExpanded.Contains(a.AppId)) { netExpanded.Remove(a.AppId); refresh(); e.Handled = true; }
        };
        var allBtn = Ui.Pill("All", () => { netAppId = null; netHostKey = null; netPlaceKey = null; tree.SelectedItem = null; refresh(); }, false, false, null, "Show all applications"); allBtn.Padding = new Thickness(10, 6, 10, 6); allBtn.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(allBtn, Dock.Right); searchRow.Children.Insert(1, allBtn);
        map.PlaceClicked += p => { netPlaceKey = p?.Key; if (netLast != null) inspectorHost.Content = Inspector(netLast); };
        var searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; searchTimer.Tick += (_, _) => { searchTimer.Stop(); refresh(); };
        search.TextChanged += (_, _) => { netFilter.Search = search.Text; searchTimer.Stop(); searchTimer.Start(); };
        if (netPreselect != null) { search.Text = netPreselect; netFilter.Search = netPreselect; netPreselect = null; }

        Layout(1300); update = () => refresh();
        return outer;

        static Border Pill2(string caption, Color c, out TextBlock value)
        {
            value = Ui.T("—", 15, true); value.HorizontalAlignment = HorizontalAlignment.Right;
            var g = new DockPanel(); var cap = new TextBlock { Text = (caption == "sent" ? "↑ " : "↓ ") + caption, FontSize = 12, Foreground = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(cap, Dock.Left); g.Children.Add(cap); g.Children.Add(value);
            return new Border { Background = new SolidColorBrush(Pal.Alpha(c, Pal.Dark ? .22 : .15)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(caption == "sent" ? 0 : 4, 0, caption == "sent" ? 4 : 0, 0), Child = g };
        }
    }

    static bool HasNetworkHistory() { try { using var s = new NetworkStore(); return s.Totals(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).Apps > 0; } catch { return false; } }
    UIElement EnableCard() => Ui.Card(Ui.V(10, Ui.T("Network monitoring is off", 20, true), Ui.Dim("When enabled, the background tray monitor records which applications connect to which addresses and ports, and keeps that history on this device for up to 30 days. Turn on “Show hostnames + bytes” (administrator) to see per-application bytes and the hostnames (domains) behind each address. URLs, page content and payloads are never recorded.", 13), Ui.H(8, Ui.Pill("Enable network monitoring", EnableNetwork, true))), 24);

    string RangeLabel()
    {
        if (customFrom is DateTime f && customTo is DateTime t) { var a = f.ToLocalTime(); var b = t.ToLocalTime(); return a.Date == b.Date ? a.ToString("MMM d, HH:mm") + " – " + b.ToString("HH:mm") : a.ToString("MMM d HH:mm") + " – " + b.ToString("MMM d HH:mm"); }
        return rangeKey switch { "15m" => "Last 15 minutes", "1h" => "Last hour", "24h" => "Last 24 hours", "7d" => "Last 7 days", _ => "Last 30 days" };
    }
    void ShiftWindow(int dir)
    {
        var (f, t, _) = Window_(); var span = t - f; var nf = f + span * dir; var nt = t + span * dir;
        if (nt >= DateTime.UtcNow.AddMinutes(-1)) { SetRange(rangeKey); return; }
        customFrom = nf; customTo = nt; StartLoad(); RebuildHeader();
    }
    void ZoomRange(int dir)
    {
        var keys = Ranges.Select(r => r.Key).ToList(); int i = keys.IndexOf(rangeKey);
        if (customFrom != null) { var span = customTo!.Value - customFrom.Value; i = Ranges.Select((r, k) => (r, k)).OrderBy(x => Math.Abs((x.r.Span - span).TotalSeconds)).First().k; }
        SetRange(keys[Math.Clamp(i + dir, 0, keys.Count - 1)]);
    }

    // ===================================================================== status strip
    UIElement NetStatusStrip(NetData b)
    {
        var st = NetworkSettings.ReadStatus(); bool enabled = NetworkSettings.IsEnabled; double age = st.LastOkUtc is DateTime t ? (DateTime.UtcNow - t).TotalSeconds : double.PositiveInfinity;
        NetMode mode = !enabled ? NetMode.Off : st.Mode == NetMode.Off ? NetMode.Starting : age > 45 ? NetMode.Stale : st.Mode;
        string eh = enabled ? st.Enhanced : "Off"; if (eh == "Active" && (st.EnhancedOkUtc == null || (DateTime.UtcNow - st.EnhancedOkUtc.Value).TotalSeconds > 20)) eh = "Failed"; bool eOn = eh is "Active" or "Starting";
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; void Add(UIElement e) { if (e is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 10, 6); fe.VerticalAlignment = VerticalAlignment.Center; } row.Children.Add(e); }
        var (fg, bg) = mode switch { NetMode.Basic or NetMode.Enhanced => (Pal.OkText, Pal.OkBg), NetMode.Off => (Pal.Text2, Pal.Inset), _ => (Pal.WarnText, Pal.WarnBg) };
        Add(Ui.Chip(mode == NetMode.Basic ? "Monitoring (basic)" : mode == NetMode.Enhanced ? "Monitoring (enhanced)" : "Monitoring: " + mode, fg, bg));
        Add(Ui.Chip(eh == "Active" ? "Hostnames + bytes on" + (st.DnsOk ? "" : " (no DNS)") : eh == "Starting" ? "Waiting for administrator permission" : eh == "Failed" ? "Hostnames + bytes stopped" : "Hostnames + bytes off", eh == "Active" ? Pal.OkText : eh == "Failed" ? Pal.WarnText : Pal.Text2, eh == "Active" ? Pal.OkBg : eh == "Failed" ? Pal.WarnBg : Pal.Inset));
        if (mode is NetMode.Basic or NetMode.Enhanced) Add(Ui.Dim("Now ↓ " + Bytes(st.RxBps) + "/s  ↑ " + Bytes(st.TxBps) + "/s (adapter)", 12, false));
        Add(Ui.Dim(b.Totals.Apps + " apps · " + b.Totals.Endpoints + " endpoints · " + Bytes(b.Totals.DbBytes) + " stored", 12, false));
        var blocks = NetworkSettings.ReadBlocks(); if (blocks.Blocks.Count > 0) Add(Ui.Chip(blocks.Blocks.Count + " temporary block" + (blocks.Blocks.Count == 1 ? "" : "s"), Pal.WarnText, Pal.WarnBg));
        else if (blocks.EndedNote != null && blocks.EndedUtc is DateTime eu && (DateTime.UtcNow - eu).TotalHours < 2) Add(Ui.Dim("Blocks ended " + eu.ToLocalTime().ToString("t") + ": " + blocks.EndedNote, 12, false));
        if (eh == "Failed" && st.EnhancedNote.Length > 0) Add(Ui.Dim(st.EnhancedNote, 12, false));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (blocks.Blocks.Count > 0) { var rm = Ui.Pill("Remove all blocks", () => { WriteNetCommand("block-remove-all|" + DateTime.UtcNow.Ticks + "|"); Toast("Removing all temporary blocks…"); }); rm.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(rm); }
        if (enabled) { var ptr = Ui.Pill(NetworkSettings.PtrEnabled ? "Reverse DNS: on" : "Reverse DNS: off", TogglePtr, false, NetworkSettings.PtrEnabled, null, "Automatic reverse DNS lookups for unnamed addresses, " + (NetworkSettings.PtrEnabled ? "on" : "off")); ptr.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(ptr); }
        if (enabled) { var e = eOn ? Ui.Pill("Stop hostnames + bytes", () => { WriteNetCommand("enhanced-stop"); Toast("Stopping enhanced monitoring. Collected data is kept."); }) : Ui.Pill("Show hostnames + bytes…", StartEnhancedFlow, true); e.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(e); }
        actions.Children.Add(enabled ? Ui.Pill("Turn off", () => { NetworkSettings.SetEnabled(false); Toast("Network monitoring turned off. Stored history is kept until you clear it in Settings."); updatePage?.Invoke(); }) : Ui.Pill("Enable network monitoring", EnableNetwork, true));
        var dock = new DockPanel(); DockPanel.SetDock(actions, Dock.Right); dock.Children.Add(actions); dock.Children.Add(row); return dock;
    }

    // ===================================================================== inspector
    UIElement Inspector(NetData d)
    {
        var p = new StackPanel(); var app = d.Apps.FirstOrDefault(a => a.Id == netAppId); HostGroup? host = null;
        if (app != null && netHostKey != null && d.Hosts.TryGetValue(app.Id, out var hs)) host = hs.FirstOrDefault(h => "h" + app.Id + "|" + h.Key == netHostKey);
        var place = netPlaceKey != null ? d.Places.FirstOrDefault(x => x.Key == netPlaceKey) : null; bool hasBytes = d.Totals.EnhancedCoverage > 0; string period = RangeLabel();

        void Header(ImageSource? icon, string glyph, string title, string? sub)
        {
            var h = new DockPanel { Margin = new Thickness(0, 0, 0, 12) }; FrameworkElement ic = icon != null ? new Image { Source = icon, Width = 44, Height = 44 } : new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 34, Foreground = Pal.Text2, Width = 44, TextAlignment = TextAlignment.Center };
            ic.Margin = new Thickness(0, 0, 12, 0); ic.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(ic, Dock.Left); h.Children.Add(ic);
            var stack = Ui.V(0, Ui.T(title, 21, true, null, true)); if (sub != null) stack.Children.Add(Ui.Dim(sub, 12)); stack.VerticalAlignment = VerticalAlignment.Center; h.Children.Add(stack); p.Children.Add(h);
        }
        void BytesPills(long sent, long recv, bool known)
        {
            var g = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (cap, val, col) in new[] { ("↑", sent, MiniBars.SentColor), ("↓", recv, MiniBars.RecvColor) })
            {
                var t = new TextBlock { Text = cap + "  " + (known ? Bytes(val) : "—"), FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(col), HorizontalAlignment = HorizontalAlignment.Right };
                g.Children.Add(new Border { Background = new SolidColorBrush(Pal.Alpha(col, Pal.Dark ? .22 : .14)), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(cap == "↑" ? 0 : 4, 0, cap == "↑" ? 4 : 0, 0), Child = t });
            }
            p.Children.Add(g); if (!known) p.Children.Add(Ui.Dim("Bytes need “Show hostnames + bytes” (enhanced monitoring).", 11.5));
        }
        StackPanel Section(string title)
        {
            bool collapsed = netCollapsed.Contains(title); var body = new StackPanel { Margin = new Thickness(4, 4, 0, 6), Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
            var head = new DockPanel(); var chev = new TextBlock { Text = collapsed ? "" : "", FontFamily = Icons, FontSize = 10, Foreground = Pal.Text2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; DockPanel.SetDock(chev, Dock.Left); head.Children.Add(chev); head.Children.Add(Ui.T(title, 12.5, true, Pal.Text2));
            var btn = Ui.RowBtn(head, () => { }, new Thickness(10, 6, 10, 6), Pal.Inset, title); btn.Margin = new Thickness(0, 8, 0, 0);
            btn.Click += (_, _) => { bool now = body.Visibility == Visibility.Visible; body.Visibility = now ? Visibility.Collapsed : Visibility.Visible; chev.Text = now ? "" : ""; if (now) netCollapsed.Add(title); else netCollapsed.Remove(title); };
            p.Children.Add(btn); p.Children.Add(body); return body;
        }
        static UIElement KV(string k, string v, UIElement? extra = null)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 0) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var kt = Ui.Dim(k + ":", 12.5, false); kt.HorizontalAlignment = HorizontalAlignment.Right; kt.Margin = new Thickness(0, 0, 10, 0); var vt = Ui.T(v, 12.5, false, null, true); var vs = new StackPanel(); vs.Children.Add(vt); if (extra != null) { if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 0); vs.Children.Add(extra); } Grid.SetColumn(vs, 1); g.Children.Add(kt); g.Children.Add(vs); return g;
        }
        static string ListMore(IEnumerable<string> items, int n = 5) { var l = items.ToList(); return l.Count == 0 ? "—" : string.Join("\n", l.Take(n)) + (l.Count > n ? "\n+ " + (l.Count - n) + " more" : ""); }
        static string Loc(string ip) { var g = Geo.Lookup(ip); return g == null ? "" : " — " + g.Label + (g.CountryCode.Length > 0 ? ", " + g.CountryCode : ""); }

        if (place != null)
        {
            Header(null, "", place.Label, (place.Country.Length > 0 ? place.Country + " · " : "") + "approximate location");
            var s = Section("Destinations here"); s.Children.Add(KV("Addresses", place.Endpoints.ToString())); s.Children.Add(KV("Hostnames", ListMore(place.Hosts, 8))); if (place.Bytes > 0) s.Children.Add(KV("Observed bytes", Bytes(place.Bytes)));
            s.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = Ui.Dim("The location is where the address is registered or announced (often a CDN or cloud region), not where the company is. It comes from the bundled DB-IP database; nothing is looked up online.", 11.5) });
            var back = Ui.Pill("Clear map selection", () => { netPlaceKey = null; updatePage?.Invoke(); }); back.HorizontalAlignment = HorizontalAlignment.Left; back.Margin = new Thickness(0, 10, 0, 0); p.Children.Add(back);
            return p;
        }
        if (host != null && app != null)
        {
            var ips = host.Dests.Select(x => x.Ip).Distinct().ToList();
            Header(null, host.Name.Name != null ? "" : "", host.Display, "via " + app.Name);
            BytesPills(host.Sent, host.Recv, hasBytes);
            var s = Section("Destination");
            s.Children.Add(KV("Name evidence", host.Name.Label + (host.Name.Name == null ? " — no DNS answer was seen for these addresses in this period." : host.Name.Kind == NameEvidenceKind.ProcessDns ? " — " + app.Name + " resolved this name to these addresses." : " — another process or the DNS cache linked this name to these addresses; weaker evidence.")));
            if (host.Name.OtherNames > 0) s.Children.Add(KV("Other names", ListMore(host.Name.Candidates.Skip(1), 6)));
            s.Children.Add(KV("IP addresses", ListMore(ips.Select(i => i + Loc(i)), 6)));
            s.Children.Add(KV("Ports", string.Join(", ", host.Dests.Select(x => ProtoName(x.Proto) + " " + Service(x.Proto, x.Port)).Distinct())));
            s.Children.Add(KV("Seen", Local(host.Dests.Min(x => x.FirstMin)) + " – " + Local(host.LastMin) + " · " + host.Obs + " snapshot" + (host.Obs == 1 ? "" : "s")));
            s.Children.Add(KV("Live now", host.Dests.Any(x => x.Live) ? "Yes" : "No"));
            var ptr = Ui.Pill("Look up reverse DNS (PTR)…", async () => { var ip = ips[0]; if (MessageBox.Show(this, "This sends a DNS query for " + ip + " to your configured DNS server. Continue?", "Reverse DNS lookup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; try { var h = await System.Net.Dns.GetHostEntryAsync(ip); Toast("Reverse DNS (PTR) for " + ip + ": " + h.HostName + ". This is the address owner's label, not necessarily what the app asked for."); } catch { Toast("No reverse DNS name for " + ip + "."); } });
            ptr.HorizontalAlignment = HorizontalAlignment.Left; ptr.Margin = new Thickness(0, 10, 0, 0); s.Children.Add(ptr);
            s.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = Ui.Dim("A hostname is correlated DNS evidence, not proof of what a connection carried. Bytes belong to the addresses; they are never split across names.", 11.5) });
            return p;
        }
        if (app != null)
        {
            Header(app.Resolved ? AppMeta.Icon(app.Path) : null, "", app.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.Name[..^4] : app.Name, app.Resolved ? null : "Executable path unavailable (protected process or already exited)");
            BytesPills(app.Sent, app.Recv, hasBytes);
            var proc = Section("Process");
            Button? openBtn = null; if (app.Resolved) { openBtn = Ui.Pill("Open folder", () => OpenFolder(System.IO.Path.GetDirectoryName(app.Path) ?? app.Path)); openBtn.HorizontalAlignment = HorizontalAlignment.Left; openBtn.Padding = new Thickness(10, 3, 10, 3); }
            proc.Children.Add(KV("Path", app.Resolved ? app.Path : "—", openBtn));
            var sigRow = KV("Code signature", app.Resolved ? "Checking…" : "—"); var sigText = (TextBlock)((StackPanel)((Grid)sigRow).Children[1]).Children[0]; proc.Children.Add(sigRow);
            if (app.Resolved) { var path = app.Path; Task.Run(() => AppMeta.Signature(path)).ContinueWith(t => Dispatcher.BeginInvoke(() => sigText.Text = t.Result + " — identity information, not a safety verdict")); }
            proc.Children.Add(KV("Instances now", d.Live.Count == 0 ? "None in the latest snapshot" : string.Join(", ", d.Live.Select(x => x.Pid).Distinct().Take(8).Select(x => "PID " + x))));
            proc.Children.Add(KV("User", Environment.UserName + " (blocks apply to this Windows account only)"));
            var pol = Section("Internet access policy"); var (chip, blk) = BlockControls(app); chip.HorizontalAlignment = HorizontalAlignment.Left; pol.Children.Add(chip); blk.HorizontalAlignment = HorizontalAlignment.Left; blk.Margin = new Thickness(0, 8, 0, 0); pol.Children.Add(blk);
            pol.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = Ui.Dim("Blocks are session-only Windows Filtering Platform rules for this exact program path. Other firewall or enterprise rules may still apply.", 11.5) });
            var cd = Section("Connection details"); var groups = d.Hosts.TryGetValue(app.Id, out var hg) ? hg : GroupHosts(d.Dests, d.Names); var dests = groups.SelectMany(x => x.Dests).ToList();
            cd.Children.Add(KV("Summary over", period));
            int unnamed = groups.Count(x => x.Name.Name == null && x.Dests.Any(y => y.Scope == IpScope.Public));
            if (unnamed > 0) cd.Children.Add(KV("Unnamed", unnamed + " public address" + (unnamed == 1 ? "" : "es") + " without a hostname. Browsers and Electron apps (Edge, Chrome, Claude, ChatGPT, VS Code) usually resolve names themselves, often over encrypted DNS, so Windows never sees the lookup." + (NetworkSettings.PtrEnabled ? "" : " Turn on Reverse DNS to show the address owner's name instead.")));
            cd.Children.Add(KV("Hostnames", ListMore(groups.Where(x => x.Name.Name != null).Select(x => x.Display), 6)));
            cd.Children.Add(KV("IP addresses", ListMore(dests.OrderByDescending(x => x.Sent + x.Recv).Select(x => x.Ip).Distinct(), 5)));
            var tcp = dests.Where(x => x.Proto == 6).Select(x => Service(6, x.Port)).Distinct().ToList(); var udp = dests.Where(x => x.Proto == 17).Select(x => Service(17, x.Port)).Distinct().ToList();
            if (tcp.Count > 0) cd.Children.Add(KV("TCP ports", ListMore(tcp, 4))); if (udp.Count > 0) cd.Children.Add(KV("UDP ports", ListMore(udp, 4)));
            cd.Children.Add(KV("Protocols", tcp.Count + udp.Count == 0 ? "—" : string.Join(", ", new[] { tcp.Count > 0 ? "TCP" : "", udp.Count > 0 ? "UDP" : "" }.Where(x => x.Length > 0))));
            var cities = dests.Select(x => Geo.Lookup(x.Ip)).Where(x => x != null).Select(x => x!.Label + ", " + x.CountryCode).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key);
            cd.Children.Add(KV("Locations", ListMore(cities, 5)));
            cd.Children.Add(KV("Connections", app.Observations.ToString("N0") + " snapshot observation" + (app.Observations == 1 ? "" : "s") + " · " + app.LiveConns + " live now"));
            cd.Children.Add(KV("Allowed / denied", "Not counted: Surface Insight does not see individual connection decisions."));
            return p;
        }
        Header(null, "", "All applications", period);
        BytesPills(d.Totals.Sent, d.Totals.Recv, hasBytes);
        var ov = Section("Overview");
        ov.Children.Add(KV("Applications", d.Totals.Apps.ToString("N0"))); ov.Children.Add(KV("Remote endpoints", d.Totals.Endpoints.ToString("N0"))); ov.Children.Add(KV("Live now", d.Totals.LiveConns.ToString("N0")));
        ov.Children.Add(KV("Top by data", ListMore(d.Apps.Where(a => a.Sent + a.Recv > 0).Take(5).Select(a => a.Name + "  ↑" + Bytes(a.Sent) + " ↓" + Bytes(a.Recv)), 5)));
        var named = GroupHosts(d.Dests, d.Names); ov.Children.Add(KV("Top hostnames", ListMore(named.Where(h => h.Name.Name != null).Take(6).Select(h => h.Display), 6)));
        if (!hasBytes || d.Names.Count == 0)
        {
            var call = Section("Hostnames and bytes");
            call.Children.Add(Ui.Dim("Hostnames (such as api.anthropic.com) and per-application bytes come from Windows network and DNS events, which only an administrator session can read. Start it with “Show hostnames + bytes…”. It runs until you stop it or quit, and is never restarted automatically. Names appear for lookups made while it runs plus answers still in the Windows DNS cache when it starts; apps that use their own encrypted DNS (DoH) may stay unnamed.", 12));
        }
        foreach (var n in d.Notes.Take(3)) p.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = Ui.Dim("Coverage: " + n.Detail + " (" + n.Time.ToLocalTime().ToString("g") + ")", 11.5) });
        p.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = Ui.Dim("Select an application or hostname on the left, or a city on the map. Locations are approximate.", 11.5) });
        return p;
    }

    // ===================================================================== enable / enhanced / blocking flows
    void EnableNetwork()
    {
        var r = MessageBox.Show(this, "Turn on network monitoring?\n\n• The tray monitor will record which applications connect to which IP addresses and ports, every few seconds.\n• That history stays on this device (your profile folder) for up to 30 days. It can reveal which apps and services you use, even without URLs. It is not encrypted by Surface Insight.\n• Basic mode needs no administrator access and does not read traffic content or measure per-application bytes.\n• You can turn it off or clear the history any time in Settings.", "Enable network monitoring", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r != MessageBoxResult.Yes) return; NetworkSettings.SetEnabled(true); Toast("Network monitoring enabled. The first snapshot arrives within a few seconds."); ShowPage();
    }
    void TogglePtr()
    {
        if (NetworkSettings.PtrEnabled) { NetworkSettings.SetPtr(false); Toast("Automatic reverse DNS lookups are off. Names already found are kept."); updatePage?.Invoke(); return; }
        var r = MessageBox.Show(this, "Look up reverse DNS (PTR) names automatically?\n\n• For public addresses with no DNS evidence, the tray monitor asks your configured DNS server for the address owner's name (at most about 24 addresses every 30 seconds).\n• This sends DNS queries that reveal those addresses to your DNS provider.\n• A PTR name describes who operates the address (for example a CDN or cloud host), not the hostname the application asked for, and many addresses have none.\n• Results are stored locally with the rest of the network history.", "Reverse DNS lookups", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return; NetworkSettings.SetPtr(true); Toast("Reverse DNS lookups are on. Names appear within about a minute."); updatePage?.Invoke();
    }
    void StartEnhancedFlow()
    {
        var r = MessageBox.Show(this, "Show hostnames and per-application bytes?\n\n• Windows will ask for administrator permission. Only a small helper (this same app) runs elevated, for this session only. No service, driver or permanent firewall rule is installed.\n• It records, per application, observed transport bytes to each IP address and port, and the DNS answers Windows returns (including answers already in the DNS cache), so names can be matched to addresses. It does not record URLs, page content or packet payloads.\n• Destination names and addresses are kept on this device for up to 30 days, unencrypted by Surface Insight, and can reveal browsing and app activity.\n• The session ends when you stop it, quit the tray monitor, sign out or restart. It is never restarted automatically.", "Show hostnames + bytes", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r != MessageBoxResult.Yes) return; WriteNetCommand("enhanced-start"); Toast("Starting the enhanced session. Approve the Windows administrator prompt.");
    }
    (Border Chip, Button Btn) BlockControls(AppRow app)
    {
        var st = NetworkSettings.ReadStatus(); var bs = NetworkSettings.ReadBlocks(); bool blocked = bs.Blocks.Any(x => x.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
        bool active = st.Enhanced == "Active" && st.EnhancedOkUtc is DateTime t && (DateTime.UtcNow - t).TotalSeconds < 20; string? why = PolicyRules.Reject(app.Resolved ? app.Path : null, new[] { Environment.ProcessPath! });
        if (blocked) { var c = Ui.Chip("Temporary block active · ends when enhanced monitoring stops", Pal.WarnText, Pal.WarnBg); var b = Ui.Pill("Remove block", () => RunBlock("block-remove", app)); return (c, b); }
        var chip = Ui.Chip("Not blocked by Surface Insight", Pal.Text2, Pal.Inset); var btn = Ui.Pill("Temporary block…", () => ConfirmBlock(app)); btn.IsEnabled = active && why == null;
        btn.ToolTip = !active ? "Turn on “Show hostnames + bytes” first: blocking uses the elevated helper." : why ?? "Blocks this program's outbound connections for this session only. Other firewall and enterprise policies may still apply."; ToolTipService.SetShowOnDisabled(btn, true);
        return (chip, btn);
    }
    void ConfirmBlock(AppRow app)
    {
        var r = MessageBox.Show(this, "Temporarily block " + app.Name + "?\n\nProgram: " + app.Path + "\n\n• Blocks outbound connections (IPv4 and IPv6) from every running instance of exactly this executable, for the current Windows user only.\n• This includes local-network and loopback traffic of this program.\n• It lasts only for this enhanced session. It ends when you remove it, stop enhanced monitoring, quit the tray monitor, or the session ends, and it is never restored automatically.\n• A connection this program already has open may be reset immediately (seen in testing).\n• It does not override other firewall or enterprise rules, and it does not stop the program from running.", "Temporary block", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes) RunBlock("block-add", app);
    }
    async void RunBlock(string type, AppRow app)
    {
        long id = DateTime.UtcNow.Ticks; WriteNetCommand(type + "|" + id + "|" + app.Path); Toast(type == "block-add" ? "Applying the block…" : "Removing the block…");
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(300); if (NetworkSettings.ReadBlocks().Results.TryGetValue(id, out var res)) { Toast(res.Ok ? (type == "block-add" ? app.Name + " is temporarily blocked. The block ends when enhanced monitoring stops." : "Removed the block on " + app.Name + ".") : "Could not " + (type == "block-add" ? "block " : "remove the block on ") + app.Name + ": " + res.Message); updatePage?.Invoke(); return; }
        }
        Toast("The tray monitor did not confirm the change, so nothing is shown as blocked until it does.");
    }
}
