using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SurfaceInsight.Network;
namespace SurfaceInsight;

/// <summary>The "Network" section of an app's drill-down: every hostname (or bare address) the app contacted in the selected interval, how each name was learned, and its traffic.</summary>
public sealed partial class MainWindow
{
    sealed record AppNetData(List<(long Id, string Path)> Ids, List<HostGroup> Hosts, double[]? Sent, double[]? Recv, double[] Dests, int Live, long SentTotal, long RecvTotal, double Coverage);

    static AppNetData? LoadAppNetwork(string name, DateTime from, DateTime to, int buckets)
    {
        using var s = new NetworkStore(); var ids = s.AppIdsByName(name); if (ids.Count == 0) return null;
        var dests = new List<DestRow>(); double[]? sent = null, recv = null; var counts = new double[buckets]; int live = 0;
        foreach (var (id, _) in ids)
        {
            dests.AddRange(s.Destinations(from, to, id, new NetFilter(), "bytes", null, 600));
            var (ts, tr) = s.TrafficSeries(from, to, buckets, id);
            if (ts.Any(double.IsFinite)) { sent ??= new double[buckets]; recv ??= new double[buckets]; for (int i = 0; i < buckets; i++) { sent[i] += double.IsFinite(ts[i]) ? ts[i] / 1024 : 0; recv[i] += double.IsFinite(tr[i]) ? tr[i] / 1024 : 0; if (!double.IsFinite(ts[i])) sent[i] = recv[i] = double.NaN; } }
            var c = s.AppDestinationSeries(id, from, to, buckets); for (int i = 0; i < buckets; i++) counts[i] += c[i];
            live += s.Live(id, new NetFilter()).Count;
        }
        var names = s.NameRows(dests.Select(x => x.Ip), from, to);
        return new AppNetData(ids, GroupHosts(dests, names), sent, recv, counts, live, dests.Sum(x => x.Sent), dests.Sum(x => x.Recv), s.EnhancedCoverage(from, to));
    }

    UIElement AppNetworkSection(string name, out Action update)
    {
        var host = new ContentControl { Content = Ui.Card(Ui.V(6, Ui.T("Network", 16, true), Ui.Dim("Loading network history…", 13))) }; host.Margin = new Thickness(0, 14, 0, 0);
        DateTime loadedAt = DateTime.MinValue; int token = 0; bool showAll = false;
        void Load()
        {
            if (data == null) return; var d = data; var (from, to, n) = (d.From, d.To, d.Buckets.Length); var times = d.Times(); int my = ++token; loadedAt = DateTime.UtcNow;
            Task.Run(() => { try { return (LoadAppNetwork(name, from, to, n), (string?)null); } catch (Exception ex) { return ((AppNetData?)null, (string?)ex.Message); } })
                .ContinueWith(t => Dispatcher.Invoke(() => { if (my == token) host.Content = AppNetworkCard(name, t.Result.Item1, t.Result.Item2, times, () => { showAll = true; Load(); }, showAll); }));
        }
        Load();
        update = () => { if ((DateTime.UtcNow - loadedAt).TotalSeconds >= 60) Load(); };
        return host;
    }

    UIElement AppNetworkCard(string name, AppNetData? nd, string? error, double[] times, Action showMore, bool showAll)
    {
        var p = new StackPanel(); var head = new DockPanel(); p.Children.Add(head);
        head.Children.Add(Ui.T("Network", 16, true));
        bool on = NetworkSettings.IsEnabled;
        if (error != null) { p.Children.Add(Ui.Dim("Network history could not be read: " + error, 13)); return Ui.Card(p); }
        if (nd == null || nd.Hosts.Count == 0)
        {
            string msg = !on && nd == null ? "Network monitoring is off, so no connections are recorded for " + name + ". Turn it on from the Network tab to see the domains and addresses it contacts."
                : nd == null ? "No network connections have been recorded for " + name + "."
                : "No network connections recorded for " + name + " in the selected interval. Choose a longer interval to see older history.";
            p.Children.Add(Ui.Dim(msg, 13)); if (!on) { var go = Ui.Pill("Open Network", () => Go("network")); go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(0, 10, 0, 0); p.Children.Add(go); }
            return Ui.Card(p);
        }
        int named = nd.Hosts.Count(h => h.Name.Name != null), addrs = nd.Hosts.Sum(h => h.Dests.Select(x => x.Ip).Distinct().Count());
        bool hasBytes = nd.SentTotal + nd.RecvTotal > 0;
        p.Children.Add(Ui.Dim($"{nd.Hosts.Count} destination{(nd.Hosts.Count == 1 ? "" : "s")} in this interval · {named} with a hostname · {addrs} address{(addrs == 1 ? "" : "es")}" + (nd.Live > 0 ? $" · {nd.Live} open now" : "") + (hasBytes ? $" · ↑ {Bytes(nd.SentTotal)}  ↓ {Bytes(nd.RecvTotal)} observed" : ""), 13));

        // traffic (enhanced sessions) or destinations contacted (basic monitoring)
        var chart = NewChart(120, name + " network activity", true, nd.Sent != null ? "KB/s" : ""); chart.Times = times;
        if (nd.Sent != null && nd.Recv != null) chart.Series = new() { S("Received", nd.Recv, Pal.Watts, "KB/s", fill: true), S("Sent", nd.Sent, Pal.Io, "KB/s") };
        else chart.Series = new() { S("Destinations contacted", nd.Dests, Pal.CpuColor, "", fill: true, fmt: "F0") };
        chart.Markers = data?.Markers.Select(m => m.Time).ToList() ?? new(); chart.Refresh(); chart.Margin = new Thickness(0, 12, 0, 0); p.Children.Add(chart);
        p.Children.Add(Ui.Dim(nd.Sent != null ? "Observed transport bytes while an enhanced session ran; gaps mean no session, not no traffic." : "Number of distinct destinations seen each slice (connection snapshots; byte counts need an enhanced session).", 11.5));

        // hostname table
        var g = new Grid { Margin = new Thickness(0, 14, 0, 0) }; var widths = new[] { new GridLength(2.2, GridUnitType.Star), new GridLength(1.25, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(104), new GridLength(104), new GridLength(80) };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w, MinWidth = w.IsStar ? 90 : 0 });
        int row = 0; void Cell(UIElement e, int c, bool right = false) { Grid.SetRow(e, row); Grid.SetColumn(e, c); if (e is FrameworkElement fe) { fe.Margin = new Thickness(0, 6, 10, 6); fe.VerticalAlignment = VerticalAlignment.Top; if (right) fe.HorizontalAlignment = HorizontalAlignment.Right; } g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition()); Cell(Ui.Cap("Hostname / address"), 0); Cell(Ui.Cap("How it was named"), 1); Cell(Ui.Cap("Service"), 2); Cell(Ui.Cap("First seen"), 3); Cell(Ui.Cap("Last seen"), 4); Cell(Ui.Cap(hasBytes ? "Traffic" : "Seen"), 5, true); row++;
        var ordered = nd.Hosts.OrderByDescending(h => h.LastMin).ThenByDescending(h => h.Sent + h.Recv).ToList(); int max = showAll ? 400 : 25;
        foreach (var h in ordered.Take(max))
        {
            g.RowDefinitions.Add(new RowDefinition());
            var ips = h.Dests.Select(x => x.Ip).Distinct().ToList(); bool isLive = h.Dests.Any(x => x.Live);
            var title = Ui.T(h.Display, 13, true, null, true); var sub = Ui.Dim(h.Name.Name != null ? (ips.Count == 1 ? ips[0] : ips[0] + $" + {ips.Count - 1} more") : IpClassifier.Label(h.Dests[0].Scope) + " address", 11.5, false);
            var nameCell = Ui.V(2, title, isLive ? Ui.H(6, sub, new Border { Child = Ui.Chip("open now", Pal.OkText, Pal.OkBg), VerticalAlignment = VerticalAlignment.Center }) : sub);
            var key = h.Key; var firstId = nd.Ids.FirstOrDefault(i => h.Dests.Any(x => ParseIds(x.AppIds).Contains(i.Id))).Id;
            var btn = Ui.RowBtn(nameCell, () => { netPreselect = name; if (firstId != 0) { netAppId = firstId; netExpanded.Add(firstId); netHostKey = "h" + firstId + "|" + key; } Go("network"); }, new Thickness(4, 2, 4, 2), null, $"{h.Display}. Open in Network");
            Cell(btn, 0);
            string how = h.Name.Name == null ? "No DNS evidence" : h.Name.Label + (h.Name.OtherNames > 0 ? $" · {h.Name.OtherNames} other name{(h.Name.OtherNames == 1 ? "" : "s")}" : "");
            var howT = Ui.T(how, 12.5, false, h.Name.Kind == NameEvidenceKind.ProcessDns ? Pal.Text : Pal.Text2, true);
            if (h.Name.Candidates.Count > 1) howT.ToolTip = "Names seen for this address: " + string.Join(", ", h.Name.Candidates.Take(8));
            Cell(howT, 1);
            Cell(Ui.T(string.Join(", ", h.Dests.Select(x => Service(x.Proto, x.Port) is var sv && sv != x.Port.ToString() ? sv : ProtoName(x.Proto) + " " + x.Port).Distinct().Take(3)), 12.5, false, Pal.Text2, true), 2);
            Cell(Ui.T(Local(h.Dests.Min(x => x.FirstMin)), 12.5, false, Pal.Text2), 3); Cell(Ui.T(Local(h.LastMin), 12.5), 4);
            Cell(Ui.T(hasBytes ? (h.Sent + h.Recv > 0 ? Bytes(h.Sent + h.Recv) : "—") : h.Obs + "×", 12.5, false, Pal.Text2), 5, true);
            row++;
        }
        p.Children.Add(g);
        var foot = Ui.H(10); foot.Margin = new Thickness(0, 10, 0, 0);
        if (!showAll && ordered.Count > max) foot.Children.Add(Ui.Pill($"Show all {ordered.Count}", showMore));
        var open = Ui.Pill("Open in Network", () => { netPreselect = name; Go("network"); }); foot.Children.Add(open); p.Children.Add(foot);
        p.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = Ui.Dim("Hostnames come from DNS lookups matched to this app's connections by time, which is evidence, not proof. Browsers and Electron apps often use their own encrypted DNS, so their destinations may show as addresses only; turning on Reverse DNS in the Network tab names more of them by the address owner.", 11.5) });
        return Ui.Card(p);
    }
}
