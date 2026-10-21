using System.Threading.Channels;

namespace DSAVehicle.ParkingCore;

// Wakes the worker as soon as a message is stored, so the decision isn't delayed by a polling interval (GC-22).
public sealed class InboxSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Notify() => channel.Writer.TryWrite(true);
    public async Task Wait(TimeSpan max, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(max);
        try { await channel.Reader.ReadAsync(timeout.Token); } catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
    }
}
