using System.Runtime.InteropServices;
namespace SurfaceInsight;
/// <summary>GPU and NPU engine utilization from one PDH read.</summary>
/// <param name="Npu">Null when the device has no NPU (or Windows does not expose one).</param>
/// <param name="NpuTotal">Busiest NPU engine, summed across processes.</param>
public sealed record AcceleratorReading(Dictionary<int, double> Gpu, Dictionary<int, double>? Npu, double? NpuTotal);

// One PDH query shares a collection timestamp across all GPU instances.
// Windows reports NPUs through the same "GPU Engine" counters, on a compute-only adapter; those engines are split out as NPU.
public sealed class GpuQuery : IDisposable
{
    IntPtr query, counter; readonly HashSet<string> npuLuids;
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    [StructLayout(LayoutKind.Explicit, Size = 16)] struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [StructLayout(LayoutKind.Sequential)] struct Item { public IntPtr Name; public Value Value; }
    public GpuQuery() { npuLuids = NpuAdapters(); if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 || PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0) Dispose(); if (query != IntPtr.Zero) PdhCollectQueryData(query); }
    public bool HasNpu => npuLuids.Count > 0;
    public AcceleratorReading? Read()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return null;
        uint bytes = 0; PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out _, IntPtr.Zero);
        if (bytes == 0 || bytes > 16 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out var count, buffer) != 0) return null;
            var items = new List<(string, double)>(); int size = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * size); if (item.Value.Status > 1 || !double.IsFinite(item.Value.Number)) continue;
                items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Number));
            }
            return Split(items, npuLuids);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Splits engine instances ("pid_1_luid_0x0_0x119F2_phys_0_eng_0_engtype_Compute") into per-process GPU and NPU utilization.</summary>
    public static AcceleratorReading Split(IEnumerable<(string Name, double Value)> items, IReadOnlySet<string> npuLuids)
    {
        var gpu = new Dictionary<int, double>(); var npu = npuLuids.Count > 0 ? new Dictionary<int, double>() : null; var npuEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in items)
        {
            if (!name.StartsWith("pid_") || !int.TryParse(name.AsSpan(4, Math.Max(0, name.IndexOf('_', 4) - 4)), out int pid)) continue;
            double v = Math.Clamp(raw, 0, 100); int l = name.IndexOf("_luid_", StringComparison.Ordinal), p = name.IndexOf("_phys_", StringComparison.Ordinal);
            string luid = l > 0 && p > l ? name[(l + 6)..p] : "";
            if (npu != null && npuLuids.Contains(luid))
            {
                npu[pid] = Math.Max(npu.GetValueOrDefault(pid), v);
                string engine = name[(l + 6)..]; npuEngine[engine] = npuEngine.GetValueOrDefault(engine) + v;
                continue;
            }
            if (!(name.Contains("engtype_3D") || name.Contains("engtype_Compute") || name.Contains("engtype_Video"))) continue;
            gpu[pid] = Math.Max(gpu.GetValueOrDefault(pid), v);
        }
        return new(gpu, npu, npu == null ? null : Math.Min(100, npuEngine.Values.DefaultIfEmpty(0).Max()));
    }

    // ---- NPU adapter discovery: compute-only, non-software adapters (D3DKMT_ADAPTERTYPE.ComputeOnly) ----
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint Handle; public Luid Luid; public uint Sources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] struct EnumAdapters3 { public ulong Filter; public uint Count; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Handle; }
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters3(ref EnumAdapters3 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryInfo q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter c);
    const int AdapterTypeQuery = 15; const uint ComputeOnly = 1u << 11, Software = 1u << 2;

    /// <summary>LUID text as it appears in counter instance names ("0x00000000_0x000119F2"), upper-case hex.</summary>
    static HashSet<string> NpuAdapters()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var e = new EnumAdapters3 { Filter = 1 }; // IncludeComputeOnly
            if (D3DKMTEnumAdapters3(ref e) != 0 || e.Count == 0 || e.Count > 64) return set;
            int size = Marshal.SizeOf<AdapterInfo>(); e.Adapters = Marshal.AllocHGlobal(size * (int)e.Count); var flags = Marshal.AllocHGlobal(4);
            try
            {
                if (D3DKMTEnumAdapters3(ref e) != 0) return set;
                for (int i = 0; i < e.Count; i++)
                {
                    var a = Marshal.PtrToStructure<AdapterInfo>(e.Adapters + i * size);
                    var q = new QueryInfo { Handle = a.Handle, Type = AdapterTypeQuery, Data = flags, Size = 4 };
                    if (D3DKMTQueryAdapterInfo(ref q) == 0) { uint f = (uint)Marshal.ReadInt32(flags); if ((f & ComputeOnly) != 0 && (f & Software) == 0) set.Add($"0x{a.Luid.High:X8}_0x{a.Luid.Low:X8}"); }
                    var c = new CloseAdapter { Handle = a.Handle }; D3DKMTCloseAdapter(ref c);
                }
            }
            finally { Marshal.FreeHGlobal(e.Adapters); Marshal.FreeHGlobal(flags); }
        }
        catch { } // D3DKMTEnumAdapters3 needs Windows 11; older systems simply report no NPU
        return set;
    }

    static string? npuName; static bool npuNameRead;
    /// <summary>Friendly NPU name from Device Manager ("Intel(R) NPU"), or null when none is present.</summary>
    public static string? NpuName()
    {
        if (npuNameRead) return npuName; npuNameRead = true;
        try { using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass='ComputeAccelerator'"); using var r = s.Get(); foreach (System.Management.ManagementObject o in r) using (o) { npuName = o["Name"] as string; if (npuName != null) break; } } catch { }
        return npuName;
    }
    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
}
