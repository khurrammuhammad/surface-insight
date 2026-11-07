using System.Diagnostics;
using Forms = System.Windows.Forms;
namespace SurfaceInsight;

public sealed class MonitorHost : Forms.ApplicationContext
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    readonly History history = new(); Collector collector = new(); readonly Forms.NotifyIcon tray;
    // Update support: the updater signals this event to ask the tray monitor to quit cleanly; the timer runs the opt-in daily check.
    readonly EventWaitHandle quitSignal = new(false, EventResetMode.AutoReset, Updater.QuitEvent); readonly Forms.Timer updTimer = new() { Interval = 10 * 60 * 1000 }; bool updBusy;
    readonly Forms.Timer timer = new() { Interval = 1000 }; bool busy; volatile bool suspended; DateTime last = DateTime.MinValue, maintenance = DateTime.MinValue;
    Task? stuck; DateTime? lastOk; string? lastError;
    // Network (basic mode) runs on its own timer and worker so a blocked IP Helper call can never delay battery samples.
    readonly Forms.Timer netTimer = new() { Interval = 1000 }; readonly Network.BasicCollector netCollector = new(); Network.NetworkStore? netStore; Task? netStuck; Network.BrokerController? broker; bool enhOpen; DateTime ptrLast = DateTime.MinValue; Task? ptrTask; readonly System.Collections.Concurrent.ConcurrentQueue<(string Ip, string Name)> ptrResults = new(); readonly object blockLock = new(); readonly Dictionary<string, DateTime> blockSince = new(StringComparer.OrdinalIgnoreCase); readonly List<string> blockResults = new(); string blockEnded = ""; readonly System.Collections.Concurrent.ConcurrentQueue<(string Kind, string Detail)> auditQueue = new();
    public static string BlocksFile => Path.Combine(Folder, "network.blocks"); string netCmdFile => Path.Combine(Folder, "network.cmd");
    bool netBusy, netResumed; DateTime netLast = DateTime.MinValue, netMaintenance = DateTime.MinValue, netOk = DateTime.MinValue; Network.NetMode netMode = Network.NetMode.Off; string netError = ""; double netRx, netTx; string netAdapters = "";
    public static string Folder => Environment.GetEnvironmentVariable("SURFACEINSIGHT_DATA") is { Length: > 0 } dev ? dev : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceInsight");
    public static string Heartbeat => Path.Combine(Folder, "dashboard.active");
    public static string StatusFile => Path.Combine(Folder, "collector.status");
    public static string ErrorLog => Path.Combine(Folder, "collector-error.txt");
    public static void EnsureRunning()
    {
        using var mutex = new Mutex(false, "Local\\SurfaceInsightHost"); bool owned = false; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return; mutex.ReleaseMutex();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--host") { UseShellExecute = false, CreateNoWindow = true });
    }
    public MonitorHost()
    {
        File.Delete(Path.Combine(Folder, "host.stopped"));
        try { var stale = Network.NetworkSettings.ReadBlocks(); if (stale.Blocks.Count > 0) { string why = "The tray monitor stopped unexpectedly; Windows removed the blocks."; blockEnded = DateTime.UtcNow.ToString("O") + "|" + why; auditQueue.Enqueue(("blocks-ended", why)); WriteBlocksFile(); } } catch { }
        tray = new Forms.NotifyIcon { Icon = Brand.TrayIcon(), Text = "Surface Insight", Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Surface Insight", null, (_, _) => Open()); menu.Items.Add("Mark loud fans", null, (_, _) => history.Mark()); var netItem = new Forms.ToolStripMenuItem("Network monitoring (basic)") { CheckOnClick = false }; netItem.Click += (_, _) => { Network.NetworkSettings.SetEnabled(!Network.NetworkSettings.IsEnabled); netLast = DateTime.MinValue; }; menu.Opening += (_, _) => netItem.Checked = Network.NetworkSettings.IsEnabled; menu.Items.Add(netItem); var enhItem = new Forms.ToolStripMenuItem("Enhanced monitoring (administrator)"); enhItem.Click += (_, _) => { if (broker != null && broker.IsRunning) StopEnhanced("Stopped from tray"); else StartEnhanced(); }; menu.Opening += (_, _) => { enhItem.Checked = broker != null && broker.IsRunning; enhItem.Enabled = Network.NetworkSettings.IsEnabled; }; menu.Items.Add(enhItem); var rmItem = new Forms.ToolStripMenuItem("Remove all temporary blocks"); rmItem.Click += (_, _) => HandleBlockCommand(new[] { "block-remove-all", "0", "" }); menu.Opening += (_, _) => { lock (blockLock) rmItem.Enabled = blockSince.Count > 0; rmItem.Text = blockSince.Count > 0 ? "Remove all temporary blocks (" + blockSince.Count + ")" : "Remove all temporary blocks"; }; menu.Items.Add(rmItem); menu.Items.Add("Check for updates…", null, (_, _) => OpenSettings(true)); menu.Items.Add("Quit", null, (_, _) => ExitThread()); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Open();
        Microsoft.Win32.SystemEvents.PowerModeChanged+=(_,e)=>{if(e.Mode==Microsoft.Win32.PowerModes.Suspend)suspended=true;if(e.Mode==Microsoft.Win32.PowerModes.Resume){suspended=false;last=DateTime.MinValue;netResumed=true;netLast=DateTime.MinValue;}}; timer.Tick += async (_, _) => { if (quitSignal.WaitOne(0)) { ExitThread(); return; } await Tick(); }; timer.Start(); updTimer.Tick += async (_, _) => await UpdateTick(); updTimer.Start(); tray.BalloonTipClicked += (_, _) => OpenSettings(false); Task.Run(Updater.Cleanup); netTimer.Tick += async (_, _) => await NetTick(); netTimer.Start();
    }
    void Open() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
    /// <summary>Opens the dashboard on Settings. With check, the dashboard also runs the update check (the user asked for it from the tray).</summary>
    void OpenSettings(bool check) { try { Directory.CreateDirectory(Folder); File.WriteAllText(Updater.OpenSettingsFlag, check ? "check" : "show"); } catch { } Open(); }
    /// <summary>The daily update check, only when the user turned it on. It never downloads or installs; it shows at most one notification per new version.</summary>
    async Task UpdateTick()
    {
        if (updBusy || suspended || !Updater.AutoEnabled) return; if (Updater.ReadState().Checked is DateTime at && (DateTime.UtcNow - at).TotalHours < 24) return;
        updBusy = true;
        try { var r = await Updater.CheckAsync(); if (r.Update is UpdateInfo u && Updater.ReadState().Notified != u.Tag) { Updater.MarkNotified(u.Tag); tray.ShowBalloonTip(10000, "Surface Insight update available", "Version " + u.Version + " is available. Click to review it.", Forms.ToolTipIcon.Info); } }
        catch { }
        finally { updBusy = false; }
    }
    public static void Log(string text)
    {
        try { var f = new FileInfo(ErrorLog); if (f.Exists && f.Length > 64 * 1024) f.Delete(); File.AppendAllText(ErrorLog, DateTime.Now.ToString("s") + "  " + text + Environment.NewLine); } catch { }
    }
    /// <summary>Runs a blocking read on a worker thread; reports a timeout instead of waiting forever. The still-running task is returned so the caller can avoid piling up more reads behind it.</summary>
    public static async Task<(Task<T> Read, bool TimedOut)> ReadWithTimeout<T>(Func<T> read, TimeSpan timeout)
    {
        var task = Task.Run(read); return (task, await Task.WhenAny(task, Task.Delay(timeout)) != task);
    }
    void WriteStatus(string stage)
    {
        try { File.WriteAllLines(StatusFile, new[] { "ok=" + (lastOk?.ToString("O") ?? ""), "stage=" + stage, "error=" + (lastError ?? "").Replace('\n', ' ') }); } catch { }
    }
    async Task Tick()
    {
        bool active = File.Exists(Heartbeat) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(Heartbeat)).TotalSeconds < 12;
        if (suspended || busy || (DateTime.UtcNow - last).TotalSeconds < (active ? 5 : 15)) return; busy = true; last = DateTime.UtcNow;
        try
        {
            if (stuck != null)
            {
                // A previous read is still blocked inside a native/WMI call. Do not pile up more threads behind it.
                if (!stuck.IsCompleted) { lastError = "Previous collector read is still blocked at stage " + collector.Stage; WriteStatus(collector.Stage); return; }
                stuck = null; collector = new Collector(); Log("Blocked read finished; collector recreated.");
            }
            var (read, timedOut) = await ReadWithTimeout(collector.Read, ReadTimeout);
            if (timedOut)
            {
                stuck = read; lastError = "Collector read timed out after " + (int)ReadTimeout.TotalSeconds + " s at stage '" + collector.Stage + "'";
                Log(lastError); WriteStatus(collector.Stage); return;
            }
            var sample = await read; history.Add(sample); lastOk = DateTime.UtcNow; lastError = null; WriteStatus("ok");
            tray.Text = $"Surface Insight · {(sample.Watts.HasValue ? sample.Watts.Value.ToString("F1") + " W" : "Power unavailable")} · CPU {sample.Cpu:F0}%";
            if ((DateTime.UtcNow - maintenance).TotalHours > 1) { maintenance = DateTime.UtcNow; try { history.Maintain(); } catch (Exception ex) { Log("Maintenance failed: " + ex.Message); } }
        }
        catch (Exception ex) { lastError = ex.Message; Log("Collector stage '" + collector.Stage + "': " + ex); WriteStatus(collector.Stage); }
        finally { busy = false; }
    }
    void WriteNetStatus(string stage = "") => Network.NetworkSettings.WriteStatus(new(netMode, netOk == DateTime.MinValue ? null : netOk, netError, stage, netRx, netTx, netAdapters, broker?.State.ToString() ?? "Off", broker == null ? "" : (broker.Error.Length > 0 ? broker.Error : broker.Note), broker?.DnsOk ?? false, broker?.Lost ?? 0, broker?.Overflow ?? 0, broker?.LastBatchUtc));
    void StartEnhanced() { if (!Network.NetworkSettings.IsEnabled) return; broker ??= CreateBroker(); broker.Start(); WriteNetStatus("enhanced-start"); }
    void StopEnhanced(string reason) { bool had = blockSince.Count > 0; broker?.Stop(); NoteBlocksEnded(had ? "Enhanced monitoring was stopped (" + reason + "); Windows removed the blocks." : ""); EndEnhancedCoverage(reason); WriteNetStatus("enhanced-stop"); }
    Network.BrokerController CreateBroker() { var b = new Network.BrokerController(); b.Log += m => Log(m); b.BlocksChanged += OnBlocksChanged; return b; }
    /// <summary>Broker-confirmed block state. Only a successful broker answer changes it; the file is what the dashboard shows.</summary>
    void OnBlocksChanged(IReadOnlyList<string> now)
    {
        lock (blockLock)
        {
            if (now.Count == 0 && blockSince.Count > 0 && broker?.State != Network.BrokerController.Phase.Active)
            {
                string why = broker?.State == Network.BrokerController.Phase.Failed ? "The enhanced session ended unexpectedly; Windows removed the blocks. " + broker.Error : "Enhanced monitoring stopped; Windows removed the blocks.";
                auditQueue.Enqueue(("blocks-ended", why)); blockEnded = DateTime.UtcNow.ToString("O") + "|" + why;
            }
            foreach (var p in now) if (!blockSince.ContainsKey(p)) { blockSince[p] = DateTime.UtcNow; auditQueue.Enqueue(("block-add", p)); }
            foreach (var p in blockSince.Keys.Where(k => !now.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) { blockSince.Remove(p); auditQueue.Enqueue(("block-remove", p)); }
            if (now.Count > 0) blockEnded = ""; WriteBlocksFile();
        }
    }
    void WriteBlocksFile() { try { var l = new List<string> { "state|" + (broker?.State.ToString() ?? "Off") + "|" + DateTime.UtcNow.ToString("O") }; foreach (var kv in blockSince) l.Add("block|" + kv.Key + "|" + kv.Value.ToString("O")); foreach (var r in blockResults.TakeLast(20)) l.Add(r); if (blockEnded.Length > 0) l.Add("ended|" + blockEnded); string tmp = BlocksFile + ".tmp"; File.WriteAllLines(tmp, l); File.Move(tmp, BlocksFile, true); } catch { } }
    void NoteBlocksEnded(string reason) { /* handled in OnBlocksChanged when the session goes away */ }
    /// <summary>block-add|id|path, block-remove|id|path, block-remove-all|id. Answers are written to network.blocks as result|id|ok/fail|message.</summary>
    async void HandleBlockCommand(string[] p)
    {
        string type = p[0]; string id = p.Length > 1 ? p[1] : "0"; string path = p.Length > 2 ? p[2] : ""; string outcome; long req = long.TryParse(id, out var r0) ? r0 : 0;
        try
        {
            if (broker == null || broker.State != Network.BrokerController.Phase.Active) throw new InvalidOperationException("Enhanced monitoring is not running, so nothing can be blocked.");
            if (type == "block-add" && Network.PolicyRules.Reject(path, new[] { Environment.ProcessPath! }) is string why) throw new InvalidOperationException(why);
            var res = await broker.RequestAsync(type, path.Length > 0 ? path : null, TimeSpan.FromSeconds(10));
            outcome = res.Ok ? "ok|" + (type == "block-add" ? "Temporary block is active." : "Removed.") : "fail|" + (res.Error ?? "The request failed.");
            if (!res.Ok) auditQueue.Enqueue(("block-failed", path + " — " + (res.Error ?? "failed")));
        }
        catch (Exception ex) { outcome = "fail|" + ex.Message; auditQueue.Enqueue(("block-failed", path + " — " + ex.Message)); }
        lock (blockLock) { blockResults.Add("result|" + req + "|" + outcome.Replace('\n', ' ')); WriteBlocksFile(); }
    }
    void EndEnhancedCoverage(string reason) { if (!enhOpen) return; enhOpen = false; try { netStore?.EndEnhanced(DateTime.UtcNow, reason); } catch (Exception ex) { Log("Enhanced coverage close failed: " + ex.Message); } }
    /// <summary>Runs every second: dashboard commands, broker batches and session state. Independent of the 5/15 s basic cadence.</summary>
    void PumpEnhanced()
    {
        try
        {
            if (File.Exists(netCmdFile)) { string cmd = ""; try { cmd = File.ReadAllText(netCmdFile).Trim(); File.Delete(netCmdFile); } catch { } if (cmd == "enhanced-start") StartEnhanced(); else if (cmd == "enhanced-stop") StopEnhanced("Stopped by user"); else if (cmd.StartsWith("block-")) HandleBlockCommand(cmd.Split('|', 3));
            }
            while (auditQueue.TryDequeue(out var ae)) { netStore ??= new Network.NetworkStore(); netStore.Audit(ae.Kind, ae.Detail); }
            if (broker == null) return; netStore ??= new Network.NetworkStore();
            if (broker.State == Network.BrokerController.Phase.Active && !enhOpen) { netStore.BeginEnhanced(DateTime.UtcNow); enhOpen = true; WriteNetStatus("enhanced-active"); }
            int n = 0; while (n++ < 64 && broker.TryDequeue(out var m)) netStore.WriteBatch(m);
            if (enhOpen && broker.State != Network.BrokerController.Phase.Active) { NoteBlocksEnded(blockSince.Count > 0 ? "The enhanced session ended unexpectedly; Windows removed the blocks. " + broker.Error : ""); EndEnhancedCoverage(broker.Error.Length > 0 ? broker.Error : "Session ended"); WriteNetStatus("enhanced-ended"); }
        }
        catch (Exception ex) { Log("Enhanced pump: " + ex.Message); }
    }
    async Task NetTick()
    {
        PumpEnhanced();
        if (!Network.NetworkSettings.IsEnabled)
        {
            if (broker != null && broker.IsRunning) StopEnhanced("Network monitoring was turned off");
            if (netMode != Network.NetMode.Off) { netMode = Network.NetMode.Off; netError = ""; netRx = netTx = 0; WriteNetStatus(); netStore?.Dispose(); netStore = null; }
            return;
        }
        bool active = File.Exists(Heartbeat) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(Heartbeat)).TotalSeconds < 12; double interval = active ? 5 : 15;
        if (suspended || netBusy || (DateTime.UtcNow - netLast).TotalSeconds < interval) return; netBusy = true; netLast = DateTime.UtcNow;
        try
        {
            if (netStuck != null) { if (!netStuck.IsCompleted) { netMode = Network.NetMode.Partial; netError = "A network table read is still blocked at stage " + netCollector.Stage; WriteNetStatus(netCollector.Stage); return; } netStuck = null; }
            netStore ??= new Network.NetworkStore(); bool resumed = netResumed; netResumed = false;
            var (read, timedOut) = await ReadWithTimeout(() => netCollector.Read(interval, resumed), ReadTimeout);
            if (timedOut) { netStuck = read; netMode = Network.NetMode.Partial; netError = "Network read timed out after " + (int)ReadTimeout.TotalSeconds + " s at stage '" + netCollector.Stage + "'"; Log(netError); WriteNetStatus(netCollector.Stage); return; }
            var snap = await read; netStore.Write(snap); netOk = DateTime.UtcNow; netMode = snap.Error != null ? Network.NetMode.Partial : broker?.State == Network.BrokerController.Phase.Active ? Network.NetMode.Enhanced : Network.NetMode.Basic; netError = snap.Error ?? "";
            var phys = snap.Adapters.Where(a => a.Physical && a.Up).Select(a => a.Id).ToHashSet(); var dd = snap.Deltas.Where(x => phys.Contains(x.Id)).ToList();
            netRx = dd.Count > 0 ? dd.Sum(x => (double)x.Rx) / dd.Max(x => x.Seconds) : 0; netTx = dd.Count > 0 ? dd.Sum(x => (double)x.Tx) / dd.Max(x => x.Seconds) : 0; netAdapters = string.Join(", ", snap.Adapters.Where(a => phys.Contains(a.Id)).Select(a => a.Name));
            WriteNetStatus("ok");
            while (ptrResults.TryDequeue(out var pr)) netStore.SavePtr(pr.Ip, pr.Name);
            if (Network.NetworkSettings.PtrEnabled && (ptrTask == null || ptrTask.IsCompleted) && (DateTime.UtcNow - ptrLast).TotalSeconds > 30)
            {
                ptrLast = DateTime.UtcNow; var ips = netStore.UnnamedPublicIps(DateTime.UtcNow.AddMinutes(-15), 24);
                if (ips.Count > 0) ptrTask = Task.Run(async () => { using var gate = new SemaphoreSlim(4); await Task.WhenAll(ips.Select(async ip => { await gate.WaitAsync(); try { using var cts = new CancellationTokenSource(3000); var h = await System.Net.Dns.GetHostEntryAsync(ip, cts.Token); ptrResults.Enqueue((ip, h.HostName != ip ? h.HostName.ToLowerInvariant() : "")); } catch { ptrResults.Enqueue((ip, "")); } finally { gate.Release(); } })); });
            }
            if ((DateTime.UtcNow - netMaintenance).TotalHours > 1) { netMaintenance = DateTime.UtcNow; try { netStore.Maintain(); } catch (Exception ex) { Log("Network maintenance failed: " + ex.Message); } }
        }
        catch (Exception ex) { netMode = Network.NetMode.Partial; netError = ex.Message; Log("Network stage '" + netCollector.Stage + "': " + ex); WriteNetStatus(netCollector.Stage); }
        finally { netBusy = false; }
    }    protected override void ExitThreadCore() { timer.Stop(); netTimer.Stop(); updTimer.Stop(); try { broker?.Stop(); NoteBlocksEnded("The tray monitor quit; Windows removed the blocks."); EndEnhancedCoverage("Host quit"); } catch { } netStore?.Dispose(); tray.Visible = false; tray.Dispose(); history.Dispose(); File.WriteAllText(Path.Combine(Folder, "host.stopped"), DateTime.UtcNow.ToString("O")); base.ExitThreadCore(); }
}
