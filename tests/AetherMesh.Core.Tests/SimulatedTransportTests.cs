// SPDX-License-Identifier: MIT

using System.Text;
using AetherMesh.Protocol;
using AetherMesh.Transport.Models;
using AetherMesh.Transport.Services;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Tests for the simulated transport layer:
///   - <see cref="BleGattFramer"/> (static framing/reassembly logic)
///   - <see cref="SimulatedBleGattTransportService"/>
///   - <see cref="SimulatedWifiDirectTransportService"/>
///   - <see cref="SimulatedNearLinkTransportService"/>
///   - Two-node RF bring-up (interop) scenarios
///
/// Each test creates fresh instances and disposes them in a using block to prevent
/// static-registry leakage between tests. The collection attribute serializes all
/// tests that touch static transport registries.
/// </summary>
[Collection("AetherMeshTransportStaticState")]
public sealed class SimulatedTransportTests
{
    // ─── BLE GATT Framer ────────────────────────────────────────────────────

    [Fact]
    public void BleGattFramer_SmallPayload_SingleFrame_RoundTrips()
    {
        var data = Encoding.UTF8.GetBytes("hello");

        var frames = BleGattFramer.Frame(data);

        Assert.Single(frames);

        var result = BleGattFramer.Reassemble(frames);

        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public void BleGattFramer_LargePayload_MultiFrame_Reassembles()
    {
        // 3000 bytes with default MTU=1024 → payload/frame = 1020 bytes → 3 frames.
        var data = new byte[3000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        var frames = BleGattFramer.Frame(data);

        Assert.True(frames.Length > 1, "Expected multiple frames for 3000-byte payload with MTU 1024.");

        var result = BleGattFramer.Reassemble(frames);

        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public void BleGattFramer_IsComplete_ReturnsFalseUntilAllFramesPresent()
    {
        var data = new byte[3000];
        var frames = BleGattFramer.Frame(data);
        Assert.True(frames.Length >= 3, "Precondition: need at least 3 frames.");

        // Only the first two frames accumulated — not yet complete.
        var partial = new List<byte[]> { frames[0], frames[1] };
        Assert.False(BleGattFramer.IsComplete(partial));

        // All frames accumulated — complete.
        var all = new List<byte[]>(frames);
        Assert.True(BleGattFramer.IsComplete(all));
    }

    [Fact]
    public void BleGattFramer_Reassemble_ReturnsNull_OnCorruptFrameCount()
    {
        var data = Encoding.UTF8.GetBytes("test payload");
        var frames = BleGattFramer.Frame(data);

        // Corrupt: feed only a subset of the frames (count mismatch).
        var incomplete = new List<byte[]> { frames[0] };

        // If there's only 1 frame (small payload), we need a payload large enough to produce ≥2 frames.
        var bigData = new byte[2100];
        var bigFrames = BleGattFramer.Frame(bigData);
        Assert.True(bigFrames.Length >= 2, "Precondition: need ≥2 frames.");

        // Supply only the first frame — reassembly must return null.
        var truncated = new List<byte[]> { bigFrames[0] };
        var result = BleGattFramer.Reassemble(truncated);

        Assert.Null(result);
    }

    // ─── Simulated BLE GATT ─────────────────────────────────────────────────

    [Fact]
    public async Task BleGatt_SendAsync_DeliversToPeer_DataReceivedFires()
    {
        using var nodeA = new SimulatedBleGattTransportService("ble-a-1");
        using var nodeB = new SimulatedBleGattTransportService("ble-b-1");

        string? receivedFrom = null;
        byte[]? receivedData = null;
        nodeB.DataReceived += (sender, data) =>
        {
            receivedFrom = sender;
            receivedData = data;
        };

        var payload = Encoding.UTF8.GetBytes("ping");
        var ok = await nodeA.SendAsync("ble-b-1", payload);

        Assert.True(ok);
        Assert.Equal("ble-a-1", receivedFrom);
        Assert.NotNull(receivedData);
        Assert.Equal(payload, receivedData);
    }

    [Fact]
    public async Task BleGatt_SendAsync_LargePayload_ChunkedAndReassembled()
    {
        using var nodeA = new SimulatedBleGattTransportService("ble-a-2");
        using var nodeB = new SimulatedBleGattTransportService("ble-b-2");

        // 5000 bytes — larger than the 1024 MTU, must be chunked.
        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 199);

        byte[]? received = null;
        nodeB.DataReceived += (_, data) => received = data;

        var ok = await nodeA.SendAsync("ble-b-2", payload);

        Assert.True(ok);
        Assert.NotNull(received);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task BleGatt_SendAsync_UnknownPeer_ReturnsFalse()
    {
        using var nodeA = new SimulatedBleGattTransportService("ble-a-3");

        var ok = await nodeA.SendAsync("no-such-peer", [1, 2, 3]);

        Assert.False(ok);
    }

    [Fact]
    public async Task BleGatt_Advertisement_BroadcastsToAllRegisteredPeers()
    {
        using var origin = new SimulatedBleGattTransportService("ble-orig");
        using var peerX = new SimulatedBleGattTransportService("ble-x");
        using var peerY = new SimulatedBleGattTransportService("ble-y");

        var xReceived = new List<BleAdvertisement>();
        var yReceived = new List<BleAdvertisement>();
        var originReceived = new List<BleAdvertisement>();

        peerX.AdvertisementReceived += adv => xReceived.Add(adv);
        peerY.AdvertisementReceived += adv => yReceived.Add(adv);
        origin.AdvertisementReceived += adv => originReceived.Add(adv);

        var advertisement = new BleAdvertisement
        {
            SourceUhid = "ble-orig",
            Rssi = -60,
            Capabilities = 3,
            Payload = [0x01, 0x02],
        };

        var ok = await origin.SendAdvertisementAsync(advertisement);

        Assert.True(ok);
        // x and y each get exactly one advertisement.
        Assert.Single(xReceived);
        Assert.Single(yReceived);
        // origin does NOT receive its own advertisement.
        Assert.Empty(originReceived);
        Assert.Equal("ble-orig", xReceived[0].SourceUhid);
    }

    // ─── Simulated Wi-Fi Direct ──────────────────────────────────────────────

    [Fact]
    public async Task WifiDirect_ConnectAndSend_RoundTrips()
    {
        using var nodeA = new SimulatedWifiDirectTransportService("wifi-a-1");
        using var nodeB = new SimulatedWifiDirectTransportService("wifi-b-1");

        var connected = await nodeA.ConnectAsync("wifi-b-1");
        Assert.True(connected);
        Assert.True(nodeA.IsConnected("wifi-b-1"));
        Assert.True(nodeB.IsConnected("wifi-a-1"));

        byte[]? received = null;
        nodeB.DataReceived += (_, data) => received = data;

        var payload = Encoding.UTF8.GetBytes("wi-fi payload");
        var ok = await nodeA.SendAsync("wifi-b-1", payload);

        Assert.True(ok);
        Assert.NotNull(received);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task WifiDirect_SendAsync_NotConnected_ReturnsFalse()
    {
        using var nodeA = new SimulatedWifiDirectTransportService("wifi-a-2");
        using var nodeB = new SimulatedWifiDirectTransportService("wifi-b-2");

        // Deliberately do NOT call ConnectAsync.
        var ok = await nodeA.SendAsync("wifi-b-2", [1, 2, 3]);

        Assert.False(ok);
    }

    [Fact]
    public async Task WifiDirect_Disconnect_FiresEvent_BothSides()
    {
        using var nodeA = new SimulatedWifiDirectTransportService("wifi-a-3");
        using var nodeB = new SimulatedWifiDirectTransportService("wifi-b-3");

        await nodeA.ConnectAsync("wifi-b-3");

        var aDisconnectedFrom = new List<string>();
        var bDisconnectedFrom = new List<string>();
        nodeA.PeerDisconnected += uhid => aDisconnectedFrom.Add(uhid);
        nodeB.PeerDisconnected += uhid => bDisconnectedFrom.Add(uhid);

        await nodeA.DisconnectAsync("wifi-b-3");

        Assert.Contains("wifi-b-3", aDisconnectedFrom);
        Assert.Contains("wifi-a-3", bDisconnectedFrom);
        Assert.False(nodeA.IsConnected("wifi-b-3"));
        Assert.False(nodeB.IsConnected("wifi-a-3"));
    }

    // ─── Simulated NearLink ──────────────────────────────────────────────────

    [Fact]
    public async Task NearLink_SendAsync_DeliversToPeer()
    {
        using var nodeA = new SimulatedNearLinkTransportService("nl-a-1");
        using var nodeB = new SimulatedNearLinkTransportService("nl-b-1");

        string? receivedFrom = null;
        byte[]? receivedData = null;
        nodeB.DataReceived += (sender, data) =>
        {
            receivedFrom = sender;
            receivedData = data;
        };

        var payload = Encoding.UTF8.GetBytes("nearlink hello");
        var ok = await nodeA.SendAsync("nl-b-1", payload);

        Assert.True(ok);
        Assert.Equal("nl-a-1", receivedFrom);
        Assert.NotNull(receivedData);
        Assert.Equal(payload, receivedData);
    }

    [Fact]
    public async Task NearLink_SendStream_DeliversToPeer()
    {
        using var nodeA = new SimulatedNearLinkTransportService("nl-a-2");
        using var nodeB = new SimulatedNearLinkTransportService("nl-b-2");

        byte[]? received = null;
        nodeB.DataReceived += (_, data) => received = data;

        var payload = Encoding.UTF8.GetBytes("stream-over-nearlink");
        using var ms = new MemoryStream(payload);

        var ok = await nodeA.SendStreamAsync("nl-b-2", ms);

        Assert.True(ok);
        Assert.NotNull(received);
        Assert.Equal(payload, received);
    }

    // ─── RF bring-up — two-node interop ─────────────────────────────────────

    [Fact]
    public async Task RfBringUp_BleAndNearLink_TwoNodeInterop_MeshPacketRoundTrip()
    {
        // Node A sends a MeshPacket to node B via BLE; B then echoes it back via NearLink.
        using var bleA = new SimulatedBleGattTransportService("interop-ble-a");
        using var bleB = new SimulatedBleGattTransportService("interop-ble-b");
        using var nlA = new SimulatedNearLinkTransportService("interop-nl-a");
        using var nlB = new SimulatedNearLinkTransportService("interop-nl-b");

        // Build a MeshPacket to transmit.
        var originalPacket = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "interop-ble-a",
            DestinationUhid = "interop-ble-b",
            Payload = Encoding.UTF8.GetBytes("RF bring-up test payload"),
            PacketNonce = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04],
        };
        var serialized = PacketSerializer.Serialize(originalPacket);

        // B receives via BLE and records the deserialized packet.
        MeshPacket? receivedAtB = null;
        byte[]? echoBytesAtA = null;

        bleB.DataReceived += async (sender, data) =>
        {
            receivedAtB = PacketSerializer.Deserialize(data);
            // Echo back via NearLink.
            await nlB.SendAsync("interop-nl-a", data);
        };

        nlA.DataReceived += (_, data) =>
        {
            echoBytesAtA = data;
        };

        // A sends serialized packet to B via BLE.
        var bleSendOk = await bleA.SendAsync("interop-ble-b", serialized);
        Assert.True(bleSendOk, "BLE send from A to B should succeed.");

        // Validate B received the correct packet.
        Assert.NotNull(receivedAtB);
        Assert.Equal(originalPacket.Id, receivedAtB!.Id);
        Assert.Equal(originalPacket.Type, receivedAtB.Type);
        Assert.Equal(originalPacket.SourceUhid, receivedAtB.SourceUhid);
        Assert.Equal(originalPacket.Payload, receivedAtB.Payload);

        // Validate the NearLink echo arrived at A.
        Assert.NotNull(echoBytesAtA);
        Assert.Equal(serialized, echoBytesAtA);
    }

    [Fact]
    public async Task RfBringUp_WifiDirect_TwoNodeInterop_LargePayload()
    {
        using var nodeA = new SimulatedWifiDirectTransportService("wifi-interop-a");
        using var nodeB = new SimulatedWifiDirectTransportService("wifi-interop-b");

        // Wi-Fi Direct supports up to 64 KB; send exactly 64 KB.
        const int payloadSize = 65_536;
        var payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++)
            payload[i] = (byte)(i % 127);

        byte[]? received = null;
        nodeB.DataReceived += (_, data) => received = data;

        var connected = await nodeA.ConnectAsync("wifi-interop-b");
        Assert.True(connected);

        var ok = await nodeA.SendAsync("wifi-interop-b", payload);
        Assert.True(ok);

        Assert.NotNull(received);
        Assert.Equal(payloadSize, received!.Length);
        Assert.Equal(payload, received);
    }
}
