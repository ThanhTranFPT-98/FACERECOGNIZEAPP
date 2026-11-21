using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Third-party AI camera input (Plan V2 Phase 2, docs/THIRD_PARTY_CAMERA_PLAN.md).
// Flow: camera → receiver (store, then ACK) → inbox worker → adapter → ParkingService.Ingest. A camera message is evidence, never a command.

public sealed class CameraInboxWorker(IServiceScopeFactory scopes, InboxSignal signal, CameraAdapters adapters, ILogger<CameraInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processed = await RunOnce(scope.ServiceProvider, adapters, DateTime.UtcNow, stop);
                if (processed == 0) await signal.Wait(TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Camera inbox processing failed"); await Task.Delay(1000, stop); }
        }
    }

    // One batch, oldest first. Public for tests.
    public static async Task<int> RunOnce(IServiceProvider services, CameraAdapters adapters, DateTime now, CancellationToken stop = default)
    {
        var db = services.GetRequiredService<ParkingDb>();
        var service = services.GetRequiredService<ParkingService>();
        var resolver = new DeviceAdapterResolver(adapters, db, services.GetService<ProfileCache>() ?? new ProfileCache());
        var batch = await db.CameraInbox.Where(x => x.Status == "Pending").OrderBy(x => x.Id).Take(20).ToListAsync(stop);
        foreach (var item in batch)
        {
            item.Attempts++;
            try
            {
                var adapter = await resolver.Find(item.Adapter, stop) ?? throw new InvalidDataException("Unknown or disabled adapter " + item.Adapter);
                item.AdapterVersion = adapter.Version;
                using var document = PayloadFormat.Parse(adapter.Format, item.Payload ?? "{}");
                if (adapter.Name == "ppe")
                {
                    var cache = services.GetRequiredService<PpeCache>();
                    var readings = 0;
                    foreach (var (device, channel, roi, reading) in PpeMessage.Parse(document.RootElement, item.ReceivedUtc))
                    {
                        var candidates = await db.Cameras.Where(x => x.ExternalDevice == device && (x.ExternalChannel == null || x.ExternalChannel == "" || x.ExternalChannel == channel)).OrderBy(x => x.Id).ToListAsync(stop);
                        var ppeCamera = PpeMessage.CameraFor(candidates, roi);
                        if (ppeCamera?.LaneId is null) continue;
                        ppeCamera.LastMessageUtc = now; cache.Record(ppeCamera.LaneId, reading); readings++;
                    }
                    item.Observations = readings; item.Status = readings > 0 ? "Processed" : "Ignored";
                    item.Error = readings > 0 ? null : "PPE message from an unmapped device or ROI";
                    item.ProcessedUtc = now; await db.SaveChangesAsync(stop);
                    continue;
                }
                var observations = adapter.Parse(document.RootElement);
                // Any message proves the device is alive (T1550 sends alarm messages every few seconds), even without a plate.
                if (adapter.Name == "t1550" && document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("dev_net_info", out var net)
                    && net.ValueKind == JsonValueKind.Array && net.GetArrayLength() > 0 && T1550Adapter.Normalize(T1550Adapter.Str(net[0], "mac")) is { Length: > 0 } alive)
                    foreach (var seen in await db.Cameras.Where(x => x.ExternalDevice == alive).ToListAsync(stop)) seen.LastMessageUtc = now;
                // Profiles: a heartbeat skipped by "match" still counts when deviceId is a "$." path readable from the message root.
                if (adapter is ProfileAdapter profile && profile.DeviceOf(document.RootElement) is { Length: > 0 } profileDevice)
                    foreach (var seen in await db.Cameras.Where(x => x.ExternalDevice == profileDevice).ToListAsync(stop)) seen.LastMessageUtc = now;
                item.Observations = observations.Count;
                var notes = new List<string>();
                foreach (var observation in observations) notes.Add(await Handle(db, service, item, observation, now));
                item.Status = observations.Count == 0 ? "Ignored" : "Processed";
                item.Error = observations.Count == 0 ? "No plate/person observation (e.g. alarm-only message)" : Truncate(string.Join("; ", notes.Where(n => n.Length > 0)));
            }
            catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A malformed message is kept for inspection and never blocks the queue.
                item.Status = "Failed"; item.Error = Truncate(ex.Message);
            }
            item.ProcessedUtc = now;
            await db.SaveChangesAsync(stop);
        }
        return batch.Count;
    }

    private static async Task<string> Handle(ParkingDb db, ParkingService service, CameraInbox item, ExternalObservation o, DateTime now)
    {
        var camera = await db.Cameras.SingleOrDefaultAsync(x => x.ExternalDevice == o.DeviceId && (x.ExternalChannel == null || x.ExternalChannel == "" || x.ExternalChannel == o.Channel));
        if (camera is null || camera.LaneId is null)
        {
            var reason = "UNKNOWN_CAMERA:" + o.DeviceId + "/" + o.Channel;
            if (!await db.Alerts.AnyAsync(x => x.Reason == reason && !x.Resolved)) db.Alerts.Add(new() { Reason = reason, Severity = "Medium" });
            return "unmapped device " + o.DeviceId + "/" + o.Channel;
        }
        camera.LastMessageUtc = now;
        var note = "";
        // CR-015: a face device's own ID → our PersonId. An unmapped ID never authorises: the result goes to the guard without a person.
        if (o.PersonId is null && !string.IsNullOrWhiteSpace(o.ExternalPersonId))
        {
            var external = o.ExternalPersonId.Trim();
            var mapped = await db.PersonIdentities.Where(x => x.Source == item.Adapter && x.ExternalId == external).Select(x => (int?)x.PersonId).SingleOrDefaultAsync();
            if (mapped is null)
            {
                var reason = "UNKNOWN_PERSON:" + item.Adapter + "/" + external;
                if (!await db.Alerts.AnyAsync(x => x.Reason == reason && !x.Resolved)) db.Alerts.Add(new() { Reason = reason, Severity = "Medium" });
                note = "unmapped person " + item.Adapter + "/" + external;
            }
            o = o with { PersonId = mapped };
        }
        var plate = VietnamPlate.Normalize(o.Plate);
        // GC-24 merge: the same plate on the same lane within the merge window is one pass, not a new event.
        var window = TimeSpan.FromSeconds((await db.Site()).MergeWindowSeconds);
        if (plate.Length > 0 || o.PersonId.HasValue)
        {
            var since = now - window;
            // A PPE refusal is not merged: the worker fixes the missing item and is read again straight away.
            var duplicate = await db.Events.AnyAsync(x => x.LaneId == camera.LaneId && x.CreatedUtc >= since && !x.Reason.StartsWith("PPE_")
                && (plate.Length > 0 ? x.Plate == plate : x.PersonId == o.PersonId));
            if (duplicate) return note.Length > 0 ? note + ", merged into recent event" : "merged into recent event";
        }
        // Without a verified confidence the result can only suggest; an admin sets TrustedConfidence after field evaluation.
        var confidence = camera.TrustedConfidence ?? 0;
        if (o.Confidence is double reported && camera.TrustedConfidence is not null) confidence = Math.Min(reported, camera.TrustedConfidence.Value);
        var recognition = new Recognition(plate, plate.Length > 0 ? confidence : 0, o.PersonId, o.PersonId.HasValue ? confidence : 0, item.AdapterVersion + ":" + camera.Id);
        var evidence = o.PlateImage is { } plateImage ? service.SaveEvidenceBytes(plateImage) : o.OverviewImage is { } only ? service.SaveEvidenceBytes(only) : null;
        var captured = o.CapturedUtc is DateTime c && Math.Abs((c - item.ReceivedUtc).TotalHours) < 24 ? c : (DateTime?)null;
        var entry = await service.Ingest(camera.LaneId, "rx:" + item.Id + ":" + o.RawKey, recognition, "camera:" + camera.Id, "ThirdPartyCamera", evidence, item.ReceivedUtc, captured);
        if (o.OverviewImage is { } overview && o.PlateImage is not null && service.SaveEvidenceBytes(overview) is string overviewId)
        { db.EvidenceImages.Add(new EvidenceImage { EventId = entry.Id, FileId = overviewId, Kind = "Overview", Sha256 = EvidenceImage.Hash(overview), TakenBy = "camera:" + camera.Id }); }
        return note;
    }
    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
