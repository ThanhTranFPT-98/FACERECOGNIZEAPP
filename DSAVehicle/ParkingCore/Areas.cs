using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class AreaInput
{
    public Guid? Id { get; set; }
    [Required, RegularExpression("^[a-zA-Z0-9_-]{1,40}$")] public string Code { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [RegularExpression("^(Mine|VehicleGate|ParkingLot|Other)$")] public string Kind { get; set; } = "ParkingLot";
    [Range(1, 7 * 24 * 60)] public int? OverstayMinutes { get; set; }
}
public class LocationInput
{
    public Guid? Id { get; set; }
    public Guid AreaId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
}

// Area/location rules live here (not in the controller) so the console tests can run them against an in-memory database.
public static class AreaCatalog
{
    public const string DefaultCode = "DEFAULT";

    // Upgraded sites have lanes but no areas: give them one default area so every lane has a place in the hierarchy.
    public static async Task<Location> EnsureDefault(ParkingDb db)
    {
        var area = await db.Areas.SingleOrDefaultAsync(x => x.Code == DefaultCode);
        if (area is null) { area = new Area { Code = DefaultCode, Name = "Default area", Kind = "Other" }; db.Areas.Add(area); }
        var location = await db.Locations.Where(x => x.AreaId == area.Id).OrderBy(x => x.Name).FirstOrDefaultAsync()
            ?? db.Locations.Local.FirstOrDefault(x => x.AreaId == area.Id);
        if (location is null) { location = new Location { AreaId = area.Id, Name = "Default location" }; db.Locations.Add(location); }
        foreach (var lane in await db.Lanes.Where(x => x.LocationId == null).ToListAsync()) lane.LocationId = location.Id;
        return location;
    }

    public static async Task<Area> SaveArea(ParkingDb db, AreaInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.Areas.AnyAsync(x => x.Code == code && x.Id != input.Id)) throw new InvalidOperationException("Area code is already used.");
        var area = input.Id.HasValue ? await db.Areas.FindAsync(input.Id.Value) ?? throw new InvalidOperationException("Area not found.") : new Area();
        if (area.Code == DefaultCode && code != DefaultCode) throw new InvalidOperationException("The default area code cannot be changed.");
        area.Code = code; area.Name = input.Name.Trim(); area.Kind = input.Kind; area.OverstayMinutes = input.OverstayMinutes;
        if (!input.Id.HasValue) db.Areas.Add(area);
        return area;
    }

    public static async Task<Location> SaveLocation(ParkingDb db, LocationInput input)
    {
        if (!await db.Areas.AnyAsync(x => x.Id == input.AreaId)) throw new InvalidOperationException("Area not found.");
        var name = input.Name.Trim();
        if (await db.Locations.AnyAsync(x => x.AreaId == input.AreaId && x.Name == name && x.Id != input.Id)) throw new InvalidOperationException("This area already has a location with that name.");
        var location = input.Id.HasValue ? await db.Locations.FindAsync(input.Id.Value) ?? throw new InvalidOperationException("Location not found.") : new Location();
        location.AreaId = input.AreaId; location.Name = name;
        if (!input.Id.HasValue) db.Locations.Add(location);
        return location;
    }

    public static async Task<Area> DeleteArea(ParkingDb db, Guid id)
    {
        var area = await db.Areas.FindAsync(id) ?? throw new InvalidOperationException("Area not found.");
        if (area.Code == DefaultCode) throw new InvalidOperationException("The default area cannot be deleted.");
        if (await db.Locations.AnyAsync(x => x.AreaId == id)) throw new InvalidOperationException("Move or delete this area's locations first.");
        // Removing the only area of a limited account would silently make it unlimited.
        if (await db.UserAreas.AnyAsync(x => x.AreaId == id)) throw new InvalidOperationException("Some accounts are limited to this area. Change them first.");
        db.Areas.Remove(area); return area;
    }

    public static async Task<Location> DeleteLocation(ParkingDb db, Guid id)
    {
        var location = await db.Locations.FindAsync(id) ?? throw new InvalidOperationException("Location not found.");
        if (await db.Lanes.AnyAsync(x => x.LocationId == id)) throw new InvalidOperationException("Move this location's lanes to another location first.");
        db.Locations.Remove(location); return location;
    }

    // Lane IDs of one area, as a query so callers can filter events/cameras in the database.
    public static IQueryable<string> LaneIds(ParkingDb db, Guid areaId) =>
        db.Lanes.Where(l => db.Locations.Any(loc => loc.Id == l.LocationId && loc.AreaId == areaId)).Select(l => l.Id);
}

[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewLive)]
public class AreaController(ParkingDb db, ParkingLock mutex, AreaScope scope) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    [HttpGet("areas")]
    public async Task<object> Areas()
    {
        var visible = await scope.Areas();
        var areas = await db.Areas.AsNoTracking().Where(x => visible == null || visible.Contains(x.Id)).OrderBy(x => x.Code).ToListAsync();
        var locations = await db.Locations.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var lanes = await db.Lanes.AsNoTracking().Select(x => new { x.Id, x.LocationId }).ToListAsync();
        return areas.Select(a => new { a.Id, a.Code, a.Name, a.Kind, a.OverstayMinutes,
            locations = locations.Where(l => l.AreaId == a.Id).Select(l => new { l.Id, l.Name, lanes = lanes.Where(x => x.LocationId == l.Id).Select(x => x.Id).OrderBy(x => x) }) });
    }
    [HttpPut("areas"), Authorize(Policy = Perm.ManageSite)]
    public Task<IActionResult> SaveArea(AreaInput input) => Mutate(async () => { var area = await AreaCatalog.SaveArea(db, input); db.Log(Actor, "SaveArea", area.Code); return area; });
    [HttpDelete("areas/{id:guid}"), Authorize(Policy = Perm.ManageSite)]
    public Task<IActionResult> DeleteArea(Guid id) => Mutate(async () => { var area = await AreaCatalog.DeleteArea(db, id); db.Log(Actor, "DeleteArea", area.Code); return area; });
    [HttpPut("locations"), Authorize(Policy = Perm.ManageSite)]
    public Task<IActionResult> SaveLocation(LocationInput input) => Mutate(async () => { var location = await AreaCatalog.SaveLocation(db, input); db.Log(Actor, "SaveLocation", location.Id); return location; });
    [HttpDelete("locations/{id:guid}"), Authorize(Policy = Perm.ManageSite)]
    public Task<IActionResult> DeleteLocation(Guid id) => Mutate(async () => { var location = await AreaCatalog.DeleteLocation(db, id); db.Log(Actor, "DeleteLocation", location.Id); return location; });

    // Same lock as lane changes: a lane must not be moved into a location that is being deleted.
    private async Task<IActionResult> Mutate(Func<Task<object>> action)
    {
        await mutex.Gate.WaitAsync();
        try { var result = await action(); await db.SaveChangesAsync(); return Ok(result); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        finally { mutex.Gate.Release(); }
    }
}
