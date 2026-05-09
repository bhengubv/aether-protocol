// SPDX-License-Identifier: MIT

using Aether.Transport.Abstractions;
using Aether.Transport.Models;
using Aether.Transport.NearLink;
using Aether.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Tests for <see cref="TransportManager"/> — the N-way transport selector. The manager
/// composes optional typed transports (BLE, Wi-Fi Direct, NearLink, CircleLink) with a
/// list of additional ITransportService instances and picks one based on priority and
/// payload size. These tests use lightweight fakes for each transport interface.
/// </summary>
[Collection("AetherTransportStaticState")]
public sealed class TransportManagerTests
{
    private static TransportManager NewManager(
        IBleTransportService? ble = null,
        ICircleLinkTransportService? circleLink = null,
        IWifiDirectService? wifiDirect = null,
        INearLinkTransportService? nearLink = null,
        IEnumerable<ITransportService>? additional = null)
    {
        return new TransportManager(
            NullLogger<TransportManager>.Instance,
            ble,
            circleLink,
            wifiDirect,
            nearLink,
            additional);
    }

    // ─── No transports ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithNoTransports_ReturnsFalseAndIncrementsFailures()
    {
        using var mgr = NewManager();

        var ok = await mgr.SendAsync("peer", [1, 2, 3]);

        Assert.False(ok);
        Assert.Equal(1, mgr.GetMetrics().TotalFailures);
    }

    // ─── Selection priority ──────────────────────────────────────

    [Fact]
    public async Task SendAsync_PrefersNearLink_WhenAvailable()
    {
        var nearLink = new FakeNearLink();
        var ble = new FakeBle();
        using var mgr = NewManager(ble: ble, nearLink: nearLink);

        var ok = await mgr.SendAsync("peer", [1, 2, 3]);

        Assert.True(ok);
        Assert.Equal(1, nearLink.SendCount);
        Assert.Equal(0, ble.SendCount);

        var m = mgr.GetMetrics();
        Assert.Equal(1, m.NearLinkSendCount);
        Assert.Equal(3, m.NearLinkBytesSent);
    }

    [Fact]
    public async Task SendAsync_SmallPayload_UsesBle_WhenNearLinkAbsent()
    {
        var ble = new FakeBle();
        var wifi = new FakeWifiDirect();
        using var mgr = NewManager(ble: ble, wifiDirect: wifi);

        var ok = await mgr.SendAsync("peer", new byte[512]);

        Assert.True(ok);
        Assert.Equal(1, ble.SendCount);
        Assert.Equal(0, wifi.SendCount);
        Assert.Equal(1, mgr.GetMetrics().BleSendCount);
    }

    [Fact]
    public async Task SendAsync_LargePayload_PrefersWifiDirect_OverBle()
    {
        // For payloads >1KB, BLE is skipped at step 2 and Wi-Fi Direct is preferred.
        var ble = new FakeBle();
        var wifi = new FakeWifiDirect();
        using var mgr = NewManager(ble: ble, wifiDirect: wifi);

        var ok = await mgr.SendAsync("peer", new byte[2048]);

        Assert.True(ok);
        Assert.Equal(0, ble.SendCount);
        Assert.Equal(1, wifi.SendCount);
        Assert.Equal(1, mgr.GetMetrics().WifiDirectSendCount);
        Assert.Equal(2048, mgr.GetMetrics().WifiDirectBytesSent);
    }

    [Fact]
    public async Task SendAsync_LargePayload_FallsBackToBle_WhenWifiDirectFails()
    {
        var ble = new FakeBle();
        var wifi = new FakeWifiDirect { ShouldSucceed = false };
        using var mgr = NewManager(ble: ble, wifiDirect: wifi);

        var ok = await mgr.SendAsync("peer", new byte[2048]);

        Assert.True(ok);
        Assert.Equal(1, wifi.SendCount); // attempted
        Assert.Equal(1, ble.SendCount);  // fallback
        var m = mgr.GetMetrics();
        Assert.Equal(0, m.WifiDirectSendCount); // didn't succeed
        Assert.Equal(1, m.BleSendCount);
    }

    [Fact]
    public async Task SendAsync_FallsThroughToCircleLink_WhenOthersUnavailableOrFail()
    {
        var ble = new FakeBle { IsAvailable = false };
        var wifi = new FakeWifiDirect { IsAvailable = false };
        var circle = new FakeCircleLink();
        using var mgr = NewManager(ble: ble, circleLink: circle, wifiDirect: wifi);

        var ok = await mgr.SendAsync("peer", [1, 2, 3]);

        Assert.True(ok);
        Assert.Equal(1, circle.SendCount);
        Assert.Equal(1, mgr.GetMetrics().CircleLinkSendCount);
    }

    // ─── Additional transports ───────────────────────────────────

    [Fact]
    public async Task SendAsync_UsesAdditionalTransport_WhenNoTypedTransportsRegistered()
    {
        var custom = new FakeGenericTransport("Lora");
        using var mgr = NewManager(additional: [custom]);

        var ok = await mgr.SendAsync("peer", [9, 9, 9]);

        Assert.True(ok);
        Assert.Equal(1, custom.SendCount);
        Assert.Equal(1, mgr.GetMetrics().AdditionalSendCount);
        Assert.Equal(3, mgr.GetMetrics().AdditionalBytesSent);
    }

    [Fact]
    public async Task SendAsync_AdditionalTransports_OrderedByPowerCost()
    {
        // Ascending order: lower PowerCostRelative goes first.
        var cheap = new FakeGenericTransport("Cheap") { PowerCostRelative = 1 };
        var costly = new FakeGenericTransport("Costly") { PowerCostRelative = 10 };
        using var mgr = NewManager(additional: [costly, cheap]);

        await mgr.SendAsync("peer", [1]);

        // Cheap one is tried (and succeeds) first; Costly never runs.
        Assert.Equal(1, cheap.SendCount);
        Assert.Equal(0, costly.SendCount);
    }

    [Fact]
    public async Task SendAsync_AdditionalTransport_SkipsUnavailableAndUsesNext()
    {
        var down = new FakeGenericTransport("Down") { IsAvailable = false, PowerCostRelative = 1 };
        var up = new FakeGenericTransport("Up") { PowerCostRelative = 5 };
        using var mgr = NewManager(additional: [down, up]);

        var ok = await mgr.SendAsync("peer", [1]);

        Assert.True(ok);
        Assert.Equal(0, down.SendCount);
        Assert.Equal(1, up.SendCount);
    }

    // ─── Receive fan-out ─────────────────────────────────────────

    [Fact]
    public void DataReceived_ForwardsBleEvent_WithTransportName()
    {
        var ble = new FakeBle();
        using var mgr = NewManager(ble: ble);

        (string sender, byte[] data, string transport)? captured = null;
        mgr.DataReceived += (sender, data, transport) =>
            captured = (sender, data, transport);

        ble.RaiseDataReceived("alice", [42]);

        Assert.NotNull(captured);
        Assert.Equal("alice", captured!.Value.sender);
        Assert.Equal([42], captured.Value.data);
        Assert.Equal("BLE", captured.Value.transport);
    }

    [Fact]
    public void DataReceived_ForwardsAdditionalTransportEvent_WithTransportName()
    {
        var custom = new FakeGenericTransport("Lora");
        using var mgr = NewManager(additional: [custom]);

        (string sender, byte[] data, string transport)? captured = null;
        mgr.DataReceived += (s, d, t) => captured = (s, d, t);

        custom.RaiseDataReceived("bob", [7]);

        Assert.NotNull(captured);
        Assert.Equal("bob", captured!.Value.sender);
        Assert.Equal("Lora", captured.Value.transport);
    }

    // ─── Disposal ────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClearsDataReceivedSubscribers()
    {
        var ble = new FakeBle();
        var mgr = NewManager(ble: ble);

        var fired = false;
        mgr.DataReceived += (_, _, _) => fired = true;

        mgr.Dispose();
        ble.RaiseDataReceived("alice", [1]);

        Assert.False(fired);
    }

    // ─── Fakes ───────────────────────────────────────────────────

    private sealed class FakeBle : IBleTransportService
    {
        public string Name => "BLE";
        public bool IsAvailable { get; set; } = true;
        public bool ShouldSucceed { get; set; } = true;
        public int SendCount { get; private set; }

        public long MaxBandwidthBps => 2_000_000;
        public int MaxRangeMeters => 100;
        public int PowerCostRelative => 3;
        public int MaxConcurrentPeers => 7;

        public event Action<string, byte[]>? DataReceived;
        public event Action<BleAdvertisement>? AdvertisementReceived;

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(ShouldSucceed);
        }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(ShouldSucceed);

        public bool IsConnected(string peerUhid) => IsAvailable;

        public Task<bool> SendAdvertisementAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void RaiseDataReceived(string sender, byte[] data) => DataReceived?.Invoke(sender, data);

        // Suppress "unused event" warnings — this event is part of the contract.
        public void RaiseAdvertisementReceived(BleAdvertisement adv) => AdvertisementReceived?.Invoke(adv);
    }

    private sealed class FakeWifiDirect : IWifiDirectService
    {
        public string Name => "Wi-Fi Direct";
        public bool IsAvailable { get; set; } = true;
        public bool ShouldSucceed { get; set; } = true;
        public int SendCount { get; private set; }

        public long MaxBandwidthBps => 250_000_000;
        public int MaxRangeMeters => 200;
        public int PowerCostRelative => 6;
        public int MaxConcurrentPeers => 8;

        public event Action<string, byte[]>? DataReceived;
        public event Action<string>? PeerConnected;
        public event Action<string>? PeerDisconnected;

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(ShouldSucceed);
        }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(ShouldSucceed);

        public bool IsConnected(string peerUhid) => IsAvailable;

        public Task<bool> ConnectAsync(string peerUhid, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task DisconnectAsync(string peerUhid, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void RaiseDataReceived(string sender, byte[] data) => DataReceived?.Invoke(sender, data);
        public void RaisePeerConnected(string uhid) => PeerConnected?.Invoke(uhid);
        public void RaisePeerDisconnected(string uhid) => PeerDisconnected?.Invoke(uhid);
    }

    private sealed class FakeNearLink : INearLinkTransportService
    {
        public string Name => "NearLink";
        public bool IsAvailable { get; set; } = true;
        public long MaxBandwidthBps => 12_000_000;
        public int MaxRangeMeters => 600;
        public int PowerCostRelative => 1;
        public int MaxConcurrentPeers => 500;
        public bool ShouldSucceed { get; set; } = true;
        public int SendCount { get; private set; }
        public int ConnectedPeerCount => 0;

        public event Action<string, byte[]>? DataReceived;
        public event Action<string>? PeerConnected;
        public event Action<string>? PeerDisconnected;

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(ShouldSucceed);
        }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(ShouldSucceed);

        public bool IsConnected(string peerUhid) => IsAvailable;

        public void RaiseDataReceived(string sender, byte[] data) => DataReceived?.Invoke(sender, data);
        public void RaisePeerConnected(string uhid) => PeerConnected?.Invoke(uhid);
        public void RaisePeerDisconnected(string uhid) => PeerDisconnected?.Invoke(uhid);
    }

    private sealed class FakeCircleLink : ICircleLinkTransportService
    {
        public string Name => "CircleLink";
        public bool IsAvailable { get; set; } = true;
        public bool ShouldSucceed { get; set; } = true;
        public int SendCount { get; private set; }

        public long MaxBandwidthBps => 100_000;
        public int MaxRangeMeters => 1_000;
        public int PowerCostRelative => 4;
        public int MaxConcurrentPeers => 16;

        public event Action<string, byte[]>? DataReceived;
        public event Action<string>? PeerConnected;
        public event Action<string>? PeerDisconnected;

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(ShouldSucceed);
        }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(ShouldSucceed);

        public bool IsConnected(string peerUhid) => IsAvailable;

        public void RaiseDataReceived(string sender, byte[] data) => DataReceived?.Invoke(sender, data);
        public void RaisePeerConnected(string uhid) => PeerConnected?.Invoke(uhid);
        public void RaisePeerDisconnected(string uhid) => PeerDisconnected?.Invoke(uhid);
    }

    private sealed class FakeGenericTransport : ITransportService
    {
        public FakeGenericTransport(string name) => Name = name;

        public string Name { get; }
        public bool IsAvailable { get; set; } = true;
        public bool ShouldSucceed { get; set; } = true;
        public int SendCount { get; private set; }

        public long MaxBandwidthBps => 1_000_000;
        public int MaxRangeMeters => 100;
        public int PowerCostRelative { get; set; } = 5;
        public int MaxConcurrentPeers => 10;

        public event Action<string, byte[]>? DataReceived;

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(ShouldSucceed);
        }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(ShouldSucceed);

        public bool IsConnected(string peerUhid) => IsAvailable;

        public void RaiseDataReceived(string sender, byte[] data) => DataReceived?.Invoke(sender, data);
    }
}
