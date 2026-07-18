// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using AetherNet.BitTorrent.Utp;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class UtpSocketTests
{
    [Fact]
    public async Task Handshake_then_reliable_ordered_transfer_over_udp()
    {
        var udpA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int portA = ((IPEndPoint)udpA.Client.LocalEndPoint!).Port;
        int portB = ((IPEndPoint)udpB.Client.LocalEndPoint!).Port;
        udpA.Connect(IPAddress.Loopback, portB);
        udpB.Connect(IPAddress.Loopback, portA);

        await using var initiator = UtpSocket.Initiator(udpA);
        await using var acceptor = UtpSocket.Acceptor(udpB);

        await Task.WhenAll(initiator.ConnectAsync(), acceptor.AcceptAsync())
            .WaitAsync(TimeSpan.FromSeconds(15));

        // ~5 KB → several µTP DATA packets, exercising sequencing + acks.
        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 13 + 1);

        await initiator.WriteAsync(payload).WaitAsync(TimeSpan.FromSeconds(15));
        await initiator.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await acceptor.WaitForFinAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(payload, acceptor.ReceivedBytes);
    }
}
