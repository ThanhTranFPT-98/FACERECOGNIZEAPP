using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// CR-031: the screen outside the mine door shows the overview's mine gate board (Cửa lò), read-only.
// The TV has no account: it opens /board?key=… and polls board/state with that key. One key per station,
// kept protected in SiteSettings (BoardKeyProtected, never serialized); no key = the board is off.
// Decision 12/11/2026 (user): the board shows everything the overview shows, names and entry / exit photos included.
public sealed class BoardSeen
{
    public DateTime? Utc { get; set; }
    public string? Ip { get; set; }
}

[ApiController, Route("api/v1/parking/board")]
public class BoardController(ParkingDb db, IDataProtectionProvider protection, IWebHostEnvironment environment, IConfiguration config, BoardSeen seen) : ControllerBase
{
    private const string Purpose = "PublicBoard.Key";
    private const int Hours = 24;
    private string Actor => User.Identity?.Name ?? "unknown";
    private IDataProtector Protector => protection.CreateProtector(Purpose);
    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private async Task<string?> CurrentKey()
    {
        var site = await db.Site();
        if (string.IsNullOrEmpty(site.BoardKeyProtected)) return null;
        try { return Protector.Unprotect(site.BoardKeyProtected); } catch (CryptographicException) { return null; }
    }
    private async Task<bool> Keyed(string? key) => key is { Length: > 0 } && await CurrentKey() is { } expected
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(key));
    private object Link(string? key) => new { key, lastSeenUtc = seen.Utc, lastSeenIp = seen.Ip };

    // The overview's "Show on the outside screen" dialog.
    [HttpGet("link"), Authorize(Policy = Perm.ManageSite)]
    public async Task<object> GetLink() => Link(await CurrentKey());

    // A new key disconnects the TV that used the old one.
    [HttpPost("rekey"), Authorize(Policy = Perm.ManageSite)]
    public Task<object> Rekey() => SetKey(NewKey(), "RekeyBoard");

    [HttpDelete("key"), Authorize(Policy = Perm.ManageSite)]
    public Task<object> TurnOff() => SetKey(null, "TurnOffBoard");

    private async Task<object> SetKey(string? key, string action)
    {
        var site = await db.SiteSettings.FindAsync(1);
        if (site is null) { site = new SiteSettings(); db.SiteSettings.Add(site); }
        site.BoardKeyProtected = key is null ? null : Protector.Protect(key);
        seen.Utc = null; seen.Ip = null;
        db.Log(Actor, action, "board");
        await db.SaveChangesAsync();
        return Link(key);
    }

    // Polled by the TV every few seconds: the same data the overview's mine gate board reads, limited to person lanes and the last 24 h.
    [HttpGet("state"), AllowAnonymous]
    public async Task<IActionResult> State([FromQuery] string? key)
    {
        if (!await Keyed(key)) return Unauthorized(new { message = "The board is off or the key is wrong." });
        seen.Utc = DateTime.UtcNow; seen.Ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var lanes = await db.Lanes.AsNoTracking().Where(x => x.Subject == "Person").ToListAsync();
        var laneIds = lanes.Select(x => x.Id).ToList();
        var since = DateTime.UtcNow.AddHours(-Hours);
        var events = await db.Events.AsNoTracking().Where(e => laneIds.Contains(e.LaneId) && e.CreatedUtc >= since).OrderByDescending(e => e.CreatedUtc).Take(100)
            .Select(e => new { e.Id, e.LaneId, e.PersonId, e.Outcome, e.PpeSummary, e.CreatedUtc }).ToListAsync();
        var codes = await db.Areas.AsNoTracking().Select(a => a.Code).ToListAsync();
        var visits = (await PresenceBook.VisitRows(db, codes, Hours)).Where(v => v.SubjectKey.StartsWith("P:")).ToList();
        // Only the people on the board, not the whole register.
        var ids = visits.Where(v => v.PersonId.HasValue).Select(v => v.PersonId!.Value).Distinct().ToList();
        var people = await db.People.AsNoTracking().Where(p => ids.Contains(p.PersonId)).Select(p => new { p.PersonId, p.Name, p.Code }).ToListAsync();
        var areas = await db.Areas.AsNoTracking().ToListAsync();
        var locations = await db.Locations.AsNoTracking().ToListAsync();
        var laneLocations = await db.Lanes.AsNoTracking().Select(x => new { x.Id, x.LocationId }).ToListAsync();
        Response.Headers.CacheControl = "no-store";
        return Ok(new {
            lanes = lanes.Select(l => new { l.Id, l.Name, l.Kind, l.Subject, l.Direction, l.PpeRules }),
            events, visits, people,
            shifts = await db.Shifts.AsNoTracking().ToListAsync(),
            areas = areas.Select(a => new { a.Id, a.Code, a.Name,
                locations = locations.Where(l => l.AreaId == a.Id).Select(l => new { l.Id, lanes = laneLocations.Where(x => x.LocationId == l.Id).Select(x => x.Id) }) }),
            serverUtc = DateTime.UtcNow,
        });
    }

    // Entry / exit photo of a card on the board: only a person-lane passage of the last 24 h.
    [HttpGet("events/{id:guid}/evidence"), AllowAnonymous]
    public async Task<IActionResult> Evidence(Guid id, [FromQuery] string? key)
    {
        if (!await Keyed(key)) return Unauthorized(new { message = "The board is off or the key is wrong." });
        var entry = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (entry?.EvidenceId is null || entry.CreatedUtc < DateTime.UtcNow.AddHours(-Hours)
            || !await db.Lanes.AnyAsync(l => l.Id == entry.LaneId && l.Subject == "Person")) return NotFound();
        var path = Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(entry.EvidenceId));
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
    }
}
