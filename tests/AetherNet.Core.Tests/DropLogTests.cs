// SPDX-License-Identifier: MIT

using AetherNet.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Throwing a packet away quietly, and saying so when it has stopped being one packet. A mesh takes payloads from
/// anyone, so one that will not parse must not make a noise; a feature that understands none of them must.
/// </summary>
public class DropLogTests
{
    [Fact]
    public void OneBadPacketIsNotWorthSayingOutLoud()
    {
        var log = new Captured();
        var count = 0;

        for (var i = 0; i < DropLog.Enough - 1; i++)
        {
            log.Dropped(ref count, new FormatException("nonsense"), "Heartbeat", "9DMPE-YEWAE");
        }

        Assert.Equal(DropLog.Enough - 1, count);
        Assert.Empty(log.Lines.Where(l => l.Level >= LogLevel.Warning));

        // Still said, quietly, with who sent it — a trickle is readable when somebody goes looking.
        Assert.Equal(DropLog.Enough - 1, log.Lines.Count(l => l.Level == LogLevel.Debug));
        Assert.Contains(log.Lines, l => l.Text.Contains("Heartbeat", StringComparison.Ordinal));
    }

    [Fact]
    public void AWallOfThemIsSaidOnce_AndThenRarely()
    {
        var log = new Captured();
        var count = 0;

        for (var i = 0; i < DropLog.Enough; i++)
        {
            log.Dropped(ref count, new FormatException("nonsense"), "PreKeyRequest");
        }

        // The hundredth is where a trickle stops being a fair reading of it.
        var loud = Assert.Single(log.Lines.Where(l => l.Level >= LogLevel.Warning));
        Assert.Contains("PreKeyRequest", loud.Text, StringComparison.Ordinal);
        Assert.Contains("100", loud.Text, StringComparison.Ordinal);

        // And then not again until the thousandth, so a flood is not itself a flood. Up to one short of it:
        for (var i = count; i < DropLog.ThenEvery - 1; i++)
        {
            log.Dropped(ref count, new FormatException("nonsense"), "PreKeyRequest");
        }

        Assert.Equal(DropLog.ThenEvery - 1, count);
        Assert.Single(log.Lines.Where(l => l.Level >= LogLevel.Warning));

        log.Dropped(ref count, new FormatException("nonsense"), "PreKeyRequest");
        Assert.Equal(2, log.Lines.Count(l => l.Level >= LogLevel.Warning));
    }

    [Fact]
    public void EachKindIsCountedApartSoTheDeafOneIsNamed()
    {
        var log = new Captured();
        var voice = 0;
        var screen = 0;

        for (var i = 0; i < DropLog.Enough; i++)
        {
            log.Dropped(ref voice, new FormatException("nonsense"), "VoicePtt");
        }

        log.Dropped(ref screen, new FormatException("nonsense"), "ScreenShare");

        // One kind has gone deaf and the other has had a single bad packet. Sharing a count would hide which.
        var loud = Assert.Single(log.Lines.Where(l => l.Level >= LogLevel.Warning));
        Assert.Contains("VoicePtt", loud.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenShare", loud.Text, StringComparison.Ordinal);
        Assert.Equal(1, screen);
    }

    [Fact]
    public void ADeviceWithNowhereToLogStillCounts()
    {
        // A null logger is ordinary on a head with no logging set up. The count is what the next line reads.
        ILogger? none = null;
        var count = 0;

        none.Dropped(ref count, new FormatException("nonsense"), "Heartbeat");
        none.Dropped(ref count, new FormatException("nonsense"), "Heartbeat");

        Assert.Equal(2, count);
    }

    private sealed class Captured : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
}
