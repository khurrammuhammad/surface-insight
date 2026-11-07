using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace SurfaceInsight;

/// <summary>A newer release that can be installed: where to get it, and the checksum file that must verify it.</summary>
public sealed record UpdateInfo(string Tag, Version Version, string Notes, string PageUrl, string AssetName, string AssetUrl, string? SumsUrl);
public sealed record CheckResult(bool Ok, UpdateInfo? Update, string Message);

/// <summary>
/// Checks GitHub for a newer release and installs it. Nothing here runs unless the user presses "Check for updates" or has turned on
/// the daily check. Downloads are only applied after their SHA-256 matches the release's SHA256SUMS.txt.
/// The swap itself (<see cref="Apply"/>) runs from the NEW build in a separate process, so fixes to it ship with the update.
/// </summary>
public static class Updater
{
    public const string Repo = "khurrammuhammad/surface-insight";
    public static string QuitEvent => InstallFiles.Signal("Quit");
    public static string StateFile => Path.Combine(MonitorHost.Folder, "update.json");
    public static string AutoFlag => Path.Combine(MonitorHost.Folder, "update.auto");
    public static string ResultFile => Path.Combine(MonitorHost.Folder, "update.result");
    public static string LogFile => Path.Combine(MonitorHost.Folder, "update.log");
    public static string OpenSettingsFlag => Path.Combine(MonitorHost.Folder, "open.settings");
    static string WorkDir => Path.Combine(MonitorHost.Folder, "updates");
    public sealed record DownloadRecord(string Application, string Target, Dictionary<string, string> Files, bool Completed);
    public static CheckResult? Last;

    public static bool AutoEnabled => File.Exists(AutoFlag);
    public static void SetAuto(bool on) { try { if (on) { Directory.CreateDirectory(MonitorHost.Folder); File.WriteAllText(AutoFlag, "on"); } else File.Delete(AutoFlag); } catch { } }

    // ------------------------------------------------------------------ pure helpers (covered by --self-test)
    /// <summary>Parses "v1.2.0" or "1.2.0". Pre-release tags ("1.2.0-beta") and anything else are rejected, so a stray tag never triggers an update.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null; var t = tag.Trim(); if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        return t.All(c => char.IsDigit(c) || c == '.') && Version.TryParse(t, out var v) ? v : null;
    }
    public static bool IsNewer(string? latestTag, string currentVersion) => ParseVersion(latestTag) is Version l && ParseVersion(currentVersion) is Version c && Normalize(l) > Normalize(c);
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>"x64" or "arm64" for the PC itself (not for this process, so an emulated x64 build is moved to the native build).</summary>
    public static string? NativeArch(Architecture os) => os switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };

    /// <summary>A zip install has coreclr.dll beside the exe; a single-file build does not.</summary>
    public static bool IsSingleFile(string installDir) => !File.Exists(Path.Combine(installDir, "coreclr.dll"));

    /// <summary>Chooses the download for this PC and install type. Accepts the stable names and the older versioned zip names.</summary>
    public static (string Name, string Url)? PickAsset(IEnumerable<(string Name, string Url)> assets, string arch, bool singleFile)
    {
        string ext = singleFile ? "exe" : "zip"; var rx = new System.Text.RegularExpressions.Regex("^SurfaceInsight-(.+-)?win-" + arch + "\\." + ext + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = assets.Where(a => rx.IsMatch(a.Name)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Reads a sha256sum-style file ("hash  name") and returns the lower-case hash for the named file.</summary>
    public static string? ParseSums(string text, string file)
    {
        string? result = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int i = line.IndexOfAny(new[] { ' ', '\t' });
            if (i < 0) continue;
            var name = line[i..].Trim().TrimStart('*');
            if (!name.Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = line[..i];
            if (result != null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
            result = hash.ToLowerInvariant();
        }
        return result;
    }
    public static string Sha256(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

    /// <summary>The PE "machine" field of an executable (0x8664 x64, 0xAA64 Arm64), or null if it is not a PE file.</summary>
    public static int? PeMachine(string exe)
    {
        try { using var f = File.OpenRead(exe); var b = new byte[0x40]; if (f.Read(b, 0, b.Length) < b.Length || b[0] != 'M' || b[1] != 'Z') return null; int pe = BitConverter.ToInt32(b, 0x3C); f.Position = pe; var h = new byte[6]; if (f.Read(h, 0, 6) < 6 || h[0] != 'P' || h[1] != 'E') return null; return BitConverter.ToUInt16(h, 4); } catch { return null; }
    }
    public static int MachineFor(string arch) => arch == "arm64" ? Platform.MachArm64 : Platform.MachX64;

    public static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>True when the folder the app runs from can be renamed and recreated (not Program Files, not read-only).</summary>
    public static bool CanReplace(string installDir, out string why)
    {
        why = ""; try
        {
            installDir = InstallFiles.Root(installDir);
            if (!IsSingleFile(installDir)) InstallFiles.Read(installDir, false);
            var parent = Path.GetDirectoryName(installDir.TrimEnd('\\', '/')); if (string.IsNullOrEmpty(parent)) { why = "The app is at the root of a drive."; return false; }
            var probe = Path.Combine(parent, ".si-write-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(probe); Directory.Delete(probe);
            var f = Path.Combine(installDir, ".si-write-test-" + Guid.NewGuid().ToString("N")); File.WriteAllText(f, ""); File.Delete(f); return true;
        }
        catch (Exception ex) { why = "This folder cannot be safely updated in place: " + ex.Message + " Install into a new dedicated folder instead."; return false; }
    }

    // ------------------------------------------------------------------ network
    static HttpClient NewClient(TimeSpan timeout) { var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = timeout }; c.DefaultRequestHeaders.UserAgent.ParseAdd("SurfaceInsight/" + Platform.Version); c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json"); return c; }

    /// <summary>Asks GitHub for the latest release. The environment variable SURFACEINSIGHT_UPDATE_TAG reads a specific tag instead (used to test an update without publishing "latest").</summary>
    public static async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string? pin = Environment.GetEnvironmentVariable("SURFACEINSIGHT_UPDATE_TAG");
            string url = "https://api.github.com/repos/" + Repo + "/releases/" + (string.IsNullOrEmpty(pin) ? "latest" : "tags/" + Uri.EscapeDataString(pin));
            using var http = NewClient(TimeSpan.FromSeconds(15)); using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return Done(new(false, null, "No release was found on GitHub yet."));
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return Done(new(false, null, "GitHub is limiting requests from this network right now. Try again later."));
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); return Done(Evaluate(doc.RootElement, Platform.Version, NativeArch(RuntimeInformation.OSArchitecture), IsSingleFile(InstallDir)));
        }
        catch (OperationCanceledException) { return Done(new(false, null, "Could not reach GitHub (timed out).")); }
        catch (HttpRequestException) { return Done(new(false, null, "Could not reach GitHub. Check your internet connection.")); }
        catch (Exception ex) { return Done(new(false, null, "Update check failed: " + ex.Message)); }
    }
    static CheckResult Done(CheckResult r) { Last = r; try { WriteState(r); } catch { } return r; }

    /// <summary>Turns a GitHub release JSON into a result. Separate from the network call so it can be tested.</summary>
    public static CheckResult Evaluate(JsonElement rel, string current, string? arch, bool singleFile)
    {
        string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : ""; var ver = ParseVersion(tag);
        if (ver == null) return new(false, null, "The latest release has an unrecognised version (" + tag + ").");
        if (!IsNewer(tag, current)) return new(true, null, "You're up to date (version " + current + ").");
        if (arch == null) return new(false, null, "Version " + ver + " is available, but automatic updates support only x64 and Arm64 Windows.");
        var assets = new List<(string Name, string Url)>(); if (rel.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array) foreach (var a in arr.EnumerateArray()) assets.Add((a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? ""));
        string page = rel.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "https://github.com/" + Repo + "/releases"; string notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var pick = PickAsset(assets, arch, singleFile); if (pick == null) return new(false, null, "Version " + ver + " is available, but it has no " + arch + " " + (singleFile ? "single-file" : "zip") + " download. See " + page);
        var sums = assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).Url;
        return new(true, new UpdateInfo(tag, ver, notes, page, pick.Value.Name, pick.Value.Url, string.IsNullOrEmpty(sums) ? null : sums), "Version " + ver + " is available (you have " + current + ").");
    }

    // ------------------------------------------------------------------ saved state (shared by the tray monitor and the dashboard)
    public static (DateTime? Checked, string Latest, string Notified) ReadState()
    {
        DateTime? at = null; string latest = "", notified = "";
        try { foreach (var l in File.ReadAllLines(StateFile)) { var kv = l.Split('=', 2); if (kv.Length != 2) continue; if (kv[0] == "checked" && DateTime.TryParse(kv[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) at = d.ToUniversalTime(); else if (kv[0] == "latest") latest = kv[1]; else if (kv[0] == "notified") notified = kv[1]; } } catch { }
        return (at, latest, notified);
    }
    static void WriteState(CheckResult r) { var s = ReadState(); Directory.CreateDirectory(MonitorHost.Folder); File.WriteAllLines(StateFile, new[] { "checked=" + DateTime.UtcNow.ToString("O"), "latest=" + (r.Update?.Tag ?? (r.Ok ? "" : s.Latest)), "notified=" + s.Notified }); }
    public static void MarkNotified(string tag) { var s = ReadState(); try { File.WriteAllLines(StateFile, new[] { "checked=" + (s.Checked ?? DateTime.UtcNow).ToString("O"), "latest=" + s.Latest, "notified=" + tag }); } catch { } }
    /// <summary>The line shown in Settings before any check this session.</summary>
    public static string StatusText()
    {
        if (Last != null) return Last.Message; var s = ReadState();
        if (s.Checked is DateTime at) return (IsNewer(s.Latest, Platform.Version) ? "Version " + ParseVersion(s.Latest) + " is available. " : "Up to date as of the last check. ") + "Last checked " + at.ToLocalTime().ToString("g") + ".";
        return "Not checked yet. Nothing is sent to GitHub unless you press the button" + (AutoEnabled ? " or the daily check runs." : ".");
    }

    // ------------------------------------------------------------------ download and stage
    /// <summary>Downloads the release file, verifies its checksum, and for zip installs extracts and checks it. Returns what <see cref="StartApply"/> needs.</summary>
    public static async Task<(string Applier, string Staging, bool SingleFile)> PrepareAsync(UpdateInfo u, IProgress<string>? progress, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        ct = deadline.Token;
        if (u.SumsUrl == null) throw new InvalidOperationException("This release has no SHA256SUMS.txt, so the download can't be verified. Download it from the release page instead.");
        string arch = NativeArch(RuntimeInformation.OSArchitecture) ?? throw new InvalidOperationException("Unsupported processor."); bool single = IsSingleFile(InstallDir);
        string dir = Path.Combine(InstallFiles.Root(WorkDir), "download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Path.GetFileName(u.AssetName));
        using (var http = NewClient(Timeout.InfiniteTimeSpan))
        {
            progress?.Report("Reading checksums…"); string sums = await http.GetStringAsync(u.SumsUrl, ct); string? want = ParseSums(sums, u.AssetName) ?? throw new InvalidOperationException("SHA256SUMS.txt does not list " + u.AssetName + ".");
            using var resp = await http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct); resp.EnsureSuccessStatusCode(); long total = resp.Content.Headers.ContentLength ?? 0;
            using (var src = await resp.Content.ReadAsStreamAsync(ct)) using (var dst = File.Create(file)) { var buf = new byte[1 << 20]; long done = 0; int n; var last = DateTime.MinValue; while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if ((DateTime.UtcNow - last).TotalMilliseconds > 400) { last = DateTime.UtcNow; progress?.Report(total > 0 ? $"Downloading… {done / 1048576} of {total / 1048576} MB" : $"Downloading… {done / 1048576} MB"); } } }
            progress?.Report("Verifying…"); string got = await Task.Run(() => Sha256(file), ct); if (got != want) { try { File.Delete(file); } catch { } throw new InvalidOperationException("The download doesn't match its published checksum, so it was discarded. Nothing was changed."); }
        }
        string applier, staging;
        if (single) { applier = staging = file; }
        else
        {
            progress?.Report("Unpacking…"); staging = Path.Combine(dir, "pkg"); await Task.Run(() => ZipFile.ExtractToDirectory(file, staging), ct); applier = Path.Combine(staging, "SurfaceInsight.exe");
        }
        if (!single) InstallFiles.Read(staging, true);
        if (!File.Exists(applier)) throw new InvalidOperationException("The package doesn't contain SurfaceInsight.exe. Nothing was changed.");
        if (PeMachine(applier) != MachineFor(arch)) throw new InvalidOperationException("The package is not built for this PC's processor. Nothing was changed.");
        var owned = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), InstallFiles.Hash);
        File.WriteAllText(Path.Combine(dir, "download.json"), JsonSerializer.Serialize(new DownloadRecord(InstallFiles.Application, InstallDir, owned, false)));
        return (applier, staging, single);
    }

    /// <summary>Starts the new build in apply mode. The caller must then close itself. The argument order is a fixed contract between versions.</summary>
    public static void StartApply((string Applier, string Staging, bool SingleFile) p)
    {
        var psi = new ProcessStartInfo(p.Applier) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(p.Applier)! };
        psi.ArgumentList.Add("--apply-update"); psi.ArgumentList.Add(InstallDir); psi.ArgumentList.Add(p.Staging); psi.ArgumentList.Add(p.SingleFile ? "1" : "0");
        psi.ArgumentList.Add(Path.GetFileName(Environment.ProcessPath) ?? "SurfaceInsight.exe");
        Process.Start(psi)?.Dispose();
    }

    // ------------------------------------------------------------------ apply (runs in the new build, no UI)
    static void Log(string text) { try { Directory.CreateDirectory(MonitorHost.Folder); File.AppendAllText(LogFile, DateTime.Now.ToString("s") + "  " + text + Environment.NewLine); } catch { } }

    static List<Process> Others(string installDir, string executableName)
    {
        var l = new List<Process>(); string root = Path.GetFullPath(installDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName))) { try { if (p.Id != Environment.ProcessId && p.MainModule?.FileName is string f && Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { l.Add(p); continue; } } catch { } p.Dispose(); }
        return l;
    }
    static void StopOld(string installDir, string executableName)
    {
        try { using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstallFiles.Signal("Quit", installDir)); quit.Set(); } catch { }
        var end = DateTime.UtcNow.AddSeconds(20); List<Process> o;
        while ((o = Others(installDir, executableName)).Count > 0 && DateTime.UtcNow < end) { foreach (var p in o) p.Dispose(); Thread.Sleep(300); }
        foreach (var p in o) { try { p.Kill(); p.WaitForExit(5000); } catch { } p.Dispose(); }
    }
    /// <summary>Replaces only inventoried files. A failed startup restores the previous files.</summary>
    public static string? Apply(string installDir, string staging, bool singleFile, bool start, Func<string, bool>? healthCheck = null, string? workRoot = null, string executableName = "SurfaceInsight.exe")
    {
        UpdateTransaction? transaction = null;
        string? error = null;
        string exe = InstallFiles.In(InstallFiles.Root(installDir), singleFile ? executableName : "SurfaceInsight.exe");
        try
        {
            transaction = UpdateTransaction.Prepare(installDir, staging, singleFile, workRoot ?? WorkDir, executableName);
            if (start) StopOld(installDir, executableName);
            transaction.Install(staging, singleFile);
            if (healthCheck != null && !healthCheck(exe)) throw new IOException("The replacement failed its startup check.");
            if (start)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir };
                psi.ArgumentList.Add("--update-health");
                psi.ArgumentList.Add(transaction.HealthFile);
                psi.ArgumentList.Add(transaction.State.Token);
                using var child = Process.Start(psi) ?? throw new IOException("The replacement could not start.");
                var deadline = DateTime.UtcNow.AddSeconds(90);
                bool healthy = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(transaction.HealthFile) && File.ReadAllText(transaction.HealthFile) == transaction.State.Token) { healthy = true; break; }
                    if (child.HasExited) break;
                    Thread.Sleep(250);
                }
                if (!healthy)
                {
                    StopOld(installDir, executableName);
                    throw new IOException("The replacement did not acknowledge a healthy dashboard and collector within 90 seconds.");
                }
            }
            // Backups are not removed until the new version acknowledges startup.
            try { transaction.Complete(); } catch (Exception cleanup) { Log("Update is healthy; backup cleanup pending: " + cleanup.Message); }
            if (start) MarkDownloadComplete(staging, singleFile, installDir);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (transaction != null)
            {
                try { transaction.Rollback(); transaction.Complete(); }
                catch (Exception recovery) { error += " Recovery needs attention: " + recovery.Message + ". Backups/journal retained at " + transaction.Folder; }
            }
            if (start)
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir })?.Dispose(); }
                catch (Exception restart) { error += " Could not restart: " + restart.Message; }
            }
        }
        if (start)
        {
            try
            {
                Directory.CreateDirectory(MonitorHost.Folder);
                string version = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? "" : "";
                File.WriteAllText(ResultFile, error == null ? "ok|" + version : "fail|" + error);
                Log(error == null ? "Updated and startup confirmed: " + version : "Update failed: " + error);
            }
            catch (Exception reporting) { Log("Update result could not be written: " + reporting.Message + "; " + error); }
        }
        return error;
    }

    static string? startupHealthFile, startupToken;
    static DateTime startupAt;
    public static void SetStartupHealth(string[] args)
    {
        if (args.Length != 3 || args[0] != "--update-health") return;
        string path = Path.GetFullPath(args[1]);
        string work = InstallFiles.Root(WorkDir) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(work, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetFileName(path) != "healthy" || args[2].Length != 64 || !args[2].All(Uri.IsHexDigit))
            throw new IOException("Invalid update health acknowledgement.");
        InstallFiles.NoLinks(path);
        var journal = JsonSerializer.Deserialize<UpdateTransaction.Journal>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "transaction.json")));
        if (journal?.Application != InstallFiles.Application || journal.Token != args[2] ||
            !InstallFiles.PathComparer.Equals(InstallFiles.Root(journal.Target), InstallFiles.Root(InstallDir)))
            throw new IOException("The update transaction does not belong to this installation.");
        startupHealthFile = path; startupToken = args[2]; startupAt = DateTime.UtcNow;
    }

    public static void ConfirmStartup(bool dashboardLoaded)
    {
        if (!dashboardLoaded || startupHealthFile == null) return;
        try
        {
            // A rendered dashboard alone is insufficient: the collector must also have written a fresh sample.
            var line = File.ReadLines(MonitorHost.StatusFile).FirstOrDefault(x => x.StartsWith("ok="));
            if (line == null || !DateTime.TryParse(line[3..], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) || at.ToUniversalTime() < startupAt) return;
            InstallFiles.NoLinks(startupHealthFile);
            File.WriteAllText(startupHealthFile, startupToken);
            startupHealthFile = startupToken = null;
        }
        catch (Exception ex) { Log("Startup acknowledgement pending: " + ex.Message); }
    }

    /// <summary>One line for the dashboard to show after an update attempt, or null. Reading it clears it.</summary>
    public static string? TakeResult()
    {
        try { if (!File.Exists(ResultFile)) return null; var t = File.ReadAllText(ResultFile).Trim(); File.Delete(ResultFile); var p = t.Split('|', 2); if (p[0] == "ok") return "Updated to version " + p[1] + "."; if (p[0] == "fail" && p.Length > 1) return "Update failed: " + p[1]; } catch { }
        return null;
    }

    static void MarkDownloadComplete(string staging, bool single, string target)
    {
        try
        {
            string folder = single ? Path.GetDirectoryName(staging)! : Path.GetDirectoryName(InstallFiles.Root(staging))!;
            string path = InstallFiles.In(InstallFiles.Root(folder), "download.json");
            if (!File.Exists(path)) return; // Downloads prepared by 1.1.0 have no ownership record.
            var record = JsonSerializer.Deserialize<DownloadRecord>(File.ReadAllText(path));
            if (record?.Application != InstallFiles.Application || !InstallFiles.PathComparer.Equals(InstallFiles.Root(record.Target), InstallFiles.Root(target))) return;
            File.WriteAllText(path, JsonSerializer.Serialize(record with { Completed = true }));
        }
        catch (Exception ex) { Log("Download cleanup remains pending: " + ex.Message); }
    }

    /// <summary>Only completed, inventoried downloads for this installation are eligible. No recursive deletion.</summary>
    public static void Cleanup() => CleanupDownloads(WorkDir, InstallDir);
    public static void CleanupDownloads(string work, string target)
    {
        try
        {
            work = InstallFiles.Root(work);
            if (!Directory.Exists(work)) return;
            foreach (string folder in Directory.EnumerateDirectories(work, "download-*"))
            {
                try
                {
                    InstallFiles.NoLinks(folder);
                    string recordPath = InstallFiles.In(folder, "download.json");
                    if (!File.Exists(recordPath)) continue;
                    var record = JsonSerializer.Deserialize<DownloadRecord>(File.ReadAllText(recordPath));
                    if (record?.Application != InstallFiles.Application || !record.Completed || record.Files == null || record.Files.Count > 10_000 ||
                        !InstallFiles.PathComparer.Equals(InstallFiles.Root(record.Target), InstallFiles.Root(target))) continue;
                    // Validate the entire record before any deletion. Preserve changed files and every unknown file.
                    var paths = record.Files.Select(e => (Path: InstallFiles.In(folder, e.Key), Hash: e.Value)).ToList();
                    if (paths.Any(e => e.Path == recordPath || e.Hash.Length != 64 || !e.Hash.All(Uri.IsHexDigit))) continue;
                    foreach (var e in paths)
                        if (File.Exists(e.Path) && InstallFiles.Hash(e.Path).Equals(e.Hash, StringComparison.OrdinalIgnoreCase)) File.Delete(e.Path);
                    var directories = paths.SelectMany(e => ParentsWithin(Path.GetDirectoryName(e.Path)!, folder)).Distinct().OrderByDescending(p => p.Length);
                    foreach (string dir in directories)
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any(p => p != recordPath))
                    {
                        File.Delete(recordPath);
                        Directory.Delete(folder);
                    }
                }
                catch (Exception ex) { Log("Owned download cleanup deferred: " + ex.Message); }
            }
        }
        catch (Exception ex) { Log("Download cleanup unavailable: " + ex.Message); }
    }
    static IEnumerable<string> ParentsWithin(string path, string root)
    {
        for (string? p = path; p != null && p.Length > root.Length; p = Path.GetDirectoryName(p)) yield return p;
    }

    /// <summary>Entry point for "SurfaceInsight.exe --apply-update installDir staging singleFile".</summary>
    public static void RunApplyMode(string[] args) { if (args.Length >= 4) Environment.ExitCode = Apply(args[1], args[2], args[3] == "1", start: true, executableName: args.Length >= 5 ? args[4] : "SurfaceInsight.exe") == null ? 0 : 1; }
}
