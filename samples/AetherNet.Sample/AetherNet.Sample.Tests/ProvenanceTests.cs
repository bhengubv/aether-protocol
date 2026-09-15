// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The provenance rule from <c>02_REMAINING_WORK</c> §0a: "every contribution … should carry
/// provenance — who, where, when, signed, ideally co-signed by a nearby witness. <b>Cheap now;
/// brutal to retrofit trust onto a corpus collected without it.</b>"
///
/// <para>
/// The cost of skipping it is not felt today. It is felt the day a reward loop is switched on over a
/// corpus nobody can vouch for, and every earlier contribution has to be discarded or trusted blind.
/// </para>
/// </summary>
public class ProvenanceTests
{
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeSignalProtocol Signal { get; } = new();
        public FakeRadioMesh Radio { get; } = new(Me);
        public ChatService Chat { get; }

        public Rig()
        {
            Chat = ConvergedChat.Build(Store, new FakeIdentity(Me), Signal, new FakePreKeyExchange(), Radio);
            Signal.OpenSessionWith(Them);
            Radio.Link();
        }

        public void Dispose() => Store.Dispose();
    }

    /// <summary>
    /// The last group envelope this device put on the wire, opened back up. Group now rides the reliable
    /// messaging core as sealed Data: the packet payload is the serialized <c>EncryptedPayload</c>, and
    /// its (fake-)ciphertext is the framed plaintext the core sealed — a compression-flag byte, then the
    /// one-byte app kind (0x02 = group), then the group-envelope JSON.
    /// </summary>
    private static GroupEnvelope? LastGroupEnvelope(Rig rig)
    {
        const byte KindGroup = 0x02;
        for (var i = rig.Radio.Sent.Count - 1; i >= 0; i--)
        {
            var packet = AetherNet.Protocol.PacketSerializer.Deserialize(rig.Radio.Sent[i]);
            if (packet.Type != AetherNet.Protocol.PacketType.Data) continue;

            AetherNet.Security.Models.EncryptedPayload sealedPayload;
            try { sealedPayload = AetherNet.Messaging.EncryptedPayloadCodec.Deserialize(packet.Payload); }
            catch { continue; }

            var framed = sealedPayload.Ciphertext;      // [compressionFlag][kind][json]
            if (framed.Length < 2 || framed[1] != KindGroup) continue;

            return GroupEnvelope.Parse(System.Text.Encoding.UTF8.GetString(framed, 2, framed.Length - 2));
        }
        return null;
    }

    // ── Who and when ──────────────────────────────────────────────────────────

    [Fact]
    public void A_group_records_who_created_it()
    {
        var group = new GroupRecord("G0123456789AB", "Load-shedding crew", Me, 1_700_000_000_000);

        Assert.Equal(Me, group.AdminTag);
    }

    [Fact]
    public void A_group_records_when_it_was_created()
    {
        var group = new GroupRecord("G0123456789AB", "Load-shedding crew", Me, 1_700_000_000_000);

        Assert.True(group.CreatedMs > 0);
    }

    // ── Signed ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_group_announcement_is_signed_by_its_author()
    {
        using var rig = new Rig();

        await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        var envelope = LastGroupEnvelope(rig);
        Assert.NotNull(envelope);
        Assert.False(string.IsNullOrEmpty(envelope!.Signature),
            "a group announcement carries no signature — anyone can claim anyone created it");
    }

    [Fact]
    public async Task A_group_message_is_signed_by_its_author()
    {
        using var rig = new Rig();
        var group = await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        await rig.Chat.SendToGroupAsync(group.Id, "power is out");

        var envelope = LastGroupEnvelope(rig);
        Assert.NotNull(envelope);
        Assert.False(string.IsNullOrEmpty(envelope!.Signature),
            "a group message names an author it cannot prove");
    }

    [Fact]
    public async Task A_signature_covers_what_the_contribution_says()
    {
        using var rig = new Rig();
        var group = await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);
        await rig.Chat.SendToGroupAsync(group.Id, "power is out");

        var envelope = LastGroupEnvelope(rig)!;
        var signedAsWritten = envelope.SignedBody();

        envelope.Body = "power is fine";          // someone edits it in transit
        var signedAsTampered = envelope.SignedBody();

        Assert.NotEqual(signedAsWritten, signedAsTampered);
    }

    // ── Where ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Place is recorded coarsely, and — crucially — inside what the signature covers, so it can be
    /// neither forged nor quietly stripped.
    ///
    /// <para>
    /// Nothing in the sample supplies a location yet: there is no location source on either head, and
    /// inventing one would fabricate provenance rather than record it. What this holds is that when a
    /// place is recorded it is carried and protected — so wiring a real source later is a change of
    /// input, not a change of format.
    /// </para>
    /// </summary>
    [Fact]
    public void A_place_when_recorded_is_covered_by_the_signature()
    {
        var withPlace = GroupEnvelope.Parse(
            GroupEnvelope.Message("G0123456789AB", "abc123", Me, "power is out", geoHash: "kf4c"))!;
        var withNone = GroupEnvelope.Parse(
            GroupEnvelope.Message("G0123456789AB", "abc123", Me, "power is out"))!;

        Assert.Equal("kf4c", withPlace.GeoHash);
        Assert.NotEqual(withPlace.SignedBody(), withNone.SignedBody());
    }
}
