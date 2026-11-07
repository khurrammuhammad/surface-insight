using System.Diagnostics;
using System.Security.Principal;
namespace SurfaceInsight;

public static class Diagnostics
{
    internal static (int Code, string Output) Exec(string file, string arguments, int timeout = 120000)
    {
        if (file is "wpr.exe" or "powercfg.exe") file = Path.Combine(Environment.SystemDirectory, file);
        if (!Path.IsPathFullyQualified(file)) throw new IOException("Diagnostics requires an absolute executable path.");
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout)) { p.Kill(true); return (-1, "Timed out"); }
        return (p.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
    internal static bool RecorderIdle(string text) => text.Contains("no trace", StringComparison.OrdinalIgnoreCase) || text.Contains("not recording", StringComparison.OrdinalIgnoreCase);
    public static void Run(string[] args)
    {
        if (args.Length != 2) return;
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceInsight");
        if (!string.Equals(Path.GetFullPath(args[1]), expected, StringComparison.OrdinalIgnoreCase)) { Environment.ExitCode = 6; return; }
        InstallFiles.NoLinks(expected);
        Directory.CreateDirectory(expected);
        using var mutex = new Mutex(true, InstallFiles.Signal("Diagnostics"), out bool first); if (!first) return;
        string folder = Path.Combine(expected, "Capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(folder);
        string instance = "SurfaceInsight-" + Guid.NewGuid().ToString("N");
        string cancel = Path.Combine(expected, "cancel.capture");
        var summary = new List<string>(); bool owns = false;
        try
        {
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!admin) { summary.Add("Administrator access was not granted. No trace started; battery reporting remains available."); }
            var status = Exec("wpr.exe", "-status"); File.WriteAllText(Path.Combine(folder, "wpr-status.txt"), status.Output);
            if (admin && RecorderIdle(status.Output))
            {
                var start = Exec("wpr.exe", "-start CPU.light -start Power.light -instancename " + instance);
                summary.Add("WPR start: " + start.Output); owns = start.Code == 0;
                if (owns)
                {
                    var end = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < end && !File.Exists(cancel)) Thread.Sleep(250);
                    summary.Add(File.Exists(cancel) ? "Capture cancelled early; partial recording retained." : "60-second capture complete.");
                    var stop = Exec("wpr.exe", "-stop \"" + Path.Combine(folder, "activity.etl") + "\" -instancename " + instance);
                    owns = stop.Code != 0; summary.Add("WPR stop: " + stop.Output);
                }
            }
            else if (admin) summary.Add("Capture skipped: another recorder may be active. Existing recordings were left untouched.");
            foreach (var report in new[] { ("batteryreport", "battery"), ("sleepstudy", "sleep") })
            {
                if (File.Exists(cancel)) { summary.Add("Remaining report generation cancelled."); break; }
                foreach (string ext in new[] { "xml", "html" })
                {
                    var result = Exec("powercfg.exe", "/" + report.Item1 + (ext == "xml" ? " /xml" : "") + " /output \"" + Path.Combine(folder, report.Item2 + "." + ext) + "\"");
                    if (result.Code != 0) summary.Add(report.Item1 + ": " + result.Output);
                }
            }
            if (File.Exists(Path.Combine(folder, "battery.xml"))) summary.Add(DeepAnalysis.Battery(Path.Combine(folder, "battery.xml")));
            if (File.Exists(Path.Combine(folder, "sleep.xml"))) summary.Add(DeepAnalysis.Sleep(Path.Combine(folder, "sleep.xml")));
            if (File.Exists(Path.Combine(folder, "activity.etl")) && !File.Exists(cancel))
            {
                string analysis = Path.Combine(folder, "trace-summary.txt"); char q = (char)34;
                var parsed = Exec(Environment.ProcessPath!, "--analyze " + q + Path.Combine(folder, "activity.etl") + q + " " + q + analysis + q, 180000);
                summary.Add(File.Exists(analysis) ? File.ReadAllText(analysis) : "Trace analysis did not complete within three minutes; raw trace retained. " + parsed.Output);
            }
        }
        catch (Exception ex) { summary.Add("Diagnostics failed: " + ex.Message); }
        finally
        {
            if (owns) Exec("wpr.exe", "-stop \"" + Path.Combine(folder, "partial.etl") + "\" -instancename " + instance);
            summary.Add("Raw reports: " + folder);
            File.WriteAllText(Path.Combine(expected, "diagnostics-summary.txt"), string.Join("\n\n", summary));
        }
    }
}



