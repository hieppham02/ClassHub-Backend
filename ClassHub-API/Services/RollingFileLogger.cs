using System.Text.Json;
using System.Data.Common;

namespace ClassHub_API.Services;
// Bounded files; no request bodies, scopes, credentials or exception messages are persisted.
public sealed class RollingFileLogger : ILoggerProvider
{
    readonly object gate = new();
    readonly string folder;
    readonly string[] secrets;
    readonly long maxBytes;
    readonly int retained;
    string? current;
    public RollingFileLogger(string path, IConfiguration config, long maxBytes = 5 * 1024 * 1024, int retained = 20)
    {
        folder = Path.GetFullPath(path); Directory.CreateDirectory(folder);
        this.maxBytes = maxBytes; this.retained = retained;
        var values = config.AsEnumerable().Where(k => k.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)).Select(k => k.Value).Where(v => !string.IsNullOrEmpty(v)).Cast<string>().ToList();
        foreach (var cs in config.GetSection("ConnectionStrings").GetChildren()) {
            if (cs.Value == null) continue;
            values.Add(cs.Value);
            try { var b = new DbConnectionStringBuilder { ConnectionString = cs.Value };
                foreach (string key in b.Keys) if (key.Equals("password", StringComparison.OrdinalIgnoreCase) || key.Equals("pwd", StringComparison.OrdinalIgnoreCase)) values.Add(Convert.ToString(b[key])!);
            } catch (ArgumentException) { }
        }
        secrets = values.Where(v => v.Length > 0).Distinct().OrderByDescending(v => v.Length).ToArray();
    }
    string Redact(string value) { foreach (var secret in secrets) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal); return value; }
    void Write(string category, LogLevel level, EventId id, string message, Exception? error)
    {
        var now = DateTime.UtcNow;
        var line = JsonSerializer.Serialize(new { atUtc = now, level = level.ToString(), category, eventId = id.Id,
            traceId = System.Diagnostics.Activity.Current?.TraceId.ToString(), message = Redact(message), exceptionType = error?.GetType().Name }) + "\n";
        if (line.Length > 32768) line = JsonSerializer.Serialize(new { atUtc=now, level=level.ToString(), category, message="Oversized log omitted" }) + "\n";
        lock (gate) {
            try {
                var prefix = "classhub-" + now.ToString("yyyyMMdd") + "-";
                if (current == null || !Path.GetFileName(current).StartsWith(prefix, StringComparison.Ordinal) || new FileInfo(current).Length + line.Length * 4 > maxBytes)
                    current = Path.Combine(folder, prefix + Guid.NewGuid().ToString("N") + ".jsonl");
                File.AppendAllText(current, line);
                foreach (var old in new DirectoryInfo(folder).GetFiles("classhub-????????-????????????????????????????????.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(retained)) if (old.FullName != current) old.Delete();
            } catch (IOException) { Console.Error.WriteLine("ClassHub file log unavailable"); }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("ClassHub file log access denied"); }
        }
    }
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }
    sealed class Sink(RollingFileLogger owner, string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState,Exception?,string> formatter) {
            if (IsEnabled(level)) owner.Write(category, level, id, formatter(state,error), error);
        }
    }
}
