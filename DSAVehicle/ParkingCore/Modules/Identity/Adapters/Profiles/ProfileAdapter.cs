using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSAVehicle.ParkingCore;

public sealed class ProfileException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class FieldRule
{
    public List<ProfilePath> Paths { get; } = [];
    public string? Const { get; set; }
    public string? Default { get; set; }
    public List<string> Transforms { get; } = [];
    public Dictionary<string, string>? Map { get; set; }
    public double Scale { get; set; } = 1;
    public string? Time { get; set; }
    public TimeSpan? TimeZone { get; set; }
    public string? Image { get; set; }
}

public sealed class ProfileAdapter : ICameraAdapter
{
    public static readonly string[] FieldNames = ["deviceId", "channel", "plate", "personId", "confidence", "capturedUtc", "plateImage", "overviewImage", "rawKey"];
    private static readonly string[] Transforms = ["trim", "upper", "lower", "alnum", "digits"];
    private static readonly string[] RuleKeys = ["path", "const", "default", "transform", "map", "scale", "time", "timeZone", "image"];
    private const int MaxRecords = 200;

    public string Name { get; }
    public string Version { get; }
    public string Format { get; }
    private ProfilePath? records;
    private readonly List<(ProfilePath Path, bool? Exists, string? EqualsValue)> match = [];
    private readonly Dictionary<string, FieldRule> fields = [];
    private bool personIdDirect;

    private ProfileAdapter(string name, string version, string format) { Name = name; Version = version; Format = format; }

    public static ProfileAdapter Compile(DeviceProfile profile) => Compile(profile.Id, profile.Format, profile.Definition, $"{profile.Id}/v{profile.Version}");

    public static ProfileAdapter Compile(string name, string format, string definition, string version)
    {
        var errors = new List<string>();
        if (format is not ("json" or "xml")) errors.Add("Format must be json or xml.");
        var adapter = new ProfileAdapter(name, version, format);
        JsonObject? root = null;
        try { root = JsonNode.Parse(definition, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException ex) { errors.Add("Definition is not valid JSON: " + ex.Message); }
        if (root is null) { if (errors.Count == 0) errors.Add("Definition must be a JSON object."); throw new ProfileException(errors); }

        foreach (var key in root.Select(p => p.Key).Where(k => k is not ("records" or "match" or "fields" or "personId")))
            errors.Add($"Unknown key '{key}'. Allowed: records, match, fields, personId.");
        if (root["records"] is JsonNode recordsNode)
            Try(errors, () => adapter.records = new ProfilePath(Text(recordsNode, "records")));
        if (root["personId"] is JsonNode mode)
        {
            var value = mode.GetValueKind() == JsonValueKind.String ? mode.GetValue<string>() : "";
            if (value is not ("identity" or "direct")) errors.Add("personId must be \"identity\" (look up PersonIdentity) or \"direct\" (the device ID is our PersonId).");
            adapter.personIdDirect = value == "direct";
        }
        if (root["match"] is JsonNode matchNode)
        {
            if (matchNode is not JsonArray rules) errors.Add("match must be a list of {path, exists?, equals?}.");
            else foreach (var rule in rules)
            {
                if (rule is not JsonObject m || m["path"] is null) { errors.Add("Each match rule needs a path."); continue; }
                foreach (var key in m.Select(p => p.Key).Where(k => k is not ("path" or "exists" or "equals"))) errors.Add($"Unknown match key '{key}'.");
                Try(errors, () =>
                {
                    var path = new ProfilePath(Text(m["path"]!, "match.path"));
                    bool? exists = m["exists"] is JsonNode e ? e.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new FormatException("match.exists must be true or false.") } : null;
                    string? equals = m["equals"] is JsonNode q ? Scalar(q) : null;
                    if (exists is null && equals is null) exists = true;
                    adapter.match.Add((path, exists, equals));
                });
            }
        }
        if (root["fields"] is not JsonObject fieldNodes) errors.Add("fields is required (an object: field name → path or rule).");
        else
        {
            foreach (var (fieldName, node) in fieldNodes)
            {
                if (!FieldNames.Contains(fieldName)) { errors.Add($"Unknown field '{fieldName}'. Allowed: {string.Join(", ", FieldNames)}."); continue; }
                if (node is null) continue;
                Try(errors, () => adapter.fields[fieldName] = Rule(fieldName, node));
            }
            if (!adapter.fields.ContainsKey("deviceId")) errors.Add("fields.deviceId is required: it links the message to a camera (Camera.ExternalDevice).");
            if (!adapter.fields.ContainsKey("plate") && !adapter.fields.ContainsKey("personId")) errors.Add("Map at least one of fields.plate or fields.personId.");
        }
        if (errors.Count > 0) throw new ProfileException(errors);
        return adapter;
    }

    private static FieldRule Rule(string field, JsonNode node)
    {
        var rule = new FieldRule();
        if (node.GetValueKind() == JsonValueKind.String) { rule.Paths.Add(FieldPath(field, node.GetValue<string>())); return rule; }
        if (node is not JsonObject o) throw new FormatException($"{field}: use a path string or an object.");
        foreach (var key in o.Select(p => p.Key).Where(k => !RuleKeys.Contains(k))) throw new FormatException($"{field}: unknown key '{key}'. Allowed: {string.Join(", ", RuleKeys)}.");
        if (o["path"] is JsonArray paths) foreach (var p in paths) rule.Paths.Add(FieldPath(field, Text(p!, field + ".path")));
        else if (o["path"] is JsonNode single) rule.Paths.Add(FieldPath(field, Text(single, field + ".path")));
        rule.Const = o["const"] is JsonNode c ? Scalar(c) : null;
        rule.Default = o["default"] is JsonNode d ? Scalar(d) : null;
        if (rule.Paths.Count == 0 && rule.Const is null) throw new FormatException($"{field}: needs a path or a const.");
        if (o["transform"] is JsonNode t)
            foreach (var name in t is JsonArray list ? list.Select(x => Text(x!, field + ".transform")) : [Text(t, field + ".transform")])
            {
                if (!Transforms.Contains(name)) throw new FormatException($"{field}: unknown transform '{name}'. Allowed: {string.Join(", ", Transforms)}.");
                rule.Transforms.Add(name);
            }
        if (o["map"] is JsonNode m)
        {
            if (m is not JsonObject table) throw new FormatException($"{field}: map must be an object (device value → our value).");
            rule.Map = table.ToDictionary(x => x.Key, x => x.Value is null ? "" : Scalar(x.Value), StringComparer.Ordinal);
        }
        if (o["scale"] is JsonNode s)
        {
            if (field != "confidence") throw new FormatException($"{field}: scale only applies to confidence.");
            rule.Scale = s.GetValueKind() == JsonValueKind.Number ? s.GetValue<double>() : 0;
            if (!(rule.Scale > 0)) throw new FormatException("confidence.scale must be a number > 0 (e.g. 100 when the device reports 0–100).");
        }
        if (o["time"] is JsonNode time)
        {
            if (field != "capturedUtc") throw new FormatException($"{field}: time only applies to capturedUtc.");
            rule.Time = Text(time, "capturedUtc.time");
        }
        if (o["timeZone"] is JsonNode zone)
        {
            var text = Text(zone, "capturedUtc.timeZone");
            if (!TimeSpan.TryParseExact(text.TrimStart('+'), @"hh\:mm", CultureInfo.InvariantCulture, out var offset) && !(text.StartsWith('-') && TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out offset)))
                throw new FormatException("capturedUtc.timeZone must look like +07:00.");
            rule.TimeZone = text.StartsWith('-') ? -offset : offset;
        }
        if (o["image"] is JsonNode image)
        {
            rule.Image = Text(image, field + ".image");
            if (rule.Image != "base64") throw new FormatException($"{field}: image must be \"base64\" (image URLs are not fetched).");
        }
        return rule;
    }

    private static ProfilePath FieldPath(string field, string text)
    {
        var path = new ProfilePath(text);
        if (path.HasWildcard) throw new FormatException($"{field}: [*] is only allowed in records; use [0] to take the first item.");
        return path;
    }

    public IReadOnlyList<ExternalObservation> Parse(JsonElement root) => Parse(root, null);

    // Device ID read from the message root, for liveness: only works when deviceId is a "$." path or a const.
    public string? DeviceOf(JsonElement root) =>
        fields["deviceId"] is { } rule && (rule.Const is not null || rule.Paths.All(p => p.FromRoot)) ? T1550Adapter.Normalize(Value(rule, root, root) ?? "") : null;

    public IReadOnlyList<ExternalObservation> Parse(JsonElement root, List<string>? warnings)
    {
        var list = new List<ExternalObservation>();
        foreach (var (path, exists, equals) in match)
        {
            var found = path.Select(root, root).FirstOrDefault(e => e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
            var present = found.ValueKind != JsonValueKind.Undefined;
            if (exists is bool mustExist && present != mustExist) { warnings?.Add($"match: '{path.Text}' {(mustExist ? "missing" : "present")} → message skipped"); return list; }
            if (equals is not null && (!present || Scalar(found) != equals)) { warnings?.Add($"match: '{path.Text}' ≠ '{equals}' → message skipped"); return list; }
        }
        var items = records is not null ? records.Select(root, root).ToList()
            : root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        if (items.Count > MaxRecords) { warnings?.Add($"{items.Count} records, only the first {MaxRecords} are read"); items = items.Take(MaxRecords).ToList(); }
        var index = 0;
        foreach (var record in items)
        {
            string? Read(string field) => fields.TryGetValue(field, out var rule) ? Value(rule, record, root) : null;
            var device = T1550Adapter.Normalize(Read("deviceId") ?? "");
            if (device.Length == 0) warnings?.Add($"record {index}: deviceId is empty");
            var person = Read("personId");
            int? personId = null; string? external = null;
            if (!string.IsNullOrWhiteSpace(person))
            {
                if (personIdDirect && int.TryParse(person, NumberStyles.None, CultureInfo.InvariantCulture, out var direct) && direct > 0) personId = direct;
                else if (personIdDirect) warnings?.Add($"record {index}: personId '{person}' is not a positive number; it will be looked up in PersonIdentity");
                if (personId is null) external = person.Trim();
            }
            list.Add(new ExternalObservation(device, Read("channel") ?? "", Read("plate") ?? "", personId,
                Confidence(Read("confidence"), index, warnings), Time(Read("capturedUtc"), index, warnings),
                Image(Read("plateImage"), "plateImage", index, warnings), Image(Read("overviewImage"), "overviewImage", index, warnings),
                $"{Read("rawKey") ?? ""}/{index}", external));
            index++;
        }
        return list;
    }

    private static string? Value(FieldRule rule, JsonElement record, JsonElement root)
    {
        string? value = rule.Const;
        if (value is null)
            foreach (var path in rule.Paths)
            {
                var found = path.Select(record, root).FirstOrDefault(e => e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array));
                if (found.ValueKind == JsonValueKind.Undefined) continue;
                var text = Scalar(found);
                if (text.Length > 0) { value = text; break; }
            }
        if (string.IsNullOrEmpty(value)) return rule.Default;
        foreach (var t in rule.Transforms)
            value = t switch
            {
                "trim" => value.Trim(),
                "upper" => value.ToUpperInvariant(),
                "lower" => value.ToLowerInvariant(),
                "alnum" => new string(value.Where(char.IsLetterOrDigit).ToArray()),
                "digits" => new string(value.Where(char.IsAsciiDigit).ToArray()),
                _ => value
            };
        if (rule.Map is not null && rule.Map.TryGetValue(value, out var mapped)) value = mapped;
        return value;
    }

    private double? Confidence(string? text, int index, List<string>? warnings)
    {
        if (text is null) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) { warnings?.Add($"record {index}: confidence '{text}' is not a number (kept unknown)"); return null; }
        var value = raw / fields["confidence"].Scale;
        if (value is >= 0 and <= 1) return value;
        // Out of range after scaling means the mapping is wrong; NaN makes the policy send it to the guard (INVALID_SCORES).
        warnings?.Add($"record {index}: confidence {raw} is outside 0–1 after scale {fields["confidence"].Scale} → invalid, guard review");
        return double.NaN;
    }

    private DateTime? Time(string? text, int index, List<string>? warnings)
    {
        if (text is null) return null;
        var rule = fields["capturedUtc"];
        var mode = rule.Time ?? "iso";
        DateTime? Local(DateTime unspecified) => rule.TimeZone is TimeSpan zone
            ? new DateTimeOffset(unspecified, zone).UtcDateTime
            // No zone given: the device is assumed to run on the site's clock, the same as the station PC.
            : DateTime.SpecifyKind(unspecified, DateTimeKind.Local).ToUniversalTime();
        try
        {
            switch (mode)
            {
                case "unixSeconds": return DateTimeOffset.FromUnixTimeSeconds(long.Parse(text, CultureInfo.InvariantCulture)).UtcDateTime;
                case "unixMillis": return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(text, CultureInfo.InvariantCulture)).UtcDateTime;
                case "iso":
                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso) && HasOffset(text)) return iso.UtcDateTime;
                    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var plain)) return Local(DateTime.SpecifyKind(plain, DateTimeKind.Unspecified));
                    break;
                default:
                    if (DateTime.TryParseExact(text, mode, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return Local(DateTime.SpecifyKind(exact, DateTimeKind.Unspecified));
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) { }
        warnings?.Add($"record {index}: capturedUtc '{text}' does not match time '{mode}' (kept unknown)");
        return null;
    }
    private static bool HasOffset(string text) => text.EndsWith('Z') || text.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");

    private static byte[]? Image(string? text, string field, int index, List<string>? warnings)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bytes = T1550Adapter.Image(text);
        if (bytes is null) warnings?.Add($"record {index}: {field} is not valid base64 (dropped)");
        return bytes;
    }

    private static void Try(List<string> errors, Action action) { try { action(); } catch (Exception ex) when (ex is FormatException or InvalidOperationException) { errors.Add(ex.Message); } }
    private static string Text(JsonNode node, string name) =>
        node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : throw new FormatException($"{name} must be a string.");
    private static string Scalar(JsonNode node) => node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    private static string Scalar(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : e.GetRawText();

    // Built-in T1550 capture written in the profile language; the UI offers it as a starting point.
    public const string T1550Example = """
        {
          "records": "data.ai_snap_picture.PlateInfo[*]",
          "fields": {
            "deviceId": "$.data.dev_net_info[0].mac",
            "channel": "StrChn",
            "plate": "SnapId",
            "capturedUtc": { "path": "StartTime", "time": "unixSeconds" },
            "plateImage": { "path": "PlateImg", "image": "base64" },
            "overviewImage": { "path": "BgImg", "image": "base64" },
            "rawKey": "GrpId"
          }
        }
        """;
}
