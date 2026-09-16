using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PcMate.Agent.Service.Diagnostics;

/// <summary>
/// Журнал агента в %ProgramData%\PcMate\logs. Своя реализация вместо внешней
/// библиотеки: у службы не должно быть лишних зависимостей, а формат нужен простой.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _worker;
    private readonly int _keepDays;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int keepDays = 7)
    {
        _directory = directory;
        _minLevel = minLevel;
        _keepDays = keepDays;
        Directory.CreateDirectory(_directory);

        _worker = new Thread(WriteLoop) { IsBackground = true, Name = "PcMate.FileLogger" };
        _worker.Start();
        CleanOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_directory, $"agent-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Журнал не должен ронять службу.
            }
        }
    }

    private void CleanOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_keepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch
        {
            // Не критично.
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(Short(logLevel)).Append("] ");
            sb.Append(_category).Append(": ");
            sb.Append(formatter(state, exception));

            if (exception is not null)
                sb.AppendLine().Append("    ").Append(exception.GetType().Name).Append(": ")
                  .Append(exception.Message).AppendLine().Append(exception.StackTrace);

            _provider.Enqueue(sb.ToString());
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
