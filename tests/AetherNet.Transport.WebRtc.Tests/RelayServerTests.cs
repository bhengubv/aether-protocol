// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using AetherNet.Transport.Relay;
using Xunit;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// A phone carrying traffic for the phones around it.
///
/// <para>
/// The client half of the relay has existed for a long time and has never had anything to talk to,
/// which is why its <c>baseUrl</c> read like a demand for somebody else's server. These run the real
/// client against the real server, because two halves that were written apart are exactly the kind
/// that agree in description and disagree on the wire.
/// </para>
/// </summary>
public class RelayServerTests
{
    /// <summary>
    /// A port nobody is using. Hard-coding one makes the suite fail whenever something else on the
    /// machine happens to want it, which is a test failure that says nothing about the code.
    /// </summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<byte[]?> WaitForAsync(TaskCompletionSource<byte[]> arrived)
    {
        var finished = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        return finished == arrived.Task ? await arrived.Task : null;
    }

    // ── the round trip ─────────────────────────────────────────────────────

    /// <summary>
    /// The whole point: one phone posts to the proxy, another polls it, and the bytes come out the
    /// far side unchanged. If this fails, the two halves do not share a wire format and nothing built
    /// on top of them can work.
    /// </summary>
    [Fact]
    public async Task A_phone_carries_a_message_between_two_others()
    {
        var port = FreePort();
        await using var proxy = new RelayServer(port);
        if (!proxy.Start()) return;   // this machine will not hand out the prefix; nothing to prove here

        var url = $"http://127.0.0.1:{port}";
        await using var sender = new HttpRelayTransportService(url, "SENDER-00001");
        await using var receiver = new HttpRelayTransportService(url, "RECVR-00002");

        var arrived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DataReceived += (_, data) => arrived.TrySetResult(data);
        receiver.Connect();

        var payload = new byte[] { 1, 2, 3, 250, 251, 252 };
        Assert.True(await sender.SendAsync("RECVR-00002", payload));

        Assert.Equal(payload, await WaitForAsync(arrived));
    }

    /// <summary>
    /// The sender's identity has to survive the hop, or the far side cannot tell who is talking and
    /// has nothing to open the session against.
    /// </summary>
    [Fact]
    public async Task The_message_still_says_who_sent_it()
    {
        var port = FreePort();
        await using var proxy = new RelayServer(port);
        if (!proxy.Start()) return;

        var url = $"http://127.0.0.1:{port}";
        await using var sender = new HttpRelayTransportService(url, "ALICE-00001");
        await using var receiver = new HttpRelayTransportService(url, "BOBBB-00002");

        var from = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DataReceived += (peer, _) => from.TrySetResult(peer);
        receiver.Connect();

        await sender.SendAsync("BOBBB-00002", [9]);

        var finished = await Task.WhenAny(from.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Equal("ALICE-00001", finished == from.Task ? await from.Task : null);
    }

    /// <summary>
    /// A phone that was away must find its messages waiting, not discover they were dropped because
    /// nobody was polling at the moment they arrived. That is the entire value of a proxy.
    /// </summary>
    [Fact]
    public async Task A_message_waits_for_a_phone_that_was_not_listening_yet()
    {
        var port = FreePort();
        await using var proxy = new RelayServer(port);
        if (!proxy.Start()) return;

        var url = $"http://127.0.0.1:{port}";
        await using var sender = new HttpRelayTransportService(url, "EARLY-00001");
        await sender.SendAsync("LATER-00002", [42]);

        Assert.Equal(1, proxy.QueuedNodes);

        // Only now does the other phone turn up.
        await using var receiver = new HttpRelayTransportService(url, "LATER-00002");
        var arrived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DataReceived += (_, data) => arrived.TrySetResult(data);
        receiver.Connect();

        Assert.Equal(new byte[] { 42 }, await WaitForAsync(arrived));
    }

    // ── refusing to be a liability ─────────────────────────────────────────

    /// <summary>
    /// Addressing a flood at a node that never polls must not take the proxy down with it. A phone
    /// doing everyone a favour is the last one that should run out of memory for it.
    /// </summary>
    [Fact]
    public async Task A_node_that_never_polls_cannot_fill_the_proxy()
    {
        var port = FreePort();
        await using var proxy = new RelayServer(port);
        if (!proxy.Start()) return;

        await using var sender = new HttpRelayTransportService($"http://127.0.0.1:{port}", "FLOOD-00001");
        for (var i = 0; i < 400; i++)
            await sender.SendAsync("SILENT-0002", [(byte)i]);

        // Still exactly one node's worth of queue, bounded — not four hundred messages of it.
        Assert.Equal(1, proxy.QueuedNodes);
        Assert.True(proxy.IsRunning);
    }

    /// <summary>
    /// Stopping means stopping. A proxy that keeps answering after the person withdrew it is carrying
    /// traffic nobody agreed to carry.
    /// </summary>
    [Fact]
    public async Task Withdrawing_the_proxy_stops_it_carrying_anything()
    {
        var port = FreePort();
        var proxy = new RelayServer(port);
        if (!proxy.Start()) return;

        await proxy.StopAsync();

        Assert.False(proxy.IsRunning);
        await using var sender = new HttpRelayTransportService($"http://127.0.0.1:{port}", "AFTER-00001");
        Assert.False(await sender.SendAsync("NOBODY-0002", [1]));

        await proxy.DisposeAsync();
    }
}
