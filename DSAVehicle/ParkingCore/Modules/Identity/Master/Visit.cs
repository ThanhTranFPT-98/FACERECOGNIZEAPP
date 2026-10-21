using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

public class Visit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Plate { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime UntilUtc { get; set; }
    public int MaxEntries { get; set; } = 1;
    public int Entries { get; set; }
    public bool Revoked { get; set; }
    // Visit = planned visitor; WalkIn = registered by a guard at the gate, ends at the next day start (GC-16).
    [MaxLength(20)] public string Kind { get; set; } = "Visit";
}
