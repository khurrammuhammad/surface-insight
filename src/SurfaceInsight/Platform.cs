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
}
