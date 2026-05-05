// SPDX-License-Identifier: MIT

using System.Text;
using Aether.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Tests for <see cref="InProcessTransportService"/> — the in-memory simulator used to
/// exercise mesh-protocol code without real radios. The service registers each instance
/// in a static, process-wide registry, so tests reset that registry before and after
/// each test and run sequentially via the "AetherTransportStaticState" collection.
/// </summary>
[Collection("AetherTransportStaticState")]
public sealed class InProcessTransportServiceTests : IDisposable
{
    public InProcessTransportServiceTests()
    {
        // Ensure no leftover nodes from any previous test pollute this one.
        InProcessTransportService.ResetNetwork();
    }

    public void Dispose()
    {
        // Clean up after every test so the next test starts from an empty network.
        InProcessTransportService.ResetNetwork();
    }

    private static InProcessTransportService NewNode(string uhid)
        => new(uhid, NullLogger<InProcessTransportService>.Instance);

    // ─── Basic delivery ──────────────────────────────────────────

    [Fact]
    public async Task SendAsync_TwoNodes_DeliversBytesToPeer()
    {
        using var alice = NewNode("alice");
        using var bob = NewNode("bob");

        string? receivedFrom = null;
        byte[]? receivedData = null;
        bob.DataReceived += (sender, data) =>
        {
            receivedFrom = sender;
            receivedData = data;
        };

        var payload = Encoding.UTF8.GetBytes("hello bob");
        var ok = await alice.SendAsync("bob", payload);

        Assert.True(ok);
        Assert.Equal("alice", receivedFrom);
        Assert.NotNull(receivedData);
        Assert.Equal(payload, receivedData);
    }

    [Fact]
    public async Task SendAsync_DefensivelyCopiesPayload_MutationsPostSendDoNotLeak()
    {
        // The contract documents a defensive copy via Buffer.BlockCopy; verify it.
        using var alice = NewNode("alice");
        using var bob = NewNode("bob");

        byte[]? received = null;
        bob.DataReceived += (_, data) => received = data;

        var payload = new byte[] { 1, 2, 3, 4 };
        await alice.SendAsync("bob", payload);

        // Mutate the original array; the receiver's copy must not change.
        payload[0] = 99;

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, received);
    }

    [Fact]
    public async Task SendAsync_EmptyPayloadIsAllowed()
    {
        using var alice = NewNode("alice");
        using var bob = NewNode("bob");

        byte[]? received = null;
        bob.DataReceived += (_, data) => received = data;

        var ok = await alice.SendAsync("bob", []);

        Assert.True(ok);
        Assert.NotNull(received);
        Assert.Empty(received);
    }

    // ─── Multi-hop ───────────────────────────────────────────────

    [Fact]
    public async Task ThreeNodeMesh_AliceToCharlieToBob_RelaysMultiHop()
    {
        // Alice → Charlie → Bob. Charlie acts as a relay by re-emitting whatever it receives.
        using var alice = NewNode("alice");
        using var charlie = NewNode("charlie");
        using var bob = NewNode("bob");

        var bobReceivedFrom = new List<string>();
        var bobReceivedData = new List<byte[]>();
        bob.DataReceived += (sender, data) =>
        {
            bobReceivedFrom.Add(sender);
            bobReceivedData.Add(data);
        };

        // Charlie automatically relays incoming traffic to Bob. The in-process service
        // delivers synchronously, so we can wait on the returned task to keep the test
        // deterministic (no fire-and-forget races).
        charlie.DataReceived += (sender, data) =>
        {
            charlie.SendAsync("bob", data).GetAwaiter().GetResult();
        };

        var payload = Encoding.UTF8.GetBytes("multi-hop");
        var ok = await alice.SendAsync("charlie", payload);

        Assert.True(ok);
        Assert.Single(bobReceivedFrom);
        Assert.Equal("charlie", bobReceivedFrom[0]);
        Assert.Equal(payload, bobReceivedData[0]);
    }

    // ─── Failure paths ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_UnknownPeer_ReturnsFalse()
    {
        using var alice = NewNode("alice");

        var ok = await alice.SendAsync("nobody-home", [1, 2, 3]);

        Assert.False(ok);
    }

    [Fact]
    public async Task SendAsync_EmptyPeerUhid_ReturnsFalse()
    {
        using var alice = NewNode("alice");

        var ok = await alice.SendAsync(string.Empty, [1, 2, 3]);

        Assert.False(ok);
    }

    [Fact]
    public async Task SendAsync_ToDisposedPeer_ReturnsFalse()
    {
        using var alice = NewNode("alice");
        var bob = NewNode("bob");

        // Dispose Bob explicitly — his UHID should be removed from the registry.
        bob.Dispose();

        var ok = await alice.SendAsync("bob", [1, 2, 3]);

        Assert.False(ok);
    }

    [Fact]
    public async Task SendAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var alice = NewNode("alice");
        using var bob = NewNode("bob");
        alice.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => alice.SendAsync("bob", [1]));
    }

    // ─── Constructor guards ──────────────────────────────────────

    [Fact]
    public void Constructor_DuplicateUhid_Throws()
    {
        using var first = NewNode("dup");

        var ex = Assert.Throws<InvalidOperationException>(() => NewNode("dup"));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void Constructor_NullUhid_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new InProcessTransportService(null!, NullLogger<InProcessTransportService>.Instance));
    }

    // ─── Static state — ResetNetwork / ActiveNodeCount ───────────

    [Fact]
    public void ActiveNodeCount_ReflectsLifecycle()
    {
        Assert.Equal(0, InProcessTransportService.ActiveNodeCount);

        var a = NewNode("a");
        var b = NewNode("b");
        Assert.Equal(2, InProcessTransportService.ActiveNodeCount);

        a.Dispose();
        Assert.Equal(1, InProcessTransportService.ActiveNodeCount);

        b.Dispose();
        Assert.Equal(0, InProcessTransportService.ActiveNodeCount);
    }

    [Fact]
    public void ResetNetwork_ClearsAllNodes()
    {
        _ = NewNode("a");
        _ = NewNode("b");
        _ = NewNode("c");
        Assert.Equal(3, InProcessTransportService.ActiveNodeCount);

        InProcessTransportService.ResetNetwork();

        Assert.Equal(0, InProcessTransportService.ActiveNodeCount);
    }

    // ─── IsConnected ─────────────────────────────────────────────

    [Fact]
    public void IsConnected_ReflectsPeerPresence()
    {
        using var alice = NewNode("alice");
        var bob = NewNode("bob");

        Assert.True(alice.IsConnected("bob"));
        Assert.False(alice.IsConnected("nobody"));
        Assert.False(alice.IsConnected(string.Empty));

        bob.Dispose();
        Assert.False(alice.IsConnected("bob"));
    }

    // ─── Concurrency ─────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ConcurrentSends_AllDelivered()
    {
        using var alice = NewNode("alice");
        using var bob = NewNode("bob");

        var counter = 0;
        bob.DataReceived += (_, _) => Interlocked.Increment(ref counter);

        const int n = 200;
        var tasks = Enumerable.Range(0, n)
            .Select(i => alice.SendAsync("bob", BitConverter.GetBytes(i)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
        Assert.Equal(n, counter);
    }

    // ─── Stream helper ───────────────────────────────────────────

    [Fact]
    public async Task SendStreamAsync_DeliversFullStreamContent()
    {
        using var alice = NewNode("alice");
        using var bob = NewNode("bob");

        byte[]? received = null;
        bob.DataReceived += (_, data) => received = data;

        var payload = Encoding.UTF8.GetBytes("streamed-bytes");
        using var ms = new MemoryStream(payload);

        var ok = await alice.SendStreamAsync("bob", ms);

        Assert.True(ok);
        Assert.NotNull(received);
        Assert.Equal(payload, received);
    }

    // ─── Capability surface (sanity) ─────────────────────────────

    [Fact]
    public void StaticCapabilities_HaveExpectedShape()
    {
        using var alice = NewNode("alice");

        Assert.Equal("InProcess", alice.Name);
        Assert.True(alice.IsAvailable);
        Assert.True(alice.MaxBandwidthBps > 0);
        Assert.Equal(0, alice.PowerCostRelative);
        Assert.True(alice.MaxConcurrentPeers > 0);
    }
}

/// <summary>
/// xUnit collection used to serialize tests that touch the static
/// <c>InProcessTransportService.Network</c> registry. Without it, parallel
/// test classes would clobber each other's nodes via ResetNetwork.
/// </summary>
[CollectionDefinition("AetherTransportStaticState", DisableParallelization = true)]
public sealed class TransportTestsCollection
{
    // Marker class — no body needed.
}
