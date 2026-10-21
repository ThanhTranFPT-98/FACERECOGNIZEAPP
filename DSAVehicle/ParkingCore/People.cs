using System.Collections.Concurrent;
using System.Text.Json;

namespace DSAVehicle.ParkingCore;

// ---- PPE (Plan V2 §3.9) ----
// One reading from a PPE camera for a lane's ROI: how many people, and which items were seen (true) or missing (false).
public sealed record PpeReading(DateTime AtUtc, int PeopleInRoi, IReadOnlyDictionary<string, bool> Items);
public sealed record PpeVerdict(string Outcome, string Reason, string Summary);   // Outcome: Ok | Warn | Deny | Review

public static class PpeRules
{
    // "helmet=required,vest=warn,boots=ignore" → item → mode.
    public static Dictionary<string, string> Parse(string? rules) => (rules ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries)).Where(x => x.Length == 2 && x[1] is "required" or "warn" or "ignore")
        .ToDictionary(x => x[0].ToLowerInvariant(), x => x[1]);
    public static string? Check(string? rules)
    {
        foreach (var part in (rules ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Split('=', 2, StringSplitOptions.TrimEntries) is not [{ Length: > 0 }, "required" or "warn" or "ignore"]) return "PPE rule '" + part + "' must look like helmet=required|warn|ignore.";
        return null;
    }

    // Pure decision used when a face result arrives. Readings = the lane's recent PPE messages, newest last.
    public static PpeVerdict Decide(IReadOnlyList<PpeReading> readings, Dictionary<string, string> rules, DateTime now, TimeSpan window, int confirm, string missingMode)
    {
        var recent = readings.Where(r => (now - r.AtUtc).Duration() <= window).ToList();
        if (recent.Count == 0)
            return missingMode == "AllowAlert" ? new("Warn", "PPE_NO_DATA", "no PPE reading") : new("Review", "PPE_NO_DATA", "no PPE reading");
        var latest = recent[^1];
        // O2: the PPE result can only belong to the face just read when exactly one person is in the ROI.
        if (latest.PeopleInRoi != 1) return new("Review", "PPE_ROI_NOT_SINGLE", latest.PeopleInRoi + " people in ROI");
        var missingRequired = new List<string>(); var unconfirmed = new List<string>(); var warnings = new List<string>();
        foreach (var (item, mode) in rules.Where(x => x.Value != "ignore"))
        {
            var seen = recent.Where(r => r.Items.ContainsKey(item)).ToList();
            var missing = seen.Count(r => !r.Items[item]);
            if (seen.Count == 0 || !latest.Items.TryGetValue(item, out var now1)) { (mode == "required" ? unconfirmed : warnings).Add(item + "?"); continue; }
            if (now1) continue;
            // O4: a missing item must be seen missing in several readings before it blocks.
            if (mode == "required") (missing >= confirm ? missingRequired : unconfirmed).Add(item);
            else warnings.Add(item);
        }
        var summary = string.Join(", ", latest.Items.Select(x => x.Key + (x.Value ? " ✓" : " ✗")));
        if (missingRequired.Count > 0) return new("Deny", "PPE_MISSING:" + string.Join("+", missingRequired), summary);
        if (unconfirmed.Count > 0) return new("Review", "PPE_UNCONFIRMED:" + string.Join("+", unconfirmed), summary);
        if (warnings.Count > 0) return new("Warn", "PPE_WARNING:" + string.Join("+", warnings), summary);
        return new("Ok", "PPE_OK", summary);
    }
}

// O1: latest PPE readings per lane kept in memory, so a face decision never waits for the PPE camera.
public sealed class PpeCache
{
    private readonly ConcurrentDictionary<string, List<PpeReading>> lanes = new();
    public void Record(string laneId, PpeReading reading)
    {
        var list = lanes.GetOrAdd(laneId, _ => []);
        lock (list) { list.Add(reading); list.RemoveAll(r => reading.AtUtc - r.AtUtc > TimeSpan.FromMinutes(1)); if (list.Count > 50) list.RemoveRange(0, list.Count - 50); }
    }
    public IReadOnlyList<PpeReading> Recent(string laneId)
    {
        if (!lanes.TryGetValue(laneId, out var list)) return [];
        lock (list) return list.OrderBy(r => r.AtUtc).ToList();
    }
}

// PPE camera message (common JSON): {"deviceId","channel","peopleInRoi":1,"items":{"helmet":true,"boots":false},"capturedUtc"?}
public static class PpeMessage
{
    public static IEnumerable<(string Device, string Channel, PpeReading Reading)> Parse(JsonElement root, DateTime receivedUtc)
    {
        foreach (var x in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root])
        {
            var items = new Dictionary<string, bool>();
            if (x.TryGetProperty("items", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var p in map.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) items[p.Name.ToLowerInvariant()] = p.Value.GetBoolean();
            // The station clock decides freshness: camera time is only used when it is close to the receive time.
            var at = x.TryGetProperty("capturedUtc", out var t) && t.TryGetDateTime(out var c) && Math.Abs((c.ToUniversalTime() - receivedUtc).TotalSeconds) < 5 ? c.ToUniversalTime() : receivedUtc;
            yield return (T1550Adapter.Normalize(T1550Adapter.Str(x, "deviceId")), T1550Adapter.Str(x, "channel"),
                new PpeReading(at, x.TryGetProperty("peopleInRoi", out var n) && n.TryGetInt32(out var people) ? people : -1, items));
        }
    }
}
