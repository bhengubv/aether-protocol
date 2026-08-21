// SPDX-License-Identifier: MIT
#if ANDROID
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Sends <see cref="ILogger"/> output to logcat, where it can actually be read.
///
/// <para>
/// The app was configured with <c>AddDebug()</c>, which writes to
/// <see cref="System.Diagnostics.Debug"/> — and on a release-configured Android build that goes
/// nowhere at all. Every <c>LogWarning</c> in the app was therefore invisible on the one platform the
/// app runs on. A message that failed to decrypt reported itself faithfully and into a void: the
/// phone that sent it saw "no receipt", the phone that received it showed nothing, and the reason was
/// written down where nobody could ever see it.
/// </para>
///
/// <para>
/// Everything the app writes through <c>global::Android.Util.Log</c> directly was visible the whole
/// time, which made the gap easy to miss — the radio talked, and the layers above it appeared silent
/// because they were merely inaudible.
/// </para>
/// </summary>
public sealed class LogcatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogcatLogger(Shorten(categoryName));

    /// <summary>
    /// Logcat truncates tags, so a full namespace would leave every line labelled the same. The last
    /// segment is the class, which is the part that identifies where a line came from.
    /// </summary>
    private static string Shorten(string category)
    {
        var lastDot = category.LastIndexOf('.');
        var name = lastDot >= 0 ? category[(lastDot + 1)..] : category;
        return name.Length > 23 ? name[..23] : name;   // logcat's own tag limit
    }

    public void Dispose() { }

    private sealed class LogcatLogger(string tag) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// Warnings and worse, always. Information and below only when someone asks for it — a mesh
        /// radio at fifty frames a second would otherwise bury the log it is meant to explain.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message += " — " + exception;

            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error: global::Android.Util.Log.Error(tag, message); break;
                case LogLevel.Warning: global::Android.Util.Log.Warn(tag, message); break;
                default: global::Android.Util.Log.Info(tag, message); break;
            }
        }
    }
}
#endif
