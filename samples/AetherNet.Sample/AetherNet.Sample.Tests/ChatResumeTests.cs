// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Picking a conversation back up when the radio link returns.
///
/// <para>
/// A mesh link is not a phone call — it drops and is rebuilt constantly, and on Bluetooth between two
/// handsets that can be every half minute. Whatever was in flight dies with each one. So the moment a
/// link comes back, everything still undelivered has to go again, or a message the person watched fail
/// stays failed for as long as they keep looking at it.
/// </para>
///
/// <para>
/// The catch is who to resume <i>with</i>. The radio can only name its peer by the rotating wire
/// address it saw in the handshake — the long-term identity deliberately does not travel in clear, and
/// only arrives inside the session. Nothing is filed under a wire address, so resuming with one flushes
/// an empty conversation and the backlog sits there.
/// </para>
///
/// <para>
/// Watched on hardware 2026-08-13: two phones rebuilt their link every thirty seconds and neither
/// re-sent a thing, because each was resuming a conversation with an address rather than a person.
/// </para>
/// </summary>
public class ChatResumeTests
{
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    /// <summary>What a radio actually reports after a handshake: 16 base-32 characters, not a tag.</summary>
    private const string WireAddressOfThem = "DB0T7HAYDGA7EECZ";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeSignalProtocol Signal { get; } = new();
        public FakePreKeyExchange PreKeys { get; } = new();
        public FakeRadioMesh Radio { get; } = new(Me);
        public ChatService Chat { get; }

        public Rig()
        {
            Chat = new ChatService(Store, new FakeIdentity(Me), Signal, PreKeys, Radio);
        }

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

    /// <summary>A message that went out and was never confirmed, exactly as the store holds it.</summary>
    private static void GiveUpOn(Rig rig, string body) =>
        rig.Store.SaveMessage(new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: Them,
            Body: body,
            Mine: true,
            State: ChatMessage.Failed,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    // ── The backlog goes again ────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_message_is_sent_again_when_the_link_returns()
    {
        using var rig = new Rig();
        GiveUpOn(rig, "did you get this");
        rig.Signal.OpenSessionWith(Them);

        rig.Radio.PeerLabel = WireAddressOfThem;   // the radio only knows a wire address
        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Sent.Count > 0),
            "the link came back and nothing was re-sent — the backlog is stranded");
    }

    [Fact]
    public async Task A_failed_message_stops_showing_as_failed_once_it_goes_again()
    {
        using var rig = new Rig();
        GiveUpOn(rig, "did you get this");
        rig.Signal.OpenSessionWith(Them);

        rig.Radio.PeerLabel = WireAddressOfThem;
        rig.Radio.Link();

        Assert.True(await Eventually(() =>
            rig.Store.GetMessages(Them).Single().State != ChatMessage.Failed));
    }

    [Fact]
    public async Task A_pending_message_is_sent_when_the_link_returns()
    {
        using var rig = new Rig();
        await rig.Chat.SendAsync(Them, "waiting on a link");   // no link yet ⇒ pending
        rig.Signal.OpenSessionWith(Them);

        rig.Radio.PeerLabel = WireAddressOfThem;
        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Sent.Count > 0));
    }

    /// <summary>
    /// The link on a mesh comes and goes constantly, and it is the same peer each time. Resuming only
    /// when the peer <i>changes</i> means a conversation is picked up once and never again.
    /// </summary>
    [Fact]
    public async Task A_backlog_goes_again_on_every_rebuild_not_just_the_first()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.PeerLabel = WireAddressOfThem;

        rig.Radio.Link();
        await Eventually(() => rig.Radio.Sent.Count > 0);
        rig.Radio.Unlink();

        GiveUpOn(rig, "second time around");
        var before = rig.Radio.Sent.Count;
        rig.Radio.Link();

        Assert.True(await Eventually(() => rig.Radio.Sent.Count > before),
            "the same peer linked again and the new backlog was not sent");
    }

    // ── Still nothing without a session ───────────────────────────────────────

    /// <summary>
    /// Resuming must not become a way round the rule that nothing readable leaves this phone. A link
    /// with no session behind it carries the handshake and nothing else.
    /// </summary>
    [Fact]
    public async Task A_failed_message_is_not_sent_in_clear_when_there_is_no_session()
    {
        using var rig = new Rig();
        GiveUpOn(rig, "did you get this");

        rig.Radio.PeerLabel = WireAddressOfThem;
        rig.Radio.Link();
        await Task.Delay(200);

        Assert.Equal(ChatMessage.Failed, rig.Store.GetMessages(Them).Single().State);
    }
}
