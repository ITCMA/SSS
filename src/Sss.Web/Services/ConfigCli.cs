using System.Text.Json;

namespace Sss.Web.Services;

/// <summary><c>Sss.Web.exe --check-config [file]</c>: validates the configuration like the startup does, lists warnings too, and exits.
/// Without a file the installed <c>sss.config.json</c> and <c>sss.local.json</c> next to the program and the environment variables are
/// checked, i.e. what SSS would run with. With a file only that file (plus <c>sss.local.json</c> next to it) is checked on top of the
/// defaults, so a new configuration can be validated before it is rolled out. Exit code 0 = no errors, 1 = errors, 2 = file unreadable.</summary>
public static class ConfigCli
{
    public static int Run(string[] args, TextWriter output, string? baseDirectory = null)
    {
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        var file = args.Length > 1 ? args[1] : null;
        IConfigurationRoot config;
        try
        {
            var b = new ConfigurationBuilder().SetBasePath(dir).AddJsonFile("appsettings.json", optional: true);
            if (file is null)
            {
                b.AddJsonFile("sss.config.json", optional: true).AddJsonFile("sss.local.json", optional: true).AddEnvironmentVariables();
                output.WriteLine($"Checking the configuration in {dir}");
            }
            else
            {
                var full = Path.GetFullPath(file);
                if (!File.Exists(full)) { output.WriteLine($"[ERROR] File not found: {full}"); return 2; }
                b.AddJsonFile(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetDirectoryName(full)!), Path.GetFileName(full), optional: false, reloadOnChange: false);
                if (File.Exists(Path.Combine(Path.GetDirectoryName(full)!, "sss.local.json")) && !Path.GetFileName(full).Equals("sss.local.json", StringComparison.OrdinalIgnoreCase))
                    b.AddJsonFile(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetDirectoryName(full)!), "sss.local.json", optional: true, reloadOnChange: false);
                output.WriteLine($"Checking {full}");
            }
            config = b.Build();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or FormatException)
        {
            output.WriteLine("[ERROR] The file is not valid JSON: " + (ex.InnerException?.Message ?? ex.Message));
            return 1;
        }

        var sss = config.GetSection("Sss");
        SssOptions options;
        try { options = sss.Get<SssOptions>() ?? new SssOptions(); }
        catch (InvalidOperationException ex) { output.WriteLine("[ERROR] A value has the wrong type: " + (ex.InnerException?.Message ?? ex.Message)); return 1; }

        var development = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        var findings = ConfigValidator.Validate(options, sss, development);
        foreach (var f in findings.OrderBy(f => f.Level == "error" ? 0 : 1)) output.WriteLine($"[{(f.Level == "error" ? "ERROR" : "WARN ")}] {f.Message}");
        var errors = findings.Count(f => f.Level == "error");
        var warnings = findings.Count - errors;
        output.WriteLine(errors > 0 ? $"The configuration has {errors} error(s) and {warnings} warning(s); SSS would not start."
            : warnings > 0 ? $"OK: no errors, {warnings} warning(s)." : "OK: the configuration is valid.");
        return errors > 0 ? 1 : 0;
    }
}
