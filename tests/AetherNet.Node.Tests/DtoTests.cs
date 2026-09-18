// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Tests;

public class DtoTests
{
    [Fact]
    public void Offline_link_status_is_not_linked_with_no_radios()
    {
        Assert.False(NodeLinkStatus.Offline.Linked);
        Assert.Equal(0, NodeLinkStatus.Offline.Total);
        Assert.Equal(0, NodeLinkStatus.Offline.Available);
    }

    [Fact]
    public void Available_counts_only_usable_radios()
    {
        var status = new NodeLinkStatus(true, "Wi-Fi Direct", new[]
        {
            new RadioStatus("Wi-Fi Direct", Available: true, Linked: true, CarriesBps: 50_000_000),
            new RadioStatus("Bluetooth", Available: true, Linked: false, CarriesBps: 9_000),
            new RadioStatus("LoRa", Available: false, Linked: false, CarriesBps: 0),
        });
        Assert.Equal(3, status.Total);
        Assert.Equal(2, status.Available);
    }

    [Fact]
    public void Outbound_results_report_honestly()
    {
        Assert.True(OutboundResult.Sent.Accepted);
        Assert.True(OutboundResult.Queued.Accepted);

        var refused = OutboundResult.Refused("no grant");
        Assert.False(refused.Accepted);
        Assert.Equal("no grant", refused.Detail);
    }

    [Fact]
    public void Inbound_message_carries_a_stable_tag_sender()
    {
        var from = AetherNetTag.Parse("ABCDE-FGHJK");
        var msg = new InboundMessage(from, new byte[] { 1, 2, 3 }, "text", System.DateTimeOffset.UnixEpoch, System.Guid.Empty);
        Assert.Equal(from, msg.From);
        Assert.Equal("text", msg.Kind);
        Assert.Equal(3, msg.Payload.Length);
    }
}
