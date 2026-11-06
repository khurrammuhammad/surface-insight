using System.Text;
namespace SurfaceInsight;

public record Ranked(string Name, double Cpu, double Peak, double Memory, double Io, double? Gpu);

/// <summary>Ranking and plain-language findings over raw samples (used by exports and self-tests).</summary>
public static class Insights
{
    public static double CpuSeconds(List<Sample> data, string name) => data.Sum(s => s.Apps.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(a => a.Cpu) * Environment.ProcessorCount / 100 * s.Seconds);
    public static List<Ranked> Rank(List<Sample> data) { double n = Math.Max(1, data.Sum(s => s.Seconds)); return data.SelectMany(s => s.Apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => new Ranked(g.Key, g.Sum(x => x.Cpu) * s.Seconds, g.Sum(x => x.Cpu), g.Sum(x => x.Memory), g.Sum(x => x.Io) * s.Seconds, g.Any(x => x.Gpu.HasValue) ? g.Max(x => x.Gpu) : null))).GroupBy(a => a.Name).Select(g => new Ranked(g.Key, g.Sum(x => x.Cpu) / n, g.Max(x => x.Peak), g.Average(x => x.Memory), g.Sum(x => x.Io) / n, g.Max(x => x.Gpu))).OrderByDescending(a => a.Cpu).ToList(); }
    public static string Findings(List<Sample> data) { if (data.Count < 3) return "Building an activity baseline. Allow at least three samples."; var top = Rank(data).Take(3); var sb = new StringBuilder(); sb.AppendLine($"{data[0].Time.ToLocalTime():t}–{data[^1].Time.ToLocalTime():t} · {data.Count} observations"); foreach (var a in top) sb.AppendLine($"• {a.Name}: average CPU {a.Cpu:F1}%, peak {a.Peak:F1}%. {(a.Cpu > 3 ? "Moderate confidence: sustained CPU activity can contribute to heat and drain." : "Low confidence: activity alone does not establish a drain cause.")} Check whether its work is expected; compare an interval after it finishes."); var battery = data.Where(a => !a.Ac && a.Watts.HasValue).ToList(); if (battery.Count > 0) sb.AppendLine($"Measured device draw averaged {battery.Average(x => x.Watts):F1} W on battery. This includes the display and hardware; per-application watts are unavailable."); return sb.ToString(); }
    public static string Activity(double avg, double peak) => avg > 2 ? "Sustained" : peak > 5 ? "Brief spikes" : "Low";
}
