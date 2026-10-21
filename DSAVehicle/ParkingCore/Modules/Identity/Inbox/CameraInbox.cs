using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.ParkingCore;

public class CameraInbox
{
    public long Id { get; set; }
    [MaxLength(40)] public string Adapter { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string RemoteIp { get; set; } = "";
    [MaxLength(64)] public string PayloadSha256 { get; set; } = "";
    // Cleared after processing + retention so base64 images don't stay in the database.
    public string? Payload { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    [MaxLength(40)] public string? AdapterVersion { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public int Observations { get; set; }
}
