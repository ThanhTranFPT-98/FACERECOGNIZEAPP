using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// "Who is inside" (GC-18/19/20) across stations (Plan V2 §3.8, option E).
// One row per sensor-confirmed passage. Rows from other stations are copied in by EventId, so the same passage is never counted twice.
public class PresenceRecord
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    [MaxLength(60)] public string StationId { get; set; } = "";
    // Area code, not ID: area IDs differ per station database, codes are agreed per site.
    [MaxLength(40)] public string AreaCode { get; set; } = "";
    [MaxLength(60)] public string SubjectKey { get; set; } = "";   // "P:<personId>" or "V:<plate>"
    [MaxLength(100)] public string SubjectName { get; set; } = "";
    [MaxLength(10)] public string Direction { get; set; } = "In";   // In | Out
    public DateTime PassageUtc { get; set; }
    [MaxLength(40)] public string LaneId { get; set; } = "";
    [MaxLength(60)] public string? Shift { get; set; }
    // Sensor = normal passage; Manual = a guard closed a stale presence (with a note).
    [MaxLength(20)] public string Source { get; set; } = "Sensor";
    [MaxLength(300)] public string? Note { get; set; }
}
public class Shift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    [MaxLength(60)] public string Name { get; set; } = "";
    [MaxLength(5)] public string Start { get; set; } = "06:00";   // local time, HH:mm
    [MaxLength(5)] public string End { get; set; } = "14:00";     // may be earlier than Start (night shift)
}
public class SyncCursor
{
    [MaxLength(300)] public string Peer { get; set; } = "";
    public long LastId { get; set; }
    [MaxLength(300)] public string? LastKey { get; set; }
}

public sealed record InsideEntry(string AreaCode, string SubjectKey, string SubjectName, DateTime SinceUtc, string LaneId, string StationId, string? Shift, bool DoubleEntry);
public sealed record UnmatchedExit(string AreaCode, string SubjectKey, string SubjectName, DateTime PassageUtc, string LaneId, string StationId);

public static class PresenceBook
{
    public static string StationId(IConfiguration config) => config["Parking:StationId"] is { Length: > 0 } id ? id : Environment.MachineName;

    // Replays passages in time order (not arrival order), per area and subject.
    public static (List<InsideEntry> Inside, List<UnmatchedExit> Unmatched) Compute(IEnumerable<PresenceRecord> records)
    {
        var inside = new Dictionary<(string, string), InsideEntry>();
        var unmatched = new List<UnmatchedExit>();
        foreach (var r in records.OrderBy(x => x.PassageUtc).ThenBy(x => x.Direction == "In" ? 0 : 1))
        {
            var key = (r.AreaCode, r.SubjectKey);
            if (r.Direction == "In")
                inside[key] = new(r.AreaCode, r.SubjectKey, r.SubjectName, r.PassageUtc, r.LaneId, r.StationId, r.Shift, inside.ContainsKey(key));
            else if (!inside.Remove(key) && r.Source != "Manual")
                unmatched.Add(new(r.AreaCode, r.SubjectKey, r.SubjectName, r.PassageUtc, r.LaneId, r.StationId));
        }
        return (inside.Values.OrderBy(x => x.SinceUtc).ToList(), unmatched);
    }

    public static string? ShiftAt(IEnumerable<Shift> shifts, DateTime localTime)
    {
        var minute = localTime.Hour * 60 + localTime.Minute;
        foreach (var s in shifts)
        {
            if (!TryMinutes(s.Start, out var start) || !TryMinutes(s.End, out var end)) continue;
            if (start <= end ? minute >= start && minute < end : minute >= start || minute < end) return s.Name;
        }
        return null;
    }
    public static bool TryMinutes(string hhmm, out int minutes)
    {
        minutes = 0;
        return TimeOnly.TryParseExact(hhmm, "HH:mm", out var t) && (minutes = t.Hour * 60 + t.Minute) >= 0;
    }

    // Called by ParkingService.ConfirmPassage (caller holds the lock and saves).
    public static async Task Record(ParkingDb db, IConfiguration config, Lane lane, AccessEvent entry, DateTime passageUtc)
    {
        var area = await db.Locations.Where(l => l.Id == lane.LocationId).Join(db.Areas, l => l.AreaId, a => a.Id, (l, a) => a).SingleOrDefaultAsync();
        if (area is null) return;
        string key, name;
        if (entry.PersonId is int person && (lane.Subject == "Person" || entry.Plate.Length == 0))
        { key = "P:" + person; name = (await db.People.FindAsync(person))?.Name ?? "Person " + person; }
        else if (entry.Plate.Length > 0)
        { key = "V:" + entry.Plate; name = (await db.Vehicles.Where(v => v.NormalizedPlate == entry.Plate).Select(v => v.Owner).FirstOrDefaultAsync()) is { Length: > 0 } owner ? entry.Plate + " · " + owner : entry.Plate; }
        else return;
        var site = await db.Site();
        var local = TimeZoneInfo.ConvertTimeFromUtc(passageUtc, TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone));
        var shifts = await db.Shifts.Where(s => s.AreaId == area.Id).ToListAsync();
        db.Presence.Add(new PresenceRecord { EventId = entry.Id, StationId = StationId(config), AreaCode = area.Code, SubjectKey = key, SubjectName = name,
            Direction = lane.Direction == "Exit" ? "Out" : "In", PassageUtc = passageUtc, LaneId = lane.Id, Shift = ShiftAt(shifts, local) });
    }

    // Inserts records from another station; already known EventIds are skipped (idempotent over LAN + cloud).
    public static async Task<int> Receive(ParkingDb db, IEnumerable<PresenceRecord> incoming, string localStation)
    {
        var list = incoming.Where(x => x.EventId != Guid.Empty && x.StationId != localStation).GroupBy(x => x.EventId).Select(g => g.First()).ToList();
        var ids = list.Select(x => x.EventId).ToList();
        var known = (await db.Presence.Where(x => ids.Contains(x.EventId)).Select(x => x.EventId).ToListAsync()).ToHashSet();
        var added = 0;
        foreach (var r in list.Where(x => !known.Contains(x.EventId)))
        {
            db.Presence.Add(new PresenceRecord { EventId = r.EventId, StationId = r.StationId, AreaCode = r.AreaCode, SubjectKey = r.SubjectKey, SubjectName = r.SubjectName,
                Direction = r.Direction == "Out" ? "Out" : "In", PassageUtc = r.PassageUtc, LaneId = r.LaneId, Shift = r.Shift, Source = r.Source, Note = r.Note });
            added++;
        }
        return added;
    }

    public static IQueryable<PresenceRecord> Window(ParkingDb db, DateTime now, int lookbackDays = 7)
    {
        var since = now.AddDays(-lookbackDays);
        return db.Presence.AsNoTracking().Where(x => x.PassageUtc >= since);
    }
}

public sealed record SyncOptions(string? Key, string[] Peers, string[] CloudPeers)
{
    public static SyncOptions From(IConfiguration c) => new(c["Sync:Key"],
        (c["Sync:Peers"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        (c["Sync:CloudPeers"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public class ShiftInput
{
    public Guid? Id { get; set; }
    public Guid AreaId { get; set; }
    [Required, StringLength(60)] public string Name { get; set; } = "";
    [Required, RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")] public string Start { get; set; } = "06:00";
    [Required, RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")] public string End { get; set; } = "14:00";
}
public class CloseInput
{
    [Required, StringLength(40)] public string AreaCode { get; set; } = "";
    [Required, StringLength(60)] public string SubjectKey { get; set; } = "";
    [Required, StringLength(300, MinimumLength = 3)] public string Note { get; set; } = "";
}

[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewLive)]
public class PresenceController(ParkingDb db, AreaScope scope, ParkingLock mutex, IConfiguration config) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    // Who is inside each visible area now, plus exits that had no entry (missing passages must be visible, GC-18).
    [HttpGet("presence")]
    public async Task<object> Presence()
    {
        var visible = await scope.Areas();
        var areas = await db.Areas.AsNoTracking().Where(a => visible == null || visible.Contains(a.Id)).ToListAsync();
        var codes = areas.Select(a => a.Code).ToList();
        var now = DateTime.UtcNow;
        var (inside, unmatched) = PresenceBook.Compute(await PresenceBook.Window(db, now).Where(x => codes.Contains(x.AreaCode)).ToListAsync());
        return new {
            station = PresenceBook.StationId(config),
            areas = areas.Select(a => new { a.Id, a.Code, a.Name, a.Kind, a.OverstayMinutes, count = inside.Count(x => x.AreaCode == a.Code) }),
            inside = inside.Select(x => new { x.AreaCode, x.SubjectKey, x.SubjectName, x.SinceUtc, x.LaneId, x.StationId, x.Shift, x.DoubleEntry,
                minutes = (int)(now - x.SinceUtc).TotalMinutes,
                overstay = areas.FirstOrDefault(a => a.Code == x.AreaCode)?.OverstayMinutes is int limit && (now - x.SinceUtc).TotalMinutes > limit }),
            unmatched = unmatched.OrderByDescending(x => x.PassageUtc).Take(100)
        };
    }

    // A guard closes a presence that will never get its exit (e.g. exit through an unmonitored gate). Audited, never silent.
    [HttpPost("presence/close"), Authorize(Policy = Perm.HandleAlerts)]
    public async Task<IActionResult> Close(CloseInput input)
    {
        var area = await db.Areas.SingleOrDefaultAsync(x => x.Code == input.AreaCode);
        if (area is null) return NotFound();
        var visible = await scope.Areas();
        if (visible is not null && !visible.Contains(area.Id)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        await mutex.Gate.WaitAsync();
        try
        {
            db.Presence.Add(new PresenceRecord { EventId = Guid.NewGuid(), StationId = PresenceBook.StationId(config), AreaCode = area.Code, SubjectKey = input.SubjectKey,
                SubjectName = input.SubjectKey, Direction = "Out", PassageUtc = DateTime.UtcNow, Source = "Manual", Note = Actor + ": " + input.Note.Trim() });
            db.Log(Actor, "ClosePresence:" + input.Note.Trim(), input.AreaCode + "/" + input.SubjectKey);
            await db.SaveChangesAsync(); return Ok(new { closed = true });
        }
        finally { mutex.Gate.Release(); }
    }

    [HttpGet("shifts")]
    public async Task<object> Shifts() => await db.Shifts.AsNoTracking().OrderBy(x => x.AreaId).ThenBy(x => x.Start).ToListAsync();
    [HttpPut("shifts"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> SaveShift(ShiftInput input)
    {
        if (!await db.Areas.AnyAsync(x => x.Id == input.AreaId)) return Conflict(new { message = "Area not found." });
        var shift = input.Id.HasValue ? await db.Shifts.FindAsync(input.Id.Value) : null;
        if (shift is null) { shift = new Shift(); db.Shifts.Add(shift); }
        shift.AreaId = input.AreaId; shift.Name = input.Name.Trim(); shift.Start = input.Start; shift.End = input.End;
        db.Log(Actor, "SaveShift", shift.Name); await db.SaveChangesAsync(); return Ok(shift);
    }
    [HttpDelete("shifts/{id:guid}"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> DeleteShift(Guid id)
    {
        var shift = await db.Shifts.FindAsync(id);
        if (shift is null) return NotFound();
        db.Shifts.Remove(shift); db.Log(Actor, "DeleteShift", shift.Name); await db.SaveChangesAsync(); return Ok(shift);
    }
}

// Station-to-station presence exchange over the LAN (option E, fast path). Authenticated with the shared Sync:Key.
[ApiController, AllowAnonymous, Route("api/v1/parking/sync")]
public class SyncController(ParkingDb db, IConfiguration config, ParkingLock mutex) : ControllerBase
{
    [HttpPost("presence"), RequestSizeLimit(4_000_000)]
    public async Task<IActionResult> Presence(List<PresenceRecord> records)
    {
        var key = SyncOptions.From(config).Key;
        var provided = Request.Headers["X-Sync-Key"].ToString();
        if (string.IsNullOrEmpty(key) || key.Length < 32 || provided.Length == 0
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(provided)))) return Unauthorized();
        if (records.Count > 1000) return BadRequest(new { message = "At most 1000 records per call." });
        await mutex.Gate.WaitAsync();
        try { var added = await PresenceBook.Receive(db, records, PresenceBook.StationId(config)); await db.SaveChangesAsync(); return Ok(new { added }); }
        finally { mutex.Gate.Release(); }
    }
}

// Pushes this station's records to LAN peers (every second) and pulls other stations' records from the cloud (every 30 s).
public sealed class PresenceSyncWorker(IServiceScopeFactory scopes, IConfiguration config, IHttpClientFactory clients, ILogger<PresenceSyncWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = SyncOptions.From(config);
        var lastCloud = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ParkingDb>();
                foreach (var peer in options.Peers) await PushOnce(db, clients.CreateClient("parking-devices"), peer, options.Key ?? "", PresenceBook.StationId(config), stop);
                if (options.CloudPeers.Length > 0 && DateTime.UtcNow - lastCloud > TimeSpan.FromSeconds(30) && scope.ServiceProvider.GetService<IObjectStorage>() is { } storage)
                {
                    lastCloud = DateTime.UtcNow;
                    foreach (var site in options.CloudPeers) await PullCloudOnce(db, storage, site, PresenceBook.StationId(config), stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { logger.LogDebug(ex, "Presence sync pass failed; will retry"); }
            catch (Exception ex) { logger.LogError(ex, "Presence sync pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(1), stop).ContinueWith(_ => { });
        }
    }

    public static async Task<int> PushOnce(ParkingDb db, HttpClient http, string peer, string key, string station, CancellationToken stop)
    {
        var cursor = await db.SyncCursors.FindAsync(["lan:" + peer], stop);
        if (cursor is null) { cursor = new SyncCursor { Peer = "lan:" + peer }; db.SyncCursors.Add(cursor); }
        var batch = await db.Presence.AsNoTracking().Where(x => x.Id > cursor.LastId && x.StationId == station).OrderBy(x => x.Id).Take(200).ToListAsync(stop);
        if (batch.Count == 0) return 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, peer.TrimEnd('/') + "/api/v1/parking/sync/presence") { Content = JsonContent.Create(batch, options: Json) };
        request.Headers.Add("X-Sync-Key", key);
        using var response = await http.SendAsync(request, stop);
        response.EnsureSuccessStatusCode();
        cursor.LastId = batch[^1].Id;
        await db.SaveChangesAsync(stop);
        return batch.Count;
    }

    // Reads {site}/presence/… keys after the last one seen. Keys are written in upload-time order, so nothing is skipped.
    public static async Task<int> PullCloudOnce(ParkingDb db, IObjectStorage storage, string site, string station, CancellationToken stop)
    {
        var cursor = await db.SyncCursors.FindAsync(["cloud:" + site], stop);
        if (cursor is null) { cursor = new SyncCursor { Peer = "cloud:" + site }; db.SyncCursors.Add(cursor); }
        var keys = await storage.ListAsync(site + "/presence/", cursor.LastKey, 500, stop);
        var records = new List<PresenceRecord>();
        foreach (var key in keys)
        {
            await using var content = await storage.GetAsync(key, stop);
            if (content is null) continue;
            if (await JsonSerializer.DeserializeAsync<PresenceRecord>(content, Json, stop) is { } record) records.Add(record);
        }
        var added = await PresenceBook.Receive(db, records, station);
        if (keys.Count > 0) cursor.LastKey = keys[^1];
        await db.SaveChangesAsync(stop);
        return added;
    }
}
