using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

public class ParkingUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Guard";
    public bool Active { get; set; } = true;
}
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
    public ICollection<VehiclePerson> AuthorizedPeople { get; set; } = new List<VehiclePerson>();
}
public class VehiclePerson
{
    public Guid VehicleId { get; set; }
    public int PersonId { get; set; }
}
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
}
public class Lane
{
    [MaxLength(40)] public string Id { get; set; } = "entry-1";
    public string Direction { get; set; } = "Entry";
    public string Policy { get; set; } = "PlateOnly";
    public double PlateThreshold { get; set; } = .95;
    public double FaceThreshold { get; set; } = .8;
    public bool ShadowMode { get; set; } = true;
    public bool Simulation { get; set; } = true;
    public bool AutoOpen { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public bool SafetyHealthy { get; set; }
    public bool VehiclePresent { get; set; }
    public bool Obstructed { get; set; }
    public string BarrierState { get; set; } = "Unknown";
}
public class AccessEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string CorrelationId { get; set; } = "";
    public string LaneId { get; set; } = "";
    public string Plate { get; set; } = "";
    public double PlateConfidence { get; set; }
    public int? PersonId { get; set; }
    public double FaceConfidence { get; set; }
    public string ModelVersion { get; set; } = "";
    public string Outcome { get; set; } = "ManualReview";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public Guid? VisitId { get; set; }
    public string? EvidenceId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public string? ReviewBy { get; set; }
    public string? ReviewNote { get; set; }
}
public class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string LaneId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PassageUtc { get; set; }
}
public class ParkingAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EventId { get; set; }
    public string Reason { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public bool Resolved { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int DeliveryAttempts { get; set; }
    public DateTime? NextDeliveryUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
}
public class ParkingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Plate { get; set; } = "";
    public Guid EntryCommandId { get; set; }
    public Guid? ExitCommandId { get; set; }
    public DateTime EntryUtc { get; set; }
    public DateTime? ExitUtc { get; set; }
}
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
}
public class ParkingDb : DbContext
{
    public ParkingDb(DbContextOptions<ParkingDb> options) : base(options) { }
    protected ParkingDb(DbContextOptions options) : base(options) { }
    public DbSet<ParkingUser> Users => Set<ParkingUser>();
    public DbSet<RegisteredVehicle> Vehicles => Set<RegisteredVehicle>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Lane> Lanes => Set<Lane>();
    public DbSet<AccessEvent> Events => Set<AccessEvent>();
    public DbSet<DeviceCommand> Commands => Set<DeviceCommand>();
    public DbSet<ParkingAlert> Alerts => Set<ParkingAlert>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<ParkingSession> Sessions => Set<ParkingSession>();
    public DbSet<VehiclePerson> VehiclePeople => Set<VehiclePerson>();
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<VideoSegment> VideoSegments => Set<VideoSegment>();
    public DbSet<SyncOutboxItem> Outbox => Set<SyncOutboxItem>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<VideoSegment>().HasIndex(x => new { x.CameraId, x.FileName }).IsUnique();
        model.Entity<VideoSegment>().HasIndex(x => new { x.Status, x.StartUtc });
        model.Entity<VideoSegment>().HasIndex(x => new { x.CameraId, x.StartUtc });
        model.Entity<SyncOutboxItem>().HasIndex(x => new { x.Status, x.Id });
        model.Entity<ParkingUser>().HasIndex(x => x.Username).IsUnique();
        model.Entity<RegisteredVehicle>().HasIndex(x => x.NormalizedPlate).IsUnique();
        model.Entity<AccessEvent>().HasIndex(x => x.CorrelationId).IsUnique();
        model.Entity<DeviceCommand>().HasIndex(x => x.EventId).IsUnique();
        model.Entity<AccessEvent>().HasIndex(x => x.CreatedUtc);
        model.Entity<ParkingAlert>().HasIndex(x => x.CreatedUtc);
        model.Entity<ParkingSession>().HasIndex(x => x.EntryCommandId).IsUnique();
        model.Entity<VehiclePerson>().HasKey(x => new { x.VehicleId, x.PersonId });
        model.Entity<RegisteredVehicle>().HasMany(x => x.AuthorizedPeople).WithOne().HasForeignKey(x => x.VehicleId);
    }
    public void Log(string actor, string action, object target) => Audit.Add(new() { Actor = actor, Action = action, Target = target.ToString() ?? "" });
}
public static class VietnamPlate
{
    // Covers common civilian layouts; special/diplomatic/military formats require review.
    public static string Normalize(string value) => Regex.Replace(value.ToUpperInvariant(), @"[\s.\-]", "");
    public static bool CommonFormat(string value) => Regex.IsMatch(value, @"^[0-9]{2}[A-Z]{1,2}[0-9]{4,6}$");
}
public class ParkingSqlServerDb(DbContextOptions<ParkingSqlServerDb> options) : ParkingDb(options) { }
public record Recognition(string Plate, double PlateConfidence, int? PersonId, double FaceConfidence, string ModelVersion);
public record PolicyResult(string Outcome, string Reason);
public static class AccessPolicy
{
    public static PolicyResult Evaluate(Lane lane, Recognition evidence, RegisteredVehicle? vehicle, bool visitor, bool faceAuthorized)
    {
        if (vehicle?.Blocked == true) return new("Deny", "BLOCKED_VEHICLE");
        if (vehicle is { Active: false }) return new("Deny", "INACTIVE_VEHICLE");
        if (!double.IsFinite(evidence.PlateConfidence) || !double.IsFinite(evidence.FaceConfidence)
            || evidence.PlateConfidence is < 0 or > 1 || evidence.FaceConfidence is < 0 or > 1) return new("ManualReview", "INVALID_SCORES");
        var plate = VietnamPlate.CommonFormat(evidence.Plate) && evidence.PlateConfidence >= lane.PlateThreshold && (vehicle?.Active == true || visitor);
        var face = faceAuthorized && evidence.PersonId.HasValue && evidence.FaceConfidence >= lane.FaceThreshold;
        if (face && vehicle?.PersonId is int owner && owner != evidence.PersonId) return new("ManualReview", "PERSON_VEHICLE_CONFLICT");
        var allow = lane.Policy switch {
            "PlateOnly" => plate, "FaceOnly" => face, "Either" => plate || face,
            "Both" => plate && face && vehicle?.PersonId == evidence.PersonId,
            _ => false
        };
        return allow ? new("Allow", visitor && vehicle is null ? "ACTIVE_VISITOR" : "POLICY_MATCH")
            : new("ManualReview", lane.Policy == "ManualReview" ? "GUARD_APPROVAL_REQUIRED" : "INSUFFICIENT_IDENTITY_EVIDENCE");
    }
}
