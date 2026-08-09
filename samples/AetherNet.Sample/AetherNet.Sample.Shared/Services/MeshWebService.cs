// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Cards;
using AetherNet.Content;
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

    public MeshWebService(
        IIdentityService me,
        IContentStore contentStore,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
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
            var privateKey = _me.PrivateKey;
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

            // Publish this device's persona: each page content-addressed, signed under this tag.
            foreach (var (name, html) in _persona.Pages)
            {
                await cards.PublishCardAsync(
                        name, Encoding.UTF8.GetBytes(html), "text/html",
                        privateKey, version: 1, cancellationToken)
                    .ConfigureAwait(false);
                _pages.Add(name);
            }

            _ready = true;
        }
        finally
        {
            _initGate.Release();
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
    private async Task SendHelloBurstAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await SendHelloAsync().ConfigureAwait(false);
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

                var html = Encoding.UTF8.GetString(bytes);
                if (!own)
                    Remember(address, authorTag, html);

                return new MeshPage(
                    Ok: true, Address: address, Name: card.Name, Html: html, AuthorTag: authorTag,
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

    private void Remember(string address, string tag, string html)
    {
        if (_saved.Any(s => s.Address == address))
            return;
        _saved.Add(new SavedCard(address, tag, TitleOf(html)));
        RaiseChanged();
    }

    private static string TitleOf(string html)
    {
        var i = html.IndexOf("<h1", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Saved card";
        var gt = html.IndexOf('>', i);
        if (gt < 0) return "Saved card";
        var end = html.IndexOf("</h1>", gt, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? "Saved card" : html[(gt + 1)..end].Trim().Replace("&amp;", "&");
    }

    private void RaiseChanged() => Changed?.Invoke();

    // ─── Personas — distinct hyper-local sites, one per device ───────────────────

    private static Persona PickPersona(string tag)
    {
        var h = 0;
        foreach (var c in tag) h = unchecked(h * 31 + c);
        return Personas[Math.Abs(h) % Personas.Length];
    }

    private sealed record Persona(string Key, (string Name, string Html)[] Pages);

    private static readonly Persona[] Personas =
    {
        new("board", new[]
        {
            ("home", Page("Kagiso Community Board",
                """
                <p>Notices pinned by people on your street.</p>
                <ul><li>⚡ Load-shedding: Stage 4, off 18:00–20:30</li>
                <li>🚗 Lift club to town, 07:15 from the rank</li>
                <li>💧 Water tanker on Vilakazi St, Thursday</li></ul>
                <p>No signal? It still updates — it's on the mesh.</p>
                """)),
            ("market", Page("Saturday Market",
                "<p>8am–1pm · community hall</p><ul><li>🍅 Co-op produce</li><li>🥘 Kota &amp; vetkoek</li><li>🎶 Amapiano from 11</li></ul>")),
        }),
        new("spaza", new[]
        {
            ("home", Page("Thabo's Spaza Shop",
                """
                <p>Open 6am–9pm · cash &amp; SnapScan</p>
                <ul><li>Fresh bread daily from 7</li><li>Airtime, data &amp; electricity</li>
                <li>Cold drinks &amp; ice</li><li>Paraffin &amp; candles</li></ul>
                <p>On the corner of Vilakazi &amp; 7th.</p>
                """)),
            ("specials", Page("Today's Specials",
                "<ul><li>2L cooldrink — R22</li><li>Loaf + polony — R30</li><li>Airtime R12 = R10</li></ul>")),
        }),
        new("salon", new[]
        {
            ("home", Page("Lerato's Hair &amp; Nails",
                """
                <p>Walk-ins welcome · Tue–Sun</p>
                <ul><li>💇🏾‍♀️ Cornrows &amp; braids</li><li>💅 Gel nails &amp; acrylics</li>
                <li>✂️ Cuts &amp; fades</li></ul>
                <p>Book on the board or just pop in.</p>
                """)),
            ("prices", Page("Price List",
                "<ul><li>Braids — from R150</li><li>Gel nails — R120</li><li>Fade — R60</li></ul>")),
        }),
        new("taxi", new[]
        {
            ("home", Page("Kagiso Taxi Rank",
                """
                <p>Live-ish routes &amp; fares, pinned by drivers.</p>
                <ul><li>🚐 Town — R18 · every 10 min</li><li>🚐 Mall — R15</li>
                <li>🚐 Clinic — R12</li></ul>
                <p>First taxi 05:00 · last 21:30.</p>
                """)),
            ("fares", Page("Fares",
                "<ul><li>Town — R18</li><li>Mall — R15</li><li>Clinic — R12</li><li>Station — R20</li></ul>")),
        }),
        new("fixit", new[]
        {
            ("home", Page("Sipho's Fix-It",
                """
                <p>Phones, kettles, radios — if it's broken, bring it.</p>
                <ul><li>🔌 Appliance repairs</li><li>📱 Screen &amp; battery swaps</li>
                <li>🔦 Load-shedding lights &amp; power banks</li></ul>
                <p>Behind the taxi rank. Cash only.</p>
                """)),
            ("hours", Page("Opening Hours",
                "<ul><li>Mon–Fri 8–17</li><li>Sat 8–13</li><li>Sun closed</li></ul>")),
        }),
    };

    private static string Page(string title, string body) =>
        $$"""
        <div style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:var(--ink);
                    background:var(--surface);border:1px solid var(--line);border-radius:14px;
                    padding:20px 22px;line-height:1.55">
          <h1 style="margin:0 0 12px;font-size:1.35rem;color:var(--brand)">{{title}}</h1>
          {{body}}
        </div>
        """;

    /// <summary>A peer card kept on this phone after fetching it over the radio.</summary>
    public sealed record SavedCard(string Address, string Tag, string Title);

    /// <summary>A rendered (or failed) mesh-web page handed to the UI.</summary>
    public sealed record MeshPage(
        bool Ok, string Address, string? Name, string? Html, string? AuthorTag,
        string? RootHash, long Bytes, int Chunks, long Version, bool Remote, bool Own, string? Error)
    {
        public static MeshPage Fail(string address, string error) =>
            new(false, address, null, null, null, null, 0, 0, 0, false, false, error);
    }
}
