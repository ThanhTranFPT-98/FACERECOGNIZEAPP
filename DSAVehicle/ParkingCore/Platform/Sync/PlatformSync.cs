using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Station → FPT Cloud management platform (DSA_Platform), contract "dsa.station/1".
// DRAFT: open points are in contracts/CHO_CHOT.md of the platform repository.
// Records leave through the CR-001 outbox; video still goes to S3 (CloudUploader) when Cloud:Enabled.

public sealed class PlatformOptions
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? StationId { get; set; }
    // The station's key, shown once when the station is created on the platform. Keep it in appsettings.Local.json.
    public string? Key { get; set; }
    public int HeartbeatSeconds { get; set; } = 60;
    // With Sync:MasterSource = Cloud: how often the station asks for master changes (plan §4.1).
    public int MasterPullSeconds { get; set; } = 30;
    public int BacklogAlertMinutes { get; set; } = 60;
    public static PlatformOptions From(IConfiguration config) => config.GetSection("Platform").Get<PlatformOptions>() ?? new();
    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(StationId) || Key is not { Length: >= 32 })
            throw new InvalidOperationException("Platform:Url, Platform:StationId and Platform:Key (at least 32 characters) are required when Platform:Enabled is true.");
    }
}

public static class PlatformMessages
{
    public const string Schema = "dsa.station/1";

    // Entity ID + outbox ID: each change of a record is its own item (a reviewed decision is sent again, newest seq wins),
    // and resending the same outbox row is the same item, which the platform stores once.
    public static string ItemId(SyncOutboxItem row) => row.EntityId + "." + row.Id;

    // One outbox row → one contract item, or the reason the row is not sent.
    public static async Task<(object? Item, string? Skip)> ToItem(ParkingDb db, SyncOutboxItem row, CancellationToken stop = default)
    {
        var payload = JsonNode.Parse(row.Payload)!.AsObject();
        string type;
        switch (row.EntityType)
        {
            case nameof(AccessEvent):
                type = "access_event";
                payload["eventId"] = row.EntityId;
                if (row.EvidenceId is not null) payload["evidenceId"] = Path.GetFileName(row.EvidenceId);
                await AddLane(db, payload, stop);
                break;
            case nameof(DeviceCommand):
                if (payload["passageUtc"] is null) return (null, "command without a confirmed passage");
                type = "passage";
                payload["commandId"] = row.EntityId;
                // The platform searches passages by person and plate, so they are copied from the decision.
                var eventId = Guid.Parse(payload["eventId"]!.GetValue<string>());
                if (await db.Events.AsNoTracking().Where(x => x.Id == eventId).Select(x => new { x.PersonId, x.Plate }).SingleOrDefaultAsync(stop) is { } decision)
                { payload["personId"] = decision.PersonId; payload["plate"] = decision.Plate; }
                await AddLane(db, payload, stop);
                break;
            case nameof(PresenceRecord):
                type = "presence";   // already carries laneId, areaCode, direction
                break;
            case nameof(ParkingAlert):
                type = "alert";
                payload["alertId"] = row.EntityId;
                break;
            default:
                return (null, row.EntityType + " is not part of " + Schema);
        }
        MarkUtc(payload);
        var occurred = type switch { "passage" or "presence" => payload["passageUtc"], _ => payload["createdUtc"] };
        return (new { type, id = ItemId(row), seq = row.Id, occurredUtc = occurred?.GetValue<string>(), payload }, null);
    }

    private static async Task AddLane(ParkingDb db, JsonObject payload, CancellationToken stop)
    {
        if (payload["laneId"]?.GetValue<string>() is not { } laneId) return;
        var lane = await db.Lanes.AsNoTracking().Where(x => x.Id == laneId).Select(x => new { x.Direction, x.LocationId }).SingleOrDefaultAsync(stop);
        if (lane is null) return;
        payload["direction"] = lane.Direction == "Exit" ? "Out" : "In";
        if (lane.LocationId is Guid location)
            payload["areaCode"] = await db.Locations.Where(x => x.Id == location).Join(db.Areas, l => l.AreaId, a => a.Id, (l, a) => a.Code).SingleOrDefaultAsync(stop);
    }

    // Times read back from SQLite lose their UTC kind and serialise without "Z"; a receiver would then read them as its own local time.
    private static void MarkUtc(JsonObject payload)
    {
        foreach (var (name, value) in payload.ToList())
            if (name.EndsWith("Utc") && value is JsonValue v && v.TryGetValue(out string? text) && DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) && time.Kind == DateTimeKind.Unspecified)
                payload[name] = text + "Z";
    }
}

public sealed class PlatformUploader(IServiceScopeFactory scopes, IConfiguration config, IWebHostEnvironment environment,
    IHttpClientFactory clients, ILogger<PlatformUploader> logger) : BackgroundService
{
    public const int Batch = 50;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = PlatformOptions.From(config);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var busy = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<ParkingDb>(), clients.CreateClient("platform"), options,
                    EvidenceStorage.Directory(environment, config), DateTime.UtcNow, stop);
                if (!busy) await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Platform upload pass failed"); await Task.Delay(TimeSpan.FromSeconds(15), stop).ContinueWith(_ => { }); }
        }
    }

    // One batch, oldest first: each decision's photo, then the records in one request. Returns true when more rows are waiting.
    // A row is Sent only when the platform lists it as accepted; Rejected rows are kept for inspection and never retried.
    public static async Task<bool> RunOnceAsync(ParkingDb db, HttpClient http, PlatformOptions options, string evidenceDirectory, DateTime now, CancellationToken stop = default)
    {
        var station = Uri.EscapeDataString(options.StationId!);
        var rows = await db.Outbox.Where(x => (x.Status == "Pending" || x.Status == "Failed") && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .OrderBy(x => x.Id).Take(Batch).ToListAsync(stop);
        var items = new List<(SyncOutboxItem Row, object Item)>();
        foreach (var row in rows)
        {
            try
            {
                var (item, skip) = await PlatformMessages.ToItem(db, row, stop);
                if (item is null) { row.Status = "Skipped"; row.SentUtc = now; row.LastError = "Not sent: " + skip; continue; }
                // The photo goes with the first version of a decision only; later versions (a review) reuse it.
                if (row.EvidenceId is not null && !await db.Outbox.AnyAsync(x => x.EntityId == row.EntityId && x.Id < row.Id && x.Status == "Sent", stop))
                    await PutEvidence(db, http, station, Path.Combine(evidenceDirectory, Path.GetFileName(row.EvidenceId)), stop);
                items.Add((row, item));
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { Fail(row, ex.Message, now); }
        }
        if (items.Count > 0)
        {
            try
            {
                using var response = await http.PostAsJsonAsync($"api/v1/stations/{station}/events",
                    new { schema = PlatformMessages.Schema, stationId = options.StationId, items = items.Select(x => x.Item) }, stop);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(stop)}");
                var result = await response.Content.ReadFromJsonAsync<BatchResult>(stop) ?? throw new HttpRequestException("Empty answer from the platform.");
                var accepted = result.Accepted.ToHashSet();
                var rejected = result.Rejected.Where(x => x.Id is not null).GroupBy(x => x.Id!).ToDictionary(g => g.Key, g => g.First().Reason);
                foreach (var (row, _) in items)
                {
                    var id = PlatformMessages.ItemId(row);
                    if (accepted.Contains(id)) { row.Status = "Sent"; row.SentUtc = now; row.LastError = null; }
                    else if (rejected.TryGetValue(id, out var reason)) { row.Status = "Rejected"; row.LastError = Truncate("Rejected by the platform: " + reason); }
                    else Fail(row, "Missing from the platform's answer.", now);
                }
                if (rejected.Count > 0) await ParkingAlerts.RaiseOnce(db, "PLATFORM_REJECTED", "Medium", stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { foreach (var (row, _) in items) Fail(row, ex.Message, now); }
        }
        await db.SaveChangesAsync(stop);
        if (await db.Outbox.AnyAsync(x => (x.Status == "Pending" || x.Status == "Failed") && x.CreatedUtc < now.AddMinutes(-options.BacklogAlertMinutes), stop))
        { await ParkingAlerts.RaiseOnce(db, "UPLOAD_BACKLOG", "High", stop); await db.SaveChangesAsync(stop); }
        await db.Outbox.Where(x => (x.Status == "Sent" || x.Status == "Skipped") && x.SentUtc < now.AddDays(-7)).ExecuteDeleteAsync(stop);
        return rows.Count == Batch;
    }

    private static async Task PutEvidence(ParkingDb db, HttpClient http, string station, string path, CancellationToken stop)
    {
        // A photo lost on the station does not hold the record back: the decision is still evidence of what happened.
        if (!File.Exists(path)) return;
        var data = await File.ReadAllBytesAsync(path, stop);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/stations/{station}/evidence/{Uri.EscapeDataString(Path.GetFileName(path))}") { Content = new ByteArrayContent(data) };
        request.Headers.Add("X-Sha256", Convert.ToHexString(SHA256.HashData(data)));
        using var response = await http.SendAsync(request, stop);
        // 409: the platform holds other content under this ID. Retrying cannot fix that, so it is raised for a person to look at.
        if (response.StatusCode == HttpStatusCode.Conflict) { await ParkingAlerts.RaiseOnce(db, "PLATFORM_EVIDENCE_CONFLICT", "High", stop); return; }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Evidence upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(stop)}");
    }

    private static void Fail(SyncOutboxItem row, string error, DateTime now)
    {
        row.Attempts++; row.Status = "Failed"; row.LastError = Truncate(error);
        row.NextAttemptUtc = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, Math.Min(row.Attempts, 10))));
    }
    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
    private sealed record BatchResult(List<string> Accepted, List<Rejection> Rejected);
    private sealed record Rejection(string? Id, string Reason);
}

// POST …/health every Platform:HeartbeatSeconds. Device states come from what the station already tracks:
// AI cameras by their last message, live lanes by the barrier bridge heartbeat.
public sealed class PlatformHeartbeat(IServiceScopeFactory scopes, IConfiguration config, IWebHostEnvironment environment,
    IHttpClientFactory clients, ILogger<PlatformHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = PlatformOptions.From(config);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, options.HeartbeatSeconds)));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var report = await Report(scope.ServiceProvider.GetRequiredService<ParkingDb>(), environment.ContentRootPath, DateTime.UtcNow, stop);
                    using var response = await clients.CreateClient("platform").PostAsJsonAsync($"api/v1/stations/{Uri.EscapeDataString(options.StationId!)}/health", report, stop);
                    if (!response.IsSuccessStatusCode) logger.LogWarning("Platform refused the heartbeat: {Status}", (int)response.StatusCode);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested) { logger.LogWarning(ex, "Platform heartbeat failed"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public static async Task<object> Report(ParkingDb db, string contentRoot, DateTime now, CancellationToken stop = default)
    {
        var pending = db.Outbox.Where(x => x.Status == "Pending" || x.Status == "Failed");
        var site = await db.Site(stop);
        var cameras = await db.Cameras.AsNoTracking().Where(x => x.Enabled && x.ExternalDevice != null).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.LastMessageUtc, x.SilentAlertSeconds }).ToListAsync(stop);
        var lanes = await db.Lanes.AsNoTracking().Where(x => !x.Simulation).OrderBy(x => x.Id).Select(x => new { x.Id, x.LastHeartbeatUtc }).ToListAsync(stop);
        bool Fresh(DateTime? seen, int seconds) => seen is DateTime at && (now - at).TotalSeconds <= seconds;
        var devices = cameras.Select(c => new { id = c.Id, kind = "Camera", lastSeenUtc = Utc(c.LastMessageUtc), status = Fresh(c.LastMessageUtc, c.SilentAlertSeconds ?? 120) ? "Online" : "Offline" })
            .Concat(lanes.Select(l => new { id = l.Id, kind = "Barrier", lastSeenUtc = Utc(l.LastHeartbeatUtc), status = Fresh(l.LastHeartbeatUtc, site.OfflineAlertSeconds) ? "Online" : "Offline" }))
            .ToList();
        var version = typeof(PlatformHeartbeat).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
        return new
        {
            softwareVersion = "station-" + version,
            // Applied master version (Sync:MasterSource = Cloud); null while the list is managed locally.
            masterVersion = await db.SyncCursors.AsNoTracking().Where(x => x.Peer == MasterSync.Cursor).Select(x => (long?)x.LastId).FirstOrDefaultAsync(stop),
            outboxBacklog = await pending.CountAsync(stop),
            oldestPendingUtc = Utc(await pending.OrderBy(x => x.Id).Select(x => (DateTime?)x.CreatedUtc).FirstOrDefaultAsync(stop)),
            diskFreeGb = Math.Round(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(contentRoot))!).AvailableFreeSpace / 1e9, 1),
            devices
        };
    }
    private static DateTime? Utc(DateTime? value) => value is DateTime v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
