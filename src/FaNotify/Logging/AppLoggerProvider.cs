using Microsoft.Extensions.Logging;

namespace FaNotify.Logging;

internal sealed class AppLoggerProvider : ILoggerProvider
{
    private readonly Lock gate = new();
    private readonly StreamWriter file;

    public AppLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new AppLogger(this);

    public void Dispose() => file.Dispose();

    private void Write(LogLevel level, string message)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}";
        var console = level >= LogLevel.Warning ? Console.Error : Console.Out;
        lock (gate)
        {
            console.WriteLine(entry);
            file.WriteLine(entry);
        }
    }

    private sealed class AppLogger(AppLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            provider.Write(logLevel, message);
        }
    }
}
