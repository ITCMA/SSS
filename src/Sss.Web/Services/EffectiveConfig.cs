using System.Reflection;
using System.Text.RegularExpressions;

namespace Sss.Web.Services;

/// <summary>One setting SSS runs with: its path below "Sss", the value, and where the value comes from.</summary>
public sealed record ConfigRow(string Path, string Value, string Source, bool Explicit);

/// <summary>The configuration SSS is really running with (read only): every option with its effective value and the file or variable that
/// set it, or "default". Values of secrets (community strings, tokens, passwords) are masked. Meant for finding out why a server behaves
/// differently from another without opening files on it.</summary>
public static partial class EffectiveConfig
{
    [GeneratedRegex("community|token|password|secret", RegexOptions.IgnoreCase)] private static partial Regex SecretName();

    public static IReadOnlyList<ConfigRow> Build(SssOptions options, IConfigurationRoot root)
    {
        var rows = new List<ConfigRow>();
        Walk(options, "", root, rows);
        return rows;
    }

    private static bool IsSection(Type t) => t.IsClass && t != typeof(string) && !t.IsArray && t.Namespace?.StartsWith("Sss", StringComparison.Ordinal) == true;

    private static void Walk(object obj, string path, IConfigurationRoot root, List<ConfigRow> rows)
    {
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            var full = path.Length == 0 ? p.Name : path + ":" + p.Name;
            var value = p.GetValue(obj);
            if (value is null) continue;
            var type = p.PropertyType;
            if (IsSection(type)) Walk(value, full, root, rows);
            else if (type.IsArray && IsSection(type.GetElementType()!))
            {
                var i = 0;
                foreach (var item in (Array)value) Walk(item!, $"{full}:{i++}", root, rows);
            }
            else if (value is Dictionary<string, string> dict)
            {
                foreach (var (k, v) in dict.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) Add(rows, $"{full}:{k}", "token", v, root);   // header values may carry keys: always masked
            }
            else if (value is string[] list) Add(rows, full, p.Name, string.Join(", ", list), root, listIndexed: true);
            else Add(rows, full, p.Name, value is bool b ? (b ? "true" : "false") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "", root);
        }
    }

    private static void Add(List<ConfigRow> rows, string path, string name, string value, IConfigurationRoot root, bool listIndexed = false)
    {
        var key = "Sss:" + path;
        var source = SourceOf(root, listIndexed ? key + ":0" : key) ?? (listIndexed ? SourceOf(root, key) : null);
        // the shipped defaults (appsettings.json) are listed under their own name but do not count as "changed"
        rows.Add(new ConfigRow(path, value.Length > 0 && SecretName().IsMatch(name) ? "••••••" : value, source ?? "default", source is not null && source != "appsettings.json"));
    }

    private static string? SourceOf(IConfigurationRoot root, string key)
    {
        foreach (var provider in root.Providers.Reverse())
            if (provider.TryGet(key, out var v) && v is not null) return Name(provider);
        return null;
    }

    private static string Name(IConfigurationProvider p) =>
        p is FileConfigurationProvider { Source: { } s } && !string.IsNullOrEmpty(s.Path) ? System.IO.Path.GetFileName(s.Path)
            : p.GetType().Name.Replace("ConfigurationProvider", "");
}
