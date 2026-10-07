using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Astra.Server.Hosting;

/// <summary>Minimal daily-rolling file logger (logs/astra-YYYYMMDD.log), keeps 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _minLevel;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Task _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string dir, LogLevel minLevel)
    {
        _dir = dir;
        _minLevel = minLevel;
        Directory.CreateDirectory(dir);
        Cleanup();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, n));

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        var sb = new StringBuilder();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            sb.Clear();
            while (_queue.Reader.TryRead(out var line)) sb.AppendLine(line);
            var file = Path.Combine(_dir, $"astra-{DateTime.Now:yyyyMMdd}.log");
            try
            {
                await File.AppendAllTextAsync(file, sb.ToString()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Dropping a log batch is preferable to crashing the gateway.
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "astra-*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Short(logLevel)}] {category}: {formatter(state, exception)}");
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Enqueue(line);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
