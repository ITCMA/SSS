using System.Reflection;

namespace Sss.Web.Services;

/// <summary>Finds keys in sss.config.json that SSS does not know. The configuration binder ignores them silently,
/// so a setting at the wrong level (for example Sss:UseHttps instead of Sss:Citrix:Monitor:UseHttps) would do nothing.</summary>
public static class ConfigCheck
{
    // Read outside the options classes.
    private static readonly string[] Extra = ["PathBase"];

    public static IReadOnlyList<string> UnknownKeys(IConfigurationSection sss)
    {
        var known = new List<string[]>();
        Collect(typeof(SssOptions), [], known);
        var unknown = new List<string>();
        Walk(sss, typeof(SssOptions), "Sss", unknown);
        return unknown.Select(path =>
        {
            var leaf = path.Split(':')[^1];
            var hint = known.FirstOrDefault(k => k[^1].Equals(leaf, StringComparison.OrdinalIgnoreCase));
            return hint is null ? path : $"{path} (did you mean Sss:{string.Join(':', hint)}?)";
        }).ToArray();
    }

    private static bool IsSection(Type t) => t.IsClass && t != typeof(string) && !t.IsArray && t.Namespace?.StartsWith("Sss", StringComparison.Ordinal) == true;

    private static void Collect(Type type, string[] prefix, List<string[]> into)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var path = prefix.Append(p.Name).ToArray();
            into.Add(path);
            if (IsSection(p.PropertyType)) Collect(p.PropertyType, path, into);
        }
    }

    private static void Walk(IConfigurationSection node, Type type, string path, List<string> unknown)
    {
        foreach (var child in node.GetChildren())
        {
            var prop = type.GetProperty(child.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var childPath = $"{path}:{child.Key}";
            if (prop is null)
            {
                if (!(type == typeof(SssOptions) && Extra.Contains(child.Key, StringComparer.OrdinalIgnoreCase))) unknown.Add(childPath);
            }
            else if (IsSection(prop.PropertyType)) Walk(child, prop.PropertyType, childPath, unknown);
        }
    }
}
