using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace SurfaceInsight.Network;

/// <summary>What may be blocked. Pure rules so the tray UI and the elevated broker agree and tests can cover them.</summary>
public static class PolicyRules
{
    public const int MaxBlocks = 50;
    static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase) { "svchost.exe", "services.exe", "lsass.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "smss.exe", "dwm.exe", "fontdrvhost.exe", "system", "ntoskrnl.exe", "dllhost.exe", "taskhostw.exe", "runtimebroker.exe", "sihost.exe", "explorer.exe", "msmpeng.exe", "searchindexer.exe" };

    /// <summary>Returns null when the executable may be blocked, otherwise the reason it may not. <paramref name="own"/> are Surface Insight's own executables (host and broker are the same file).</summary>
    public static string? Reject(string? path, IEnumerable<string> own, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No executable path is known for this application (protected or already exited).";
        if (path.StartsWith("unattributed:", StringComparison.OrdinalIgnoreCase)) return "This traffic could not be attributed to an executable.";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "Network and device paths cannot be blocked.";
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return "Only local absolute paths can be blocked.";
        string full; try { full = Path.GetFullPath(path); } catch { return "The path is not valid."; }
        if (!Path.IsPathRooted(full) || full.Length < 4 || full[1] != ':') return "Only local absolute paths can be blocked.";
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "Only .exe programs can be blocked.";
        if (own.Any(o => string.Equals(Path.GetFullPath(o), full, StringComparison.OrdinalIgnoreCase))) return "Surface Insight cannot block itself.";
        string name = Path.GetFileName(full); if (SharedHosts.Contains(name)) return name + " is a shared system host; blocking it would affect unrelated services.";
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\"; if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "Programs inside the Windows folder are protected system targets.";
        string pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\"; if (full.StartsWith(pkg, StringComparison.OrdinalIgnoreCase)) return "Packaged (Store) app identity is not supported yet.";
        if (!(exists ?? File.Exists)(full)) return "The executable no longer exists at that path.";
        return null;
    }
}

/// <summary>Session-scoped Windows Filtering Platform blocks. The engine uses a dynamic session, so Windows removes every filter when the handle closes or the process dies.</summary>
public sealed class WfpPolicy : IDisposable
{
    static readonly Guid LayerConnect4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), LayerConnect6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971"), CondUserId = new("af043a0a-b34d-4f86-979c-c90371af6e66");
    const int FWP_BYTE_BLOB_TYPE = 12, FWP_SECURITY_DESCRIPTOR_TYPE = 14; const uint FWP_ACTION_BLOCK = 0x1001, SessionDynamic = 1;
    [StructLayout(LayoutKind.Explicit, Size = 72)] struct Session { [FieldOffset(0)] public Guid Key; [FieldOffset(16)] public IntPtr Name; [FieldOffset(24)] public IntPtr Desc; [FieldOffset(32)] public uint Flags; [FieldOffset(36)] public uint TxnWait; }
    [StructLayout(LayoutKind.Explicit, Size = 72)] struct SubLayer { [FieldOffset(0)] public Guid Key; [FieldOffset(16)] public IntPtr Name; [FieldOffset(24)] public IntPtr Desc; [FieldOffset(64)] public ushort Weight; }
    [StructLayout(LayoutKind.Explicit, Size = 200)] struct Filter { [FieldOffset(0)] public Guid Key; [FieldOffset(16)] public IntPtr Name; [FieldOffset(24)] public IntPtr Desc; [FieldOffset(64)] public Guid Layer; [FieldOffset(80)] public Guid SubLayer; [FieldOffset(96)] public int WeightType; [FieldOffset(112)] public uint NumConditions; [FieldOffset(120)] public IntPtr Conditions; [FieldOffset(128)] public uint ActionType; [FieldOffset(176)] public ulong Id; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct Condition { [FieldOffset(0)] public Guid Field; [FieldOffset(16)] public int Match; [FieldOffset(24)] public int ValueType; [FieldOffset(32)] public IntPtr Value; }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public uint Size; public IntPtr Data; }
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineOpen0(string? server, uint authn, IntPtr identity, ref Session session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerAdd0(IntPtr engine, ref SubLayer sub, IntPtr sd);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string file, out IntPtr appId);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr p);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    static void Check(uint rc, string what) { if (rc != 0) throw new Win32Exception(unchecked((int)rc), what + " failed (0x" + rc.ToString("X8") + ")"); }

    IntPtr engine; readonly Guid subLayerKey = Guid.NewGuid(); bool subAdded; readonly Dictionary<string, List<ulong>> blocks = new(StringComparer.OrdinalIgnoreCase);
    public WfpPolicy()
    {
        IntPtr name = Marshal.StringToHGlobalUni("SurfaceInsight session"); try { var s = new Session { Key = Guid.NewGuid(), Name = name, Flags = SessionDynamic }; Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, ref s, out engine), "Opening the Windows Filtering Platform engine"); } finally { Marshal.FreeHGlobal(name); }
    }
    public IReadOnlyCollection<string> Blocked => blocks.Keys;

    /// <summary>Adds paired IPv4/IPv6 outbound BLOCK filters for one exact executable path and the current user, atomically. Idempotent.</summary>
    public void Add(string exePath)
    {
        string key = Path.GetFullPath(exePath); if (blocks.ContainsKey(key)) return; if (blocks.Count >= PolicyRules.MaxBlocks) throw new InvalidOperationException("Too many temporary blocks.");
        Check(FwpmTransactionBegin0(engine, 0), "Starting the filter transaction"); var ids = new List<ulong>(); IntPtr appId = IntPtr.Zero, sdPtr = IntPtr.Zero, userBlob = IntPtr.Zero, condMem = IntPtr.Zero;
        try
        {
            if (!subAdded) { IntPtr n = Marshal.StringToHGlobalUni("SurfaceInsight temporary blocks"); var sl = new SubLayer { Key = subLayerKey, Name = n, Weight = 0x8000 }; try { Check(FwpmSubLayerAdd0(engine, ref sl, IntPtr.Zero), "Creating the filter sublayer"); } finally { Marshal.FreeHGlobal(n); } subAdded = true; }
            Check(FwpmGetAppIdFromFileName0(key, out appId), "Resolving the application identity");
            condMem = Marshal.AllocHGlobal(80); Marshal.StructureToPtr(new Condition { Field = CondAppId, Match = 0, ValueType = FWP_BYTE_BLOB_TYPE, Value = appId }, condMem, false);
            string sid = WindowsIdentity.GetCurrent().User!.Value; Check(ConvertStringSecurityDescriptorToSecurityDescriptorW("O:LSD:(A;;0x1;;;" + sid + ")", 1, out sdPtr, out uint len) ? 0u : (uint)Marshal.GetLastWin32Error(), "Building the user condition");
            userBlob = Marshal.AllocHGlobal(Marshal.SizeOf<Blob>()); Marshal.StructureToPtr(new Blob { Size = len, Data = sdPtr }, userBlob, false); Marshal.StructureToPtr(new Condition { Field = CondUserId, Match = 0, ValueType = FWP_SECURITY_DESCRIPTOR_TYPE, Value = userBlob }, condMem + 40, false);
            foreach (var layer in new[] { LayerConnect4, LayerConnect6 })
            {
                IntPtr fn = Marshal.StringToHGlobalUni("SurfaceInsight block " + Path.GetFileName(key));
                try { var f = new Filter { Key = Guid.NewGuid(), Name = fn, Layer = layer, SubLayer = subLayerKey, WeightType = 0, NumConditions = 2, Conditions = condMem, ActionType = FWP_ACTION_BLOCK }; Check(FwpmFilterAdd0(engine, ref f, IntPtr.Zero, out var id), "Adding a block filter"); ids.Add(id); }
                finally { Marshal.FreeHGlobal(fn); }
            }
            Check(FwpmTransactionCommit0(engine), "Committing the filters"); blocks[key] = ids;
        }
        catch { FwpmTransactionAbort0(engine); throw; }
        finally { if (condMem != IntPtr.Zero) Marshal.FreeHGlobal(condMem); if (userBlob != IntPtr.Zero) Marshal.FreeHGlobal(userBlob); if (sdPtr != IntPtr.Zero) LocalFree(sdPtr); if (appId != IntPtr.Zero) FwpmFreeMemory0(ref appId); }
    }
    public bool Remove(string exePath)
    {
        string key = Path.GetFullPath(exePath); if (!blocks.TryGetValue(key, out var ids)) return false;
        Check(FwpmTransactionBegin0(engine, 0), "Starting the filter transaction"); try { foreach (var id in ids) FwpmFilterDeleteById0(engine, id); Check(FwpmTransactionCommit0(engine), "Committing the removal"); blocks.Remove(key); return true; } catch { FwpmTransactionAbort0(engine); throw; }
    }
    public void RemoveAll() { foreach (var k in blocks.Keys.ToList()) { try { Remove(k); } catch { } } }
    public void Dispose() { try { RemoveAll(); } catch { } if (engine != IntPtr.Zero) { FwpmEngineClose0(engine); engine = IntPtr.Zero; } }
}
