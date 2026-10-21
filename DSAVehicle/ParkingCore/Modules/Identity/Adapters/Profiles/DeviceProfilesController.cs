using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class DeviceProfileInput
{
    [Required, RegularExpression("^[a-z0-9][a-z0-9_-]{1,39}$")] public string Id { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [RegularExpression("^(json|xml)$")] public string Format { get; set; } = "json";
    [Required, StringLength(20_000)] public string Definition { get; set; } = "";
    [StringLength(4_000_000)] public string? Sample { get; set; }
}
public class ProfileTestInput
{
    [RegularExpression("^(json|xml)$")] public string Format { get; set; } = "json";
    [Required, StringLength(20_000)] public string Definition { get; set; } = "";
    [Required, StringLength(8_000_000)] public string Sample { get; set; } = "";
}

[ApiController, Route("api/v1/parking/device-profiles"), Authorize(Policy = Perm.ManageSite)]
public class DeviceProfilesController(ParkingDb db, CameraAdapters builtIn) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";
    private static readonly string[] BuiltInNames = ["t1550", "generic", "ppe"];

    [HttpGet]
    public async Task<object> List() => new
    {
        builtIn = BuiltInNames.Where(n => builtIn.Find(n) is not null),
        t1550Example = ProfileAdapter.T1550Example,
        profiles = await db.DeviceProfiles.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
    };

    [HttpPut]
    public async Task<IActionResult> Save(DeviceProfileInput input)
    {
        if (builtIn.Find(input.Id) is not null) return Conflict(new { message = "This ID is a built-in adapter. Choose another ID." });
        try { ProfileAdapter.Compile(input.Id, input.Format, input.Definition, "check"); }
        catch (ProfileException ex) { return Conflict(new { message = "The mapping is not valid.", errors = ex.Errors }); }
        var profile = await db.DeviceProfiles.FindAsync(input.Id);
        if (profile is null) { profile = new DeviceProfile { Id = input.Id, Version = 0 }; db.DeviceProfiles.Add(profile); }
        profile.Name = input.Name.Trim(); profile.Enabled = input.Enabled; profile.Format = input.Format; profile.Definition = input.Definition;
        profile.Sample = string.IsNullOrWhiteSpace(input.Sample) ? null : input.Sample;
        profile.Version++; profile.UpdatedUtc = DateTime.UtcNow; profile.UpdatedBy = Actor;
        db.Log(Actor, "SaveDeviceProfile:v" + profile.Version + (profile.Enabled ? "" : ":disabled"), profile.Id);
        await db.SaveChangesAsync();
        return Ok(new { profile.Id, profile.Version });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var profile = await db.DeviceProfiles.FindAsync(id);
        if (profile is null) return NotFound();
        // Identity mappings are kept: re-creating the profile with the same ID reuses them.
        db.DeviceProfiles.Remove(profile);
        db.Log(Actor, "DeleteDeviceProfile", id);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Runs a sample through a mapping without saving anything, so an admin sees the result before a device depends on it.
    [HttpPost("test"), RequestSizeLimit(16_000_000)]
    public IActionResult Test(ProfileTestInput input)
    {
        ProfileAdapter adapter;
        try { adapter = ProfileAdapter.Compile("test", input.Format, input.Definition, "test"); }
        catch (ProfileException ex) { return Ok(new { ok = false, errors = ex.Errors, warnings = Array.Empty<string>(), observations = Array.Empty<object>() }); }
        JsonDocument document;
        try { document = PayloadFormat.Parse(input.Format, input.Sample); }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException)
        { return Ok(new { ok = false, errors = new[] { "The sample is not valid " + input.Format.ToUpperInvariant() + ": " + ex.Message }, warnings = Array.Empty<string>(), observations = Array.Empty<object>() }); }
        using (document)
        {
            var warnings = new List<string>();
            var observations = adapter.Parse(document.RootElement, warnings);
            return Ok(new
            {
                ok = true, errors = Array.Empty<string>(), warnings,
                observations = observations.Select(o => new
                {
                    o.DeviceId, o.Channel, o.Plate, normalizedPlate = VietnamPlate.Normalize(o.Plate), o.PersonId, o.ExternalPersonId,
                    confidence = o.Confidence is double c && double.IsNaN(c) ? "invalid" : o.Confidence?.ToString(CultureInfo.InvariantCulture), o.CapturedUtc,
                    plateImageBytes = o.PlateImage?.Length, overviewImageBytes = o.OverviewImage?.Length, o.RawKey
                })
            });
        }
    }
}
