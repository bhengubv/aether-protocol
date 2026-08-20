// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using AetherNet.Diagnostics;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the OpenTelemetry-compatible instrumentation surface published
/// by <see cref="AetherNetTelemetry"/>:
///   <list type="bullet">
///     <item>Counters increment on encrypt/decrypt round trips</item>
///     <item>Latency histograms record positive samples</item>
///     <item>Activities surface with the expected operation names</item>
///     <item>UHID tags are sanitised — never the raw identifier</item>
///     <item>The hot path is safe to call with no listener attached</item>
///   </list>
///
/// All listeners are explicitly disposed at the end of each test so the
/// global <see cref="AetherNetTelemetry.Meter"/> and
/// <see cref="AetherNetTelemetry.ActivitySource"/> remain in their unsubscribed
/// (zero-overhead) state for subsequent tests.
/// </summary>
public class AetherNetTelemetryTests
{
    private const string AliceUhid = "alice-uhid-very-long-identifier-12345";
    private const string BobUhid = "bob-uhid-very-long-identifier-67890";

    private static SignalProtocolService NewService() =>
        new(NullLogger<SignalProtocolService>.Instance);

    /// <summary>
    /// Round-trips one encrypt + one decrypt on a fresh X3DH session and
    /// asserts that the corresponding counters were both observed at least
    /// once by a subscribed <see cref="MeterListener"/>.
    /// </summary>
    [Fact]
    public async Task Counters_Increment_On_Encrypt_And_Decrypt()
    {
        var observed = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AetherNetTelemetry.MeterName
                    && instrument is Counter<long>)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            observed.AddOrUpdate(instrument.Name, value, (_, current) => current + value));
        listener.Start();

        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("hello mesh"));
        var decrypted = await bob.DecryptAsync(AliceUhid, encrypted);

        Assert.Equal("hello mesh", Encoding.UTF8.GetString(decrypted));

        // Synchronous flush.
        listener.RecordObservableInstruments();

        Assert.True(observed.GetValueOrDefault("aethernet.messages.encrypted") >= 1,
            "MessagesEncrypted counter did not increment");
        Assert.True(observed.GetValueOrDefault("aethernet.messages.decrypted") >= 1,
            "MessagesDecrypted counter did not increment");
        Assert.True(observed.GetValueOrDefault("aethernet.sessions.established") >= 1,
            "SessionsEstablished counter did not increment");
    }

    /// <summary>
    /// Encrypt latency must be a positive number — even on a tiny payload
    /// the chain-key advance + AES-GCM encrypt is observable on a
    /// high-resolution stopwatch.
    /// </summary>
    [Fact]
    public async Task Histograms_Record_NonZero_Latency()
    {
        var samples = new ConcurrentBag<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AetherNetTelemetry.MeterName
                    && instrument is Histogram<double>)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            samples.Add((instrument.Name, value)));
        listener.Start();

        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("payload"));
        await bob.DecryptAsync(AliceUhid, encrypted);

        listener.RecordObservableInstruments();

        var encryptSamples = samples.Where(s => s.Name == "aethernet.encrypt.latency").ToArray();
        var decryptSamples = samples.Where(s => s.Name == "aethernet.decrypt.latency").ToArray();

        Assert.NotEmpty(encryptSamples);
        Assert.NotEmpty(decryptSamples);

        // Latency is non-negative on every modern stopwatch; at least ONE
        // observation must be strictly > 0 unless the host is impossibly fast.
        Assert.All(encryptSamples, s => Assert.True(s.Value >= 0));
        Assert.All(decryptSamples, s => Assert.True(s.Value >= 0));
        Assert.Contains(encryptSamples, s => s.Value > 0);
    }

    /// <summary>
    /// The <see cref="ActivitySource"/> must surface activities with the
    /// canonical operation names — these are public API. Renaming any of
    /// them is a breaking change for downstream observability dashboards.
    /// </summary>
    [Fact]
    public async Task ActivitySource_Produces_Expected_Operation_Names()
    {
        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AetherNetTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("data"));
        await bob.DecryptAsync(AliceUhid, encrypted);

        var names = captured.Select(a => a.OperationName).ToHashSet();
        Assert.Contains("AetherNet.X3DH.Initiator", names);
        Assert.Contains("AetherNet.X3DH.Responder", names);
        Assert.Contains("AetherNet.Encrypt", names);
        Assert.Contains("AetherNet.Decrypt", names);
    }

    /// <summary>
    /// PII safety: every UHID-typed activity tag MUST go through the
    /// sanitiser. Asserts that no captured activity carries the raw
    /// <see cref="AliceUhid"/> or <see cref="BobUhid"/> string anywhere
    /// in its tag values.
    /// </summary>
    [Fact]
    public async Task Activity_Tags_Are_Sanitised_Never_Raw_UHID()
    {
        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AetherNetTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);
        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("payload"));
        await bob.DecryptAsync(AliceUhid, encrypted);

        Assert.NotEmpty(captured);
        foreach (var activity in captured)
        {
            foreach (var (_, value) in activity.TagObjects)
            {
                if (value is string text)
                {
                    Assert.DoesNotContain(AliceUhid, text);
                    Assert.DoesNotContain(BobUhid, text);
                }
            }
        }

        // Sanity check: the sanitiser itself drops the long suffix.
        var sanitised = AetherNetTelemetry.SanitizeUhid(AliceUhid);
        Assert.NotEqual(AliceUhid, sanitised);
        Assert.StartsWith(AliceUhid[..4], sanitised);
        Assert.Contains("...", sanitised);
    }

    /// <summary>
    /// No-listener fast path: with NO MeterListener and NO ActivityListener
    /// attached, encrypt/decrypt must complete without throwing and without
    /// stalling. This is the common production case and the BCL guarantees
    /// that <see cref="Counter{T}.Add(T)"/> on an unsubscribed counter is
    /// effectively a no-op (volatile read + branch). We don't measure
    /// allocations directly in the test (allocation profilers are out of
    /// scope here) — but exercising the path round-trip is enough to
    /// guarantee that no listener-only code accidentally throws when no
    /// listener is attached.
    /// </summary>
    [Fact]
    public async Task NoListener_HotPath_Does_Not_Throw_Or_Allocate_Activity()
    {
        // Sanity: no listeners attached to the source — StartActivity returns null.
        Assert.False(AetherNetTelemetry.ActivitySource.HasListeners(),
            "No ActivityListener should be attached at the start of this test " +
            "(other tests must dispose their listeners). If this fires, a previous " +
            "test leaked a listener.");

        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Round-trip three times — the second send hits the no-PreKey path,
        // the third the post-DH-ratchet path. None of them should observe
        // a non-null Activity (HasListeners is false).
        var enc1 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("one"));
        var enc2 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("two"));
        await bob.DecryptAsync(AliceUhid, enc1);
        await bob.DecryptAsync(AliceUhid, enc2);

        // Reaching here without exception is the assertion.
    }

    /// <summary>
    /// Stable contract: hosts pin to these strings in their OTel pipeline
    /// configuration. Renaming MeterName / ActivitySourceName is a breaking
    /// change.
    /// </summary>
    [Fact]
    public void MeterName_And_ActivitySourceName_Are_Stable()
    {
        Assert.Equal("AetherNet.Protocol", AetherNetTelemetry.MeterName);
        Assert.Equal("AetherNet.Protocol", AetherNetTelemetry.ActivitySourceName);
        Assert.Equal("AetherNet.Protocol", AetherNetTelemetry.Meter.Name);
        Assert.Equal("AetherNet.Protocol", AetherNetTelemetry.ActivitySource.Name);
    }

    /// <summary>
    /// <see cref="ValueStopwatch"/> must report a non-negative elapsed time
    /// after a small busy-wait, and zero for a default-constructed instance.
    /// </summary>
    [Fact]
    public void ValueStopwatch_Reports_Elapsed()
    {
        var sw = ValueStopwatch.StartNew();
        Assert.True(sw.IsActive);

        // Burn a small amount of CPU so any reasonable timer registers > 0.
        var spin = 0;
        for (var i = 0; i < 100_000; i++) spin += i;
        Assert.True(spin >= 0); // keep the loop from being optimised away

        var elapsed = sw.GetElapsedMilliseconds();
        Assert.True(elapsed >= 0);

        var defaultSw = default(ValueStopwatch);
        Assert.False(defaultSw.IsActive);
        Assert.Equal(0d, defaultSw.GetElapsedMilliseconds());
    }
}
