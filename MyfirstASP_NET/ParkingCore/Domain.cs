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
    public DateTime? ExpiresUtc { get; set; }
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
// Site hierarchy (GC-01): Area → Location → Lane → Camera. Every lane belongs to a location once the site is initialised.
public class Area
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Code { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(20)] public string Kind { get; set; } = "ParkingLot";
    // GC-19: alert when someone is inside this area longer than this (null = off).
    public int? OverstayMinutes { get; set; }
}
public class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
}
public class Lane
{
    [MaxLength(40)] public string Id { get; set; } = "entry-1";
    public Guid? LocationId { get; set; }
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
    // Timing overrides (GC-44). Null = use the site default in SiteSettings.
    public int? DecisionValidSeconds { get; set; }
    public int? ReviewWindowSeconds { get; set; }
    public int? CooldownSeconds { get; set; }
    public int? HeartbeatFreshSeconds { get; set; }
    public int? OfflineAlertSeconds { get; set; }
    // GC-10: what the lane controls. Person lanes decide by face against AuthorizedPerson, not by vehicle.
    [MaxLength(20)] public string Subject { get; set; } = "Vehicle";
    // PPE (Plan V2 §3.9): "helmet=required,vest=warn"; empty = no PPE check. Applied on entry only (O7).
    [MaxLength(300)] public string? PpeRules { get; set; }
    // O6, when there is no PPE reading: Review (guard checks) or AllowAlert (let through + alert).
    [MaxLength(20)] public string PpeMissingMode { get; set; } = "Review";
    // GC-12: on an exit lane, a plate with no open entry goes to the guard instead of opening.
    public bool ExitRequiresEntry { get; set; }
    // GC-17: the guard must attach an inspection photo before this lane opens (e.g. cargo check on car exits).
    public bool RequireInspectionPhoto { get; set; }
}
// Site profile: one row per site database. Defaults equal the former hard-coded constants.
public class SiteSettings
{
    public int Id { get; set; } = 1;
    public int DecisionValidSeconds { get; set; } = 30;
    public int ReviewWindowSeconds { get; set; } = 300;
    public int CooldownSeconds { get; set; } = 8;
    public int HeartbeatFreshSeconds { get; set; } = 10;
    public int OfflineAlertSeconds { get; set; } = 30;
    // GC-43: one regular expression per line, matched against the normalized plate. Empty = built-in Vietnamese civilian formats.
    [MaxLength(2000)] public string PlatePatterns { get; set; } = "";
    // GC-24: third-party results for the same plate/person on the same lane within this window are one pass.
    public int MergeWindowSeconds { get; set; } = 10;
    // PPE pairing: a reading counts if within this many seconds of the face result; a missing item must be seen this many times.
    public int PpeWindowSeconds { get; set; } = 3;
    public int PpeConfirmCount { get; set; } = 2;
    // GC-15/16: comma-separated vehicle categories that always need a guard; walk-in passes end at this local hour.
    [MaxLength(200)] public string ReviewCategories { get; set; } = "Delivery";
    public int WalkInResetHour { get; set; } = 0;
    [MaxLength(60)] public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";
    public DateTime NextReset(DateTime utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var reset = local.Date.AddHours(WalkInResetHour);
        if (reset <= local) reset = reset.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(reset, DateTimeKind.Unspecified), zone);
    }
}
public record LaneTimings(TimeSpan DecisionValid, TimeSpan ReviewWindow, TimeSpan Cooldown, TimeSpan HeartbeatFresh, TimeSpan OfflineAlert)
{
    public static LaneTimings Resolve(SiteSettings site, Lane lane) => new(
        TimeSpan.FromSeconds(lane.DecisionValidSeconds ?? site.DecisionValidSeconds),
        TimeSpan.FromSeconds(lane.ReviewWindowSeconds ?? site.ReviewWindowSeconds),
        TimeSpan.FromSeconds(lane.CooldownSeconds ?? site.CooldownSeconds),
        TimeSpan.FromSeconds(lane.HeartbeatFreshSeconds ?? site.HeartbeatFreshSeconds),
        TimeSpan.FromSeconds(lane.OfflineAlertSeconds ?? site.OfflineAlertSeconds));
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
    // GC-31: SHA-256 of the evidence file, taken when the event is created.
    [MaxLength(64)] public string? EvidenceSha256 { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    // Latency (GC-22): ReceivedUtc = message arrived (t1), CreatedUtc = decided (t2); command times are on DeviceCommand.
    public DateTime? ReceivedUtc { get; set; }
    public DateTime? CapturedUtc { get; set; }
    // Shift of the lane's area at decision time (GC-20), for search and statistics.
    [MaxLength(60)] public string? Shift { get; set; }
    [MaxLength(20)] public string? PpeOutcome { get; set; }
    [MaxLength(300)] public string? PpeSummary { get; set; }
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
    public DateTime? AckUtc { get; set; }
    public DateTime? PassageUtc { get; set; }
}
// Extra images of an event: overview, inspection photo taken by a guard, face (GC-17, GC-30, GC-31).
public class EvidenceImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    [MaxLength(80)] public string FileId { get; set; } = "";
    [MaxLength(20)] public string Kind { get; set; } = "Overview";
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    [MaxLength(100)] public string TakenBy { get; set; } = "";
    [MaxLength(500)] public string? Note { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
public class ParkingAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EventId { get; set; }
    public string Reason { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    // Resolved = closed (Resolved or FalseAlarm); kept for queries and the webhook.
    public bool Resolved { get; set; }
    public string? ResolvedBy { get; set; }
    // GC-25 lifecycle: Open | Acknowledged | Resolved | FalseAlarm.
    [MaxLength(20)] public string Status { get; set; } = "Open";
    [MaxLength(100)] public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    [MaxLength(500)] public string? ResolutionNote { get; set; }
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
    // GC-13: who drove in, so the exit can be checked against the same person.
    public int? EntryPersonId { get; set; }
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
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserArea> UserAreas => Set<UserArea>();
    public DbSet<CameraInbox> CameraInbox => Set<CameraInbox>();
    public DbSet<EvidenceImage> EvidenceImages => Set<EvidenceImage>();
    public DbSet<AuthorizedPerson> People => Set<AuthorizedPerson>();
    public DbSet<PresenceRecord> Presence => Set<PresenceRecord>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    // A missing row (fresh test database) means the built-in defaults.
    public async Task<SiteSettings> Site(CancellationToken cancellation = default) =>
        await SiteSettings.AsNoTracking().SingleOrDefaultAsync(cancellation) ?? new();
    public async Task<LaneTimings> Timings(Lane lane) => LaneTimings.Resolve(await Site(), lane);
    public async Task<PlateRules> PlateRules() => new((await Site()).PlatePatterns);
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SiteSettings>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<CameraInbox>().HasIndex(x => new { x.Status, x.Id });
        model.Entity<CameraInbox>().HasIndex(x => x.PayloadSha256);
        model.Entity<EvidenceImage>().HasIndex(x => x.EventId);
        model.Entity<AuthorizedPerson>().HasKey(x => x.PersonId);
        model.Entity<PresenceRecord>().HasIndex(x => x.EventId).IsUnique();
        model.Entity<PresenceRecord>().HasIndex(x => new { x.AreaCode, x.PassageUtc });
        model.Entity<PresenceRecord>().HasIndex(x => new { x.StationId, x.Id });
        model.Entity<SyncCursor>().HasKey(x => x.Peer);
        model.Entity<Shift>().HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AuthorizedPerson>().Property(x => x.PersonId).ValueGeneratedNever();
        model.Entity<Camera>().HasIndex(x => x.ExternalDevice);
        model.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.Permission });
        model.Entity<UserArea>().HasKey(x => new { x.UserId, x.AreaId });
        model.Entity<UserArea>().HasOne<ParkingUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Restrict: deleting an area must not silently lift an account's limit.
        model.Entity<UserArea>().HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<RolePermission>().HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Area>().HasIndex(x => x.Code).IsUnique();
        model.Entity<Location>().HasIndex(x => new { x.AreaId, x.Name }).IsUnique();
        // Restrict: removing an area or location must never silently remove lanes.
        model.Entity<Location>().HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Lane>().HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<VideoSegment>().HasIndex(x => new { x.CameraId, x.FileName }).IsUnique();
        model.Entity<VideoSegment>().HasIndex(x => new { x.Status, x.StartUtc });
        model.Entity<VideoSegment>().HasIndex(x => new { x.CameraId, x.StartUtc });
        model.Entity<SyncOutboxItem>().HasIndex(x => new { x.Status, x.Id });
        model.Entity<ParkingUser>().HasIndex(x => x.Username).IsUnique();
        model.Entity<RegisteredVehicle>().HasIndex(x => x.NormalizedPlate).IsUnique();
        model.Entity<AccessEvent>().HasIndex(x => x.CorrelationId).IsUnique();
        model.Entity<DeviceCommand>().HasIndex(x => x.EventId).IsUnique();
        model.Entity<AccessEvent>().HasIndex(x => x.CreatedUtc);
        model.Entity<AccessEvent>().HasIndex(x => new { x.LaneId, x.CreatedUtc });
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
// Site plate formats (GC-43). Patterns are anchored and time-limited, so an admin's regex can't hang a decision.
public sealed class PlateRules
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(50);
    private readonly Regex[] patterns;
    public static readonly PlateRules Default = new("");
    public PlateRules(string patterns) => this.patterns = Lines(patterns).Select(p => new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant, Limit)).ToArray();
    public bool Custom => patterns.Length > 0;
    public bool Valid(string normalizedPlate)
    {
        if (!Custom) return VietnamPlate.CommonFormat(normalizedPlate);
        try { return patterns.Any(p => p.IsMatch(normalizedPlate)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
    // Returns an error message, or null when every line is a usable pattern.
    public static string? Check(string patterns)
    {
        foreach (var line in Lines(patterns))
        {
            try { _ = new Regex("^(?:" + line + ")$", RegexOptions.CultureInvariant, Limit); }
            catch (ArgumentException ex) { return "Invalid pattern '" + line + "': " + ex.Message; }
        }
        return null;
    }
    private static IEnumerable<string> Lines(string patterns) => patterns.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0);
}
public class ParkingSqlServerDb(DbContextOptions<ParkingSqlServerDb> options) : ParkingDb(options) { }
public record Recognition(string Plate, double PlateConfidence, int? PersonId, double FaceConfidence, string ModelVersion);
public record PolicyResult(string Outcome, string Reason);
public static class AccessPolicy
{
    public static PolicyResult Evaluate(Lane lane, Recognition evidence, RegisteredVehicle? vehicle, bool visitor, bool faceAuthorized, PlateRules? plates = null, DateTime? now = null)
    {
        if (vehicle?.Blocked == true) return new("Deny", "BLOCKED_VEHICLE");
        if (vehicle is { Active: false }) return new("Deny", "INACTIVE_VEHICLE");
        // An expired registration is not a ban: the guard decides (and may register a walk-in pass).
        if (vehicle?.ValidUntilUtc is DateTime until && until <= (now ?? DateTime.UtcNow) && !visitor) return new("ManualReview", "REGISTRATION_EXPIRED");
        if (!double.IsFinite(evidence.PlateConfidence) || !double.IsFinite(evidence.FaceConfidence)
            || evidence.PlateConfidence is < 0 or > 1 || evidence.FaceConfidence is < 0 or > 1) return new("ManualReview", "INVALID_SCORES");
        var plate = (plates ?? PlateRules.Default).Valid(evidence.Plate) && evidence.PlateConfidence >= lane.PlateThreshold && (vehicle?.Active == true || visitor);
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
