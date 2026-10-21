using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using DSAVehicle.Data;
using DSAVehicle.Models;
using DSAVehicle.ParkingCore;

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

// ---- CR-002: areas and locations ----
{
    // Upgrade path: a site on the previous schema with lanes and no areas.
    using var areaConnection = new SqliteConnection("Data Source=:memory:");
    areaConnection.Open();
    var areaOptions = new DbContextOptionsBuilder<ParkingDb>().UseSqlite(areaConnection).Options;
    using (var old = new ParkingDb(areaOptions))
    {
        await old.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20260924105312_VideoCloudSync");
        await old.Database.ExecuteSqlRawAsync("INSERT INTO Lanes (Id, Direction, Policy, PlateThreshold, FaceThreshold, ShadowMode, Simulation, AutoOpen, SafetyHealthy, VehiclePresent, Obstructed, BarrierState) VALUES ('entry-1','Entry','PlateOnly',0.95,0.8,1,1,0,0,0,0,'Unknown')");
    }
    using var adb = new ParkingDb(areaOptions);
    await adb.Database.MigrateAsync();
    Check((await adb.Lanes.SingleAsync()).Id == "entry-1", "Existing lane survives the SQLite table rebuild for the new foreign key");
    var fallback = await AreaCatalog.EnsureDefault(adb); await adb.SaveChangesAsync();
    Check((await adb.Lanes.SingleAsync()).LocationId == fallback.Id, "Upgraded lanes are placed in the DEFAULT area");
    await AreaCatalog.EnsureDefault(adb); await adb.SaveChangesAsync();
    Check(await adb.Areas.CountAsync() == 1 && await adb.Locations.CountAsync() == 1, "Default area is created only once");

    var mine = await AreaCatalog.SaveArea(adb, new AreaInput { Code = "gieng-chinh", Name = "Cửa lò Giếng chính", Kind = "Mine" }); await adb.SaveChangesAsync();
    Check(mine.Code == "GIENG-CHINH", "Area codes are stored upper-case");
    var dup = await Throws(() => AreaCatalog.SaveArea(adb, new AreaInput { Code = "GIENG-chinh", Name = "Copy" }));
    Check(dup, "Duplicate area code is refused (case-insensitive)");
    var shaft = await AreaCatalog.SaveLocation(adb, new LocationInput { AreaId = mine.Id, Name = "Flap lanes 1-6" }); await adb.SaveChangesAsync();
    Check(await Throws(() => AreaCatalog.SaveLocation(adb, new LocationInput { AreaId = mine.Id, Name = "Flap lanes 1-6" })), "Duplicate location name in one area is refused");
    Check(await Throws(() => AreaCatalog.SaveLocation(adb, new LocationInput { AreaId = Guid.NewGuid(), Name = "Nowhere" })), "Location needs an existing area");

    adb.Lanes.Add(new Lane { Id = "flap-1", LocationId = shaft.Id }); await adb.SaveChangesAsync();
    adb.Events.Add(new AccessEvent { CorrelationId = "a1", LaneId = "flap-1" });
    adb.Events.Add(new AccessEvent { CorrelationId = "a2", LaneId = "entry-1" }); await adb.SaveChangesAsync();
    var laneIds = AreaCatalog.LaneIds(adb, mine.Id);
    var inMine = await adb.Events.Where(x => laneIds.Contains(x.LaneId)).Select(x => x.CorrelationId).ToListAsync();
    Check(inMine.SequenceEqual(new[] { "a1" }), "Area filter returns only events from that area's lanes");

    Check(await Throws(() => AreaCatalog.DeleteLocation(adb, shaft.Id)), "A location with lanes cannot be deleted");
    Check(await Throws(() => AreaCatalog.DeleteArea(adb, mine.Id)), "An area with locations cannot be deleted");
    Check(await Throws(() => AreaCatalog.DeleteArea(adb, fallback.AreaId)), "The DEFAULT area cannot be deleted");
    adb.ChangeTracker.Clear();
    (await adb.Lanes.FindAsync("flap-1"))!.LocationId = fallback.Id; await adb.SaveChangesAsync();
    await AreaCatalog.DeleteLocation(adb, shaft.Id); await adb.SaveChangesAsync();
    await AreaCatalog.DeleteArea(adb, mine.Id); await adb.SaveChangesAsync();
    Check(!await adb.Areas.AnyAsync(x => x.Id == mine.Id), "Empty location and area can be deleted");

    adb.Lanes.Add(new Lane { Id = "ghost", LocationId = Guid.NewGuid() });
    Check(await Throws(() => adb.SaveChangesAsync()), "Database foreign key rejects a lane pointing at a missing location");
}

// ---- CR-003: configurable timings ----
{
    var site = new SiteSettings();
    var defaults = LaneTimings.Resolve(site, new Lane());
    Check(defaults == new LaneTimings(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)),
        "Default timings equal the former hard-coded constants");
    var moto = LaneTimings.Resolve(site, new Lane { CooldownSeconds = 4 });
    Check(moto.Cooldown == TimeSpan.FromSeconds(4) && moto.DecisionValid == TimeSpan.FromSeconds(30), "Lane override changes only its own value");
    site.DecisionValidSeconds = 45;
    Check(LaneTimings.Resolve(site, new Lane()).DecisionValid == TimeSpan.FromSeconds(45), "Lanes without an override follow the site default");
    Check(LaneTimings.Resolve(site, new Lane { DecisionValidSeconds = 60 }).DecisionValid == TimeSpan.FromSeconds(60), "Lane override wins over the site default");

    using var timingConnection = new SqliteConnection("Data Source=:memory:");
    timingConnection.Open();
    using var tdb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(timingConnection).Options);
    await tdb.Database.MigrateAsync();
    Check((await tdb.Site()).CooldownSeconds == 8, "A database without a settings row uses the built-in defaults");

    var bad = new TimingInput { CooldownSeconds = 0 };
    var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
    Check(!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(bad, new(bad), errors, true), "A zero cooldown is rejected (double-opening guard)");
}

// ---- CR-004: roles, permissions, expiry ----
{
    using var roleConnection = new SqliteConnection("Data Source=:memory:");
    roleConnection.Open();
    using var rdb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(roleConnection).Options);
    await rdb.Database.MigrateAsync();
    await RoleCatalog.Seed(rdb); await rdb.SaveChangesAsync();
    await RoleCatalog.Seed(rdb); await rdb.SaveChangesAsync();
    Check(await rdb.Roles.CountAsync() == 7, "Seven built-in roles, seeded once");
    Check((await RoleCatalog.Permissions(rdb, "Guard")).SetEquals(new[] { Perm.ViewLive, Perm.ViewEvidence, Perm.OperateLane, Perm.ManageVisitors, Perm.HandleAlerts }), "Guard keeps its former rights");
    Check((await RoleCatalog.Permissions(rdb, "Auditor")).SetEquals(new[] { Perm.ViewLive, Perm.ViewAudit }), "Auditor keeps its former rights");
    Check((await RoleCatalog.Permissions(rdb, "Admin")).SetEquals(Perm.All), "Admin has every permission");
    Check((await RoleCatalog.Permissions(rdb, "Device")).Count == 0, "Device principal gets no user permissions");

    Check(await Throws(() => RoleCatalog.Save(rdb, new RoleInput { Id = "Admin", Name = "x", Permissions = [] })), "Admin role cannot be edited");
    Check(await Throws(() => RoleCatalog.Save(rdb, new RoleInput { Id = "Lead", Name = "x", Permissions = ["OpenAnything"] })), "Unknown permission is refused");
    await RoleCatalog.Save(rdb, new RoleInput { Id = "Lead", Name = "Gate lead", Permissions = [Perm.OperateLane] }); await rdb.SaveChangesAsync();
    Check((await RoleCatalog.Permissions(rdb, "Lead")).SetEquals(new[] { Perm.OperateLane, Perm.ViewLive }), "Custom role gets ViewLive automatically");
    await RoleCatalog.Save(rdb, new RoleInput { Id = "Guard", Name = "Guard", Permissions = [Perm.OperateLane] }); await rdb.SaveChangesAsync();
    await RoleCatalog.Seed(rdb); await rdb.SaveChangesAsync();
    Check(!(await RoleCatalog.Permissions(rdb, "Guard")).Contains(Perm.HandleAlerts), "Re-seeding keeps an admin's changes to a built-in role");

    ParkingUser NewUser(string name, string role) { var u = new ParkingUser { Username = name, Role = role }; rdb.Users.Add(u); return u; }
    var boss = NewUser("BOSS", "Admin"); var guard = NewUser("G1", "Guard"); var lead = NewUser("L1", "Lead");
    await rdb.SaveChangesAsync();
    Check(await Throws(() => RoleCatalog.UpdateUser(rdb, boss.Id, new UserUpdate { Role = "Guard" }, "BOSS", true)), "You cannot change your own role");
    Check(await Throws(() => RoleCatalog.UpdateUser(rdb, guard.Id, new UserUpdate { Role = "Admin" }, "L1", false)), "A non-admin cannot grant Admin");
    Check(await Throws(() => RoleCatalog.UpdateUser(rdb, boss.Id, new UserUpdate { Role = "Guard" }, "L1", false)), "A non-admin cannot change an admin");
    Check(await Throws(() => RoleCatalog.UpdateUser(rdb, guard.Id, new UserUpdate { Role = "Nope" }, "BOSS", true)), "Unknown role is refused");
    var second = NewUser("BOSS2", "Admin"); await rdb.SaveChangesAsync();
    await RoleCatalog.UpdateUser(rdb, second.Id, new UserUpdate { Role = "Guard" }, "BOSS", true); await rdb.SaveChangesAsync();
    Check(second.Role == "Guard", "An admin can demote another admin while one admin remains");
    second.Role = "Admin"; boss.Active = false; await rdb.SaveChangesAsync();
    Check(await Throws(() => RoleCatalog.UpdateUser(rdb, second.Id, new UserUpdate { Role = "Admin", ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) }, "BOSS", true)), "The last active admin cannot be expired");
    Check(!RoleCatalog.Usable(new ParkingUser { ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) }, DateTime.UtcNow), "Expired account is not usable");
    Check(RoleCatalog.Usable(new ParkingUser { ExpiresUtc = DateTime.UtcNow.AddDays(1) }, DateTime.UtcNow), "Account before its expiry is usable");
    Check(await Throws(() => RoleCatalog.Delete(rdb, "Lead")), "A role with users cannot be deleted");
    Check(await Throws(() => RoleCatalog.Delete(rdb, "Viewer")), "A built-in role cannot be deleted");
}

// ---- CR-005: area scope for accounts ----
{
    using var scopeConnection = new SqliteConnection("Data Source=:memory:");
    scopeConnection.Open();
    using var sdb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(scopeConnection).Options);
    await sdb.Database.MigrateAsync();
    var gate1 = await AreaCatalog.SaveArea(sdb, new AreaInput { Code = "G1", Name = "Gate I" });
    var gate2 = await AreaCatalog.SaveArea(sdb, new AreaInput { Code = "G2", Name = "Gate II" }); await sdb.SaveChangesAsync();
    var loc1 = await AreaCatalog.SaveLocation(sdb, new LocationInput { AreaId = gate1.Id, Name = "L" });
    var loc2 = await AreaCatalog.SaveLocation(sdb, new LocationInput { AreaId = gate2.Id, Name = "L" }); await sdb.SaveChangesAsync();
    sdb.Lanes.AddRange(new Lane { Id = "g1-in", LocationId = loc1.Id }, new Lane { Id = "g2-in", LocationId = loc2.Id });
    var guardUser = new ParkingUser { Username = "G", Role = "Guard" }; var bossUser = new ParkingUser { Username = "B", Role = "Admin" };
    sdb.Users.AddRange(guardUser, bossUser); await sdb.SaveChangesAsync();

    Check(await AreaScope.LanesFor(sdb, guardUser.Id, "Guard") is null, "An account without areas is unlimited (existing accounts unchanged)");
    await AreaScope.SetAreas(sdb, guardUser, [gate2.Id]); await sdb.SaveChangesAsync();
    Check((await AreaScope.LanesFor(sdb, guardUser.Id, "Guard"))!.SetEquals(new[] { "g2-in" }), "A limited account sees only its areas' lanes");
    Check(await Throws(() => AreaScope.SetAreas(sdb, bossUser, [gate1.Id])), "Admin accounts cannot be limited");
    Check(await Throws(() => AreaScope.SetAreas(sdb, guardUser, [Guid.NewGuid()])), "Unknown area is refused");
    Check(await Throws(() => AreaCatalog.DeleteArea(sdb, gate2.Id)), "An area that limits an account cannot be deleted");

    var lanesOf = new Dictionary<Guid, string>();
    var withEvent = new ParkingAlert { EventId = Guid.NewGuid(), Reason = "INSUFFICIENT_IDENTITY_EVIDENCE" }; lanesOf[withEvent.EventId!.Value] = "g1-in";
    Check(AreaScope.AlertLane(withEvent, lanesOf) == "g1-in", "Alert lane comes from its event");
    Check(AreaScope.AlertLane(new ParkingAlert { Reason = "DEVICE_OFFLINE:g2-in" }, lanesOf) == "g2-in", "Device alert lane comes from its reason");
    Check(AreaScope.AlertLane(new ParkingAlert { Reason = "CLOCK_DRIFT" }, lanesOf) is null, "Site-wide alert has no lane");
    await AreaScope.SetAreas(sdb, guardUser, []); await sdb.SaveChangesAsync();
    Check(await AreaScope.LanesFor(sdb, guardUser.Id, "Guard") is null, "Clearing the areas makes the account unlimited again");
}

// ---- CR-006: plate formats per site ----
{
    Check(PlateRules.Default.Valid("30A12345") && !PlateRules.Default.Valid("AB1234"), "Empty site patterns keep the Vietnamese civilian formats");
    var custom = new PlateRules("[A-Z]{2}[0-9]{4}\n  \n80NG[0-9]{3,5}");
    Check(custom.Valid("AB1234") && custom.Valid("80NG12345"), "Custom patterns accept their formats (one per line, blanks ignored)");
    Check(!custom.Valid("30A12345"), "Custom patterns replace the built-in ones");
    Check(!custom.Valid("XAB1234"), "Patterns are anchored to the whole plate");
    Check(PlateRules.Check("[A-Z") is not null && PlateRules.Check("[A-Z]{2}\n[0-9]+") is null, "Invalid regex is reported before saving");
    var plateLane = new Lane { Policy = "PlateOnly", PlateThreshold = .9 };
    var foreign = new RegisteredVehicle { NormalizedPlate = "AB1234" };
    Check(AccessPolicy.Evaluate(plateLane, new("AB1234", .99, null, 0, "t"), foreign, false, false, custom).Outcome == "Allow", "Policy uses the site's plate rules");
    Check(AccessPolicy.Evaluate(plateLane, new("AB1234", .99, null, 0, "t"), foreign, false, false).Outcome == "ManualReview", "Without site rules the same plate goes to review");
}

// ---- CR-007: third-party camera receiver ----
{
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    // Same shape as the T1550FAI-Z capture: two PlateInfo items must give two observations.
    var message = System.Text.Json.JsonDocument.Parse("""
        {"data":{"dev_net_info":[{"device_name":"T1550FAI-Z","mac":"38-24-F1-0E-80-F8","ip":"192.168.1.6"}],
         "ai_snap_picture":{"PlateInfo":[{"Id":"","GrpId":1,"SnapId":"30A12345","StrChn":"CH1","StartTime":1766061353,"PlateImg":"PNG","BgImg":"PNG"},
                                         {"Id":"","GrpId":2,"SnapId":"35A39957","StrChn":"CH1","StartTime":1766061354,"PlateImg":"PNG","BgImg":"not-base64!"}]},
         "subscribe_id":1,"data_pos":75}}
        """.Replace("\"PNG\"", "\"" + png + "\""));
    var parsed = new T1550Adapter().Parse(message.RootElement);
    Check(parsed.Count == 2 && parsed[0].DeviceId == "3824F10E80F8" && parsed[0].Channel == "CH1", "T1550 message gives one observation per PlateInfo, device = MAC without separators");
    Check(parsed[0].Plate == "30A12345" && parsed[0].Confidence is null, "SnapId is the plate candidate; missing confidence stays unknown (null)");
    Check(parsed[0].PlateImage is { Length: > 8 } && parsed[1].OverviewImage is null, "Base64 images are decoded; a broken image is dropped, not fatal");
    Check(parsed[0].CapturedUtc == DateTimeOffset.FromUnixTimeSeconds(1766061353).UtcDateTime, "StartTime read as Unix seconds (checked against receive time later)");
    var alarmOnly = System.Text.Json.JsonDocument.Parse("""{"data":{"dev_net_info":[{"mac":"38-24-F1-0E-80-F8"}],"alarm_list":[{"time":"2025-12-18T12:32:28Z+00:00","channel_alarm":[]}]}}""");
    Check(new T1550Adapter().Parse(alarmOnly.RootElement).Count == 0, "Alarm-only message yields no observation");
    var generic = new GenericAdapter().Parse(System.Text.Json.JsonDocument.Parse("""[{"deviceId":"zk-01","channel":"1","personId":7,"confidence":0.97}]""").RootElement);
    Check(generic.Single().PersonId == 7 && generic.Single().Confidence == .97 && generic.Single().DeviceId == "ZK01", "Generic adapter carries person results (face terminals)");
    Check(ReceiverController.IpAllowed(System.Net.IPAddress.Parse("192.168.1.6"), ["192.168.1.0/24"]), "Camera inside the allowed subnet");
    Check(!ReceiverController.IpAllowed(System.Net.IPAddress.Parse("192.168.2.6"), ["192.168.1.0/24", "10.0.0.5"]), "Camera outside the allowed list is refused");
    Check(ReceiverController.IpAllowed(System.Net.IPAddress.Parse("::ffff:10.0.0.5"), ["10.0.0.5"]), "IPv4-mapped IPv6 address is matched");

    // Real T1550 capture, if present on this machine (kept outside the repo: it contains real vehicle photos).
    var capture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tcp"));
    if (File.Exists(capture))
    {
        var raw = File.ReadAllText(capture);
        int messages = 0, observations = 0, images = 0, failures = 0;
        for (var at = raw.IndexOf("{\"data\""); at >= 0; at = raw.IndexOf("{\"data\"", at + 1))
        {
            try
            {
                using var doc = FirstJson(raw[at..]);
                var found = new T1550Adapter().Parse(doc.RootElement);
                messages++; observations += found.Count; images += found.Count(x => x.PlateImage is not null) + found.Count(x => x.OverviewImage is not null);
            }
            catch (Exception) { failures++; }
        }
        Console.WriteLine($"  capture: {messages} messages, {observations} plate observations, {images} images, {failures} unreadable");
        Check(messages == 43 && failures == 0, "All 43 captured T1550 messages parse");
        Check(observations == 7 && images == 14, "The 7 PlateInfo items and their 14 plate/overview images are decoded");
    }
    else Console.WriteLine("  (real ../tcp capture not present; skipped)");
}

// ---- CR-008: person lanes and PPE ----
{
    var t0 = new DateTime(2026, 9, 25, 6, 0, 0, DateTimeKind.Utc);
    var rules = PpeRules.Parse("helmet=required, boots=warn, gloves=ignore");
    Check(rules.Count == 3 && rules["helmet"] == "required", "PPE rules parse item=mode pairs");
    Check(PpeRules.Check("helmet=maybe") is not null && PpeRules.Check("helmet=required,vest=warn") is null, "Bad PPE rule is refused before saving");
    PpeReading R(int secondsAgo, int people, bool helmet, bool boots = true) => new(t0.AddSeconds(-secondsAgo), people, new Dictionary<string, bool> { ["helmet"] = helmet, ["boots"] = boots });
    var window = TimeSpan.FromSeconds(3);
    PpeVerdict D(params PpeReading[] r) => PpeRules.Decide(r, rules, t0, window, 2, "Review");
    Check(D(R(1, 1, true)).Outcome == "Ok", "One person, all items → OK");
    Check(D(R(2, 1, false), R(1, 1, false)).Reason == "PPE_MISSING:helmet", "Helmet missing in 2 readings → deny");
    Check(D(R(1, 1, false)).Outcome == "Review", "Helmet missing in only 1 reading → guard review, not deny (O4)");
    Check(D(R(1, 2, true)).Reason == "PPE_ROI_NOT_SINGLE", "Two people in the ROI → guard review (O2)");
    Check(D(R(1, 1, true, boots: false)).Outcome == "Warn", "Warn-only item missing → allow with alert (O5)");
    Check(D(R(10, 1, true)).Reason == "PPE_NO_DATA", "Old reading outside the window counts as no data");
    Check(PpeRules.Decide([], rules, t0, window, 2, "AllowAlert").Outcome == "Warn", "No PPE data with AllowAlert mode → allow + alert (O6)");
    var cache = new PpeCache(); cache.Record("flap-1", R(1, 1, true)); cache.Record("flap-2", R(1, 2, true));
    Check(cache.Recent("flap-1").Single().PeopleInRoi == 1 && cache.Recent("none").Count == 0, "PPE cache keeps readings per lane");
    Check(AuthorizedPerson.Allowed(new AuthorizedPerson { Active = true }, t0) && !AuthorizedPerson.Allowed(new AuthorizedPerson { Active = true, ValidUntilUtc = t0.AddDays(-1) }, t0)
        && !AuthorizedPerson.Allowed(null, t0), "Person access: active and not expired");

    // Upgrade keeps sensible defaults for settings columns added after the settings row existed.
    using var upConnection = new SqliteConnection("Data Source=:memory:");
    upConnection.Open();
    var upOptions = new DbContextOptionsBuilder<ParkingDb>().UseSqlite(upConnection).Options;
    using (var old = new ParkingDb(upOptions))
    {
        await old.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(old.Database.GetMigrations().First(m => m.EndsWith("_SiteTimings")));
        await old.Database.ExecuteSqlRawAsync("INSERT INTO SiteSettings (Id, DecisionValidSeconds, ReviewWindowSeconds, CooldownSeconds, HeartbeatFreshSeconds, OfflineAlertSeconds) VALUES (1,30,300,8,10,30)");
        await old.Database.ExecuteSqlRawAsync("INSERT INTO Lanes (Id, Direction, Policy, PlateThreshold, FaceThreshold, ShadowMode, Simulation, AutoOpen, SafetyHealthy, VehiclePresent, Obstructed, BarrierState) VALUES ('entry-1','Entry','PlateOnly',0.95,0.8,1,1,0,0,0,0,'Unknown')");
    }
    using var up = new ParkingDb(upOptions);
    await up.Database.MigrateAsync();
    var upgraded = await up.Site();
    Check(upgraded.MergeWindowSeconds == 10 && upgraded.PpeWindowSeconds == 3 && upgraded.PpeConfirmCount == 2, "Upgraded site gets the default merge/PPE settings, not 0");
    var upgradedLane = await up.Lanes.SingleAsync();
    Check(upgradedLane.Subject == "Vehicle" && upgradedLane.PpeMissingMode == "Review", "Upgraded lanes are vehicle lanes with review on missing PPE data");
}

// ---- CR-009: gate operations ----
{
    var site = new SiteSettings();   // Asia/Ho_Chi_Minh (UTC+7), reset at 00:00 local
    Check(site.NextReset(new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 9, 25, 17, 0, 0, DateTimeKind.Utc), "Walk-in pass at 17:00 local ends at 00:00 local next day");
    Check(site.NextReset(new DateTime(2026, 9, 25, 16, 59, 0, DateTimeKind.Utc)) == new DateTime(2026, 9, 25, 17, 0, 0, DateTimeKind.Utc), "Walk-in pass at 23:59 local ends one minute later");
    site.WalkInResetHour = 6;
    Check(site.NextReset(new DateTime(2026, 9, 25, 20, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 9, 25, 23, 0, 0, DateTimeKind.Utc), "Reset hour 06:00 local: a 03:00 pass ends at 06:00 the same morning");
    var gateLane = new Lane { Policy = "PlateOnly", PlateThreshold = .9 };
    var expired = new RegisteredVehicle { NormalizedPlate = "30A12345", ValidUntilUtc = DateTime.UtcNow.AddDays(-1) };
    var gateEvidence = new Recognition("30A12345", .99, null, 0, "t");
    Check(AccessPolicy.Evaluate(gateLane, gateEvidence, expired, false, false).Reason == "REGISTRATION_EXPIRED", "Expired registration goes to the guard");
    Check(AccessPolicy.Evaluate(gateLane, gateEvidence, expired, true, false).Outcome == "Allow", "A valid visitor/walk-in pass still admits an expired registration");
    expired.ValidUntilUtc = DateTime.UtcNow.AddDays(1);
    Check(AccessPolicy.Evaluate(gateLane, gateEvidence, expired, false, false).Outcome == "Allow", "Registration before its end date is valid");
}

// ---- CR-010: presence, shifts, station sync ----
{
    var t0 = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
    PresenceRecord P(string subject, string dir, int minute, string source = "Sensor") => new() { EventId = Guid.NewGuid(), AreaCode = "MINE", SubjectKey = subject, SubjectName = subject, Direction = dir, PassageUtc = t0.AddMinutes(minute), StationId = "A", Source = source };
    // Arrival order is shuffled on purpose: the OUT of W1 arrives before its IN.
    var (inside, unmatched) = PresenceBook.Compute([P("W1", "Out", 30), P("W1", "In", 10), P("W2", "In", 12), P("W3", "Out", 5), P("W4", "In", 1), P("W4", "In", 2)]);
    Check(inside.Select(x => x.SubjectKey).SequenceEqual(new[] { "W4", "W2" }), "Inside = entered and not exited, replayed in passage-time order");
    Check(unmatched.Single().SubjectKey == "W3", "An exit without an entry is reported, not hidden");
    Check(inside.Single(x => x.SubjectKey == "W4").DoubleEntry, "Two entries without an exit are flagged");
    Check(PresenceBook.Compute([P("W5", "In", 1), P("W5", "Out", 2, "Manual")]).Inside.Count == 0, "A guard's manual close ends the presence");
    var shifts = new[] { new Shift { Name = "Ca 1", Start = "06:00", End = "14:00" }, new Shift { Name = "Ca 3", Start = "22:00", End = "06:00" } };
    Check(PresenceBook.ShiftAt(shifts, new DateTime(2026, 9, 25, 7, 30, 0)) == "Ca 1" && PresenceBook.ShiftAt(shifts, new DateTime(2026, 9, 25, 2, 0, 0)) == "Ca 3"
        && PresenceBook.ShiftAt(shifts, new DateTime(2026, 9, 25, 15, 0, 0)) is null, "Shift lookup handles day and overnight shifts");

    // Cloud path of option E: station A uploads through the outbox, station B reads A's presence keys.
    var cloudRoot = Directory.CreateTempSubdirectory("presence-cloud-");
    try
    {
        using var aConn = new SqliteConnection("Data Source=:memory:"); aConn.Open();
        using var bConn = new SqliteConnection("Data Source=:memory:"); bConn.Open();
        using var a = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(aConn).AddInterceptors(new SyncOutboxInterceptor()).Options);
        using var b = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(bConn).Options);
        await a.Database.MigrateAsync(); await b.Database.MigrateAsync();
        var storage = new FolderObjectStorage(cloudRoot.FullName);
        var stationA = new CloudOptions { SiteId = "station-a" };
        a.Presence.Add(P("W1", "In", 1)); await a.SaveChangesAsync();
        await CloudUploader.RunOnceAsync(a, storage, stationA, cloudRoot.FullName, cloudRoot.FullName, DateTime.UtcNow, default);
        Check(await PresenceSyncWorker.PullCloudOnce(b, storage, "station-a", "B", default) == 1, "Station B reads station A's presence record from the cloud");
        Check(await PresenceSyncWorker.PullCloudOnce(b, storage, "station-a", "B", default) == 0, "Reading again adds nothing (cursor + EventId)");
        a.Presence.Add(P("W1", "Out", 5)); await a.SaveChangesAsync();
        await CloudUploader.RunOnceAsync(a, storage, stationA, cloudRoot.FullName, cloudRoot.FullName, DateTime.UtcNow.AddSeconds(1), default);
        await PresenceSyncWorker.PullCloudOnce(b, storage, "station-a", "B", default);
        Check(PresenceBook.Compute(await b.Presence.ToListAsync()).Inside.Count == 0, "The later exit also arrives, so B sees nobody inside");
        var echo = await b.Presence.FirstAsync();
        Check(await PresenceBook.Receive(a, [echo], "A") == 0, "A station ignores its own records coming back");
    }
    finally { cloudRoot.Delete(true); }
}

// ---- CR-011: alert lifecycle ----
{
    var alert = new ParkingAlert { Reason = "TEST" };
    var at = DateTime.UtcNow;
    AlertLifecycle.Acknowledge(alert, "G1", at);
    Check(alert.Status == "Acknowledged" && alert.AcknowledgedBy == "G1" && !alert.Resolved, "Acknowledge records who and when, alert stays open");
    Check(await Throws(() => { AlertLifecycle.Acknowledge(alert, "G2", at); return Task.CompletedTask; }), "An alert is acknowledged once");
    Check(await Throws(() => { AlertLifecycle.Resolve(alert, "G1", at, "", false, false); return Task.CompletedTask; }), "Resolving needs a note");
    Check(await Throws(() => { AlertLifecycle.Resolve(alert, "G1", at, "bird on camera", true, false); return Task.CompletedTask; }), "False alarm needs the MarkFalseAlarm permission");
    AlertLifecycle.Resolve(alert, "S1", at, "bird on camera", true, true);
    Check(alert.Status == "FalseAlarm" && alert.Resolved && alert.ResolutionNote == "bird on camera", "Safety officer closes it as a false alarm with a note");
    var direct = new ParkingAlert();
    AlertLifecycle.Resolve(direct, "G1", at, "fixed", false, false);
    Check(direct.AcknowledgedBy == "G1" && direct.Status == "Resolved", "Resolving an open alert also records the acknowledgement");

    using var alertConnection = new SqliteConnection("Data Source=:memory:"); alertConnection.Open();
    var alertOptions = new DbContextOptionsBuilder<ParkingDb>().UseSqlite(alertConnection).Options;
    using (var old = new ParkingDb(alertOptions))
    {
        await old.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(old.Database.GetMigrations().First(m => m.EndsWith("_PresenceAndSync")));
        await old.Database.ExecuteSqlRawAsync("INSERT INTO Alerts (Id, Reason, Severity, Resolved, ResolvedBy, CreatedUtc, DeliveryAttempts) VALUES ('11111111-1111-1111-1111-111111111111','OLD','Medium',1,'ADMIN','2026-09-01 00:00:00',0), ('22222222-2222-2222-2222-222222222222','OPEN','Medium',0,NULL,'2026-09-01 00:00:00',0)");
    }
    using var upgradedAlerts = new ParkingDb(alertOptions);
    await upgradedAlerts.Database.MigrateAsync();
    var statuses = await upgradedAlerts.Alerts.OrderBy(x => x.Reason).Select(x => x.Status).ToListAsync();
    Check(statuses.SequenceEqual(new[] { "Resolved", "Open" }), "Upgrade: previously resolved alerts become Resolved, open ones Open");
}

// ---- CR-012: reporting ----
{
    var values = new List<double> { 1, 2, 3, 4, 100 };
    var (mean, median, p95, n) = Reports.Summary(values);
    Check(n == 5 && mean == 22 && median == 3 && Math.Abs(p95!.Value - 80.8) < 1e-9, "Mean / median / P95 (linear interpolation)");
    Check(Reports.Summary([]).Mean is null, "No data gives empty measures, not zero");
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    var late = new AccessEvent { CreatedUtc = new DateTime(2026, 9, 25, 17, 30, 0, DateTimeKind.Utc), Shift = "Ca 3", LaneId = "gate" };
    Check(Reports.Bucket(late, "day", zone) == "2026-09-26" && Reports.Bucket(late, "hour", zone) == "00:00", "Buckets use the site's local time (17:30 UTC = next day 00:30)");
    Check(Reports.Bucket(late, "shift", zone) == "Ca 3" && Reports.Bucket(new AccessEvent(), "shift", zone) == "(no shift)" && Reports.Bucket(late, "month", zone) == "2026-09", "Shift and month buckets");

    // Upgrade of built-in roles: new permissions arrive once, an admin's removals stay.
    using var seedConnection = new SqliteConnection("Data Source=:memory:"); seedConnection.Open();
    using var seedDb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(seedConnection).Options);
    await seedDb.Database.MigrateAsync();
    seedDb.Roles.Add(new Role { Id = "Manager", Name = "Manager", BuiltIn = true, SeedVersion = 1 });
    seedDb.RolePermissions.AddRange(new[] { Perm.ViewLive, Perm.ManageVehicles }.Select(p => new RolePermission { RoleId = "Manager", Permission = p }));   // admin had removed ViewAudit
    await seedDb.SaveChangesAsync();
    await RoleCatalog.Seed(seedDb); await seedDb.SaveChangesAsync();
    var manager = await RoleCatalog.Permissions(seedDb, "Manager");
    Check(manager.Contains(Perm.ViewReports) && manager.Contains(Perm.ManagePeople), "Upgraded built-in role receives newly introduced permissions");
    Check(!manager.Contains(Perm.ViewAudit), "A permission the admin removed is not added back");
    seedDb.RolePermissions.RemoveRange(seedDb.RolePermissions.Where(x => x.RoleId == "Manager" && x.Permission == Perm.ViewReports)); await seedDb.SaveChangesAsync();
    await RoleCatalog.Seed(seedDb); await seedDb.SaveChangesAsync();
    Check(!(await RoleCatalog.Permissions(seedDb, "Manager")).Contains(Perm.ViewReports), "Seeding again does not undo a later removal");
}

// ---- CR-015: device profiles, identity mapping, SDK entry point ----
{
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    using var message = System.Text.Json.JsonDocument.Parse("""
        {"data":{"dev_net_info":[{"device_name":"T1550FAI-Z","mac":"38-24-F1-0E-80-F8","ip":"192.168.1.6"}],
         "ai_snap_picture":{"PlateInfo":[{"Id":"","GrpId":1,"SnapId":"30A12345","StrChn":"CH1","StartTime":1766061353,"PlateImg":"PNG","BgImg":"PNG"},
                                         {"Id":"","GrpId":2,"SnapId":"35A39957","StrChn":"CH1","StartTime":1766061354,"PlateImg":"PNG","BgImg":"not-base64!"}]}}}
        """.Replace("\"PNG\"", "\"" + png + "\""));
    var t1550Profile = ProfileAdapter.Compile("t1550-profile", "json", ProfileAdapter.T1550Example, "test");
    var builtInResult = new T1550Adapter().Parse(message.RootElement);
    var profileResult = t1550Profile.Parse(message.RootElement);
    Check(profileResult.Count == 2 && profileResult.Zip(builtInResult).All(p => p.First.DeviceId == p.Second.DeviceId && p.First.Channel == p.Second.Channel
        && p.First.Plate == p.Second.Plate && p.First.CapturedUtc == p.Second.CapturedUtc && p.First.Confidence == p.Second.Confidence
        && (p.First.PlateImage?.Length ?? 0) == (p.Second.PlateImage?.Length ?? 0) && (p.First.OverviewImage?.Length ?? 0) == (p.Second.OverviewImage?.Length ?? 0)),
        "T1550 written as a profile gives the same observations as the built-in adapter");
    Check(profileResult[0].RawKey != profileResult[1].RawKey, "Profile raw keys are unique within a message (index appended)");

    // Invalid mappings are refused with a readable reason before they are saved.
    string? Refusal(string definition, string format = "json")
    {
        try { ProfileAdapter.Compile("x", format, definition, "test"); return null; }
        catch (ProfileException ex) { return string.Join(" | ", ex.Errors); }
    }
    Check(Refusal("""{"fields":{"plate":"a"}}""")?.Contains("deviceId") == true, "A profile without deviceId is refused");
    Check(Refusal("""{"fields":{"deviceId":"d","plat":"a"}}""")?.Contains("Unknown field 'plat'") == true, "A misspelt field name is refused");
    Check(Refusal("""{"fields":{"deviceId":"d","plate":"items[*].no"}}""")?.Contains("[*]") == true, "[*] outside records is refused");
    Check(Refusal("""{"fields":{"deviceId":"d","plate":{"path":"a","transform":"shout"}}}""")?.Contains("shout") == true, "Unknown transform is refused");
    Check(Refusal("""{"fields":{"deviceId":"d","plate":{"path":"a","scale":100}}}""") is not null, "scale on a non-confidence field is refused");
    Check(Refusal("""{"fields":{"deviceId":"d","plate":"a"}""") is not null && Refusal("""{"fields":{"deviceId":"d","plate":"a"}}""", "csv") is not null, "Broken JSON and unknown formats are refused");
    Check(Refusal("""{"records":"a[*]","fields":{"deviceId":"$.dev","personId":"id"}, "personId":"direct"}""") is null, "Valid face profile compiles");

    // Face device, JSON: 0–100 score, local time string, own user IDs.
    var face = ProfileAdapter.Compile("zk-face", "json", """
        { "match": [{ "path": "event", "equals": "verify" }],
          "records": "results[*]",
          "fields": {
            "deviceId": "$.device.sn",
            "channel": { "path": "door", "default": "1" },
            "personId": { "path": ["userCode", "pin"], "transform": ["trim", "upper"] },
            "confidence": { "path": "score", "scale": 100 },
            "capturedUtc": { "path": "time", "time": "yyyy-MM-dd HH:mm:ss", "timeZone": "+07:00" },
            "overviewImage": "photo" } }
        """, "test");
    using var faceMessage = System.Text.Json.JsonDocument.Parse("""
        {"event":"verify","device":{"sn":"zk-01"},"results":[
          {"userCode":" nv042 ","score":97,"time":"2026-09-29 08:00:00","photo":"PNG"},
          {"pin":"77","score":150,"door":"2","time":"bad"}]}
        """.Replace("\"PNG\"", "\"" + png + "\""));
    var warnings = new List<string>();
    var faces = face.Parse(faceMessage.RootElement, warnings);
    Check(faces.Count == 2 && faces[0].DeviceId == "ZK01" && faces[0].Channel == "1" && faces[1].Channel == "2", "Root path ($.), default and per-record channel");
    Check(faces[0].ExternalPersonId == "NV042" && faces[0].PersonId is null && faces[1].ExternalPersonId == "77", "Person ID: fallback paths, transforms, looked up later (identity mode)");
    Check(faces[0].Confidence == .97, "0–100 score scaled to 0–1");
    Check(faces[0].CapturedUtc == new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc), "Local time string with the device time zone → UTC");
    Check(faces[1].Confidence is double bad && double.IsNaN(bad) && warnings.Any(w => w.Contains("outside 0–1")), "Out-of-range score becomes invalid (guard review) with a warning");
    Check(faces[1].CapturedUtc is null && warnings.Any(w => w.Contains("does not match")), "Unreadable time stays unknown with a warning");
    Check(faces[0].OverviewImage is { Length: > 8 }, "Image path without options decodes base64");
    using var heartbeat = System.Text.Json.JsonDocument.Parse("""{"event":"heartbeat","device":{"sn":"zk-01"}}""");
    Check(face.Parse(heartbeat.RootElement).Count == 0 && face.DeviceOf(heartbeat.RootElement) == "ZK01", "Heartbeat skipped by match but still identifies the device (liveness)");
    var direct = ProfileAdapter.Compile("d", "json", """{"personId":"direct","fields":{"deviceId":"sn","personId":"pin","plate":{"path":"plate","map":{"NONE":""}}}}""", "test");
    using var directMessage = System.Text.Json.JsonDocument.Parse("""[{"sn":"a","pin":"12","plate":"NONE"},{"sn":"a","pin":"E-12"}]""");
    var directResult = direct.Parse(directMessage.RootElement);
    Check(directResult[0].PersonId == 12 && directResult[0].ExternalPersonId is null && directResult[0].Plate == "", "Direct mode uses the number as PersonId; map turns a placeholder into empty");
    Check(directResult[1].PersonId is null && directResult[1].ExternalPersonId == "E-12", "Direct mode falls back to identity lookup for a non-number");

    // XML (e.g. ISAPI-style ANPR): namespaces ignored, attributes as @name, one or many elements both work.
    var xml = ProfileAdapter.Compile("hik-anpr", "xml", """
        { "match": [{ "path": "EventNotificationAlert.eventType", "equals": "ANPR" }],
          "records": "EventNotificationAlert.ANPR[*]",
          "fields": { "deviceId": "$.EventNotificationAlert.macAddress", "channel": "$.EventNotificationAlert.channelID",
                      "plate": "licensePlate", "confidence": { "path": "confidenceLevel", "scale": 100 },
                      "capturedUtc": { "path": "$.EventNotificationAlert.dateTime", "time": "iso" }, "rawKey": "@id" } }
        """, "test");
    const string anpr = """
        <?xml version="1.0" encoding="UTF-8"?>
        <EventNotificationAlert version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
          <macAddress>a4:14:37:00:00:01</macAddress><channelID>1</channelID><dateTime>2026-09-29T08:00:00+07:00</dateTime>
          <eventType>ANPR</eventType><ANPR id="k1"><licensePlate>30A12345</licensePlate><confidenceLevel>92</confidenceLevel></ANPR>
        </EventNotificationAlert>
        """;
    using (var anprDoc = PayloadFormat.XmlToJson(anpr))
    {
        var read = xml.Parse(anprDoc.RootElement);
        Check(read.Count == 1 && read[0].DeviceId == "A41437000001" && read[0].Plate == "30A12345" && read[0].Confidence == .92 && read[0].Channel == "1", "XML message mapped through a profile (single element, namespace ignored)");
        Check(read[0].CapturedUtc == new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc) && read[0].RawKey.StartsWith("k1/"), "ISO time with offset and attributes (@id) are read");
    }
    using (var two = PayloadFormat.XmlToJson(anpr.Replace("</EventNotificationAlert>", "<ANPR id=\"k2\"><licensePlate>30A67890</licensePlate></ANPR></EventNotificationAlert>")))
        Check(xml.Parse(two.RootElement).Select(o => o.Plate).SequenceEqual(["30A12345", "30A67890"]), "Repeated XML elements become several records");
    Check(!PayloadFormat.Valid("xml", """<?xml version="1.0"?><!DOCTYPE a [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><a>&x;</a>"""), "XML with a DTD / external entity is refused (XXE)");
    Check(!PayloadFormat.Valid("xml", string.Concat(Enumerable.Repeat("<a>", 40)) + string.Concat(Enumerable.Repeat("</a>", 40))), "Too deeply nested XML is refused");
    Check(!PayloadFormat.Valid("json", "<a/>") && PayloadFormat.Valid("xml", "<a/>"), "Body format follows the adapter");

    // Several receiver ports, each with its own adapter; the legacy single port still works.
    var portConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Receiver:Port"] = "2123", ["Receiver:Ports"] = "2124=hik-anpr; 2125 = zk-face; bad; 99999=x" }).Build();
    var ports = ReceiverOptions.From(portConfig).Ports;
    Check(ports.Count == 3 && ports[2123] == "t1550" && ports[2124] == "hik-anpr" && ports[2125] == "zk-face", "Receiver:Ports maps ports to adapters; invalid entries ignored");

    // Store-before-ACK entry point shared by the HTTP receiver and SDK connectors.
    using var inboxConnection = new SqliteConnection("Data Source=:memory:");
    inboxConnection.Open();
    using var idb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(inboxConnection).Options);
    await idb.Database.MigrateAsync();
    idb.DeviceProfiles.Add(new DeviceProfile { Id = "zk-face", Name = "ZK", Definition = """{"fields":{"deviceId":"sn","personId":"pin"}}""" });
    idb.DeviceProfiles.Add(new DeviceProfile { Id = "hik-anpr", Name = "Hik", Format = "xml", Definition = """{"fields":{"deviceId":"$.a.mac","plate":"$.a.plate"}}""" });
    idb.DeviceProfiles.Add(new DeviceProfile { Id = "old", Name = "Old", Enabled = false, Definition = """{"fields":{"deviceId":"sn","plate":"p"}}""" });
    await idb.SaveChangesAsync();
    var builtIns = new CameraAdapters([new T1550Adapter(), new GenericAdapter(), new PpeAdapter()]);
    var resolver = new DeviceAdapterResolver(builtIns, idb, new ProfileCache());
    var writer = new InboxWriter(idb, resolver, new InboxSignal());
    Check(await writer.Store("zk-face", """{"sn":"zk-01","pin":"7","t":1}""", "sdk:zk") == InboxOutcome.Stored, "SDK connector stores a message through a profile");
    Check(await writer.Store("zk-face", """{"sn":"zk-01","pin":"7","t":1}""", "sdk:zk") == InboxOutcome.Duplicate, "The same message again is acknowledged, stored once");
    Check(await writer.Store("hik-anpr", "<a><mac>1</mac><plate>30A12345</plate></a>", "10.0.0.5") == InboxOutcome.Stored, "XML profile accepts an XML body");
    Check(await writer.Store("hik-anpr", """{"a":1}""", "10.0.0.5") == InboxOutcome.BadBody, "XML profile refuses a JSON body");
    Check(await writer.Store("old", """{"sn":"a","p":"x"}""", "x") == InboxOutcome.UnknownAdapter && await writer.Store("nope", "{}", "x") == InboxOutcome.UnknownAdapter, "Disabled or unknown profile is refused");
    Check(await writer.Store("t1550", """{"data":{}}""", "x") == InboxOutcome.Stored, "Built-in adapters still resolve first");
    var zkRow = await idb.CameraInbox.FirstAsync(x => x.Adapter == "zk-face");
    Check(zkRow.Status == "Pending" && zkRow.RemoteIp == "sdk:zk" && await idb.CameraInbox.CountAsync() == 3, "Stored rows wait for the worker, with their source");
    var cache = new ProfileCache();
    var v1 = cache.Get(new DeviceProfile { Id = "c", Version = 1, Definition = """{"fields":{"deviceId":"a","plate":"b"}}""" });
    Check(ReferenceEquals(v1, cache.Get(new DeviceProfile { Id = "c", Version = 1, Definition = "ignored: same version" }))
        && cache.Get(new DeviceProfile { Id = "c", Version = 2, Definition = """{"fields":{"deviceId":"a","plate":"c"}}""" }).Version == "c/v2", "Compiled profiles are reused until the version changes");

    // Real T1550 capture through the profile, if present on this machine.
    var capture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tcp"));
    if (File.Exists(capture))
    {
        var raw = File.ReadAllText(capture);
        int messages = 0, observations = 0, images = 0;
        for (var at = raw.IndexOf("{\"data\""); at >= 0; at = raw.IndexOf("{\"data\"", at + 1))
        {
            using var doc = FirstJson(raw[at..]);
            var found = t1550Profile.Parse(doc.RootElement);
            messages++; observations += found.Count; images += found.Count(x => x.PlateImage is not null) + found.Count(x => x.OverviewImage is not null);
        }
        Check(messages == 43 && observations == 7 && images == 14, "The real capture replays through the T1550 profile: 43 messages, 7 plates, 14 images");
    }
    else Console.WriteLine("  (real ../tcp capture not present; profile replay skipped)");
}

// ---- Station → FPT Cloud platform (dsa.station/1 draft) ----
{
    using var pconn = new SqliteConnection("Data Source=:memory:");
    pconn.Open();
    using var pdb = new ParkingDb(new DbContextOptionsBuilder<ParkingDb>().UseSqlite(pconn).AddInterceptors(new SyncOutboxInterceptor()).Options);
    pdb.Database.EnsureCreated();
    var pevidence = Directory.CreateTempSubdirectory("platform-evidence-");
    try
    {
        Check(await Throws(() => { new PlatformOptions { Enabled = true, Url = "http://x", StationId = "S", Key = "short" }.Validate(); return Task.CompletedTask; }), "Platform settings need a URL, station ID and a key of ≥ 32 characters");
        var parea = new Area { Code = "CUALO", Name = "Cửa lò" };
        var plocation = new Location { AreaId = parea.Id, Name = "Giếng chính" };
        pdb.Areas.Add(parea); pdb.Locations.Add(plocation);
        pdb.Lanes.Add(new Lane { Id = "CL-L3", LocationId = plocation.Id, Direction = "Entry", Simulation = false });
        pdb.Cameras.Add(new Camera { Id = "face-3", ExternalDevice = "FACES3", LastMessageUtc = DateTime.UtcNow });
        await pdb.SaveChangesAsync();
        var photo = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(pevidence.FullName, "a1.jpg"), photo);
        var decided = new AccessEvent { LaneId = "CL-L3", CorrelationId = "p1", Plate = "14B123456", PersonId = 1023, Outcome = "Allow", Reason = "POLICY_MATCH", EvidenceId = "a1.jpg", Shift = "Ca 1" };
        pdb.Events.Add(decided); await pdb.SaveChangesAsync();
        var opened = new DeviceCommand { EventId = decided.Id, LaneId = "CL-L3" };
        pdb.Commands.Add(opened); await pdb.SaveChangesAsync();
        opened.Status = "Acknowledged"; opened.PassageUtc = DateTime.UtcNow; await pdb.SaveChangesAsync();
        pdb.Sessions.Add(new ParkingSession { Plate = "14B123456", EntryCommandId = opened.Id, EntryUtc = DateTime.UtcNow }); await pdb.SaveChangesAsync();

        var platformOptions = new PlatformOptions { Enabled = true, Url = "http://platform.test", StationId = "CUALO-GC", Key = new string('k', 40) };
        var fake = new FakePlatform();
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://platform.test/") };
        await PlatformUploader.RunOnceAsync(pdb, http, platformOptions, pevidence.FullName, DateTime.UtcNow);
        var rows = pdb.Outbox.AsNoTracking().OrderBy(x => x.Id).ToList();
        Check(rows.Select(x => x.Status).SequenceEqual(["Sent", "Skipped", "Sent", "Skipped"]), "Decision and passage sent; a command without passage and a session are skipped (no contract type)");
        Check(fake.Evidence.Count == 1 && fake.Evidence[0].Path == "/api/v1/stations/CUALO-GC/evidence/a1.jpg" && fake.Evidence[0].Sha == Convert.ToHexString(SHA256.HashData(photo)),
            "The decision's photo is uploaded first, with its SHA-256");
        var sent = fake.Batches.Single();
        Check(fake.EventPaths.Single() == "/api/v1/stations/CUALO-GC/events" && sent.GetProperty("schema").GetString() == "dsa.station/1" && sent.GetProperty("stationId").GetString() == "CUALO-GC",
            "One batch to the station's events URL, with schema and station");
        var access = sent.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "access_event");
        var passage = sent.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "passage");
        Check(access.GetProperty("id").GetString() == decided.Id + "." + rows[0].Id && access.GetProperty("seq").GetInt64() == rows[0].Id, "Item ID = record ID + outbox ID; seq = outbox ID");
        var ap = access.GetProperty("payload");
        Check(ap.GetProperty("areaCode").GetString() == "CUALO" && ap.GetProperty("direction").GetString() == "In" && ap.GetProperty("personId").GetInt32() == 1023
            && ap.GetProperty("evidenceId").GetString() == "a1.jpg" && ap.GetProperty("shift").GetString() == "Ca 1", "Decision payload gains area code, direction and evidence ID");
        Check(passage.GetProperty("payload").GetProperty("plate").GetString() == "14B123456" && passage.GetProperty("payload").GetProperty("personId").GetInt32() == 1023
            && passage.GetProperty("payload").GetProperty("areaCode").GetString() == "CUALO", "Passage carries the decision's person, plate and area (trace on the platform)");

        pdb.ChangeTracker.Clear();
        var reviewed = pdb.Events.Single(); reviewed.Outcome = "Deny"; reviewed.ReviewBy = "GUARD1"; await pdb.SaveChangesAsync();
        await PlatformUploader.RunOnceAsync(pdb, http, platformOptions, pevidence.FullName, DateTime.UtcNow);
        var again = fake.Batches[1].GetProperty("items")[0];
        Check(again.GetProperty("payload").GetProperty("outcome").GetString() == "Deny" && again.GetProperty("id").GetString() != access.GetProperty("id").GetString(), "A reviewed decision is sent again as a new item");
        Check(again.GetProperty("occurredUtc").GetString()!.EndsWith('Z') && again.GetProperty("payload").GetProperty("createdUtc").GetString()!.EndsWith('Z'), "Times read back from SQLite are still sent as UTC");
        Check(fake.Evidence.Count == 1, "The photo is not uploaded again for a later version");

        fake.Reject = true;
        var refused = new ParkingAlert { Reason = "TEST_REJECT" };
        pdb.Alerts.Add(refused); await pdb.SaveChangesAsync();
        await PlatformUploader.RunOnceAsync(pdb, http, platformOptions, pevidence.FullName, DateTime.UtcNow);
        var refusedRow = pdb.Outbox.AsNoTracking().Single(x => x.EntityId == refused.Id.ToString());
        Check(refusedRow.Status == "Rejected" && refusedRow.LastError!.Contains("unknown type") && pdb.Alerts.Any(x => x.Reason == "PLATFORM_REJECTED"), "A rejected item is kept with the reason and raises an alert");

        fake.Reject = false; fake.Status = System.Net.HttpStatusCode.InternalServerError;
        var outage = new ParkingAlert { Reason = "TEST_OUTAGE" };
        pdb.Alerts.Add(outage); await pdb.SaveChangesAsync();
        var now = DateTime.UtcNow;
        await PlatformUploader.RunOnceAsync(pdb, http, platformOptions, pevidence.FullName, now);
        var outageRow = pdb.Outbox.AsNoTracking().Single(x => x.EntityId == outage.Id.ToString());
        Check(outageRow.Status == "Failed" && outageRow.Attempts == 1 && outageRow.NextAttemptUtc > now, "Platform down: the batch is kept and retried later (backoff)");
        fake.Status = System.Net.HttpStatusCode.OK;
        await PlatformUploader.RunOnceAsync(pdb, http, platformOptions, pevidence.FullName, now.AddHours(1));
        Check(pdb.Outbox.AsNoTracking().Single(x => x.Id == outageRow.Id).Status == "Sent" && pdb.Outbox.AsNoTracking().Single(x => x.Id == refusedRow.Id).Status == "Rejected",
            "After the outage it is sent; a rejected item is not retried");

        var report = System.Text.Json.JsonSerializer.SerializeToElement(await PlatformHeartbeat.Report(pdb, AppContext.BaseDirectory, DateTime.UtcNow), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var devices = report.GetProperty("devices").EnumerateArray().ToList();
        Check(report.GetProperty("softwareVersion").GetString() is { Length: <= 40 } software && software.StartsWith("station-") && report.GetProperty("diskFreeGb").GetDouble() > 0
            && devices.Any(d => d.GetProperty("id").GetString() == "face-3" && d.GetProperty("status").GetString() == "Online")
            && devices.Any(d => d.GetProperty("id").GetString() == "CL-L3" && d.GetProperty("kind").GetString() == "Barrier" && d.GetProperty("status").GetString() == "Offline"),
            "Heartbeat: version, disk, AI camera online, live lane without bridge heartbeat offline");

        // Optional: against a running DSA_Platform (PLATFORM_E2E_URL, PLATFORM_E2E_STATION, PLATFORM_E2E_KEY).
        if (Environment.GetEnvironmentVariable("PLATFORM_E2E_URL") is { Length: > 0 } e2eUrl)
        {
            var e2e = new PlatformOptions { Enabled = true, Url = e2eUrl, StationId = Environment.GetEnvironmentVariable("PLATFORM_E2E_STATION"), Key = Environment.GetEnvironmentVariable("PLATFORM_E2E_KEY") };
            e2e.Validate();
            var real = new HttpClient { BaseAddress = new Uri(e2eUrl.TrimEnd('/') + "/") };
            real.DefaultRequestHeaders.Add("X-Station-Key", e2e.Key);
            pdb.Outbox.Where(x => x.Status == "Sent").ExecuteUpdate(x => x.SetProperty(r => r.Status, "Pending").SetProperty(r => r.NextAttemptUtc, (DateTime?)null));
            pdb.ChangeTracker.Clear();   // ExecuteUpdate bypasses tracked rows; the uploader runs on a fresh context in production
            await PlatformUploader.RunOnceAsync(pdb, real, e2e, pevidence.FullName, DateTime.UtcNow.AddHours(2));
            var notSent = pdb.Outbox.AsNoTracking().Where(x => x.Status == "Pending" || x.Status == "Failed").Select(x => x.Id + " " + x.EntityType + " " + x.LastError).ToList();
            Check(notSent.Count == 0, "E2E: the real platform accepts every item" + (notSent.Count > 0 ? " — " + notSent[0] : ""));
            var health = await real.PostAsJsonAsync($"api/v1/stations/{e2e.StationId}/health", await PlatformHeartbeat.Report(pdb, AppContext.BaseDirectory, DateTime.UtcNow));
            Check(health.IsSuccessStatusCode, "E2E: the real platform accepts the heartbeat");
        }
        else Console.WriteLine("  (PLATFORM_E2E_URL not set; run against a real platform skipped)");
    }
    finally { pevidence.Delete(true); }
}

Console.WriteLine($"{count} checks passed.");

// Reads the first JSON value from text that continues with other data (an HTTP capture). Sync helper: Utf8JsonReader can't live in async code.
static System.Text.Json.JsonDocument FirstJson(string text)
{
    var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text));
    return System.Text.Json.JsonDocument.ParseValue(ref reader);
}

static async Task<bool> Throws(Func<Task> action)
{
    try { await action(); return false; }
    catch (InvalidOperationException) { return true; }
    catch (DbUpdateException) { return true; }
}

// Records what the station sends and answers like the platform would (accepted, rejected or an outage).
sealed class FakePlatform : HttpMessageHandler
{
    public List<System.Text.Json.JsonElement> Batches { get; } = [];
    public List<string> EventPaths { get; } = [];
    public List<(string Path, string? Sha)> Evidence { get; } = [];
    public bool Reject { get; set; }
    public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        if (Status != System.Net.HttpStatusCode.OK) return new HttpResponseMessage(Status) { Content = new StringContent("platform down") };
        if (request.Method == HttpMethod.Put)
        {
            Evidence.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-Sha256", out var sha) ? sha.Single() : null));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
        var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)).RootElement.Clone();
        Batches.Add(body); EventPaths.Add(request.RequestUri!.AbsolutePath);
        var ids = body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
        object answer = Reject ? new { accepted = Array.Empty<string>(), rejected = ids.Select(id => new { id, reason = "unknown type" }) } : new { accepted = ids, rejected = Array.Empty<object>() };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(answer) };
    }
}

sealed class FlakyStorage(IObjectStorage inner, int failures) : IObjectStorage
{
    private int remaining = failures;
    public Task PutAsync(string key, Stream content, string contentType, IDictionary<string, string> metadata, CancellationToken cancellation) =>
        Interlocked.Decrement(ref remaining) >= 0 ? throw new HttpRequestException("simulated network outage") : inner.PutAsync(key, content, contentType, metadata, cancellation);
    public Task<long?> SizeAsync(string key, CancellationToken cancellation) => inner.SizeAsync(key, cancellation);
    public string? ReadUrl(string key, TimeSpan validFor) => null;
    public Task<IReadOnlyList<string>> ListAsync(string prefix, string? startAfter, int max, CancellationToken cancellation) => inner.ListAsync(prefix, startAfter, max, cancellation);
    public Task<Stream?> GetAsync(string key, CancellationToken cancellation) => inner.GetAsync(key, cancellation);
}
