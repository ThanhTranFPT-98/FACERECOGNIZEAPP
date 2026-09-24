using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

public class VehicleInput
{
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(100)] public string Owner { get; set; } = "";
    [RegularExpression("^(Car|Motorcycle)$")] public string Kind { get; set; } = "Motorcycle";
    [Range(1, int.MaxValue)] public int? PersonId { get; set; }
    [MaxLength(20)] public int[]? AdditionalPersonIds { get; set; }
    public bool Active { get; set; } = true;
    public bool Blocked { get; set; }
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

[ApiController, Route("api/v1/parking"), Authorize(Roles = "Admin,Guard,Auditor")]
public class ParkingController(ParkingDb db, ParkingService service, ParkingLock mutex, IWebHostEnvironment environment, IConfiguration config) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";
    [HttpGet("snapshot")]
    public async Task<object> Snapshot() => new {
        vehicles = await db.Vehicles.AsNoTracking().Include(x => x.AuthorizedPeople).OrderBy(x => x.Plate).ToListAsync(),
        visits = await db.Visits.AsNoTracking().OrderByDescending(x => x.UntilUtc).Take(200).ToListAsync(),
        lanes = await db.Lanes.AsNoTracking().ToListAsync(),
        events = await db.Events.AsNoTracking().OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(),
        commands = await db.Commands.AsNoTracking().OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(),
        alerts = await db.Alerts.AsNoTracking().Where(x => !x.Resolved).OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync()
        , sessions = await db.Sessions.AsNoTracking().OrderByDescending(x => x.EntryUtc).Take(200).ToListAsync()
    };
    [HttpGet("events")]
    public async Task<object> Events(string? plate, DateTime? from, DateTime? until, int page = 1)
    {
        var query = db.Events.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(plate)) { var normalized = VietnamPlate.Normalize(plate); query = query.Where(x => x.Plate == normalized); }
        if (from.HasValue) query = query.Where(x => x.CreatedUtc >= from.Value);
        if (until.HasValue) query = query.Where(x => x.CreatedUtc < until.Value);
        return new { total = await query.CountAsync(), items = await query.OrderByDescending(x => x.CreatedUtc).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync() };
    }
    [HttpGet("audit"), Authorize(Roles = "Admin,Auditor")]
    public async Task<object> Audit(int page = 1) => await db.Audit.AsNoTracking().OrderByDescending(x => x.Id).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync();

    [HttpPost("vehicles"), Authorize(Roles = "Admin")]
    public Task<IActionResult> AddVehicle(VehicleInput input) => UpdateVehicle(null, input);
    [HttpPut("vehicles/{id:guid}"), Authorize(Roles = "Admin")]
    public Task<IActionResult> EditVehicle(Guid id, VehicleInput input) => UpdateVehicle(id, input);
    private async Task<IActionResult> UpdateVehicle(Guid? id, VehicleInput input) => await Mutate(async () => {
        var plate = VietnamPlate.Normalize(input.Plate);
        if (!VietnamPlate.CommonFormat(plate)) throw new InvalidOperationException("Use a common Vietnamese civilian plate. Special plates require a separately validated format rule.");
        if (await db.Vehicles.AnyAsync(x => x.NormalizedPlate == plate && x.Id != id)) throw new InvalidOperationException("Plate is already registered.");
        var vehicle = id.HasValue ? await db.Vehicles.FindAsync(id.Value) ?? throw new InvalidOperationException("Vehicle not found.") : new RegisteredVehicle();
        vehicle.Plate = input.Plate.Trim(); vehicle.NormalizedPlate = plate; vehicle.Owner = input.Owner.Trim(); vehicle.Kind = input.Kind;
        vehicle.PersonId = input.PersonId; vehicle.Active = input.Active; vehicle.Blocked = input.Blocked;
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
    [HttpPost("visits"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Visit(VisitInput input) => await Mutate(async () => {
        if (input.From >= input.Until || input.Until <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Visit window must end in the future and after its start.");
        var plate = VietnamPlate.Normalize(input.Plate);
        if (!VietnamPlate.CommonFormat(plate)) throw new InvalidOperationException("Unsupported plate format.");
        var visit = new Visit { Name = input.Name.Trim(), Host = input.Host.Trim(), Plate = plate, FromUtc = input.From.UtcDateTime, UntilUtc = input.Until.UtcDateTime, MaxEntries = input.MaxEntries };
        db.Visits.Add(visit); db.Log(Actor, "CreateVisit", visit.Id); await Task.CompletedTask; return visit;
    });
    [HttpPost("visits/{id:guid}/revoke"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Revoke(Guid id) => await Mutate(async () => {
        var visit = await db.Visits.FindAsync(id) ?? throw new InvalidOperationException("Visit not found.");
        visit.Revoked = true; db.Log(Actor, "RevokeVisit", id); return visit;
    });
    [HttpPut("lanes"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Lane(LaneInput input) => await Mutate(async () => {
        var lane = await db.Lanes.FindAsync(input.Id);
        if (lane is null) { lane = new Lane { Id = input.Id }; db.Lanes.Add(lane); }
        lane.Direction = input.Direction; lane.Policy = input.Policy; lane.PlateThreshold = input.PlateThreshold; lane.FaceThreshold = input.FaceThreshold;
        lane.ShadowMode = input.ShadowMode; lane.Simulation = input.Simulation; lane.AutoOpen = input.AutoOpen;
        db.Log(Actor, "ConfigureLane", input.Id); return lane;
    });
    [HttpPost("simulate"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Simulate(SimulatedObservation input) => await Execute(async () => await service.Observe(input.LaneId, input.CorrelationId,
        new(input.Plate, input.PlateConfidence, input.PersonId, input.FaceConfidence, "simulation"), Actor, "Simulation"));

    [HttpPost("recognize"), Authorize(Roles = "Admin,Guard"), RequestSizeLimit(11_000_000)]
    public async Task<IActionResult> Recognize([FromForm, Required, StringLength(40)] string laneId,
        [FromForm, Required, StringLength(100)] string correlationId, IFormFile image, IFormFile? face) => await Execute(async () => {
        return await service.Capture(laneId, correlationId, image, face, Actor);
    });
    [HttpPost("events/{id:guid}/review"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Review(Guid id, ReviewInput input) => await Execute(async () => await service.Review(id, input.Approve, input.Note, Actor));
    [HttpPost("events/{id:guid}/open"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Open(Guid id) => await Execute(async () => await service.Open(id, Actor));
    [HttpPost("alerts/{id:guid}/resolve"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Resolve(Guid id) => await Mutate(async () => {
        var alert = await db.Alerts.FindAsync(id) ?? throw new InvalidOperationException("Alert not found.");
        alert.Resolved = true; alert.ResolvedBy = Actor; db.Log(Actor, "ResolveAlert", id); return alert;
    });
    [HttpPost("commands/{id:guid}/reconcile"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reconcile(Guid id, ReviewInput input) => await Mutate(async () => {
        var command = await db.Commands.FindAsync(id) ?? throw new InvalidOperationException("Command not found.");
        if (command.Status != "Unknown" && !(command.Status == "Acknowledged" && !command.PassageUtc.HasValue)) throw new InvalidOperationException("Only unknown outcomes or unconfirmed passages need reconciliation.");
        command.Status = "Reconciled"; db.Log(Actor, "ReconcileCommand:" + input.Note, id); return command;
    });
    [HttpPost("commands/{id:guid}/simulate-passage"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> SimulatePassage(Guid id) => await Mutate(async () => {
        var command = await db.Commands.FindAsync(id) ?? throw new InvalidOperationException("Command not found.");
        var lane = await db.Lanes.FindAsync(command.LaneId) ?? throw new InvalidOperationException("Lane missing.");
        if (!lane.Simulation || command.Status != "Simulated") throw new InvalidOperationException("Only simulated commands support simulated passage.");
        await service.ConfirmPassage(command, lane); db.Log(Actor, "SimulatedPassage", command.Id); return command;
    });
    [HttpGet("events/{id:guid}/evidence"), Authorize(Roles = "Admin,Guard")]
    public async Task<IActionResult> Evidence(Guid id)
    {
        var entry = await db.Events.FindAsync(id);
        if (entry?.EvidenceId is null) return NotFound();
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
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(503, new { message = "Recognition service is unavailable. Use guard review or restore the AI service." }); }
        catch (TaskCanceledException) { return StatusCode(504, new { message = "Recognition service timed out." }); }
    }
}

[ApiController, Route("api/v1/parking/device"), Authorize(AuthenticationSchemes = "Device", Roles = "Device")]
public class ParkingDeviceController(ParkingDb db, ParkingService service, ParkingLock mutex, MyfirstASP_NET.Data.ApplicationDBContext faceDb) : ControllerBase
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
