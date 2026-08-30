// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Browser;

/// <summary>
/// A card — the mesh-web's unit of publishing, decided in <c>02_REMAINING_WORK</c> §2.
///
/// <para>
/// It is <b>a signed JSON blob, not HTML</b>, because two properties are non-negotiable for
/// decentralised hosting. It is rendered from a blob authored by a <b>stranger</b>, so it must be
/// <b>safe</b>. And the same blob must render on any device, offline, years later, with the author
/// long gone, so it must be <b>portable</b>. HTML surrenders both: it executes, it fetches, and it
/// depends on an engine that will have moved on.
/// </para>
///
/// <para>
/// So a card is a versioned document plus an ordered list of typed blocks, drawn by one renderer we
/// own — uniform, theme-aware, and <b>inert</b>. An old renderer meeting an unknown block skips it
/// rather than failing the card, so a newer author never breaks an older reader.
/// </para>
/// </summary>
public sealed class CardDocument
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("blocks")] public List<CardBlock> Blocks { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The media type a card is published under — never <c>text/html</c>.</summary>
    public const string ContentType = "application/vnd.aether.card+json";

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Read a card. Anything that is not one comes back null rather than being coerced — a renderer
    /// that guesses at malformed input is how a stranger's blob becomes a stranger's instructions.
    /// </summary>
    public static CardDocument? Parse(string json)
    {
        try
        {
            var card = JsonSerializer.Deserialize<CardDocument>(json, Options);
            return card is null || card.Blocks is null ? null : card;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One piece of a card. <see cref="Kind"/> says how to draw it; a renderer that does not know the
/// kind leaves it out and carries on.
/// </summary>
public sealed class CardBlock
{
    public const string Heading = "heading";
    public const string Text = "text";
    public const string List = "list";
    public const string KeyValue = "kv";
    public const string Link = "link";

    /// <summary>
    /// The page's name, as the reader sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A block rather than a property, so an author can put it where they want it and put things
    /// above it. It used to be hoisted to the top of every page along with the label, which meant a
    /// page could not open with a picture, or put its navigation over its name, or do any of the
    /// things a designed page does — the renderer was deciding the layout and calling it a document.
    /// </para>
    /// <para>
    /// <see cref="CardDocument.Title"/> stays as the page's name for lists, addresses and file names.
    /// A card with no title block still shows that name first, so nothing written before this
    /// suddenly loses its heading.
    /// </para>
    /// <para>
    /// <c>As</c> may be <c>small</c>, which sets it as a wordmark rather than a headline — the
    /// difference between a masthead and a signature, and a decision only the author can make.
    /// </para>
    /// </remarks>
    public const string Title = "title";

    /// <summary>
    /// A small letterspaced label above a title — a role, a place, a date.
    /// </summary>
    /// <remarks>
    /// Not a heading. A heading opens a section; an eyebrow qualifies the thing under it, and it is
    /// set small, wide and quiet rather than large and loud. Separating them is what stops every page
    /// having four things all shouting at the same size.
    /// </remarks>
    public const string Eyebrow = "eyebrow";

    /// <summary>
    /// A numbered index — a list of things, each with a name and where it belongs.
    /// </summary>
    /// <remarks>
    /// <see cref="Items"/> are <c>name = place</c>, the same shape a labelled fact uses, drawn as a
    /// plate index: an ordinal, the name, and the place aligned right, with a hairline between rows.
    /// It is the single most useful block for making a page look composed rather than typed — a
    /// catalogue, a menu, a set of works, a schedule are all this shape.
    /// </remarks>
    public const string Index = "index";

    /// <summary>A pulled quote, set larger than the prose around it.</summary>
    public const string Quote = "quote";

    /// <summary>A break between passages. Carries no text.</summary>
    public const string Rule = "rule";

    /// <summary>A picture, carried by content hash. <see cref="Value"/> is its caption.</summary>
    public const string Image = "image";

    /// <summary>
    /// A tip jar — Buy Me a Coffee, Ko-fi, a PayFast page. <see cref="Value"/> is the label a reader
    /// sees, <see cref="Target"/> the address it stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one block that names somewhere outside the mesh, and it exists because creators do not move
    /// platform for a philosophy — they move when the living they already earn moves with them. A page
    /// that cannot carry a tip jar is a page nobody who is paid for their work will publish.
    /// </para>
    /// <para>
    /// It is still never fetched and never followed on the reader's behalf. The address is shown, and
    /// going there is something the person holding the phone decides to do — which keeps the rule that
    /// matters: opening a stranger's card causes no outbound request of any kind.
    /// </para>
    /// </remarks>
    public const string Tip = "tip";

    /// <summary>
    /// The card's own accent colour, so a shop does not look like a taxi rank. A declared choice this
    /// renderer interprets — never a style sheet.
    /// </summary>
    public const string Theme = "theme";

    [JsonPropertyName("k")] public string Kind { get; set; } = Text;
    [JsonPropertyName("t")] public string? Value { get; set; }
    [JsonPropertyName("items")] public List<string>? Items { get; set; }

    /// <summary>
    /// An asset referenced by content-hash, never a URL. Nothing phones home: the bytes come from the
    /// mesh, which is what lets a card render years later with no network at all.
    /// </summary>
    [JsonPropertyName("hash")] public string? ContentHash { get; set; }

    /// <summary>
    /// Where a <see cref="Link"/> block points — an <c>aether://</c> address, never an <c>http</c> one.
    /// <para>
    /// A card that can reach the open web is a card that phones home the moment a stranger renders it,
    /// which is the whole thing the card model exists to prevent. Links stay inside the mesh.
    /// </para>
    /// </summary>
    [JsonPropertyName("to")] public string? Target { get; set; }

    /// <summary>
    /// How this block sits: <c>centre</c>, or left if it says nothing.
    /// </summary>
    /// <remarks>
    /// A named position rather than a style. A card that could set its own alignment in CSS could set
    /// anything, and the whole reason a card is typed JSON is that it cannot. One property, two
    /// values, and a renderer that has never heard of a third simply leaves the block where it was.
    /// </remarks>
    [JsonPropertyName("a")] public string? Align { get; set; }

    /// <summary>
    /// How a picture is shown: <c>wide</c> to run to the edges at its own shape.
    /// </summary>
    /// <remarks>
    /// The first picture on a card is its masthead and is cropped to a band, because a page needs a
    /// face. A picture that is the subject rather than the backdrop should not be cropped at all —
    /// somebody's drawing is not a header image.
    /// </remarks>
    [JsonPropertyName("as")] public string? As { get; set; }

    /// <summary>
    /// Whether this block is centred.
    /// </summary>
    /// <remarks>
    /// Not written, and neither are the three below. They are questions asked about <c>a</c> and
    /// <c>as</c>, not things a block stores — but they were being serialised into every block of
    /// every card, so a third of a document was answers to questions nobody asked, repeated once per
    /// block, over a radio. Worse for the one person this format exists to teach: somebody reading
    /// the JSON to learn how a card works was shown four fields that look settable and are not.
    /// </remarks>
    [JsonIgnore]
    public bool IsCentred => string.Equals(Align, "centre", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this picture runs to the edges at its own shape.</summary>
    [JsonIgnore]
    public bool IsWide => string.Equals(As, "wide", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this picture is the page's ground rather than something on it.
    /// </summary>
    /// <remarks>
    /// A wash: an illustration bleeding off the margins, behind everything, that the reader never
    /// scrolls past because it is the paper rather than a thing printed on it. It is what separates a
    /// page that was made from a page that was laid out, and there was no way to express it — a
    /// picture could only ever be a masthead or a figure in the flow.
    /// </remarks>
    [JsonIgnore]
    public bool IsWash => string.Equals(As, "wash", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is set as a wordmark rather than a headline.</summary>
    [JsonIgnore]
    public bool IsSmall => string.Equals(As, "small", StringComparison.OrdinalIgnoreCase);

    public static CardBlock Of(string kind, string value) => new() { Kind = kind, Value = value };

    /// <summary>
    /// Is this something we can fetch from the mesh, or is it an address?
    ///
    /// <para>
    /// A content hash names bytes; a URL names a place to go and get them. Only the first can be
    /// honoured. A card that could name <c>http</c>, <c>data:</c> or a protocol-relative address would
    /// reach outside the mesh the instant a stranger opened it — and would stop working the day that
    /// host disappeared, which is precisely what content-addressing exists to prevent.
    /// </para>
    /// </summary>
    public static bool IsUsableAssetHash(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) &&
        hash.All(c => char.IsAsciiLetterOrDigit(c));

    /// <summary>
    /// Is this an accent colour we are willing to apply?
    ///
    /// <para>
    /// A plain hex colour and nothing else. The accent is written into a style, so anything richer is a
    /// way for a stranger's card to inject CSS and restyle the app around itself.
    /// </para>
    /// </summary>
    public static bool IsUsableAccent(string? colour) =>
        !string.IsNullOrWhiteSpace(colour) &&
        colour.Length is 4 or 7 &&
        colour[0] == '#' &&
        colour.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>The longest a tip address may be.</summary>
    /// <summary>The longest address a card may carry, in a tip jar or inside a sentence.</summary>
    public const int LongestTip = 200;

    /// <summary>
    /// Is this a tip address we are willing to put in front of a reader?
    ///
    /// <para>
    /// <b>https only.</b> This is the one place a card names money, and an <c>http</c> jar is somebody
    /// on the same network rewriting where the money goes.
    /// </para>
    ///
    /// <para>
    /// <b>No credentials in the authority.</b> <c>https://buymeacoffee.com@example.invalid/x</c> reads
    /// as the real thing to a person and resolves to something else entirely — the oldest way to make
    /// a familiar name point at a stranger's wallet.
    /// </para>
    ///
    /// <para>
    /// No allow-list of providers, deliberately. Deciding which tip jars are permitted is a central
    /// authority over who may be paid, which is the thing this network exists to not be.
    /// </para>
    /// </summary>
    public static bool IsUsableTip(string? address) => IsUsableWeb(address);

    /// <summary>
    /// An address on the open web that a reader may safely be offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Https and nothing else. Not because http is rare, but because every other scheme a written
    /// address could carry is a way to make the reader's browser do something on the author's behalf —
    /// <c>javascript:</c> most obviously, but <c>data:</c>, <c>file:</c> and whatever the browser
    /// happens to have registered are all worse than useless here. An address that does not pass this
    /// is drawn as words with nothing to click.
    /// </para>
    /// <para>
    /// A userinfo <c>@</c> is refused as well: it is the oldest way of making an address read as one
    /// host while going to another, and no honest link on a card needs one.
    /// </para>
    /// </remarks>
    public static bool IsUsableWeb(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (address.Length > LongestTip) return false;

        const string scheme = "https://";
        if (!address.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = address[scheme.Length..];
        if (rest.Length == 0) return false;

        var cut = rest.IndexOfAny(['/', '?', '#']);
        var authority = cut < 0 ? rest : rest[..cut];

        if (authority.Contains('@')) return false;
        if (!authority.Contains('.')) return false;
        if (authority.StartsWith('.') || authority.EndsWith('.')) return false;

        return address.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
    }

    /// <summary>Whether this is an address on the mesh rather than on the web.</summary>
    /// <remarks>
    /// Checked here, checked again in the script inside the page, and checked a third time by the
    /// host before it acts. A card asks to be followed; it never follows anything itself.
    /// </remarks>
    public static bool IsMeshAddress(string? target) =>
        target is { Length: > 0 and < 512 } &&
        target.StartsWith("aether://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The host a tip address points at, as a reader should see it.</summary>
    /// <remarks>
    /// Shown beside the label so the decision is made on where the money actually goes rather than on
    /// what the author chose to call it.
    /// </remarks>
    public static string TipHost(string? address)
    {
        if (!IsUsableTip(address)) return "";

        var rest = address!["https://".Length..];
        var cut = rest.IndexOfAny(['/', '?', '#']);
        return cut < 0 ? rest : rest[..cut];
    }
}
