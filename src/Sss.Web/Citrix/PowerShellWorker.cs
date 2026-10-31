using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sss.Web.Providers;

namespace Sss.Web.Citrix;

/// <summary>One warm PowerShell process speaking line-delimited JSON. Requests are strictly serialised;
/// a timeout or crash kills the process and the next request starts a fresh one.</summary>
internal sealed class PowerShellWorker(CitrixOptions options, string scriptPath, ILogger log) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private long _nextId;

    public async Task<JsonElement> RequestAsync(Dictionary<string, object?> request, CancellationToken ct, int? timeoutSeconds = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? options.RequestTimeoutSeconds));
            try
            {
                return await ExchangeAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill();
                throw new ProviderUnavailableException("Citrix worker timed out.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Kill();
                throw new ProviderUnavailableException("Citrix worker failed: " + ex.Message, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> ExchangeAsync(Dictionary<string, object?> request, CancellationToken ct)
    {
        var proc = EnsureStarted();
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
        await proc.StandardInput.FlushAsync(ct);

        while (true)
        {
            var line = await proc.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("Worker closed its output.");
            if (!line.StartsWith('{')) continue; // ignore any stray console noise
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt64() != id) continue;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return root.Clone();
            var msg = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
            if (msg?.Contains("administrative privilege", StringComparison.OrdinalIgnoreCase) == true)
                msg += " (the app pool identity is not a Citrix administrator, or its role/scope does not allow reading sessions; see the README, section 'Prepare the service account')";
            throw new ProviderUnavailableException("Citrix worker reported: " + msg);
        }
    }

    private Process EnsureStarted()
    {
        if (_proc is { HasExited: false }) return _proc;
        Kill();

        var psi = new ProcessStartInfo(options.PowerShellPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                     "-Controllers", string.Join(',', options.Controllers) })
            psi.ArgumentList.Add(a);
        if (options.Processes.Enabled)
        {
            psi.ArgumentList.Add("-JeaConfiguration");
            psi.ArgumentList.Add(options.Processes.Configuration);
        }
        if (!string.IsNullOrWhiteSpace(options.SnapinOverride))
        {
            psi.ArgumentList.Add("-SnapinOverride");
            psi.ArgumentList.Add(options.SnapinOverride);
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell.");
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log.LogWarning("Citrix worker stderr: {Line}", e.Data);
        };
        proc.BeginErrorReadLine();
        _proc = proc;
        return proc;
    }

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { /* already gone */ }
        p.Dispose();
    }

    public void Dispose()
    {
        Kill();
        _gate.Dispose();
    }
}
