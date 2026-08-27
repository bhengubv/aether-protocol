// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Cards;
using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Data;
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

    /// <summary>
    /// "Here is a card." Followed by the address, in UTF-8.
    /// </summary>
    /// <remarks>
    /// Rides <c>PacketType.Data</c> rather than getting a type of its own. A new packet type costs
    /// every one of the eight language SDKs and a round of byte-parity fixtures, and buys nothing
    /// here — the marker is doing the same job the greeting already does.
    /// </remarks>
    private static readonly byte[] GiveMarker = Encoding.UTF8.GetBytes("MWGIVE:");

    private readonly ILoggerFactory _loggerFactory;
    private readonly IRadioMesh? _radio;
    private readonly IIdentityService _me;
    private readonly AetherNet.Identity.INodeIdentity _node;
    private readonly IContentStore _contentStore;
    private readonly MyPages _mine;
    private readonly Deck _deck;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _ready;

    private IMeshSender _sender = default!;
    private IContentService _content = default!;
    private IContentStore _store = default!;
    private IAetherResolver _resolver = default!;
    private IDirectoryService _directory = default!;
    private ICardService _cards = default!;
    private string _localTag = "";

    private string? _peerSite;
    private bool _wasLinked;
    private bool _helloSent;

    private readonly Dictionary<string, string> _assets = new(StringComparer.Ordinal);

    /// <summary>
    /// Every picture this device can supply, by content hash.
    /// </summary>
    /// <remarks>
    /// Mastheads this app generated and photographs their author chose, held together because a peer
    /// asking for one cannot tell the difference and should not have to. A picture is separate content
    /// from the card that names it — the card carries its own descriptor, the picture does not — so
    /// without announcing these a peer has nothing to verify an arriving chunk against. It cannot even
    /// ask, and every page it fetches from us renders as text with a hole where the picture goes.
    /// </remarks>
    private readonly Dictionary<string, ContentDescriptor> _carried = new(StringComparer.Ordinal);

    public MeshWebService(
        IIdentityService me,
        AetherNet.Identity.INodeIdentity node,
        IContentStore contentStore,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null,
        MyPages? mine = null,
        Deck? deck = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        // Cards are signed by the device, so the node signs them. This service never sees the key.
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _radio = radio;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        // Last, and optional, so every existing caller still compiles. A head that hosts nothing of its
        // own — or a test — gets a set of pages backed by memory rather than by somebody's phone.
        _mine = mine ?? new MyPages(AetherStore.InMemory());
        _deck = deck ?? new Deck(AetherStore.InMemory());
    }

    public event Action? Changed;

    /// <summary>
    /// Somebody handed us a card. The address they gave, nothing more.
    /// </summary>
    /// <remarks>
    /// Deliberately not "somebody handed us a card, here it is". An address is a claim; what arrives
    /// is fetched and verified against its author's own key and hashes like anything else. Being
    /// given something is not a reason to trust it — it is only a reason to go and look.
    /// </remarks>
    public event Action<string>? Offered;

    public string LocalTag => _localTag;

    /// <summary>The pages this device hosts — written here, served from here.</summary>
    public MyPages Mine => _mine;

    /// <summary>The names of the pages currently standing on the mesh.</summary>
    public IReadOnlyList<string> Pages => [.. _mine.All.Where(p => p.Live).Select(p => p.Name)];

    /// <summary>This device's front door.</summary>
    public string HomeAddress => Address(MyPages.Home);

    public string Address(string name) => $"aether://{_localTag}/{name}";

    public bool RadioAvailable => _radio is { IsSupported: true };
    public string RadioName => _radio?.SelectedRadio ?? "radio";
    public bool RadioLinked => _radio?.IsLinked ?? false;
    public string? PeerSiteAddress => _peerSite;
    public void LinkRadio() => _radio?.Link();

    /// <summary>
    /// Cards written by other people that this phone holds — browsable offline, and servable.
    /// </summary>
    /// <remarks>
    /// This was a list in memory, which made "held offline forever" last exactly until the app was
    /// closed. It is the device's own database now, because a card that does not survive a restart is
    /// a cache with ambitions rather than an object somebody owns.
    /// </remarks>
    public Deck Deck => _deck;

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
            _cards = new CardService(_content, _directory);
            _resolver = new AetherResolver(_cards);

            // Somebody who has published nothing still answers at their front door. A tag that resolves
            // to nothing is a person a visitor bounces off, and the visitor has no way to tell an empty
            // node from a broken one — so every device hosts a page from its first launch.
            if (_mine.Get(MyPages.Home) is null)
                _mine.Save(new WebCard
                {
                    Name = MyPages.Home,
                    // Their tag stands in until they have typed a name. It is the one thing that is
                    // true about a device on its first launch, and a front door with a blank heading
                    // reads as broken rather than as new.
                    Doc = PageTemplate.Of("me").Build(MyName.OrTag(_mine.OwnerName, _localTag)),
                });

            // Everything already standing on the mesh gets said again, because a directory binding
            // lives in the memory of whichever phones heard it and coming back after a day away means
            // repeating yourself. The front door goes up whether or not anybody has published it.
            //
            // Drafts stay put. Somebody who opened the editor, got halfway and walked off has not
            // decided to publish anything, and a restart is not their decision either — a half-written
            // page appearing under their tag is the app publishing on their behalf.
            foreach (var page in _mine.All.ToArray())
                if (page.Live || page.Name == MyPages.Home)
                    await PublishPageAsync(page, cancellationToken).ConfigureAwait(false);

            // And everything this phone holds for other people.
            //
            // Hold-and-forward is the whole card economy: a phone that has been handed a card can
            // serve it to a third that never met its author, who still verifies it against the
            // author's own key and hashes. The protocol already answers a query from any binding it
            // has admitted — but admitted bindings live in memory, so without this a restart quietly
            // turned this node from a holder back into a bystander.
            await ReofferHeldCardsAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Offer every card this phone holds, on its author's behalf.
    /// </summary>
    /// <remarks>
    /// Two things have to be in place before this node can answer for somebody else's card: the
    /// descriptor, so an arriving chunk request can be served and a chunk verified, and the author's
    /// signed name binding, so a query for their address gets their signature rather than ours.
    /// Replaying the binding files it locally and re-announces it, which is exactly what a holder
    /// should do on waking up next to other phones.
    /// </remarks>
    private async Task ReofferHeldCardsAsync(CancellationToken cancellationToken)
    {
        foreach (var held in _deck.All)
        {
            if (Deck.DescriptorOf(held) is not { } descriptor) continue;

            await _store.SaveDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);

            try
            {
                await _directory
                    .PublishSignedAsync(
                        held.Name, descriptor, held.AuthorKey, held.Version, held.Signature,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A card we cannot re-offer is one this phone still reads and cannot pass on. Worth
                // nothing to anybody else, worth everything to its holder — so it stays in the deck.
            }
        }
    }

    // ─── Handing a card on ───────────────────────────────────────────────────────

    /// <summary>
    /// Give a card to whoever is standing next to us — including one we did not write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things go across, in the order the far side needs them. The descriptor, so an arriving
    /// chunk can be checked against something. The author's own signed binding, replayed exactly as
    /// they made it, so a query for the address answers with their signature rather than ours. And
    /// then the address itself, so the other phone knows to go and look.
    /// </para>
    /// <para>
    /// Nothing here asserts anything. The far side fetches, checks the signature against the author's
    /// key and the chunks against the hashes, and would refuse all of it if any of that failed —
    /// which is what makes handing on a stranger's card safe to do and safe to receive.
    /// </para>
    /// </remarks>
    /// <returns>False when there is nothing to give or nobody to give it to.</returns>
    public async Task<bool> GiveAsync(string? address, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(address)) return false;
        if (_radio is not { IsLinked: true }) return false;

        // Ours to give, or somebody else's that we hold. Both are legitimate; only the source of the
        // signature differs, and neither is ours to alter.
        if (_deck.Get(address) is { } held && Deck.DescriptorOf(held) is { } descriptor)
        {
            await _content.AnnounceAsync(descriptor, cancellationToken).ConfigureAwait(false);
            await _directory
                .PublishSignedAsync(
                    held.Name, descriptor, held.AuthorKey, held.Version, held.Signature, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (Mine.Get(NameIn(address)) is { Live: true } page)
        {
            await PublishPageAsync(page, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return false;
        }

        // Every picture the card names, too. A page that arrives without its photograph is a page
        // whose author is standing right there and cannot be asked.
        foreach (var picture in _carried.Values.ToArray())
            await _content.AnnounceAsync(picture, cancellationToken).ConfigureAwait(false);

        return await SayAsync(GiveMarker, address, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Send a marked message to whoever is linked.</summary>
    private Task<bool> SayAsync(byte[] marker, string said, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(said);
        var payload = new byte[marker.Length + body.Length];
        marker.CopyTo(payload, 0);
        body.CopyTo(payload, marker.Length);

        return _sender.SendAsync(
            new MeshPacket
            {
                Type = PacketType.Data,
                SourceUhid = _localTag,
                DestinationUhid = string.Empty,
                Ttl = 1,
                Payload = payload,
            },
            string.Empty);
    }

    /// <summary>The page name inside an address, or empty.</summary>
    private static string NameIn(string address)
    {
        var cut = address.LastIndexOf('/');
        return cut >= 0 && cut + 1 < address.Length ? address[(cut + 1)..] : "";
    }

    // ─── Pictures ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Take a photograph into this device's content store and hand back the hash a card names it by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Content-addressed, never located. The card carries the hash, so the same picture published by
    /// two people is one piece of content, a page renders years later with its author long gone, and a
    /// third phone that only ever met somebody holding the card can still supply the bytes.
    /// </para>
    /// <para>
    /// Kept the moment it is chosen rather than at publish time. Somebody who picks a picture, sees it
    /// in the preview and closes the app should find it there when they come back — and the store is
    /// the same one everything else on this device already survives in.
    /// </para>
    /// </remarks>
    /// <returns>The root hash, or null if these are not bytes we will carry.</returns>
    public async Task<string?> KeepPictureAsync(
        byte[] bytes, string mime, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!PagePhoto.IsUsable(mime, bytes)) return null;

        var picture = await _content
            .PublishAsync("picture", bytes, mime, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _carried[picture.RootHash] = picture;

        // Somebody is standing next to us right now. A picture chosen while the link is up should be
        // fetchable on the other phone without waiting for the radio to come back around.
        if (_radio is { IsLinked: true })
            await _content.AnnounceAsync(picture, cancellationToken).ConfigureAwait(false);

        return picture.RootHash;
    }

    // ─── Publishing what you wrote ───────────────────────────────────────────────

    /// <summary>
    /// Put a page on the mesh, or refresh the copy already standing there.
    /// </summary>
    /// <returns>The address it now answers at, or null if there is no page by that name.</returns>
    public async Task<string?> PublishAsync(string? name, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        return _mine.Get(name) is { } page
            ? await PublishPageAsync(page, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Content-address the page, sign the name binding under this tag, and say so on the mesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things happen here that the stored page does not carry, and each is added to a copy so the
    /// author's own draft keeps none of it.
    /// </para>
    /// <para>
    /// The blanks come out — a page arrives from a template as a shape with its values empty, and those
    /// are scaffolding for whoever is writing, not for whoever is reading.
    /// </para>
    /// <para>
    /// The look's colour goes in as a plain hex block, because the browser that draws a peer's card
    /// reads a single accent and knows nothing about looks. Without it, a sepia page opens in somebody
    /// else's app in the default blue.
    /// </para>
    /// <para>
    /// And the masthead is published as content in its own right and named by hash — which is what lets
    /// a third phone that only ever met a card-holder still draw the picture: the bytes are addressed,
    /// not located.
    /// </para>
    /// </remarks>
    private async Task<string> PublishPageAsync(WebCard page, CancellationToken cancellationToken)
    {
        var look = CardLook.FromCard(page.Doc);
        var document = OwnCard.ForPublish(page.Doc);

        var themed = document.Blocks.FindIndex(b => b.Kind == CardBlock.Theme);
        document.Blocks.Insert(themed + 1, CardBlock.Of(CardBlock.Theme, look.Accent));

        // A drawing only where there is no photograph. The generated masthead exists so that a page
        // nobody has put a picture on still has a face; a page whose author chose one does not need
        // ours, and two mastheads is a page arguing with itself.
        if (!document.Blocks.Any(b => b.Kind == CardBlock.Image && CardBlock.IsUsableAssetHash(b.ContentHash)))
        {
            var art = await _content
                .PublishAsync(
                    page.Name + "-art",
                    Encoding.UTF8.GetBytes(PageArt.Svg(document.Title, $"{_localTag}/{page.Name}", look.Accent)),
                    "image/svg+xml",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _carried[art.RootHash] = art;
            document.Blocks.Insert(0, new CardBlock
            {
                Kind = CardBlock.Image,
                ContentHash = art.RootHash,
                Value = document.Title,
            });
        }

        // Whatever pictures the page does name, this device must be ready to be asked for.
        foreach (var named in document.Blocks
            .Where(b => b.Kind == CardBlock.Image && CardBlock.IsUsableAssetHash(b.ContentHash))
            .Select(b => b.ContentHash!)
            .Distinct(StringComparer.Ordinal))
        {
            if (_carried.ContainsKey(named)) continue;
            if (await _store.GetDescriptorAsync(named, cancellationToken).ConfigureAwait(false) is { } held)
                _carried[named] = held;
        }

        // Newer than whatever stands, always. A binding that is not newer is refused by the directory,
        // which looks exactly like a successful publish from here and like nothing at all from there.
        var version = _mine.NextVersion(page.Name);

        await _cards
            .PublishCardAsync(
                page.Name, Encoding.UTF8.GetBytes(document.ToJson()), CardDocument.ContentType,
                _node, version, cancellationToken)
            .ConfigureAwait(false);

        _mine.WentLive(page.Name, version);

        // Somebody is already standing next to us. A page written while the link is up should be
        // readable on the other phone without waiting for the next time the radio comes back.
        if (_radio is { IsLinked: true })
            foreach (var picture in _carried.Values.ToArray())
                await _content.AnnounceAsync(picture, cancellationToken).ConfigureAwait(false);

        RaiseChanged();
        return Address(page.Name);
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

            case PacketType.Data when Offer(packet.Payload) is { Length: > 0 } offered:
                Offered?.Invoke(offered);
                break;
        }
    }

    private static bool IsHello(byte[]? payload) =>
        payload is not null && payload.AsSpan().SequenceEqual(HelloMarker);

    /// <summary>The address inside an offer, or null if this is not one.</summary>
    /// <remarks>
    /// Length-checked before anything is read out of it, and refused unless it is an
    /// <c>aether://</c> address. A packet from a stranger is untrusted input, and the one thing worse
    /// than ignoring an offer is following one somewhere off the mesh.
    /// </remarks>
    private static string? Offer(byte[]? payload)
    {
        if (payload is null || payload.Length <= GiveMarker.Length) return null;
        if (!payload.AsSpan(0, GiveMarker.Length).SequenceEqual(GiveMarker)) return null;

        var address = Encoding.UTF8.GetString(payload, GiveMarker.Length, payload.Length - GiveMarker.Length);

        return address.StartsWith("aether://", StringComparison.OrdinalIgnoreCase) && address.Length < 512
            ? address
            : null;
    }

    private void OnPeerHello(string? peerTag)
    {
        if (string.IsNullOrEmpty(peerTag) || peerTag == _localTag)
            return;

        // Their front door, which is the same name on every device — a greeting carries a tag, not a
        // sitemap, so the one page every node is guaranteed to answer at is the only thing worth
        // guessing at.
        var address = $"aether://{peerTag}/{MyPages.Home}";
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
            if (i is 0 or 3)
                foreach (var picture in _carried.Values.ToArray())
                    await _content.AnnounceAsync(picture).ConfigureAwait(false);
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
                // Opening somebody's card is how you come to hold it. The whole binding is kept —
                // their key, their version, their signature — so this phone can hand the card on to
                // a third that has never met them, and that third can still check it.
                if (!own)
                    _deck.Hold(address, card, document.Title, from: resolved.Card is not null ? null : null);

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

        // As what it is, not as what card art used to be. This assumed SVG, so the first photograph
        // anybody published came back declared as vector and drew as nothing at all.
        var kind = await _store.GetDescriptorAsync(contentHash!, cancellationToken).ConfigureAwait(false)
            is { ContentType.Length: > 0 } held ? held.ContentType : "image/svg+xml";

        if (kind != "image/svg+xml" && !PagePhoto.IsUsable(kind)) return null;

        var uri = $"data:{kind};base64," + Convert.ToBase64String(bytes);
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

    private void RaiseChanged() => Changed?.Invoke();

    // ─── Personas — the demo sites, no longer published ──────────────────────────
    //
    // A device used to host one of three invented sites so that two phones showed visibly different
    // pages before anybody had written anything. It hosts what its owner wrote now, so none of this is
    // published — it is kept because it is the only worked example of the block model in the repo, and
    // because deleting somebody's reference material to save a few hundred bytes of source is not a
    // trade worth making silently.

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

    /// <summary>A rendered (or failed) mesh-web page handed to the UI.</summary>
    public sealed record MeshPage(
        bool Ok, string Address, string? Name, CardDocument? Card, string? AuthorTag,
        string? RootHash, long Bytes, int Chunks, long Version, bool Remote, bool Own, string? Error)
    {
        public static MeshPage Fail(string address, string error) =>
            new(false, address, null, null, null, null, 0, 0, 0, false, false, error);
    }
}
