// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A page that is up stays up until somebody changes it.
///
/// <para>
/// The editor promises this in so many words — "the address stays put now, so anyone who wrote it
/// down still finds you" — and it was not true. Saving cleared the published flag unconditionally,
/// and the editor saves on every keystroke, so opening a published page and touching nothing at all
/// took it off the air. It was found on a phone, by noticing a page had gone from the bookmark row.
/// </para>
///
/// <para>
/// Nothing about it looked like a failure: the page was still there, still correct, still editable.
/// It had simply stopped answering at its address, which is the one thing a reader would notice and
/// the author never would.
/// </para>
/// </summary>
public class PublishedStaysUpTests
{
    private static MyPages APhone() => new(new InMemoryCardStore());

    private static WebCard Page(string words = "We come out on a Sunday.") => new()
    {
        Name = "shop",
        Doc = new CardDocument
        {
            Title = "Kagiso Plumbing",
            Blocks = [CardBlock.Of(CardBlock.Text, words)],
        },
    };

    private static MyPages Published()
    {
        var mine = APhone();
        mine.Save(Page());
        mine.WentLive("shop", mine.NextVersion("shop"));
        return mine;
    }

    [Fact]
    public void Publishing_puts_a_page_up()
    {
        Assert.True(Published().Get("shop")!.Live);
    }

    /// <summary>
    /// Opening it and changing nothing leaves it up.
    /// </summary>
    /// <remarks>
    /// This is the case that was broken, and it is the commonest thing anybody does: open a page to
    /// look at it, tap through, close it. Every one of those taps is a save.
    /// </remarks>
    [Fact]
    public void Saving_without_changing_anything_leaves_it_up()
    {
        var mine = Published();

        // What the editor does on every keystroke, and on every step of the wizard.
        for (var i = 0; i < 5; i++) mine.Save(mine.Get("shop")!);

        Assert.True(mine.Get("shop")!.Live, "a page went off the air without being edited");
    }

    /// <summary>Editing it takes it down, because what is standing is no longer what you have.</summary>
    [Fact]
    public void Editing_it_takes_it_down()
    {
        var mine = Published();
        var page = mine.Get("shop")!;

        page.Doc.Blocks.Add(CardBlock.Of(CardBlock.Text, "Open on Sundays too."));
        mine.Save(page);

        Assert.False(mine.Get("shop")!.Live);
    }

    /// <summary>And publishing again puts it back.</summary>
    [Fact]
    public void Publishing_again_puts_it_back_up()
    {
        var mine = Published();
        var page = mine.Get("shop")!;

        page.Doc.Blocks.Add(CardBlock.Of(CardBlock.Text, "Open on Sundays too."));
        mine.Save(page);
        mine.WentLive("shop", mine.NextVersion("shop"));

        Assert.True(mine.Get("shop")!.Live);

        // And it stays up through the next round of saves.
        mine.Save(mine.Get("shop")!);
        Assert.True(mine.Get("shop")!.Live);
    }

    /// <summary>
    /// It is still up after the app is closed and opened again.
    /// </summary>
    /// <remarks>
    /// The same bug one launch later: with nothing remembered about what was published, the first
    /// save after a restart looks like an edit and takes the page down again.
    /// </remarks>
    [Fact]
    public void It_is_still_up_after_a_restart()
    {
        var store = new InMemoryCardStore();

        var before = new MyPages(store);
        before.Save(Page());
        before.WentLive("shop", before.NextVersion("shop"));

        var after = new MyPages(store);
        Assert.True(after.Get("shop")!.Live, "it did not survive the restart");

        after.Save(after.Get("shop")!);
        Assert.True(after.Get("shop")!.Live, "the first save after a restart took it off the air");
    }

    /// <summary>A page that was never published does not become published by being saved.</summary>
    [Fact]
    public void A_draft_stays_a_draft()
    {
        var mine = APhone();

        mine.Save(Page());
        mine.Save(mine.Get("shop")!);

        Assert.False(mine.Get("shop")!.Live);
    }
}
