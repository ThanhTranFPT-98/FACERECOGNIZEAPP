using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Data;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.ParkingCore;

var count = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); count++; }
Check(VietnamPlate.Normalize("59-X1\n123.45") == "59X112345", "Two-line motorcycle normalization");
Check(VietnamPlate.CommonFormat("30A12345"), "Common car format");
Check(!VietnamPlate.CommonFormat("3OA12345"), "Ambiguous O/0 never silently corrected");
var lane = new Lane();
var vehicle = new RegisteredVehicle { NormalizedPlate = "30A12345", PersonId = 7 };
var evidence = new Recognition("30A12345", .99, 7, .99, "test");
foreach (var mode in new[] { "PlateOnly", "FaceOnly", "Either", "Both" })
{
    lane.Policy = mode;
    Check(AccessPolicy.Evaluate(lane, evidence, vehicle, false, true).Outcome == "Allow", mode + " matches correct evidence");
}
lane.Policy = "Both";
Check(AccessPolicy.Evaluate(lane, evidence with { PersonId = 8 }, vehicle, false, true).Reason == "PERSON_VEHICLE_CONFLICT", "Face/plate conflict requires review");
vehicle.Blocked = true;
Check(AccessPolicy.Evaluate(lane, evidence, vehicle, true, true).Outcome == "Deny", "Deny list overrides both matches and visitor pass");
vehicle.Blocked = false;
Check(AccessPolicy.Evaluate(lane, evidence with { PlateConfidence = double.NaN }, vehicle, false, true).Outcome == "ManualReview", "NaN cannot authorize");
Check(AccessPolicy.Evaluate(lane, evidence with { FaceConfidence = 1.1 }, vehicle, false, true).Outcome == "ManualReview", "Out-of-range score cannot authorize");
Check(AccessPolicy.Evaluate(lane, evidence, vehicle, false, false).Outcome == "ManualReview", "Unregistered face cannot authorize Both");

// Verify stored templates are ciphertext while legacy vectors remain readable.
using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<ApplicationDBContext>().UseSqlite(connection).Options;
var protection = new EphemeralDataProtectionProvider();
int id;
using (var context = new ApplicationDBContext(options, protection))
{
    context.Database.EnsureCreated();
    var template = new Embedding { Vector = "[0.1,0.2,0.3]" };
    context.Embeddings.Add(template); context.SaveChanges(); id = template.Id;
}
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT Vector FROM Embeddings LIMIT 1";
    var stored = (string)command.ExecuteScalar()!;
    Check(stored.StartsWith("protected:v1:") && !stored.Contains("[0.1"), "New template encrypted in database");
}
using (var context = new ApplicationDBContext(options, protection))
    Check(context.Embeddings.AsNoTracking().Single().Vector == "[0.1,0.2,0.3]", "Protected template decrypts for AI transfer");
using (var command = connection.CreateCommand())
{
    command.CommandText = "UPDATE Embeddings SET Vector = '[0.4,0.5]'"; command.ExecuteNonQuery();
}
using (var context = new ApplicationDBContext(options, protection))
    Check(context.Embeddings.AsNoTracking().Single().Vector == "[0.4,0.5]", "Legacy plaintext template remains readable");

// ---- CR-001: video recording and cloud sync ----
Check(VideoNaming.TryParseStart("20260924T081530.ts", out var parsedStart) && parsedStart == new DateTime(2026, 9, 24, 8, 15, 30, DateTimeKind.Local).ToUniversalTime(), "Segment file name gives its start time (station clock)");
Check(!VideoNaming.TryParseStart("segments.csv", out _), "Non-segment files are ignored");
var keySegment = new VideoSegment { CameraId = "gate-in-1", FileName = "20260924T081530.ts", StartUtc = new DateTime(2026, 9, 24, 1, 15, 30, DateTimeKind.Utc) };
Check(VideoNaming.ObjectKey("uongbi-mb24", keySegment) == "uongbi-mb24/video/gate-in-1/2026/09/24/20260924T081530.ts", "Cloud key groups by site, camera and day");
Check(VideoNaming.Overlaps(new VideoSegment { StartUtc = DateTime.UnixEpoch, EndUtc = DateTime.UnixEpoch.AddMinutes(1) }, DateTime.UnixEpoch.AddSeconds(50), DateTime.UnixEpoch.AddSeconds(70)), "Event window overlapping a segment finds it");

VideoSegment Seg(string name, string status) => new() { FileName = name, Status = status, SizeBytes = 100 };
var ring = new[] { Seg("a", "Recorded"), Seg("b", "Uploaded"), Seg("c", "Uploaded"), Seg("d", "Recorded") };
var chosen = VideoRetention.SelectForDeletion(ring, 400, 250, cloudEnabled: true);
Check(chosen.Select(x => x.FileName).SequenceEqual(new[] { "b", "c" }), "Ring buffer frees oldest uploaded segments and keeps un-uploaded ones");
Check(VideoRetention.SelectForDeletion(ring, 400, 250, cloudEnabled: false).Select(x => x.FileName).SequenceEqual(new[] { "a", "b" }), "Local-only recorder frees the oldest segments");
Check(VideoRetention.SelectForDeletion(ring, 200, 250, cloudEnabled: true).Count == 0, "Nothing is deleted under quota");

var ntp = new byte[48]; ntp[40] = 0xEC; ntp[41] = 0x8F; ntp[42] = 0x2C; ntp[43] = 0x80; ntp[44] = 0x80; // 0xEC8F2C80 = 3968806016 s, fraction 0x80000000 = 0.5 s
Check(Sntp.ReadTimestamp(ntp, 40) == new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(3968806016.5), "NTP timestamp decoding");

var sandbox = Directory.CreateTempSubdirectory("parking-video-test-");
try
{
    var cloudRoot = Path.Combine(sandbox.FullName, "cloud");
    var videoRoot = Path.Combine(sandbox.FullName, "video");
    var evidenceRoot = Path.Combine(sandbox.FullName, "evidence");
    var folder = new FolderObjectStorage(cloudRoot);
    var escaped = false;
    try { await folder.PutAsync("../outside.txt", new MemoryStream(), "text/plain", new Dictionary<string, string>(), default); } catch (InvalidOperationException) { escaped = true; }
    Check(escaped, "Folder storage rejects keys that escape its root");

    using var videoConnection = new SqliteConnection("Data Source=:memory:");
    videoConnection.Open();
    var withOutbox = new DbContextOptionsBuilder<ParkingDb>().UseSqlite(videoConnection).AddInterceptors(new SyncOutboxInterceptor()).Options;
    using var vdb = new ParkingDb(withOutbox);
    vdb.Database.EnsureCreated();
    Directory.CreateDirectory(evidenceRoot);
    File.WriteAllBytes(Path.Combine(evidenceRoot, "photo1.jpg"), new byte[] { 1, 2, 3, 4 });
    var decision = new AccessEvent { LaneId = "entry-1", CorrelationId = "c1", Plate = "30A12345", Outcome = "Allow", EvidenceId = "photo1.jpg" };
    vdb.Events.Add(decision); vdb.Lanes.Add(new Lane());
    await vdb.SaveChangesAsync();
    Check(vdb.Outbox.Count() == 1 && vdb.Outbox.Single().EntityType == "AccessEvent", "Decision written to the outbox in the same save; lanes are not synced");
    decision.Outcome = "Deny"; await vdb.SaveChangesAsync();
    Check(vdb.Outbox.Count() == 2, "Every change to a decision is queued again");

    var cameraDir = Directory.CreateDirectory(Path.Combine(videoRoot, "cam1")).FullName;
    File.WriteAllBytes(Path.Combine(cameraDir, "20260924T081500.ts"), new byte[4096]);
    var start = DateTime.UtcNow.AddHours(-3);
    var segment = new VideoSegment { CameraId = "cam1", FileName = "20260924T081500.ts", StartUtc = start, EndUtc = start.AddMinutes(1), SizeBytes = 4096, Sha256 = "x" };
    vdb.VideoSegments.Add(segment); await vdb.SaveChangesAsync();
    var cloudOptions = new CloudOptions { SiteId = "site-test", BacklogAlertMinutes = 60 };
    var flaky = new FlakyStorage(folder, failures: 3);
    var now = DateTime.UtcNow;
    await CloudUploader.RunOnceAsync(vdb, flaky, cloudOptions, videoRoot, evidenceRoot, now, default);
    Check(vdb.Outbox.Where(x => x.EntityType == "AccessEvent").All(x => x.Status == "Failed" && x.NextAttemptUtc > now), "Failed record upload is scheduled for retry, not dropped");
    Check(vdb.Outbox.Any(x => x.EntityType == "ParkingAlert" && x.Status == "Pending"), "The backlog alert itself is queued for the cloud");
    Check(segment.Status == "Failed" && segment.Attempts == 1 && segment.NextAttemptUtc > now, "Failed video upload backs off");
    Check(vdb.Alerts.Count(x => x.Reason == "UPLOAD_BACKLOG") == 1, "Video older than the backlog limit raises one alert");
    await CloudUploader.RunOnceAsync(vdb, flaky, cloudOptions, videoRoot, evidenceRoot, now, default);
    Check(segment.Attempts == 1 && vdb.Alerts.Count(x => x.Reason == "UPLOAD_BACKLOG") == 1, "No retry before the backoff time; backlog alert not duplicated");
    await CloudUploader.RunOnceAsync(vdb, flaky, cloudOptions, videoRoot, evidenceRoot, now.AddHours(1), default);
    Check(segment.Status == "Uploaded" && segment.ObjectKey is not null && new FileInfo(Path.Combine(cloudRoot, segment.ObjectKey.Replace('/', Path.DirectorySeparatorChar))).Length == 4096, "Video uploaded after the network recovers, size verified");
    Check(vdb.Outbox.All(x => x.Status == "Sent") && File.Exists(Path.Combine(cloudRoot, "site-test", "evidence", "photo1.jpg")), "Records and the evidence photo reach the cloud");
    File.Delete(Path.Combine(cameraDir, "20260924T081500.ts"));
    var lost = new VideoSegment { CameraId = "cam1", FileName = "20260924T081600.ts", StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(2), SizeBytes = 1 };
    vdb.VideoSegments.Add(lost); await vdb.SaveChangesAsync();
    await CloudUploader.RunOnceAsync(vdb, folder, cloudOptions, videoRoot, evidenceRoot, now.AddHours(2), default);
    Check(lost.Status == "Missing", "A segment whose file disappeared is marked Missing instead of retrying forever");

    var throttle = System.Diagnostics.Stopwatch.StartNew();
    await using (var slow = new ThrottledStream(new MemoryStream(new byte[62_500]), 1)) await slow.CopyToAsync(Stream.Null);
    Check(throttle.Elapsed.TotalSeconds >= .4, "Upload bandwidth cap (1 Mbps) is enforced");
}
finally { try { sandbox.Delete(true); } catch (IOException) { } }

Console.WriteLine($"{count} checks passed.");

sealed class FlakyStorage(IObjectStorage inner, int failures) : IObjectStorage
{
    private int remaining = failures;
    public Task PutAsync(string key, Stream content, string contentType, IDictionary<string, string> metadata, CancellationToken cancellation) =>
        Interlocked.Decrement(ref remaining) >= 0 ? throw new HttpRequestException("simulated network outage") : inner.PutAsync(key, content, contentType, metadata, cancellation);
    public Task<long?> SizeAsync(string key, CancellationToken cancellation) => inner.SizeAsync(key, cancellation);
    public string? ReadUrl(string key, TimeSpan validFor) => null;
}
