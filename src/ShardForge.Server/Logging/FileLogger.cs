using System.Collections.Concurrent;
using System.Text;

namespace ShardForge.Server.Logging;

/// <summary>
/// Writes the server's log to data/logs/server-YYYY-MM-DD.log (one file a day, kept two weeks), so a server
/// started without a window (by the Manager) still has a log to read.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;
    public const int KeepDays = 14;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "server-*.log"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-KeepDays)) File.Delete(f); } catch (IOException) { }
        _writer = new Thread(Write) { IsBackground = true, Name = "file-log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    internal void Add(string line) => _queue.TryAdd(line);

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path.Combine(_dir, $"server-{DateTime.Now:yyyy-MM-dd}.log"), line, Encoding.UTF8); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= (category.StartsWith("Microsoft.AspNetCore") ? LogLevel.Warning : LogLevel.Information);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            string tag = level switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", LogLevel.Critical => "FATAL", LogLevel.Debug => "debug", _ => "info" };
            string short_ = category[(category.LastIndexOf('.') + 1)..];
            owner.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {tag,-5} {short_}: {format(state, ex)}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
        }
    }
}
