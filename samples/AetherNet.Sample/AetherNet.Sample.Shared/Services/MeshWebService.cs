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
/// Each page is a content-addressed, Ed25519-signed <see cref="Card"/> published under a
/// human name at an <c>aether://&lt;tag&gt;/&lt;name&gt;</c> address. Opening an address does
/// <i>not</i> hit DNS or HTTP — it resolves the signed name→content binding over the mesh
/// (here the in-process byte transport), pulls the content by its content hash, verifies it,
/// and renders it. Same code the eight language SDKs port — the real
/// <c>AetherNet.Cards</c> + <c>AetherNet.Content</c> stack; only the radio is simulated.
///
/// One node both hosts a tiny site and browses it, so a single phone proves the whole path:
/// an address with no server behind it, resolved and rendered on the device.
/// </summary>
public sealed class MeshWebService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly List<string> _pages = new();
    private volatile bool _ready;

    private IContentService _content = default!;
    private IAetherResolver _resolver = default!;
    private string _localTag = "";

    public MeshWebService(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>This device's AetherTag — the authority half of every address it serves.</summary>
    public string LocalTag => _localTag;

    /// <summary>The names this device publishes; each maps to <c>aether://{LocalTag}/{name}</c>.</summary>
    public IReadOnlyList<string> Pages => _pages;

    /// <summary>The address of the site's front page.</summary>
    public string HomeAddress => Address("home");

    /// <summary>Build the <c>aether://</c> address for one of this device's pages.</summary>
    public string Address(string name) => $"aether://{_localTag}/{name}";

    // ─── Setup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stand up the mesh-web node and publish the sample site. Idempotent — the first caller
    /// builds it, everyone else awaits the same result.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_ready)
            return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
                return;

            var (privateKey, publicKey) = Ed25519SigningService.GenerateKeyPair();
            _localTag = AetherNetTag.FromPublicKey(publicKey).Value;

            // One in-process node hosts and browses the site. A tag-derived UHID keeps it clear of
            // the other demo's nodes in the process-wide in-process registry (no ResetNetwork here).
            var uhid = "aether:web:" + _localTag;
            var transport = new InProcessTransportService(
                uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
            var sender = new InProcessMeshSender(uhid, transport);

            // The two mesh-web planes: a directory (signed name→content bindings) and a content
            // plane (chunked, hash-addressed bytes). Cards bind them; the resolver is the front door.
            var routing = new RoutingService(sender);
            var content = new ContentService(sender, routing);
            var directory = new DirectoryService(sender, new Ed25519NameBindingVerifier());
            var cards = new CardService(content, directory);

            // Inbound wire: route each plane's packets to its service, so a real peer's pages resolve
            // over the radio too. Single node here means nothing arrives — but this is the real dispatch.
            transport.DataReceived += (_source, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                _ = packet.Type switch
                {
                    PacketType.NamePublish or PacketType.NameQuery
                        => directory.HandleAsync(packet),
                    PacketType.TorrentMetadata or PacketType.ChunkBitmap
                        or PacketType.ChunkRequest or PacketType.ChunkData
                        => content.HandleAsync(packet),
                    _ => Task.CompletedTask,
                };
            };

            _content = content;
            _resolver = new AetherResolver(cards);

            // Publish the site: each page is content-addressed, signed under this tag, and versioned.
            foreach (var (name, html) in Site)
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

    // ─── Browse ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open an <c>aether://</c> address: resolve the signed card, pull its content by hash, verify,
    /// and hand back the page to render. No HTTP, no DNS, no host — just the mesh.
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
                .ResolveAsync(address.Trim(), TimeSpan.FromSeconds(2), cancellationToken)
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

                // The tag that signed this page — recomputed from the card's own key, so the
                // address's authority is proven, not asserted.
                var authorTag = AetherNetTag.FromPublicKey(card.AuthorPublicKey).Value;

                var bytes = await _content
                    .AssembleAsync(card.Descriptor.RootHash, cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is null)
                    return MeshPage.Fail(address, "content not on the mesh yet");

                return new MeshPage(
                    Ok: true,
                    Address: address,
                    Name: card.Name,
                    Html: Encoding.UTF8.GetString(bytes),
                    AuthorTag: authorTag,
                    RootHash: card.Descriptor.RootHash,
                    Bytes: bytes.LongLength,
                    Chunks: card.Descriptor.ChunkCount,
                    Version: card.Version,
                    Error: null);
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

    // ─── The sample site (three signed, content-addressed pages) ─────────────────

    private static IEnumerable<(string Name, string Html)> Site => new (string, string)[]
    {
        ("home", Page(
            "The web, with no server",
            """
            <p>This page has no URL and no host. It lives at an <b>aether://</b> address — a
            content-addressed, signed <b>card</b> — and it reached you over the mesh, not the
            internet. No DNS resolved it, no server served it.</p>
            <p>Open <code>about</code> and <code>shop</code> below: each is another card this
            device published under a human name, verifiable against its AetherTag.</p>
            """)),

        ("about", Page(
            "What just happened",
            """
            <p>Opening the address did four things, all on-device:</p>
            <ol>
              <li><b>Resolved</b> the name → a signed binding (author key + version).</li>
              <li><b>Verified</b> the binding's Ed25519 signature against the address's tag.</li>
              <li><b>Fetched</b> the content by its hash — the bytes can't be swapped without the
                  hash changing.</li>
              <li><b>Rendered</b> it. Same <code>AetherNet.Cards</code> + <code>AetherNet.Content</code>
                  the eight SDKs port.</li>
            </ol>
            <p>A neighbour's phone can host a page the same way; you'd pull it over BLE or Wi-Fi
            Direct with no infrastructure at all.</p>
            """)),

        ("shop", Page(
            "Kagiso Corner Store",
            """
            <p>A hyper-local shop page, hosted on the shopkeeper's own phone — no website, no
            hosting bill, no platform in the middle.</p>
            <ul>
              <li>Fresh bread — daily from 7am</li>
              <li>Airtime &amp; electricity</li>
              <li>Cold drinks &amp; ice</li>
            </ul>
            <p>Walk past, pull the card, it's yours to keep and re-share on the mesh.</p>
            """)),
    };

    /// <summary>Wrap page body copy in a small self-contained, brand-coloured document.</summary>
    private static string Page(string title, string body) =>
        $$"""
        <div style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:#2c3e50;
                    background:#ffffff;border-radius:14px;padding:20px 22px;line-height:1.5;
                    box-shadow:0 1px 0 rgba(44,62,80,.08)">
          <h1 style="margin:0 0 12px;font-size:1.35rem;color:#2196F3">{{title}}</h1>
          {{body}}
        </div>
        """;

    /// <summary>A rendered (or failed) mesh-web page handed to the UI.</summary>
    public sealed record MeshPage(
        bool Ok,
        string Address,
        string? Name,
        string? Html,
        string? AuthorTag,
        string? RootHash,
        long Bytes,
        int Chunks,
        long Version,
        string? Error)
    {
        public static MeshPage Fail(string address, string error) =>
            new(false, address, null, null, null, null, 0, 0, 0, error);
    }
}
