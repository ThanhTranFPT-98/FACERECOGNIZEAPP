using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

public class DeviceProfile
{
    // Also the adapter name: /api/v1/parking/receiver/{Id}, Receiver:Ports, InboxWriter.Store(Id, …).
    [MaxLength(40)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // Bumped on every save; recorded on each inbox row (AdapterVersion) so a result can be traced to the mapping that produced it.
    public int Version { get; set; } = 1;
    [MaxLength(10)] public string Format { get; set; } = "json";
    public string Definition { get; set; } = "{}";
    // A real message from the device, kept so the mapping can be re-tested after an edit.
    public string? Sample { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string UpdatedBy { get; set; } = "";
}
