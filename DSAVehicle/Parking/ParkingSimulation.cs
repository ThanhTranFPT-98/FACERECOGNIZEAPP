using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace DSAVehicle.Parking;

// A deliberately isolated development prototype. No device adapter is registered.
public record Vehicle(Guid Id, string Plate, string Region, string Kind, string Owner);
public record VisitorPass(Guid Id, string Name, string Plate, string Region, DateTimeOffset From, DateTimeOffset Until);
public record Decision(Guid Id, string Plate, string Region, string Direction, string Outcome, string Reason,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public record BarrierCommand(Guid Id, Guid DecisionId, DateTimeOffset CreatedAt);

public class VehicleRequest
{
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(32)] public string Region { get; set; } = "";
    [Required, RegularExpression("^(Car|Motorcycle)$")] public string Kind { get; set; } = "Car";
    [Required, StringLength(100)] public string Owner { get; set; } = "";
}
public class VisitRequest
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(32)] public string Region { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset Until { get; set; }
}
public class ObservationRequest
{
    [Required, StringLength(24)] public string Plate { get; set; } = "";
    [Required, StringLength(32)] public string Region { get; set; } = "";
    [Required, RegularExpression("^(Entry|Exit)$")] public string Direction { get; set; } = "Entry";
    [Range(0, 1)] public double Confidence { get; set; }
}

public sealed class ParkingSimulation
{
    // Lock makes authorization consumption and command creation atomic for this single-process demo.
    private readonly object gate = new();
    private readonly TimeProvider clock;
    public ParkingSimulation(TimeProvider? clock = null) { this.clock = clock ?? TimeProvider.System; }
    private readonly List<Vehicle> vehicles = new();
    private readonly List<VisitorPass> visits = new();
    private readonly List<Decision> decisions = new();
    private readonly List<BarrierCommand> commands = new();
    private DateTimeOffset openUntil = DateTimeOffset.MinValue;
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public object Snapshot()
    {
        lock (gate) return new {
            mode = "Simulation", storage = "Memory — resets when API restarts",
            policy = "PlateOnly", confidenceThreshold = 0.9,
            vehicles = vehicles.ToArray(), visits = visits.ToArray(),
            decisions = decisions.AsEnumerable().Reverse().Take(100).ToArray(),
            commands = commands.AsEnumerable().Reverse().Take(100).ToArray(),
            barrierState = clock.GetUtcNow() < openUntil ? "Open" : "Closed", openUntil
        };
    }

    public Vehicle AddVehicle(VehicleRequest request)
    {
        lock (gate)
        {
            var plate = Normalize(request.Plate);
            var region = Normalize(request.Region);
            if (vehicles.Any(v => v.Plate == plate && v.Region == region))
                throw new InvalidOperationException("This plate is already registered in this region.");
            var vehicle = new Vehicle(Guid.NewGuid(), plate, region, request.Kind, request.Owner.Trim());
            vehicles.Add(vehicle);
            return vehicle;
        }
    }

    public VisitorPass AddVisit(VisitRequest request)
    {
        lock (gate)
        {
            if (request.From >= request.Until || request.Until <= clock.GetUtcNow())
                throw new InvalidOperationException("Visit must end after its start and in the future.");
            var visit = new VisitorPass(Guid.NewGuid(), request.Name.Trim(), Normalize(request.Plate),
                Normalize(request.Region), request.From, request.Until);
            visits.Add(visit);
            return visit;
        }
    }

    public Decision Evaluate(ObservationRequest request)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var plate = Normalize(request.Plate);
            var region = Normalize(request.Region);
            var registered = vehicles.Any(v => v.Plate == plate && v.Region == region);
            var visitor = visits.Any(v => v.Plate == plate && v.Region == region && v.From <= now && now < v.Until);
            var outcome = "Allow";
            var reason = registered ? "REGISTERED_VEHICLE" : "ACTIVE_VISITOR_PASS";
            if (!double.IsFinite(request.Confidence) || request.Confidence < 0.9)
            { outcome = "ManualReview"; reason = "LOW_PLATE_CONFIDENCE"; }
            else if (!registered && !visitor)
            { outcome = "ManualReview"; reason = "UNKNOWN_OR_EXPIRED_PLATE"; }
            var decision = new Decision(Guid.NewGuid(), plate, region, request.Direction, outcome, reason, now, now.AddSeconds(30));
            decisions.Add(decision);
            return decision;
        }
    }

    public BarrierCommand Open(Guid decisionId)
    {
        lock (gate)
        {
            // Return the original result without extending the open interval on retry.
            var prior = commands.FirstOrDefault(c => c.DecisionId == decisionId);
            if (prior is not null) return prior;
            var decision = decisions.FirstOrDefault(d => d.Id == decisionId);
            var now = clock.GetUtcNow();
            if (decision is null || decision.Outcome != "Allow" || decision.ExpiresAt <= now)
                throw new InvalidOperationException("An unexpired Allow decision is required.");
            if (now < openUntil) throw new InvalidOperationException("The simulated barrier is already open.");
            var command = new BarrierCommand(Guid.NewGuid(), decisionId, now);
            commands.Add(command);
            openUntil = now.AddSeconds(5);
            return command;
        }
    }
}

[ApiController]
[Route("api/v1/parking-simulation")]
public class ParkingSimulationController : ControllerBase
{
    private readonly ParkingSimulation simulation;
    private readonly IWebHostEnvironment environment;
    public ParkingSimulationController(ParkingSimulation simulation, IWebHostEnvironment environment)
    { this.simulation = simulation; this.environment = environment; }

    // No prototype endpoints are accessible outside Development.
    private IActionResult Run(Func<object> action)
    {
        if (!environment.IsDevelopment()) return NotFound();
        try { return Ok(action()); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
    [HttpGet] public IActionResult Get() => Run(simulation.Snapshot);
    [HttpPost("vehicles")] public IActionResult Vehicle(VehicleRequest request) => Run(() => simulation.AddVehicle(request));
    [HttpPost("visits")] public IActionResult Visit(VisitRequest request) => Run(() => simulation.AddVisit(request));
    [HttpPost("observations")] public IActionResult Observe(ObservationRequest request) => Run(() => simulation.Evaluate(request));
    [HttpPost("decisions/{id:guid}/open")] public IActionResult Open(Guid id) => Run(() => simulation.Open(id));
}
