using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

// People allowed through person lanes (GC-10), e.g. mine-shaft flap gates. PersonId is the ID the face source reports
// (our face DB, or the PIN of a ZKTeco terminal) — one ID space per site.
public class AuthorizedPerson
{
    public int PersonId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(40)] public string? Code { get; set; }
    public bool Active { get; set; } = true;
    public DateTime? ValidUntilUtc { get; set; }
    public static bool Allowed(AuthorizedPerson? person, DateTime now) => person is { Active: true } && (person.ValidUntilUtc is null || person.ValidUntilUtc > now);
}
