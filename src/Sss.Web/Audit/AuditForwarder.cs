using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sss.Web.Audit;

/// <summary>What the status page shows about one sink.</summary>
public sealed class SinkState
{
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastError { get; set; }
    public string? Error { get; set; }
    public long Sent { get; set; }
    public long PendingBytes { get; set; }
}

/// <summary>Copies the local JSONL files to the central sinks. The local file is the outbox: each (stream, sink) pair keeps a
/// position (file and byte offset) in the data folder, sends complete lines in batches and moves the position only after the
/// sink accepted them. An outage, a restart or a recycle therefore loses nothing; delivery is "at least once".</summary>
public sealed partial class AuditForwarder(
    IReadOnlyList<JsonlStream> streams, IReadOnlyList<AuditSink> sinks, string dataDirectory, int retentionDays,
    bool perServerFiles, TimeProvider clock, ILogger<AuditForwarder> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private readonly Dictionary<AuditSink, SinkState> _state = sinks.ToDictionary(s => s, _ => new SinkState());

    public IReadOnlyList<(AuditSink Sink, SinkState State)> States => sinks.Select(s => (s, _state[s])).ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var loops = new List<Task>();
        foreach (var sink in sinks)
            foreach (var stream in streams.Where(st => sink.Options.Streams.Contains(st.Stream, StringComparer.OrdinalIgnoreCase)))
                loops.Add(Task.Run(() => LoopAsync(stream, sink, ct), ct));
        loops.Add(Task.Run(() => RetentionLoopAsync(ct), ct));
        await Task.WhenAll(loops);
    }

    private async Task LoopAsync(JsonlStream stream, AuditSink sink, CancellationToken ct)
    {
        var failures = 0;
        var lastLogged = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var moved = await PumpAsync(stream, sink, ct);
                failures = 0;
                if (moved) continue;
                await Task.Delay(Idle, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failures++;
                var st = _state[sink];
                st.LastError = clock.GetUtcNow();
                st.Error = ex.Message;
                if (clock.GetUtcNow() - lastLogged > TimeSpan.FromMinutes(10))
                {
                    lastLogged = clock.GetUtcNow();
                    log.LogWarning("Audit sink {Sink} ({Target}) is not reachable, events wait in {Dir}: {Error}", sink.Name, sink.Target, stream.Directory, ex.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(failures, 6)))), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private sealed record Cursor(string? File, long Offset);

    private string CursorPath(JsonlStream stream, AuditSink sink) =>
        Path.Combine(dataDirectory, "forward", $"{stream.Stream}-{Regex.Replace(sink.Name, @"[^A-Za-z0-9._-]", "_")}.json");

    private IReadOnlyList<string> Files(JsonlStream stream)
    {
        if (!Directory.Exists(stream.Directory)) return [];
        var own = perServerFiles
            ? new Regex($@"^{stream.Stream}-\d{{8}}-{Regex.Escape(Environment.MachineName)}\.jsonl$", RegexOptions.IgnoreCase)
            : new Regex($@"^{stream.Stream}-\d{{8}}\.jsonl$");
        return Directory.GetFiles(stream.Directory, $"{stream.Stream}-*.jsonl").Where(f => own.IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Sends one batch. Returns true when it moved (so the caller continues at once), false when everything is sent.</summary>
    public async Task<bool> PumpAsync(JsonlStream stream, AuditSink sink, CancellationToken ct)
    {
        var state = _state[sink];
        var files = Files(stream);
        var cursor = LoadCursor(stream, sink) ?? InitialCursor(stream, sink, files);
        var current = cursor.File is null ? files.FirstOrDefault() : files.FirstOrDefault(f => Path.GetFileName(f) == cursor.File);
        if (current is null)
        {
            // the position file was deleted by retention or by hand: continue with the next newer file
            current = cursor.File is null ? null : files.FirstOrDefault(f => string.CompareOrdinal(Path.GetFileName(f), cursor.File) > 0);
            if (current is null) { state.PendingBytes = 0; return false; }
            cursor = new Cursor(Path.GetFileName(current), 0);
        }
        else if (cursor.File is null) cursor = new Cursor(Path.GetFileName(current), 0);

        var (lines, end) = ReadBatch(current, cursor.Offset, sink.Options.BatchSize);
        state.PendingBytes = files.Where(f => string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(current)) >= 0)
            .Sum(f => new FileInfo(f).Length) - cursor.Offset;
        if (lines.Count == 0)
        {
            var next = files.FirstOrDefault(f => string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(current)) > 0);
            if (next is null) { SaveCursor(stream, sink, cursor); return false; }
            SaveCursor(stream, sink, new Cursor(Path.GetFileName(next), 0));
            return true;
        }

        var events = new List<SinkEvent>();
        foreach (var l in lines)
        {
            try { var e = new SinkEvent(l); if (sink.Wants(e)) events.Add(e); }
            catch (JsonException) { /* a damaged line is skipped here; the verification of the local file reports it */ }
        }
        if (events.Count > 0) await sink.SendAsync(events, ct);
        SaveCursor(stream, sink, new Cursor(Path.GetFileName(current), end));
        state.Sent += events.Count;
        state.LastSuccess = clock.GetUtcNow();
        state.Error = null;
        return true;
    }

    private static (List<string> Lines, long End) ReadBatch(string path, long offset, int max)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > fs.Length) offset = 0;   // the file was replaced
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[Math.Min(fs.Length - offset, 1 << 20)];
        var n = fs.Read(buf, 0, buf.Length);
        var consumed = 0;
        var start = 0;
        for (var i = 0; i < n && lines.Count < Math.Max(1, max); i++)
        {
            if (buf[i] != (byte)'\n') continue;
            var text = Encoding.UTF8.GetString(buf, start, i - start).TrimEnd('\r');
            if (text.Length > 0) lines.Add(text);
            start = i + 1;
            consumed = start;
        }
        return (lines, offset + consumed);
    }

    private Cursor InitialCursor(JsonlStream stream, AuditSink sink, IReadOnlyList<string> files)
    {
        // A new sink starts with what is written from now on; old history stays where it is (copy it by hand if needed).
        var c = files.Count == 0 ? new Cursor(null, 0) : new Cursor(Path.GetFileName(files[^1]), new FileInfo(files[^1]).Length);
        SaveCursor(stream, sink, c);
        return c;
    }

    private Cursor? LoadCursor(JsonlStream stream, AuditSink sink)
    {
        try { return JsonSerializer.Deserialize<Cursor>(File.ReadAllText(CursorPath(stream, sink))); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void SaveCursor(JsonlStream stream, AuditSink sink, Cursor c)
    {
        var path = CursorPath(stream, sink);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c));
        File.Move(tmp, path, overwrite: true);
    }

    private async Task RetentionLoopAsync(CancellationToken ct)
    {
        if (retentionDays <= 0) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = clock.GetUtcNow().AddDays(-retentionDays).UtcDateTime.ToString("yyyyMMdd");
                foreach (var stream in streams)
                {
                    // never delete what a sink has not passed yet
                    var barrier = sinks.Where(s => s.Options.Streams.Contains(stream.Stream, StringComparer.OrdinalIgnoreCase))
                        .Select(s => LoadCursor(stream, s)?.File ?? "").Append("~").Order(StringComparer.Ordinal).First();
                    foreach (var f in Files(stream))
                    {
                        var name = Path.GetFileName(f);
                        var day = DayRegex().Match(name).Value;
                        if (day.Length == 8 && string.CompareOrdinal(day, cutoff) < 0 && string.CompareOrdinal(name, barrier) < 0)
                            File.Delete(f);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Audit retention could not delete old files."); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { return; }
        }
    }

    [GeneratedRegex(@"\d{8}")] private static partial Regex DayRegex();
}
