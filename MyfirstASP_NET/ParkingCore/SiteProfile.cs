using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MyfirstASP_NET.ParkingCore;

// Bounds keep every timing inside a safe range; the cooldown in particular can never be 0 because it guards against double opening.
public class TimingInput
{
    [Range(5, 300)] public int DecisionValidSeconds { get; set; } = 30;
    [Range(30, 3600)] public int ReviewWindowSeconds { get; set; } = 300;
    [Range(2, 120)] public int CooldownSeconds { get; set; } = 8;
    [Range(2, 60)] public int HeartbeatFreshSeconds { get; set; } = 10;
    [Range(10, 600)] public int OfflineAlertSeconds { get; set; } = 30;
}

public class PlateInput
{
    [StringLength(2000)] public string Patterns { get; set; } = "";
}

[ApiController, Route("api/v1/parking/settings"), Authorize(Policy = Perm.ViewLive)]
public class SiteSettingsController(ParkingDb db, ParkingLock mutex) : ControllerBase
{
    [HttpGet("timings")]
    public async Task<SiteSettings> Timings() => await db.Site();

    [HttpPut("plates"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> SavePlates(PlateInput input)
    {
        if (PlateRules.Check(input.Patterns) is string error) return Conflict(new { message = error });
        await mutex.Gate.WaitAsync();
        try
        {
            var site = await db.SiteSettings.FindAsync(1);
            if (site is null) { site = new SiteSettings(); db.SiteSettings.Add(site); }
            site.PlatePatterns = input.Patterns.Trim();
            db.Log(User.Identity?.Name ?? "unknown", "SavePlateFormats", site.PlatePatterns.Length == 0 ? "built-in" : site.PlatePatterns);
            await db.SaveChangesAsync(); return Ok(site);
        }
        finally { mutex.Gate.Release(); }
    }

    [HttpPut("timings"), Authorize(Policy = Perm.ManageSite)]
    public async Task<SiteSettings> SaveTimings(TimingInput input)
    {
        await mutex.Gate.WaitAsync();
        try
        {
            var site = await db.SiteSettings.FindAsync(1);
            if (site is null) { site = new SiteSettings(); db.SiteSettings.Add(site); }
            site.DecisionValidSeconds = input.DecisionValidSeconds; site.ReviewWindowSeconds = input.ReviewWindowSeconds; site.CooldownSeconds = input.CooldownSeconds;
            site.HeartbeatFreshSeconds = input.HeartbeatFreshSeconds; site.OfflineAlertSeconds = input.OfflineAlertSeconds;
            db.Log(User.Identity?.Name ?? "unknown", "SaveSiteTimings", System.Text.Json.JsonSerializer.Serialize(input));
            await db.SaveChangesAsync(); return site;
        }
        finally { mutex.Gate.Release(); }
    }
}
