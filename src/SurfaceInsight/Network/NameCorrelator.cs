namespace SurfaceInsight.Network;

public enum NameEvidenceKind { Unknown, ProcessDns, SystemDns, ReverseDns }
public sealed record NameResult(string? Name, NameEvidenceKind Kind, int OtherNames, IReadOnlyList<string> Candidates)
{
    public static readonly NameResult None = new(null, NameEvidenceKind.Unknown, 0, Array.Empty<string>());
    public string Label => Kind switch { NameEvidenceKind.ProcessDns => "Process DNS match", NameEvidenceKind.SystemDns => "Recent system DNS candidate", NameEvidenceKind.ReverseDns => "Reverse DNS (PTR)", _ => "Unknown hostname" };
}

/// <summary>
/// Chooses a hostname for an observed endpoint from DNS evidence. Evidence is correlation, never proof:
/// an answer must precede (or just follow) the observed activity, a requesting-process match beats a system-wide candidate,
/// every distinct candidate is kept, and bytes are never copied onto names.
/// </summary>
public static class NameCorrelator
{
    public const int WindowSeconds = 300; // no TTL is reported by the DNS-Client events, so a five-minute window applies and is labeled weaker evidence

    /// <param name="appIds">Applications observed talking to this endpoint (process-scoped evidence only counts for them).</param>
    public static NameResult Resolve(string ip, IReadOnlyCollection<long> appIds, long firstSeenMinute, long lastSeenMinute, IEnumerable<NameRow> evidence)
    {
        long from = firstSeenMinute * 60 - WindowSeconds, to = lastSeenMinute * 60 + 60;
        var cands = evidence.Where(e => e.Ip == ip && e.Src != "ptr" && e.T >= from && e.T <= to).ToList();
        if (cands.Count == 0) { var ptr = evidence.FirstOrDefault(e => e.Ip == ip && e.Src == "ptr"); return ptr == null ? NameResult.None : new NameResult(ptr.Name, NameEvidenceKind.ReverseDns, 0, new[] { ptr.Name }); }
        var proc = cands.Where(e => e.AppId != 0 && appIds.Contains(e.AppId)).OrderByDescending(e => e.T).ToList();
        var all = cands.OrderByDescending(e => e.T).Select(e => e.Name).Distinct().ToList();
        if (proc.Count > 0) { string primary = proc[0].Name; return new NameResult(primary, NameEvidenceKind.ProcessDns, all.Count - 1, all); }
        return new NameResult(all[0], NameEvidenceKind.SystemDns, all.Count - 1, all);
    }
}
