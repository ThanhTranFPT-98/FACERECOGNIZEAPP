using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

public class RegisteredVehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Plate { get; set; } = "";
    [MaxLength(32)] public string NormalizedPlate { get; set; } = "";
    public string Kind { get; set; } = "Motorcycle";
    public string Owner { get; set; } = "";
    public int? PersonId { get; set; }
    public bool Active { get; set; } = true;
    public bool Blocked { get; set; }
    // GC-15: Registered | Guest | Delivery. Categories listed in SiteSettings.ReviewCategories always go to the guard.
    [MaxLength(20)] public string Category { get; set; } = "Registered";
    public DateTime? ValidUntilUtc { get; set; }
    public ICollection<VehiclePerson> AuthorizedPeople { get; set; } = new List<VehiclePerson>();
}
public class VehiclePerson
{
    public Guid VehicleId { get; set; }
    public int PersonId { get; set; }
}
