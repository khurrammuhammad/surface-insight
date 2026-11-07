using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
namespace SurfaceInsight.Network;

/// <summary>One decoded transport event. Remote is the far end of the flow (see <see cref="EtwDecode.Orient"/>).</summary>
public readonly record struct FlowEvent(int Pid, byte Proto, string RemoteIp, ushort RemotePort, uint Bytes, bool Send);
sealed record NameRaw(string Name, List<string> Ips, int Pid, string Src);

/// <summary>Pure decoding rules learned from the compatibility probe; unit-tested without ETW.</summary>
public static class EtwDecode
{
    public const int TcpSend = 10, TcpRecv = 11, TcpSend6 = 26, TcpRecv6 = 27, UdpSend = 42, UdpRecv = 43, UdpSend6 = 58, UdpRecv6 = 59;
    public static bool IsData(int id) => id is TcpSend or TcpRecv or TcpSend6 or TcpRecv6 or UdpSend or UdpRecv or UdpSend6 or UdpRecv6;
    public static byte ProtoOf(int id) => id is UdpSend or UdpRecv or UdpSend6 or UdpRecv6 ? (byte)17 : (byte)6;
    public static bool IsSend(int id) => id is TcpSend or TcpSend6 or UdpSend or UdpSend6;
    /// <summary>IPv4 addresses arrive as int32 holding the network-order bytes read little-endian (127.0.0.1 = 16777343).</summary>
    public static string Ip4(int v) => new IPAddress(BitConverter.GetBytes(v)).ToString();
    /// <summary>Ports arrive as signed 16-bit values with bytes swapped (443 = -17663).</summary>
    public static ushort Port(int v) { ushort u = unchecked((ushort)v); return (ushort)((u << 8) | (u >> 8)); }
    /// <summary>TCP and UDP-send events list the remote end as daddr/dport. UDP-receive events list the sender as saddr/sport (observed with multicast traffic), so the remote end is the source.</summary>
    public static bool RemoteIsSource(int id) => id is UdpRecv or UdpRecv6;
}

/// <summary>Bounded per-interval aggregation of flow events. Overflow bytes are preserved in an explicit unattributed bucket.</summary>
public sealed class FlowAggregator
{
    public const int MaxGroups = 10_000;
    readonly Dictionary<(int Pid, byte Proto, string Ip, ushort Port), (ulong Sent, ulong Recv, int Sc, int Rc)> groups = new(); public long FlowsDropped;
    public void Add(in FlowEvent e)
    {
        var key = (e.Pid, e.Proto, e.RemoteIp, e.RemotePort);
        if (!groups.TryGetValue(key, out var a)) { if (groups.Count >= MaxGroups) { key = (-1, e.Proto, "", (ushort)0); FlowsDropped++; groups.TryGetValue(key, out a); } }
        if (e.Send) a = (a.Sent + e.Bytes, a.Recv, a.Sc + 1, a.Rc); else a = (a.Sent, a.Recv + e.Bytes, a.Sc, a.Rc + 1);
        groups[key] = a;
    }
    public List<BucketMsg> Drain(Func<int, (long Start, string? Path)> resolve)
    {
        var list = new List<BucketMsg>(groups.Count); var cache = new Dictionary<int, (long, string?)>();
        foreach (var (k, v) in groups) { (long Start, string? Path) id = (0, null); if (k.Pid > 0 && !cache.TryGetValue(k.Pid, out var c)) cache[k.Pid] = c = resolve(k.Pid); if (k.Pid > 0) id = cache[k.Pid]; list.Add(new BucketMsg { Pid = k.Pid, Start = id.Start, Path = id.Path, Proto = k.Proto, Ip = k.Ip, Port = k.Port, Sent = v.Sent, Recv = v.Recv, Sc = v.Sc, Rc = v.Rc }); }
        groups.Clear(); return list;
    }
    public int Count => groups.Count;
}

/// <summary>Elevated role: owns a private ETW session and streams one-second aggregates to the tray host. It owns only its ETW session and explicit temporary WFP blocks.</summary>
public static class BrokerRole
{
    static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88"), DnsClient = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    public const string SessionPrefix = "SurfaceInsightNet-";
    const int MaxQueue = 200_000, MaxNames = 20_000;
    static readonly ConcurrentQueue<FlowEvent> queue = new(); static readonly ConcurrentQueue<NameRaw> nameQueue = new(); static long queued, overflow, unsupported, nameCount;
    static readonly Dictionary<int, int[]> index = new();

    /// <summary>--network-broker &lt;pipeName&gt; &lt;nonce&gt; &lt;hostPid&gt; &lt;userSid&gt;</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[3], out int hostPid)) return 2; string pipe = args[1], nonce = args[2];
        if (TraceEventSession.IsElevated() != true) return 3;
        if (args.Length != 5 || System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value != args[4]) return 6;
        using var cts = new CancellationTokenSource(); TraceEventSession? session = null; var started = Ipc.AwakeSeconds();
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            client.Connect(10_000);
            // The server must be our tray host: right PID and the very same executable.
            if (Ipc.ServerPid(client) != hostPid || !Ipc.SamePath(Ipc.ImagePath(hostPid), Environment.ProcessPath)) return 4;
            Ipc.WriteAsync(client, new Msg { T = "hello", V = Ipc.Version, Nonce = nonce, Pid = Environment.ProcessId }, cts.Token).GetAwaiter().GetResult();
            var welcome = Ipc.ReadAsync(client, cts.Token).GetAwaiter().GetResult(); if (welcome is not { T: "welcome", Ok: true }) return 5;

            string name = SessionPrefix + hostPid + "-" + Guid.NewGuid().ToString("N"); session = new TraceEventSession(name) { BufferSizeMB = 32 }; session.CpuSampleIntervalMSec = 0;
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, ulong.MaxValue); bool dnsOk = true; try { session.EnableProvider(DnsClient, TraceEventLevel.Informational, ulong.MaxValue); } catch { dnsOk = false; }
            WfpPolicy? policy = null; string policyNote = ""; try { policy = new WfpPolicy(); PolicyForCleanup = policy; } catch (Exception ex) { policyNote = "Blocking unavailable: " + ex.Message; }
            session.Source.Dynamic.All += OnEvent; var proc = new Thread(() => { try { session.Source.Process(); } catch { } finally { cts.Cancel(); } }) { IsBackground = true, Name = "etw" }; proc.Start();
            var outbox = new BlockingCollection<Msg>(Ipc.MaxQueuedFrames); var delivery = new BrokerDelivery(outbox); double lastHost = Ipc.AwakeSeconds();
            var writer = Task.Run(async () => { try { foreach (var m in outbox.GetConsumingEnumerable(cts.Token)) await Ipc.WriteAsync(client, m, cts.Token); } catch { cts.Cancel(); } });
            var reader = Task.Run(async () => { try { while (!cts.IsCancellationRequested) { var m = await Ipc.ReadAsync(client, cts.Token); if (m == null) break; if (m.T == "stop") break; if (m.T == "hb") lastHost = Ipc.AwakeSeconds(); else if (m.T.StartsWith("block-")) HandlePolicy(m, policy, outbox); } } catch { } cts.Cancel(); });
            outbox.Add(new Msg { T = "status", Ok = true, Session = name, DnsOk = dnsOk, PolicyOk = policy != null, Note = (dnsOk ? "Kernel-Network and DNS-Client providers enabled." : "DNS-Client provider could not be enabled; names unavailable.") + (policyNote.Length > 0 ? " " + policyNote : "") });

            Task.Run(SeedFromCache); double nextSeed = Ipc.AwakeSeconds() + 300;
            var agg = new FlowAggregator(); var hostProc = System.Diagnostics.Process.GetProcessById(hostPid); long seq = 0; double nextHb = 0; long lastLost = 0; var tick = new System.Diagnostics.Stopwatch(); tick.Start();
            while (!cts.IsCancellationRequested)
            {
                if (cts.Token.WaitHandle.WaitOne(1000)) break;
                if (hostProc.HasExited || Ipc.AwakeSeconds() - lastHost > Ipc.WatchdogSeconds) break;
                int n = 0; while (n++ < 400_000 && queue.TryDequeue(out var fe)) { Interlocked.Decrement(ref queued); agg.Add(fe); }
                var names = new List<NameMsg>(); while (names.Count < 500 && nameQueue.TryDequeue(out var nr)) { Interlocked.Decrement(ref nameCount); var (st, path) = Resolve(nr.Pid); names.Add(new NameMsg { Name = nr.Name, Ips = nr.Ips, Pid = nr.Pid, Start = st, Path = path, Src = nr.Src, T = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }); }
                long newLost = session.EventsLost, lostDelta = newLost - lastLost; lastLost = newLost; var buckets = agg.Drain(Resolve); long flowsDropped = agg.FlowsDropped; agg.FlowsDropped = 0;
                if (buckets.Count > 0 || names.Count > 0 || lostDelta != 0 || Interlocked.Read(ref overflow) > 0 || Interlocked.Read(ref unsupported) > 0)
                {
                    foreach (var chunk in Chunk(buckets, names)) { chunk.Seq = ++seq; chunk.Utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); chunk.Lost = lostDelta; chunk.Overflow = Interlocked.Exchange(ref overflow, 0); chunk.Unsupported = Interlocked.Exchange(ref unsupported, 0); chunk.FlowsDropped = flowsDropped; lostDelta = 0; flowsDropped = 0; delivery.TrySend(chunk); }
                }
                if (delivery.Pending) delivery.TrySend(new Msg { T = "batch", Seq = ++seq, Utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                if (Ipc.AwakeSeconds() >= nextSeed) { nextSeed = Ipc.AwakeSeconds() + 300; _ = Task.Run(SeedFromCache); }
                if (tick.Elapsed.TotalSeconds >= nextHb) { outbox.TryAdd(new Msg { T = "hb", Pid = Environment.ProcessId }); nextHb = tick.Elapsed.TotalSeconds + Ipc.HeartbeatSeconds; }
            }
            return 0;
        }
        catch (Exception) { return 1; }
        finally { try { PolicyForCleanup?.Dispose(); } catch { } try { session?.Stop(); session?.Dispose(); } catch { } }
    }

    static WfpPolicy? PolicyForCleanup;
    /// <summary>Applies one validated policy request and always answers with an explicit success or failure. Nothing here takes shell commands, files or SQL.</summary>
    static void HandlePolicy(Msg m, WfpPolicy? policy, BlockingCollection<Msg> outbox)
    {
        var r = new Msg { T = "block-result", Req = m.Req, Path = m.Path };
        try
        {
            if (policy == null) throw new InvalidOperationException("The filtering engine is not available in this session.");
            PolicyForCleanup = policy;
            if (m.T == "block-add")
            {
                var own = new[] { Environment.ProcessPath! }; string? why = PolicyRules.Reject(m.Path, own); if (why != null) { r.Ok = false; r.Error = why; }
                else { policy.Add(m.Path!); r.Ok = true; }
            }
            else if (m.T == "block-remove") { r.Ok = policy.Remove(m.Path ?? ""); if (!r.Ok) r.Error = "That application is not blocked by this session."; }
            else if (m.T == "block-remove-all") { policy.RemoveAll(); r.Ok = policy.Blocked.Count == 0; if (!r.Ok) r.Error = "Some blocks could not be removed."; }
        }
        catch (Exception ex) { r.Ok = false; r.Error = ex.Message; }
        r.Blocks = policy?.Blocked.ToList() ?? new(); outbox.TryAdd(r);
    }
    static IEnumerable<Msg> Chunk(List<BucketMsg> b, List<NameMsg> n)
    {
        const int PerFrame = 2500; int i = 0; bool first = true;
        while (i < b.Count || first) { first = false; var m = new Msg { T = "batch", Buckets = b.Skip(i).Take(PerFrame).ToList(), Names = i == 0 ? n : null }; i += PerFrame; yield return m; if (b.Count == 0) break; }
    }
    static (long Start, string? Path) Resolve(int pid) { if (pid <= 0) return (0, null); var (k, app) = ProcessIdentity.Resolve(pid); return (k.StartFileTime, app.Resolved ? app.Path : null); }

    /// <summary>Reads the Windows DNS client cache (administrator only) so connections opened before the session can still be named.
    /// Cache entries have no requesting process, so they only ever count as system-wide candidates.</summary>
    static void SeedFromCache()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Entry, Data, Type FROM MSFT_DNSClientCache"); var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var res = q.Get();
            foreach (System.Management.ManagementObject o in res) using (o) { int type = Convert.ToInt32(o["Type"]); if (type != 1 && type != 28) continue; string entry = Convert.ToString(o["Entry"]) ?? "", data = Convert.ToString(o["Data"]) ?? ""; if (entry.Length == 0 || entry.Length > 255 || !IPAddress.TryParse(data, out var ip)) continue; if (!map.TryGetValue(entry, out var l)) map[entry] = l = new(); if (l.Count < 32) l.Add(ip.ToString()); }
            foreach (var (name, ips) in map) { if (Interlocked.Read(ref nameCount) >= MaxNames) break; Interlocked.Increment(ref nameCount); nameQueue.Enqueue(new NameRaw(name.TrimEnd('.').ToLowerInvariant(), ips, 0, "cache")); }
        }
        catch { Interlocked.Increment(ref unsupported); }
    }
    // ---- ETW callbacks: decode into small structs and enqueue; no I/O, no lookups.
    static void OnEvent(TraceEvent e)
    {
        try
        {
            int id = (int)e.ID;
            if (e.ProviderGuid == KernelNetwork) { if (EtwDecode.IsData(id)) OnData(e, id); return; }
            if (e.ProviderGuid == DnsClient && (id == 3008 || id == 3020)) OnDns(e, id);
        }
        catch { Interlocked.Increment(ref unsupported); }
    }
    static void OnData(TraceEvent e, int id)
    {
        if (!index.TryGetValue(id, out var ix)) { var names = e.PayloadNames; var a = new[] { Array.IndexOf(names, "PID"), Array.IndexOf(names, "size"), Array.IndexOf(names, "daddr"), Array.IndexOf(names, "saddr"), Array.IndexOf(names, "dport"), Array.IndexOf(names, "sport") }; lock (index) index[id] = ix = a; }
        if (ix.Any(x => x < 0)) { Interlocked.Increment(ref unsupported); return; }
        int pid = Convert.ToInt32(e.PayloadValue(ix[0])); long size = Convert.ToInt64(e.PayloadValue(ix[1])); if (size <= 0 || size > uint.MaxValue) return;
        bool src = EtwDecode.RemoteIsSource(id); object addrObj = e.PayloadValue(src ? ix[3] : ix[2]); int portRaw = Convert.ToInt32(e.PayloadValue(src ? ix[5] : ix[4]));
        string ip = addrObj switch { byte[] b when b.Length == 16 => new IPAddress(b).ToString(), byte[] b4 when b4.Length == 4 => new IPAddress(b4).ToString(), _ => EtwDecode.Ip4(Convert.ToInt32(addrObj)) };
        if (Interlocked.Read(ref queued) >= MaxQueue) { Interlocked.Increment(ref overflow); return; }
        Interlocked.Increment(ref queued); queue.Enqueue(new FlowEvent(pid, EtwDecode.ProtoOf(id), ip, EtwDecode.Port(portRaw), (uint)size, EtwDecode.IsSend(id)));
    }
    static void OnDns(TraceEvent e, int id)
    {
        string name = Convert.ToString(e.PayloadByName("QueryName")) ?? ""; if (name.Length == 0 || name.Length > 255) return;
        string results = Convert.ToString(e.PayloadByName(id == 3008 ? "QueryResults" : "QueryResults")) ?? ""; int status = Convert.ToInt32(e.PayloadByName(id == 3008 ? "QueryStatus" : "Status")); if (status != 0 || results.Length == 0) return;
        int pid = id == 3008 ? e.ProcessID : Convert.ToInt32(e.PayloadByName("ClientPID"));
        var ips = new List<string>(); foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var p = part.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? part[7..] : part; if (IPAddress.TryParse(p, out var a)) ips.Add(a.ToString()); else if (part.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) { /* CNAME-style record: kept out of the address list */ } }
        if (ips.Count == 0 || Interlocked.Read(ref nameCount) >= MaxNames) return;
        Interlocked.Increment(ref nameCount); nameQueue.Enqueue(new NameRaw(name.TrimEnd('.').ToLowerInvariant(), ips.Take(32).ToList(), pid, id == 3008 ? "3008" : "3020"));
    }
}
