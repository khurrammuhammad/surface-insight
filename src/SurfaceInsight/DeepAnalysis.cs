using Microsoft.Windows.EventTracing;
using System.Xml.Linq;
using System.Globalization;
namespace SurfaceInsight;

public static class DeepAnalysis
{
    public static string Trace(string path)
    {
        var lines = new List<string>();
        try
        {
            using var trace = TraceProcessor.Create(path, new TraceProcessorSettings { AllowLostEvents = true, AllowTimeInversion = true, SuppressFirstTimeSetupMessage = true });
            var cpu = trace.UseCpuSamplingData();
            var energy = trace.UseEnergyEstimationData();
            trace.Process();
            var samples = cpu.Result.Samples;
            lines.Add("CPU trace · estimated execution time from sampled instruction pointers:");
            foreach (var g in samples.Where(x => x.Process?.Id != 0).GroupBy(x => x.Process?.ImageName ?? "Unknown").OrderByDescending(g => g.Sum(x => x.Weight.TotalSeconds)).Take(10))
                lines.Add($"• {g.Key}: {g.Sum(x => x.Weight.TotalSeconds):F2} CPU seconds ({g.Count()} samples).");
            lines.Add($"Sampling gaps: {cpu.Result.Gaps.Count}. Sampling is statistical; lost events can reduce completeness.");
            var drivers = samples.Where(x => x.IsExecutingDeferredProcedureCall == true || x.IsExecutingInterruptServicingRoutine == true).GroupBy(x => x.Image?.FileName ?? "Unresolved module").OrderByDescending(g => g.Sum(x => x.Weight.TotalSeconds)).Take(8).ToList();
            lines.Add(drivers.Count == 0 ? "Driver DPC/ISR attribution: unavailable in this trace." : "Driver interrupt/deferred work (sampled):");
            foreach (var g in drivers) lines.Add($"• {g.Key}: {g.Sum(x => x.Weight.TotalSeconds):F3} sampled seconds. Check driver updates if sustained across captures; module activity alone does not prove a fault.");
            var intervals = energy.Result.Intervals;
            lines.Add(intervals.Count == 0 ? "Per-app energy estimates: unavailable; this trace contains no Energy Estimation Engine intervals." : "Windows Energy Estimation Engine · modeled energy, not direct per-app measurements:");
            foreach (var g in intervals.GroupBy(x => x.ImageName ?? x.OtherConsumerName ?? "Unattributed").OrderByDescending(g => g.Sum(x => x.TotalEnergy.TotalJoules)).Take(10))
                lines.Add($"• {System.IO.Path.GetFileName(g.Key)}: {g.Sum(x => x.TotalEnergy.TotalJoules):F2} estimated joules.");
        }
        catch (Exception ex) { lines.Add("ETL analysis unavailable: " + ex.Message + ". Raw trace retained for Windows Performance Analyzer."); }
        return string.Join("\n", lines);
    }
    public static string Battery(string path)
    {
        try
        {
            var doc = XDocument.Load(path); var lines = new List<string>();
            foreach (var b in doc.Descendants().Where(x => x.Name.LocalName == "Battery"))
            {
                double? Get(string n) => double.TryParse(b.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
                var full = Get("FullChargeCapacity"); var design = Get("DesignCapacity");
                lines.Add($"Battery report: full {full / 1000:F2} Wh / design {design / 1000:F2} Wh; cycle count {Get("CycleCount")}. Capacity is a firmware estimate.");
            }
            foreach (var u in doc.Descendants().Where(x => x.Name.LocalName == "UsageEntry" && (string?)x.Attribute("Ac") == "0").TakeLast(12))
            {
                if (!double.TryParse((string?)u.Attribute("Duration"), out var ticks) || ticks <= 0 || !double.TryParse((string?)u.Attribute("Discharge"), out var discharge)) continue;
                if (discharge < 0 || ticks > TimeSpan.TicksPerDay * 30L || (string?)u.Attribute("EntryType") == "ReportGenerated") continue; double hours = ticks / TimeSpan.TicksPerHour;
                lines.Add($"• {u.Attribute("LocalTimestamp")?.Value}: {u.Attribute("EntryType")?.Value}, {hours * 60:F1} min, {discharge / 1000:F3} Wh decrease, {discharge / 1000 / hours:F2} W average.");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex) { return "Battery XML unavailable: " + ex.Message; }
    }
    public static string Sleep(string path)
    {
        try
        {
            var doc = XDocument.Load(path); var lines = new List<string> { "Windows SleepStudy · observed sleep sessions:" };
            static double Num(XElement e, string a) => double.TryParse((string?)e.Attribute(a), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
            var sessions = doc.Descendants().Where(x => x.Name.LocalName == "ScenarioInstance" && (string?)x.Attribute("Type") == "Sleep").TakeLast(12).ToList();
            foreach (var session in sessions)
            {
                double ticks = Num(session, "Duration"), hours = ticks / TimeSpan.TicksPerHour, loss = Num(session, "Discharge") / 1000;
                bool ac = (string?)session.Attribute("Ac") != "0";
                if (hours <= 0 || !double.IsFinite(hours)) continue;
                string drain = !ac && double.IsFinite(loss) && loss >= 0 ? $"{loss:F2} Wh decrease, {loss / hours:F2} W average" : "drain comparison unavailable (AC or invalid capacity)";
                double low = Num(session, "HwLowPowerStateTime") / ticks * 100;
                lines.Add($"• {session.Attribute("LocalTimestamp")?.Value}: {hours:F2} h sleep, {drain}. Hardware low-power residency {(double.IsFinite(low) ? low.ToString("F1") + "%" : "unavailable")}.");
                if (!ac && loss / hours > 1 && hours > .25) lines.Add("  Elevated sleep draw: inspect the blockers below and compare another sleep session after background work finishes.");
                foreach (var b in session.Descendants().Where(x => x.Name.LocalName == "TopBlocker").OrderByDescending(x => Num(x, "ActiveTime")).Take(3))
                    lines.Add($"  Reported blocker: {b.Attribute("Name")?.Value} ({b.Attribute("ScenarioActiveTimePercent")?.Value}% of session active). Presence alone does not establish a fault.");
            }
            if (sessions.Count == 0) lines.Add("No recognized sleep sessions in this report.");
            var active = doc.Descendants().Where(x => x.Name.LocalName == "RecentUsageInstance" && (string?)x.Attribute("Ac") == "0" && (string?)x.Attribute("Type") == "Active").LastOrDefault();
            if (active != null)
            {
                lines.Add("Most recent on-battery active period · Windows modeled power estimates:");
                lines.Add("Interval start: " + active.Attribute("LocalTimestamp")?.Value + ". Estimates are model-based and do not sum reliably to whole-device draw.");
                foreach (var app in active.Descendants().Where(x => x.Name.LocalName == "PowerEstimationRecord").OrderByDescending(x => Num(x, "TotalPowerConsumption")).Take(8))
                    lines.Add($"• {System.IO.Path.GetFileName((string?)app.Attribute("AppId") ?? "Unknown")}: {Num(app, "TotalPowerConsumption"):F0} mW estimated (CPU {Num(app, "CpuPowerConsumption"):F0}, display {Num(app, "DisplayPowerConsumption"):F0}, network {Num(app, "NetworkPowerConsumption"):F0} mW).");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex) { return "SleepStudy XML unavailable: " + ex.Message; }
    }
}
