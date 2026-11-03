using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sss.Web.Services;

/// <summary>Builds the JSON schema of <c>sss.config.json</c> from the option classes: names, types, defaults and, from their XML
/// comments, the descriptions. Editors such as Visual Studio Code then offer completion, the descriptions and warnings for unknown
/// or wrongly typed keys. A test keeps the committed file <c>docs/sss.config.schema.json</c> identical to this output.</summary>
public static partial class ConfigSchema
{
    // Values the types cannot express; the keys are the property paths below "Sss".
    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Provider"] = ["Mock", "Citrix"],
        ["Auth:Mode"] = ["Iis", "Dev"],
        ["Citrix:Processes:Audience"] = ["All", "Helpdesk"],
        ["Citrix:Balancing:Strategy"] = ["Failover", "RoundRobin", "LeastLatency"],
        ["Audit:Sinks:Syslog:Protocol"] = ["Udp", "Tcp", "Tls"],
        ["Audit:Sinks:Syslog:Format"] = ["Json", "Cef"],
        ["Audit:Sinks:Syslog:Framing"] = ["Newline", "OctetCount"],
        ["Audit:Sinks:Http:Format"] = ["Ndjson", "JsonArray", "SplunkHec", "ElasticBulk", "Text"],
    };

    private static readonly string[] StreamValues = ["audit", "ops"];

    [GeneratedRegex(@"\s+")] private static partial Regex White();
    [GeneratedRegex(@"<see\s+cref=""[A-Z]:(?:[\w.]*\.)?([\w]+)""\s*/>")] private static partial Regex SeeRef();

    private static string DefaultDocPath() => Path.ChangeExtension(typeof(SssOptions).Assembly.Location, ".xml");

    public static string Build(string? xmlDocPath = null)
    {
        var docs = LoadDocs(xmlDocPath ?? DefaultDocPath());
        var sss = Describe(typeof(SssOptions), "", docs);
        sss["properties"]!.AsObject().Add("PathBase", new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Application path for local runs behind a prefix. Behind IIS the ASP.NET Core Module sets it; leave it empty.",
        });
        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "SSS configuration (sss.config.json, sss.local.json)",
            ["description"] = "Everything lives in the section \"Sss\". Write only what you change. Keys are case-insensitive in SSS; this schema uses the spelling shown. Generated from the option classes; do not edit by hand.",
            ["type"] = "object",
            ["properties"] = new JsonObject { ["Sss"] = sss },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>Property paths of the option classes that have no XML comment (so no description in the schema).</summary>
    public static IReadOnlyList<string> Undocumented(string? xmlDocPath = null)
    {
        var docs = LoadDocs(xmlDocPath ?? DefaultDocPath());
        var missing = new List<string>();
        Walk(typeof(SssOptions), "", docs, missing);
        return missing;
    }

    private static void Walk(Type t, string path, Dictionary<string, string> docs, List<string> missing)
    {
        foreach (var p in Props(t))
        {
            var full = path.Length == 0 ? p.Name : path + ":" + p.Name;
            if (!docs.ContainsKey($"{p.DeclaringType!.FullName}.{p.Name}")) missing.Add(full);
            var inner = p.PropertyType.IsArray ? p.PropertyType.GetElementType()! : p.PropertyType;
            if (IsSection(inner)) Walk(inner, full, docs, missing);
        }
    }

    private static bool IsSection(Type t) => t.IsClass && t != typeof(string) && !t.IsArray && t.Namespace?.StartsWith("Sss", StringComparison.Ordinal) == true;

    private static IEnumerable<PropertyInfo> Props(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);

    private static Dictionary<string, string> LoadDocs(string path)
    {
        var docs = new Dictionary<string, string>();
        if (!File.Exists(path)) return docs;
        foreach (var m in XDocument.Load(path).Descendants("member"))
        {
            var name = (string?)m.Attribute("name");
            if (name is null || !name.StartsWith("P:", StringComparison.Ordinal)) continue;
            var summary = m.Element("summary");
            if (summary is null) continue;
            var text = string.Concat(summary.Nodes().Select(n => n.ToString()));
            text = SeeRef().Replace(text, "$1");
            text = Regex.Replace(text, @"</?(c|para|paramref|see)[^>]*>", "");
            text = System.Net.WebUtility.HtmlDecode(White().Replace(text, " ").Trim());
            if (text.Length > 0) docs[name[2..]] = text;
        }
        return docs;
    }

    private static JsonObject Describe(Type t, string path, Dictionary<string, string> docs)
    {
        var instance = Activator.CreateInstance(t)!;
        var props = new JsonObject();
        foreach (var p in Props(t))
        {
            var full = path.Length == 0 ? p.Name : path + ":" + p.Name;
            JsonObject node;
            var pt = p.PropertyType;
            var section = IsSection(pt) || (pt.IsArray && IsSection(pt.GetElementType()!));
            if (IsSection(pt)) node = Describe(pt, full, docs);
            else if (pt.IsArray && IsSection(pt.GetElementType()!)) node = new JsonObject { ["type"] = "array", ["items"] = Describe(pt.GetElementType()!, full, docs) };
            else if (pt == typeof(string[])) node = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
            else if (pt == typeof(Dictionary<string, string>)) node = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } };
            else if (pt == typeof(bool)) node = new JsonObject { ["type"] = "boolean" };
            else if (pt == typeof(int)) node = new JsonObject { ["type"] = "integer" };
            else node = new JsonObject { ["type"] = "string" };

            if (Enums.TryGetValue(full, out var values))
            {
                if (node["type"]?.GetValue<string>() == "array") node["items"]!.AsObject().Add("enum", Array(values));
                else node.Add("enum", Array(values));
            }
            if (p.Name == "Streams") node["items"]!.AsObject().Add("enum", Array(StreamValues));
            if (!section)
            {
                JsonNode? def = p.GetValue(instance) switch
                {
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    string[] a => Array(a),
                    string s => JsonValue.Create(s),
                    _ => null,
                };
                if (def is not null) node.Add("default", def);
            }
            if (docs.TryGetValue($"{p.DeclaringType!.FullName}.{p.Name}", out var d)) node = Prepend(node, "description", d);
            props.Add(p.Name, node);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
    }

    private static JsonArray Array(IEnumerable<string> v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    // description first for readability in editors
    private static JsonObject Prepend(JsonObject node, string key, string value)
    {
        var result = new JsonObject { [key] = value };
        foreach (var (k, v) in node.ToList()) { node.Remove(k); result.Add(k, v); }
        return result;
    }
}
