using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

public sealed class ParkingService(ParkingDb db, ParkingLock mutex, IConfiguration config, IHttpClientFactory clients,
    IHubContext<ParkingHub> hub, IWebHostEnvironment environment, PpeCache ppe, AutoOpenQueue autoOpen)
{
    public async Task<AccessEvent> Observe(string laneId, string correlation, Recognition recognition, string actor, string source, string? evidenceId = null,
        DateTime? receivedUtc = null, DateTime? capturedUtc = null)
    {
        await mutex.Gate.WaitAsync();
        try
        {
            var existing = await db.Events.SingleOrDefaultAsync(x => x.CorrelationId == correlation);
            if (existing is not null)
            {
                if (evidenceId is not null && evidenceId != existing.EvidenceId) DeleteEvidence(evidenceId);
                if (existing.LaneId != laneId) throw new InvalidOperationException("Correlation ID belongs to another lane.");
                return existing;
            }
            var lane = await db.Lanes.FindAsync(laneId) ?? throw new InvalidOperationException("Unknown lane.");
            if (source == "Simulation" && !lane.Simulation) throw new InvalidOperationException("Manual observations are restricted to simulated lanes.");
            var now = DateTime.UtcNow;
            var plate = VietnamPlate.Normalize(recognition.Plate);
            recognition = recognition with { Plate = plate };
            var vehicle = await db.Vehicles.SingleOrDefaultAsync(x => x.NormalizedPlate == plate);
            var visit = await db.Visits.Where(x => x.Plate == plate && !x.Revoked && x.FromUtc <= now && now < x.UntilUtc && x.Entries < x.MaxEntries)
                .OrderBy(x => x.UntilUtc).FirstOrDefaultAsync();
            // Only active, nonblocked enrolled vehicle owners can authorize face-based parking access.
            var faceAuthorized = await FaceAuthorized(lane, recognition.PersonId);
            var openSession = lane.Direction == "Exit" && plate.Length > 0 ? await db.Sessions.Where(x => x.Plate == plate && x.ExitUtc == null).OrderByDescending(x => x.EntryUtc).FirstOrDefaultAsync() : null;
            var inside = openSession is not null;
            var result = AccessPolicy.Evaluate(lane, recognition, lane.Subject == "Person" ? null : await PolicyVehicle(vehicle, recognition.PersonId), visit is not null || inside, faceAuthorized, await db.PlateRules(), now);
            if (result.Outcome == "Allow" && lane.Subject == "Vehicle")
            {
                var site = await db.Site();
                // GC-15: some categories (e.g. Delivery) are never opened automatically.
                if (vehicle is not null && site.ReviewCategories.Split(',', StringSplitOptions.TrimEntries).Contains(vehicle.Category)) result = new("ManualReview", "CATEGORY_REVIEW:" + vehicle.Category);
                // GC-12: an exit needs a matching entry when the lane asks for it.
                else if (lane.Direction == "Exit" && lane.ExitRequiresEntry && !inside) result = new("ManualReview", "EXIT_WITHOUT_ENTRY");
                // GC-13: the person leaving must be the one who drove in, when both were identified.
                else if (openSession?.EntryPersonId is int driver && recognition.PersonId is int leaving && leaving != driver) result = new("ManualReview", "EXIT_PERSON_MISMATCH");
            }
            // PPE (Plan V2 §3.9) can only tighten an Allow: never turn a review or deny into an opening.
            string? ppeOutcome = null, ppeSummary = null, ppeWarning = null;
            if (lane.Direction == "Entry" && !string.IsNullOrWhiteSpace(lane.PpeRules))
            {
                var site = await db.Site();
                var verdict = PpeRules.Decide(ppe.Recent(lane.Id), PpeRules.Parse(lane.PpeRules), now, TimeSpan.FromSeconds(site.PpeWindowSeconds), site.PpeConfirmCount, lane.PpeMissingMode);
                ppeOutcome = verdict.Outcome; ppeSummary = verdict.Summary;
                if (result.Outcome == "Allow")
                {
                    if (verdict.Outcome == "Deny") result = new("Deny", verdict.Reason);
                    else if (verdict.Outcome == "Review") result = new("ManualReview", verdict.Reason);
                    else if (verdict.Outcome == "Warn") ppeWarning = verdict.Reason;
                }
            }
            var evidenceHash = evidenceId is null ? null : HashEvidence(evidenceId);
            var shiftName = await ShiftFor(lane, now);
            var entry = new AccessEvent { LaneId = laneId, CorrelationId = correlation, Plate = plate, EvidenceSha256 = evidenceHash,
                PlateConfidence = recognition.PlateConfidence, PersonId = recognition.PersonId, FaceConfidence = recognition.FaceConfidence,
                ModelVersion = recognition.ModelVersion, Source = source, EvidenceId = evidenceId, Outcome = result.Outcome, Reason = result.Reason,
                VisitId = vehicle is null && lane.Direction == "Entry" ? visit?.Id : null, ExpiresUtc = now + (await db.Timings(lane)).DecisionValid,
                ReceivedUtc = receivedUtc ?? now, CapturedUtc = capturedUtc, CreatedUtc = now, PpeOutcome = ppeOutcome, PpeSummary = ppeSummary, Shift = shiftName };
            db.Events.Add(entry);
            if (result.Outcome != "Allow") db.Alerts.Add(new() { EventId = entry.Id, Reason = result.Reason, Severity = result.Outcome == "Deny" ? "High" : "Medium" });
            if (ppeWarning is not null) db.Alerts.Add(new() { EventId = entry.Id, Reason = ppeWarning, Severity = "Medium" });
            db.Log(actor, "AccessDecision:" + result.Outcome, entry.Id);
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync("ParkingChanged", new { eventId = entry.Id });
            return entry;
        }
        finally { mutex.Gate.Release(); }
    }

    public async Task<AccessEvent> Review(Guid id, bool approve, string note, string actor)
    {
        await mutex.Gate.WaitAsync();
        try
        {
            var entry = await db.Events.FindAsync(id) ?? throw new InvalidOperationException("Event not found.");
            if (entry.Outcome != "ManualReview" || entry.ReviewBy is not null) throw new InvalidOperationException("Only pending reviews can be resolved once.");
            var lane = await db.Lanes.FindAsync(entry.LaneId) ?? throw new InvalidOperationException("Lane missing.");
            var timings = await db.Timings(lane);
            if (DateTime.UtcNow - entry.CreatedUtc > timings.ReviewWindow) throw new InvalidOperationException("Review expired; capture a new observation.");
            entry.Outcome = approve ? "Allow" : "Deny";
            entry.Reason = approve ? "GUARD_APPROVED" : "GUARD_REJECTED";
            entry.ReviewBy = actor; entry.ReviewNote = note.Trim(); entry.ExpiresUtc = DateTime.UtcNow + timings.DecisionValid;
            db.Log(actor, entry.Reason + ":" + note.Trim(), id);
            await db.SaveChangesAsync();
            return entry;
        }
        finally { mutex.Gate.Release(); }
    }

    public async Task<DeviceCommand> Open(Guid id, string actor, bool automatic = false)
    {
        DeviceCommand command; string laneId; DateTime expires;
        await mutex.Gate.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var prior = await db.Commands.SingleOrDefaultAsync(x => x.EventId == id);
            if (prior is not null) return prior;
            var entry = await db.Events.FindAsync(id) ?? throw new InvalidOperationException("Event not found.");
            var lane = await db.Lanes.FindAsync(entry.LaneId) ?? throw new InvalidOperationException("Lane missing.");
            var now = DateTime.UtcNow;
            if (entry.Outcome != "Allow" || entry.ExpiresUtc <= now) throw new InvalidOperationException("An unexpired Allow decision is required.");
            if (lane.ShadowMode) throw new InvalidOperationException("Shadow mode records decisions without opening a barrier.");
            if (automatic && !lane.AutoOpen) throw new InvalidOperationException("Automatic opening has been disabled.");
            var vehicle = await db.Vehicles.SingleOrDefaultAsync(x => x.NormalizedPlate == entry.Plate);
            if (vehicle is { Blocked: true } or { Active: false }) throw new InvalidOperationException("Vehicle authorization has been revoked.");
            if (entry.Source == "Simulation" && !lane.Simulation) throw new InvalidOperationException("Simulated evidence cannot operate a live lane.");
            if (lane.RequireInspectionPhoto && !await db.EvidenceImages.AnyAsync(x => x.EventId == id && x.Kind == "Inspection"))
                throw new InvalidOperationException("This lane needs an inspection photo before opening. Take the photo first.");
            var timings = await db.Timings(lane);
            var since = now - timings.Cooldown;
            if (await db.Commands.AnyAsync(x => x.LaneId == lane.Id && (x.Status == "Unknown" || x.Status == "Pending" || (x.Status == "Acknowledged" && x.PassageUtc == null) || x.CreatedUtc > since)))
                throw new InvalidOperationException("Lane has a recent or unresolved command. Check its state before retrying.");
            Visit? visit = entry.VisitId.HasValue ? await db.Visits.FindAsync(entry.VisitId.Value) : null;
            if (visit is not null && (visit.Revoked || now < visit.FromUtc || now >= visit.UntilUtc || visit.Entries >= visit.MaxEntries))
                throw new InvalidOperationException("Visitor pass is no longer valid.");
            if (entry.ReviewBy is null)
            {
                var faceAuthorized = await FaceAuthorized(lane, entry.PersonId);
                var inside = lane.Direction == "Exit" && await db.Sessions.AnyAsync(x => x.Plate == entry.Plate && x.ExitUtc == null);
                var current = AccessPolicy.Evaluate(lane, new(entry.Plate, entry.PlateConfidence, entry.PersonId, entry.FaceConfidence, entry.ModelVersion), lane.Subject == "Person" ? null : await PolicyVehicle(vehicle, entry.PersonId), visit is not null || inside, faceAuthorized, await db.PlateRules(), now);
                if (current.Outcome != "Allow") throw new InvalidOperationException("Authorization changed; capture a new observation.");
            }
            if (!lane.Simulation)
            {
                if (!config.GetValue<bool>("Parking:EnableLiveBarrier") || string.IsNullOrWhiteSpace(config["Parking:BarrierUrl"])) throw new InvalidOperationException("Live adapter has not been enabled and configured.");
                if (lane.LastHeartbeatUtc is null || now - lane.LastHeartbeatUtc > timings.HeartbeatFresh || !lane.SafetyHealthy || !lane.VehiclePresent || lane.Obstructed || lane.BarrierState != "Closed")
                    throw new InvalidOperationException("Fresh healthy sensor feedback and a closed, clear barrier are required.");
            }
            command = new DeviceCommand { EventId = id, LaneId = lane.Id, Status = lane.Simulation ? "Simulated" : "Pending" };
            // Reserve a visitor entry conservatively before I/O; uncertain hardware outcomes never refund automatically.
            if (visit is not null && lane.Direction == "Entry") visit.Entries++;
            db.Commands.Add(command); db.Log(actor, "BarrierRequested", command.Id);
            await db.SaveChangesAsync();
            if (lane.Simulation)
            {
                db.Log(actor, "BarrierResult:" + command.Status, command.Id);
                await db.SaveChangesAsync();
                await hub.Clients.All.SendAsync("ParkingChanged", new { commandId = command.Id });
                return command;
            }
            laneId = lane.Id; expires = entry.ExpiresUtc;
        }
        finally { mutex.Gate.Release(); }

        // GC-23: the controller call runs WITHOUT the global lock, so a slow controller on one lane doesn't delay decisions on the others.
        // Safe because the command is already stored as Pending: that blocks every other opening on this lane, and the unique EventId blocks a duplicate.
        string status; DateTime? ackUtc = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config["Parking:BarrierUrl"]!.TrimEnd('/') + "/commands");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Parking:BarrierKey"]);
            request.Content = JsonContent.Create(new { commandId = command.Id, laneId, action = "Open", expiresUtc = expires });
            using var response = await clients.CreateClient("parking-devices").SendAsync(request);
            response.EnsureSuccessStatusCode();
            var ack = await response.Content.ReadFromJsonAsync<BridgeAck>();
            status = ack?.CommandId == command.Id && ack.Accepted ? "Acknowledged" : "Unknown";
            if (status == "Acknowledged") ackUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { status = "Unknown"; }

        await mutex.Gate.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var stored = await db.Commands.SingleAsync(x => x.Id == command.Id);
            // Only Pending is updated; a passage reported meanwhile (PassageUtc) is kept.
            if (stored.Status == "Pending") { stored.Status = status; stored.AckUtc = ackUtc; }
            if (status == "Unknown") db.Alerts.Add(new() { EventId = id, Severity = "Critical", Reason = "BARRIER_OUTCOME_UNKNOWN" });
            db.Log(actor, "BarrierResult:" + stored.Status, stored.Id);
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync("ParkingChanged", new { commandId = stored.Id });
            return stored;
        }
        finally { mutex.Gate.Release(); }
    }

    // Vehicle lanes: a face only counts for a person who drives an active vehicle. Person lanes: the person must be on the access list.
    private async Task<bool> FaceAuthorized(Lane lane, int? personId)
    {
        if (!personId.HasValue) return false;
        if (lane.Subject == "Person") return AuthorizedPerson.Allowed(await db.People.FindAsync(personId.Value), DateTime.UtcNow);
        return await db.Vehicles.AnyAsync(v => v.Active && !v.Blocked && (v.PersonId == personId || v.AuthorizedPeople.Any(p => p.PersonId == personId)));
    }
    private async Task<RegisteredVehicle?> PolicyVehicle(RegisteredVehicle? vehicle, int? personId)
    {
        if (vehicle is null || !personId.HasValue || !await db.VehiclePeople.AnyAsync(x => x.VehicleId == vehicle.Id && x.PersonId == personId)) return vehicle;
        // A detached view keeps the primary owner unchanged while checking a shared-vehicle relationship.
        return new RegisteredVehicle { Id = vehicle.Id, PersonId = personId, Active = vehicle.Active, Blocked = vehicle.Blocked };
    }
    public async Task<Recognition> Recognize(IFormFile image, IFormFile? face, CancellationToken cancellation = default)
    {
        if (image.Length is <= 0 or > 5_000_000 || image.ContentType is not ("image/jpeg" or "image/png")) throw new InvalidOperationException("Upload a JPEG or PNG up to 5 MB.");
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(image.OpenReadStream()), "file", "plate.jpg");
        var client = clients.CreateClient("parking-ai");
        using var response = await client.PostAsync((config["Parking:PlateAiUrl"] ?? "http://127.0.0.1:8001").TrimEnd('/') + "/v1/recognition/plate", content, cancellation);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PlateResult>() ?? throw new InvalidOperationException("Empty AI result.");
        int? person = null; double score = 0;
        if (face is not null)
        {
            if (face.Length is <= 0 or > 5_000_000 || face.ContentType is not ("image/jpeg" or "image/png")) throw new InvalidOperationException("Invalid face image.");
            using var faceContent = new MultipartFormDataContent();
            faceContent.Add(new StreamContent(face.OpenReadStream()), "Image", "face.jpg");
            using var faceResponse = await client.PostAsync((config["Parking:FaceAiUrl"] ?? "http://127.0.0.1:8000").TrimEnd('/') + "/face/recognize", faceContent, cancellation);
            faceResponse.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await faceResponse.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (root.GetProperty("totalFaces").GetInt32() == 1 && root.GetProperty("recognizedFaces").GetArrayLength() == 1)
            {
                var match = root.GetProperty("recognizedFaces")[0];
                person = match.GetProperty("peopleId").GetInt32(); score = match.GetProperty("confidence").GetDouble();
            }
        }
        // Baseline OCR reports calibrated=false: retain the candidate but force review until site evaluation approves a model.
        var approved = config["Parking:ApprovedPlateModel"];
        var confidence = result.ModelVersion == approved ? result.Confidence : 0;
        if (!config.GetValue<bool>("Parking:EnableFaceAuthorization")) score = 0;
        return new(result.Plate, confidence, person, score, result.ModelVersion);
    }
    public async Task<string> SaveEvidence(IFormFile file)
    {
        var directory = EvidenceStorage.Directory(environment, config);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N") + (file.ContentType == "image/png" ? ".png" : ".jpg");
        await using var stream = File.Create(Path.Combine(directory, id));
        await file.CopyToAsync(stream);
        return id;
    }
    public async Task<AccessEvent> Capture(string laneId, string correlation, IFormFile image, IFormFile? face, string actor)
    {
        var recognition = await Recognize(image, face);
        var id = await SaveEvidence(image);
        AccessEvent entry;
        try { entry = await Observe(laneId, correlation, recognition, actor, "Camera", id); }
        catch { DeleteEvidence(id); throw; }
        await AutoOpen(entry); return entry;
    }
    // Entry point for adapters (third-party cameras, face terminals): same policy path as every other input, then optional auto-open.
    public async Task<AccessEvent> Ingest(string laneId, string correlation, Recognition recognition, string actor, string source, string? evidenceId,
        DateTime receivedUtc, DateTime? capturedUtc)
    {
        AccessEvent entry;
        try { entry = await Observe(laneId, correlation, recognition, actor, source, evidenceId, receivedUtc, capturedUtc); }
        catch { if (evidenceId is not null) DeleteEvidence(evidenceId); throw; }
        // The adapter worker must not wait for the barrier controller; the opening runs on the auto-open queue.
        if (entry.Outcome == "Allow") autoOpen.Enqueue(entry.Id);
        return entry;
    }
    public async Task TryAutoOpen(Guid eventId)
    {
        var entry = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId);
        if (entry is not null) await AutoOpen(entry);
    }
    // Stores image bytes from a device after checking they really are JPEG/PNG. Returns null for anything else.
    public string? SaveEvidenceBytes(byte[] data)
    {
        var png = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
        var jpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        if ((!png && !jpeg) || data.Length > 5_000_000) return null;
        var directory = EvidenceStorage.Directory(environment, config);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N") + (png ? ".png" : ".jpg");
        File.WriteAllBytes(Path.Combine(directory, id), data);
        return id;
    }
    private async Task<string?> ShiftFor(Lane lane, DateTime utc)
    {
        var areaId = await db.Locations.Where(l => l.Id == lane.LocationId).Select(l => (Guid?)l.AreaId).SingleOrDefaultAsync();
        if (areaId is null) return null;
        var shifts = await db.Shifts.Where(s => s.AreaId == areaId).ToListAsync();
        if (shifts.Count == 0) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await db.Site()).TimeZone);
        return PresenceBook.ShiftAt(shifts, TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
    }
    private string? HashEvidence(string id)
    {
        var path = Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(id));
        return File.Exists(path) ? EvidenceImage.Hash(File.ReadAllBytes(path)) : null;
    }
    private void DeleteEvidence(string id) => File.Delete(Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(id)));
    public async Task<AccessEvent> CaptureBurst(string laneId, string correlation, List<IFormFile> images, string actor)
    {
        if (images.Count is < 2 or > 3) throw new InvalidOperationException("Send two or three correlated frames.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var results = new List<Recognition>();
        foreach (var image in images) results.Add(await Recognize(image, null, timeout.Token));
        var rules = await db.PlateRules();
        var usable = results.Where(x => rules.Valid(VietnamPlate.Normalize(x.Plate))).ToList();
        var agreed = usable.Count >= 2 && usable.Select(x => x.Plate).Distinct().Count() == 1 && usable.Select(x => x.ModelVersion).Distinct().Count() == 1;
        var observation = agreed ? usable[0] with { PlateConfidence = usable.Min(x => x.PlateConfidence) }
            : new Recognition("", 0, null, 0, "burst-no-consensus");
        var id = await SaveEvidence(images[0]);
        AccessEvent entry;
        try { entry = await Observe(laneId, correlation, observation, actor, "CameraBurst", id); }
        catch { DeleteEvidence(id); throw; }
        await AutoOpen(entry); return entry;
    }
    private async Task AutoOpen(AccessEvent entry)
    {
        var lane = await db.Lanes.AsNoTracking().SingleAsync(x => x.Id == entry.LaneId);
        if (!lane.AutoOpen || lane.ShadowMode || entry.Outcome != "Allow") return;
        try { await Open(entry.Id, "automatic-policy", automatic: true); }
        catch (InvalidOperationException)
        {
            if (!await db.Alerts.AnyAsync(x => x.EventId == entry.Id && x.Reason == "AUTO_OPEN_HELD"))
            { db.Alerts.Add(new() { EventId = entry.Id, Reason = "AUTO_OPEN_HELD", Severity = "High" }); await db.SaveChangesAsync(); }
        }
    }
    // Caller holds the process-wide gate. Only controller sensor feedback or explicit simulation confirms passage.
    public async Task ConfirmPassage(DeviceCommand command, Lane lane)
    {
        if (command.PassageUtc.HasValue) return;
        // Pending counts too: a fast controller can report the passage before our HTTP call has returned its ack.
        if (command.Status is not ("Acknowledged" or "Simulated" or "Unknown" or "Reconciled" or "Pending")) throw new InvalidOperationException("Command has no possible passage to confirm.");
        var entry = await db.Events.FindAsync(command.EventId) ?? throw new InvalidOperationException("Event missing.");
        command.PassageUtc = DateTime.UtcNow;
        // Presence is recorded for every lane in an area, people and vehicles (GC-18); parking sessions only for vehicles.
        await PresenceBook.Record(db, config, lane, entry, command.PassageUtc.Value);
        if (lane.Subject == "Person" || entry.Plate.Length == 0) { db.Log("lane-controller", "PassageConfirmed", command.Id); return; }
        if (lane.Direction == "Entry")
        {
            if (await db.Sessions.AnyAsync(x => x.Plate == entry.Plate && x.ExitUtc == null))
                db.Alerts.Add(new() { EventId = entry.Id, Reason = "DUPLICATE_ENTRY", Severity = "High" });
            else db.Sessions.Add(new() { Plate = entry.Plate, EntryCommandId = command.Id, EntryUtc = command.PassageUtc.Value, EntryPersonId = entry.PersonId });
        }
        else
        {
            var session = await db.Sessions.Where(x => x.Plate == entry.Plate && x.ExitUtc == null).OrderByDescending(x => x.EntryUtc).FirstOrDefaultAsync();
            if (session is not null) { session.ExitCommandId = command.Id; session.ExitUtc = command.PassageUtc; }
            else db.Alerts.Add(new() { EventId = entry.Id, Reason = "EXIT_WITHOUT_ENTRY", Severity = "Medium" });
        }
        db.Log("lane-controller", "PassageConfirmed", command.Id);
    }
    private record PlateResult(string Plate, double Confidence, string ModelVersion);
    private record BridgeAck(Guid CommandId, bool Accepted);
}
public static class EvidenceStorage
{
    public static string Directory(IWebHostEnvironment environment, IConfiguration config) =>
        config["Parking:EvidenceDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "evidence");
}
