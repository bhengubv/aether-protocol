// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// AetherNetService's log on Windows — what logcat is on a phone. With no window and no console, a line written
/// anywhere else is a line nobody reads, so it goes to a file beside the identity: <c>aethernetservice.log</c>, kept to
/// about a megabyte, the one before it kept as <c>aethernetservice.log.old</c>.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MostBytes = 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "aethernetservice.log");
    }

    public ILogger CreateLogger(string categoryName)
    {
        var lastDot = categoryName.LastIndexOf('.');
        return new FileLogger(this, lastDot >= 0 ? categoryName[(lastDot + 1)..] : categoryName);
    }

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MostBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the service down with it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message += " — " + exception;
            owner.Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Short(logLevel)} {category}: {message}");
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Critical => "F",
            LogLevel.Error => "E",
            LogLevel.Warning => "W",
            _ => "I",
        };
    }
}
