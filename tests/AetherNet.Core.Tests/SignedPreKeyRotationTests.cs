// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using AetherNet.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the signed-pre-key rotation policy:
///
/// 1. A SPK older than <see cref="SignedPreKeyRotationOptions.RotationInterval"/>
///    is rotated on the next <c>GeneratePreKeyBundleAsync</c> or
///    <c>RotateSignedPreKeyAsync</c> call.
/// 2. Recently-rotated SPKs (within the retention window) still complete
///    X3DH on the responder side.
/// 3. Pruned SPKs (rotated out of the retention window) fail X3DH on the
///    responder side.
///
/// All time math is driven by a synthetic <see cref="DateTimeOffset"/>
/// provider so the tests run deterministically without sleeps.
/// </summary>
public class SignedPreKeyRotationTests
{
    private const string ResponderUhid = "responder-rotation";
    private const string InitiatorUhid = "initiator-rotation";

    /// <summary>
    /// Build a service with a synthetic clock and a mutable rotation
    /// configuration. The returned <c>SetNow</c> action lets tests advance
    /// time without re-instantiating the service.
    /// </summary>
    private static (SignalProtocolService Svc, Action<DateTimeOffset> SetNow) BuildResponder(
        TimeSpan rotationInterval,
        int retainedHistory,
        IKeyValueStore? preKeyKv = null,
        IKeyValueStore? sessionKv = null)
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch.AddYears(40));
        var opts = new SignedPreKeyRotationOptions(rotationInterval, retainedHistory);
        var svc = new SignalProtocolService(
            NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 16,
            sessionStore: sessionKv is null ? null : new KeyValueSignalSessionStore(sessionKv),
            preKeyStore: preKeyKv is null ? null : new KeyValuePreKeyStore(preKeyKv),
            rotationOptions: opts,
            nowProvider: () => clock.Now);
        return (svc, t => clock.Now = t);
    }

    [Fact]
    public async Task NoRotation_BeforeIntervalElapses()
    {
        var (svc, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3);

        var b1 = await svc.GeneratePreKeyBundleAsync(ResponderUhid);
        Assert.Equal(1, svc.SignedPreKeyHistoryCount);

        // Advance 6 days — under the 7-day rotation interval.
        setNow(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(6));
        var b2 = await svc.GeneratePreKeyBundleAsync(ResponderUhid);
        Assert.Equal(b1.SignedPreKeyId, b2.SignedPreKeyId);
        Assert.Equal(1, svc.SignedPreKeyHistoryCount);
    }

    [Fact]
    public async Task RotatesAfterIntervalElapses()
    {
        var (svc, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3);

        var b1 = await svc.GeneratePreKeyBundleAsync(ResponderUhid);

        // Advance past the rotation interval.
        setNow(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(8));
        var b2 = await svc.GeneratePreKeyBundleAsync(ResponderUhid);
        Assert.NotEqual(b1.SignedPreKeyId, b2.SignedPreKeyId);
        Assert.Equal(2, svc.SignedPreKeyHistoryCount);
    }

    [Fact]
    public async Task RetainsPriorSpks_UpToHistoryCount()
    {
        var (svc, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3);

        var t0 = DateTimeOffset.UnixEpoch.AddYears(40);
        await svc.GeneratePreKeyBundleAsync(ResponderUhid); // SPK1

        // Rotate three more times — history should be at the cap (1+3=4).
        for (var i = 1; i <= 3; i++)
        {
            setNow(t0.AddDays(7 * i + 1));
            await svc.GeneratePreKeyBundleAsync(ResponderUhid);
        }
        Assert.Equal(4, svc.SignedPreKeyHistoryCount);

        // One more rotation — oldest entry should be pruned.
        setNow(t0.AddDays(7 * 4 + 1));
        await svc.GeneratePreKeyBundleAsync(ResponderUhid);
        Assert.Equal(4, svc.SignedPreKeyHistoryCount);
    }

    [Fact]
    public async Task RetainedSpk_StillDecryptsInflightX3DH()
    {
        // Responder uses a 7-day rotation interval with 3-deep history.
        var (responder, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3);
        var b0 = await responder.GeneratePreKeyBundleAsync(ResponderUhid);

        // Initiator processes the OLD bundle (b0) and prepares to send.
        var initiator = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        await initiator.GeneratePreKeyBundleAsync(InitiatorUhid);
        await initiator.ProcessPreKeyBundleAsync(b0);

        // Responder rotates SPK before the initiator's first message arrives.
        setNow(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(8));
        var b1 = await responder.GeneratePreKeyBundleAsync(ResponderUhid);
        Assert.NotEqual(b0.SignedPreKeyId, b1.SignedPreKeyId);

        // Initiator now sends — under the OLD SPK. The responder must
        // still be able to decrypt because b0's SPK is in the retained
        // history.
        var msg = await initiator.EncryptAsync(ResponderUhid, Encoding.UTF8.GetBytes("retained-spk-msg"));
        var plain = await responder.DecryptAsync(InitiatorUhid, msg);
        Assert.Equal("retained-spk-msg", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public async Task PrunedSpk_FailsX3DH()
    {
        // Retain 0 prior — every rotation prunes the previous SPK
        // immediately. The initiator's PreKey message under the old
        // SPK then fails on the responder side.
        var (responder, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 0);
        var b0 = await responder.GeneratePreKeyBundleAsync(ResponderUhid);

        var initiator = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        await initiator.GeneratePreKeyBundleAsync(InitiatorUhid);
        await initiator.ProcessPreKeyBundleAsync(b0);

        // Rotate the responder's SPK — b0's SPK is pruned (no retention).
        setNow(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(8));
        await responder.RotateSignedPreKeyAsync();
        Assert.Equal(1, responder.SignedPreKeyHistoryCount);

        var msg = await initiator.EncryptAsync(ResponderUhid, Encoding.UTF8.GetBytes("pruned-spk-msg"));

        // X3DH on the responder side must reject — the SPK referenced by
        // the PreKey message has been pruned.
        await Assert.ThrowsAsync<CryptographicException>(() =>
            responder.DecryptAsync(InitiatorUhid, msg));
    }

    [Fact]
    public async Task ExplicitRotate_ReturnsTrue_WhenIntervalElapsed()
    {
        var (svc, setNow) = BuildResponder(rotationInterval: TimeSpan.FromDays(7), retainedHistory: 1);
        await svc.GeneratePreKeyBundleAsync(ResponderUhid);

        // Inside the interval — explicit rotate is a no-op.
        Assert.False(await svc.RotateSignedPreKeyAsync());
        Assert.Equal(1, svc.SignedPreKeyHistoryCount);

        // Past the interval — explicit rotate succeeds.
        setNow(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(8));
        Assert.True(await svc.RotateSignedPreKeyAsync());
        Assert.Equal(2, svc.SignedPreKeyHistoryCount);
    }

    [Fact]
    public async Task RotationHistory_PersistsAcrossRestart()
    {
        var preKeyKv = new InMemoryKeyValueStore();
        var (svc1, setNow1) = BuildResponder(
            rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3, preKeyKv: preKeyKv);
        await svc1.GeneratePreKeyBundleAsync(ResponderUhid);
        setNow1(DateTimeOffset.UnixEpoch.AddYears(40).AddDays(8));
        await svc1.RotateSignedPreKeyAsync();
        var historyBefore = svc1.SignedPreKeyHistoryCount;

        // Restart against the same store. History should be hydrated.
        var (svc2, _) = BuildResponder(
            rotationInterval: TimeSpan.FromDays(7), retainedHistory: 3, preKeyKv: preKeyKv);
        Assert.Equal(historyBefore, svc2.SignedPreKeyHistoryCount);
    }

    [Fact]
    public void NegativeRetentionCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalProtocolService(
            NullLogger<SignalProtocolService>.Instance,
            preKeyStore: null,
            rotationOptions: new SignedPreKeyRotationOptions(TimeSpan.FromDays(7), -1)));
    }

    [Fact]
    public void ZeroRotationInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalProtocolService(
            NullLogger<SignalProtocolService>.Instance,
            preKeyStore: null,
            rotationOptions: new SignedPreKeyRotationOptions(TimeSpan.Zero, 1)));
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
        public MutableClock(DateTimeOffset now) { Now = now; }
    }
}
