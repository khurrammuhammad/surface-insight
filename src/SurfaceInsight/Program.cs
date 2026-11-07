using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Diagnostics;
using System.Text;
using System.Net;
using Forms = System.Windows.Forms;
namespace SurfaceInsight;

public class Program
{
    // --shot <page> <out.png> [app] [theme] [range]: renders a dashboard page to PNG for visual checks; does not touch the singleton mutex.
    static void Shot(string[] a)
    {
        var app = new System.Windows.Application(); app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SurfaceInsight;component/UI/Theme.xaml", UriKind.Relative) });
        string Opt(int i) => a.Length > i && a[i] != "-" ? a[i] : null!;
        var w = new MainWindow { Width = double.TryParse(Environment.GetEnvironmentVariable("SHOT_WIDTH"), out var sw) ? sw : 1220, Height = double.TryParse(Environment.GetEnvironmentVariable("SHOT_HEIGHT"), out var sh) ? sh : 900 }; w.Show(); w.PrepareShot(a[1], Opt(3), Opt(4), Opt(5));
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) => { t.Stop(); try { w.Shot(a[2]); } catch (Exception ex) { File.WriteAllText(a[2] + ".err.txt", ex.ToString()); } app.Shutdown(); };
        t.Start(); app.Run();
    }
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--apply-update") { Updater.RunApplyMode(args); return; }
        if (args.Contains("--diagnose")) { Diagnostics.Run(args); return; }
        if(args.Length==3&&args[0]=="--reports"){File.WriteAllText(args[2],DeepAnalysis.Battery(Path.Combine(args[1],"battery.xml"))+Environment.NewLine+DeepAnalysis.Sleep(Path.Combine(args[1],"sleep.xml")));return;}
        if(args.Length==3&&args[0]=="--analyze"){File.WriteAllText(args[2],DeepAnalysis.Trace(args[1]));return;}
        if (args.Length >= 4 && args[0] == "--network-broker") { Environment.ExitCode = Network.BrokerRole.Run(args); return; }
        if (args.Contains("--self-test")) { SelfTest.Run(args.Contains("--ci")); return; }
        if (args.Contains("--host")) { using var hostMutex = new Mutex(true, InstallFiles.Signal("Host"), out bool owned); if (!owned) return; Forms.Application.Run(new MonitorHost()); return; }
        if (args.Length >= 3 && args[0] == "--shot") { Shot(args); return; }
        if (args.Length >= 2 && args[0] == "--brand") { Pal.Apply(false, new ResourceDictionary()); Brand.Export(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--net-collect") { Network.DevTools.Collect(int.Parse(args[1])); return; }
        if (args.Length >= 2 && args[0] == "--net-enhanced-test") { Network.DevTools.EnhancedTest(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--net-names") { Network.DevTools.NameReport(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--net-audit") { Network.DevTools.DumpAudit(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--net-seed") { Network.DevTools.Seed(int.Parse(args[1])); return; }
        if (args.Length >= 2 && args[0] == "--net-dump") { var c = new Network.BasicCollector(); c.Read(5); Thread.Sleep(2000); var s = c.Read(5); File.WriteAllLines(args[1], s.Conns.Select(x => string.Join("|", x.Proto, x.LocalIp, x.LocalPort, x.RemoteIp, x.RemotePort, x.State, x.Pid, Network.ProcessIdentity.Resolve(x.Pid).App.Name)).Concat(s.Adapters.Select(a => "ADAPTER|" + a.Name + "|" + a.Type + "|" + a.Physical + "|" + a.Up + "|" + a.RxBytes + "|" + a.TxBytes)).Concat(s.Deltas.Select(x => "DELTA|" + x.Id + "|" + x.Rx + "|" + x.Tx + "|" + x.Seconds)).Append("ERROR|" + s.Error)); return; }
        Updater.SetStartupHealth(args);
        MonitorHost.EnsureRunning(); if (args.Contains("--background")) return;
        using var mutex = new Mutex(true, InstallFiles.Signal("Dashboard"), out bool first); if (!first) { using var show=new EventWaitHandle(false,EventResetMode.AutoReset,InstallFiles.Signal("Show"));show.Set();return; }
        var app = new System.Windows.Application(); app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SurfaceInsight;component/UI/Theme.xaml", UriKind.Relative) }); app.Run(new MainWindow());
    }
}

