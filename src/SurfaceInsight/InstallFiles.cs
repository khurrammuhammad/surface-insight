using System.Security.Cryptography;
using System.Text.Json;

namespace SurfaceInsight;

/// <summary>A versioned inventory of application-owned files. Unknown files are never deleted.</summary>
public sealed record InstallManifest(string Application, int Schema, Dictionary<string, string> Files);

public static class InstallFiles
{
    public const string ManifestName = "surface-insight.files.json";
    public const string Application = "SurfaceInsight";
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Root(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(full) || Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full)
            throw new IOException("A drive root cannot be an installation folder.");
        NoLinks(full);
        return full;
    }

    public static void NoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            // LinkTarget also detects dangling links, which File.Exists does not.
            var info = new DirectoryInfo(p);
            if (info.LinkTarget != null || ((File.Exists(p) || Directory.Exists(p)) &&
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Linked/reparse paths are not supported for installation: " + p);
        }
    }

    public static string In(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || "._- ".Contains(c))) ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new IOException("Unsafe package path: " + relative);
        string path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Package path escapes its folder.");
        NoLinks(path);
        return path;
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static InstallManifest Read(string folder, bool verify)
    {
        string root = Root(folder);
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(In(root, ManifestName)))
            ?? throw new IOException("Missing package inventory.");
        if (manifest.Application != Application || manifest.Schema != 1 || manifest.Files == null ||
            manifest.Files.Count == 0 || manifest.Files.Count > 10_000 || !manifest.Files.ContainsKey("SurfaceInsight.exe"))
            throw new IOException("This is not a Surface Insight package inventory.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in manifest.Files)
        {
            string path = In(root, name);
            if (!names.Add(name.Replace('\\', '/')) || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new IOException("Invalid package inventory entry.");
            if (verify && (!File.Exists(path) || !Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Package inventory verification failed: " + name);
        }
        return manifest;
    }

    public static string Signal(string name, string? folder = null)
    {
        string path = Path.GetFullPath(folder ?? Updater.InstallDir).TrimEnd('\\', '/').ToUpperInvariant();
        string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..24];
        return "Local\\SurfaceInsight." + name + "." + id;
    }
}

/// <summary>
/// File-level replacement, with a unique durable recovery journal. Backups are retained until startup is acknowledged.
/// Does not rename or recursively delete an installation, its siblings, or unowned files.
/// </summary>
public sealed class UpdateTransaction
{
    public sealed record Entry(string Name, string? Before, string? After, string Backup);
    public sealed record Journal(string Application, string Target, string Token, List<Entry> Entries);
    public string Folder { get; }
    public string HealthFile => Path.Combine(Folder, "healthy");
    public Journal State { get; }

    UpdateTransaction(string folder, Journal state) { Folder = folder; State = state; }

    public static UpdateTransaction Prepare(string target, string staging, bool single, string work, string executableName = "SurfaceInsight.exe")
    {
        target = InstallFiles.Root(target);
        if (single && (Path.GetFileName(executableName) != executableName || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A single-file executable name must be a local .exe filename.");
        InstallFiles.NoLinks(staging);
        if (!File.Exists(InstallFiles.In(target, single ? executableName : "SurfaceInsight.exe")))
            throw new IOException("The target is not an existing Surface Insight installation.");
        var desired = single
            ? new Dictionary<string, string> { [executableName] = InstallFiles.Hash(staging) }
            : new Dictionary<string, string>(InstallFiles.Read(staging, true).Files, StringComparer.OrdinalIgnoreCase);
        if (!single) desired.Add(InstallFiles.ManifestName, InstallFiles.Hash(InstallFiles.In(staging, InstallFiles.ManifestName)));
        var previous = single ? new Dictionary<string, string> { [executableName] = InstallFiles.Hash(InstallFiles.In(target, executableName)) } : File.Exists(InstallFiles.In(target, InstallFiles.ManifestName))
            ? InstallFiles.Read(target, false).Files : throw new IOException("This installation predates safe file inventories. Install the new release into a new empty folder; history in LocalAppData is kept.");
        var entries = new List<Entry>();
        foreach (string name in desired.Keys.Union(previous.Keys, StringComparer.OrdinalIgnoreCase))
        {
            string dest = InstallFiles.In(target, name);
            string? before = File.Exists(dest) ? InstallFiles.Hash(dest) : null;
            if (desired.ContainsKey(name) && before != null && name != InstallFiles.ManifestName && !previous.ContainsKey(name))
                throw new IOException("Refusing to overwrite an unowned file: " + name);
            // Preserve locally modified obsolete files. Never remove them merely because a manifest named them.
            if (!desired.ContainsKey(name) && (before == null || !before.Equals(previous[name], StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(new Entry(name, before, desired.GetValueOrDefault(name), entries.Count.ToString("D5") + ".bak"));
        }
        work = InstallFiles.Root(work);
        Directory.CreateDirectory(work);
        string folder = Path.Combine(work, "transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var state = new Journal(InstallFiles.Application, target, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), entries);
        foreach (var e in entries.Where(e => e.Before != null))
            File.Copy(InstallFiles.In(target, e.Name), Path.Combine(folder, e.Backup));
        File.WriteAllText(Path.Combine(folder, "transaction.json"), JsonSerializer.Serialize(state));
        return new UpdateTransaction(folder, state);
    }

    public void Install(string staging, bool single)
    {
        foreach (var e in State.Entries)
        {
            string dest = InstallFiles.In(State.Target, e.Name);
            string? current = File.Exists(dest) ? InstallFiles.Hash(dest) : null;
            if (current != e.Before) throw new IOException("A file changed during update: " + e.Name);
            if (e.After == null) { File.Delete(dest); continue; }
            string source = single ? staging : InstallFiles.In(InstallFiles.Root(staging), e.Name);
            if (InstallFiles.Hash(source) != e.After) throw new IOException("Staging changed during update: " + e.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string temp = dest + ".si-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp, false);
                if (InstallFiles.Hash(temp) != e.After) throw new IOException("Replacement verification failed.");
                InstallFiles.NoLinks(dest);
                File.Move(temp, dest, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public void Rollback()
    {
        foreach (var e in State.Entries.AsEnumerable().Reverse())
        {
            string dest = InstallFiles.In(State.Target, e.Name);
            string? current = File.Exists(dest) ? InstallFiles.Hash(dest) : null;
            if (current == e.Before) continue;
            if (current != e.After) throw new IOException("Recovery stopped to preserve a subsequently changed file: " + dest);
            if (e.Before == null) File.Delete(dest);
            else
            {
                string backup = InstallFiles.In(Folder, e.Backup);
                if (InstallFiles.Hash(backup) != e.Before) throw new IOException("Recovery backup verification failed.");
                File.Copy(backup, dest, true);
            }
        }
    }

    public void Complete()
    {
        // Only remove the exact files we created. Unknown files keep the transaction directory in place.
        foreach (var e in State.Entries.Where(e => e.Before != null)) File.Delete(InstallFiles.In(Folder, e.Backup));
        File.Delete(HealthFile);
        File.Delete(Path.Combine(Folder, "transaction.json"));
        if (!Directory.EnumerateFileSystemEntries(Folder).Any()) Directory.Delete(Folder);
    }
}
