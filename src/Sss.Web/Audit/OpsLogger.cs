using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sss.Web.Audit;

/// <summary>Writes warnings and errors of SSS itself (not of ASP.NET Core in general) into the "ops" stream, so failover,
/// worker restarts and sink problems reach the same central destinations as the audit trail. Identical messages are written once a minute.</summary>
public sealed class OpsLoggerProvider(JsonlStream stream, TimeProvider clock) : ILoggerProvider
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();

    public ILogger CreateLogger(string category) =>
        category.StartsWith("Sss.", StringComparison.Ordinal) || category.Contains("ExceptionHandler", StringComparison.Ordinal)
            ? new OpsLogger(this, category) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var now = clock.GetUtcNow();
        var key = category + "|" + message;
        if (_last.TryGetValue(key, out var t) && now - t < TimeSpan.FromMinutes(1)) return;
        _last[key] = now;
        if (_last.Count > 500) _last.Clear();
        var detail = message.Length > 500 ? message[..500] : message;
        if (ex is not null) detail += " [" + ex.GetType().Name + ": " + (ex.Message.Length > 200 ? ex.Message[..200] : ex.Message) + "]";
        try
        {
            stream.Append(JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString("N")[..16], time = now, stream = "ops", action = "ops:" + category.Split('.')[^1],
                result = level.ToString().ToLowerInvariant(), detail, server = Environment.MachineName,
            }, Json));
        }
        catch (IOException) { /* the log must never take the application down */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class OpsLogger(OpsLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) owner.Write(category, level, formatter(state, ex), ex);
        }
    }
}
