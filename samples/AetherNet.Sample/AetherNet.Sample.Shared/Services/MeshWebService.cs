// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Cards;
using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The AetherNet <b>mesh-web</b>: web pages that have no URL and no server.
///
/// Each device hosts its own small site — a distinct <see cref="Persona"/> chosen from its AetherTag,
/// so two phones show visibly different pages. Each page is a content-addressed, Ed25519-signed
/// <see cref="Card"/> at an <c>aether://&lt;tag&gt;/&lt;name&gt;</c> address. Opening a peer's address
/// resolves + verifies + fetches the card over the radio; the bytes are then <b>cached locally</b>
/// (content-addressed), so the same card re-opens instantly and offline even after the peer is gone —
/// it's a saved card now. Same <c>AetherNet.Cards</c> + <c>AetherNet.Content</c> stack the SDKs port.
/// </summary>
public sealed class MeshWebService
{
    private static readonly byte[] HelloMarker = Encoding.UTF8.GetBytes("MWHELLO");

    private readonly ILoggerFactory _loggerFactory;
    private readonly IRadioMesh? _radio;
    private readonly IIdentityService _me;
    private readonly AetherNet.Identity.INodeIdentity _node;
    private readonly IContentStore _contentStore;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly List<string> _pages = new();
    private readonly List<SavedCard> _saved = new();
    private volatile bool _ready;

    private IMeshSender _sender = default!;
    private IContentService _content = default!;
    private IContentStore _store = default!;
    private IAetherResolver _resolver = default!;
    private IDirectoryService _directory = default!;
    private string _localTag = "";
    private Persona _persona = Personas[0];

    private string? _peerSite;
    private bool _wasLinked;
    private bool _helloSent;

    private readonly Dictionary<string, string> _assets = new(StringComparer.Ordinal);
    private ContentDescriptor? _art;

    public MeshWebService(
        IIdentityService me,
        AetherNet.Identity.INodeIdentity node,
        IContentStore contentStore,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        // Cards are signed by the device, so the node signs them. This service never sees the key.
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _radio = radio;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public event Action? Changed;

    public string LocalTag => _localTag;
    public IReadOnlyList<string> Pages => _pages;
    public string HomeAddress => Address("home");
    public string Address(string name) => $"aether://{_localTag}/{name}";

    public bool RadioAvailable => _radio is { IsSupported: true };
    public string RadioName => _radio?.SelectedRadio ?? "radio";
    public bool RadioLinked => _radio?.IsLinked ?? false;
    public string? PeerSiteAddress => _peerSite;
    public void LinkRadio() => _radio?.Link();

    /// <summary>Peer cards fetched over the radio and kept on this phone — browsable offline.</summary>
    public IReadOnlyList<SavedCard> SavedCards => _saved;

    // ─── Setup ──────────────────────────────────────────────────────────────────

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_ready)
            return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
                return;

            // One identity for the whole app: the card is signed by the same tag Settings shows, and it
            // is the same tag after a restart — so an address someone saved still resolves to you.
            _localTag = _me.AetherTag;
            _persona = PickPersona(_localTag);

            if (_radio is { IsSupported: true })
            {
                _sender = new RadioMeshSender(_localTag, _radio);
                _radio.PacketReceived += OnInboundPacket;
                _radio.Changed += OnRadioChanged;
            }
            else
            {
                var uhid = "aether:web:" + _localTag;
                var transport = new InProcessTransportService(
                    uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
                transport.DataReceived += (_source, bytes) => OnInboundPacket(bytes);
                _sender = new InProcessMeshSender(uhid, transport);
            }

            // Durable: cards this device hosts, and cards it collected from others, survive a restart.
            _store = _contentStore;
            var routing = new RoutingService(_sender);
            _content = new ContentService(_sender, routing, _store);
            _directory = new DirectoryService(_sender, new Ed25519NameBindingVerifier());
            var cards = new CardService(_content, _directory);
            _resolver = new AetherResolver(cards);

            // The persona's artwork goes on the mesh first, as content in its own right — the card then
            // names it by hash. Publishing it separately is what lets a third phone that only ever met
            // a card-holder still render the picture: the bytes are addressed, not located.
            _art = await _content
                .PublishAsync($"{_persona.Key}-art", Encoding.UTF8.GetBytes(PersonaArt(_persona.Key, _localTag)),
                    "image/svg+xml", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var artHash = _art.RootHash;

            // Publish this device's persona: each page content-addressed, signed under this tag. Links
            // between a persona's own pages are written with a placeholder because the tag is not known
            // when the personas are declared — it is resolved here, to this device's real tag, so a
            // card can only ever point at its own author. The artwork hash is filled in the same way.
            foreach (var (name, document) in _persona.Pages)
            {
                var json = document.ToJson()
                    .Replace(PageAuthorToken, _localTag, StringComparison.Ordinal)
                    .Replace(PageArtToken, artHash, StringComparison.Ordinal);

                await cards.PublishCardAsync(
                        name, Encoding.UTF8.GetBytes(json), CardDocument.ContentType,
                        _node, version: 1, cancellationToken)
                    .ConfigureAwait(false);
                _pages.Add(name);
            }

            _ready = true;
        }
        finally
        {
            _initGate.Release();
        }

        // Greet whoever is already there.
        //
        // The link usually comes up while you are somewhere else in the app, and you reach the
        // mesh-web afterwards. A greeting sent only when the link *changes* is therefore never sent
        // at all — by either phone, each waiting on a transition that happened before it was
        // listening — and neither ever learns there is a site an arm's length away.
        if (_radio is { IsLinked: true } && !_helloSent)
        {
            _helloSent = true;
            _wasLinked = true;
            _ = SendHelloBurstAsync();
        }
    }

    // ─── Inbound wire ────────────────────────────────────────────────────────────

    private void OnInboundPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        switch (packet.Type)
        {
            case PacketType.NamePublish:
            case PacketType.NameQuery:
                _ = _directory.HandleAsync(packet);
                break;
            case PacketType.TorrentMetadata:
            case PacketType.ChunkBitmap:
            case PacketType.ChunkRequest:
            case PacketType.ChunkData:
                _ = _content.HandleAsync(packet);
                break;
            case PacketType.Data when IsHello(packet.Payload):
                OnPeerHello(packet.SourceUhid);
                break;
        }
    }

    private static bool IsHello(byte[]? payload) =>
        payload is not null && payload.AsSpan().SequenceEqual(HelloMarker);

    private void OnPeerHello(string? peerTag)
    {
        if (string.IsNullOrEmpty(peerTag) || peerTag == _localTag)
            return;

        var address = $"aether://{peerTag}/home";
        var changed = _peerSite != address;
        _peerSite = address;

        // Answer so the phone that greeted first also learns our address (both directions).
        if (!_helloSent)
        {
            _helloSent = true;
            _ = SendHelloBurstAsync();
        }

        if (changed)
            RaiseChanged();
    }

    private void OnRadioChanged()
    {
        var linked = _radio?.IsLinked ?? false;
        if (linked && !_wasLinked)
        {
            _wasLinked = true;
            if (!_helloSent)
            {
                _helloSent = true;
                _ = SendHelloBurstAsync();
            }
        }
        else if (!linked && _wasLinked)
        {
            _wasLinked = false;
            _helloSent = false;
            _peerSite = null;
        }
        RaiseChanged();
    }

    // Send the beacon a few times — BLE notifies/writes can drop one, and both phones need it to
    // discover each other, so the exchange is reliably bidirectional.
    //
    // The artwork's descriptor rides along, because a picture is separate content from the card that
    // names it: the card carries its own descriptor, the art does not. Without the descriptor a peer
    // has nothing to verify an arriving chunk against, so it cannot even ask — announcing here is what
    // lets a phone that has only just met us draw our card in full rather than as text.
    private async Task SendHelloBurstAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await SendHelloAsync().ConfigureAwait(false);
            if (i is 0 or 3 && _art is { } art)
                await _content.AnnounceAsync(art).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private Task<bool> SendHelloAsync()
    {
        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = _localTag,        // the peer learns our mesh-web tag from here
            DestinationUhid = string.Empty,
            Ttl = 1,
            Payload = HelloMarker,
        };
        return _sender.SendAsync(packet, string.Empty);
    }

    // ─── Browse ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open an <c>aether://</c> address: resolve the signed card, fetch its content by hash — from the
    /// local cache if we already hold it (offline), or pulled over the radio from the phone that does —
    /// verify, keep a copy, and hand back the page.
    /// </summary>
    public async Task<MeshPage> OpenAsync(string address, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(address))
            return MeshPage.Fail(address, "type an aether:// address");

        AetherResolution resolution;
        try
        {
            resolution = await _resolver
                .ResolveAsync(address.Trim(), TimeSpan.FromSeconds(6), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return MeshPage.Fail(address, ex.Message);
        }

        switch (resolution)
        {
            case AetherResolution.CardResolved resolved:
            {
                var card = resolved.Card;
                var authorTag = AetherNetTag.FromPublicKey(card.AuthorPublicKey).Value;
                var own = authorTag == _localTag;

                var remote = false;
                var bytes = await _content
                    .AssembleAsync(card.Descriptor.RootHash, cancellationToken)
                    .ConfigureAwait(false);

                if (bytes is null)
                {
                    // Not held here — pull the chunks over the mesh. Filing the descriptor first means the
                    // arriving chunks verify against it instead of being dropped. Once assembled they stay
                    // in the local store, so the next open of this address is served locally (offline).
                    remote = true;
                    await _store.SaveDescriptorAsync(card.Descriptor, cancellationToken).ConfigureAwait(false);
                    await _content
                        .RequestChunksAsync(card.Descriptor.RootHash, Array.Empty<int>(), null, cancellationToken)
                        .ConfigureAwait(false);
                    bytes = await WaitForContentAsync(card.Descriptor.RootHash, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (bytes is null)
                    return MeshPage.Fail(address, "content didn't arrive over the mesh");

                                var document = CardDocument.Parse(Encoding.UTF8.GetString(bytes));
                if (document is null)
                    return MeshPage.Fail(address, "that is not a card this renderer can draw");
                if (!own)
                    Remember(address, authorTag, document);

                return new MeshPage(
                    Ok: true, Address: address, Name: card.Name, Card: document, AuthorTag: authorTag,
                    RootHash: card.Descriptor.RootHash, Bytes: bytes.LongLength,
                    Chunks: card.Descriptor.ChunkCount, Version: card.Version,
                    Remote: remote, Own: own, Error: null);
            }

            case AetherResolution.ContentTarget:
                return MeshPage.Fail(address, "raw content address — open a named page instead");

            case AetherResolution.NotFound notFound:
                return MeshPage.Fail(address, "not found — " + notFound.Reason);

            case AetherResolution.Invalid invalid:
                return MeshPage.Fail(address, "invalid address — " + invalid.Error);

            default:
                return MeshPage.Fail(address, "unresolvable");
        }
    }

    /// <summary>
    /// The bytes behind an image block, as something an <c>&lt;img&gt;</c> can show.
    ///
    /// <para>
    /// The card names a content hash, never a place — so this assembles the bytes we already hold (or
    /// pull from the mesh) and hands back a <c>data:</c> URI built <b>by us</b>. The card never supplies
    /// a URI of its own, so opening a stranger's card cannot cause a single outbound request.
    /// </para>
    ///
    /// <para>Null when the artwork has not arrived yet — the caller shows the description instead.</para>
    /// </summary>
    public async Task<string?> AssetAsync(string? contentHash, CancellationToken cancellationToken = default)
    {
        if (!CardBlock.IsUsableAssetHash(contentHash)) return null;
        if (_assets.TryGetValue(contentHash!, out var cached)) return cached;

        var bytes = await _content.AssembleAsync(contentHash!, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            // Not held here. The author announces the artwork's descriptor when the link comes up; until
            // that has arrived there is nothing to verify an incoming chunk against, so we ask for
            // nothing and the reader sees the description. The picture fills in on the next open.
            if (await _store.GetDescriptorAsync(contentHash!, cancellationToken).ConfigureAwait(false) is null)
                return null;

            await _content
                .RequestChunksAsync(contentHash!, Array.Empty<int>(), null, cancellationToken)
                .ConfigureAwait(false);
            bytes = await WaitForContentAsync(contentHash!, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null || bytes.Length == 0) return null;

        // Card art is SVG: a few hundred bytes that stay sharp at any size, which is what a link
        // measured at roughly 5 kbps can actually carry.
        var uri = "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
        _assets[contentHash!] = uri;
        return uri;
    }

    private async Task<byte[]?> WaitForContentAsync(string rootHash, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 60; i++)
        {
            var bytes = await _content.AssembleAsync(rootHash, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
                return bytes;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return await _content.AssembleAsync(rootHash, cancellationToken).ConfigureAwait(false);
    }

    private void Remember(string address, string tag, CardDocument document)
    {
        if (_saved.Any(s => s.Address == address))
            return;
        _saved.Add(new SavedCard(address, tag, document.Title));
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // ─── Personas — distinct hyper-local sites, one per device ───────────────────

    private static Persona PickPersona(string tag)
    {
        var h = 0;
        foreach (var c in tag) h = unchecked(h * 31 + c);
        return Personas[Math.Abs(h) % Personas.Length];
    }

    private sealed record Persona(string Key, (string Name, CardDocument Document)[] Pages);

    private static readonly Persona[] Personas =
    {
        new("board", new[]
        {
            ("home", Card("Kagiso Community Board",
                Accent(BoardAccent),
                Art("The noticeboard at the corner"),
                CardBlock.Of(CardBlock.Text, "Notices pinned by people on your street."),
                Kv("Updated", "This morning"),
                Kv("Pinned by", "14 neighbours"),
                Items("Load-shedding: Stage 4, off 18:00-20:30",
                      "Lift club to town, 07:15 from the rank",
                      "Water tanker on Vilakazi St, Thursday"),
                CardBlock.Of(CardBlock.Text, "No signal? It still updates - it is on the mesh."),
                Link("This week's schedule", "power"))),

            ("power", Card("Load-shedding this week",
                Accent(BoardAccent),
                CardBlock.Of(CardBlock.Text, "Posted by whoever gets the notice first."),
                Kv("Monday", "Stage 2 · 06:00 - 08:30"),
                Kv("Tuesday", "Stage 4 · 18:00 - 20:30"),
                Kv("Wednesday", "Stage 4 · 18:00 - 20:30"),
                Kv("Thursday", "None expected"),
                Kv("Friday", "Stage 2 · 20:00 - 22:30"),
                CardBlock.Of(CardBlock.Text, "Times shift. Charge when the power is on."),
                Link("Back to the board", "home"))),
        }),
        new("spaza", new[]
        {
            ("home", Card("Mama Dlamini's Spaza",
                Accent(SpazaAccent),
                Art("The shop front on the corner"),
                CardBlock.Of(CardBlock.Text, "Open 06:00 to 20:00, every day."),
                Kv("Open", "06:00 - 20:00, every day"),
                Kv("Airtime", "All networks"),
                Kv("Stokvel", "Ask inside"),
                Items("Bread, milk, airtime, paraffin",
                      "Cold drinks in the fridge at the back",
                      "Ask about the stokvel"),
                CardBlock.Of(CardBlock.Text, "Pay in cash or on the mesh."),
                Link("Today's prices", "prices"))),

            ("prices", Card("Today's prices",
                Accent(SpazaAccent),
                CardBlock.Of(CardBlock.Text, "Changed this morning. Cash or mesh, same price."),
                Kv("Bread", "R18"),
                Kv("Milk 1L", "R24"),
                Kv("Paraffin 1L", "R31"),
                Kv("Airtime", "From R5"),
                Link("Back to the shop", "home"))),
        }),
        new("taxi", new[]
        {
            ("home", Card("Rank 7 Taxi Times",
                Accent(TaxiAccent),
                Art("The rank at seven in the morning"),
                CardBlock.Of(CardBlock.Text, "Times people actually saw, not a timetable."),
                Kv("First one", "05:10"),
                Kv("To the mall", "About every 20 min"),
                Kv("Last one back", "21:30"),
                Items("Town: first 05:10, then when it fills",
                      "Mall: roughly every 20 minutes to 18:00",
                      "Last one back: 21:30, do not count on it"),
                Link("What it costs", "fares"))),

            ("fares", Card("Fares from Rank 7",
                Accent(TaxiAccent),
                CardBlock.Of(CardBlock.Text, "What people paid this week. Have the coins ready."),
                Kv("Town", "R16"),
                Kv("The mall", "R13"),
                Kv("Krugersdorp", "R22"),
                Kv("After 20:00", "R2 more, most drivers"),
                Link("Back to the times", "home"))),
        }),
    };

    /// <summary>
    /// Build a card: a title and an ordered list of typed blocks. No markup anywhere — a renderer
    /// draws these, so a card authored by a stranger cannot execute anything or fetch anything.
    /// </summary>
    private static CardDocument Card(string title, params CardBlock[] blocks) =>
        new() { Title = title, Blocks = [.. blocks] };

    private static CardBlock Items(params string[] items) =>
        new() { Kind = CardBlock.List, Items = [.. items] };

    /// <summary>A labelled fact — opening hours, a price, a platform number.</summary>
    private static CardBlock Kv(string key, string value) =>
        new() { Kind = CardBlock.KeyValue, Value = $"{key} · {value}" };

    /// <summary>
    /// A link to another card <b>by this same author</b>. The address carries a placeholder for the
    /// author's tag, which is not known when the personas are declared and is resolved at publish time
    /// — so a card can only ever point within the mesh, never at the open web.
    /// </summary>
    private static CardBlock Link(string label, string page) =>
        new() { Kind = CardBlock.Link, Value = label, Target = $"aether://{PageAuthorToken}/{page}" };

    /// <summary>Stand-in for the author's tag inside a declared persona, swapped in at publish time.</summary>
    private const string PageAuthorToken = "{author}";

    /// <summary>Stand-in for the persona artwork's content hash, which only exists once published.</summary>
    private const string PageArtToken = "{art}";

    /// <summary>The card's picture, referenced by hash rather than by where it lives.</summary>
    private static CardBlock Art(string description) =>
        new() { Kind = CardBlock.Image, ContentHash = PageArtToken, Value = description };

    /// <summary>The card's accent colour — one declared value, interpreted by our renderer.</summary>
    private static CardBlock Accent(string hex) =>
        new() { Kind = CardBlock.Theme, Value = hex };

    /// <summary>
    /// A persona's artwork, drawn as SVG.
    ///
    /// <para>
    /// A few hundred bytes that stay sharp at any size — which is the only kind of picture a link
    /// measured at roughly 5 kbps can carry without the reader waiting a minute and a half. It is also
    /// generated rather than photographed, so every device produces its own without shipping assets in
    /// the APK.
    /// </para>
    /// </summary>
    private static string PersonaArt(string key, string tag)
    {
        var accent = AccentFor(key);
        var mark = key switch { "spaza" => "S", "taxi" => "T", _ => "K" };

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 260" role="img">
              <defs>
                <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="{accent}"/>
                  <stop offset="1" stop-color="{accent}" stop-opacity=".55"/>
                </linearGradient>
              </defs>
              <rect width="600" height="260" fill="url(#g)"/>
              <circle cx="505" cy="52" r="120" fill="#fff" fill-opacity=".08"/>
              <circle cx="90" cy="232" r="90" fill="#000" fill-opacity=".10"/>
              <text x="40" y="150" font-family="system-ui,sans-serif" font-size="104" font-weight="800"
                    fill="#fff" fill-opacity=".92">{mark}</text>
              <text x="42" y="196" font-family="ui-monospace,monospace" font-size="20"
                    fill="#fff" fill-opacity=".70">{tag}</text>
            </svg>
            """;
    }

    // Each persona gets its own colour, so two cards never look like the same document. Declared once
    // and read by both the card's theme block and its artwork — a picture in one colour under a card
    // in another is exactly the kind of drift a shared constant prevents.
    private const string SpazaAccent = "#B4541F";
    private const string TaxiAccent = "#1F6FB4";
    private const string BoardAccent = "#2E7D4F";

    private static string AccentFor(string key) => key switch
    {
        "spaza" => SpazaAccent,
        "taxi" => TaxiAccent,
        _ => BoardAccent,
    };

    /// <summary>A peer card kept on this phone after fetching it over the radio.</summary>
    public sealed record SavedCard(string Address, string Tag, string Title);

    /// <summary>A rendered (or failed) mesh-web page handed to the UI.</summary>
    public sealed record MeshPage(
        bool Ok, string Address, string? Name, CardDocument? Card, string? AuthorTag,
        string? RootHash, long Bytes, int Chunks, long Version, bool Remote, bool Own, string? Error)
    {
        public static MeshPage Fail(string address, string error) =>
            new(false, address, null, null, null, null, 0, 0, 0, false, false, error);
    }
}
