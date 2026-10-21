using System.Text.Json;

namespace DSAVehicle.ParkingCore;

// T1550FAI-Z plate camera, format from the ../tcp capture. SnapId is the plate candidate; the camera sends no confidence.
public sealed class T1550Adapter : ICameraAdapter
{
    public string Name => "t1550";
    public string Version => "t1550/2026-09-25";
    public IReadOnlyList<ExternalObservation> Parse(JsonElement root)
    {
        var data = root.GetProperty("data");
        var device = data.TryGetProperty("dev_net_info", out var net) && net.ValueKind == JsonValueKind.Array && net.GetArrayLength() > 0
            ? Normalize(Str(net[0], "mac")) : "";
        var list = new List<ExternalObservation>();
        if (!data.TryGetProperty("ai_snap_picture", out var ai) || !ai.TryGetProperty("PlateInfo", out var plates) || plates.ValueKind != JsonValueKind.Array) return list;
        var index = 0;
        foreach (var plate in plates.EnumerateArray())
        {
            // StartTime looks like Unix seconds in the capture; only trusted when close to "now", otherwise unknown.
            DateTime? captured = plate.TryGetProperty("StartTime", out var start) && start.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
            list.Add(new(device, Str(plate, "StrChn"), Str(plate, "SnapId"), null, null, captured,
                Image(Str(plate, "PlateImg")), Image(Str(plate, "BgImg")), $"{device}/{Str(plate, "GrpId")}/{index++}"));
        }
        return list;
    }
    internal static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    internal static string Normalize(string mac) => new(mac.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    internal static byte[]? Image(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 8_000_000) return null;
        var comma = base64.IndexOf(',');
        if (base64.StartsWith("data:") && comma > 0) base64 = base64[(comma + 1)..];
        try { return Convert.FromBase64String(base64); } catch (FormatException) { return null; }
    }
}
