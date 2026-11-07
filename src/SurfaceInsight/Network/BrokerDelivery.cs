using System.Collections.Concurrent;

namespace SurfaceInsight.Network;

/// <summary>Single-producer accounting: a full bounded queue cannot discard its own loss report.</summary>
public sealed class BrokerDelivery
{
    readonly BlockingCollection<Msg> queue;
    long lost, overflow, unsupported, groups, frames;
    ulong bytes;
    public bool Pending => lost != 0 || overflow != 0 || unsupported != 0 || groups != 0 || frames != 0;
    public BrokerDelivery(BlockingCollection<Msg> queue) { this.queue = queue; }

    public bool TrySend(Msg message)
    {
        lost += message.Lost;
        overflow += message.Overflow;
        unsupported += message.Unsupported;
        groups += message.FlowsDropped;
        message.Lost = lost; message.Overflow = overflow; message.Unsupported = unsupported;
        message.FlowsDropped = groups; message.DroppedFrames = frames; message.DroppedBytes = bytes;
        if (!queue.TryAdd(message))
        {
            frames++;
            foreach (var b in message.Buckets ?? new()) bytes += b.Sent + b.Recv;
            return false;
        }
        lost = overflow = unsupported = groups = frames = 0;
        bytes = 0;
        return true;
    }
}
