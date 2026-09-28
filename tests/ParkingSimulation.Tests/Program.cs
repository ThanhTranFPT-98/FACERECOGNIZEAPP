using DSAVehicle.Parking;

var clock = new TestClock();
var service = new ParkingSimulation(clock);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine($"PASS: {description}");
}
void Reject(Action action, string description)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, description); return; }
    throw new Exception(description);
}
ObservationRequest Observe(string plate = "AB-123", double confidence = .99, string region = "TEST") =>
    new() { Plate = plate, Region = region, Confidence = confidence };

service.AddVehicle(new() { Plate = " ab-123 ", Region = "test", Owner = "Demo", Kind = "Motorcycle" });
Reject(() => service.AddVehicle(new() { Plate = "AB-123", Region = "TEST", Owner = "Duplicate" }), "Duplicate normalized plate rejected");
var allowed = service.Evaluate(Observe());
Check(allowed.Outcome == "Allow", "Registered motorcycle allowed");
Check(service.Evaluate(Observe(region: "OTHER")).Outcome == "ManualReview", "Region mismatch requires review");
Check(service.Evaluate(Observe(confidence: .89)).Outcome == "ManualReview", "Low confidence requires review");
Check(service.Evaluate(Observe(confidence: double.NaN)).Outcome == "ManualReview", "Nonfinite confidence cannot allow access");
var unknown = service.Evaluate(Observe("UNKNOWN"));
Reject(() => service.Open(unknown.Id), "Unknown plate cannot open barrier");
Reject(() => service.Open(Guid.NewGuid()), "Invented decision cannot open barrier");
var command = service.Open(allowed.Id);
clock.Advance(TimeSpan.FromSeconds(6));
Check(service.Open(allowed.Id) == command, "Retry returns original command");
// A second decision can open after five seconds, proving the retry did not extend the open interval.
service.Open(service.Evaluate(Observe()).Id);
Check(true, "Retry does not extend barrier opening");
var expires = service.Evaluate(Observe());
clock.Advance(TimeSpan.FromSeconds(30));
Reject(() => service.Open(expires.Id), "Decision expires at 30 seconds");
service.AddVisit(new() { Name = "Visitor", Plate = "VISITOR", Region = "TEST", From = clock.GetUtcNow(), Until = clock.GetUtcNow().AddMinutes(1) });
Check(service.Evaluate(Observe("VISITOR")).Reason == "ACTIVE_VISITOR_PASS", "Visitor valid at start boundary");
clock.Advance(TimeSpan.FromMinutes(1));
Check(service.Evaluate(Observe("VISITOR")).Outcome == "ManualReview", "Visitor invalid at end boundary");
Reject(() => service.AddVisit(new() { Name = "Invalid", Plate = "BAD", Region = "TEST", From = clock.GetUtcNow(), Until = clock.GetUtcNow() }), "Empty visit window rejected");
var concurrentDecision = service.Evaluate(Observe());
var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
Parallel.For(0, 20, _ => ids.Add(service.Open(concurrentDecision.Id).Id));
Check(ids.Distinct().Count() == 1, "Concurrent retries produce one command");
Console.WriteLine($"{checks} checks passed.");

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
