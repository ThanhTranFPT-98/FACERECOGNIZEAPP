using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// CR-015 flexible device input (docs/DEVICE_INPUT.md). Layers:
//   1 transport: HTTP receiver, receiver ports, or an in-process SDK connector → InboxWriter (store before ACK)
//   2 decoding:  built-in code adapters, or a DeviceProfile (declarative mapping of a JSON/XML message)
//   3 contract:  ExternalObservation; external person IDs resolved through PersonIdentity
//   4 business:  ParkingService.Ingest (unchanged). A profile only produces evidence, never a command.

public class DeviceProfile
{
    // Also the adapter name: /api/v1/parking/receiver/{Id}, Receiver:Ports, InboxWriter.Store(Id, …).
    [MaxLength(40)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // Bumped on every save; recorded on each inbox row (AdapterVersion) so a result can be traced to the mapping that produced it.
    public int Version { get; set; } = 1;
    [MaxLength(10)] public string Format { get; set; } = "json";
    public string Definition { get; set; } = "{}";
    // A real message from the device, kept so the mapping can be re-tested after an edit.
    public string? Sample { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string UpdatedBy { get; set; } = "";
}

// Face devices report their own IDs (terminal PIN, employee code, UUID). Replacing a device needs a new mapping, not new people.
public class PersonIdentity
{
    public long Id { get; set; }
    // Adapter name (profile ID) whose results use this external ID.
    [MaxLength(40)] public string Source { get; set; } = "";
    [MaxLength(100)] public string ExternalId { get; set; } = "";
    public int PersonId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProfileException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

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

// Message bodies are JSON or XML. XML is turned into the same tree (root element name kept, attributes as "@name", text beside attributes as "#text").
public static class PayloadFormat
{
    private const int MaxDepth = 32;
    public static JsonDocument Parse(string format, string body) => format == "xml" ? XmlToJson(body) : JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth });

    public static bool Valid(string format, string body)
    {
        try { using var _ = Parse(format, body); return true; }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException) { return false; }
    }

    public static JsonDocument XmlToJson(string xml)
    {
        // No DTD, no external entities (XXE), no network access while parsing.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, MaxCharactersInDocument = 20_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var document = XDocument.Load(reader);
        if (document.Root is null) throw new XmlException("Empty XML document.");
        var tree = new JsonObject { [document.Root.Name.LocalName] = Convert(document.Root, 1) };
        return JsonDocument.Parse(tree.ToJsonString(), new JsonDocumentOptions { MaxDepth = MaxDepth * 2 + 2 });
    }

    private static JsonNode? Convert(XElement element, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"XML deeper than {MaxDepth} levels.");
        var node = new JsonObject();
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) node["@" + attribute.Name.LocalName] = attribute.Value;
        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            if (node.Count == 0) return JsonValue.Create(element.Value);
            if (element.Value.Length > 0) node["#text"] = element.Value;
            return node;
        }
        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.Select(c => Convert(c, depth + 1)).ToArray();
            node[group.Key] = items.Length == 1 ? items[0] : new JsonArray(items);
        }
        return node;
    }
}

// Compiled profiles, reused until the profile's Version changes (one API process per site, so no cross-process invalidation).
public sealed class ProfileCache
{
    private readonly ConcurrentDictionary<string, (int Version, ProfileAdapter Adapter)> compiled = new();
    public ProfileAdapter Get(DeviceProfile profile)
    {
        if (compiled.TryGetValue(profile.Id, out var entry) && entry.Version == profile.Version) return entry.Adapter;
        var adapter = ProfileAdapter.Compile(profile);
        compiled[profile.Id] = (profile.Version, adapter);
        return adapter;
    }
}

// Built-in code adapters first, then enabled device profiles.
public sealed class DeviceAdapterResolver(CameraAdapters builtIn, ParkingDb db, ProfileCache cache)
{
    public async Task<ICameraAdapter?> Find(string name, CancellationToken stop = default)
    {
        if (builtIn.Find(name) is { } adapter) return adapter;
        var profile = await db.DeviceProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == name && x.Enabled, stop);
        if (profile is null) return null;
        // Profiles are validated on save; one that no longer compiles is treated as unknown so its messages are refused, not misread.
        try { return cache.Get(profile); } catch (ProfileException) { return null; }
    }
}

public enum InboxOutcome { Stored, Duplicate, UnknownAdapter, BadBody }

// The only way a device message enters the system: HTTP receiver or an in-process connector (vendor SDK).
// The message is durable when this returns Stored/Duplicate, so a device (or SDK) may be acknowledged after it, not before.
public sealed class InboxWriter(ParkingDb db, DeviceAdapterResolver adapters, InboxSignal signal)
{
    public async Task<InboxOutcome> Store(string adapterName, string body, string source, CancellationToken stop = default)
    {
        var adapter = await adapters.Find(adapterName, stop);
        if (adapter is null) return InboxOutcome.UnknownAdapter;
        if (!PayloadFormat.Valid(adapter.Format, body)) return InboxOutcome.BadBody;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        // A retry of the same message is acknowledged again but stored once. Connectors must put a device ID/time in the body.
        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.CameraInbox.AnyAsync(x => x.PayloadSha256 == hash && x.ReceivedUtc > since, stop)) return InboxOutcome.Duplicate;
        db.CameraInbox.Add(new CameraInbox { Adapter = adapterName, Payload = body, PayloadSha256 = hash, RemoteIp = source.Length > 64 ? source[..64] : source });
        await db.SaveChangesAsync(stop);   // durable before ACK
        signal.Notify();
        return InboxOutcome.Stored;
    }
}

public class DeviceProfileInput
{
    [Required, RegularExpression("^[a-z0-9][a-z0-9_-]{1,39}$")] public string Id { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [RegularExpression("^(json|xml)$")] public string Format { get; set; } = "json";
    [Required, StringLength(20_000)] public string Definition { get; set; } = "";
    [StringLength(4_000_000)] public string? Sample { get; set; }
}
public class ProfileTestInput
{
    [RegularExpression("^(json|xml)$")] public string Format { get; set; } = "json";
    [Required, StringLength(20_000)] public string Definition { get; set; } = "";
    [Required, StringLength(8_000_000)] public string Sample { get; set; } = "";
}

[ApiController, Route("api/v1/parking/device-profiles"), Authorize(Policy = Perm.ManageSite)]
public class DeviceProfilesController(ParkingDb db, CameraAdapters builtIn) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";
    private static readonly string[] BuiltInNames = ["t1550", "generic", "ppe"];

    [HttpGet]
    public async Task<object> List() => new
    {
        builtIn = BuiltInNames.Where(n => builtIn.Find(n) is not null),
        t1550Example = ProfileAdapter.T1550Example,
        profiles = await db.DeviceProfiles.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
    };

    [HttpPut]
    public async Task<IActionResult> Save(DeviceProfileInput input)
    {
        if (builtIn.Find(input.Id) is not null) return Conflict(new { message = "This ID is a built-in adapter. Choose another ID." });
        try { ProfileAdapter.Compile(input.Id, input.Format, input.Definition, "check"); }
        catch (ProfileException ex) { return Conflict(new { message = "The mapping is not valid.", errors = ex.Errors }); }
        var profile = await db.DeviceProfiles.FindAsync(input.Id);
        if (profile is null) { profile = new DeviceProfile { Id = input.Id, Version = 0 }; db.DeviceProfiles.Add(profile); }
        profile.Name = input.Name.Trim(); profile.Enabled = input.Enabled; profile.Format = input.Format; profile.Definition = input.Definition;
        profile.Sample = string.IsNullOrWhiteSpace(input.Sample) ? null : input.Sample;
        profile.Version++; profile.UpdatedUtc = DateTime.UtcNow; profile.UpdatedBy = Actor;
        db.Log(Actor, "SaveDeviceProfile:v" + profile.Version + (profile.Enabled ? "" : ":disabled"), profile.Id);
        await db.SaveChangesAsync();
        return Ok(new { profile.Id, profile.Version });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var profile = await db.DeviceProfiles.FindAsync(id);
        if (profile is null) return NotFound();
        // Identity mappings are kept: re-creating the profile with the same ID reuses them.
        db.DeviceProfiles.Remove(profile);
        db.Log(Actor, "DeleteDeviceProfile", id);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Runs a sample through a mapping without saving anything, so an admin sees the result before a device depends on it.
    [HttpPost("test"), RequestSizeLimit(16_000_000)]
    public IActionResult Test(ProfileTestInput input)
    {
        ProfileAdapter adapter;
        try { adapter = ProfileAdapter.Compile("test", input.Format, input.Definition, "test"); }
        catch (ProfileException ex) { return Ok(new { ok = false, errors = ex.Errors, warnings = Array.Empty<string>(), observations = Array.Empty<object>() }); }
        JsonDocument document;
        try { document = PayloadFormat.Parse(input.Format, input.Sample); }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException)
        { return Ok(new { ok = false, errors = new[] { "The sample is not valid " + input.Format.ToUpperInvariant() + ": " + ex.Message }, warnings = Array.Empty<string>(), observations = Array.Empty<object>() }); }
        using (document)
        {
            var warnings = new List<string>();
            var observations = adapter.Parse(document.RootElement, warnings);
            return Ok(new
            {
                ok = true, errors = Array.Empty<string>(), warnings,
                observations = observations.Select(o => new
                {
                    o.DeviceId, o.Channel, o.Plate, normalizedPlate = VietnamPlate.Normalize(o.Plate), o.PersonId, o.ExternalPersonId,
                    confidence = o.Confidence is double c && double.IsNaN(c) ? "invalid" : o.Confidence?.ToString(CultureInfo.InvariantCulture), o.CapturedUtc,
                    plateImageBytes = o.PlateImage?.Length, overviewImageBytes = o.OverviewImage?.Length, o.RawKey
                })
            });
        }
    }
}

public class PersonIdentityInput
{
    [Required, StringLength(40)] public string Source { get; set; } = "";
    [Required, StringLength(100)] public string ExternalId { get; set; } = "";
    [Range(1, int.MaxValue)] public int PersonId { get; set; }
}

[ApiController, Route("api/v1/parking/person-identities"), Authorize(Policy = Perm.ManagePeople)]
public class PersonIdentitiesController(ParkingDb db, CameraAdapters builtIn) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    [HttpGet]
    public async Task<object> List([FromQuery] string? source) =>
        await db.PersonIdentities.AsNoTracking().Where(x => source == null || x.Source == source).OrderBy(x => x.Source).ThenBy(x => x.ExternalId).Take(5000).ToListAsync();

    [HttpPut]
    public async Task<IActionResult> Save(PersonIdentityInput input)
    {
        if (await Invalid(input.Source) is { } refused) return refused;
        Upsert(await db.PersonIdentities.ToDictionaryAsync(x => (x.Source, x.ExternalId)), input);
        db.Log(Actor, "SavePersonIdentity:" + input.Source + "/" + input.ExternalId.Trim(), input.PersonId);
        await db.SaveChangesAsync();
        return Ok();
    }

    // Bulk import (e.g. the export of a face terminal's user list), one audit entry.
    [HttpPost("import")]
    public async Task<IActionResult> Import(List<PersonIdentityInput> rows)
    {
        if (rows.Count is 0 or > 5000) return BadRequest(new { message = "Send 1–5000 rows." });
        foreach (var source in rows.Select(r => r.Source).Distinct())
            if (await Invalid(source) is { } refused) return refused;
        var existing = await db.PersonIdentities.ToDictionaryAsync(x => (x.Source, x.ExternalId));
        foreach (var row in rows) Upsert(existing, row);
        db.Log(Actor, "ImportPersonIdentities:" + rows.Count, string.Join(",", rows.Select(r => r.Source).Distinct()));
        await db.SaveChangesAsync();
        return Ok(new { imported = rows.Count });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var row = await db.PersonIdentities.FindAsync(id);
        if (row is null) return NotFound();
        db.PersonIdentities.Remove(row);
        db.Log(Actor, "DeletePersonIdentity:" + row.Source + "/" + row.ExternalId, row.PersonId);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private void Upsert(Dictionary<(string, string), PersonIdentity> existing, PersonIdentityInput input)
    {
        var key = (input.Source, input.ExternalId.Trim());
        if (!existing.TryGetValue(key, out var row)) { row = new PersonIdentity { Source = key.Source, ExternalId = key.Item2 }; db.PersonIdentities.Add(row); existing[key] = row; }
        row.PersonId = input.PersonId;
    }

    private async Task<IActionResult?> Invalid(string source) =>
        builtIn.Find(source) is not null || await db.DeviceProfiles.AnyAsync(x => x.Id == source) ? null : Conflict(new { message = "Unknown source: use a device profile ID or a built-in adapter." });
}
