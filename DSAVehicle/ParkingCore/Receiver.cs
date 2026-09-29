using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Third-party AI camera input (Plan V2 Phase 2, docs/THIRD_PARTY_CAMERA_PLAN.md).
// Flow: camera → receiver (store, then ACK) → inbox worker → adapter → ParkingService.Ingest. A camera message is evidence, never a command.

public class CameraInbox
{
    public long Id { get; set; }
    [MaxLength(40)] public string Adapter { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string RemoteIp { get; set; } = "";
    [MaxLength(64)] public string PayloadSha256 { get; set; } = "";
    // Cleared after processing + retention so base64 images don't stay in the database.
    public string? Payload { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    [MaxLength(40)] public string? AdapterVersion { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public int Observations { get; set; }
}

// Vendor-neutral observation every adapter produces (GC-05).
// ExternalPersonId: the face device's own ID, resolved to PersonId through PersonIdentity by the worker (CR-015).
public sealed record ExternalObservation(string DeviceId, string Channel, string Plate, int? PersonId, double? Confidence,
    DateTime? CapturedUtc, byte[]? PlateImage, byte[]? OverviewImage, string RawKey, string? ExternalPersonId = null);

public interface ICameraAdapter
{
    string Name { get; }
    string Version { get; }
    // Body format the receiver accepts for this adapter: json, or xml (device profiles).
    string Format => "json";
    // Returns the observations in the message; an empty list means a message with nothing to decide (e.g. alarm-only).
    IReadOnlyList<ExternalObservation> Parse(JsonElement root);
}

// T1550FAI-Z plate camera, format from the ../tcp capture. SnapId is the plate candidate; the camera sends no confidence.
public sealed class T1550Adapter : ICameraAdapter
{
    public string Name => "t1550";
    public string Version => "t1550/2026-09-25";
    public IReadOnlyList<ExternalObservation> Parse(JsonElement root)
    {
        var data = root.GetProperty("data");
        var device = data.TryGetProperty("dev_net_info", out var net) && net.ValueKind == JsonValueKind.Array && net.GetArrayLength() > 0
            ? Normalize(Str(net[0], "mac")) : "";
        var list = new List<ExternalObservation>();
        if (!data.TryGetProperty("ai_snap_picture", out var ai) || !ai.TryGetProperty("PlateInfo", out var plates) || plates.ValueKind != JsonValueKind.Array) return list;
        var index = 0;
        foreach (var plate in plates.EnumerateArray())
        {
            // StartTime looks like Unix seconds in the capture; only trusted when close to "now", otherwise unknown.
            DateTime? captured = plate.TryGetProperty("StartTime", out var start) && start.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
            list.Add(new(device, Str(plate, "StrChn"), Str(plate, "SnapId"), null, null, captured,
                Image(Str(plate, "PlateImg")), Image(Str(plate, "BgImg")), $"{device}/{Str(plate, "GrpId")}/{index++}"));
        }
        return list;
    }
    internal static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    internal static string Normalize(string mac) => new(mac.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    internal static byte[]? Image(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 8_000_000) return null;
        var comma = base64.IndexOf(',');
        if (base64.StartsWith("data:") && comma > 0) base64 = base64[(comma + 1)..];
        try { return Convert.FromBase64String(base64); } catch (FormatException) { return null; }
    }
}

// Common JSON for any other device (and for integrators): {"deviceId","channel","plate"?,"personId"?,"confidence"?,"capturedUtc"?,"plateImage"?,"overviewImage"?,"id"?}
public sealed class GenericAdapter : ICameraAdapter
{
    public string Name => "generic";
    public string Version => "generic/1";
    public IReadOnlyList<ExternalObservation> Parse(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        return items.Select((x, i) => new ExternalObservation(T1550Adapter.Normalize(T1550Adapter.Str(x, "deviceId")), T1550Adapter.Str(x, "channel"), T1550Adapter.Str(x, "plate"),
            x.TryGetProperty("personId", out var p) && p.TryGetInt32(out var person) ? person : null,
            x.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var conf) ? conf : null,
            x.TryGetProperty("capturedUtc", out var t) && t.TryGetDateTime(out var at) ? at.ToUniversalTime() : null,
            T1550Adapter.Image(T1550Adapter.Str(x, "plateImage")), T1550Adapter.Image(T1550Adapter.Str(x, "overviewImage")),
            T1550Adapter.Str(x, "id") is { Length: > 0 } id ? id : i.ToString())).ToList();
    }
}

public sealed class CameraAdapters(IEnumerable<ICameraAdapter> adapters)
{
    public ICameraAdapter? Find(string name) => adapters.FirstOrDefault(x => x.Name == name);
}

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

public sealed record ReceiverOptions(string? Key, int? Port, string PortAdapter, string[] AllowedIps)
{
    public static ReceiverOptions From(IConfiguration c) => new(c["Receiver:Key"], c.GetValue<int?>("Receiver:Port"), c["Receiver:PortAdapter"] ?? "t1550",
        (c["Receiver:AllowedIps"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { Ports = ParsePorts(c) };
    // Port → adapter for devices that can only post to "host:port/api": Receiver:Port/PortAdapter plus Receiver:Ports = "2124=hik-anpr;2125=zk-face" (CR-015).
    public IReadOnlyDictionary<int, string> Ports { get; init; } = new Dictionary<int, string>();
    public static Dictionary<int, string> ParsePorts(IConfiguration c)
    {
        var ports = new Dictionary<int, string>();
        if (c.GetValue<int?>("Receiver:Port") is int legacy) ports[legacy] = c["Receiver:PortAdapter"] ?? "t1550";
        foreach (var entry in (c["Receiver:Ports"] ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var port) && port is > 0 and < 65536 && parts[1].Length > 0) ports[port] = parts[1];
        }
        return ports;
    }
}

[ApiController, AllowAnonymous]
public class ReceiverController(InboxWriter inbox, IConfiguration config) : ControllerBase
{
    // Preferred: http://station/api/v1/parking/receiver/{adapter}?key=<Receiver:Key> (key ≥ 32 characters).
    [HttpPost("api/v1/parking/receiver/{adapter}"), RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> Receive(string adapter, [FromQuery] string? key)
    {
        var options = ReceiverOptions.From(config);
        if (string.IsNullOrEmpty(options.Key) || options.Key.Length < 32 || key is null
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(options.Key)), SHA256.HashData(Encoding.UTF8.GetBytes(key)))) return Unauthorized();
        return await Store(adapter);
    }

    // For cameras that can only post to "host:port/api" (T1550 capture). Served only on a receiver port and only to Receiver:AllowedIps.
    [HttpPost("api"), RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> ReceiveOnPort()
    {
        var options = ReceiverOptions.From(config);
        if (!options.Ports.TryGetValue(HttpContext.Connection.LocalPort, out var adapter)) return NotFound();
        if (!IpAllowed(HttpContext.Connection.RemoteIpAddress, options.AllowedIps)) return StatusCode(403);
        return await Store(adapter);
    }

    public static bool IpAllowed(IPAddress? remote, string[] allowed)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        foreach (var entry in allowed)
        {
            var parts = entry.Split('/');
            if (!IPAddress.TryParse(parts[0], out var network)) continue;
            if (parts.Length == 1) { if (network.Equals(remote)) return true; continue; }
            if (!int.TryParse(parts[1], out var bits) || network.AddressFamily != remote.AddressFamily) continue;
            var a = network.GetAddressBytes(); var b = remote.GetAddressBytes();
            var match = true;
            for (var i = 0; i < a.Length && match; i++)
            {
                var take = Math.Clamp(bits - i * 8, 0, 8);
                var mask = (byte)(0xFF << (8 - take));
                match = (a[i] & mask) == (b[i] & mask);
            }
            if (match) return true;
        }
        return false;
    }

    private async Task<IActionResult> Store(string adapterName)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        // Stored (or already stored: a camera retry) before the ACK; see InboxWriter.
        return await inbox.Store(adapterName, body, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "") switch
        {
            InboxOutcome.UnknownAdapter => NotFound(new { message = "Unknown adapter." }),
            InboxOutcome.BadBody => BadRequest(new { message = "Body must be JSON (or XML for an XML device profile)." }),
            _ => Ok(new { result = "ok" })
        };
    }
}

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
                    foreach (var (device, channel, reading) in PpeMessage.Parse(document.RootElement, item.ReceivedUtc))
                    {
                        var ppeCamera = await db.Cameras.SingleOrDefaultAsync(x => x.ExternalDevice == device && (x.ExternalChannel == null || x.ExternalChannel == "" || x.ExternalChannel == channel), stop);
                        if (ppeCamera?.LaneId is null) continue;
                        ppeCamera.LastMessageUtc = now; cache.Record(ppeCamera.LaneId, reading); readings++;
                    }
                    item.Observations = readings; item.Status = readings > 0 ? "Processed" : "Ignored";
                    item.Error = readings > 0 ? null : "PPE message from an unmapped device";
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
