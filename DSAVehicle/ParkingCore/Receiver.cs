using System.Text.Json;
using System.Threading.Channels;

namespace DSAVehicle.ParkingCore;

// Module 3 (auto-open) and module 2 (PPE adapter) parts of the camera input; the receiver, inbox and adapters are in Modules/Identity.
// Auto-open requests from adapters, handled apart from the inbox so a slow barrier controller never delays the next decision (GC-23).
public sealed class AutoOpenQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid eventId) => channel.Writer.TryWrite(eventId);
    public ChannelReader<Guid> Reader => channel.Reader;
}
public sealed class AutoOpenWorker(IServiceScopeFactory scopes, AutoOpenQueue queue, ILogger<AutoOpenWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Up to 4 openings in flight (one per lane in practice: a Pending command blocks its lane).
        using var slots = new SemaphoreSlim(4);
        var running = new List<Task>();
        try
        {
            await foreach (var eventId in queue.Reader.ReadAllAsync(stop))
            {
                await slots.WaitAsync(stop);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ParkingService>().TryAutoOpen(eventId); }
                    catch (Exception ex) { logger.LogError(ex, "Automatic opening for {EventId} failed", eventId); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        await Task.WhenAll(running);
    }
}

// PPE camera messages don't create events; the worker feeds them into PpeCache (see People.cs).
public sealed class PpeAdapter : ICameraAdapter
{
    public string Name => "ppe";
    public string Version => "ppe/1";
    public IReadOnlyList<ExternalObservation> Parse(JsonElement root) => [];
}
