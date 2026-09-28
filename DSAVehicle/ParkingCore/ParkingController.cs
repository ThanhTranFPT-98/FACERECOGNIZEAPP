using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class VehicleInput
{
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(100)] public string Owner { get; set; } = "";
    [RegularExpression("^(Car|Motorcycle)$")] public string Kind { get; set; } = "Motorcycle";
    [Range(1, int.MaxValue)] public int? PersonId { get; set; }
    [MaxLength(20)] public int[]? AdditionalPersonIds { get; set; }
    public bool Active { get; set; } = true;
    public bool Blocked { get; set; }
    [RegularExpression("^(Registered|Guest|Delivery)$")] public string Category { get; set; } = "Registered";
    public DateTimeOffset? ValidUntilUtc { get; set; }
}
public class WalkInInput
{
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(100)] public string Host { get; set; } = "";
    [Range(1, 100)] public int MaxEntries { get; set; } = 10;
}
public class VisitInput
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(100)] public string Host { get; set; } = "";
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset Until { get; set; }
    [Range(1, 100)] public int MaxEntries { get; set; } = 1;
}
public class LaneInput
{
    [Required, RegularExpression("^[a-zA-Z0-9_-]{1,40}$")] public string Id { get; set; } = "";
    [RegularExpression("^(Entry|Exit)$")] public string Direction { get; set; } = "Entry";
    [RegularExpression("^(PlateOnly|FaceOnly|Either|Both|ManualReview)$")] public string Policy { get; set; } = "PlateOnly";
    [Range(.01, 1)] public double PlateThreshold { get; set; } = .95;
    [Range(.01, 1)] public double FaceThreshold { get; set; } = .8;
    public bool ShadowMode { get; set; } = true;
    public bool Simulation { get; set; } = true;
    public bool AutoOpen { get; set; }
    public Guid? LocationId { get; set; }
    // Optional overrides of the site timings (GC-44); same bounds as TimingInput, null = site default.
    [Range(5, 300)] public int? DecisionValidSeconds { get; set; }
    [Range(30, 3600)] public int? ReviewWindowSeconds { get; set; }
    [Range(2, 120)] public int? CooldownSeconds { get; set; }
    [Range(2, 60)] public int? HeartbeatFreshSeconds { get; set; }
    [Range(10, 600)] public int? OfflineAlertSeconds { get; set; }
    [RegularExpression("^(Vehicle|Person)$")] public string Subject { get; set; } = "Vehicle";
    [StringLength(300)] public string? PpeRules { get; set; }
    [RegularExpression("^(Review|AllowAlert)$")] public string PpeMissingMode { get; set; } = "Review";
    public bool ExitRequiresEntry { get; set; }
    public bool RequireInspectionPhoto { get; set; }
}
public class SimulatedObservation
{
    [Required, StringLength(40)] public string LaneId { get; set; } = "";
    [Required, StringLength(100)] public string CorrelationId { get; set; } = "";
    [StringLength(24)] public string Plate { get; set; } = "";
    [Range(0, 1)] public double PlateConfidence { get; set; }
    public int? PersonId { get; set; }
    [Range(0, 1)] public double FaceConfidence { get; set; }
}
public class ReviewInput
{
    public bool Approve { get; set; }
    [Required, StringLength(500, MinimumLength = 3)] public string Note { get; set; } = "";
}
public class HeartbeatInput
{
    [Required, StringLength(40)] public string LaneId { get; set; } = "";
    public bool SafetyHealthy { get; set; }
    public bool VehiclePresent { get; set; }
    public bool Obstructed { get; set; }
    [RegularExpression("^(Open|Closed|Moving|Fault|Unknown)$")] public string BarrierState { get; set; } = "Unknown";
    public Guid? PassageCommandId { get; set; }
    public bool Tailgating { get; set; }
}

[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewLive)]
public class ParkingController(ParkingDb db, ParkingService service, ParkingLock mutex, IWebHostEnvironment environment, IConfiguration config, AreaScope scope) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";
    // Vehicles and visitor passes are site-wide; everything tied to a lane is limited to the account's areas (CR-005).
    [HttpGet("snapshot")]
    public async Task<object> Snapshot()
    {
        var allowed = (await scope.Lanes())?.ToList();
        var events = db.Events.AsNoTracking(); var commands = db.Commands.AsNoTracking(); var sessions = db.Sessions.AsNoTracking();
        var alerts = await db.Alerts.AsNoTracking().Where(x => !x.Resolved).OrderByDescending(x => x.CreatedUtc).Take(allowed is null ? 100 : 1000).ToListAsync();
        if (allowed is not null)
        {
            events = events.Where(x => allowed.Contains(x.LaneId)); commands = commands.Where(x => allowed.Contains(x.LaneId));
            sessions = sessions.Where(s => db.Commands.Any(c => c.Id == s.EntryCommandId && allowed.Contains(c.LaneId)));
            var eventIds = alerts.Where(x => x.EventId.HasValue).Select(x => x.EventId!.Value).ToList();
            var eventLanes = await db.Events.Where(x => eventIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.LaneId);
            alerts = alerts.Where(a => AreaScope.AlertLane(a, eventLanes) is string lane && allowed.Contains(lane)).Take(100).ToList();
        }
        return new {
            vehicles = await db.Vehicles.AsNoTracking().Include(x => x.AuthorizedPeople).OrderBy(x => x.Plate).ToListAsync(),
            visits = await db.Visits.AsNoTracking().OrderByDescending(x => x.UntilUtc).Take(200).ToListAsync(),
            lanes = await db.Lanes.AsNoTracking().Where(x => allowed == null || allowed.Contains(x.Id)).ToListAsync(),
            events = await events.OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(),
            commands = await commands.OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(),
            alerts,
            sessions = await sessions.OrderByDescending(x => x.EntryUtc).Take(200).ToListAsync()
        };
    }
    [HttpGet("events")]
    public async Task<object> Events(string? plate, DateTime? from, DateTime? until, Guid? areaId, int page = 1)
    {
        var query = db.Events.AsNoTracking().AsQueryable();
        if ((await scope.Lanes())?.ToList() is { } allowed) query = query.Where(x => allowed.Contains(x.LaneId));
        if (areaId.HasValue) { var laneIds = AreaCatalog.LaneIds(db, areaId.Value); query = query.Where(x => laneIds.Contains(x.LaneId)); }
        if (!string.IsNullOrWhiteSpace(plate)) { var normalized = VietnamPlate.Normalize(plate); query = query.Where(x => x.Plate == normalized); }
        if (from.HasValue) query = query.Where(x => x.CreatedUtc >= from.Value);
        if (until.HasValue) query = query.Where(x => x.CreatedUtc < until.Value);
        return new { total = await query.CountAsync(), items = await query.OrderByDescending(x => x.CreatedUtc).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync() };
    }
    [HttpGet("audit"), Authorize(Policy = Perm.ViewAudit)]
    public async Task<object> Audit(int page = 1) => await db.Audit.AsNoTracking().OrderByDescending(x => x.Id).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync();

    [HttpPost("vehicles"), Authorize(Policy = Perm.ManageVehicles)]
    public Task<IActionResult> AddVehicle(VehicleInput input) => UpdateVehicle(null, input);
    [HttpPut("vehicles/{id:guid}"), Authorize(Policy = Perm.ManageVehicles)]
    public Task<IActionResult> EditVehicle(Guid id, VehicleInput input) => UpdateVehicle(id, input);
    private async Task<IActionResult> UpdateVehicle(Guid? id, VehicleInput input) => await Mutate(async () => {
        var plate = VietnamPlate.Normalize(input.Plate);
        if (!(await db.PlateRules()).Valid(plate)) throw new InvalidOperationException("Plate does not match the site's plate formats. Special plates need a format rule in Lane settings.");
        if (await db.Vehicles.AnyAsync(x => x.NormalizedPlate == plate && x.Id != id)) throw new InvalidOperationException("Plate is already registered.");
        var vehicle = id.HasValue ? await db.Vehicles.FindAsync(id.Value) ?? throw new InvalidOperationException("Vehicle not found.") : new RegisteredVehicle();
        vehicle.Plate = input.Plate.Trim(); vehicle.NormalizedPlate = plate; vehicle.Owner = input.Owner.Trim(); vehicle.Kind = input.Kind;
        vehicle.PersonId = input.PersonId; vehicle.Active = input.Active; vehicle.Blocked = input.Blocked;
        vehicle.Category = input.Category; vehicle.ValidUntilUtc = input.ValidUntilUtc?.UtcDateTime;
        if (!id.HasValue) db.Vehicles.Add(vehicle);
        if (input.AdditionalPersonIds is not null)
        {
            if (input.AdditionalPersonIds.Any(x => x <= 0)) throw new InvalidOperationException("Person IDs must be positive integers.");
            var links = await db.VehiclePeople.Where(x => x.VehicleId == vehicle.Id).ToListAsync();
            var wanted = input.AdditionalPersonIds.Distinct().ToHashSet();
            db.VehiclePeople.RemoveRange(links.Where(x => !wanted.Contains(x.PersonId)));
            foreach (var personId in wanted.Where(x => links.All(l => l.PersonId != x))) db.VehiclePeople.Add(new() { VehicleId = vehicle.Id, PersonId = personId });
        }
        db.Log(Actor, "SaveVehicle", vehicle.Id); return vehicle;
    });
    [HttpPost("visits"), Authorize(Policy = Perm.ManageVisitors)]
    public async Task<IActionResult> Visit(VisitInput input) => await Mutate(async () => {
        if (input.From >= input.Until || input.Until <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Visit window must end in the future and after its start.");
        var plate = VietnamPlate.Normalize(input.Plate);
        if (!(await db.PlateRules()).Valid(plate)) throw new InvalidOperationException("Plate does not match the site's plate formats.");
        var visit = new Visit { Name = input.Name.Trim(), Host = input.Host.Trim(), Plate = plate, FromUtc = input.From.UtcDateTime, UntilUtc = input.Until.UtcDateTime, MaxEntries = input.MaxEntries };
        db.Visits.Add(visit); db.Log(Actor, "CreateVisit", visit.Id); await Task.CompletedTask; return visit;
    });
    // GC-16: a guard registers an unknown vehicle for today; the pass ends at the site's next day start.
    [HttpPost("visits/walk-in"), Authorize(Policy = Perm.ManageVisitors)]
    public async Task<IActionResult> WalkIn(WalkInInput input) => await Mutate(async () => {
        var plate = VietnamPlate.Normalize(input.Plate);
        if (!(await db.PlateRules()).Valid(plate)) throw new InvalidOperationException("Plate does not match the site's plate formats.");
        var now = DateTime.UtcNow;
        var visit = new Visit { Kind = "WalkIn", Name = input.Name.Trim(), Host = string.IsNullOrWhiteSpace(input.Host) ? Actor : input.Host.Trim(), Plate = plate,
            FromUtc = now, UntilUtc = (await db.Site()).NextReset(now), MaxEntries = input.MaxEntries };
        db.Visits.Add(visit); db.Log(Actor, "CreateWalkIn", visit.Id); return visit;
    });
    [HttpPost("visits/{id:guid}/revoke"), Authorize(Policy = Perm.ManageVisitors)]
    public async Task<IActionResult> Revoke(Guid id) => await Mutate(async () => {
        var visit = await db.Visits.FindAsync(id) ?? throw new InvalidOperationException("Visit not found.");
        visit.Revoked = true; db.Log(Actor, "RevokeVisit", id); return visit;
    });
    [HttpPut("lanes"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> Lane(LaneInput input) => await Mutate(async () => {
        var lane = await db.Lanes.FindAsync(input.Id);
        if (lane is null) { lane = new Lane { Id = input.Id }; db.Lanes.Add(lane); }
        lane.Direction = input.Direction; lane.Policy = input.Policy; lane.PlateThreshold = input.PlateThreshold; lane.FaceThreshold = input.FaceThreshold;
        lane.ShadowMode = input.ShadowMode; lane.Simulation = input.Simulation; lane.AutoOpen = input.AutoOpen;
        lane.DecisionValidSeconds = input.DecisionValidSeconds; lane.ReviewWindowSeconds = input.ReviewWindowSeconds; lane.CooldownSeconds = input.CooldownSeconds;
        lane.HeartbeatFreshSeconds = input.HeartbeatFreshSeconds; lane.OfflineAlertSeconds = input.OfflineAlertSeconds;
        if (PpeRules.Check(input.PpeRules) is string ppeError) throw new InvalidOperationException(ppeError);
        if (input.Subject == "Person" && input.Policy is not ("FaceOnly" or "ManualReview")) throw new InvalidOperationException("A person lane decides by face: use the FaceOnly or ManualReview policy.");
        lane.ExitRequiresEntry = input.ExitRequiresEntry; lane.RequireInspectionPhoto = input.RequireInspectionPhoto;
        lane.Subject = input.Subject; lane.PpeRules = string.IsNullOrWhiteSpace(input.PpeRules) ? null : input.PpeRules.Trim(); lane.PpeMissingMode = input.PpeMissingMode;
        if (input.LocationId.HasValue && !await db.Locations.AnyAsync(x => x.Id == input.LocationId)) throw new InvalidOperationException("Location not found.");
        lane.LocationId = input.LocationId ?? lane.LocationId ?? (await AreaCatalog.EnsureDefault(db)).Id;
        db.Log(Actor, "ConfigureLane", input.Id); return lane;
    });
    [HttpPost("simulate"), Authorize(Policy = Perm.OperateLane)]
    public async Task<IActionResult> Simulate(SimulatedObservation input) => await Execute(async () => { await scope.Ensure(input.LaneId); return await service.Observe(input.LaneId, input.CorrelationId,
        new(input.Plate, input.PlateConfidence, input.PersonId, input.FaceConfidence, "simulation"), Actor, "Simulation"); });

    [HttpPost("recognize"), Authorize(Policy = Perm.OperateLane), RequestSizeLimit(11_000_000)]
    public async Task<IActionResult> Recognize([FromForm, Required, StringLength(40)] string laneId,
        [FromForm, Required, StringLength(100)] string correlationId, IFormFile image, IFormFile? face) => await Execute(async () => {
        await scope.Ensure(laneId);
        return await service.Capture(laneId, correlationId, image, face, Actor);
    });
    [HttpPost("events/{id:guid}/review"), Authorize(Policy = Perm.OperateLane)]
    public async Task<IActionResult> Review(Guid id, ReviewInput input) => await Execute(async () => { await scope.EnsureEvent(id); return await service.Review(id, input.Approve, input.Note, Actor); });
    [HttpPost("events/{id:guid}/open"), Authorize(Policy = Perm.OperateLane)]
    public async Task<IActionResult> Open(Guid id) => await Execute(async () => { await scope.EnsureEvent(id); return await service.Open(id, Actor); });
    [HttpPost("alerts/{id:guid}/acknowledge"), Authorize(Policy = Perm.HandleAlerts)]
    public async Task<IActionResult> Acknowledge(Guid id) => await Mutate(async () => {
        await scope.EnsureAlert(id);
        var alert = await db.Alerts.FindAsync(id) ?? throw new InvalidOperationException("Alert not found.");
        AlertLifecycle.Acknowledge(alert, Actor, DateTime.UtcNow); db.Log(Actor, "AcknowledgeAlert", id); return alert;
    });
    [HttpPost("alerts/{id:guid}/resolve"), Authorize(Policy = Perm.HandleAlerts)]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveInput? input, [FromServices] IAuthorizationService authorization) => await Mutate(async () => {
        await scope.EnsureAlert(id);
        var alert = await db.Alerts.FindAsync(id) ?? throw new InvalidOperationException("Alert not found.");
        var mayMarkFalse = (await authorization.AuthorizeAsync(User, Perm.MarkFalseAlarm)).Succeeded;
        AlertLifecycle.Resolve(alert, Actor, DateTime.UtcNow, input?.Note, input?.FalseAlarm == true, mayMarkFalse);
        db.Log(Actor, (alert.Status == "FalseAlarm" ? "FalseAlarm:" : "ResolveAlert:") + alert.ResolutionNote, id); return alert;
    });
    [HttpPost("commands/{id:guid}/reconcile"), Authorize(Policy = Perm.Reconcile)]
    public async Task<IActionResult> Reconcile(Guid id, ReviewInput input) => await Mutate(async () => {
        await scope.EnsureCommand(id);
        var command = await db.Commands.FindAsync(id) ?? throw new InvalidOperationException("Command not found.");
        if (command.Status != "Unknown" && !(command.Status == "Acknowledged" && !command.PassageUtc.HasValue)) throw new InvalidOperationException("Only unknown outcomes or unconfirmed passages need reconciliation.");
        command.Status = "Reconciled"; db.Log(Actor, "ReconcileCommand:" + input.Note, id); return command;
    });
    [HttpPost("commands/{id:guid}/simulate-passage"), Authorize(Policy = Perm.OperateLane)]
    public async Task<IActionResult> SimulatePassage(Guid id) => await Mutate(async () => {
        await scope.EnsureCommand(id);
        var command = await db.Commands.FindAsync(id) ?? throw new InvalidOperationException("Command not found.");
        var lane = await db.Lanes.FindAsync(command.LaneId) ?? throw new InvalidOperationException("Lane missing.");
        if (!lane.Simulation || command.Status != "Simulated") throw new InvalidOperationException("Only simulated commands support simulated passage.");
        await service.ConfirmPassage(command, lane); db.Log(Actor, "SimulatedPassage", command.Id); return command;
    });
    [HttpGet("events/{id:guid}/evidence"), Authorize(Policy = Perm.ViewEvidence)]
    public async Task<IActionResult> Evidence(Guid id)
    {
        var entry = await db.Events.FindAsync(id);
        if (entry?.EvidenceId is null) return NotFound();
        if (!await scope.Allows(entry.LaneId)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        var path = Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(entry.EvidenceId));
        if (!System.IO.File.Exists(path)) return NotFound();
        db.Log(Actor, "ViewEvidence", id); await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
    }
    private async Task<IActionResult> Mutate(Func<Task<object>> action)
    {
        await mutex.Gate.WaitAsync();
        try { return await Execute(async () => { var result = await action(); await db.SaveChangesAsync(); return result; }); }
        finally { mutex.Gate.Release(); }
    }
    private async Task<IActionResult> Execute(Func<Task<object>> action)
    {
        try { return Ok(await action()); }
        catch (AreaForbiddenException ex) { return StatusCode(403, new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(503, new { message = "Recognition service is unavailable. Use guard review or restore the AI service." }); }
        catch (TaskCanceledException) { return StatusCode(504, new { message = "Recognition service timed out." }); }
    }
}

[ApiController, Route("api/v1/parking/device"), Authorize(AuthenticationSchemes = "Device", Roles = "Device")]
public class ParkingDeviceController(ParkingDb db, ParkingService service, ParkingLock mutex, DSAVehicle.Data.ApplicationDBContext faceDb) : ControllerBase
{
    [HttpPost("capture-burst"), RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> Burst([FromForm, Required, StringLength(40)] string laneId,
        [FromForm, Required, StringLength(100)] string correlationId, List<IFormFile> images)
    {
        try { return Ok(await service.CaptureBurst(laneId, correlationId, images, "lane-worker")); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(503); }
        catch (TaskCanceledException) { return StatusCode(504); }
    }
    [HttpGet("face-templates")]
    public async Task<object> FaceTemplates() => await faceDb.Embeddings.AsNoTracking().Select(x => new { x.Id, x.PeopleId, x.Vector }).ToListAsync();
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(HeartbeatInput input)
    {
        await mutex.Gate.WaitAsync();
        try
        {
            var lane = await db.Lanes.FindAsync(input.LaneId);
            if (lane is null) return NotFound();
            lane.LastHeartbeatUtc = DateTime.UtcNow; lane.SafetyHealthy = input.SafetyHealthy; lane.VehiclePresent = input.VehiclePresent;
            lane.Obstructed = input.Obstructed; lane.BarrierState = input.BarrierState;
            if (input.PassageCommandId.HasValue)
            {
                var command = await db.Commands.SingleOrDefaultAsync(x => x.Id == input.PassageCommandId && x.LaneId == lane.Id);
                if (command is null) return BadRequest(new { message = "Passage command does not belong to this lane." });
                await service.ConfirmPassage(command, lane);
            }
            if (input.Tailgating || !input.SafetyHealthy)
            {
                var reason = (input.Tailgating ? "TAILGATING:" : "SAFETY_FAULT:") + lane.Id;
                if (!await db.Alerts.AnyAsync(x => !x.Resolved && x.Reason == reason)) db.Alerts.Add(new() { Reason = reason, Severity = "Critical" });
            }
            await db.SaveChangesAsync(); return Ok(new { received = true });
        }
        finally { mutex.Gate.Release(); }
    }
    [HttpPost("capture"), RequestSizeLimit(11_000_000)]
    public async Task<IActionResult> Capture([FromForm, Required, StringLength(40)] string laneId,
        [FromForm, Required, StringLength(100)] string correlationId, IFormFile image, IFormFile? face)
    {
        try
        {
            var entry = await service.Capture(laneId, correlationId, image, face, "lane-worker");
            // Operator explicitly opens after reviewing; worker ingestion cannot bypass the policy or shadow gate.
            return Ok(entry);
        }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(503); }
        catch (TaskCanceledException) { return StatusCode(504); }
    }
}
