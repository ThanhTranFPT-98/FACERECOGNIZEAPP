using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Station plan §4.1 (CR-017): with Sync:MasterSource = Cloud the master list (people, face IDs, vehicles, shifts) comes from
// the FPT Cloud platform. The station pulls (the cloud never connects in), applies a whole version in one transaction, then
// acknowledges it. personId is shared with the cloud, so decisions keep using the same IDs. Contract: dsa.station/1 DRAFT.

public static class MasterSource
{
    public const string Cloud = "Cloud";
    public static bool FromCloud(IConfiguration config) => string.Equals(config["Sync:MasterSource"], Cloud, StringComparison.OrdinalIgnoreCase);
}

// On endpoints that change master data: refused while the cloud owns the list, so a local edit is never silently undone by the next pull.
// Visitors and walk-ins stay local (plan §4.1).
public sealed class LocalMasterOnlyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (MasterSource.FromCloud(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()))
            context.Result = new ConflictObjectResult(new { message = "People, vehicles, face IDs and shifts are managed on the FPT Cloud platform (Sync:MasterSource = Cloud)." });
    }
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

public sealed record MasterMessage(long Version, bool Full, List<MasterPersonMessage> People, List<MasterVehicleMessage> Vehicles,
    List<MasterShiftMessage> Shifts, MasterDeletedMessage Deleted);
public sealed record MasterPersonMessage(int Id, string? Code, string Name, string? Unit, bool Active, DateTime? ValidUntil,
    List<string> AreaCodes, List<MasterFaceIdMessage> FaceIds);
public sealed record MasterFaceIdMessage(string Source, string ExternalId);
public sealed record MasterVehicleMessage(string Plate, string Kind, string Category, List<int> PersonIds, DateTime? ValidUntil, bool Blocked, bool Active);
public sealed record MasterShiftMessage(string AreaCode, string Name, string Start, string End);
public sealed record MasterDeletedMessage(List<int> People, List<string> Vehicles);
public sealed record MasterCounts(int People, int Vehicles, int Shifts);

public sealed class MasterSync(IServiceScopeFactory scopes, IConfiguration config, IHttpClientFactory clients, ILogger<MasterSync> logger) : BackgroundService
{
    // SyncCursor row: LastId = applied version, LastKey = "acked" once the platform has the acknowledgement.
    public const string Cursor = "platform:master";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = PlatformOptions.From(config);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.MasterPullSeconds)));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await RunOnceAsync(scope.ServiceProvider.GetRequiredService<ParkingDb>(), clients.CreateClient("platform"), options, DateTime.UtcNow, stop);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested) { logger.LogWarning(ex, "Master pull from the platform failed; the current list stays in use"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    // Returns the version in force after the pass. Any failure before the commit leaves the previous list (and version) in force.
    public static async Task<long?> RunOnceAsync(ParkingDb db, HttpClient http, PlatformOptions options, DateTime now, CancellationToken stop = default)
    {
        var station = Uri.EscapeDataString(options.StationId!);
        var cursor = await db.SyncCursors.FindAsync([Cursor], stop);
        var since = cursor?.LastId;
        var message = await http.GetFromJsonAsync<MasterMessage>($"api/v1/stations/{station}/master" + (since is long s ? "?since=" + s : ""), stop)
            ?? throw new InvalidDataException("Empty master answer from the platform.");
        if (!message.Full && message.Version == since)
        {
            // Up to date. An acknowledgement that failed last time is sent again, so the platform's fleet view catches up.
            if (cursor!.LastKey != "acked" && await Ack(http, station, message.Version, now, await Counts(db, stop), stop)) { cursor.LastKey = "acked"; await db.SaveChangesAsync(stop); }
            return since;
        }
        MasterCounts counts;
        await using (var transaction = await db.Database.BeginTransactionAsync(stop))
        {
            await Apply(db, message, stop);
            cursor ??= db.SyncCursors.Add(new SyncCursor { Peer = Cursor }).Entity;
            cursor.LastId = message.Version; cursor.LastKey = null;
            db.Log("platform", $"MasterApplied:v{message.Version}:{(message.Full ? "full" : "changes")}", message.People.Count + " people");
            await db.SaveChangesAsync(stop);
            counts = await Counts(db, stop);
            await transaction.CommitAsync(stop);
        }
        if (await Ack(http, station, message.Version, now, counts, stop)) { cursor.LastKey = "acked"; await db.SaveChangesAsync(stop); }
        return message.Version;
    }

    private static async Task Apply(ParkingDb db, MasterMessage m, CancellationToken stop)
    {
        // A full list replaces everything; a change list touches the listed and deleted entries only.
        var listed = m.People.Select(p => p.Id).ToHashSet();
        var deleted = m.Deleted.People.ToHashSet();
        var people = await (m.Full ? db.People : db.People.Where(x => listed.Contains(x.PersonId) || deleted.Contains(x.PersonId))).ToDictionaryAsync(x => x.PersonId, stop);
        foreach (var p in m.People)
        {
            if (p.Id <= 0) throw new InvalidDataException("Person IDs must be positive integers: " + p.Id);
            if (!people.TryGetValue(p.Id, out var row)) { row = new AuthorizedPerson { PersonId = p.Id }; db.People.Add(row); people[p.Id] = row; }
            row.Name = p.Name; row.Code = p.Code; row.Active = p.Active; row.ValidUntilUtc = p.ValidUntil;
        }
        var goneIds = (m.Full ? people.Keys.Except(listed) : deleted.Where(people.ContainsKey)).ToList();
        db.People.RemoveRange(goneIds.Select(id => people[id]));

        // Face IDs. A moved one is re-pointed (not deleted and re-added) because (Source, ExternalId) is unique.
        // ToDictionary throws on a face ID given to two people, so such a list is refused as a whole.
        var wanted = m.People.SelectMany(p => p.FaceIds.Select(f => (f.Source, f.ExternalId, Person: p.Id))).ToDictionary(x => (x.Source, x.ExternalId), x => x.Person);
        var affected = listed.Concat(deleted).ToHashSet();
        var sources = wanted.Keys.Select(k => k.Source).Distinct().ToList();
        var externals = wanted.Keys.Select(k => k.ExternalId).Distinct().ToList();
        var identities = await (m.Full ? db.PersonIdentities
            : db.PersonIdentities.Where(x => affected.Contains(x.PersonId) || (sources.Contains(x.Source) && externals.Contains(x.ExternalId)))).ToListAsync(stop);
        foreach (var row in identities)
        {
            if (wanted.Remove((row.Source, row.ExternalId), out var owner)) row.PersonId = owner;
            else if (m.Full || affected.Contains(row.PersonId)) db.PersonIdentities.Remove(row);
        }
        db.PersonIdentities.AddRange(wanted.Select(w => new PersonIdentity { Source = w.Key.Source, ExternalId = w.Key.ExternalId, PersonId = w.Value }));

        // Vehicles. The first person is the owner, the others share it (VehiclePeople): the policy accepts any of them.
        var plates = m.Vehicles.Select(v => VietnamPlate.Normalize(v.Plate)).ToHashSet();
        var removed = m.Deleted.Vehicles.Select(VietnamPlate.Normalize).ToHashSet();
        var vehicles = await (m.Full ? db.Vehicles : db.Vehicles.Where(x => plates.Contains(x.NormalizedPlate) || removed.Contains(x.NormalizedPlate)))
            .Include(x => x.AuthorizedPeople).ToDictionaryAsync(x => x.NormalizedPlate, stop);
        var owners = m.Vehicles.Where(v => v.PersonIds.Count > 0).Select(v => v.PersonIds[0]).Distinct().ToList();
        var names = await db.People.AsNoTracking().Where(x => owners.Contains(x.PersonId)).ToDictionaryAsync(x => x.PersonId, x => x.Name, stop);
        foreach (var p in m.People) names[p.Id] = p.Name;
        foreach (var v in m.Vehicles)
        {
            var plate = VietnamPlate.Normalize(v.Plate);
            if (!vehicles.TryGetValue(plate, out var row)) { row = new RegisteredVehicle { NormalizedPlate = plate }; db.Vehicles.Add(row); vehicles[plate] = row; }
            row.Plate = v.Plate; row.Kind = v.Kind; row.Category = v.Category; row.Active = v.Active; row.Blocked = v.Blocked; row.ValidUntilUtc = v.ValidUntil;
            row.PersonId = v.PersonIds.Count > 0 ? v.PersonIds[0] : null;
            row.Owner = row.PersonId is int owner && names.TryGetValue(owner, out var name) ? name : "";
            var others = v.PersonIds.Skip(1).ToHashSet();
            foreach (var link in row.AuthorizedPeople.Where(x => !others.Contains(x.PersonId)).ToList()) { row.AuthorizedPeople.Remove(link); db.VehiclePeople.Remove(link); }
            foreach (var id in others.Where(id => row.AuthorizedPeople.All(x => x.PersonId != id))) row.AuthorizedPeople.Add(new VehiclePerson { PersonId = id });
        }
        var gonePlates = (m.Full ? vehicles.Keys.Except(plates) : removed.Where(vehicles.ContainsKey)).ToList();
        db.Vehicles.RemoveRange(gonePlates.Select(p => vehicles[p]));

        // Shifts: the cloud always sends all of them; the station keeps those of its own areas.
        var areas = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Id, stop);
        db.Shifts.RemoveRange(await db.Shifts.ToListAsync(stop));
        db.Shifts.AddRange(m.Shifts.Where(s => areas.ContainsKey(s.AreaCode)).Select(s => new Shift { AreaId = areas[s.AreaCode], Name = s.Name, Start = s.Start, End = s.End }));
    }

    private static async Task<MasterCounts> Counts(ParkingDb db, CancellationToken stop) =>
        new(await db.People.CountAsync(stop), await db.Vehicles.CountAsync(stop), await db.Shifts.CountAsync(stop));

    private static async Task<bool> Ack(HttpClient http, string station, long version, DateTime now, MasterCounts counts, CancellationToken stop)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"api/v1/stations/{station}/master/ack",
                new { version, appliedUtc = now, counts = new { people = counts.People, vehicles = counts.Vehicles, shifts = counts.Shifts } }, stop);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }
}
