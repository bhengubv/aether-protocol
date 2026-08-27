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
            Title = Key is "me" or "links" ? name : "",
            Blocks = [CardBlock.Of(CardBlock.Theme, Look), .. Shape()],
        };

        return OwnCard.Tidy(card);
    }

    /// <summary>The blocks this kind of page is made of — headings written, values empty.</summary>
    private IEnumerable<CardBlock> Shape() => Key switch
    {
        "me" =>
        [
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "What I do"),
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.KeyValue, "Where ="),
            CardBlock.Of(CardBlock.KeyValue, "Reach me ="),
        ],

        "business" =>
        [
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "Hours"),
            CardBlock.Of(CardBlock.KeyValue, "Open ="),
            CardBlock.Of(CardBlock.KeyValue, "Closed ="),
            CardBlock.Of(CardBlock.Heading, "What we do"),
            new CardBlock { Kind = CardBlock.List, Items = ["", "", ""] },
            CardBlock.Of(CardBlock.KeyValue, "Call ="),
        ],

        "links" =>
        [
            CardBlock.Of(CardBlock.Text, ""),
            CardBlock.Of(CardBlock.Heading, "Find me"),
            new CardBlock { Kind = CardBlock.List, Items = ["", "", ""] },
            new CardBlock { Kind = CardBlock.Tip, Value = "Buy me a coffee", Target = "" },
        ],

        "notice" =>
        [
            CardBlock.Of(CardBlock.Text, ""),
            new CardBlock { Kind = CardBlock.List, Items = ["", ""] },
            CardBlock.Of(CardBlock.KeyValue, "Until ="),
        ],

        _ => [CardBlock.Of(CardBlock.Text, "")],
    };
}
