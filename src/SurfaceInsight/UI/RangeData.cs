namespace SurfaceInsight;

public sealed class InstAgg { public int Pid; public long Start; public double CpuPctSec, Sec, Peak; public int? Mach; }

public sealed class AppAgg
{
    public string Name = ""; public double CpuPctSec, Peak, MemSum, MemSec, IoSum, Sec, NpuSum, NpuSec; public double? Gpu, Npu;
    public Dictionary<(int, long), InstAgg> Inst = new();
    public double AvgCpu, CpuSeconds, Share, AvgMem, AvgIo; public double? AvgNpu; public string Activity = "Low";
    /// <summary>Architecture label for the Apps table ("x64", "Arm64", "x64 · emulated", "Mixed" or "—" when unknown) and whether any instance runs under emulation.</summary>
    public string Arch = "—"; public bool Emulated;
}

public sealed class Bucket
{
    public double Sec, WSum, WSec, CpuSum, GpuSum, GpuSec, NpuSum, NpuSec, IoSum, BrSum, BrSec, AcSec; public bool Ac;
    public Dictionary<string, double> ModeSec = new();
    public string? Mode => ModeSec.Count == 0 ? null : ModeSec.MaxBy(k => k.Value).Key;
    public Dictionary<string, double> AppCpu = new(StringComparer.OrdinalIgnoreCase), AppMem = new(StringComparer.OrdinalIgnoreCase), AppIo = new(StringComparer.OrdinalIgnoreCase), AppNpu = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Streams stored samples into fixed time buckets and per-app aggregates, so a 30-day range never holds raw samples in memory.
/// Everything is weighted by observed duration, matching Insights.Rank.
/// </summary>
public sealed class RangeData
{
    public DateTime From, To; public TimeSpan Span; public Bucket[] Buckets;
    public Dictionary<string, AppAgg> Apps = new(StringComparer.OrdinalIgnoreCase);
    public double TotalSec, TotalCpuPctSec; public List<(DateTime Time, string Label)> Markers = new();
    public DateTime? LastSample; public bool HasNpu;
    const double Mb = 1048576;

    public RangeData(DateTime from, DateTime to, int buckets = 180) { From = from; To = to; Span = TimeSpan.FromTicks(Math.Max(1, (to - from).Ticks / buckets)); Buckets = Enumerable.Range(0, buckets).Select(_ => new Bucket()).ToArray(); }

    public static RangeData Load(DateTime from, DateTime to, int buckets = 180)
    {
        var d = new RangeData(from, to, buckets);
        using var h = new History();
        h.ReadEach(from, to, d.Add);
        d.Markers = h.MarkerList(from, to);
        d.Finish(); return d;
    }

    public int IndexOf(DateTime t) => (int)Math.Floor((t - From).Ticks / (double)Span.Ticks);
    public DateTime BucketTime(int i) => From + TimeSpan.FromTicks(Span.Ticks * i + Span.Ticks / 2);

    /// <summary>Adds one sample. When it falls after the window, the window slides forward (aggregates only grow until the next full load).</summary>
    public void Add(Sample s)
    {
        double w = s.Seconds; LastSample = s.Time; if (w <= 0) return;
        int i = IndexOf(s.Time); if (i < 0) return;
        if (i >= Buckets.Length) { int shift = i - Buckets.Length + 1; Slide(shift); i = Buckets.Length - 1; }
        var b = Buckets[i]; b.Sec += w; b.CpuSum += s.Cpu * w; if (s.Ac) b.Ac = true;
        if (!s.Ac && s.Watts is double watts) { b.WSum += watts * w; b.WSec += w; }
        if (s.Gpu is double g && double.IsFinite(g)) { b.GpuSum += g * w; b.GpuSec += w; }
        if (s.Npu is double np && double.IsFinite(np)) { b.NpuSum += np * w; b.NpuSec += w; HasNpu = true; }
        if (s.Brightness is double br && double.IsFinite(br)) { b.BrSum += br * w; b.BrSec += w; }
        if (s.Ac) b.AcSec += w; if (!string.IsNullOrEmpty(s.Mode)) b.ModeSec[s.Mode] = b.ModeSec.GetValueOrDefault(s.Mode) + w;
        TotalSec += w;
        foreach (var g2 in s.Apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            double cpu = g2.Sum(x => x.Cpu), mem = g2.Sum(x => x.Memory), io = g2.Sum(x => x.Io);
            if (!Apps.TryGetValue(g2.Key, out var a)) Apps[g2.Key] = a = new AppAgg { Name = g2.Key };
            a.CpuPctSec += cpu * w; a.Peak = Math.Max(a.Peak, cpu); a.MemSum += mem * w; a.MemSec += w; a.IoSum += io * w; a.Sec += w;
            var gg = g2.Where(x => x.Gpu.HasValue).Select(x => x.Gpu!.Value).DefaultIfEmpty(double.NaN).Max(); if (double.IsFinite(gg)) a.Gpu = Math.Max(a.Gpu ?? 0, gg);
            if (s.Npu.HasValue) { double nn = g2.Select(x => x.Npu ?? 0).DefaultIfEmpty(0).Max(); a.Npu = Math.Max(a.Npu ?? 0, nn); a.NpuSum += nn * w; a.NpuSec += w; b.AppNpu[g2.Key] = b.AppNpu.GetValueOrDefault(g2.Key) + nn * w; }
            b.AppCpu[g2.Key] = b.AppCpu.GetValueOrDefault(g2.Key) + cpu * w; b.AppMem[g2.Key] = b.AppMem.GetValueOrDefault(g2.Key) + mem * w; b.AppIo[g2.Key] = b.AppIo.GetValueOrDefault(g2.Key) + io * w; b.IoSum += io * w; TotalCpuPctSec += cpu * w;
            foreach (var p in g2) { var k = (p.Pid, p.Start); if (!a.Inst.TryGetValue(k, out var inst)) a.Inst[k] = inst = new InstAgg { Pid = p.Pid, Start = p.Start }; inst.CpuPctSec += p.Cpu * w; inst.Sec += w; inst.Peak = Math.Max(inst.Peak, p.Cpu); if (p.Mach.HasValue) inst.Mach = p.Mach; }
        }
    }

    void Slide(int n)
    {
        n = Math.Min(n, Buckets.Length);
        var nb = new Bucket[Buckets.Length]; Array.Copy(Buckets, n, nb, 0, Buckets.Length - n);
        for (int i = Buckets.Length - n; i < nb.Length; i++) nb[i] = new Bucket();
        Buckets = nb; From += TimeSpan.FromTicks(Span.Ticks * n); To = From + TimeSpan.FromTicks(Span.Ticks * Buckets.Length);
    }

    public void Finish()
    {
        int cores = Environment.ProcessorCount;
        foreach (var a in Apps.Values)
        {
            a.AvgCpu = TotalSec > 0 ? a.CpuPctSec / TotalSec : 0; a.CpuSeconds = a.CpuPctSec / 100 * cores;
            a.Share = TotalCpuPctSec > 0 ? a.CpuPctSec / TotalCpuPctSec * 100 : 0;
            a.AvgMem = a.MemSec > 0 ? a.MemSum / a.MemSec : 0; a.AvgIo = TotalSec > 0 ? a.IoSum / TotalSec : 0;
            a.AvgNpu = a.NpuSec > 0 ? a.NpuSum / a.NpuSec : null; a.Activity = Insights.Activity(a.AvgCpu, a.Peak);
            var machs = a.Inst.Values.Where(i => i.Mach.HasValue).Select(i => i.Mach!.Value).Distinct().ToList();
            a.Arch = machs.Count == 0 ? "—" : machs.Count == 1 ? Platform.ArchLabel(machs[0]) : "Mixed"; a.Emulated = machs.Any(m => Platform.IsEmulatedMachine(m, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture));
        }
    }

    public List<AppAgg> Ranked() => Apps.Values.OrderByDescending(a => a.AvgCpu).ToList();
    public AppAgg? App(string name) => Apps.TryGetValue(name, out var a) ? a : null;

    // ---- series (NaN = no data in that bucket) ----
    double[] Map(Func<Bucket, double> f) => Buckets.Select(f).ToArray();
    public double[] Times() => Enumerable.Range(0, Buckets.Length).Select(i => BucketTime(i).ToLocalTime().ToOADate()).ToArray();
    public double[] Watts() => Map(b => b.WSec > 0 ? b.WSum / b.WSec : double.NaN);
    public double[] Cpu() => Map(b => b.Sec > 0 ? b.CpuSum / b.Sec : double.NaN);
    public double[] Gpu() => Map(b => b.GpuSec > 0 ? b.GpuSum / b.GpuSec : double.NaN);
    public double[] Npu() => Map(b => b.NpuSec > 0 ? b.NpuSum / b.NpuSec : double.NaN);
    public double[] AppNpu(string name) => Map(b => b.NpuSec > 0 ? b.AppNpu.GetValueOrDefault(name) / b.NpuSec : double.NaN);
    public double[] Io() => Map(b => b.Sec > 0 ? b.IoSum / b.Sec / Mb : double.NaN);
    public double[] Brightness() => Map(b => b.BrSec > 0 ? b.BrSum / b.BrSec : double.NaN);
    public double[] AppCpu(string name) => Map(b => b.Sec > 0 ? b.AppCpu.GetValueOrDefault(name) / b.Sec : double.NaN);
    public double[] AppMemMb(string name) => Map(b => b.Sec > 0 && b.AppMem.ContainsKey(name) ? b.AppMem[name] / b.Sec : double.NaN);
    public double[] AppIoMb(string name) => Map(b => b.Sec > 0 ? b.AppIo.GetValueOrDefault(name) / b.Sec / Mb : double.NaN);
    public double[] AppRaw(string name) => Map(b => b.Sec > 0 ? b.AppCpu.GetValueOrDefault(name) / b.Sec : 0);

    /// <summary>Pearson correlation of app CPU with device watts across buckets that have battery data; null when there is too little variation.</summary>
    public (double R, int N)? Correlation(string name)
    {
        var w = Watts(); var c = AppCpu(name); var xs = new List<double>(); var ys = new List<double>();
        for (int i = 0; i < w.Length; i++) if (double.IsFinite(w[i]) && double.IsFinite(c[i])) { xs.Add(c[i]); ys.Add(w[i]); }
        if (xs.Count < 8) return null;
        double mx = xs.Average(), my = ys.Average(), xx = xs.Sum(x => (x - mx) * (x - mx)), yy = ys.Sum(y => (y - my) * (y - my));
        if (xx < 1e-6 || yy < 1e-6) return null;
        return (xs.Zip(ys).Sum(p => (p.First - mx) * (p.Second - my)) / Math.Sqrt(xx * yy), xs.Count);
    }

    /// <summary>Top bucket indexes for an app, at least <paramref name="gap"/> buckets apart.</summary>
    public List<int> PeakMoments(string name, int count = 3, int gap = 4)
    {
        var s = AppCpu(name); var order = Enumerable.Range(0, s.Length).Where(i => double.IsFinite(s[i])).OrderByDescending(i => s[i]).ToList(); var picked = new List<int>();
        foreach (var i in order) { if (picked.Count >= count) break; if (picked.All(p => Math.Abs(p - i) > gap)) picked.Add(i); }
        return picked;
    }
}
