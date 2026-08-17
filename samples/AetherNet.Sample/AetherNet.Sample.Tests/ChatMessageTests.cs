// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using Xunit;

namespace AetherNet.Sample.Tests;

public class ChatMessageTests
{
    // ── Delivery vocabulary ───────────────────────────────────────────────────
    //
    // A tick meaning "handed to the radio" is a lie the user acts on — they believe
    // the message arrived. These pin the words so they cannot drift back.

    [Fact]
    public void Sent_is_not_delivered() =>
        Assert.NotEqual(ChatMessage.Sent, ChatMessage.Delivered);

    [Fact]
    public void Failed_is_not_pending() =>
        Assert.NotEqual(ChatMessage.Failed, ChatMessage.Pending);

    [Fact]
    public void Failed_is_not_sent() =>
        Assert.NotEqual(ChatMessage.Failed, ChatMessage.Sent);

    [Fact]
    public void States_are_all_distinct()
    {
        string[] states =
        [
            ChatMessage.Pending, ChatMessage.Sent, ChatMessage.Delivered,
            ChatMessage.Failed, ChatMessage.Received,
        ];

        Assert.Equal(states.Length, states.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Authorship ────────────────────────────────────────────────────────────

    [Fact]
    public void SenderTag_defaults_to_unset_for_a_one_to_one_message()
    {
        // In a one-to-one chat the author is implied by the conversation; only a group
        // needs it stated, which is why it is optional rather than required.
        var message = new ChatMessage("id", "DY5CF-84G9T", "hello", Mine: true,
            ChatMessage.Pending, SentMs: 0);

        Assert.Null(message.SenderTag);
    }

    [Fact]
    public void SenderTag_is_kept_for_a_group_message()
    {
        var message = new ChatMessage("id", "G0123456789AB", "hello", Mine: false,
            ChatMessage.Received, SentMs: 0, SenderTag: "BH8CZ-B09CA");

        Assert.Equal("BH8CZ-B09CA", message.SenderTag);
    }
}
