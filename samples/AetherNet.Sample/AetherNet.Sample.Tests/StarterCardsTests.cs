// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The card a phone hosts before anybody has written a word.
///
/// <para>
/// It used to be the "me" template, which supplies headings and the labels of facts and deliberately
/// answers none of them — and that rule is right: a template that answers for you is a template most
/// people publish unedited, which
/// <see cref="WebCardAuthoringTests.A_template_states_no_fact_on_the_authors_behalf"/> exists to stop.
/// </para>
///
/// <para>
/// The cost of it was that the one card every device hosts by default rendered as a title, one label
/// and a lot of white — sitting one tap from a finished card in the same list. It read as broken
/// rather than as new. So the default is now "example", the one template allowed to arrive filled in
/// because everything on it is true about the network rather than invented about a person.
/// </para>
/// </summary>
public class StarterCardsTests
{
    /// <summary>The templates stay honest — this is the rule that must not be traded away.</summary>
    [Theory]
    [InlineData("me")]
    [InlineData("business")]
    public void A_shape_template_still_answers_nothing(string key)
    {
        foreach (var row in PageTemplate.Of(key).Build("Thabang").Blocks
                     .Where(b => b.Kind == CardBlock.KeyValue))
        {
            var said = row.Value!.Split('=', 2);
            Assert.True(said.Length == 2 && said[1].Trim().Length == 0,
                $"{key} answers '{row.Value}' on somebody's behalf");
        }
    }

    /// <summary>And the card that ships is not one of them.</summary>
    [Fact]
    public void The_card_a_phone_hosts_by_default_is_finished()
    {
        var card = PageTemplate.Of("example").Build("Y6TK9-EW9KK");

        var writing = card.Blocks
            .Where(b => b.Kind is CardBlock.Text or CardBlock.Heading or CardBlock.Eyebrow
                              or CardBlock.KeyValue or CardBlock.Index)
            .ToList();

        Assert.NotEmpty(writing);
        Assert.DoesNotContain(writing, b =>
            string.IsNullOrWhiteSpace(b.Value) && (b.Items is null || b.Items.Count == 0));
    }

    /// <summary>
    /// It uses the vocabulary rather than a corner of it.
    /// </summary>
    /// <remarks>
    /// The sketchbook is assembled entirely through <c>OwnCard.Add</c> — the editor's own call — so
    /// whether the editor CAN compose a page like that was never the question. Whether what ships
    /// bothers to is. A first card made only of paragraphs teaches that a card is a document.
    /// </remarks>
    [Fact]
    public void The_default_card_shows_more_than_paragraphs()
    {
        var kinds = PageTemplate.Of("example").Build("Y6TK9-EW9KK").Blocks
            .Select(b => b.Kind).Distinct().ToList();

        Assert.Contains(CardBlock.Eyebrow, kinds);
        Assert.Contains(CardBlock.Quote, kinds);
        Assert.Contains(CardBlock.Index, kinds);
        Assert.Contains(CardBlock.Rule, kinds);
        Assert.Contains(CardBlock.KeyValue, kinds);
    }

    /// <summary>Written out so it can be looked at, the same way the sketchbook is.</summary>
    [Fact]
    public void The_default_card_is_written_out_for_comparison()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null && !Directory.Exists(Path.Combine(here.FullName, ".fixtures")))
            here = here.Parent;
        if (here is null) return;

        var card = PageTemplate.Of("example").Build("Y6TK9-EW9KK");
        OwnCard.SetTitle(card, "Y6TK9-EW9KK");

        File.WriteAllText(
            Path.Combine(here.FullName, ".fixtures", "starter-default.html"),
            CardExport.Standalone(card, _ => null, at: "aether://Y6TK9-EW9KK/me"));
    }
}
