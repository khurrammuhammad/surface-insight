namespace SurfaceInsight;

/// <summary>One possible reason for a change in device draw. <see cref="EstWatts"/> is a statistical estimate, null when it cannot be estimated.</summary>
public sealed record DrawFactor(string Name, string Before, string Now, double Delta, double? EstWatts, string Note = "");
/// <summary>Why draw differs between a quieter part of the period and now.</summary>
public sealed record DrawExplanation(double BaselineW, double NowW, List<DrawFactor> Factors, double? R2, int Samples, bool ModelOk, string Note);
/// <summary>A notable change at a point in time (screen brightness step, power mode, charger).</summary>
public sealed record DrawEvent(DateTime Utc, string Label);

/// <summary>
/// Relates changes in whole-device draw to things the app measures: screen brightness, CPU, GPU, power mode and charging.
/// A least-squares fit of draw against brightness, CPU and GPU over the selected period estimates each factor's share.
/// These are associations from this device's own history, not component power measurements.
/// </summary>
public static class DrawExplainer
{
    /// <summary>Solves a small linear system with partial pivoting; returns null when singular.</summary>
    public static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var v = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-9) return null;
            if (p != c) { for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]); (v[c], v[p]) = (v[p], v[c]); }
            for (int r = c + 1; r < n; r++) { double f = m[r, c] / m[c, c]; for (int k = c; k < n; k++) m[r, k] -= f * m[c, k]; v[r] -= f * v[c]; }
        }
        var x = new double[n]; for (int r = n - 1; r >= 0; r--) { double s = v[r]; for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k]; x[r] = s / m[r, r]; }
        return x;
    }

    /// <summary>Ordinary least squares y = b0 + Σ bi·xi with a tiny ridge for stability. Returns coefficients and R², or null.</summary>
    public static (double[] Coef, double R2)? Fit(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        int n = y.Count; if (n < 8) return null; int k = x[0].Length + 1; var xtx = new double[k, k]; var xty = new double[k];
        for (int i = 0; i < n; i++) { var row = new double[k]; row[0] = 1; for (int j = 1; j < k; j++) row[j] = x[i][j - 1]; for (int a = 0; a < k; a++) { xty[a] += row[a] * y[i]; for (int b = 0; b < k; b++) xtx[a, b] += row[a] * row[b]; } }
        for (int a = 1; a < k; a++) xtx[a, a] += 1e-6 * n;
        var coef = Solve(xtx, xty); if (coef == null) return null;
        double mean = y.Average(), ssTot = 0, ssRes = 0; for (int i = 0; i < n; i++) { double pred = coef[0]; for (int j = 1; j < k; j++) pred += coef[j] * x[i][j - 1]; ssRes += (y[i] - pred) * (y[i] - pred); ssTot += (y[i] - mean) * (y[i] - mean); }
        return (coef, ssTot > 1e-9 ? 1 - ssRes / ssTot : 0);
    }

    /// <summary>Compares the last few intervals with the quieter third of the rest of the period.</summary>
    public static DrawExplanation? Explain(RangeData d, int nowBuckets = 3)
    {
        var w = d.Watts(); var br = d.Brightness(); var cpu = d.Cpu(); var gpu = d.Gpu();
        var idx = Enumerable.Range(0, w.Length).Where(i => double.IsFinite(w[i]) && double.IsFinite(cpu[i])).ToList(); if (idx.Count < 6) return null;
        var now = idx.TakeLast(Math.Min(nowBuckets, idx.Count / 3)).ToList(); var rest = idx.Except(now).ToList();
        var calm = rest.OrderBy(i => w[i]).Take(Math.Max(2, rest.Count / 3)).ToList();
        double Avg(double[] s, IEnumerable<int> ix) { var v = ix.Select(i => s[i]).Where(double.IsFinite).ToList(); return v.Count == 0 ? double.NaN : v.Average(); }
        double gpuMean = gpu.Where(double.IsFinite).DefaultIfEmpty(0).Average(), brMean = br.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        bool hasBr = double.IsFinite(brMean) && idx.Count(i => double.IsFinite(br[i])) >= idx.Count * 0.6;
        double G(int i) => double.IsFinite(gpu[i]) ? gpu[i] : gpuMean; double B(int i) => double.IsFinite(br[i]) ? br[i] : brMean;
        // fit on all intervals of the period
        var xs = idx.Select(i => hasBr ? new[] { B(i), cpu[i], G(i) } : new[] { cpu[i], G(i) }).ToList(); var ys = idx.Select(i => w[i]).ToList();
        var fit = Fit(xs, ys); double brVar = hasBr ? idx.Select(B).Select(v => (v - brMean) * (v - brMean)).Average() : 0;
        bool ok = fit is { R2: >= 0.25 } && idx.Count >= 12;
        double Coef(int feature) => fit == null ? 0 : fit.Value.Coef[feature + 1];
        var factors = new List<DrawFactor>(); double baseW = Avg(w, calm), nowW = Avg(w, now);
        void Add(string name, double before, double after, int feature, string unit, double minVar = 0)
        {
            if (!double.IsFinite(before) || !double.IsFinite(after)) return; double delta = after - before;
            double? est = ok && Coef(feature) > 0 && minVar >= 0 ? Coef(feature) * delta : null; if (est is double e && Math.Abs(e) < 0.05) est = 0;
            string note = ok && Coef(feature) <= 0 ? "no clear effect on draw in this period" : "";
            factors.Add(new DrawFactor(name, before.ToString("F0") + unit, after.ToString("F0") + unit, delta, est, note));
        }
        int f = 0;
        if (hasBr) { Add("Screen brightness", Avg(br, calm), Avg(br, now), f, "%"); if (brVar < 4 && factors.Count > 0) factors[^1] = factors[^1] with { EstWatts = null, Note = "brightness barely changed in this period, so its effect cannot be estimated" }; f++; }
        else factors.Add(new DrawFactor("Screen brightness", "—", "—", 0, null, "not reported by this display"));
        Add("CPU", Avg(cpu, calm), Avg(cpu, now), f++, "%"); Add("GPU (busiest engine)", Avg(gpu, calm), Avg(gpu, now), f++, "%");
        string ModeOf(IEnumerable<int> ix) { var m = ix.Select(i => d.Buckets[i].Mode).Where(x => x != null).GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key; return m ?? "—"; }
        string mb = ModeOf(calm), mn = ModeOf(now); if (mb != mn) factors.Add(new DrawFactor("Power mode", mb, mn, 1, null, "a different power mode changes how hard the processor runs"));
        if (d.HasNpu) { var npu = d.Npu(); double nb = Avg(npu, calm), nn = Avg(npu, now); if (double.IsFinite(nb) && double.IsFinite(nn) && Math.Abs(nn - nb) >= 5) factors.Add(new DrawFactor("NPU (busiest engine)", nb.ToString("F0") + "%", nn.ToString("F0") + "%", nn - nb, null, "AI workload on the NPU; listed, not estimated")); }
        var io = d.Io(); double iob = Avg(io, calm), ion = Avg(io, now); if (double.IsFinite(iob) && double.IsFinite(ion) && Math.Abs(ion - iob) > 1) factors.Add(new DrawFactor("Process I/O", iob.ToString("F1") + " MB/s", ion.ToString("F1") + " MB/s", ion - iob, null, "includes disk, network and other I/O; not estimated"));
        string note = fit == null ? "Not enough on-battery history in this period to estimate effects." : !ok ? $"Draw is only weakly explained by these factors in this period (R² {fit.Value.R2:F2}), so no watt estimates are shown." : $"Estimates come from a fit of draw against {(hasBr ? "brightness, " : "")}CPU and GPU over {idx.Count} intervals (R² {fit.Value.R2:F2}). They are associations from this device's own history, not measurements of individual parts; display, radios, other hardware and background work also draw power.";
        return new DrawExplanation(baseW, nowW, factors, fit?.R2, idx.Count, ok, note);
    }

    /// <summary>Brightness steps (≥ 10 points between consecutive intervals), power-mode switches and charger changes, for chart notes.</summary>
    public static List<DrawEvent> Events(RangeData d)
    {
        var l = new List<DrawEvent>(); var br = d.Brightness(); double last = double.NaN; string? mode = null; bool? ac = null;
        for (int i = 0; i < d.Buckets.Length; i++)
        {
            var b = d.Buckets[i]; if (b.Sec <= 0) continue; var t = d.BucketTime(i);
            if (double.IsFinite(br[i])) { if (double.IsFinite(last) && Math.Abs(br[i] - last) >= 10) l.Add(new DrawEvent(t, "Brightness " + last.ToString("F0") + "→" + br[i].ToString("F0") + "%")); last = br[i]; }
            var m = b.Mode; if (m != null) { if (mode != null && m != mode) l.Add(new DrawEvent(t, "Mode: " + m)); mode = m; }
            bool isAc = b.AcSec > b.Sec / 2; if (ac != null && isAc != ac) l.Add(new DrawEvent(t, isAc ? "Plugged in" : "On battery")); ac = isAc;
        }
        return l;
    }
}
