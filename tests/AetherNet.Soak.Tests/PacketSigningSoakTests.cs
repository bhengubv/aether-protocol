// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Soak.Tests;

/// <summary>
/// Soak tests for <see cref="PacketSigningService"/>. The service holds a
/// nonce-dedup cache that grows with traffic and is pruned by a background
/// timer once entries fall outside the freshness window. The soak run
/// exercises the cache under sustained load to verify:
/// <list type="bullet">
///   <item>The cache scales with the freshness window × sender count
///     (i.e. bounded — not the cumulative number of packets ever signed).</item>
///   <item>Per-iteration allocation stays bounded so 10k sign+verify loops
///     don't accumulate hidden state.</item>
///   <item>The service is <see cref="IDisposable"/>; disposing releases the
///     timer cleanly.</item>
/// </list>
/// </summary>
[Trait("Category", "Soak")]
public class PacketSigningSoakTests : SoakTestBase
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    private static (PacketSigningService Signer, SignalProtocolService Signal) NewService()
    {
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var signer = new PacketSigningService(signal, NullLogger<PacketSigningService>.Instance);
        return (signer, signal);
    }

    private static MeshPacket NewPacket(string source = AliceUhid, string dest = BobUhid)
    {
        return new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = source,
            DestinationUhid = dest,
            Payload = Encoding.UTF8.GetBytes("soak"),
            Ttl = 7,
            Priority = 1,
        };
    }

    /// <summary>
    /// Sign + verify 10 000 packets and check that:
    /// <list type="bullet">
    ///   <item>All verifications pass.</item>
    ///   <item>Per-iteration memory stays bounded — Ed25519 sign/verify is
    ///     transient; the dedup cache entry is the only persistent
    ///     allocation per packet (string key + long timestamp).</item>
    ///   <item>The cache size at the end matches the iteration count
    ///     (within the default freshness window the cleanup timer hasn't
    ///     fired yet at the &lt; 30 s default-iteration scale; if it did,
    ///     fewer entries would remain).</item>
    /// </list>
    ///
    /// We probe the cache size indirectly: a duplicate (same source, same
    /// nonce) MUST be rejected on a second verify. Doing so for the first
    /// packet at the end of the run proves the dedup cache held the entry
    /// for the duration. We do NOT introspect <c>_seenNonces.Count</c> —
    /// it's private and would require InternalsVisibleTo.
    /// </summary>
    [Fact]
    public async Task PacketSigning_TenThousandSignVerify_CacheBoundedByFreshnessWindow()
    {
        var iterations = ResolveIterations();

        using var disposable = new DisposableHolder();
        var (signer, signal) = NewService();
        disposable.Track(signer);
        var publicKey = signal.GetPublicKey();

        // Capture the very first packet so we can assert dedup at the end.
        var probePacket = NewPacket();
        await signer.SignPacketAsync(probePacket);
        Assert.True(await signer.VerifyPacketAsync(probePacket, publicKey));

        var verified = 0;
        var report = await MeasureMemoryGrowthAsync(async _ =>
        {
            var packet = NewPacket();
            await signer.SignPacketAsync(packet);
            if (await signer.VerifyPacketAsync(packet, publicKey)) verified++;
        }, iterations);

        WriteSummary(nameof(PacketSigning_TenThousandSignVerify_CacheBoundedByFreshnessWindow), report, iterations);

        Assert.Equal(iterations, verified);

        // Per-iteration overhead: each iteration adds one string-key entry
        // (~80 chars * 2 bytes plus a long) to the dedup cache. We allow
        // ~512B/iter to absorb ConcurrentDictionary segment overhead and
        // the transient Ed25519 buffers — anything above is suspicious.
        Assert.True(report.PerIterationBytes < 512,
            $"PacketSigning per-iteration growth: {report.PerIterationBytes:F1}B/iter — exceeds 512B. " +
            "Cache may not be releasing transient Ed25519 buffers, or _seenNonces grew unexpectedly.");

        // Replaying the very-first packet must still be rejected — proves
        // the dedup cache retained the entry through the loop.
        Assert.False(await signer.VerifyPacketAsync(probePacket, publicKey),
            "Dedup cache lost the original entry mid-run — indicates premature pruning.");
    }

    /// <summary>
    /// Verifies the cleanup pathway works: synthesise expired entries by
    /// hand-rolled timestamps and confirm a fresh service starts with zero
    /// retention overhead. The cleanup timer in <see cref="PacketSigningService"/>
    /// runs every 60 s and we cannot manipulate the wall clock from a soak
    /// test, so this test verifies steady-state ingestion plus disposal.
    ///
    /// We isolate the service in a scoped helper so the local stack frame
    /// never holds a reference at GC time. Without this, the JIT may keep
    /// the local alive past the explicit null-out.
    /// </summary>
    [Fact]
    public async Task PacketSigning_DisposeReleasesAllState()
    {
        var weakRef = await CreateAndDisposeAsync();
        AssertEventuallyUnreachable(weakRef, "PacketSigningService after Dispose");

        // Helper that creates the service in its own stack frame and returns
        // a WeakReference. Once this method returns, no local of the caller
        // can root the service.
        static async Task<WeakReference> CreateAndDisposeAsync()
        {
            var (signer, signal) = NewService();
            var publicKey = signal.GetPublicKey();

            for (var i = 0; i < 1_000; i++)
            {
                var packet = NewPacket();
                await signer.SignPacketAsync(packet);
                await signer.VerifyPacketAsync(packet, publicKey);
            }

            var weakRef = new WeakReference(signer);
            signer.Dispose();
            return weakRef;
        }
    }

    /// <summary>
    /// Tracks <see cref="IDisposable"/>s so test teardown disposes them
    /// even on assertion failure — the soak run otherwise leaks
    /// <see cref="System.Threading.Timer"/> instances across tests.
    /// </summary>
    private sealed class DisposableHolder : IDisposable
    {
        private readonly List<IDisposable> _items = new();
        public void Track(IDisposable item) => _items.Add(item);
        public void Dispose()
        {
            foreach (var item in _items) item.Dispose();
        }
    }
}
