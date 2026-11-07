namespace SurfaceInsight;

public static class SelfTest
{
    public static void Run(bool ci = false)
    {
        var results = new List<string>(); int failed = 0, passed = 0, skipped = 0;
        void Check(bool ok, string name) { results.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; else passed++; }
        var s = new Sample(DateTime.UtcNow, false, 25, 50, 25000, 50000, 52000, 10, null, null, null, null, "Balanced", new() { new(1, 10, "test", 10, 20, 30, null) }, 5);
        Check(Insights.Rank(new() { s })[0].Cpu == 10, "Normalized CPU ranking");
        Check(Insights.Rank(new() { s, s with { Apps = new() } })[0].Cpu == 5, "Exited processes contribute zero in later intervals");
        Check(Math.Abs(Insights.Rank(new() { s, s with { Apps = new(), Seconds = 15 } })[0].Cpu - 2.5) < .001, "Sampling duration weights");
        Check(Math.Abs(Insights.CpuSeconds(new() { s }, "test") - .5 * Environment.ProcessorCount) < .001, "CPU-seconds conversion");
        Check(Insights.Findings(new() { s, s, s }).Contains("25.0 W"), "Battery watts preserved");
        Check(Insights.Rank(new() { s with { Apps = new() { new(1, 10, "same", 2, 10, 0, null), new(2, 20, "same", 3, 15, 0, null) } } })[0].Cpu == 5, "Related process grouping");
        Check(Analytics.Correlations(new() { s }).Contains("eight"), "Insufficient-data explanation");
        Check(Analytics.SleepGaps(new() { s, s with { Time = s.Time.AddHours(1), Remaining = 24000 } }).Contains("1.00 Wh"), "Sleep gap energy");
        Check(Diagnostics.RecorderIdle("WPR is not recording") && !Diagnostics.RecorderIdle("Recording is running") && !Diagnostics.RecorderIdle("Access denied"), "Existing recorder and unknown status protection");
        {
            var t0 = DateTime.UtcNow.AddMinutes(-10);
            var rd = new RangeData(t0, t0.AddMinutes(10), 10);
            Sample Mk(double sec, double cpu, string app, double w) => new(t0.AddMinutes(1).AddSeconds(sec), false, w, 50, 25000, 50000, 52000, cpu, null, null, null, null, "Balanced", new() { new(1, 10, app, cpu, 20, 0, null), new(2, 11, app, cpu, 30, 0, null) }, 5);
            rd.Add(Mk(0, 10, "x", 10)); rd.Add(Mk(5, 20, "x", 20)); rd.Add(Mk(10, 0, "y", 30) with { Seconds = 0 }); rd.Finish();
            var x = rd.App("x")!; Check(rd.App("y") == null, "Zero-duration samples are ignored");
            Check(Math.Abs(x.AvgCpu - 2 * 15) < .001, "Per-app CPU groups instances and weights by duration (10 s, 2 instances)");
            Check(Math.Abs(x.CpuSeconds - (20 * 5 + 40 * 5) / 100.0 * Environment.ProcessorCount) < .001, "Per-app CPU-seconds");
            Check(x.Inst.Count == 2 && Math.Abs(x.Share - 100) < .001, "Instances and share of CPU time");
            Check(rd.Watts().Count(double.IsFinite) == 1 && Math.Abs(rd.Watts().First(double.IsFinite) - 15) < .001, "Bucket watts weighted; empty buckets are gaps");
            var rs = new RangeData(t0, t0.AddMinutes(10), 10); rs.Add(Mk(0, 5, "z", 5)); rs.Add(new Sample(t0.AddMinutes(14), false, 5, 50, 1, 1, 1, 5, null, null, null, null, "Balanced", new(), 5)); Check(rs.To > t0.AddMinutes(10) && rs.App("z") != null, "Live window slides forward on newer samples");
        }
        {
            var hang = MonitorHost.ReadWithTimeout(() => { Thread.Sleep(3000); return s; }, TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
            Check(hang.TimedOut && !hang.Read.IsCompleted, "Collector read timeout returns instead of blocking");
            var ok = MonitorHost.ReadWithTimeout(() => s, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); Check(!ok.TimedOut && ok.Read.Result == s, "Collector read completes normally");
        }        {
            // ---- network: classification, adapter deltas, store behaviour
            var cases = new (string Ip, Network.IpScope Want)[] { ("10.1.2.3", Network.IpScope.Private), ("172.20.0.1", Network.IpScope.Private), ("8.8.8.8", Network.IpScope.Public), ("169.254.1.1", Network.IpScope.LinkLocal), ("100.64.0.1", Network.IpScope.CarrierGrade), ("127.0.0.1", Network.IpScope.Loopback), ("0.0.0.0", Network.IpScope.Unspecified), ("224.0.0.251", Network.IpScope.Multicast), ("fe80::1%5", Network.IpScope.LinkLocal), ("fd00::1", Network.IpScope.Private), ("2606:4700::1", Network.IpScope.Public), ("::1", Network.IpScope.Loopback), ("ff02::fb", Network.IpScope.Multicast), ("2001:db8::1", Network.IpScope.Special) };
            Check(cases.All(c => Network.IpClassifier.Classify(c.Ip) == c.Want), "IP scope classification (IPv4 and IPv6 ranges)");
            Check(Network.BasicCollector.Delta(100, 100, 150, 130, 5, 5) is (50, 30) && Network.BasicCollector.Delta(100, 100, 90, 130, 5, 5) == null && Network.BasicCollector.Delta(100, 100, 150, 130, 400, 5) == null && Network.BasicCollector.Delta(1, 1, 2, 2, 0, 5) == null, "Adapter deltas reject counter resets, gaps and zero elapsed time");
            Check(Network.ApplicationIdentity.Unattributed(5).Path != Network.ApplicationIdentity.Unattributed(6).Path, "Unresolved processes are never merged");
            string ntemp = Path.Combine(Path.GetTempPath(), "SurfaceInsightNet-" + Guid.NewGuid()); Directory.CreateDirectory(ntemp);
            using (var hh = new History(ntemp)) hh.Add(s);
            using (var ns = new Network.NetworkStore(ntemp))
            {
                using (var hh = new History(ntemp)) Check(hh.Read(DateTime.UtcNow.AddHours(-1)).Count == 1, "Network migration leaves battery history intact");
                var t0 = DateTime.UtcNow.AddMinutes(-30); var appA = new Network.ApplicationIdentity(@"C:\a\app.exe", "app.exe", true); var appB = new Network.ApplicationIdentity(@"C:\b\app.exe", "app.exe", true);
                Network.NetSnapshot Snap(DateTime t, params (Network.ApplicationIdentity App, string Ip, ushort Port)[] rows) { var procs = new Dictionary<int, (Network.ProcessInstanceKey, Network.ApplicationIdentity)>(); var conns = new List<Network.Conn>(); int pid = 100; foreach (var r in rows) { procs[pid] = (new(pid, 1), r.App); conns.Add(new Network.Conn(6, "192.168.1.5", 50000, r.Ip, r.Port, 0, 5, pid)); pid++; } return new Network.NetSnapshot(t, conns, new(), new(), null, procs); }
                for (int i = 0; i < 3; i++) ns.Write(Snap(t0.AddSeconds(i * 5), (appA, "8.8.8.8", 443), (appB, "8.8.8.8", 443), (appA, "10.0.0.7", 80)));
                var f0 = new Network.NetFilter(); var apps = ns.Apps(t0.AddMinutes(-1), DateTime.UtcNow, f0);
                Check(apps.Count == 2 && apps.All(a => a.Name == "app.exe"), "Same-named executables in different folders stay separate applications");
                var dests = ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, f0, "last", null);
                Check(dests.Count == 2 && dests.Single(x => x.Ip == "8.8.8.8").AppCount == 2 && dests.Single(x => x.Ip == "8.8.8.8").Observations == 6, "Destinations aggregate apps and samples without duplicating");
                Check(ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, apps[0].Id, f0, "last", null).Sum(x => x.Observations) == 6 || ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, apps[0].Id, f0, "last", null).Sum(x => x.Observations) == 3, "Per-application destination filter");
                var fp = new Network.NetFilter { Search = "8.8" }; Check(ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, fp, "last", null).Count == 1, "Literal IP search");
                var fw = new Network.NetFilter { Search = "%" }; Check(ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, fw, "last", null).Count == 0 && ns.Apps(t0.AddMinutes(-1), DateTime.UtcNow, fw).Count == 0, "Search treats % and _ literally");
                var f6 = new Network.NetFilter(); f6.Scopes.Add((int)Network.IpScope.Public); Check(ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, f6, "last", null).Count == 1, "Address scope filter");
                // keyset paging over many destinations: no duplicates, no gaps
                var rows = new List<(Network.ApplicationIdentity, string, ushort)>(); for (int i = 0; i < 250; i++) rows.Add((appA, "93.184." + (i / 250) + "." + (i % 250 + 1), 443));
                for (int m = 0; m < 5; m++) ns.Write(Snap(t0.AddMinutes(m + 1), rows.ToArray()));
                var all = new List<Network.DestRow>(); (long, long)? cur = null; for (int guard = 0; guard < 20; guard++) { var page = ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, f0, "last", cur, 60); if (page.Count == 0) break; all.AddRange(page); cur = (ns.SortValue(page[^1], "last"), page[^1].Gid); }
                Check(all.Count == 252 && all.Select(x => x.Gid).Distinct().Count() == 252, "Keyset paging returns every destination exactly once");
                long before = ns.Destinations(t0.AddHours(-1), DateTime.UtcNow, null, f0, "last", null, 1000).Sum(x => x.Observations);
                var future = DateTime.UtcNow.AddDays(3); ns.Maintain(future.AddDays(-1)); // compaction only: everything is older than 48 h relative to that clock
                long after = ns.Destinations(t0.AddHours(-1), DateTime.UtcNow, null, f0, "last", null, 1000).Sum(x => x.Observations);
                Check(before == after, "15-minute compaction preserves sample totals");
                ns.Clear(); Check(ns.Apps(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, f0).Count == 0 && ns.Totals(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).Endpoints == 0, "Clear network history");
                ns.Write(Snap(DateTime.UtcNow.AddDays(-40), (appA, "1.1.1.1", 443))); ns.Maintain(); Check(ns.Totals(DateTime.UtcNow.AddDays(-60), DateTime.UtcNow).Endpoints == 0, "30-day expiry removes old network rows");
            }
        }        {
            // ---- enhanced session: protocol, decoding, aggregation, naming, byte storage
            static byte[] Frame(byte[] body) { var b = new byte[4 + body.Length]; BitConverter.TryWriteBytes(b.AsSpan(0, 4), body.Length); body.CopyTo(b, 4); return b; }
            async Task<Network.Msg?> Read(byte[] bytes) { using var ms = new MemoryStream(bytes); return await Network.Ipc.ReadAsync(ms, CancellationToken.None); }
            bool Throws(byte[] bytes) { try { Read(bytes).GetAwaiter().GetResult(); return false; } catch (InvalidDataException) { return true; } }
            var okMsg = Network.Ipc.Json is var jo ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Network.Msg { T = "hb", Pid = 7 }, jo) : null!;
            Check(Read(Frame(okMsg)).GetAwaiter().GetResult() is { T: "hb", Pid: 7 }, "IPC frame round trip");
            Check(Throws(BitConverter.GetBytes(Network.Ipc.MaxFrameBytes + 1)) && Throws(BitConverter.GetBytes(0)) && Throws(Frame(System.Text.Encoding.UTF8.GetBytes("{not json"))) && Throws(Frame(System.Text.Encoding.UTF8.GetBytes("{\"t\":\"rm-rf\"}"))), "IPC rejects oversized, empty, malformed and unknown-type frames");
            Check(Network.EtwDecode.Ip4(16777343) == "127.0.0.1" && Network.EtwDecode.Port(-17663) == 443 && Network.EtwDecode.Port(unchecked((short)0xBB01)) == 443 && Network.EtwDecode.RemoteIsSource(Network.EtwDecode.UdpRecv) && !Network.EtwDecode.RemoteIsSource(Network.EtwDecode.TcpRecv) && Network.EtwDecode.IsData(26) && !Network.EtwDecode.IsData(18) && !Network.EtwDecode.IsData(16), "ETW decoding: byte order, ports, orientation; event 18/16 are not byte events");
            var agg = new Network.FlowAggregator(); ulong total = 0; for (int i = 0; i < Network.FlowAggregator.MaxGroups + 500; i++) { agg.Add(new Network.FlowEvent(1000 + i % 50, 6, "10.0.0." + (i % 250) + "." + i, (ushort)(i % 60000), 100, i % 2 == 0)); total += 100; }
            var drained = agg.Drain(pid => (1, "C:\\x.exe")); Check(drained.Count <= Network.FlowAggregator.MaxGroups + 1 && drained.Sum(x => (long)(x.Sent + x.Recv)) == (long)total && drained.Any(x => x.Pid == -1) && agg.FlowsDropped >= 500, "Flow aggregation is bounded and keeps overflow bytes as unattributed");
            var ev = new[] { new Network.NameRow("a.example", "1.2.3.4", 5, 1000, "3008"), new Network.NameRow("b.example", "1.2.3.4", 0, 1100, "3020"), new Network.NameRow("old.example", "1.2.3.4", 5, 100, "3008") };
            var nr = Network.NameCorrelator.Resolve("1.2.3.4", new long[] { 5 }, 1000 / 60 + 1, 1200 / 60, ev);
            Check(nr.Name == "a.example" && nr.Kind == Network.NameEvidenceKind.ProcessDns && nr.OtherNames == 1 && !nr.Candidates.Contains("old.example"), "Names: process match wins, other candidates kept, stale evidence outside the 5-minute window ignored");
            Check(Network.NameCorrelator.Resolve("1.2.3.4", new long[] { 9 }, 1000 / 60, 1200 / 60, ev).Kind == Network.NameEvidenceKind.SystemDns && Network.NameCorrelator.Resolve("9.9.9.9", new long[] { 5 }, 1, 2, ev).Kind == Network.NameEvidenceKind.Unknown, "Names: another app's evidence is only a system candidate; no evidence stays unknown");
            var ptrOnly = new[] { new Network.NameRow("server-1.cdn.example", "5.6.7.8", 0, 1, "ptr") }; var both = ptrOnly.Append(new Network.NameRow("api.example.com", "5.6.7.8", 5, 1000, "3008")).ToArray();
            Check(Network.NameCorrelator.Resolve("5.6.7.8", new long[] { 5 }, 1000 / 60, 1200 / 60, ptrOnly).Kind == Network.NameEvidenceKind.ReverseDns && Network.NameCorrelator.Resolve("5.6.7.8", new long[] { 5 }, 1000 / 60, 1200 / 60, both).Name == "api.example.com", "Names: reverse DNS is only a fallback and is labelled as such");
            string etemp = Path.Combine(Path.GetTempPath(), "SurfaceInsightEnh-" + Guid.NewGuid()); Directory.CreateDirectory(etemp);
            using (var ns = new Network.NetworkStore(etemp))
            {
                long t = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds(); ns.BeginEnhanced(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime); string exe = @"C:\apps\one.exe";
                Network.Msg B(long utc, ulong sent, ulong recv, string? path, string ip = "93.184.216.34") => new() { T = "batch", Utc = utc, Buckets = new() { new() { Pid = 10, Start = 1, Path = path, Proto = 6, Ip = ip, Port = 443, Sent = sent, Recv = recv } } };
                ns.WriteBatch(B(t, 100, 1000, exe)); ns.WriteBatch(B(t + 5, 50, 500, exe)); ns.WriteBatch(B(t + 30, 10, 20, null)); ns.WriteBatch(new Network.Msg { T = "batch", Utc = t + 31, Lost = 3, Names = new() { new() { Name = "example.test", Ips = new() { "93.184.216.34" }, Pid = 10, Path = exe, Src = "3008", T = t } } });
                ns.TouchEnhanced(DateTimeOffset.FromUnixTimeSeconds(t + 60).UtcDateTime); var from = DateTimeOffset.FromUnixTimeSeconds(t - 60).UtcDateTime; var to = DateTime.UtcNow.AddMinutes(1); var nf = new Network.NetFilter();
                var one = ns.Apps(from, to, nf).First(a => a.Path == exe); Check(one.Sent == 150 && one.Recv == 1500, "Enhanced bytes aggregate per application");
                Check(ns.Apps(from, to, nf).Any(a => !a.Resolved && a.Sent == 10 && a.Recv == 20), "Bytes without a resolvable path stay unattributed, not merged");
                var dst = ns.Destinations(from, to, null, nf, "bytes", null); Check(dst.Count == 1 && dst[0].Sent == 160 && dst[0].Recv == 1520 && dst[0].AppCount == 2, "Destination bytes are summed once across applications");
                Check(ns.Totals(from, to).EnhancedCoverage is > 0 and < 1 && ns.NameRows(new[] { "93.184.216.34" }, from, to).Count == 1 && ns.CoverageNotes(from, to).Any(c => c.Kind == "etw-events-lost"), "Enhanced coverage, name evidence and loss notes are stored");
                var (ts, tr) = ns.TrafficSeries(DateTimeOffset.FromUnixTimeSeconds(t - 600).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(t + 600).UtcDateTime, 20); Check(ts.Any(double.IsNaN) && ts.Any(x => x > 0), "Traffic outside enhanced coverage is a gap (NaN), not zero");
                long before = ns.Totals(from, to).Recv; ns.Maintain(DateTime.UtcNow.AddDays(3)); long after = ns.Totals(from, to.AddDays(4)).Recv; Check(before == after && after == 1520, "Byte compaction to minutes preserves totals");
                ns.Clear(); Check(ns.NameRows(new[] { "93.184.216.34" }, from, to).Count == 0 && ns.Totals(from, to).Sent == 0, "Clear removes bytes and names");
            }
        }        {
            // ---- session blocking: what may be blocked, wire types, confirmed-state parsing, audit and filters
            var own = new[] { @"C:\Program Files\SurfaceInsight\SurfaceInsight.exe" }; string? R(string? p, bool exists = true) => Network.PolicyRules.Reject(p, own, _ => exists);
            Check(R(@"D:\tools\app.exe") == null && R(@"C:\Users\me\AppData\Local\Prog\prog.exe") == null, "Policy: ordinary executables may be blocked");
            Check(R(null) != null && R("") != null && R("unattributed:12") != null && R(@"relative\app.exe") != null && R(@"\\server\share\a.exe") != null && R(@"\\?\C:\a.exe") != null && R(@"D:\tools\readme.txt") != null && R(@"D:\tools\app.exe", false) != null, "Policy: unresolved, relative, network, non-exe and missing targets are refused");
            Check(R(own[0]) != null && R(@"D:\x\SVCHOST.EXE") != null && R(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "curl.exe")) != null && R(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", "Pkg", "a.exe")) != null, "Policy: itself, shared hosts, the Windows folder and packaged apps are refused");
            Check(Network.Ipc.Parse(System.Text.Encoding.UTF8.GetBytes("{\"t\":\"block-add\",\"req\":4,\"path\":\"D:\\\\a.exe\"}")) is { T: "block-add", Req: 4 } && Network.Ipc.Parse(System.Text.Encoding.UTF8.GetBytes("{\"t\":\"block-result\",\"ok\":true,\"blocks\":[\"D:\\\\a.exe\"]}")).Blocks!.Count == 1, "IPC accepts the fixed block message types");
            string old = Environment.GetEnvironmentVariable("SURFACEINSIGHT_DATA") ?? ""; string btemp = Path.Combine(Path.GetTempPath(), "SurfaceInsightBlk-" + Guid.NewGuid()); Directory.CreateDirectory(btemp);
            try
            {
                Environment.SetEnvironmentVariable("SURFACEINSIGHT_DATA", btemp);
                File.WriteAllLines(Path.Combine(btemp, "network.blocks"), new[] { "state|Active|2026-11-04T10:00:00.0000000Z", @"block|D:\a\x.exe|2026-11-04T10:01:00.0000000Z", "result|7|ok|Temporary block is active.", "result|8|fail|svchost.exe is a shared system host; blocking it would affect unrelated services.", "ended|2026-11-04T09:00:00.0000000Z|Enhanced monitoring stopped; Windows removed the blocks." });
                var bs = Network.NetworkSettings.ReadBlocks(); Check(bs.Blocks.Count == 1 && bs.Blocks[0].Path == @"D:\a\x.exe" && bs.Results[7].Ok && !bs.Results[8].Ok && bs.Results[8].Message.Contains("shared") && bs.EndedNote!.Contains("removed"), "Confirmed block state parses (only broker results count)");
                File.Delete(Path.Combine(btemp, "network.blocks")); Check(Network.NetworkSettings.ReadBlocks().Blocks.Count == 0, "No block file means no blocks");
            }
            finally { Environment.SetEnvironmentVariable("SURFACEINSIGHT_DATA", old.Length == 0 ? null : old); }
            using (var ns = new Network.NetworkStore(btemp))
            {
                var t0 = DateTime.UtcNow.AddMinutes(-5); var a1 = new Network.ApplicationIdentity(@"D:\a\one.exe", "one.exe", true); var a2 = new Network.ApplicationIdentity(@"D:\b\two.exe", "two.exe", true);
                var procs = new Dictionary<int, (Network.ProcessInstanceKey, Network.ApplicationIdentity)> { [1] = (new(1, 1), a1), [2] = (new(2, 1), a2) };
                ns.Write(new Network.NetSnapshot(t0, new() { new Network.Conn(6, "10.0.0.2", 5000, "8.8.8.8", 443, 0, 5, 1), new Network.Conn(6, "10.0.0.2", 5001, "1.1.1.1", 443, 0, 5, 2) }, new(), new(), null, procs));
                var onlyOne = new Network.NetFilter { OnlyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"d:\a\ONE.exe" } }; var nf = new Network.NetFilter();
                Check(ns.Apps(t0.AddMinutes(-1), DateTime.UtcNow, onlyOne).Single().Path == @"D:\a\one.exe" && ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, null, onlyOne, "last", null).Single().Ip == "8.8.8.8" && ns.Apps(t0.AddMinutes(-1), DateTime.UtcNow, new Network.NetFilter { OnlyPaths = new() }).Count == 0, "Blocked-only filter matches exact paths case-insensitively");
                ns.Audit("block-add", @"D:\a\one.exe"); ns.Audit("block-failed", "x"); ns.Audit("blocks-ended", "session ended"); var ev = ns.AuditEvents(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)); Check(ev.Count == 2 && ev.Any(e => e.Kind == "block-add") && ev.Any(e => e.Kind == "blocks-ended"), "Audit events for timeline markers exclude internal failures");
                Check(ns.AppIdsByName("one").Count == 1 && ns.AppIdsByName("ONE").Single().Path == @"D:\a\one.exe" && ns.AppIdsByName("three").Count == 0, "App drill-down finds its network history by process name");
                var mine = ns.Destinations(t0.AddMinutes(-1), DateTime.UtcNow, ns.AppIdsByName("two").Single().Id, nf, "last", null); Check(mine.Count == 1 && mine[0].Ip == "1.1.1.1", "App drill-down lists only that app's destinations");
            }
        }        {
            // ---- draw explanation and the network switch binding
            var fx = new List<double[]>(); var fy = new List<double>(); for (int i = 0; i < 40; i++) { double fb = 30 + (i % 8) * 10, fc = 5 + (i % 5) * 4; fx.Add(new[] { fb, fc }); fy.Add(4 + 0.05 * fb + 0.1 * fc); }
            var fit = DrawExplainer.Fit(fx, fy); Check(fit is { } ft && Math.Abs(ft.Coef[1] - 0.05) < 1e-3 && Math.Abs(ft.Coef[2] - 0.1) < 1e-3 && ft.R2 > 0.999, "Draw model recovers known brightness and CPU effects");
            var t0 = DateTime.UtcNow.AddMinutes(-60); var rd = new RangeData(t0, t0.AddMinutes(60), 60); var rnd = new Random(3);
            for (int i = 0; i < 60; i++) { double br = i < 50 ? 40 + (i % 3) * 5 : 100, cpu = 8 + rnd.NextDouble() * 6; rd.Add(new Sample(t0.AddMinutes(i).AddSeconds(30), false, 5 + 0.05 * br + 0.12 * cpu + (rnd.NextDouble() - .5) * .2, 60, 30000, 50000, 52000, cpu, 3, null, null, br, "Balanced", new(), 30)); }
            var ex = DrawExplainer.Explain(rd); var brf = ex?.Factors.FirstOrDefault(x => x.Name == "Screen brightness");
            Check(ex != null && ex.ModelOk && brf?.EstWatts is double bw && bw > 2.4 && bw < 3.6, "Brightness rise outdoors is attributed (≈ +3 W expected)");
            Check(DrawExplainer.Events(rd).Any(e => e.Label.StartsWith("Brightness") && e.Label.EndsWith("100%")), "Brightness step appears as a timeline note");
            var node = new AppNode { AppId = 1, Key = "a1" }; var cb = new System.Windows.Controls.CheckBox { DataContext = node }; cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Allowed") { Mode = System.Windows.Data.BindingMode.OneWay });
            ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(cb)).Toggle(); cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, node.Allowed); node.Allowed = false; bool off = cb.IsChecked == false; node.Allowed = true;
            Check(off && cb.IsChecked == true, "Network switch keeps following confirmed block state after a click");
            // ---- NPU: engines on the compute-only adapter are NPU, not GPU
            var npuSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0x00000000_0x000119F2" };
            var acc = GpuQuery.Split(new[] { ("pid_10_luid_0x00000000_0x000119f2_phys_0_eng_0_engtype_Compute", 30.0), ("pid_11_luid_0x00000000_0x000119F2_phys_0_eng_0_engtype_Compute", 25.0), ("pid_10_luid_0x00000000_0x00011565_phys_0_eng_0_engtype_3D", 12.0), ("pid_12_luid_0x00000000_0x00011565_phys_0_eng_5_engtype_Compute", 7.0) }, npuSet);
            Check(acc.Npu != null && acc.Npu[10] == 30 && acc.Npu[11] == 25 && acc.NpuTotal == 55 && acc.Gpu[10] == 12 && acc.Gpu[12] == 7 && !acc.Gpu.ContainsKey(11), "NPU engines are split from GPU and summed per engine");
            Check(GpuQuery.Split(new[] { ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 5.0) }, new HashSet<string>()).Npu == null, "No NPU adapter means NPU is unavailable, not zero");
            var oldJson = "{\"Time\":\"2026-11-01T10:00:00Z\",\"Ac\":false,\"Watts\":7,\"Percent\":50,\"Cpu\":3,\"Gpu\":1,\"Mode\":\"Balanced\",\"Apps\":[{\"Pid\":1,\"Start\":2,\"Name\":\"x\",\"Cpu\":1,\"Memory\":2,\"Io\":0,\"Gpu\":null}],\"Seconds\":5}";
            var os = System.Text.Json.JsonSerializer.Deserialize<Sample>(oldJson)!; var nsamp = System.Text.Json.JsonSerializer.Deserialize<Sample>(System.Text.Json.JsonSerializer.Serialize(os with { Npu = 42, Apps = new() { os.Apps[0] with { Npu = 40 } } }))!;
            Check(os.Npu == null && os.Apps[0].Npu == null && nsamp.Npu == 42 && nsamp.Apps[0].Npu == 40, "History from before NPU support still loads");
            var rn = new RangeData(t0, t0.AddMinutes(10), 10); for (int i = 0; i < 10; i++) rn.Add(new Sample(t0.AddMinutes(i).AddSeconds(30), false, 6, 60, null, null, null, 5, 2, null, null, null, "Balanced", new() { new AppUsage(5, 1, "copilot", 1, 100, 0, 0, i < 5 ? 0 : 60) }, 60, i < 5 ? 0 : 60)); rn.Finish();
            Check(rn.HasNpu && Math.Abs(rn.App("copilot")!.AvgNpu!.Value - 30) < 0.01 && rn.App("copilot")!.Npu == 60 && rn.Npu()[9] == 60, "NPU device and per-app series aggregate by duration");
        }        string temp = Path.Combine(Path.GetTempPath(), "SurfaceInsightTest-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
        using (var h = new History(temp))
        {
            h.Add(s); Check(h.Read(DateTime.UtcNow.AddHours(-1)).Count == 1, "SQLite persistence");
            h.Add(s with { Time = DateTime.UtcNow.AddDays(-31) }); h.Maintain(); Check(h.Read(DateTime.UtcNow.AddYears(-1)).Count == 1, "30-day expiration");
            var old = DateTime.UtcNow.AddDays(-3); old = new DateTime(old.Year, old.Month, old.Day, old.Hour, old.Minute, 0, DateTimeKind.Utc);
            h.Add(s with { Time = old, Npu = 20 }); h.Add(s with { Time = old.AddSeconds(10), Npu = 40 }); h.Maintain(); var archived = h.Read(old.AddSeconds(-1)).Where(x => x.Time < old.AddMinutes(1)).ToList();
            Check(archived.Count == 1 && archived[0].Seconds == 10 && archived[0].Npu == 30, "Minute aggregation preserves observed duration and NPU");
            h.Clear(); Check(h.Read(DateTime.UtcNow.AddYears(-1)).Count == 0, "Clear history");
        }
        string battery = Path.Combine(temp, "battery.xml"); File.WriteAllText(battery, "<BatteryReport xmlns='test'><Battery><FullChargeCapacity>50000</FullChargeCapacity><DesignCapacity>52000</DesignCapacity><CycleCount>12</CycleCount></Battery><UsageEntry Ac='0' Duration='36000000000' Discharge='10000' EntryType='Active' LocalTimestamp='test'/></BatteryReport>");
        Check(DeepAnalysis.Battery(battery).Contains("10.00 W"), "Battery XML time and capacity conversion");
        Check(DeepAnalysis.Battery(battery).Contains("50.00 Wh"), "Battery XML namespace independence");
        Check(DeepAnalysis.Sleep(Path.Combine(temp, "missing.xml")).Contains("unavailable"), "Missing report handled");
        Check(DeepAnalysis.Trace(Path.Combine(temp, "missing.etl")).Contains("unavailable"), "Missing trace handled");
        Check(Platform.ArchLabel(Platform.MachArm64, System.Runtime.InteropServices.Architecture.Arm64) == "Arm64" && Platform.ArchLabel(Platform.MachX64, System.Runtime.InteropServices.Architecture.Arm64) == "x64 · emulated" && Platform.ArchLabel(Platform.MachX86, System.Runtime.InteropServices.Architecture.X64) == "x86 (32-bit)" && Platform.ArchLabel(Platform.MachX64, System.Runtime.InteropServices.Architecture.X64) == "x64" && Platform.ArchLabel(null, System.Runtime.InteropServices.Architecture.X64) == "—", "Architecture labels (native vs emulated)");
        Check(Platform.ProcessMachine(Environment.ProcessId) == (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? Platform.MachArm64 : Platform.MachX64), "This process reports its own architecture");
        Check(System.Text.Json.JsonSerializer.Deserialize<AppUsage>("{\"Pid\":1,\"Start\":2,\"Name\":\"old\",\"Cpu\":1,\"Memory\":1,\"Io\":0,\"Gpu\":null,\"Npu\":null}")?.Mach == null, "History saved before the architecture column still loads");
        { string bd = AppContext.BaseDirectory, nd = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64"; var missing = new[] { "e_sqlite3.dll", Path.Combine(nd, "KernelTraceControl.dll"), Path.Combine(nd, "msdia140.dll"), Path.Combine("wpt", "perfcore.dll"), Path.Combine("Assets", "ne_50m_land.geojson") }.Where(f => !File.Exists(Path.Combine(bd, f))).ToList(); Check(missing.Count == 0, "Bundled native libraries and map data are present" + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing) + ")" : "")); }
        UpdaterTests.Run(Check, reason => { results.Add("SKIP " + reason); skipped++; });
        Check(Platform.Describe(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.Architecture.X64) == "Version " + Platform.Version + " · X64 build (native)" && Platform.Describe(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.Architecture.Arm64).EndsWith("(emulated on Arm64)") && Platform.Emulated_(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.Architecture.Arm64) && !Platform.Emulated_(System.Runtime.InteropServices.Architecture.Arm64, System.Runtime.InteropServices.Architecture.Arm64), "Platform description distinguishes native and emulated builds");
        results.Add("INFO " + Platform.Describe() + " · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + (ci ? " · CI mode: live hardware checks skipped" : ""));
        if (!ci) { var c = new Collector(); c.Read(); Thread.Sleep(1200); var live = c.Read();
        Check(live.Cpu >= 0 && live.Cpu <= 100, "Live total CPU range"); Check(live.Apps.All(a => a.Cpu >= 0 && a.Cpu <= 100), "Live process CPU range"); Check(live.Remaining > 0 && live.Full > 0, "Live battery capacity"); Check(live.Gpu == null || live.Gpu >= 0 && live.Gpu <= 100, "PDH GPU range"); using (var gq = new GpuQuery()) Check((live.Npu != null) == gq.HasNpu && (live.Npu == null || live.Npu >= 0 && live.Npu <= 100), "NPU reading " + (gq.HasNpu ? "present for " + (GpuQuery.NpuName() ?? "the NPU") : "absent (no NPU adapter)")); }
        results.Add($"{passed} passed; {failed} failed; {skipped} skipped.");
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "self-test.txt"), results); Environment.ExitCode = failed == 0 ? 0 : 1;
    }
}

