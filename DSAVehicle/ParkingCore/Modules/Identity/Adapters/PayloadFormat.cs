using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace DSAVehicle.ParkingCore;

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
