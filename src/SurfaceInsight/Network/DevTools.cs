using System.Net;
using System.Net.Http;
namespace SurfaceInsight.Network;

/// <summary>Developer/test helpers (not part of normal use): collect real snapshots into the data folder, or generate large synthetic histories for scale tests. Point SURFACEINSIGHT_DATA at a scratch folder first.</summary>
public static class DevTools
{
    public static void Collect(int seconds)
    {
        var c = new BasicCollector(); using var store = new NetworkStore(); var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { store.Write(c.Read(2)); Thread.Sleep(2000); }
    }
    /// <summary>End-to-end check of the enhanced path (UAC prompt expected): starts the broker, generates traffic of known size from this process, stores the batches and writes a report.</summary>
    public static void EnhancedTest(string report)
    {
        var sb = new System.Text.StringBuilder(); void W(string x) { sb.AppendLine(x); File.WriteAllText(report, sb.ToString()); }
        using var store = new NetworkStore(); using var ctl = new BrokerController(); ctl.Log += m => W("controller: " + m);
        W("start " + DateTime.Now.ToString("s")); ctl.Start(); var wait = DateTime.UtcNow.AddSeconds(120);
        while (ctl.State == BrokerController.Phase.Starting && DateTime.UtcNow < wait) Thread.Sleep(250);
        W("state after start: " + ctl.State + " · " + ctl.Error + ctl.Note + " · dns=" + ctl.DnsOk); if (ctl.State != BrokerController.Phase.Active) return;
        store.BeginEnhanced(DateTime.UtcNow); var stopAt = DateTime.UtcNow; void Pump() { while (ctl.TryDequeue(out var m)) store.WriteBatch(m); }
        long got4 = 0, got6 = 0; string host = "speed.cloudflare.com";
        try { using var http = new HttpClient(); got4 = http.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=5000000").GetAwaiter().GetResult().Length; } catch (Exception ex) { W("https v4/default download failed: " + ex.Message); }
        W("HTTPS download body bytes (default route): " + got4);
        try { var v6 = Dns.GetHostAddresses(host).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList(); if (v6.Count > 0) { using var h = new HttpClient(new SocketsHttpHandler { ConnectCallback = async (ctx, ct) => { var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetworkV6, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp); await sock.ConnectAsync(new IPEndPoint(v6[0], 443), ct); return new System.Net.Sockets.NetworkStream(sock, true); } }); got6 = h.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=2000000").GetAwaiter().GetResult().Length; W("IPv6 download body bytes to " + v6[0] + ": " + got6); } else W("no AAAA record; IPv6 test skipped"); } catch (Exception ex) { W("IPv6 test failed/unavailable: " + ex.Message); }
        try { using var udp = new System.Net.Sockets.UdpClient(); udp.Client.ReceiveTimeout = 3000; var q = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 }; udp.Send(q, q.Length, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)); var ep = new IPEndPoint(IPAddress.Any, 0); var r = udp.Receive(ref ep); W("UDP DNS to 1.1.1.1: sent " + q.Length + " B, received " + r.Length + " B"); } catch (Exception ex) { W("UDP test failed: " + ex.Message); }
        try
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.IPv6Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; const int down = 300_000, up = 50_000; long cg = 0, sg = 0;
            var srv = Task.Run(() => { using var c = l.AcceptTcpClient(); var st = c.GetStream(); var buf = new byte[16384]; int sent = 0; var tx = Task.Run(() => { while (sent < down) { int n = Math.Min(buf.Length, down - sent); st.Write(buf, 0, n); sent += n; } st.Flush(); }); int r; while (sg < up && (r = st.Read(buf, 0, buf.Length)) > 0) sg += r; tx.Wait(); });
            using (var cl = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6)) { cl.Connect(IPAddress.IPv6Loopback, port); var st = cl.GetStream(); var cb = new byte[16384]; var utask = Task.Run(() => { int sent = 0; while (sent < up) { int n = Math.Min(cb.Length, up - sent); st.Write(cb, 0, n); sent += n; } st.Flush(); }); int r; while (cg < down && (r = st.Read(cb, 0, cb.Length)) > 0) cg += r; utask.Wait(); }
            srv.Wait(); l.Stop(); W("IPv6 loopback TCP port " + port + ": client received " + cg + ", server received " + sg);
            using var us = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)); int uport = ((IPEndPoint)us.Client.LocalEndPoint!).Port; var echo = Task.Run(() => { var ep = new IPEndPoint(IPAddress.IPv6Any, 0); for (int i = 0; i < 20; i++) { var b = us.Receive(ref ep); us.Send(b, b.Length, ep); } });
            using var uc = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetworkV6); uc.Client.ReceiveTimeout = 3000; var peer6 = new IPEndPoint(IPAddress.IPv6Any, 0); int back = 0; for (int i = 0; i < 20; i++) { uc.Send(new byte[100], 100, new IPEndPoint(IPAddress.IPv6Loopback, uport)); try { uc.Receive(ref peer6); back++; } catch { } } echo.Wait(3000); W("IPv6 loopback UDP port " + uport + ": 20 x 100 B each way, echoes " + back);
        }
        catch (Exception ex) { W("IPv6 loopback test failed: " + ex.Message); }        Thread.Sleep(6000); Pump(); Thread.Sleep(4000); Pump(); ctl.Stop(); store.EndEnhanced(DateTime.UtcNow, "test");
        var from = stopAt.AddMinutes(-1); var to = DateTime.UtcNow.AddMinutes(1); var all = new NetFilter();
        var apps = store.Apps(from, to, all); var me = apps.FirstOrDefault(a => a.Path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)); W("apps seen: " + apps.Count + "; this process present: " + (me != null) + (me != null ? $" sent={me.Sent} recv={me.Recv}" : ""));
        if (me != null) foreach (var dst in store.Destinations(from, to, me.Id, all, "bytes", null)) W($"  dest {dst.Ip}:{dst.Port} {(dst.Proto == 6 ? "TCP" : "UDP")} sent={dst.Sent} recv={dst.Recv}");
        var tot = store.Totals(from, to); W("totals: sent=" + tot.Sent + " recv=" + tot.Recv + " enhanced coverage=" + tot.EnhancedCoverage.ToString("P0"));
        var ips = store.Destinations(from, to, me?.Id, all, "bytes", null).Select(x => x.Ip); var names = store.NameRows(ips, from, to); W("name evidence rows for those IPs: " + names.Count); foreach (var n in names.Take(12)) W($"  {n.Name} -> {n.Ip} app={n.AppId} via {n.Src}");
        foreach (var dst in store.Destinations(from, to, me?.Id, all, "bytes", null).Take(6)) { var ids = dst.AppIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList(); var r = NameCorrelator.Resolve(dst.Ip, ids, dst.FirstMin, dst.LastMin, names); W($"  {dst.Ip} => {r.Name ?? "(unknown)"} [{r.Label}] others={r.OtherNames}"); }
        foreach (var c in store.CoverageNotes(from, to)) W("coverage note: " + c.Kind + " — " + c.Detail);
        W("name evidence by source: " + string.Join(", ", store.NameSourceCounts().Select(x => x.Src + "=" + x.Count)));
        var allDests = store.Destinations(from, to, null, all, "bytes", null, 600); var allNames = store.NameRows(allDests.Select(x => x.Ip), from, to); int named = allDests.Count(x => NameCorrelator.Resolve(x.Ip, x.AppIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList(), x.FirstMin, x.LastMin, allNames).Name != null);
        W($"destinations named: {named} of {allDests.Count} (public: {allDests.Count(x => x.Scope == IpScope.Public && NameCorrelator.Resolve(x.Ip, x.AppIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList(), x.FirstMin, x.LastMin, allNames).Name != null)} of {allDests.Count(x => x.Scope == IpScope.Public)})");
        W("done");
    }    public static void NameReport(string file)
    {
        using var st = new NetworkStore(); var from = DateTime.UtcNow.AddMinutes(-10); var to = DateTime.UtcNow.AddMinutes(1); var dests = st.Destinations(from, to, null, new NetFilter(), "last", null, 1000); var names = st.NameRows(dests.Select(x => x.Ip), from, to);
        var res = dests.Select(x => (x, NameCorrelator.Resolve(x.Ip, x.AppIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList(), x.FirstMin, x.LastMin, names))).ToList(); var pub = res.Where(r => r.x.Scope == IpScope.Public).ToList();
        var lines = new List<string> { "name evidence by source: " + string.Join(", ", st.NameSourceCounts().Select(x => x.Src + "=" + x.Count)), $"public destinations named: {pub.Count(r => r.Item2.Name != null)} of {pub.Count}", $"  process DNS match: {pub.Count(r => r.Item2.Kind == NameEvidenceKind.ProcessDns)}, system/cache candidate: {pub.Count(r => r.Item2.Kind == NameEvidenceKind.SystemDns)}" };
        lines.AddRange(pub.Take(25).Select(r => $"  {r.x.AppName,-28} {r.x.Ip,-40} {r.Item2.Name ?? "(unknown)"} [{r.Item2.Label}]")); File.WriteAllLines(file, lines);
    }
    public static void DumpAudit(string file) { using var st = new NetworkStore(); File.WriteAllLines(file, st.AuditEvents(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(5)).Select(a => a.Time.ToLocalTime().ToString("T") + " " + a.Kind + " " + a.Detail)); }
    public static void Seed(int destinations)
    {
        var rnd = new Random(7); using var store = new NetworkStore(); string[] apps = Enumerable.Range(1, 30).Select(i => i switch { 1 => "msedge.exe", 2 => "OneDrive.exe", 3 => "Teams.exe", _ => "app" + i + ".exe" }).ToArray();
        int snapshots = 240, perSnap = Math.Max(1, destinations / snapshots + 1); var now = DateTime.UtcNow; int made = 0;
        for (int s = 0; s < snapshots && made < destinations; s++)
        {
            var conns = new List<Conn>(); var procs = new Dictionary<int, (ProcessInstanceKey, ApplicationIdentity)>();
            for (int i = 0; i < perSnap && made < destinations; i++, made++)
            {
                int a = rnd.Next(apps.Length); int pid = 1000 + a; procs[pid] = (new ProcessInstanceKey(pid, 1), new ApplicationIdentity(@"C:\Program Files\Test\" + apps[a], apps[a], true));
                string ip = rnd.Next(4) == 0 ? "2606:4700:" + rnd.Next(65535).ToString("x") + "::" + rnd.Next(65535).ToString("x") : rnd.Next(5) == 0 ? "192.168." + rnd.Next(256) + "." + rnd.Next(1, 255) : (20 + rnd.Next(180)) + "." + rnd.Next(256) + "." + rnd.Next(256) + "." + rnd.Next(1, 255);
                conns.Add(new Conn(rnd.Next(5) == 0 ? (byte)17 : (byte)6, "192.168.8.152", (ushort)(50000 + i), ip, rnd.Next(3) == 0 ? (ushort)80 : (ushort)443, 0, 5, pid));
            }
            store.Write(new NetSnapshot(now.AddMinutes(-(snapshots - s) * 5), conns, new(), new(), null, procs));
        }
    }
}