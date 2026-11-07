using System.Runtime.InteropServices;
using System.Text.Json;
namespace SurfaceInsight;

/// <summary>Failure-focused tests. All writes are inside fresh disposable folders; no processes are started.</summary>
static class UpdaterTests
{
    public static void Run(Action<bool, string> check, Action<string>? skip = null)
    {
        check(Updater.IsNewer("v1.1.1", "1.1.0") && Updater.IsNewer("1.10.0", "1.9.0") && !Updater.IsNewer("1.2", "1.2.0") && !Updater.IsNewer("1.0.0", "1.1.0"), "Updater: numeric versions and new release offer");
        check(Updater.ParseVersion("latest") == null && Updater.ParseVersion("1.2-beta") == null && Updater.ParseVersion(null) == null, "Updater: rejects ambiguous versions");
        var assets = new[] { ("SurfaceInsight-win-arm64.exe", "arm"), ("SurfaceInsight-win-x64.zip", "x64") };
        check(Updater.PickAsset(assets, "arm64", true)?.Url == "arm" && Updater.PickAsset(assets, "arm64", false) == null && Updater.NativeArch(Architecture.X86) == null, "Updater: architecture and package choice");
        string hash = new('a', 64), file = "SurfaceInsight-win-x64.zip";
        check(Updater.ParseSums(hash + " *" + file, file) == hash, "Updater: sha256sum binary marker");
        check(Updater.ParseSums(hash + "  " + file + "\n" + hash + "  " + file, file) == null && Updater.ParseSums(new string('g', 64) + "  " + file, file) == null, "Updater: duplicate and nonhex checksums fail closed");
        check(Updater.ParseSums("", file) == null && Updater.ParseSums(hash + "  other.zip", file) == null, "Updater: missing checksums fail closed");
        string root = Path.Combine(Path.GetTempPath(), "si-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string work = Path.Combine(root, "work");
            (string Target, string Stage) Fixture(string name)
            {
                string target = Path.Combine(root, name), stage = Path.Combine(root, name + "-stage");
                Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(target, "SurfaceInsight.exe"), "old");
                File.WriteAllText(Path.Combine(stage, "SurfaceInsight.exe"), "new");
                File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");
                WriteManifest(target); WriteManifest(stage);
                return (target, stage);
            }
            var f = Fixture("success");
            Directory.CreateDirectory(f.Target + ".old");
            File.WriteAllText(Path.Combine(f.Target + ".old", "personal-backup.txt"), "keep");
            var tx = UpdateTransaction.Prepare(f.Target, f.Stage, false, work);
            tx.Install(f.Stage, false);
            check(File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "new" && File.ReadAllText(Path.Combine(f.Target, "personal.txt")) == "keep", "Update replaces inventoried files and preserves unrelated files");
            check(File.ReadAllText(Path.Combine(f.Target + ".old", "personal-backup.txt")) == "keep", "Pre-existing sibling .old directory is never touched");
            File.WriteAllText(Path.Combine(tx.Folder, "unknown.txt"), "keep"); tx.Complete();
            check(File.Exists(Path.Combine(tx.Folder, "unknown.txt")), "Transaction cleanup preserves unknown files");

            f = Fixture("rollback"); tx = UpdateTransaction.Prepare(f.Target, f.Stage, false, work); tx.Install(f.Stage, false); tx.Rollback(); tx.Complete();
            check(File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "old" && File.Exists(Path.Combine(f.Target, "personal.txt")), "Rollback restores prior files and preserves personal data");

            f = Fixture("changed-stage"); tx = UpdateTransaction.Prepare(f.Target, f.Stage, false, work);
            File.WriteAllText(Path.Combine(f.Stage, "SurfaceInsight.exe"), "tampered");
            check(Throws(() => tx.Install(f.Stage, false)) && File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "old", "Staging mutation is rejected before replacement"); tx.Rollback(); tx.Complete();

            f = Fixture("changed-after"); tx = UpdateTransaction.Prepare(f.Target, f.Stage, false, work); tx.Install(f.Stage, false);
            File.WriteAllText(Path.Combine(f.Target, "SurfaceInsight.exe"), "subsequent change");
            check(Throws(tx.Rollback) && File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "subsequent change" && File.Exists(Path.Combine(tx.Folder, "transaction.json")), "Recovery preserves subsequently modified files and retains its journal");

            f = Fixture("startup-failure");
            string? error = Updater.Apply(f.Target, f.Stage, false, false, _ => false, work);
            check(error != null && File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "old", "A failed health check rolls back the replacement");

            f = Fixture("unknown-collision"); File.WriteAllText(Path.Combine(f.Stage, "personal.txt"), "replacement");
            WriteManifest(f.Stage, "personal.txt");
            check(Throws(() => UpdateTransaction.Prepare(f.Target, f.Stage, false, work)) && File.ReadAllText(Path.Combine(f.Target, "personal.txt")) == "keep", "New package cannot overwrite a previously unowned file");

            f = Fixture("legacy"); File.Delete(Path.Combine(f.Target, InstallFiles.ManifestName));
            check(Throws(() => UpdateTransaction.Prepare(f.Target, f.Stage, false, work)), "Uninventoried ZIP installs require an explicit safe migration");

            f = Fixture("single"); tx = UpdateTransaction.Prepare(f.Target, Path.Combine(f.Stage, "SurfaceInsight.exe"), true, work);
            tx.Install(Path.Combine(f.Stage, "SurfaceInsight.exe"), true); tx.Rollback(); tx.Complete();
            check(File.ReadAllText(Path.Combine(f.Target, "SurfaceInsight.exe")) == "old" && File.Exists(Path.Combine(f.Target, "personal.txt")), "Single-file replacement and rollback preserve neighbouring files");

            f = Fixture("renamed-single");
            File.Move(Path.Combine(f.Target, "SurfaceInsight.exe"), Path.Combine(f.Target, "My Monitor.exe"));
            tx = UpdateTransaction.Prepare(f.Target, Path.Combine(f.Stage, "SurfaceInsight.exe"), true, work, "My Monitor.exe");
            tx.Install(Path.Combine(f.Stage, "SurfaceInsight.exe"), true); tx.Rollback(); tx.Complete();
            check(File.ReadAllText(Path.Combine(f.Target, "My Monitor.exe")) == "old" && File.Exists(Path.Combine(f.Target, "personal.txt")), "Downloaded or renamed single-file executables keep their actual filename through replacement and rollback");

            foreach (string path in new[] { "../outside", "/absolute", "C:/absolute", "Assets/../../outside", "con.txt", "Assets/file:stream", "trailing. ", "Assets//file" })
                check(Throws(() => InstallFiles.In(root, path)), "Unsafe inventory path rejected: " + path);
            string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            string link = Path.Combine(root, "linked");
            try { Directory.CreateSymbolicLink(link, outside); check(Throws(() => InstallFiles.In(root, "linked/file")), "Directory symlink/reparse traversal is rejected"); }
            catch (UnauthorizedAccessException) { skip?.Invoke("Symlink creation requires link privilege/developer mode; Windows junction acceptance remains required."); }
            string download = Path.Combine(work, "download-cleanup-test"); Directory.CreateDirectory(download);
            File.WriteAllText(Path.Combine(download, "owned.bin"), "owned");
            File.WriteAllText(Path.Combine(download, "personal.txt"), "keep");
            var downloadRecord = new Updater.DownloadRecord(InstallFiles.Application, f.Target, new() { ["owned.bin"] = InstallFiles.Hash(Path.Combine(download, "owned.bin")) }, false);
            File.WriteAllText(Path.Combine(download, "download.json"), JsonSerializer.Serialize(downloadRecord));
            Updater.CleanupDownloads(work, f.Target);
            check(File.Exists(Path.Combine(download, "owned.bin")), "Incomplete download is retained for recovery");
            File.WriteAllText(Path.Combine(download, "download.json"), JsonSerializer.Serialize(downloadRecord with { Completed = true }));
            Updater.CleanupDownloads(work, f.Target);
            check(!File.Exists(Path.Combine(download, "owned.bin")) && File.Exists(Path.Combine(download, "personal.txt")), "Completed download cleanup removes only inventoried unchanged files");
            check(File.Exists(Path.Combine(root, "success.old", "personal-backup.txt")), "Safe cleanup never visits sibling .old directories");
            File.WriteAllText(Path.Combine(root, "abc"), "abc");
            check(Updater.Sha256(Path.Combine(root, "abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" && Updater.PeMachine(Path.Combine(root, "abc")) == null, "Hash implementation and malformed PE handling");
        }
        finally { Directory.Delete(root, true); }
    }
    static bool Throws(Action action) { try { action(); return false; } catch (IOException) { return true; } }
    static void WriteManifest(string folder, params string[] extras)
    {
        var files = new[] { "SurfaceInsight.exe" }.Concat(extras).ToDictionary(x => x, x => InstallFiles.Hash(Path.Combine(folder, x)));
        File.WriteAllText(Path.Combine(folder, InstallFiles.ManifestName), JsonSerializer.Serialize(new InstallManifest(InstallFiles.Application, 1, files)));
    }
}
