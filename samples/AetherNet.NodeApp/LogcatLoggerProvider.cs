// SPDX-License-Identifier: MIT
#if ANDROID
using Microsoft.Extensions.Logging;

namespace AetherNet.NodeApp;

/// <summary>
/// Routes <see cref="ILogger"/> output to logcat, so the node's own lines — radio bring-up, the messaging
/// core, a decrypt that failed — are visible on the device next to the platform's. Microsoft's AddDebug
/// provider writes to <c>System.Diagnostics.Debug</c>, which on Android reaches nothing: the recurring
/// "the app is silent on the only platform it runs on" trap.
/// </summary>
internal sealed class LogcatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogcatLogger(categoryName);

    public void Dispose() { }

    private sealed class LogcatLogger : ILogger
    {
        private readonly string _tag;

        public LogcatLogger(string category)
            => _tag = "AetherNode/" + (category.Split('.').LastOrDefault() ?? category);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception;

            switch (logLevel)
            {
                case LogLevel.Trace:
                case LogLevel.Debug: global::Android.Util.Log.Debug(_tag, msg); break;
                case LogLevel.Information: global::Android.Util.Log.Info(_tag, msg); break;
                case LogLevel.Warning: global::Android.Util.Log.Warn(_tag, msg); break;
                case LogLevel.Error:
                case LogLevel.Critical: global::Android.Util.Log.Error(_tag, msg); break;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
#endif
