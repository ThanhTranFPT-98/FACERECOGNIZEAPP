using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class PersonInput
{
    [Range(1, int.MaxValue)] public int PersonId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(40)] public string? Code { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? ValidUntilUtc { get; set; }
}

[ApiController, Route("api/v1/parking/people"), Authorize(Policy = Perm.ViewLive)]
public class PeopleController(ParkingDb db) : ControllerBase
{
    [HttpGet]
    public async Task<object> List() => await db.People.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    [HttpPut, Authorize(Policy = Perm.ManagePeople), LocalMasterOnly]
    public async Task<object> Save(PersonInput input)
    {
        var person = await db.People.FindAsync(input.PersonId);
        if (person is null) { person = new AuthorizedPerson { PersonId = input.PersonId }; db.People.Add(person); }
        person.Name = input.Name.Trim(); person.Code = input.Code?.Trim(); person.Active = input.Active; person.ValidUntilUtc = input.ValidUntilUtc?.UtcDateTime;
        db.Log(User.Identity?.Name ?? "unknown", "SavePerson:" + (person.Active ? "active" : "inactive"), person.PersonId);
        await db.SaveChangesAsync(); return person;
    }
}
