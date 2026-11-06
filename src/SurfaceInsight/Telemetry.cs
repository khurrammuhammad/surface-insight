using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Management;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace SurfaceInsight;

public record AppUsage(int Pid, long Start, string Name, double Cpu, double Memory, double Io, double? Gpu, double? Npu = null);
public record Sample(DateTime Time, bool Ac, double? Watts, double Percent, double? Remaining, double? Full, double? Design, double Cpu, double? Gpu, double? Frequency, double? Interrupts, double? Brightness, string Mode, List<AppUsage> Apps, double Seconds = 5, double? Npu = null);
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Battery { public byte Ac, Present, Charging, Discharging; public byte Spare1, Spare2, Spare3, Tag; public uint Max, Remaining, Rate, Estimated, Alert1, Alert2; }
    [DllImport("powrprof.dll")] public static extern uint CallNtPowerInformation(int level, IntPtr input, uint size, out Battery battery, uint outputSize);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] public struct IO { public ulong ReadOps, WriteOps, OtherOps, Read, Write, Other; }
    [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(IntPtr h, out IO io);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid guid);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static long oldIdle, oldTotal;
    public static double Cpu() { GetSystemTimes(out var idle, out var kernel, out var user); long total = kernel + user, dt = total - oldTotal, di = idle - oldIdle; bool first = oldTotal == 0; oldTotal = total; oldIdle = idle; return first || dt <= 0 ? 0 : Math.Clamp(100.0 * (dt - di) / dt, 0, 100); }
    public static string Mode() { try { if (PowerGetEffectiveOverlayScheme(out var p) != 0) return "Unavailable"; var g = p; return g.ToString() switch { "00000000-0000-0000-0000-000000000000" => "Balanced", "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best efficiency", "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance", _ => g.ToString() }; } catch { return "Unavailable"; } }
}
public sealed class Collector
{
    Dictionary<(int, long), (double Cpu, ulong Io)> previous = new(); DateTime last = DateTime.UtcNow; double? full, design, brightness; DateTime slow = DateTime.MinValue, brightAt = DateTime.MinValue;
    PerformanceCounter? freq, interrupt; GpuQuery gpu = new();
    public Collector() { try { freq = new("Processor Information", "Processor Frequency", "_Total"); interrupt = new("Processor", "% Interrupt Time", "_Total"); } catch { } }
    static double? Wmi(string cls, string prop) { try { using var q = new ManagementObjectSearcher("root\\wmi", "SELECT " + prop + " FROM " + cls); using var results = q.Get(); foreach (ManagementObject o in results) using (o) return Convert.ToDouble(o[prop]); } catch { } return null; }
    public volatile string Stage = "idle";
    public Sample Read()
    {
        Stage = "start";
        var now = DateTime.UtcNow; double elapsed = (now - last).TotalSeconds; bool gap = elapsed > 60; last = now; if ((now - slow).TotalMinutes > 2) { Stage = "wmi"; full = Wmi("BatteryFullChargedCapacity", "FullChargedCapacity"); design = Wmi("BatteryStaticData", "DesignedCapacity"); slow = now; }
        if ((now - brightAt).TotalSeconds >= 10) { Stage = "brightness"; brightness = Wmi("WmiMonitorBrightness", "CurrentBrightness"); brightAt = now; }
        Stage = "gpu-pdh"; var acc = gpu.Read(); Stage = "processes"; var gpuByPid = acc?.Gpu ?? new Dictionary<int, double>(); bool hasGpu = acc != null; var npuByPid = acc?.Npu;
        var apps = new List<AppUsage>(); var next = new Dictionary<(int, long), (double, ulong)>(); foreach (var p in Process.GetProcesses()) { using (p) try { if (p.Id == 0) continue; long start = p.StartTime.ToUniversalTime().Ticks; double cpu = p.TotalProcessorTime.TotalSeconds; Native.GetProcessIoCounters(p.Handle, out var io); ulong bytes = io.Read + io.Write + io.Other; var key = (p.Id, start); if (previous.TryGetValue(key, out var old) && elapsed > 0 && !gap) apps.Add(new(p.Id, start, p.ProcessName, Math.Clamp((cpu - old.Cpu) / elapsed / Environment.ProcessorCount * 100, 0, 100), p.WorkingSet64 / 1048576.0, bytes >= old.Io ? (bytes - old.Io) / elapsed : 0, hasGpu ? gpuByPid.GetValueOrDefault(p.Id) : null, npuByPid != null ? npuByPid.GetValueOrDefault(p.Id) : null)); next[key] = (cpu, bytes); } catch { } }
        previous = next; Stage = "battery";
        Native.CallNtPowerInformation(5, IntPtr.Zero, 0, out var b, (uint)Marshal.SizeOf<Native.Battery>()); double? watts = b.Discharging != 0 && b.Rate != uint.MaxValue && Math.Abs((long)unchecked((int)b.Rate)) < 1000000 ? Math.Abs((long)unchecked((int)b.Rate)) / 1000.0 : null; double? cap = b.Remaining != uint.MaxValue ? b.Remaining : null; double? max = full ?? (b.Max != uint.MaxValue ? b.Max : null); double? f = null, i = null; Stage = "counters"; try { f = freq?.NextValue(); i = interrupt?.NextValue(); } catch { }
        Stage = "done";
        return new(now, b.Ac != 0, watts, cap.HasValue && max > 0 ? 100 * cap.Value / max.Value : double.NaN, cap, max, design, Native.Cpu(), hasGpu ? gpuByPid.Values.DefaultIfEmpty().Max() : null, f, i, brightness, Native.Mode(), apps.OrderByDescending(a => a.Cpu).ToList(), gap ? 0 : Math.Clamp(elapsed, 0, 60), acc?.NpuTotal);
    }
}
public sealed class History : IDisposable
{
    public string Folder = MonitorHost.Folder; SqliteConnection db;
    public History(string? folder = null) { if (folder != null) Folder = folder; Directory.CreateDirectory(Folder); db = new("Data Source=" + Path.Combine(Folder, "history.db") + ";Default Timeout=5"); db.Open(); Run("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS samples(t TEXT PRIMARY KEY,data TEXT); CREATE TABLE IF NOT EXISTS markers(t TEXT,label TEXT);"); }
    void Run(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    public void Add(Sample s) { using var c = db.CreateCommand(); c.CommandText = "INSERT OR REPLACE INTO samples VALUES($t,$d)"; c.Parameters.AddWithValue("$t", s.Time.ToString("O")); c.Parameters.AddWithValue("$d", JsonSerializer.Serialize(s, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })); c.ExecuteNonQuery(); }
    public List<Sample> Read(DateTime since, DateTime? until = null) { using var c = db.CreateCommand(); c.CommandText = "SELECT data FROM samples WHERE t >= $t AND t <= $u ORDER BY t"; c.Parameters.AddWithValue("$t", since.ToUniversalTime().ToString("O")); c.Parameters.AddWithValue("$u", (until ?? DateTime.UtcNow).ToUniversalTime().ToString("O")); using var r = c.ExecuteReader(); var list = new List<Sample>(); while (r.Read()) try { list.Add(JsonSerializer.Deserialize<Sample>(r.GetString(0), new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })!); } catch { } return list; }
    public DateTime? LastTime() { using var c = db.CreateCommand(); c.CommandText = "SELECT max(t) FROM samples"; var v = c.ExecuteScalar() as string; return v != null && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null; }
    public void ReadEach(DateTime since, DateTime? until, Action<Sample> each) { using var c = db.CreateCommand(); c.CommandText = "SELECT data FROM samples WHERE t >= $t AND t <= $u ORDER BY t"; c.Parameters.AddWithValue("$t", since.ToUniversalTime().ToString("O")); c.Parameters.AddWithValue("$u", (until ?? DateTime.UtcNow).ToUniversalTime().ToString("O")); using var r = c.ExecuteReader(); var opt = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }; while (r.Read()) { Sample? s = null; try { s = JsonSerializer.Deserialize<Sample>(r.GetString(0), opt); } catch { } if (s != null) each(s); } }
    public List<(DateTime Time, string Label)> MarkerList(DateTime since, DateTime until) { using var c = db.CreateCommand(); c.CommandText = "SELECT t,label FROM markers WHERE t >= $a AND t <= $b ORDER BY t"; c.Parameters.AddWithValue("$a", since.ToUniversalTime().ToString("O")); c.Parameters.AddWithValue("$b", until.ToUniversalTime().ToString("O")); using var r = c.ExecuteReader(); var l = new List<(DateTime, string)>(); while (r.Read()) l.Add((DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetString(1))); return l; }
    public void Mark() { using var c = db.CreateCommand(); c.CommandText = "INSERT INTO markers VALUES($t,'Fans are loud')"; c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O")); c.ExecuteNonQuery(); }
    public string Markers() { using var c = db.CreateCommand(); c.CommandText = "SELECT t,label FROM markers ORDER BY t DESC LIMIT 20"; using var r = c.ExecuteReader(); var text = ""; while (r.Read()) text += DateTime.Parse(r.GetString(0)).ToLocalTime().ToString("g") + " · " + r.GetString(1) + "\n"; return text; }
    public void Maintain() { Aggregate(); Run("DELETE FROM samples WHERE t < '" + DateTime.UtcNow.AddDays(-30).ToString("O") + "'; DELETE FROM markers WHERE t < '" + DateTime.UtcNow.AddDays(-30).ToString("O") + "';;"); if (new FileInfo(Path.Combine(Folder, "history.db")).Length > 240 * 1024 * 1024) { Run("DELETE FROM samples WHERE rowid IN (SELECT rowid FROM samples ORDER BY t LIMIT (SELECT count(*)/5 FROM samples)); VACUUM;"); } Run("PRAGMA wal_checkpoint(TRUNCATE);"); }
    void Aggregate() { var items = Read(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-2)).GroupBy(x => new DateTime(x.Time.Year, x.Time.Month, x.Time.Day, x.Time.Hour, x.Time.Minute, 0, DateTimeKind.Utc)); foreach (var group in items) { var a = group.ToList(); if (a.Count < 2) continue; double? Avg(Func<Sample, double?> f) { var v = a.Select(f).Where(x => x.HasValue && double.IsFinite(x.Value)).ToArray(); return v.Length == 0 ? null : v.Average(); } var first = a[0]; if (a.Any(x => x.Ac != first.Ac || x.Mode != first.Mode)) continue; var apps = a.SelectMany(x => x.Apps).GroupBy(x => (x.Pid, x.Start, x.Name)).Select(g => new AppUsage(g.Key.Pid, g.Key.Start, g.Key.Name, g.Sum(x => x.Cpu) / a.Count, g.Average(x => x.Memory), g.Sum(x => x.Io) / a.Count, g.Max(x => x.Gpu), g.Max(x => x.Npu))).ToList(); var summary = first with { Watts = Avg(x => x.Watts), Cpu = a.Average(x => x.Cpu), Gpu = Avg(x => x.Gpu), Npu = Avg(x => x.Npu), Apps = apps, Seconds = a.Sum(x => x.Seconds) }; using var tx = db.BeginTransaction(); using (var c = db.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM samples WHERE t >= $a AND t < $b"; c.Parameters.AddWithValue("$a", group.Key.ToString("O")); c.Parameters.AddWithValue("$b", group.Key.AddMinutes(1).ToString("O")); c.ExecuteNonQuery(); } using (var c = db.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT INTO samples VALUES($t,$d)"; c.Parameters.AddWithValue("$t", summary.Time.ToString("O")); c.Parameters.AddWithValue("$d", JsonSerializer.Serialize(summary, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })); c.ExecuteNonQuery(); } tx.Commit(); } }
    public void Clear() { Run("DELETE FROM samples; DELETE FROM markers; VACUUM;"); }
    public void Dispose() => db.Dispose();
}







