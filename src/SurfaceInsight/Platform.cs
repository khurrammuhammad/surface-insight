using System.Reflection;
using System.Runtime.InteropServices;
namespace SurfaceInsight;

/// <summary>Which build is running and on what hardware. The app ships as native x64 and native Arm64; an x64 build can also run on Arm64 Windows through emulation, which works but costs battery.</summary>
public static class Platform
{
    public static string Version => typeof(Platform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";
    public static bool Emulated => Emulated_(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
    public static bool Emulated_(Architecture process, Architecture os) => process != os;
    public static string Describe() => Describe(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
    public static string Describe(Architecture process, Architecture os) => $"Version {Version} · {process} build" + (process == os ? " (native)" : $" (emulated on {os})");
    public static string Advice => "This is the " + RuntimeInformation.ProcessArchitecture + " build running under emulation on " + RuntimeInformation.OSArchitecture + " Windows. It works, but the native Arm64 download is faster and uses less battery.";

    // ---- Per-process architecture (IMAGE_FILE_MACHINE values) ----
    public const int MachX86 = 0x014C, MachArm32 = 0x01C4, MachX64 = 0x8664, MachArm64 = 0xAA64;
    [StructLayout(LayoutKind.Sequential)] struct ProcessMachineInfo { public ushort ProcessMachine, Res0; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessInformation(IntPtr h, int cls, out ProcessMachineInfo info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsWow64Process2(IntPtr h, out ushort processMachine, out ushort nativeMachine);

    /// <summary>The CPU architecture a running process was built for, or null when Windows will not tell us (protected or exited processes).</summary>
    public static int? ProcessMachine(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid); if (h == IntPtr.Zero) return null;
            try { if (GetProcessInformation(h, 9 /* ProcessMachineTypeInfo, Windows 11 */, out var mi, Marshal.SizeOf<ProcessMachineInfo>()) && mi.ProcessMachine != 0) return mi.ProcessMachine; } catch (EntryPointNotFoundException) { }
            if (IsWow64Process2(h, out var pm, out var nm)) { if (pm != 0) return pm; if (nm != 0) return nm; }
        }
        catch { }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
        return null;
    }

    static int OsMachine(Architecture os) => os switch { Architecture.Arm64 => MachArm64, Architecture.X64 => MachX64, Architecture.X86 => MachX86, Architecture.Arm => MachArm32, _ => 0 };
    /// <summary>Short label for the Apps table. On Arm64 Windows, x64/x86 programs run through emulation, which costs speed and battery, so say so.</summary>
    public static string ArchLabel(int? mach) => ArchLabel(mach, RuntimeInformation.OSArchitecture);
    public static string ArchLabel(int? mach, Architecture os)
    {
        if (mach is not int m) return "—";
        string name = m switch { MachArm64 => "Arm64", MachX64 => "x64", MachX86 => "x86 (32-bit)", MachArm32 => "Arm (32-bit)", _ => "0x" + m.ToString("X4") };
        return IsEmulatedMachine(m, os) ? name + " · emulated" : name;
    }
    /// <summary>True when a process of this machine type is not native to the OS (Arm64 Windows running x64/x86, or x64 Windows running 32-bit x86 through WOW64 is NOT counted: that is cheap).</summary>
    public static bool IsEmulatedMachine(int? mach, Architecture os) => mach is int m && os == Architecture.Arm64 && (m == MachX64 || m == MachX86);
}
