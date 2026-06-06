// SPDX-License-Identifier: MIT

using System.Text;
using AetherMesh.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherMesh.Soak.Tests;

/// <summary>
/// Soak tests for the Signal encrypt/decrypt hot path and the DH-ratchet step.
///
/// Why these matter: every Encrypt allocates a fresh per-message key, runs
/// AES-GCM, and zeros the key in the <c>finally</c>. Every DH-ratchet step
/// generates a fresh X25519 keypair. Unit tests catch correctness; only a
/// long run catches:
/// <list type="bullet">
///   <item>Per-message GC pressure that doesn't level off (a leak masked by
///     individual small allocations).</item>
///   <item>Ratchet state bloat — skipped-keys cache or chain state retaining
///     references it should have released after a DH-ratchet boundary.</item>
///   <item>Throughput regressions from the cumulative cost of session-store
///     persistence on every mutation.</item>
/// </list>
/// </summary>
[Trait("Category", "Soak")]
public class SignalEncryptDecryptSoakTests : SoakTestBase
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    private static SignalProtocolService NewService() =>
        new(NullLogger<SignalProtocolService>.Instance);

    /// <summary>
    /// Alice ↔ Bob, alternating encrypts and decrypts, for the full default
    /// iteration budget. Asserts:
    /// <list type="bullet">
    ///   <item>Per-iteration net memory growth stays under 1 KB. AES-GCM,
    ///     HKDF, and the per-message-key allocations are all transient — a
    ///     bigger persistent footprint signals a leak.</item>
    ///   <item>Total net growth stays under 5 MB (generous to absorb the
    ///     Concurrent-Dictionary segment growth that the GC can't reliably
    ///     compact).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task SignalEncryptDecrypt_TenThousandMessages_NoMemoryLeak()
    {
        var iterations = ResolveIterations();

        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Burn the PreKey-flagged first message so the rest of the loop
        // exercises the steady-state Double-Ratchet path. Without this the
        // first iteration's allocations dominate the per-iteration average.
        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("warmup"));
        await bob.DecryptAsync(AliceUhid, first);

        var plaintext = Encoding.UTF8.GetBytes("the mesh is alive — soaking the encrypt/decrypt path");

        var report = await MeasureMemoryGrowthAsync(async iter =>
        {
            // Alternate sender each iteration so the ratchet exercises both
            // chains equally and gets one DH-ratchet step per pair of
            // iterations.
            if ((iter & 1) == 0)
            {
                var enc = await alice.EncryptAsync(BobUhid, plaintext);
                _ = await bob.DecryptAsync(AliceUhid, enc);
            }
            else
            {
                var enc = await bob.EncryptAsync(AliceUhid, plaintext);
                _ = await alice.DecryptAsync(BobUhid, enc);
            }
        }, iterations);

        WriteSummary(nameof(SignalEncryptDecrypt_TenThousandMessages_NoMemoryLeak), report, iterations);

        Assert.True(report.PerIterationBytes < 1_024,
            $"Per-iteration net growth was {report.PerIterationBytes:F1}B/iter — exceeds 1 KB threshold. " +
            $"Likely a memory leak in the encrypt/decrypt path.");

        Assert.True(report.NetGrowthBytes < 5 * 1024 * 1024,
            $"Net growth across {iterations} iterations was {report.NetGrowthBytes:N0}B (>5 MB). " +
            "Either a leak or unbounded session/skip-key state.");
    }

    /// <summary>
    /// Force a thousand DH-ratchet steps by alternating each iteration
    /// (every alternation rotates the sender's ratchet keypair). Verifies
    /// that the session's ratchet state — root key, chain keys,
    /// skipped-message-keys cache — does not bloat over a long
    /// conversation. The skipped-keys dictionary is bounded by
    /// <see cref="SignalProtocolService.MaxSkippedKeys"/> per session, so
    /// in-order delivery should leave it empty.
    /// </summary>
    [Fact]
    public async Task SignalDhRatchet_ThousandRoundtrips_NoStateBloat()
    {
        var iterations = Math.Min(ResolveIterations(), 1_000);

        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // PreKey first.
        var pk = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("bootstrap"));
        await bob.DecryptAsync(AliceUhid, pk);

        var payload = Encoding.UTF8.GetBytes("ratchet-step");

        var report = await MeasureMemoryGrowthAsync(async iter =>
        {
            // True DH-ratchet step: every iteration the receiving side
            // becomes the sender and rotates DHs. Two services per
            // iteration → ~2 DH-ratchet steps per loop.
            var aToB = await alice.EncryptAsync(BobUhid, payload);
            _ = await bob.DecryptAsync(AliceUhid, aToB);

            var bToA = await bob.EncryptAsync(AliceUhid, payload);
            _ = await alice.DecryptAsync(BobUhid, bToA);
        }, iterations);

        WriteSummary(nameof(SignalDhRatchet_ThousandRoundtrips_NoStateBloat), report, iterations);

        // Each iteration is two encrypts + two decrypts + DH-ratchet keypair
        // generation on both sides. The steady-state allocation is bounded
        // by ratchet output (32+32 bytes for new RK/CK). Anything beyond a
        // few KB per iteration is suspicious.
        Assert.True(report.PerIterationBytes < 4_096,
            $"DH-ratchet per-iteration growth: {report.PerIterationBytes:F1}B/iter — exceeds 4 KB. " +
            "Likely a leak in the DH-ratchet step or skipped-keys cache.");
    }
}
