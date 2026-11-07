using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace SurfaceInsight.Network;

/// <summary>Wire protocol between the tray host and the elevated enhanced-session broker: length-prefixed JSON frames with fixed message types.</summary>
public static class Ipc
{
    public const int Version = 2, MaxFrameBytes = 1024 * 1024, MaxQueuedFrames = 32, HeartbeatSeconds = 2, WatchdogSeconds = 15;
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static async Task WriteAsync(Stream s, Msg m, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, Json); if (body.Length > MaxFrameBytes) throw new InvalidOperationException("Frame exceeds the 1 MiB limit.");
        var buf = new byte[4 + body.Length]; BitConverter.TryWriteBytes(buf.AsSpan(0, 4), body.Length); body.CopyTo(buf, 4); await s.WriteAsync(buf, ct).ConfigureAwait(false); await s.FlushAsync(ct).ConfigureAwait(false);
    }
    /// <summary>Reads one frame. Returns null at end of stream. Throws for oversized, empty or malformed frames and for unknown message types.</summary>
    public static async Task<Msg?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4]; if (!await ReadExactAsync(s, head, ct).ConfigureAwait(false)) return null;
        int len = BitConverter.ToInt32(head); if (len <= 0 || len > MaxFrameBytes) throw new InvalidDataException("Invalid frame length " + len);
        var body = new byte[len]; if (!await ReadExactAsync(s, body, ct).ConfigureAwait(false)) return null;
        return Parse(body);
    }
    public static Msg Parse(byte[] body)
    {
        Msg? m; try { m = JsonSerializer.Deserialize<Msg>(body, Json); } catch (JsonException ex) { throw new InvalidDataException("Malformed frame: " + ex.Message); }
        if (m == null || Array.IndexOf(Types, m.T) < 0) throw new InvalidDataException("Unknown message type"); return m;
    }
    public static readonly string[] Types = { "hello", "welcome", "batch", "hb", "stop", "status", "block-add", "block-remove", "block-remove-all", "block-result" };
    static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct) { int off = 0; while (off < buf.Length) { int n = await s.ReadAsync(buf.AsMemory(off), ct).ConfigureAwait(false); if (n == 0) return false; off += n; } return true; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll")] static extern bool QueryUnbiasedInterruptTime(out ulong t);
    public static int ClientPid(NamedPipeServerStream s) => GetNamedPipeClientProcessId(s.SafePipeHandle.DangerousGetHandle(), out var p) ? (int)p : -1;
    public static int ServerPid(NamedPipeClientStream s) => GetNamedPipeServerProcessId(s.SafePipeHandle.DangerousGetHandle(), out var p) ? (int)p : -1;
    /// <summary>Awake time in seconds: does not advance during sleep, so the watchdog never expires because the machine slept.</summary>
    public static double AwakeSeconds() => QueryUnbiasedInterruptTime(out var t) ? t / 1e7 : Environment.TickCount64 / 1000.0;
    public static string? ImagePath(int pid) { var (_, app) = ProcessIdentity.Resolve(pid); return app.Resolved ? app.Path : null; }
    public static bool SamePath(string? a, string? b) => a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

public sealed class Msg
{
    public string T { get; set; } = ""; public int V { get; set; } public string? Nonce { get; set; } public int Pid { get; set; } public bool Ok { get; set; } public string? Error { get; set; }
    public long DroppedFrames { get; set; } public ulong DroppedBytes { get; set; }
    public long Seq { get; set; } public long Utc { get; set; } public List<BucketMsg>? Buckets { get; set; } public List<NameMsg>? Names { get; set; }
    public long Lost { get; set; } public long Overflow { get; set; } public long Unsupported { get; set; } public long FlowsDropped { get; set; }
    public string? Session { get; set; } public bool DnsOk { get; set; } public string? Note { get; set; }
    public long Req { get; set; } public string? Path { get; set; } public List<string>? Blocks { get; set; } public bool PolicyOk { get; set; }
}
public sealed class BucketMsg { public int Pid { get; set; } public long Start { get; set; } public string? Path { get; set; } public int Proto { get; set; } public string Ip { get; set; } = ""; public int Port { get; set; } public ulong Sent { get; set; } public ulong Recv { get; set; } public int Sc { get; set; } public int Rc { get; set; } }
public sealed class NameMsg { public string Name { get; set; } = ""; public List<string> Ips { get; set; } = new(); public int Pid { get; set; } public long Start { get; set; } public string? Path { get; set; } public string Src { get; set; } = ""; public long T { get; set; } }
