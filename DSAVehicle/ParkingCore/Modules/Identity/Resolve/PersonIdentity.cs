using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

// Face devices report their own IDs (terminal PIN, employee code, UUID). Replacing a device needs a new mapping, not new people.
public class PersonIdentity
{
    public long Id { get; set; }
    // Adapter name (profile ID) whose results use this external ID.
    [MaxLength(40)] public string Source { get; set; } = "";
    [MaxLength(100)] public string ExternalId { get; set; } = "";
    public int PersonId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
