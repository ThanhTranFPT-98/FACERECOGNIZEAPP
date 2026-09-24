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
Console.WriteLine($"{count} checks passed.");
