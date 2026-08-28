// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// What kind of page somebody is about to make.
///
/// <para>
/// <b>A template is a shape, never a claim.</b> It arrives with the right blocks in the right order and
/// with the headings already written — "Hours", "What I do", "Find me" — because those are true of the
/// page regardless of who is writing it. Every value a reader would take as a fact is left empty.
/// </para>
///
/// <para>
/// That line matters more than it looks. A template that pre-fills "Open 06:00 to 20:00" produces a
/// page that lies until somebody edits it, and most people will not edit it — so the first thing this
/// network would be known for is shops with invented opening hours. An empty value renders as nothing
/// at all, which means a half-finished page is simply a shorter page rather than a wrong one.
/// </para>
///
/// <para>
/// The point of offering these at all is attention: somebody starting from a blank page has to decide
/// what a page even is before they can begin, and most people stop there. Starting from a shape turns
/// authoring into filling in, which is the difference between a network of pages and a network of
/// people who meant to make one.
/// </para>
/// </summary>
public sealed record PageTemplate(
    string Key,
    string Name,
    string Blurb,
    string Suggests,
    string Look)
{
    /// <summary>The kinds offered, in the order the wizard shows them.</summary>
    public static readonly PageTemplate[] All =
    [
        new("me", "About me",
            "Who you are, what you do, how to reach you. Your front door.",
            Suggests: MyPages.Home, Look: "editorial"),

        new("business", "A business",
            "Trading name, hours, what you sell, what it costs.",
            Suggests: "shop", Look: "studio"),

        new("links", "My links",
            "Everywhere else you exist, and a tip jar. Yours, not a middleman's.",
            Suggests: "mylinks", Look: "night"),

        new("notice", "A notice",
            "Something the street needs to know. Times, changes, warnings.",
            Suggests: "notice", Look: "plain"),

        new("example", "Show me a finished one",
            "A page using everything, set properly. Edit it into your own.",
            Suggests: "example", Look: "editorial"),

        new("blank", "Blank",
            "Nothing at all. Build it yourself.",
            Suggests: "page", Look: "plain"),
    ];

    /// <summary>The template with this key, or the first one.</summary>
    public static PageTemplate Of(string? key)
    {
        var wanted = key?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(t => t.Key == wanted) ?? All[0];
    }

    /// <summary>
    /// Build the starting document.
    /// </summary>
    /// <param name="owner">
    /// The author's name, used only as a title where a title is genuinely theirs — never invented into
    /// the body of the page.
    /// </param>
    public CardDocument Build(string? owner)
    {
        var name = MyName.Clean(owner);
        var card = new CardDocument
        {
            Title = Key switch
            {
                "me" or "links" => name,
                "example" => "A card on AetherNet",
                _ => "",
            },
            Blocks = [CardBlock.Of(CardBlock.Theme, Look), .. Shape()],
        };

        return OwnCard.Tidy(card);
    }

    /// <summary>The blocks this kind of page is made of — headings written, values empty.</summary>
    private IEnumerable<CardBlock> Shape() => Key switch
    {
        "me" =>
        [
            CardBlock.Of(CardBlock.Eyebrow, ""),
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "What I do"),
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.KeyValue, "Where ="),
            CardBlock.Of(CardBlock.KeyValue, "Reach me ="),
        ],

        "business" =>
        [
            CardBlock.Of(CardBlock.Eyebrow, ""),
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "Hours"),
            CardBlock.Of(CardBlock.KeyValue, "Open ="),
            CardBlock.Of(CardBlock.KeyValue, "Closed ="),
            // A price list is an index: a name, and what it costs, set as a plate. It is the block
            // that makes a trade's page look composed instead of typed.
            CardBlock.Of(CardBlock.Heading, "What we do"),
            new CardBlock { Kind = CardBlock.Index, Items = ["", "", ""] },
            CardBlock.Of(CardBlock.KeyValue, "Call ="),
        ],

        "links" =>
        [
            CardBlock.Of(CardBlock.Eyebrow, ""),
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "Find me"),
            new CardBlock { Kind = CardBlock.Index, Items = ["", "", ""] },
            new CardBlock { Kind = CardBlock.Tip, Value = "Buy me a coffee", Target = "" },
        ],

        "notice" =>
        [
            CardBlock.Of(CardBlock.Text, ""),
            new CardBlock { Kind = CardBlock.List, Items = ["", ""] },
            CardBlock.Of(CardBlock.KeyValue, "Until ="),
        ],

        // The one template that arrives filled in — and the only one that may, because it says so on
        // the button. Everything else supplies a shape and leaves every claim blank; this exists
        // because seeing a finished page teaches what the blocks are for far faster than a blank one
        // with good labels ever will. It is about this network rather than about a person or a
        // business, so somebody who publishes it unedited has published something true.
        "example" =>
        [
            CardBlock.Of(CardBlock.Eyebrow, "A page with no server, no URL and no owner but you"),

            CardBlock.Of(CardBlock.Text,
                "This page is a card. It lives on the phone that wrote it, it opens with no signal, " +
                "and anybody who has been handed it can hand it on again."),

            CardBlock.Of(CardBlock.Text,
                "Every part of it below is a block you can add, move or delete. Change the words, " +
                "pick a different look and background, and it becomes yours."),

            CardBlock.Of(CardBlock.Quote, "Nobody issued your address, so nobody can withdraw it."),

            CardBlock.Of(CardBlock.Heading, "What a card can hold"),

            new CardBlock
            {
                Kind = CardBlock.Index,
                Items =
                [
                    "Label = above the title",
                    "Words = a measure, not a column",
                    "Quote = set apart",
                    "Index = this, numbered for you",
                    "Detail = a fact, aligned right",
                    "Picture = shrunk before it travels",
                    "Tip jar = paid to you, not through us",
                ],
            },

            new CardBlock { Kind = CardBlock.Rule },

            CardBlock.Of(CardBlock.Heading, "What it costs to carry"),
            CardBlock.Of(CardBlock.KeyValue, "This whole page = about 2 KB"),
            CardBlock.Of(CardBlock.KeyValue, "Over Bluetooth = a second or two"),
            CardBlock.Of(CardBlock.KeyValue, "Kept once opened = forever"),
        ],

        _ => [CardBlock.Of(CardBlock.Text, "")],
    };
}
