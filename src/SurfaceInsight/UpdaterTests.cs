using System.Runtime.InteropServices;
using System.Text.Json;
namespace SurfaceInsight;

/// <summary>Self-test checks for the updater. The swap tests use throw-away folders and never start or stop real processes.</summary>
static class UpdaterTests
{
    public static void Run(Action<bool, string> check)
    {
        // Versions: numeric comparison, tolerant of a leading v, and anything odd never counts as an update.
        check(Updater.IsNewer("v1.1.0", "1.0.0") && Updater.IsNewer("1.10.0", "1.9.0") && Updater.IsNewer("v2.0", "1.9.9") && !Updater.IsNewer("v1.1.0", "1.1.0") && !Updater.IsNewer("1.2", "1.2.0") && !Updater.IsNewer("v1.0.0", "1.1.0"), "Updater: version comparison");
        check(Updater.ParseVersion("latest") == null && Updater.ParseVersion("1.2.0-beta") == null && Updater.ParseVersion("") == null && Updater.ParseVersion(null) == null && !Updater.IsNewer("nightly", "1.0.0"), "Updater: malformed and pre-release tags are never updates");

        // Asset choice: stable names and the older versioned zip names, per architecture and install type.
        var assets = new[] { ("SHA256SUMS.txt", "u0"), ("SurfaceInsight-win-arm64.exe", "u1"), ("SurfaceInsight-win-arm64.zip", "u2"), ("SurfaceInsight-win-x64.exe", "u3"), ("SurfaceInsight-win-x64.zip", "u4") };
        check(Updater.PickAsset(assets, "x64", false)?.Url == "u4" && Updater.PickAsset(assets, "x64", true)?.Url == "u3" && Updater.PickAsset(assets, "arm64", false)?.Url == "u2" && Updater.PickAsset(assets, "arm64", true)?.Url == "u1", "Updater: picks the zip or single-file exe for this architecture");
        check(Updater.PickAsset(new[] { ("SurfaceInsight-1.0.0-win-x64.zip", "old") }, "x64", false)?.Url == "old" && Updater.PickAsset(new[] { ("SurfaceInsight-1.0.0-win-x64.zip", "old") }, "arm64", false) == null && Updater.PickAsset(new[] { ("notes.txt", "n") }, "x64", false) == null, "Updater: accepts versioned zip names, never the wrong architecture");
        check(Updater.NativeArch(Architecture.X64) == "x64" && Updater.NativeArch(Architecture.Arm64) == "arm64" && Updater.NativeArch(Architecture.X86) == null, "Updater: native architecture names");

        // Checksums.
        string h1 = new string('a', 64), h2 = new string('b', 64);
        string sums = h1 + "  SurfaceInsight-win-x64.zip\r\n" + h2.ToUpperInvariant() + " *SurfaceInsight-win-arm64.zip\r\nnot a hash line\r\n";
        check(Updater.ParseSums(sums, "SurfaceInsight-win-x64.zip") == h1 && Updater.ParseSums(sums, "SurfaceInsight-win-arm64.zip") == h2 && Updater.ParseSums(sums, "missing.zip") == null && Updater.ParseSums("", "x") == null, "Updater: SHA256SUMS parsing");

        string dir = Path.Combine(Path.GetTempPath(), "si-upd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string abc = Path.Combine(dir, "abc.bin"); File.WriteAllText(abc, "abc"); check(Updater.Sha256(abc) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Updater: SHA-256 of a file");
            check(Updater.PeMachine(abc) == null && Updater.PeMachine(Path.Combine(dir, "nope.exe")) == null, "Updater: non-executables have no machine type");
            if (Environment.ProcessPath is string self && Path.GetFileNameWithoutExtension(self).Equals("SurfaceInsight", StringComparison.OrdinalIgnoreCase)) check(Updater.PeMachine(self) == (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Platform.MachArm64 : Platform.MachX64), "Updater: reads this executable's machine type");

            // Release JSON -> result.
            string Json(string tag, params string[] names) => JsonSerializer.Serialize(new { tag_name = tag, body = "Notes here", html_url = "https://example.test/rel", assets = names.Select(n => new { name = n, browser_download_url = "https://example.test/" + n }).ToArray() });
            using (var newer = JsonDocument.Parse(Json("v9.9.9", "SHA256SUMS.txt", "SurfaceInsight-win-x64.zip")))
            {
                var r = Updater.Evaluate(newer.RootElement, "1.0.0", "x64", false);
                check(r.Ok && r.Update is { AssetName: "SurfaceInsight-win-x64.zip", SumsUrl: not null, Notes: "Notes here" } && r.Update.Version == new Version(9, 9, 9), "Updater: a newer release yields its download, checksum file and notes");
                var same = Updater.Evaluate(newer.RootElement, "9.9.9", "x64", false); check(same.Ok && same.Update == null, "Updater: same version means up to date");
                var wrong = Updater.Evaluate(newer.RootElement, "1.0.0", "arm64", false); check(!wrong.Ok && wrong.Update == null, "Updater: a release without this architecture is reported, not installed");
            }
            using (var nosums = JsonDocument.Parse(Json("v9.9.9", "SurfaceInsight-win-x64.zip"))) check(Updater.Evaluate(nosums.RootElement, "1.0.0", "x64", false).Update?.SumsUrl == null, "Updater: a release without SHA256SUMS.txt has no checksum URL (the download is then refused)");

            // Install type and writability.
            string probe = Path.Combine(dir, "probe"); Directory.CreateDirectory(probe); check(Updater.IsSingleFile(probe), "Updater: no coreclr.dll means single-file"); File.WriteAllText(Path.Combine(probe, "coreclr.dll"), ""); check(!Updater.IsSingleFile(probe) && Updater.CanReplace(probe, out _), "Updater: coreclr.dll means a zip install; temp folder is replaceable");

            // Full swap, zip install: old files replaced, previous install kept as .old, nested folders copied.
            string inst = Path.Combine(dir, "zip", "App"), stg = Path.Combine(dir, "stage-zip"); Directory.CreateDirectory(inst); Directory.CreateDirectory(Path.Combine(stg, "Assets"));
            File.WriteAllText(Path.Combine(inst, "SurfaceInsight.exe"), "old"); File.WriteAllText(Path.Combine(inst, "gone.txt"), "x"); File.WriteAllText(Path.Combine(stg, "SurfaceInsight.exe"), "new"); File.WriteAllText(Path.Combine(stg, "Assets", "map.json"), "m");
            string? err = Updater.Apply(inst, stg, false, false);
            check(err == null && File.ReadAllText(Path.Combine(inst, "SurfaceInsight.exe")) == "new" && File.Exists(Path.Combine(inst, "Assets", "map.json")) && !File.Exists(Path.Combine(inst, "gone.txt")) && File.ReadAllText(Path.Combine(inst + ".old", "SurfaceInsight.exe")) == "old", "Updater: zip update replaces the install and keeps the old one aside");

            // Rollback, zip install: staging is missing, so the original must come back untouched.
            string inst2 = Path.Combine(dir, "zip2", "App"); Directory.CreateDirectory(inst2); File.WriteAllText(Path.Combine(inst2, "SurfaceInsight.exe"), "keep"); File.WriteAllText(Path.Combine(inst2, "data.txt"), "d");
            err = Updater.Apply(inst2, Path.Combine(dir, "does-not-exist"), false, false);
            check(err != null && File.ReadAllText(Path.Combine(inst2, "SurfaceInsight.exe")) == "keep" && File.Exists(Path.Combine(inst2, "data.txt")) && !Directory.Exists(inst2 + ".old"), "Updater: a failed zip update restores the previous install");

            // Single-file install: swap, then rollback.
            string one = Path.Combine(dir, "one"); Directory.CreateDirectory(one); string exe = Path.Combine(one, "SurfaceInsight.exe"), staged = Path.Combine(dir, "staged.exe"); File.WriteAllText(exe, "old"); File.WriteAllText(staged, "new");
            err = Updater.Apply(one, staged, true, false); check(err == null && File.ReadAllText(exe) == "new" && File.ReadAllText(exe + ".old") == "old", "Updater: single-file update swaps the exe and keeps the old one aside");
            File.WriteAllText(exe, "keep"); File.Delete(exe + ".old"); err = Updater.Apply(one, Path.Combine(dir, "missing.exe"), true, false);
            check(err != null && File.ReadAllText(exe) == "keep" && !File.Exists(exe + ".old"), "Updater: a failed single-file update restores the previous exe");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
