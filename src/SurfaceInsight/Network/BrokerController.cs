using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
namespace SurfaceInsight.Network;

/// <summary>
/// Tray-host side of the enhanced session. Starts the elevated broker only on explicit request (the user sees a UAC prompt),
/// authenticates it, receives bounded batches and heartbeats, and reports state. It never restarts the broker on its own.
/// </summary>
public sealed class BrokerController : IDisposable
{
    public enum Phase { Off, Starting, Active, Failed }
    public Phase State { get; private set; } = Phase.Off; public string Error { get; private set; } = ""; public string Note { get; private set; } = ""; public bool DnsOk { get; private set; }
    public DateTime? LastBatchUtc { get; private set; } public DateTime? StartedUtc { get; private set; } public long Lost, Overflow;
    readonly ConcurrentQueue<Msg> batches = new(); CancellationTokenSource? cts; NamedPipeServerStream? pipe; Task? run; readonly SemaphoreSlim writeLock = new(1, 1); volatile bool connectionClosedByUs;
    public event Action<string>? Log; public bool PolicyOk { get; private set; }
    public List<string> Blocks { get; private set; } = new(); public event Action<IReadOnlyList<string>>? BlocksChanged; long nextReq; readonly ConcurrentDictionary<long, TaskCompletionSource<Msg>> pending = new();
    /// <summary>Sends a policy request to the broker and waits for its explicit result. Throws if the session is not active or the broker does not answer in time.</summary>
    public async Task<Msg> RequestAsync(string type, string? path, TimeSpan timeout)
    {
        if (State != Phase.Active || pipe is not { IsConnected: true }) throw new InvalidOperationException("The enhanced session is not running.");
        long id = Interlocked.Increment(ref nextReq); var tcs = new TaskCompletionSource<Msg>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = tcs;
        try { await writeLock.WaitAsync(); try { await Ipc.WriteAsync(pipe, new Msg { T = type, Req = id, Path = path }, CancellationToken.None); } finally { writeLock.Release(); } using var c = new CancellationTokenSource(timeout); using (c.Token.Register(() => tcs.TrySetException(new TimeoutException("The broker did not answer in time.")))) return await tcs.Task; }
        finally { pending.TryRemove(id, out _); }
    }
    public bool TryDequeue(out Msg m) => batches.TryDequeue(out m!);
    public bool IsRunning => State is Phase.Starting or Phase.Active;

    public void Start()
    {
        if (IsRunning) return; cts = new CancellationTokenSource(); State = Phase.Starting; Error = ""; Note = "Waiting for the administrator prompt…"; DnsOk = false; Lost = Overflow = 0; StartedUtc = null; LastBatchUtc = null; connectionClosedByUs = false;
        while (batches.TryDequeue(out _)) { }
        run = Task.Run(() => RunAsync(cts.Token));
    }
    public void Stop()
    {
        if (!IsRunning) { State = Phase.Off; return; }
        connectionClosedByUs = true; try { if (pipe is { IsConnected: true }) { writeLock.Wait(1000); try { Ipc.WriteAsync(pipe, new Msg { T = "stop" }, CancellationToken.None).Wait(2000); } finally { writeLock.Release(); } } } catch { }
        cts?.Cancel(); State = Phase.Off; Note = "Stopped."; try { pipe?.Dispose(); } catch { } EndBlocks();
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            string name = "SurfaceInsight.NetBroker." + Guid.NewGuid().ToString("N"), nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var ps = new PipeSecurity(); ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, ps);
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }; psi.ArgumentList.Add("--network-broker"); psi.ArgumentList.Add(name); psi.ArgumentList.Add(nonce); psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi)?.Dispose();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Fail("Administrator permission was declined. Basic monitoring continues."); return; }
            catch (Exception ex) { Fail("The enhanced session could not start: " + ex.Message); return; }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct); connectCts.CancelAfter(TimeSpan.FromSeconds(120));
            try { await pipe.WaitForConnectionAsync(connectCts.Token); } catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Fail("The enhanced session did not connect within two minutes."); return; }

            // Authenticate the peer: it must be our own executable and present the session nonce.
            int peer = Ipc.ClientPid(pipe); var hello = await ReadWithTimeout(pipe, TimeSpan.FromSeconds(10), ct);
            if (hello is not { T: "hello" } || hello.V != Ipc.Version || hello.Nonce != nonce || hello.Pid != peer || peer <= 0 || !Ipc.SamePath(Ipc.ImagePath(peer), Environment.ProcessPath)) { Fail("The enhanced session was rejected: the peer could not be authenticated."); return; }
            await Ipc.WriteAsync(pipe, new Msg { T = "welcome", Ok = true, V = Ipc.Version }, ct); StartedUtc = DateTime.UtcNow; State = Phase.Active; Note = "Connected.";
            using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () => { try { while (!hbCts.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(Ipc.HeartbeatSeconds), hbCts.Token); await writeLock.WaitAsync(hbCts.Token); try { await Ipc.WriteAsync(pipe, new Msg { T = "hb" }, hbCts.Token); } finally { writeLock.Release(); } } } catch { } });
            while (!ct.IsCancellationRequested)
            {
                var m = await Ipc.ReadAsync(pipe, ct); if (m == null) break;
                switch (m.T)
                {
                    case "status": DnsOk = m.DnsOk; PolicyOk = m.PolicyOk; Note = m.Note ?? ""; break;
                    case "block-result": Blocks = m.Blocks ?? new(); BlocksChanged?.Invoke(Blocks); if (pending.TryGetValue(m.Req, out var tcs)) tcs.TrySetResult(m); break;
                    case "batch": if (batches.Count < 256) batches.Enqueue(m); else Interlocked.Increment(ref Overflow); LastBatchUtc = DateTime.UtcNow; Interlocked.Add(ref Lost, m.Lost); Interlocked.Add(ref Overflow, m.Overflow); break;
                    case "hb": LastBatchUtc ??= DateTime.UtcNow; break;
                    default: throw new InvalidDataException("Unexpected message " + m.T);
                }
            }
            hbCts.Cancel();
            if (!connectionClosedByUs && !ct.IsCancellationRequested) Fail("The enhanced session ended unexpectedly. Basic monitoring continues.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!connectionClosedByUs) Fail("The enhanced session failed: " + ex.Message); }
        finally { if (State is Phase.Starting or Phase.Active) State = connectionClosedByUs ? Phase.Off : Phase.Failed; try { pipe?.Dispose(); } catch { } }
    }
    static async Task<Msg?> ReadWithTimeout(Stream s, TimeSpan t, CancellationToken ct) { using var c = CancellationTokenSource.CreateLinkedTokenSource(ct); c.CancelAfter(t); try { return await Ipc.ReadAsync(s, c.Token); } catch { return null; } }
    void Fail(string why) { Error = why; Note = ""; State = Phase.Failed; Log?.Invoke(why); EndBlocks(); }
    /// <summary>Temporary blocks live only as long as the broker process; once it is gone Windows has removed them.</summary>
    void EndBlocks() { if (Blocks.Count == 0) return; Blocks = new(); BlocksChanged?.Invoke(Blocks); foreach (var p in pending.Values) p.TrySetException(new InvalidOperationException("The enhanced session ended.")); }
    public void Dispose() { Stop(); cts?.Dispose(); }
}
