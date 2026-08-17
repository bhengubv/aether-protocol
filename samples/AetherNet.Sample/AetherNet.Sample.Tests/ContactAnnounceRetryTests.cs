// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Telling someone you have added them, when the radio is not ready yet.
///
/// <para>
/// Adding a person is the one action everything else waits on: until both sides have added each other
/// the pair is not real, no session forms, and no message can be sent. And it is almost always done at
/// the worst possible moment — during first-run setup, before any link exists, because a link is
/// exactly what the person is trying to establish.
/// </para>
///
/// <para>
/// Watched on hardware 2026-08-15: a P30 announced its add at 17:43:20 with <c>linked=False
/// sent=False</c>; the link came up at 17:45:11, two minutes later. The announcement was never sent
/// again, so both phones sat on "waiting for them to add you back" indefinitely, with a perfectly good
/// radio between them. A message would have survived this — it goes on a backlog and is flushed when a
/// link appears. The add had no such thing.
/// </para>
/// </summary>
public class ContactAnnounceRetryTests
{
    private const string Me = "TT2JV-17BDD";
    private const string Them = "NGCCR-XNBH6";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeRadioMesh Radio { get; } = new(Me);
        public ContactService Contacts { get; }

        public Rig() => Contacts = new ContactService(Store, new FakeIdentity(Me), Radio);

        public void Dispose() => Store.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> condition, int withinMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(withinMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    // ── An add made before there is a link still gets there ───────────────────

    /// <summary>The exact sequence two phones performed during first-run setup.</summary>
    [Fact]
    public async Task An_add_made_with_no_link_is_announced_when_one_appears()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, via: "typed");   // no link yet — nothing can go out
        var beforeLink = rig.Radio.Sent.Count;

        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Sent.Count > beforeLink),
            "the link came up and the add was never announced — both phones wait on each other forever");
    }

    [Fact]
    public async Task An_add_that_could_not_be_sent_is_announced_when_the_link_returns()
    {
        using var rig = new Rig();
        rig.Radio.Link();
        rig.Radio.CanSend = false;                          // linked, but the radio refuses
        await rig.Contacts.AddAsync(Them, via: "typed");

        rig.Radio.CanSend = true;
        rig.Radio.Unlink();
        rig.Radio.Link();
        var afterRelink = rig.Radio.Sent.Count;

        Assert.True(await Eventually(() => rig.Radio.Sent.Count >= afterRelink && rig.Radio.Sent.Count > 0));
    }

    /// <summary>
    /// A link drops and comes back constantly on a mesh. Each return is another chance to reach someone
    /// still waiting on us — not just the first one.
    /// </summary>
    [Fact]
    public async Task An_unanswered_add_is_announced_again_on_a_later_link()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, via: "typed");
        rig.Radio.Link();
        await Eventually(() => rig.Radio.Sent.Count > 0);
        rig.Radio.Unlink();

        var beforeSecond = rig.Radio.Sent.Count;
        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Sent.Count > beforeSecond),
            "the peer still has not added us back and we stopped telling them");
    }

    /// <summary>
    /// The link event and the radio's own "I am linked" flag do not flip at the same instant. On a P30
    /// Lite the announcement fired 3 ms before the flag turned true, so the send was refused — and with
    /// nothing scheduled after it, that phone never told its peer again. The peer sat on
    /// "waiting for them" while both radios worked perfectly.
    /// </summary>
    [Fact]
    public async Task An_announce_that_loses_the_race_with_the_link_is_tried_again()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, via: "typed");

        rig.Radio.CanSend = false;    // the transition fires while the radio still refuses
        rig.Radio.Link();
        await Task.Delay(120);
        rig.Radio.CanSend = true;     // the link settles — but nothing transitions again

        Assert.True(await Eventually(() => rig.Radio.Delivered.Count > 0, 6000),
            "the announcement lost the race and was never tried again");
    }

    // ── Once they answer, we stop ─────────────────────────────────────────────

    /// <summary>
    /// Once the announcement has actually gone out, stop. Repeating it on every link is pointless
    /// traffic on a radio with little to spare, and on a mesh it is a beacon nobody asked for.
    /// </summary>
    [Fact]
    public async Task A_settled_pair_is_left_alone()
    {
        using var rig = new Rig();
        rig.Radio.Link();
        await rig.Contacts.AddAsync(Them, via: "typed");             // goes out immediately
        await Eventually(() => rig.Radio.Delivered.Count > 0);
        rig.Store.UpsertContact(Them, null, byMe: true, byThem: true, "typed");   // they added us back
        var settled = rig.Radio.Delivered.Count;

        rig.Radio.Unlink();
        rig.Radio.Link();
        await Task.Delay(400);

        Assert.Equal(settled, rig.Radio.Delivered.Count);
    }

    /// <summary>
    /// Them adding us does <b>not</b> mean our announcement reached them — their packet may simply have
    /// crossed ours, or they added us from a QR code. Treating "mutual" as "told them" stranded a P30:
    /// its announcement lost a race with the link, merlin's add then arrived and made the contact
    /// mutual, and the retry concluded there was nothing left to say. merlin never heard from it.
    /// </summary>
    [Fact]
    public async Task Becoming_mutual_does_not_cancel_an_announcement_that_never_went_out()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, via: "typed");            // no link — never sent
        rig.Store.UpsertContact(Them, null, byMe: true, byThem: true, "typed");   // their add arrives

        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Delivered.Count > 0, 6000),
            "we went mutual and stopped telling them — they still do not know we added them");
    }

    [Fact]
    public async Task A_device_with_no_contacts_announces_nothing()
    {
        using var rig = new Rig();

        rig.Radio.Link();
        await Task.Delay(300);

        Assert.Empty(rig.Radio.Sent);
    }

    // ── The record itself ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_records_the_contact_even_when_it_cannot_announce()
    {
        using var rig = new Rig();

        var added = await rig.Contacts.AddAsync(Them, via: "typed");

        Assert.True(added);
        Assert.Contains(rig.Store.GetContacts(), c => c.Tag == Them && c.AddedByMe && !c.AddedByThem);
    }
}
