using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// GC-37: an account limited to some areas. No rows = all areas.
public class UserArea
{
    public Guid UserId { get; set; }
    public Guid AreaId { get; set; }
}

// Derives from InvalidOperationException so existing catch blocks still stop the action; controllers map it to 403.
public sealed class AreaForbiddenException() : InvalidOperationException("This lane is outside your areas.");

public sealed class AreaScope(ParkingDb db, IHttpContextAccessor http)
{
    // Site-wide configuration cannot be split by area, so these need an account without area limits.
    public static readonly string[] UnscopedOnly = [Perm.ManageSite, Perm.ManageUsers];

    private bool loaded;
    private HashSet<string>? lanes;

    // Lane IDs the user may see and act on, or null when the account has no area limit.
    public static async Task<HashSet<string>?> LanesFor(ParkingDb db, Guid userId, string? role)
    {
        if (role == RoleCatalog.Admin) return null;
        var areas = await AreasOf(db, userId);
        if (areas.Count == 0) return null;
        return (await db.Lanes.Where(l => db.Locations.Any(loc => loc.Id == l.LocationId && areas.Contains(loc.AreaId))).Select(l => l.Id).ToListAsync()).ToHashSet();
    }
    public static Task<List<Guid>> AreasOf(ParkingDb db, Guid userId) => db.UserAreas.Where(x => x.UserId == userId).Select(x => x.AreaId).ToListAsync();

    public static async Task SetAreas(ParkingDb db, ParkingUser user, Guid[] areaIds)
    {
        var wanted = areaIds.Distinct().ToHashSet();
        if (wanted.Count > 0 && user.Role == RoleCatalog.Admin) throw new InvalidOperationException("Administrator accounts cannot be limited to areas.");
        if (await db.Areas.CountAsync(x => wanted.Contains(x.Id)) != wanted.Count) throw new InvalidOperationException("Area not found.");
        var current = await db.UserAreas.Where(x => x.UserId == user.Id).ToListAsync();
        db.UserAreas.RemoveRange(current.Where(x => !wanted.Contains(x.AreaId)));
        db.UserAreas.AddRange(wanted.Where(a => current.All(c => c.AreaId != a)).Select(a => new UserArea { UserId = user.Id, AreaId = a }));
    }

    public async Task<HashSet<string>?> Lanes()
    {
        if (loaded) return lanes;
        loaded = true;
        var user = http.HttpContext?.User;
        // A signed-in user without a readable ID is treated as limited to nothing rather than unlimited.
        if (user is null || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return lanes = [];
        return lanes = await LanesFor(db, id, user.FindFirstValue(ClaimTypes.Role));
    }
    // Area IDs the user may see, or null when unlimited.
    public async Task<List<Guid>?> Areas()
    {
        var user = http.HttpContext?.User;
        if (user is null || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return [];
        if (user.IsInRole(RoleCatalog.Admin)) return null;
        var areas = await AreasOf(db, id);
        return areas.Count == 0 ? null : areas;
    }
    public async Task<bool> Allows(string? laneId) => await Lanes() is not { } allowed || (laneId is not null && allowed.Contains(laneId));
    public async Task Ensure(string? laneId) { if (!await Allows(laneId)) throw new AreaForbiddenException(); }
    public async Task EnsureEvent(Guid eventId) => await Ensure(await db.Events.Where(x => x.Id == eventId).Select(x => x.LaneId).SingleOrDefaultAsync());
    public async Task EnsureCommand(Guid commandId) => await Ensure(await db.Commands.Where(x => x.Id == commandId).Select(x => x.LaneId).SingleOrDefaultAsync());

    // Lane of an alert: its event's lane, else the lane named after the colon (DEVICE_OFFLINE:<lane>). Null for site-wide alerts.
    public static string? AlertLane(ParkingAlert alert, IReadOnlyDictionary<Guid, string> eventLanes) =>
        alert.EventId is Guid e && eventLanes.TryGetValue(e, out var lane) ? lane : alert.Reason.Contains(':') ? alert.Reason[(alert.Reason.IndexOf(':') + 1)..] : null;
    public async Task EnsureAlert(Guid alertId)
    {
        if (await Lanes() is null) return;
        var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == alertId);
        if (alert is null) return;
        var eventLanes = alert.EventId is Guid e ? await db.Events.Where(x => x.Id == e).ToDictionaryAsync(x => x.Id, x => x.LaneId) : new Dictionary<Guid, string>();
        await Ensure(AlertLane(alert, eventLanes));
    }
}
