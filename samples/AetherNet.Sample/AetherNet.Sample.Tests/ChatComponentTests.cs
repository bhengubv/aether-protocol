// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Pages;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// bUnit component tests for the chat screen — the UI layer the service tests don't reach. Chat.razor is
/// rendered over the SAME converged stack the app runs (the reliable MessagingService, wired by
/// <see cref="ConvergedChat"/> over the fake radio + fake Signal), so the day-divider, the per-message
/// timestamps, and the send flow into the converged send path are exercised as rendered markup rather
/// than asserted on the service alone.
/// </summary>
public sealed class ChatComponentTests : IDisposable
{
    private const string Me = "KXJB7-MN2P4";
    private const string Peer = "DY5CF-84G9T";

    private readonly Bunit.TestContext _ctx = new();
    private readonly AetherStore _store = AetherStore.InMemory();
    private readonly FakeRadioMesh _radio = new(Me);

    public ChatComponentTests()
    {
        // Chat.razor's scroll helpers are best-effort JS the component wraps in try/catch; loose mode
        // lets those calls no-op instead of the test having to stub each one.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var me = new FakeIdentity(Me);
        var signal = new FakeSignalProtocol();
        var chat = ConvergedChat.Build(_store, me, signal, new FakePreKeyExchange(), _radio);

        // The nine services Chat.razor @injects — the real ones where they construct cheaply from the
        // test fakes, so the component runs against genuine code, not hollow stand-ins. NavigationManager
        // and IJSRuntime are provided by bUnit's TestContext.
        _ctx.Services.AddSingleton(chat);
        _ctx.Services.AddSingleton(new ContactService(_store, me, _radio));
        _ctx.Services.AddSingleton(new CallService(me, signal, new NullAudioIo()));
        _ctx.Services.AddSingleton(new GroupCallService(me, signal, new NullAudioIo()));
        _ctx.Services.AddSingleton<IRadioMesh>(_radio);
        _ctx.Services.AddSingleton<IMediaCapture>(new NullMediaCapture());
        _ctx.Services.AddSingleton<IFilePicker>(new NullFilePicker());
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _store.Dispose();
    }

    private void Seed(string body, bool mine, long sentMs, string state) =>
        _store.SaveMessage(new ChatMessage(Guid.NewGuid().ToString("N"), Peer, body, mine, state, sentMs));

    [Fact]
    public void The_thread_heads_each_day_with_a_divider_and_stamps_every_message()
    {
        var today = DateTimeOffset.Now;
        var yesterday = today.AddDays(-1);
        Seed("was yesterday", mine: true, yesterday.ToUnixTimeMilliseconds(), ChatMessage.Sent);
        Seed("is today", mine: true, today.ToUnixTimeMilliseconds(), ChatMessage.Sent);

        var cut = _ctx.RenderComponent<Chat>(p => p.Add(c => c.Peer, Peer));

        // A divider heads each day, read from this phone's own clock — "Today" and "Yesterday" relative
        // to now, so a bare time is never ambiguous about which day it belongs to.
        var dividers = cut.FindAll(".daysep span").Select(e => e.TextContent).ToArray();
        Assert.Contains("Yesterday", dividers);
        Assert.Contains("Today", dividers);

        // One HH:mm stamp per message.
        var stamps = cut.FindAll(".btime").Select(e => e.TextContent).ToArray();
        Assert.Equal(2, stamps.Length);
        Assert.All(stamps, s => Assert.Matches(@"^\d{2}:\d{2}$", s));

        Assert.Contains(cut.FindAll(".bub").Select(e => e.TextContent.Trim()), t => t == "is today");
    }

    [Fact]
    public void Typing_a_message_and_pressing_send_puts_it_in_the_thread()
    {
        var cut = _ctx.RenderComponent<Chat>(p => p.Add(c => c.Peer, Peer));

        // The send button only appears once something is typed (the composer swaps mic → send). The
        // composer binds on `oninput`, so drive that event, not `onchange`.
        cut.Find(".cv-input").Input("no tower no wifi");
        cut.Find(".cv-send").Click();

        // Send goes through the converged ChatService.SendAsync — stored immediately, shown at once, and
        // pending until a session exists (there is none in this fixture, which is the point: the UI still
        // records and displays it).
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_store.GetMessages(Peer), m => m.Mine && m.Body == "no tower no wifi");
            Assert.Contains(cut.FindAll(".bub.mine").Select(e => e.TextContent.Trim()), t => t == "no tower no wifi");
        });
    }
}
