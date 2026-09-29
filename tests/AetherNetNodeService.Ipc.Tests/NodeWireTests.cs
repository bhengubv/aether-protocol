// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNetNodeService;
using Xunit;

namespace AetherNetNodeService.Ipc.Tests;

/// <summary>
/// The wire codec is the one place the bind-contract values become bytes and come back. Every value that
/// crosses the process boundary must survive the trip byte-for-byte — a dropped payload byte or a mangled
/// tag is a message sent to the wrong node, or an identity that reads as absent. These round trips are the
/// guard, and they run without a device.
/// </summary>
public class NodeWireTests
{
    private static AetherNetTag SampleTag(byte seed = 7)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(seed + i);
        }

        return AetherNetTag.FromPublicKey(key);
    }

    [Fact]
    public void Tag_round_trips()
    {
        var tag = SampleTag();
        var back = NodeWire.DecodeTag(NodeWire.EncodeTag(tag));
        Assert.Equal(tag, back);
        Assert.Equal(tag.Value, back.Value);
    }

    [Fact]
    public void Empty_tag_bytes_decode_to_default()
    {
        Assert.Equal(default, NodeWire.DecodeTag([]));
        Assert.Equal(default, NodeWire.DecodeTag(null));
    }

    [Fact]
    public void Send_argument_round_trips_tag_and_payload()
    {
        var to = SampleTag(11);
        var payload = new byte[] { 1, 2, 3, 250, 0, 99 };
        var (backTo, backPayload, backId) = NodeWire.DecodeSendArgument(NodeWire.EncodeSendArgument(to, payload));
        Assert.Equal(to, backTo);
        Assert.Equal(payload, backPayload);
        Assert.Equal(Guid.Empty, backId);   // no id sent → the node assigns one
    }

    [Fact]
    public void SendArgument_carries_the_apps_message_id()
    {
        var id = Guid.NewGuid();
        var (_, _, backId) = NodeWire.DecodeSendArgument(NodeWire.EncodeSendArgument(SampleTag(12), new byte[] { 5 }, id));
        Assert.Equal(id, backId);
    }

    [Fact]
    public void Meet_round_trips_every_contact()
    {
        var contacts = new[]
        {
            new NodeContact(SampleTag(21), new byte[] { 9, 8, 7 }, true),
            new NodeContact(SampleTag(22), null, false),
        };

        var back = NodeWire.DecodeMeet(NodeWire.EncodeMeet(contacts));

        Assert.Equal(2, back.Count);
        Assert.Equal(contacts[0].Tag, back[0].Tag);
        Assert.Equal(contacts[0].PublicKey, back[0].PublicKey);
        Assert.True(back[0].Mutual);
        Assert.Equal(contacts[1].Tag, back[1].Tag);
        Assert.Null(back[1].PublicKey);
        Assert.False(back[1].Mutual);
    }

    [Fact]
    public void Meet_of_nobody_is_an_empty_list()
    {
        Assert.Empty(NodeWire.DecodeMeet(NodeWire.EncodeMeet(Array.Empty<NodeContact>())));
        Assert.Empty(NodeWire.DecodeMeet([]));
    }

    [Fact]
    public void Delivered_round_trips_the_message_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, NodeWire.DecodeDelivered(NodeWire.EncodeDelivered(id)));
        Assert.Equal(Guid.Empty, NodeWire.DecodeDelivered([1, 2, 3]));
    }

    [Theory]
    [InlineData(true, "sent")]
    [InlineData(true, "queued")]
    [InlineData(false, "no session with them yet")]
    public void Outbound_round_trips(bool accepted, string detail)
    {
        var back = NodeWire.DecodeOutbound(NodeWire.EncodeOutbound(new OutboundResult(accepted, detail)));
        Assert.Equal(accepted, back.Accepted);
        Assert.Equal(detail, back.Detail);
    }

    [Fact]
    public void Link_round_trips_including_radios()
    {
        var status = new NodeLinkStatus(true, "Wi-Fi Direct",
        [
            new RadioStatus("Wi-Fi Direct", true, true, 50_000_000),
            new RadioStatus("Bluetooth", true, false, 900_000),
        ]);

        var back = NodeWire.DecodeLink(NodeWire.EncodeLink(status));

        Assert.True(back.Linked);
        Assert.Equal("Wi-Fi Direct", back.Radio);
        Assert.Equal(2, back.Radios.Count);
        Assert.Equal("Bluetooth", back.Radios[1].Name);
        Assert.Equal(900_000, back.Radios[1].CarriesBps);
        Assert.False(back.Radios[1].Linked);
    }

    [Fact]
    public void Link_round_trips_why_a_radio_cannot_be_used()
    {
        var status = new NodeLinkStatus(false, null,
        [
            new RadioStatus("Wi-Fi Direct", false, false, 0) { Reason = "needs permission to find phones nearby", Fixable = true },
            new RadioStatus("LoRa", false, false, 0) { Reason = "plug in a USB LoRa module" },
            new RadioStatus("Bluetooth", true, false, 0),
        ]);

        var back = NodeWire.DecodeLink(NodeWire.EncodeLink(status));

        Assert.Equal("needs permission to find phones nearby", back.Radios[0].Reason);
        Assert.True(back.Radios[0].Fixable);
        Assert.Equal("plug in a USB LoRa module", back.Radios[1].Reason);
        Assert.False(back.Radios[1].Fixable);
        Assert.Null(back.Radios[2].Reason);
    }

    [Fact]
    public void Link_from_a_service_that_sends_no_reason_still_decodes()
    {
        var older = System.Text.Encoding.UTF8.GetBytes(
            """{"linked":false,"radio":null,"radios":[{"name":"Wi-Fi Direct","available":false,"linked":false,"carriesBps":0}]}""");

        var back = NodeWire.DecodeLink(older);

        Assert.Equal("Wi-Fi Direct", back.Radios[0].Name);
        Assert.Null(back.Radios[0].Reason);
        Assert.False(back.Radios[0].Fixable);
    }

    [Fact]
    public void Offline_link_round_trips()
    {
        var back = NodeWire.DecodeLink(NodeWire.EncodeLink(NodeLinkStatus.Offline));
        Assert.False(back.Linked);
        Assert.Null(back.Radio);
        Assert.Empty(back.Radios);
    }

    [Fact]
    public void Inbox_round_trips_messages_with_binary_payload()
    {
        var msgs = new[]
        {
            new InboundMessage(SampleTag(3), new byte[] { 9, 8, 7 }, "text", DateTimeOffset.UnixEpoch, Guid.NewGuid()),
            new InboundMessage(SampleTag(4), Array.Empty<byte>(), "app-package", new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), Guid.NewGuid()),
        };

        var back = NodeWire.DecodeInbox(NodeWire.EncodeInbox(msgs));

        Assert.Equal(2, back.Count);
        Assert.Equal(msgs[0].From, back[0].From);
        Assert.Equal(new byte[] { 9, 8, 7 }, back[0].Payload.ToArray());
        Assert.Equal("app-package", back[1].Kind);
        Assert.Empty(back[1].Payload.ToArray());
        Assert.Equal(msgs[1].Id, back[1].Id);
    }

    [Fact]
    public void Inbound_single_round_trips()
    {
        var m = new InboundMessage(SampleTag(5), new byte[] { 42 }, "text", DateTimeOffset.UnixEpoch, Guid.NewGuid());
        var back = NodeWire.DecodeInbound(NodeWire.EncodeInbound(m));
        Assert.Equal(m.From, back.From);
        Assert.Equal(new byte[] { 42 }, back.Payload.ToArray());
        Assert.Equal(m.Id, back.Id);
        Assert.Equal(m.ReceivedAt, back.ReceivedAt);
    }

    [Theory]
    [InlineData(GrantState.Absent)]
    [InlineData(GrantState.AwaitingGrant)]
    [InlineData(GrantState.Bound)]
    [InlineData(GrantState.Revoked)]
    public void Grant_round_trips(GrantState state)
    {
        Assert.Equal(state, NodeWire.DecodeGrant(NodeWire.EncodeGrant(state)));
    }
}
