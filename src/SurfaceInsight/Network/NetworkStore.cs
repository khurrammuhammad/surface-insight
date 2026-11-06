using System.Text;
using Microsoft.Data.Sqlite;
namespace SurfaceInsight.Network;

/// <summary>Small file-based switches shared by the tray host and the dashboard (network collection is off until the user enables it).</summary>
public static class NetworkSettings
{
    public static string Folder => MonitorHost.Folder;
    static string EnabledFile => Path.Combine(Folder, "network.enabled");
    public static string StatusFile => Path.Combine(Folder, "network.status");
    public static bool IsEnabled => File.Exists(EnabledFile);
    static string PtrFile => Path.Combine(Folder, "network.ptr");
    /// <summary>Opt-in automatic reverse-DNS (PTR) lookups for unnamed public addresses. Each lookup sends a DNS query to the configured resolver.</summary>
    public static bool PtrEnabled => File.Exists(PtrFile);
    public static void SetPtr(bool on) { Directory.CreateDirectory(Folder); if (on) File.WriteAllText(PtrFile, DateTime.UtcNow.ToString("O")); else File.Delete(PtrFile); }
    public static void SetEnabled(bool on) { Directory.CreateDirectory(Folder); if (on) File.WriteAllText(EnabledFile, DateTime.UtcNow.ToString("O")); else File.Delete(EnabledFile); }

    public sealed record BlockState(IReadOnlyList<(string Path, DateTime SinceUtc)> Blocks, IReadOnlyDictionary<long, (bool Ok, string Message)> Results, string? EndedNote, DateTime? EndedUtc);
    /// <summary>Broker-confirmed temporary blocks as last written by the tray host. Empty when no session or no blocks.</summary>
    public static BlockState ReadBlocks()
    {
        var blocks = new List<(string, DateTime)>(); var results = new Dictionary<long, (bool, string)>(); string? note = null; DateTime? endedAt = null;
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Folder, "network.blocks")))
            {
                var p = line.Split('|');
                if (p[0] == "block" && p.Length >= 3) blocks.Add((p[1], DateTime.TryParse(p[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : DateTime.UtcNow));
                else if (p[0] == "result" && p.Length >= 4 && long.TryParse(p[1], out var id)) results[id] = (p[2] == "ok", string.Join("|", p.Skip(3)));
                else if (p[0] == "ended" && p.Length >= 3) { note = string.Join("|", p.Skip(2)); endedAt = DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var et) ? et.ToUniversalTime() : null; }
            }
        }
        catch { }
        return new BlockState(blocks, results, note, endedAt);
    }
    public sealed record Status(NetMode Mode, DateTime? LastOkUtc, string Error, string Stage, double RxBps, double TxBps, string Adapters, string Enhanced = "Off", string EnhancedNote = "", bool DnsOk = false, long Lost = 0, long Overflow = 0, DateTime? EnhancedOkUtc = null);
    public static void WriteStatus(Status s) { try { File.WriteAllLines(StatusFile, new[] { "mode=" + s.Mode, "ok=" + (s.LastOkUtc?.ToString("O") ?? ""), "error=" + s.Error.Replace('\n', ' '), "stage=" + s.Stage, "rx=" + s.RxBps.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "tx=" + s.TxBps.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "adapters=" + s.Adapters, "enhanced=" + s.Enhanced, "enhancedNote=" + s.EnhancedNote.Replace('\n', ' '), "dns=" + s.DnsOk, "lost=" + s.Lost, "overflow=" + s.Overflow, "enhancedOk=" + (s.EnhancedOkUtc?.ToString("O") ?? "") }); } catch { } }
    public static Status ReadStatus()
    {
        try
        {
            var l = File.ReadAllLines(StatusFile); string G(string k) => l.FirstOrDefault(x => x.StartsWith(k + "="))?[(k.Length + 1)..] ?? ""; var inv = System.Globalization.CultureInfo.InvariantCulture;
            Enum.TryParse<NetMode>(G("mode"), out var m); DateTime? ok = DateTime.TryParse(G("ok"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;
            return new Status(m, ok, G("error"), G("stage"), double.TryParse(G("rx"), inv, out var rx) ? rx : 0, double.TryParse(G("tx"), inv, out var tx) ? tx : 0, G("adapters"), G("enhanced") is { Length: > 0 } en ? en : "Off", G("enhancedNote"), G("dns") == "True", long.TryParse(G("lost"), out var lo) ? lo : 0, long.TryParse(G("overflow"), out var ov) ? ov : 0, DateTime.TryParse(G("enhancedOk"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var eo) ? eo.ToUniversalTime() : null);
        }
        catch { return new Status(NetMode.Off, null, "", "", 0, 0, ""); }
    }
}

public sealed class NetFilter
{
    public string Search = ""; public int Proto, Family; public HashSet<int> Scopes = new(); public bool LiveOnly, HistoricalOnly; public HashSet<string>? OnlyPaths;
}
public sealed record AppRow(long Id, string Name, string Path, bool Resolved, int Endpoints, long Observations, long FirstMin, long LastMin, int LiveConns, long Sent = 0, long Recv = 0);
public sealed record DestRow(long Gid, long EndpointId, string Ip, int Port, int Proto, int Family, IpScope Scope, string AppName, int AppCount, long FirstMin, long LastMin, long Observations, bool Live, long Sent = 0, long Recv = 0, string AppIds = "");
public sealed record LiveRow(string AppName, string AppPath, int Proto, string LocalIp, int LocalPort, string? RemoteIp, int RemotePort, string State, int Pid, long StartFileTime, int Family);
public sealed record BindingRow(int Proto, int LocalPort, long Observations, long LastMin, bool Live);
public sealed record AdapterInfo(string Id, string Name, string Type, bool Physical);
public sealed record NetTotals(int Apps, int Endpoints, int LiveConns, long DbBytes, long FirstMin, long LastMin, long Sent = 0, long Recv = 0, double EnhancedCoverage = 0);
public sealed record NameRow(string Name, string Ip, long AppId, long T, string Src);

/// <summary>Versioned relational network tables inside history.db. The tray host writes; the dashboard only reads (and clears on request).</summary>
public sealed class NetworkStore : IDisposable
{
    public const int SchemaVersion = 3; public const long BudgetBytes = 100L * 1024 * 1024;
    readonly SqliteConnection db; readonly string folder; long generation = -1;
    readonly Dictionary<string, long> appIds = new(StringComparer.OrdinalIgnoreCase); readonly Dictionary<(string, int, uint), long> epIds = new();

    public NetworkStore(string? folder = null)
    {
        this.folder = folder ?? MonitorHost.Folder; Directory.CreateDirectory(this.folder);
        db = new("Data Source=" + Path.Combine(this.folder, "history.db") + ";Default Timeout=5"); db.Open(); Exec("PRAGMA journal_mode=WAL;"); Migrate();
    }
    public static long Epoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    public static DateTime FromMinute(long minute) => DateTimeOffset.FromUnixTimeSeconds(minute * 60).UtcDateTime;
    static long Min(DateTime utc) => Epoch(utc) / 60;
    void Exec(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    SqliteCommand Cmd(string sql, params (string, object?)[] p) { var c = db.CreateCommand(); c.CommandText = sql; foreach (var (k, v) in p) c.Parameters.AddWithValue(k, v ?? DBNull.Value); return c; }
    T Scalar<T>(string sql, T fallback, params (string, object?)[] p) { using var c = Cmd(sql, p); var v = c.ExecuteScalar(); return v == null || v is DBNull ? fallback : (T)Convert.ChangeType(v, typeof(T)); }

    void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY, v INTEGER NOT NULL)");
        int v = Scalar("SELECT v FROM schema_version WHERE component='network'", 0);
        if (v >= SchemaVersion) return;
        using var tx = db.BeginTransaction();
        if (v < 1) Exec(@"
CREATE TABLE IF NOT EXISTS net_meta(k TEXT PRIMARY KEY, v TEXT);
CREATE TABLE IF NOT EXISTS net_app(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL, resolved INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS net_endpoint(id INTEGER PRIMARY KEY, ip TEXT NOT NULL, port INTEGER NOT NULL, scope INTEGER NOT NULL, family INTEGER NOT NULL, ip_scope INTEGER NOT NULL, UNIQUE(ip,port,scope));
CREATE TABLE IF NOT EXISTS net_presence(minute INTEGER NOT NULL, app_id INTEGER NOT NULL, endpoint_id INTEGER NOT NULL, proto INTEGER NOT NULL, local_port INTEGER NOT NULL, samples INTEGER NOT NULL, PRIMARY KEY(minute,app_id,endpoint_id,proto,local_port)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS net_presence_app ON net_presence(app_id, minute);
CREATE INDEX IF NOT EXISTS net_presence_ep ON net_presence(endpoint_id, minute);
CREATE TABLE IF NOT EXISTS net_live(app_id INTEGER NOT NULL, endpoint_id INTEGER NOT NULL, proto INTEGER NOT NULL, local_ip TEXT NOT NULL, local_port INTEGER NOT NULL, state INTEGER NOT NULL, pid INTEGER NOT NULL, start_ft INTEGER NOT NULL, ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS net_adapter_info(id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, physical INTEGER NOT NULL, last_seen INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS net_adapter(minute INTEGER NOT NULL, adapter TEXT NOT NULL, rx INTEGER NOT NULL, tx INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(minute,adapter)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS net_coverage(id INTEGER PRIMARY KEY, from_t INTEGER NOT NULL, to_t INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS net_audit(id INTEGER PRIMARY KEY, t INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
INSERT OR IGNORE INTO net_meta VALUES('generation','0');");
        if (v < 2) Exec(@"
CREATE TABLE IF NOT EXISTS net_bytes(t INTEGER NOT NULL, app_id INTEGER NOT NULL, endpoint_id INTEGER NOT NULL, proto INTEGER NOT NULL, sent INTEGER NOT NULL, recv INTEGER NOT NULL, PRIMARY KEY(t,app_id,endpoint_id,proto)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS net_bytes_app ON net_bytes(app_id, t);
CREATE INDEX IF NOT EXISTS net_bytes_ep ON net_bytes(endpoint_id, t);
CREATE TABLE IF NOT EXISTS net_bytes_min(minute INTEGER NOT NULL, app_id INTEGER NOT NULL, endpoint_id INTEGER NOT NULL, proto INTEGER NOT NULL, sent INTEGER NOT NULL, recv INTEGER NOT NULL, PRIMARY KEY(minute,app_id,endpoint_id,proto)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS net_bytes_min_app ON net_bytes_min(app_id, minute);
CREATE TABLE IF NOT EXISTS net_name(id INTEGER PRIMARY KEY, name TEXT NOT NULL, ip TEXT NOT NULL, app_id INTEGER NOT NULL, t INTEGER NOT NULL, src TEXT NOT NULL, UNIQUE(name,ip,app_id,t));
CREATE INDEX IF NOT EXISTS net_name_ip ON net_name(ip, t);
CREATE VIEW IF NOT EXISTS net_traffic AS SELECT t AS ts, app_id, endpoint_id, proto, sent, recv FROM net_bytes UNION ALL SELECT minute*60, app_id, endpoint_id, proto, sent, recv FROM net_bytes_min;");
        if (v < 3) Exec("CREATE TABLE IF NOT EXISTS net_ptr(ip TEXT PRIMARY KEY, name TEXT NOT NULL, t INTEGER NOT NULL);");
        Exec("INSERT INTO schema_version(component,v) VALUES('network'," + SchemaVersion + ") ON CONFLICT(component) DO UPDATE SET v=excluded.v");
        tx.Commit();
    }

    // ------------------------------------------------------------------ writer (tray host)
    long Generation() => Scalar("SELECT v FROM net_meta WHERE k='generation'", 0L);
    long AppId(ApplicationIdentity a, long t)
    {
        if (appIds.TryGetValue(a.Path, out var id)) return id;
        using (var c = Cmd("INSERT INTO net_app(path,name,resolved,first_seen,last_seen) VALUES($p,$n,$r,$t,$t) ON CONFLICT(path) DO UPDATE SET last_seen=excluded.last_seen", ("$p", a.Path), ("$n", a.Name), ("$r", a.Resolved ? 1 : 0), ("$t", t))) c.ExecuteNonQuery();
        id = Scalar("SELECT id FROM net_app WHERE path=$p", 0L, ("$p", a.Path)); appIds[a.Path] = id; return id;
    }
    long EndpointId(string ip, int port, uint scope)
    {
        if (epIds.TryGetValue((ip, port, scope), out var id)) return id;
        var cls = (int)IpClassifier.Classify(ip); int fam = ip.Contains(':') ? 6 : 4;
        using (var c = Cmd("INSERT OR IGNORE INTO net_endpoint(ip,port,scope,family,ip_scope) VALUES($i,$p,$s,$f,$c)", ("$i", ip), ("$p", port), ("$s", (long)scope), ("$f", fam), ("$c", cls))) c.ExecuteNonQuery();
        id = Scalar("SELECT id FROM net_endpoint WHERE ip=$i AND port=$p AND scope=$s", 0L, ("$i", ip), ("$p", port), ("$s", (long)scope)); if (epIds.Count > 20000) epIds.Clear(); epIds[(ip, port, scope)] = id; return id;
    }

    /// <summary>Persists one snapshot atomically: presence per minute, the live list, and adapter byte deltas.</summary>
    public void Write(NetSnapshot s)
    {
        long g = Generation(); if (g != generation) { appIds.Clear(); epIds.Clear(); generation = g; }
        long t = Epoch(s.Utc), minute = t / 60;
        using var tx = db.BeginTransaction();
        foreach (var a in s.Adapters) { using var c = Cmd("INSERT INTO net_adapter_info VALUES($i,$n,$t,$p,$l) ON CONFLICT(id) DO UPDATE SET name=excluded.name,type=excluded.type,physical=excluded.physical,last_seen=excluded.last_seen", ("$i", a.Id), ("$n", a.Name), ("$t", a.Type), ("$p", a.Physical ? 1 : 0), ("$l", t)); c.ExecuteNonQuery(); }
        foreach (var d in s.Deltas) { using var c = Cmd("INSERT INTO net_adapter VALUES($m,$a,$rx,$tx,$ms) ON CONFLICT(minute,adapter) DO UPDATE SET rx=rx+excluded.rx,tx=tx+excluded.tx,ms=ms+excluded.ms", ("$m", minute), ("$a", d.Id), ("$rx", (long)d.Rx), ("$tx", (long)d.Tx), ("$ms", (long)(d.Seconds * 1000))); c.ExecuteNonQuery(); }
        Exec("DELETE FROM net_live");
        var seen = new HashSet<(long, long, byte, ushort)>();
        foreach (var c in s.Conns)
        {
            var (key, app) = s.Procs != null && s.Procs.TryGetValue(c.Pid, out var pr0) ? pr0 : ProcessIdentity.Resolve(c.Pid); long appId = AppId(app, t); long ep = c.HasRemote ? EndpointId(c.RemoteIp!, c.RemotePort, c.RemoteScope) : 0; ushort lp = c.HasRemote ? (ushort)0 : c.LocalPort;
            using (var l = Cmd("INSERT INTO net_live VALUES($a,$e,$p,$li,$lp,$st,$pid,$ft,$t)", ("$a", appId), ("$e", ep), ("$p", (int)c.Proto), ("$li", c.LocalIp), ("$lp", (int)c.LocalPort), ("$st", c.State), ("$pid", c.Pid), ("$ft", key.StartFileTime), ("$t", t))) l.ExecuteNonQuery();
            if (!seen.Add((appId, ep, c.Proto, lp))) continue;
            using var pr = Cmd("INSERT INTO net_presence VALUES($m,$a,$e,$p,$lp,1) ON CONFLICT DO UPDATE SET samples=samples+1", ("$m", minute), ("$a", appId), ("$e", ep), ("$p", (int)c.Proto), ("$lp", (int)lp)); pr.ExecuteNonQuery();
        }
        tx.Commit();
    }
    // ---- Enhanced (elevated broker) data: observed transport bytes in 10-second slices and DNS evidence
    long enhancedCoverageId;
    public void BeginEnhanced(DateTime utc) { long t = Epoch(utc); using var c = Cmd("INSERT INTO net_coverage(from_t,to_t,kind,detail) VALUES($t,$t,'enhanced','Enhanced session: per-application observed transport bytes and DNS evidence were collected.')", ("$t", t)); c.ExecuteNonQuery(); enhancedCoverageId = Scalar("SELECT last_insert_rowid()", 0L); Audit("enhanced-start", "Enhanced monitoring session started"); }
    public void TouchEnhanced(DateTime utc) { if (enhancedCoverageId == 0) return; using var c = Cmd("UPDATE net_coverage SET to_t=$t WHERE id=$i", ("$t", Epoch(utc)), ("$i", enhancedCoverageId)); c.ExecuteNonQuery(); }
    public void EndEnhanced(DateTime utc, string reason) { TouchEnhanced(utc); enhancedCoverageId = 0; Audit("enhanced-stop", reason); }

    /// <summary>Persists one broker batch: bytes per (app, endpoint, protocol) per 10-second slice, name evidence, and loss/overflow notes. Bytes stay on endpoints; names never multiply them.</summary>
    public void WriteBatch(Msg m)
    {
        long g = Generation(); if (g != generation) { appIds.Clear(); epIds.Clear(); generation = g; }
        long t = m.Utc > 0 ? m.Utc : Epoch(DateTime.UtcNow), slice = t / 10 * 10, minute = t / 60; var now = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
        ApplicationIdentity Identity(int pid, string? path) => path != null ? new ApplicationIdentity(path, System.IO.Path.GetFileName(path), true) : pid < 0 ? new ApplicationIdentity("unattributed:overflow", "Unattributed (flow groups over limit)", false) : ApplicationIdentity.Unattributed(pid);
        using var tx = db.BeginTransaction();
        foreach (var b in m.Buckets ?? new())
        {
            if (b.Port < 0 || b.Port > 65535 || b.Proto is not (6 or 17)) continue;
            long app = AppId(Identity(b.Pid, b.Path), t); long ep = b.Ip.Length == 0 ? 0 : EndpointId(b.Ip, b.Port, 0);
            using (var c = Cmd("INSERT INTO net_bytes VALUES($t,$a,$e,$p,$s,$r) ON CONFLICT DO UPDATE SET sent=sent+excluded.sent,recv=recv+excluded.recv", ("$t", slice), ("$a", app), ("$e", ep), ("$p", b.Proto), ("$s", (long)Math.Min(b.Sent, long.MaxValue / 4)), ("$r", (long)Math.Min(b.Recv, long.MaxValue / 4)))) c.ExecuteNonQuery();
            if (ep != 0) using (var c = Cmd("INSERT OR IGNORE INTO net_presence VALUES($m,$a,$e,$p,0,0)", ("$m", minute), ("$a", app), ("$e", ep), ("$p", b.Proto))) c.ExecuteNonQuery();
        }
        foreach (var n in m.Names ?? new())
        {
            if (n.Name.Length == 0 || n.Name.Length > 255) continue; long app = n.Pid > 0 && n.Path != null ? AppId(Identity(n.Pid, n.Path), t) : 0;
            foreach (var ip in n.Ips.Take(32)) using (var c = Cmd("INSERT OR IGNORE INTO net_name(name,ip,app_id,t,src) VALUES($n,$i,$a,$t,$s)", ("$n", n.Name), ("$i", ip), ("$a", app), ("$t", n.T > 0 ? n.T : t), ("$s", n.Src))) c.ExecuteNonQuery();
        }
        if (m.Lost > 0) Coverage(now, now, "etw-events-lost", m.Lost + " ETW events were lost; byte totals for this moment may be incomplete.");
        if (m.Overflow > 0) Coverage(now, now, "queue-overflow", m.Overflow + " events were dropped because the broker queue was full.");
        if (m.Unsupported > 0) Coverage(now, now, "unsupported-events", m.Unsupported + " events used a layout this version does not decode.");
        if (m.FlowsDropped > 0) Coverage(now, now, "flow-groups-overflow", "More than " + FlowAggregator.MaxGroups + " flow groups in one second; the excess bytes were kept as unattributed.");
        tx.Commit(); TouchEnhanced(now);
    }
    public void Audit(string kind, string detail) { using var c = Cmd("INSERT INTO net_audit(t,kind,detail) VALUES($t,$k,$d)", ("$t", Epoch(DateTime.UtcNow)), ("$k", kind), ("$d", detail)); c.ExecuteNonQuery(); }
    public void Coverage(DateTime from, DateTime to, string kind, string detail) { using var c = Cmd("INSERT INTO net_coverage(from_t,to_t,kind,detail) VALUES($a,$b,$k,$d)", ("$a", Epoch(from)), ("$b", Epoch(to)), ("$k", kind), ("$d", detail)); c.ExecuteNonQuery(); }

    // ------------------------------------------------------------------ retention
    public long DbBytes()
    {
        try { return Scalar("SELECT COALESCE(SUM(pgsize),0) FROM dbstat WHERE name LIKE 'net_%' OR name LIKE 'sqlite_autoindex_net_%'", 0L); }
        catch { return Scalar("SELECT (SELECT COUNT(*) FROM net_presence)*60 + (SELECT COUNT(*) FROM net_endpoint)*80 + (SELECT COUNT(*) FROM net_adapter)*40", 0L); }
    }
    /// <summary>Compacts presence older than 48 h to 15-minute buckets, enforces 30-day expiry and the network storage budget, then prunes unreferenced identities.</summary>
    public void Maintain(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow; long cutDetail = Min(now.AddHours(-48)), cutExpiry = Min(now.AddDays(-30));
        using (var tx = db.BeginTransaction())
        {
            using (var c = Cmd("INSERT INTO net_presence SELECT minute-(minute%15),app_id,endpoint_id,proto,local_port,SUM(samples) FROM net_presence WHERE minute<$cut AND minute%15<>0 GROUP BY minute-(minute%15),app_id,endpoint_id,proto,local_port ON CONFLICT DO UPDATE SET samples=samples+excluded.samples", ("$cut", cutDetail))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_presence WHERE minute<$cut AND minute%15<>0", ("$cut", cutDetail))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_presence WHERE minute<$e", ("$e", cutExpiry))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_adapter WHERE minute<$e", ("$e", cutExpiry))) c.ExecuteNonQuery();
            using (var c = Cmd("INSERT INTO net_bytes_min SELECT t/60,app_id,endpoint_id,proto,SUM(sent),SUM(recv) FROM net_bytes WHERE t<$cut GROUP BY t/60,app_id,endpoint_id,proto ON CONFLICT DO UPDATE SET sent=sent+excluded.sent,recv=recv+excluded.recv", ("$cut", cutDetail * 60))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_bytes WHERE t<$cut", ("$cut", cutDetail * 60))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_bytes_min WHERE minute<$e", ("$e", cutExpiry))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_name WHERE t<$e", ("$e", cutExpiry * 60))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_ptr WHERE t<$e", ("$e", cutExpiry * 60))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_audit WHERE t<$e", ("$e", cutExpiry * 60))) c.ExecuteNonQuery();
            using (var c = Cmd("DELETE FROM net_coverage WHERE to_t<$e", ("$e", cutExpiry * 60))) c.ExecuteNonQuery();
            tx.Commit();
        }
        for (int i = 0; i < 6 && DbBytes() > BudgetBytes; i++)
        {
            long oldest = Scalar("SELECT MIN(minute) FROM net_presence", 0L); long newest = Scalar("SELECT MAX(minute) FROM net_presence", 0L); if (oldest == 0 || newest <= oldest) break;
            long cut = oldest + Math.Max(1, (newest - oldest) / 10); using (var c = Cmd("DELETE FROM net_presence WHERE minute<$c", ("$c", cut))) c.ExecuteNonQuery(); using (var c = Cmd("DELETE FROM net_bytes_min WHERE minute<$c", ("$c", cut))) c.ExecuteNonQuery(); using (var c = Cmd("DELETE FROM net_bytes WHERE t<$c", ("$c", cut * 60))) c.ExecuteNonQuery(); using (var c = Cmd("DELETE FROM net_name WHERE t<$c", ("$c", cut * 60))) c.ExecuteNonQuery(); Coverage(FromMinute(oldest), FromMinute(cut), "history-shortened", "Oldest network history was removed to stay inside the 100 MB network storage budget.");
        }
        Exec("DELETE FROM net_endpoint WHERE id NOT IN (SELECT DISTINCT endpoint_id FROM net_presence UNION SELECT DISTINCT endpoint_id FROM net_bytes UNION SELECT DISTINCT endpoint_id FROM net_bytes_min)");
        Exec("DELETE FROM net_app WHERE id NOT IN (SELECT DISTINCT app_id FROM net_presence UNION SELECT DISTINCT app_id FROM net_bytes UNION SELECT DISTINCT app_id FROM net_bytes_min)");
        generation = -1; // identities may have been removed; reload caches on next write
    }
    /// <summary>Removes stored network rows. Bumps a generation so the host drops its identity caches. Does not touch session blocks.</summary>
    public void Clear()
    {
        using var tx = db.BeginTransaction();
        Exec("DELETE FROM net_presence; DELETE FROM net_live; DELETE FROM net_adapter; DELETE FROM net_bytes; DELETE FROM net_bytes_min; DELETE FROM net_name; DELETE FROM net_ptr; DELETE FROM net_endpoint; DELETE FROM net_app; DELETE FROM net_coverage; DELETE FROM net_audit;");
        Exec("UPDATE net_meta SET v=CAST(v AS INTEGER)+1 WHERE k='generation'"); tx.Commit(); generation = -1; appIds.Clear(); epIds.Clear();
    }

    // ------------------------------------------------------------------ queries (dashboard)
    static string Like(string q) => q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    string FilterSql(NetFilter f, string pAlias, string eAlias, List<(string, object?)> ps)
    {
        var sb = new StringBuilder();
        if (f.Proto != 0) { sb.Append($" AND {pAlias}.proto=$fproto"); ps.Add(("$fproto", f.Proto)); }
        if (f.Family != 0) { sb.Append($" AND {eAlias}.family=$ffam"); ps.Add(("$ffam", f.Family)); }
        if (f.Scopes.Count > 0) sb.Append($" AND {eAlias}.ip_scope IN (" + string.Join(",", f.Scopes.Select(x => (int)x)) + ")");
        if (f.OnlyPaths != null) { int i = 0; var names = new List<string>(); foreach (var pth in f.OnlyPaths.Take(100)) { names.Add("$op" + i); ps.Add(("$op" + i++, pth)); } sb.Append(" AND a.path IN (" + (names.Count == 0 ? "NULL" : string.Join(",", names)) + ")"); }
        return sb.ToString();
    }
    const string SearchApp = " AND ($q='' OR instr(lower(a.name),$q)>0 OR instr(lower(a.path),$q)>0 OR EXISTS(SELECT 1 FROM net_presence p2 JOIN net_endpoint e2 ON e2.id=p2.endpoint_id WHERE p2.app_id=a.id AND p2.minute BETWEEN $a AND $b AND (instr(lower(e2.ip),$q)>0 OR CAST(e2.port AS TEXT)=$q)) OR EXISTS(SELECT 1 FROM net_presence p4 JOIN net_endpoint e4 ON e4.id=p4.endpoint_id JOIN net_name n4 ON n4.ip=e4.ip WHERE p4.app_id=a.id AND p4.minute BETWEEN $a AND $b AND instr(n4.name,$q)>0))";

    public List<AppRow> Apps(DateTime fromUtc, DateTime toUtc, NetFilter f, int limit = 500)
    {
        var ps = new List<(string, object?)> { ("$a", Min(fromUtc)), ("$b", Min(toUtc)), ("$q", f.Search.Trim().ToLowerInvariant()), ("$lim", limit), ("$ta", Min(fromUtc) * 60), ("$tb", Min(toUtc) * 60 + 59) };
        string sql = @"SELECT a.id,a.name,a.path,a.resolved,COUNT(DISTINCT CASE WHEN p.endpoint_id<>0 THEN p.endpoint_id END),SUM(p.samples),MIN(p.minute),MAX(p.minute),
 (SELECT COUNT(*) FROM net_live l WHERE l.app_id=a.id),
 (SELECT COALESCE(SUM(t.sent),0) FROM net_traffic t WHERE t.app_id=a.id AND t.ts BETWEEN $ta AND $tb),(SELECT COALESCE(SUM(t.recv),0) FROM net_traffic t WHERE t.app_id=a.id AND t.ts BETWEEN $ta AND $tb)
 FROM net_presence p JOIN net_app a ON a.id=p.app_id LEFT JOIN net_endpoint e ON e.id=p.endpoint_id
 WHERE p.minute BETWEEN $a AND $b" + SearchApp + FilterSql(f, "p", "e", ps) + " GROUP BY a.id ORDER BY (SELECT COALESCE(SUM(t.sent+t.recv),0) FROM net_traffic t WHERE t.app_id=a.id AND t.ts BETWEEN $ta AND $tb) DESC, (SELECT COUNT(*) FROM net_live l WHERE l.app_id=a.id) DESC, MAX(p.minute) DESC, a.name COLLATE NOCASE LIMIT $lim";
        using var c = Cmd(sql, ps.ToArray()); using var r = c.ExecuteReader(); var l = new List<AppRow>();
        while (r.Read()) l.Add(new AppRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetInt64(9), r.GetInt64(10)));
        if (f.LiveOnly) l = l.Where(x => x.LiveConns > 0).ToList(); return l;
    }

    /// <summary>Destinations for the interval, optionally for one app. Keyset-paged on (sort value, group id).</summary>
    public List<DestRow> Destinations(DateTime fromUtc, DateTime toUtc, long? appId, NetFilter f, string sort, (long Value, long Gid)? after, int limit = 200)
    {
        var ps = new List<(string, object?)> { ("$a", Min(fromUtc)), ("$b", Min(toUtc)), ("$q", f.Search.Trim().ToLowerInvariant()), ("$lim", limit), ("$ta", Min(fromUtc) * 60), ("$tb", Min(toUtc) * 60 + 59) };
        string where = " WHERE p.endpoint_id<>0 AND p.minute BETWEEN $a AND $b";
        if (appId is long id) { where += " AND p.app_id=$app"; ps.Add(("$app", id)); }
        where += " AND ($q='' OR instr(lower(e.ip),$q)>0 OR CAST(e.port AS TEXT)=$q OR instr(lower(a.name),$q)>0 OR instr(lower(a.path),$q)>0)" + FilterSql(f, "p", "e", ps);
        string live = "EXISTS(SELECT 1 FROM net_live l WHERE l.endpoint_id=e.id AND l.proto=p.proto)";
        string having = f.LiveOnly ? " HAVING " + live : f.HistoricalOnly ? " HAVING NOT " + live : "";
        string valueExpr = sort == "observed" ? "SUM(p.samples)" : sort == "bytes" ? "(MAX(COALESCE(trd.s,0))+MAX(COALESCE(trd.r,0)))" : "MAX(p.minute)"; string gid = "(e.id*32+p.proto)";
        if (after is var (av, ag)) { having += (having.Length == 0 ? " HAVING " : " AND ") + $"({valueExpr} < $av OR ({valueExpr} = $av AND {gid} < $ag))"; ps.Add(("$av", av)); ps.Add(("$ag", ag)); }
        string trdWhere = appId is long ? " AND app_id=$app" : "";
        string sql = $@"WITH trd AS (SELECT endpoint_id,proto,SUM(sent) s,SUM(recv) r FROM net_traffic WHERE ts BETWEEN $ta AND $tb{trdWhere} GROUP BY endpoint_id,proto)
 SELECT {gid},e.id,e.ip,e.port,p.proto,e.family,e.ip_scope,MIN(a.name),COUNT(DISTINCT p.app_id),MIN(p.minute),MAX(p.minute),SUM(p.samples),{live},MAX(COALESCE(trd.s,0)),MAX(COALESCE(trd.r,0)),GROUP_CONCAT(DISTINCT p.app_id)
 FROM net_presence p JOIN net_endpoint e ON e.id=p.endpoint_id JOIN net_app a ON a.id=p.app_id LEFT JOIN trd ON trd.endpoint_id=e.id AND trd.proto=p.proto{where} GROUP BY e.id,p.proto{having} ORDER BY {valueExpr} DESC,{gid} DESC LIMIT $lim";
        using var c = Cmd(sql, ps.ToArray()); using var r = c.ExecuteReader(); var l = new List<DestRow>();
        while (r.Read()) l.Add(new DestRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), (IpScope)r.GetInt32(6), r.GetString(7), r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt32(12) != 0, r.GetInt64(13), r.GetInt64(14), r.IsDBNull(15) ? "" : Convert.ToString(r.GetValue(15)) ?? ""));
        return l;
    }
    public long SortValue(DestRow r, string sort) => sort == "observed" ? r.Observations : sort == "bytes" ? r.Sent + r.Recv : r.LastMin;

    public List<LiveRow> Live(long? appId, NetFilter f)
    {
        var ps = new List<(string, object?)> { ("$q", f.Search.Trim().ToLowerInvariant()) }; string where = " WHERE 1=1";
        if (appId is long id) { where += " AND l.app_id=$app"; ps.Add(("$app", id)); }
        where += " AND ($q='' OR instr(lower(a.name),$q)>0 OR instr(lower(a.path),$q)>0 OR instr(lower(COALESCE(e.ip,'')),$q)>0 OR CAST(l.local_port AS TEXT)=$q OR CAST(COALESCE(e.port,0) AS TEXT)=$q)";
        if (f.Proto != 0) { where += " AND l.proto=$fp"; ps.Add(("$fp", f.Proto)); }
        if (f.Family != 0) where += f.Family == 6 ? " AND instr(l.local_ip,':')>0" : " AND instr(l.local_ip,':')=0";
        if (f.Scopes.Count > 0) where += " AND e.ip_scope IN (" + string.Join(",", f.Scopes.Select(x => (int)x)) + ")";
        using var c = Cmd("SELECT a.name,a.path,l.proto,l.local_ip,l.local_port,e.ip,e.port,l.state,l.pid,l.start_ft FROM net_live l JOIN net_app a ON a.id=l.app_id LEFT JOIN net_endpoint e ON e.id=l.endpoint_id" + where + " ORDER BY a.name COLLATE NOCASE,l.proto,l.local_port LIMIT 2000", ps.ToArray());
        using var r = c.ExecuteReader(); var l = new List<LiveRow>();
        while (r.Read()) { var conn = new Conn((byte)r.GetInt32(2), r.GetString(3), (ushort)r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? (ushort)0 : (ushort)r.GetInt32(6), 0, r.GetInt32(7), r.GetInt32(8)); l.Add(new LiveRow(r.GetString(0), r.GetString(1), conn.Proto, conn.LocalIp, conn.LocalPort, conn.RemoteIp, conn.RemotePort, conn.StateName, conn.Pid, r.GetInt64(9), conn.LocalIp.Contains(':') ? 6 : 4)); }
        return l;
    }
    public List<BindingRow> Bindings(long appId, DateTime fromUtc, DateTime toUtc)
    {
        using var c = Cmd("SELECT p.proto,p.local_port,SUM(p.samples),MAX(p.minute),EXISTS(SELECT 1 FROM net_live l WHERE l.app_id=p.app_id AND l.proto=p.proto AND l.local_port=p.local_port AND l.endpoint_id=0) FROM net_presence p WHERE p.app_id=$a AND p.endpoint_id=0 AND p.local_port<>0 AND p.minute BETWEEN $f AND $t GROUP BY p.proto,p.local_port ORDER BY MAX(p.minute) DESC LIMIT 200", ("$a", appId), ("$f", Min(fromUtc)), ("$t", Min(toUtc)));
        using var r = c.ExecuteReader(); var l = new List<BindingRow>(); while (r.Read()) l.Add(new BindingRow(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4) != 0)); return l;
    }
    /// <summary>Network applications matching a process name from the performance history ("msedge" → every msedge.exe path seen).</summary>
    public List<(long Id, string Path)> AppIdsByName(string processName)
    {
        using var c = Cmd("SELECT id,path FROM net_app WHERE resolved=1 AND (name=$n COLLATE NOCASE OR name=$e COLLATE NOCASE) ORDER BY last_seen DESC", ("$n", processName), ("$e", processName + ".exe"));
        using var r = c.ExecuteReader(); var l = new List<(long, string)>(); while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1))); return l;
    }
    public List<AdapterInfo> Adapters() { using var c = Cmd("SELECT id,name,type,physical FROM net_adapter_info ORDER BY physical DESC,name"); using var r = c.ExecuteReader(); var l = new List<AdapterInfo>(); while (r.Read()) l.Add(new AdapterInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0)); return l; }

    /// <summary>Adapter counter rates (bytes/s) on the same bucket grid as the battery charts. NaN = no covered time in that bucket.</summary>
    public (double[] Rx, double[] Tx) AdapterSeries(DateTime fromUtc, DateTime toUtc, int buckets, bool physicalOnly = true)
    {
        var rx = new double[buckets]; var tx = new double[buckets]; var ms = new double[buckets]; long f = Epoch(fromUtc), span = Math.Max(1, (Epoch(toUtc) - f) / buckets);
        using var c = Cmd("SELECT a.minute,SUM(a.rx),SUM(a.tx),SUM(a.ms) FROM net_adapter a JOIN net_adapter_info i ON i.id=a.adapter WHERE a.minute BETWEEN $a AND $b" + (physicalOnly ? " AND i.physical=1" : "") + " GROUP BY a.minute", ("$a", Min(fromUtc)), ("$b", Min(toUtc)));
        using var r = c.ExecuteReader();
        while (r.Read()) { long i = (r.GetInt64(0) * 60 - f) / span; if (i < 0 || i >= buckets) continue; rx[i] += r.GetInt64(1); tx[i] += r.GetInt64(2); ms[i] += r.GetInt64(3); }
        for (int i = 0; i < buckets; i++) { if (ms[i] <= 0) { rx[i] = tx[i] = double.NaN; } else { rx[i] = rx[i] / (ms[i] / 1000); tx[i] = tx[i] / (ms[i] / 1000); } }
        return (rx, tx);
    }
    public double[] AppDestinationSeries(long appId, DateTime fromUtc, DateTime toUtc, int buckets)
    {
        var v = new double[buckets]; long f = Epoch(fromUtc), span = Math.Max(1, (Epoch(toUtc) - f) / buckets); var sets = new HashSet<long>[buckets];
        using var c = Cmd("SELECT minute,endpoint_id FROM net_presence WHERE app_id=$a AND endpoint_id<>0 AND minute BETWEEN $f AND $t", ("$a", appId), ("$f", Min(fromUtc)), ("$t", Min(toUtc))); using var r = c.ExecuteReader();
        while (r.Read()) { long i = (r.GetInt64(0) * 60 - f) / span; if (i < 0 || i >= buckets) continue; (sets[i] ??= new()).Add(r.GetInt64(1)); }
        for (int i = 0; i < buckets; i++) v[i] = sets[i]?.Count ?? 0; return v;
    }
    public NetTotals Totals(DateTime fromUtc, DateTime toUtc)
    {
        using var c = Cmd("SELECT COUNT(DISTINCT app_id),COUNT(DISTINCT CASE WHEN endpoint_id<>0 THEN endpoint_id END),COALESCE(MIN(minute),0),COALESCE(MAX(minute),0) FROM net_presence WHERE minute BETWEEN $a AND $b", ("$a", Min(fromUtc)), ("$b", Min(toUtc)));
        using var r = c.ExecuteReader(); r.Read(); int live = Scalar("SELECT COUNT(*) FROM net_live", 0); long ta = Min(fromUtc) * 60, tb = Min(toUtc) * 60 + 59;
        long sent = Scalar("SELECT COALESCE(SUM(sent),0) FROM net_traffic WHERE ts BETWEEN $a AND $b", 0L, ("$a", ta), ("$b", tb)), recv = Scalar("SELECT COALESCE(SUM(recv),0) FROM net_traffic WHERE ts BETWEEN $a AND $b", 0L, ("$a", ta), ("$b", tb));
        return new NetTotals(r.GetInt32(0), r.GetInt32(1), live, DbBytes(), r.GetInt64(2), r.GetInt64(3), sent, recv, EnhancedCoverage(fromUtc, toUtc));
    }
    /// <summary>Fraction (0..1) of the interval covered by enhanced sessions. Bytes are only meaningful where this is above zero; missing coverage is not zero traffic.</summary>
    public double EnhancedCoverage(DateTime fromUtc, DateTime toUtc)
    {
        long a = Epoch(fromUtc), b = Epoch(toUtc); if (b <= a) return 0; var spans = EnhancedSpans(a, b); long cov = 0; foreach (var (x, y) in spans) cov += y - x; return Math.Min(1.0, cov / (double)(b - a));
    }
    List<(long From, long To)> EnhancedSpans(long a, long b)
    {
        using var c = Cmd("SELECT MAX(from_t,$a),MIN(to_t,$b) FROM net_coverage WHERE kind='enhanced' AND to_t>=$a AND from_t<=$b ORDER BY from_t", ("$a", a), ("$b", b)); using var r = c.ExecuteReader(); var l = new List<(long, long)>(); while (r.Read()) l.Add((r.GetInt64(0), r.GetInt64(1))); return l;
    }
    /// <summary>Observed transport bytes per second on the chart grid (NaN where no enhanced session covered the slice). appId null = all applications.</summary>
    public (double[] Sent, double[] Recv) TrafficSeries(DateTime fromUtc, DateTime toUtc, int buckets, long? appId = null)
    {
        long f = Epoch(fromUtc), t = Epoch(toUtc), span = Math.Max(1, (t - f) / buckets); var sent = new double[buckets]; var recv = new double[buckets];
        using (var c = Cmd("SELECT ts,SUM(sent),SUM(recv) FROM net_traffic WHERE ts BETWEEN $a AND $b" + (appId is long ? " AND app_id=$app" : "") + " GROUP BY ts", appId is long id ? new (string, object?)[] { ("$a", f), ("$b", t), ("$app", id) } : new (string, object?)[] { ("$a", f), ("$b", t) })) using (var r = c.ExecuteReader())
            while (r.Read()) { long i = (r.GetInt64(0) - f) / span; if (i < 0 || i >= buckets) continue; sent[i] += r.GetInt64(1); recv[i] += r.GetInt64(2); }
        var spans = EnhancedSpans(f, t);
        for (int i = 0; i < buckets; i++) { long bs = f + i * span, be = bs + span; bool covered = spans.Any(x => x.From < be && x.To > bs); if (!covered) { sent[i] = recv[i] = double.NaN; } else { sent[i] /= span; recv[i] /= span; } }
        return (sent, recv);
    }
    /// <summary>DNS evidence for a set of IPs around an interval: candidates may precede the first use by up to five minutes.</summary>
    public List<NameRow> NameRows(IEnumerable<string> ips, DateTime fromUtc, DateTime toUtc)
    {
        var list = ips.Distinct().Take(3000).ToList(); var rows = new List<NameRow>(); if (list.Count == 0) return rows; long a = Epoch(fromUtc) - 300, b = Epoch(toUtc) + 60;
        for (int i = 0; i < list.Count; i += 100)
        {
            var chunk = list.Skip(i).Take(100).ToList(); var ps = new List<(string, object?)> { ("$a", a), ("$b", b) }; for (int k = 0; k < chunk.Count; k++) ps.Add(("$i" + k, chunk[k]));
            using var c = Cmd("SELECT name,ip,app_id,t,src FROM net_name WHERE ip IN (" + string.Join(",", chunk.Select((_, k) => "$i" + k)) + ") AND t BETWEEN $a AND $b", ps.ToArray()); using var r = c.ExecuteReader();
            while (r.Read()) rows.Add(new NameRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)));
            using var c2 = Cmd("SELECT name,ip,0,t,'ptr' FROM net_ptr WHERE name<>'' AND ip IN (" + string.Join(",", chunk.Select((_, k) => "$i" + k)) + ")", ps.Skip(2).ToArray()); using var r2 = c2.ExecuteReader();
            while (r2.Read()) rows.Add(new NameRow(r2.GetString(0), r2.GetString(1), 0, r2.GetInt64(3), "ptr"));
        }
        return rows;
    }
    public List<(DateTime Time, string Kind, string Detail)> AuditEvents(DateTime fromUtc, DateTime toUtc)
    {
        using var c = Cmd("SELECT t,kind,detail FROM net_audit WHERE t BETWEEN $a AND $b AND kind IN ('block-add','block-remove','blocks-ended','enhanced-start','enhanced-stop') ORDER BY t", ("$a", Epoch(fromUtc)), ("$b", Epoch(toUtc))); using var r = c.ExecuteReader(); var l = new List<(DateTime, string, string)>();
        while (r.Read()) l.Add((DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).UtcDateTime, r.GetString(1), r.GetString(2))); return l;
    }
    /// <summary>Per-application mini histograms. Bytes (enhanced) where available, otherwise snapshot presence as grey activity. Values are 0..1, sqrt-scaled against the busiest application.</summary>
    public Dictionary<long, (double[]? Up, double[]? Down, double[]? Act)> AppBars(DateTime fromUtc, DateTime toUtc, int n)
    {
        long f = Epoch(fromUtc), t = Epoch(toUtc), span = Math.Max(1, (t - f) / n); var up = new Dictionary<long, double[]>(); var down = new Dictionary<long, double[]>(); var act = new Dictionary<long, double[]>();
        using (var c = Cmd("SELECT app_id,ts,SUM(sent),SUM(recv) FROM net_traffic WHERE ts BETWEEN $a AND $b GROUP BY app_id,ts", ("$a", f), ("$b", t))) using (var r = c.ExecuteReader())
            while (r.Read()) { long id = r.GetInt64(0), i = (r.GetInt64(1) - f) / span; if (i < 0 || i >= n) continue; if (!up.TryGetValue(id, out var u)) { up[id] = u = new double[n]; down[id] = new double[n]; } u[i] += r.GetInt64(2); down[id][i] += r.GetInt64(3); }
        using (var c = Cmd("SELECT app_id,minute,SUM(samples) FROM net_presence WHERE minute BETWEEN $a AND $b GROUP BY app_id,minute", ("$a", f / 60), ("$b", t / 60))) using (var r = c.ExecuteReader())
            while (r.Read()) { long id = r.GetInt64(0), i = (r.GetInt64(1) * 60 - f) / span; if (i < 0 || i >= n || up.ContainsKey(id)) continue; if (!act.TryGetValue(id, out var a)) act[id] = a = new double[n]; a[i] += Math.Max(1, r.GetInt64(2)); }
        double mb = up.Values.Concat(down.Values).SelectMany(x => x).DefaultIfEmpty(0).Max(), ma = act.Values.SelectMany(x => x).DefaultIfEmpty(0).Max(); var res = new Dictionary<long, (double[]?, double[]?, double[]?)>();
        foreach (var id in up.Keys) res[id] = (up[id].Select(v => mb > 0 ? Math.Sqrt(v / mb) : 0).ToArray(), down[id].Select(v => mb > 0 ? Math.Sqrt(v / mb) : 0).ToArray(), null);
        foreach (var id in act.Keys) res[id] = (null, null, act[id].Select(v => ma > 0 ? Math.Sqrt(v / ma) : 0).ToArray());
        return res;
    }
    public List<(string Src, long Count)> NameSourceCounts() { using var c = Cmd("SELECT src,COUNT(*) FROM net_name GROUP BY src"); using var r = c.ExecuteReader(); var l = new List<(string, long)>(); while (r.Read()) l.Add((r.GetString(0), r.GetInt64(1))); return l; }
    /// <summary>Public addresses seen recently with no DNS evidence and no reverse-DNS attempt yet (for the opt-in PTR lookups).</summary>
    public List<string> UnnamedPublicIps(DateTime fromUtc, int limit)
    {
        using var c = Cmd("SELECT DISTINCT e.ip FROM net_presence p JOIN net_endpoint e ON e.id=p.endpoint_id WHERE p.minute>=$a AND e.ip_scope=$s AND NOT EXISTS(SELECT 1 FROM net_name n WHERE n.ip=e.ip) AND NOT EXISTS(SELECT 1 FROM net_ptr r WHERE r.ip=e.ip) LIMIT $n", ("$a", Min(fromUtc)), ("$s", (int)IpScope.Public), ("$n", limit));
        using var r = c.ExecuteReader(); var l = new List<string>(); while (r.Read()) l.Add(r.GetString(0)); return l;
    }
    public void SavePtr(string ip, string name) { using var c = Cmd("INSERT OR REPLACE INTO net_ptr VALUES($i,$n,$t)", ("$i", ip), ("$n", name.Length > 255 ? name[..255] : name), ("$t", Epoch(DateTime.UtcNow))); c.ExecuteNonQuery(); }
    public bool HasEnhancedEver() => Scalar("SELECT COUNT(*) FROM net_coverage WHERE kind='enhanced'", 0) > 0;
    public (DateTime Time, string Kind, string Detail)[] CoverageNotes(DateTime fromUtc, DateTime toUtc)
    {
        using var c = Cmd("SELECT to_t,kind,detail FROM net_coverage WHERE kind<>'enhanced' AND to_t>=$a AND from_t<=$b ORDER BY to_t DESC LIMIT 20", ("$a", Epoch(fromUtc)), ("$b", Epoch(toUtc))); using var r = c.ExecuteReader(); var l = new List<(DateTime, string, string)>();
        while (r.Read()) l.Add((DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).UtcDateTime, r.GetString(1), r.GetString(2))); return l.ToArray();
    }
    public void Dispose() => db.Dispose();
}
