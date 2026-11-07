// The production file operations, updater and protocol are linked unchanged.
// Only the Windows collector/platform identity is substituted in this portable test runner.
namespace SurfaceInsight
{
    public static class MonitorHost
    {
        public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "si-core-" + Guid.NewGuid().ToString("N"));
        public static string StatusFile => Path.Combine(Folder, "collector.status");
    }
    public static class Platform
    {
        public const int MachX64 = 0x8664, MachArm64 = 0xaa64;
        public static string Version => "1.1.1";
    }
}
namespace SurfaceInsight.Network
{
    public static class ProcessIdentity
    {
        public static (int Key, Identity App) Resolve(int pid) => (pid, new Identity());
        public sealed record Identity { public bool Resolved => false; public string? Path => null; }
    }
}
