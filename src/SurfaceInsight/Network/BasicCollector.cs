using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
namespace SurfaceInsight.Network;

/// <summary>Resolves a PID to a canonical executable path and creation time. Protected processes may not resolve; they stay "Unattributed".</summary>
public static class ProcessIdentity
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    const uint QueryLimited = 0x1000;
    static readonly Dictionary<ProcessInstanceKey, ApplicationIdentity> cache = new();

    public static (ProcessInstanceKey Key, ApplicationIdentity App) Resolve(int pid)
    {
        if (pid <= 4) return (new(pid, 0), ApplicationIdentity.Unattributed(pid));
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return (new(pid, 0), ApplicationIdentity.Unattributed(pid));
        try
        {
            if (!GetProcessTimes(h, out long create, out _, out _, out _)) create = 0;
            var key = new ProcessInstanceKey(pid, create); if (cache.TryGetValue(key, out var hit)) return (key, hit);
            var sb = new StringBuilder(1024); int len = sb.Capacity; ApplicationIdentity id;
            if (QueryFullProcessImageNameW(h, 0, sb, ref len) && len > 0) { string path = sb.ToString(0, len); id = new ApplicationIdentity(path, System.IO.Path.GetFileName(path), true); }
            else id = ApplicationIdentity.Unattributed(pid);
            if (cache.Count > 4000) cache.Clear(); cache[key] = id; return (key, id);
        }
        finally { CloseHandle(h); }
    }
}

/// <summary>Standard-user collector: TCP/UDP table snapshots plus interface counters. No per-flow bytes exist at this level.</summary>
public sealed class BasicCollector
{
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    const int AfInet = 2, AfInet6 = 23, TcpOwnerPidAll = 5, UdpOwnerPid = 1;
    static readonly Regex Virtual = new("lightweight filter|packet scheduler|native wifi filter|wfp|qos|kernel debugger|virtual|vEthernet|vmware|hyper-v|tap-|tap |wintun|vpn|wireguard|loopback|bluetooth|tunnel|pseudo|teredo|isatap|6to4|npcap|miniport", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly Dictionary<string, (ulong Rx, ulong Tx, long Ticks)> previous = new(); public volatile string Stage = "idle";
    public void ResetBaselines() => previous.Clear();
    /// <summary>Byte deltas between two counter readings; null when the counters went backwards (reset), no time elapsed, or the gap exceeds 3x the interval (sleep, stall).</summary>
    public static (ulong Rx, ulong Tx)? Delta(ulong rxPrev, ulong txPrev, ulong rx, ulong tx, double seconds, double expectedIntervalSeconds) => rx >= rxPrev && tx >= txPrev && seconds > 0 && seconds <= Math.Max(30, expectedIntervalSeconds * 3) ? (rx - rxPrev, tx - txPrev) : null;

    static ushort Port(uint raw) => (ushort)(((raw & 0xff) << 8) | ((raw >> 8) & 0xff));
    static string Ip4(uint a) => new IPAddress(a).ToString();
    static (string Ip, uint Scope) Ip6(byte[] b, int off, uint scope) { var addr = new IPAddress(b[off..(off + 16)]); return (addr.ToString(), scope); }

    delegate uint TableCall(IntPtr buf, ref int size);
    static byte[] Fetch(TableCall call)
    {
        int size = 16 * 1024;
        for (int i = 0; i < 8; i++)
        {
            var p = Marshal.AllocHGlobal(size);
            try
            {
                uint r = call(p, ref size);
                if (r == 0) { var data = new byte[size]; Marshal.Copy(p, data, 0, size); return data; }
                if (r != 122) throw new System.ComponentModel.Win32Exception((int)r, "IP Helper table call failed");
                size += 4096; // ERROR_INSUFFICIENT_BUFFER: the table grew between calls
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        throw new InvalidOperationException("Connection table kept growing; snapshot abandoned.");
    }

    public List<Conn> ReadConnections()
    {
        var list = new List<Conn>();
        Stage = "tcp4"; var t4 = Fetch((IntPtr p, ref int s) => GetExtendedTcpTable(p, ref s, false, AfInet, TcpOwnerPidAll, 0));
        int n = BitConverter.ToInt32(t4, 0);
        for (int i = 0, o = 4; i < n && o + 24 <= t4.Length; i++, o += 24)
        {
            int state = BitConverter.ToInt32(t4, o); if (state == 11 || state == 12 || state == 1) continue; // TIME_WAIT/DELETE_TCB have no live owner
            uint lAddr = BitConverter.ToUInt32(t4, o + 4), lPort = BitConverter.ToUInt32(t4, o + 8), rAddr = BitConverter.ToUInt32(t4, o + 12), rPort = BitConverter.ToUInt32(t4, o + 16); int pid = BitConverter.ToInt32(t4, o + 20);
            list.Add(new Conn(6, Ip4(lAddr), Port(lPort), rAddr == 0 && rPort == 0 ? null : Ip4(rAddr), Port(rPort), 0, state, pid));
        }
        Stage = "tcp6"; var t6 = Fetch((IntPtr p, ref int s) => GetExtendedTcpTable(p, ref s, false, AfInet6, TcpOwnerPidAll, 0));
        n = BitConverter.ToInt32(t6, 0);
        for (int i = 0, o = 4; i < n && o + 56 <= t6.Length; i++, o += 56)
        {
            int state = BitConverter.ToInt32(t6, o + 48); if (state == 11 || state == 12 || state == 1) continue;
            var (lip, _) = Ip6(t6, o, BitConverter.ToUInt32(t6, o + 16)); uint lPort = BitConverter.ToUInt32(t6, o + 20); var (rip, rScope) = Ip6(t6, o + 24, BitConverter.ToUInt32(t6, o + 40)); uint rPort = BitConverter.ToUInt32(t6, o + 44); int pid = BitConverter.ToInt32(t6, o + 52);
            bool none = IPAddress.IPv6Any.ToString() == rip && rPort == 0; list.Add(new Conn(6, lip, Port(lPort), none ? null : rip, Port(rPort), rScope, state, pid));
        }
        Stage = "udp4"; var u4 = Fetch((IntPtr p, ref int s) => GetExtendedUdpTable(p, ref s, false, AfInet, UdpOwnerPid, 0));
        n = BitConverter.ToInt32(u4, 0);
        for (int i = 0, o = 4; i < n && o + 12 <= u4.Length; i++, o += 12) list.Add(new Conn(17, Ip4(BitConverter.ToUInt32(u4, o)), Port(BitConverter.ToUInt32(u4, o + 4)), null, 0, 0, 0, BitConverter.ToInt32(u4, o + 8)));
        Stage = "udp6"; var u6 = Fetch((IntPtr p, ref int s) => GetExtendedUdpTable(p, ref s, false, AfInet6, UdpOwnerPid, 0));
        n = BitConverter.ToInt32(u6, 0);
        for (int i = 0, o = 4; i < n && o + 28 <= u6.Length; i++, o += 28) { var (ip, _) = Ip6(u6, o, 0); list.Add(new Conn(17, ip, Port(BitConverter.ToUInt32(u6, o + 20)), null, 0, 0, 0, BitConverter.ToInt32(u6, o + 24))); }
        return list;
    }

    public List<AdapterReading> ReadAdapters()
    {
        var l = new List<AdapterReading>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var st = nic.GetIPStatistics(); bool phys = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 && !Virtual.IsMatch(nic.Description + " " + nic.Name);
                l.Add(new AdapterReading(nic.Id, nic.Name, nic.NetworkInterfaceType.ToString(), phys, nic.OperationalStatus == OperationalStatus.Up, (ulong)Math.Max(0, st.BytesReceived), (ulong)Math.Max(0, st.BytesSent)));
            }
            catch { }
        }
        return l;
    }

    /// <summary>Takes one snapshot. <paramref name="resumed"/> or a gap longer than 3x the interval discards counter baselines so no inflated rates are produced.</summary>
    public NetSnapshot Read(double expectedIntervalSeconds, bool resumed = false)
    {
        if (resumed) previous.Clear();
        var now = DateTime.UtcNow; long ticks = Stopwatch.GetTimestamp(); string? error = null; List<Conn> conns = new();
        try { conns = ReadConnections(); } catch (Exception ex) { error = "Connection tables: " + ex.Message; }
        Stage = "processes"; var procs = new Dictionary<int, (ProcessInstanceKey, ApplicationIdentity)>(); foreach (var pid in conns.Select(c => c.Pid).Distinct()) procs[pid] = ProcessIdentity.Resolve(pid);
        Stage = "adapters"; var adapters = new List<AdapterReading>(); var deltas = new List<AdapterDelta>();
        try
        {
            adapters = ReadAdapters(); var seen = new HashSet<string>();
            foreach (var a in adapters)
            {
                seen.Add(a.Id);
                if (previous.TryGetValue(a.Id, out var p)) { double sec = (ticks - p.Ticks) / (double)Stopwatch.Frequency; if (Delta(p.Rx, p.Tx, a.RxBytes, a.TxBytes, sec, expectedIntervalSeconds) is var (rx, tx)) deltas.Add(new AdapterDelta(a.Id, rx, tx, sec)); }
                previous[a.Id] = (a.RxBytes, a.TxBytes, ticks);
            }
            foreach (var k in previous.Keys.Where(k => !seen.Contains(k)).ToList()) previous.Remove(k);
        }
        catch (Exception ex) { error = (error == null ? "" : error + "; ") + "Adapters: " + ex.Message; }
        Stage = "done"; return new NetSnapshot(now, conns, adapters, deltas, error, procs);
    }
}
