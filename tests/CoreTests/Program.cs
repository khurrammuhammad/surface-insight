using System.Collections.Concurrent;
using SurfaceInsight;
using SurfaceInsight.Network;
int passed = 0, failed = 0, skipped = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (condition) passed++; else failed++;
}
try
{
    UpdaterTests.Run(Check, reason => { skipped++; Console.WriteLine("SKIP " + reason); });
    using var queue = new BlockingCollection<Msg>(1);
    var delivery = new BrokerDelivery(queue);
    Check(delivery.TrySend(new Msg { T = "batch" }), "Delivery accepts a frame when capacity is available");
    Check(!delivery.TrySend(new Msg { T = "batch", Lost = 3, Overflow = 2, Buckets = new() { new() { Sent = 40, Recv = 60 } } }), "Delivery applies bounded backpressure");
    Check(!delivery.TrySend(new Msg { T = "batch", Lost = 4 }), "Repeated backpressure remains bounded");
    queue.Take();
    Check(delivery.TrySend(new Msg { T = "batch" }), "An empty status batch can deliver retained losses");
    var recovered = queue.Take();
    Check(recovered.DroppedFrames == 2 && recovered.DroppedBytes == 100 && recovered.Lost == 7 && recovered.Overflow == 2, "All lost frames, bytes and diagnostic counters survive a full queue");
    Check(!delivery.Pending, "Successfully delivered counters reset exactly once");
    using var stream = new MemoryStream();
    Ipc.WriteAsync(stream, recovered, CancellationToken.None).GetAwaiter().GetResult(); stream.Position = 0;
    var roundtrip = Ipc.ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    Check(roundtrip?.DroppedFrames == 2 && roundtrip.DroppedBytes == 100, "Loss accounting survives IPC serialization");
}
catch (Exception ex) { Check(false, "Unhandled test exception: " + ex); }
finally { if (Directory.Exists(MonitorHost.Folder)) Directory.Delete(MonitorHost.Folder, true); }
Console.WriteLine($"{passed} passed; {failed} failed; {skipped} skipped.");
return failed == 0 ? 0 : 1;
