using System.Text.Json;

namespace DSAVehicle.ParkingCore;

// Common JSON for any other device (and for integrators): {"deviceId","channel","plate"?,"personId"?,"confidence"?,"capturedUtc"?,"plateImage"?,"overviewImage"?,"id"?}
public sealed class GenericAdapter : ICameraAdapter
{
    public string Name => "generic";
    public string Version => "generic/1";
    public IReadOnlyList<ExternalObservation> Parse(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        return items.Select((x, i) => new ExternalObservation(T1550Adapter.Normalize(T1550Adapter.Str(x, "deviceId")), T1550Adapter.Str(x, "channel"), T1550Adapter.Str(x, "plate"),
            x.TryGetProperty("personId", out var p) && p.TryGetInt32(out var person) ? person : null,
            x.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var conf) ? conf : null,
            x.TryGetProperty("capturedUtc", out var t) && t.TryGetDateTime(out var at) ? at.ToUniversalTime() : null,
            T1550Adapter.Image(T1550Adapter.Str(x, "plateImage")), T1550Adapter.Image(T1550Adapter.Str(x, "overviewImage")),
            T1550Adapter.Str(x, "id") is { Length: > 0 } id ? id : i.ToString())).ToList();
    }
}
