using System.Globalization;
using System.Text.Json;

namespace DSAVehicle.ParkingCore;

// Path syntax: "a.b[0].c", "[*]" (records only), leading "$." = message root instead of the current record.
// A property step on an array applies to every item and "[0]" on a single object is the object itself,
// because XML gives an object for one element and an array for several.
public sealed class ProfilePath
{
    private abstract record Step;
    private sealed record Property(string Name) : Step;
    private sealed record Index(int Value) : Step;
    private sealed record All : Step;
    private readonly List<Step> steps = [];
    public bool FromRoot { get; }
    public bool HasWildcard => steps.Any(s => s is All);
    public string Text { get; }

    public ProfilePath(string text)
    {
        Text = text.Trim();
        var rest = Text;
        if (rest == "$") { FromRoot = true; return; }
        if (rest.StartsWith("$.")) { FromRoot = true; rest = rest[2..]; }
        var i = 0;
        while (i < rest.Length)
        {
            if (rest[i] == '.') { if (i == 0 || i == rest.Length - 1 || rest[i + 1] == '.') throw new FormatException($"Empty step in path '{Text}'."); i++; continue; }
            if (rest[i] == '[')
            {
                var close = rest.IndexOf(']', i);
                if (close < 0) throw new FormatException($"Missing ']' in path '{Text}'.");
                var inner = rest[(i + 1)..close].Trim();
                if (inner == "*") steps.Add(new All());
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) steps.Add(new Index(n));
                else throw new FormatException($"Use [number] or [*] in path '{Text}'.");
                i = close + 1;
                continue;
            }
            var end = i;
            while (end < rest.Length && rest[end] != '.' && rest[end] != '[') end++;
            steps.Add(new Property(rest[i..end]));
            i = end;
        }
        if (steps.Count == 0 && !FromRoot) throw new FormatException("Empty path.");
    }

    public IEnumerable<JsonElement> Select(JsonElement record, JsonElement root)
    {
        IEnumerable<JsonElement> current = [FromRoot ? root : record];
        foreach (var step in steps) current = current.SelectMany(e => Apply(step, e));
        return current;
    }

    private static IEnumerable<JsonElement> Apply(Step step, JsonElement e)
    {
        switch (step)
        {
            case Property p when e.ValueKind == JsonValueKind.Object:
                if (e.TryGetProperty(p.Name, out var value)) yield return value;
                break;
            case Property p when e.ValueKind == JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(p.Name, out var v)) yield return v;
                break;
            case Index x when e.ValueKind == JsonValueKind.Array:
                if (x.Value < e.GetArrayLength()) yield return e[x.Value];
                break;
            case Index x when x.Value == 0 && e.ValueKind != JsonValueKind.Null && e.ValueKind != JsonValueKind.Undefined:
                yield return e;
                break;
            case All when e.ValueKind == JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) yield return item;
                break;
            case All when e.ValueKind is JsonValueKind.Object:
                yield return e;
                break;
        }
    }
}
