using System.Net;
using System.Net.Sockets;
namespace SurfaceInsight.Network;

public enum NetMode { Off, Basic, Enhanced, Starting, Partial, Stale, Stopped }
public enum IpScope { Unspecified, Loopback, LinkLocal, Private, CarrierGrade, Multicast, Public, Special }

/// <summary>Boot-independent process instance identity: PID plus creation FILETIME. A reused PID gets a different start time.</summary>
public readonly record struct ProcessInstanceKey(int Pid, long StartFileTime);

/// <summary>An application is identified by its canonical image path (case-insensitive). Same-named binaries in different folders are different applications.</summary>
public sealed record ApplicationIdentity(string Path, string Name, bool Resolved)
{
    public static ApplicationIdentity Unattributed(int pid) => new("unattributed:" + pid, pid == 0 ? "System (no owning process)" : pid == 4 ? "System" : "Unattributed (PID " + pid + ")", false);
}

/// <summary>A remote (or local) transport endpoint. The IP is canonical text; IPv6 scope is carried separately.</summary>
public sealed record EndpointIdentity(string Ip, ushort Port, uint Scope = 0)
{
    public AddressFamily Family => Ip.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
    public bool IsIPv6 => Family == AddressFamily.InterNetworkV6;
}

/// <summary>One row of a TCP/UDP table snapshot. TCP snapshots have no byte counts and UDP tables list local bindings only.</summary>
public sealed record Conn(byte Proto, string LocalIp, ushort LocalPort, string? RemoteIp, ushort RemotePort, uint RemoteScope, int State, int Pid)
{
    public bool HasRemote => RemoteIp != null && IpClassifier.Classify(RemoteIp) != IpScope.Unspecified;
    public bool IsListening => Proto == 6 && State == 2;
    public string StateName => Proto == 17 ? "bound" : State switch { 2 => "listening", 3 => "SYN sent", 4 => "SYN received", 5 => "established", 6 => "FIN wait 1", 7 => "FIN wait 2", 8 => "close wait", 9 => "closing", 10 => "last ACK", _ => "state " + State };
}

public sealed record AdapterReading(string Id, string Name, string Type, bool Physical, bool Up, ulong RxBytes, ulong TxBytes);
/// <summary>Byte deltas between two readings of one interface. Null deltas mean no valid baseline (first reading, reset, resume or long gap).</summary>
public sealed record AdapterDelta(string Id, ulong Rx, ulong Tx, double Seconds);
public sealed record NetSnapshot(DateTime Utc, List<Conn> Conns, List<AdapterReading> Adapters, List<AdapterDelta> Deltas, string? Error, Dictionary<int, (ProcessInstanceKey Key, ApplicationIdentity App)>? Procs = null);

public sealed record NetworkCoverage(DateTime FromUtc, DateTime ToUtc, string Kind, string Detail);

public static class IpClassifier
{
    public static IpScope Classify(string ip)
    {
        if (!IPAddress.TryParse(ip.Split('%')[0], out var a)) return IpScope.Special;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.Any.Equals(a) || IPAddress.IPv6Any.Equals(a)) return IpScope.Unspecified;
        if (IPAddress.IsLoopback(a)) return IpScope.Loopback;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return IpScope.Private;
            if (b[0] == 169 && b[1] == 254) return IpScope.LinkLocal;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return IpScope.CarrierGrade;
            if (b[0] >= 224 && b[0] <= 239) return IpScope.Multicast;
            if (b[0] == 0 || b[0] >= 240 || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 192 && b[1] == 0 && b[2] == 2) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113)) return IpScope.Special;
            return IpScope.Public;
        }
        if (a.IsIPv6LinkLocal) return IpScope.LinkLocal;
        if (b[0] == 0xff) return IpScope.Multicast;
        if ((b[0] & 0xfe) == 0xfc) return IpScope.Private; // unique local fc00::/7
        if (a.IsIPv6SiteLocal) return IpScope.Private;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return IpScope.Special; // documentation 2001:db8::/32
        if ((b[0] & 0xe0) == 0x20) return IpScope.Public; // global unicast 2000::/3
        return IpScope.Special;
    }
    public static string Label(IpScope s) => s switch { IpScope.Public => "Public", IpScope.Private => "Private", IpScope.Loopback => "Loopback", IpScope.LinkLocal => "Link-local", IpScope.Multicast => "Multicast", IpScope.CarrierGrade => "Carrier-grade NAT", IpScope.Special => "Special-use", _ => "Unspecified" };
}
