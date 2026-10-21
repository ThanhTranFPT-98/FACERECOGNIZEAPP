using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class PersonIdentityInput
{
    [Required, StringLength(40)] public string Source { get; set; } = "";
    [Required, StringLength(100)] public string ExternalId { get; set; } = "";
    [Range(1, int.MaxValue)] public int PersonId { get; set; }
}

[ApiController, Route("api/v1/parking/person-identities"), Authorize(Policy = Perm.ManagePeople)]
public class PersonIdentitiesController(ParkingDb db, CameraAdapters builtIn) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    [HttpGet]
    public async Task<object> List([FromQuery] string? source) =>
        await db.PersonIdentities.AsNoTracking().Where(x => source == null || x.Source == source).OrderBy(x => x.Source).ThenBy(x => x.ExternalId).Take(5000).ToListAsync();

    [HttpPut, LocalMasterOnly]
    public async Task<IActionResult> Save(PersonIdentityInput input)
    {
        if (await Invalid(input.Source) is { } refused) return refused;
        Upsert(await db.PersonIdentities.ToDictionaryAsync(x => (x.Source, x.ExternalId)), input);
        db.Log(Actor, "SavePersonIdentity:" + input.Source + "/" + input.ExternalId.Trim(), input.PersonId);
        await db.SaveChangesAsync();
        return Ok();
    }

    // Bulk import (e.g. the export of a face terminal's user list), one audit entry.
    [HttpPost("import"), LocalMasterOnly]
    public async Task<IActionResult> Import(List<PersonIdentityInput> rows)
    {
        if (rows.Count is 0 or > 5000) return BadRequest(new { message = "Send 1–5000 rows." });
        foreach (var source in rows.Select(r => r.Source).Distinct())
            if (await Invalid(source) is { } refused) return refused;
        var existing = await db.PersonIdentities.ToDictionaryAsync(x => (x.Source, x.ExternalId));
        foreach (var row in rows) Upsert(existing, row);
        db.Log(Actor, "ImportPersonIdentities:" + rows.Count, string.Join(",", rows.Select(r => r.Source).Distinct()));
        await db.SaveChangesAsync();
        return Ok(new { imported = rows.Count });
    }

    [HttpDelete("{id:long}"), LocalMasterOnly]
    public async Task<IActionResult> Delete(long id)
    {
        var row = await db.PersonIdentities.FindAsync(id);
        if (row is null) return NotFound();
        db.PersonIdentities.Remove(row);
        db.Log(Actor, "DeletePersonIdentity:" + row.Source + "/" + row.ExternalId, row.PersonId);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private void Upsert(Dictionary<(string, string), PersonIdentity> existing, PersonIdentityInput input)
    {
        var key = (input.Source, input.ExternalId.Trim());
        if (!existing.TryGetValue(key, out var row)) { row = new PersonIdentity { Source = key.Source, ExternalId = key.Item2 }; db.PersonIdentities.Add(row); existing[key] = row; }
        row.PersonId = input.PersonId;
    }

    private async Task<IActionResult?> Invalid(string source) =>
        builtIn.Find(source) is not null || await db.DeviceProfiles.AnyAsync(x => x.Id == source) ? null : Conflict(new { message = "Unknown source: use a device profile ID or a built-in adapter." });
}
