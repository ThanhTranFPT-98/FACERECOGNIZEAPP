using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// CR-024: public screens, e.g. a TV outside the mine door showing the lane's PPE cameras.
// Configured from the station (ManageSite); the TV itself opens /display/<id>?key=… without signing in.
// The screen only ever shows camera images and PPE results: never names or person IDs.
public class PublicDisplay
{
    [MaxLength(40)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(40)] public string? LaneId { get; set; }
    [MaxLength(120)] public string Title { get; set; } = "";
    [MaxLength(200)] public string Subtitle { get; set; } = "";
    [MaxLength(200)] public string StopText { get; set; } = "";
    // [{"id":"PPE-01","name":"Lối vào trái"}, …] in display order, at most 4.
    [MaxLength(1000)] public string CamerasJson { get; set; } = "[]";
    public double Zoom { get; set; } = 1.8;  // 1 = no zoom
    public bool Enlarge { get; set; } = true;
    public bool AlarmHeader { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    public int HoldSeconds { get; set; } = 8;
    [MaxLength(10)] public string Sound { get; set; } = "off";  // off | short | loop
    // The TV's access key, protected like camera passwords so an administrator can show the URL again.
    public string KeyProtected { get; set; } = "";
    public DateTime? LastSeenUtc { get; set; }
    [MaxLength(64)] public string? LastSeenIp { get; set; }
}
public class DisplayCamera
{
    [Required, StringLength(40)] public string Id { get; set; } = "";
    [StringLength(60)] public string Name { get; set; } = "";
}
public class DisplayInput
{
    [Required, RegularExpression("^[a-zA-Z0-9_-]{1,40}$")] public string Id { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(40)] public string? LaneId { get; set; }
    [StringLength(120)] public string Title { get; set; } = "";
    [StringLength(200)] public string Subtitle { get; set; } = "";
    [StringLength(200)] public string StopText { get; set; } = "";
    [MaxLength(4)] public List<DisplayCamera> Cameras { get; set; } = [];
    [Range(1, 3)] public double Zoom { get; set; } = 1.8;
    public bool Enlarge { get; set; } = true;
    public bool AlarmHeader { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    [Range(2, 120)] public int HoldSeconds { get; set; } = 8;
    [RegularExpression("^(off|short|loop)$")] public string Sound { get; set; } = "off";
}

// What one camera on a public screen shows, from its recent PPE readings (oldest first) and the lane's rules.
// A missing required item keeps the camera red for HoldSeconds after the last reading that showed it;
// a missing warn-only item gives amber; an item the AI box did not report is not counted as missing.
public static class DisplayState
{
    public sealed record Result(string State, string[] Missing, double[]? Box);
    public static Result Of(IReadOnlyList<PpeReading> readings, IReadOnlyDictionary<string, string> rules, DateTime now, int holdSeconds, bool showWarn)
    {
        string[] Missing(PpeReading r, string mode) => rules.Where(x => x.Value == mode && r.Items.TryGetValue(x.Key, out var seen) && !seen).Select(x => x.Key).ToArray();
        var held = readings.Where(r => r.PeopleInRoi == 1 && now - r.AtUtc <= TimeSpan.FromSeconds(holdSeconds)).ToList();
        if (held.LastOrDefault(r => Missing(r, "required").Length > 0) is { } bad) return new("violation", Missing(bad, "required"), bad.Box);
        if (showWarn && held.LastOrDefault(r => Missing(r, "warn").Length > 0) is { } warn) return new("warning", Missing(warn, "warn"), warn.Box);
        if (readings.LastOrDefault() is { PeopleInRoi: >= 1 } last && now - last.AtUtc <= TimeSpan.FromSeconds(3)) return new("ok", [], last.Box);
        return new("idle", [], null);
    }
}

[ApiController, Route("api/v1/parking")]
public class DisplaysController(ParkingDb db, IDataProtectionProvider protection, PpeCache ppe, IConfiguration config, IAuthorizationService authorization) : ControllerBase
{
    private const string Purpose = "PublicDisplay.Key";
    private string Actor => User.Identity?.Name ?? "unknown";
    private IDataProtector Protector => protection.CreateProtector(Purpose);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static List<DisplayCamera> Cameras(PublicDisplay d) => JsonSerializer.Deserialize<List<DisplayCamera>>(d.CamerasJson, Json) ?? [];
    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private string? Key(PublicDisplay d) { try { return Protector.Unprotect(d.KeyProtected); } catch (CryptographicException) { return null; } }
    private object View(PublicDisplay d) => new { d.Id, d.Name, d.LaneId, d.Title, d.Subtitle, d.StopText, cameras = Cameras(d), d.Zoom, d.Enlarge, d.AlarmHeader, d.ShowWarn, d.HoldSeconds, d.Sound, key = Key(d), d.LastSeenUtc, d.LastSeenIp };

    [HttpGet("displays"), Authorize(Policy = Perm.ManageSite)]
    public async Task<object> List() => (await db.PublicDisplays.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Select(View);

    [HttpPut("displays"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> Save(DisplayInput input)
    {
        if (input.LaneId is not null && !await db.Lanes.AnyAsync(x => x.Id == input.LaneId)) return Conflict(new { message = "Unknown lane." });
        var ids = input.Cameras.Select(c => c.Id).ToList();
        if (ids.Distinct().Count() != ids.Count) return Conflict(new { message = "A camera is listed twice." });
        if (ids.Count > 0 && await db.Cameras.CountAsync(c => ids.Contains(c.Id) && c.LaneId == input.LaneId) != ids.Count) return Conflict(new { message = "Every camera on the screen must belong to its lane." });
        var d = await db.PublicDisplays.FindAsync(input.Id);
        if (d is null) { d = new PublicDisplay { Id = input.Id, KeyProtected = Protector.Protect(NewKey()) }; db.PublicDisplays.Add(d); }
        d.Name = input.Name.Trim(); d.LaneId = string.IsNullOrWhiteSpace(input.LaneId) ? null : input.LaneId;
        d.Title = input.Title.Trim(); d.Subtitle = input.Subtitle.Trim(); d.StopText = input.StopText.Trim();
        d.CamerasJson = JsonSerializer.Serialize(input.Cameras.Select(c => new DisplayCamera { Id = c.Id, Name = c.Name.Trim() }), Json);
        d.Zoom = Math.Round(input.Zoom, 1); d.Enlarge = input.Enlarge; d.AlarmHeader = input.AlarmHeader; d.ShowWarn = input.ShowWarn;
        d.HoldSeconds = input.HoldSeconds; d.Sound = input.Sound;
        db.Log(Actor, "SaveDisplay", d.Id);
        await db.SaveChangesAsync();
        return Ok(View(d));
    }

    // A new key disconnects the TV that used the old one.
    [HttpPost("displays/{id}/rekey"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> Rekey(string id)
    {
        var d = await db.PublicDisplays.FindAsync(id);
        if (d is null) return NotFound();
        d.KeyProtected = Protector.Protect(NewKey()); d.LastSeenUtc = null; d.LastSeenIp = null;
        db.Log(Actor, "RekeyDisplay", id);
        await db.SaveChangesAsync();
        return Ok(View(d));
    }

    [HttpDelete("displays/{id}"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> Delete(string id)
    {
        var d = await db.PublicDisplays.FindAsync(id);
        if (d is null) return NotFound();
        db.PublicDisplays.Remove(d); db.Log(Actor, "DeleteDisplay", id);
        await db.SaveChangesAsync();
        return Ok(new { id });
    }

    // Polled by the TV every second. Open with the screen's key, or to a signed-in ManageSite user (the preview).
    [HttpGet("display/{id}/state"), AllowAnonymous]
    public async Task<IActionResult> State(string id, [FromQuery] string? key)
    {
        var d = await db.PublicDisplays.FindAsync(id);
        var keyed = d is not null && key is { Length: > 0 } && Key(d) is { } expected
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(key));
        var preview = !keyed && User.Identity?.IsAuthenticated == true && (await authorization.AuthorizeAsync(User, null, Perm.ManageSite)).Succeeded;
        if (d is null || !(keyed || preview)) return Unauthorized(new { message = "Unknown screen or wrong key." });
        var now = DateTime.UtcNow;
        if (keyed && (d.LastSeenUtc is null || now - d.LastSeenUtc > TimeSpan.FromSeconds(10)))
        {
            d.LastSeenUtc = now; d.LastSeenIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync();
        }
        var lane = d.LaneId is null ? null : await db.Lanes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.LaneId);
        var rules = PpeRules.Parse(lane?.PpeRules);
        var cameras = Cameras(d);
        Response.Headers.CacheControl = "no-store";
        return Ok(new {
            config = new { title = d.Title, sub = d.Subtitle, stopText = d.StopText, cams = cameras, rules, zoom = d.Zoom, enlarge = d.Enlarge, alarmHeader = d.AlarmHeader,
                showWarn = d.ShowWarn, hold = d.HoldSeconds, sound = d.Sound },
            gatewayUrl = config["Video:GatewayUrl"],
            cameras = cameras.Select(c => { var s = DisplayState.Of(ppe.RecentForCamera(c.Id), rules, now, d.HoldSeconds, d.ShowWarn); return new { c.Id, state = s.State, missing = s.Missing, box = s.Box }; }),
            serverUtc = now,
        });
    }
}
