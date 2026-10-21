using System.Text.Json;

namespace DSAVehicle.ParkingCore;

// Vendor-neutral observation every adapter produces (GC-05).
// ExternalPersonId: the face device's own ID, resolved to PersonId through PersonIdentity by the worker (CR-015).
public sealed record ExternalObservation(string DeviceId, string Channel, string Plate, int? PersonId, double? Confidence,
    DateTime? CapturedUtc, byte[]? PlateImage, byte[]? OverviewImage, string RawKey, string? ExternalPersonId = null);

public interface ICameraAdapter
{
    string Name { get; }
    string Version { get; }
    // Body format the receiver accepts for this adapter: json, or xml (device profiles).
    string Format => "json";
    // Returns the observations in the message; an empty list means a message with nothing to decide (e.g. alarm-only).
    IReadOnlyList<ExternalObservation> Parse(JsonElement root);
}
