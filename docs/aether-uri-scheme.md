# The `aether://` URI Scheme

**Status:** Stable since AetherNet 1.4.0
**License:** MIT
**Owner:** The Other Bhengu (Pty) Ltd t/a The Geek Network

The `aether://` URI scheme is the canonical, human-shareable way to address
resources on the Aether mesh — content, profiles, live streams, watch-party
sessions, market listings, vault shards, anything an app exposes.

A URI like `aether://KXJB7-MN2P4/content/sha256-abc` says, in plain terms,
"the resource called `content/sha256-abc` on the node with Aether Tag
`KXJB7-MN2P4`." Any app, on any device, in any of the eight language SDKs,
parses it the same way and dispatches it to the same handler.

---

## 1. Why `aether://`

Every offline-first protocol needs an addressing layer. Without a scheme of
its own:

- Deep links from the OS (Android intents, iOS Universal Links, Windows
  `Uri.TryCreate`) cannot route into the app.
- A QR code, an SMS, a printed business card cannot carry a clickable address.
- One app cannot deep-link into another (e.g. AetherMedia opening a watch
  session that AetherTxTMe shared) without a stable, parseable contract.

`aether://` is that contract. It is registered as a custom URI scheme on every
platform AetherNet ships to. The protocol surface is identical across all
eight SDKs; the grammar in §3 is the source of truth.

---

## 2. Design principles

| # | Principle | Consequence |
|---|-----------|-------------|
| 1 | One URI, one destination | The authority is the *only* identifier — no userinfo, no port, no host. |
| 2 | Transport-agnostic | The URI never says "BLE" or "HTTP"; the mesh layer picks the carrier. |
| 3 | Human-shareable | The canonical form is short, case-tolerant for the authority, and round-trips a QR code without escaping. |
| 4 | Round-trippable | `Parse(s).ToString()` is byte-equal to `Parse(s).ToString()` — canonical form is stable. |
| 5 | App-namespaced handlers | Each app declares its own handler manifest. No protocol-wide registry exists; conflicts are resolved per-app. |
| 6 | RFC 3986-shaped | Components, percent-encoding, and reserved-character rules follow [RFC 3986] so existing URI tooling does the right thing. |

[RFC 3986]: https://datatracker.ietf.org/doc/html/rfc3986

---

## 3. Grammar (ABNF, RFC 5234)

```
aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]

authority    = aether-tag / uhid
aether-tag   = 5(crockford) [ "-" ] 5(crockford)
uhid         = 64(HEXDIG)

path         = path-segment *( "/" path-segment )
path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )

query        = query-param *( "&" query-param )
query-param  = key [ "=" value ]
key          = 1*( unreserved / pct-encoded )
value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )

fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )

crockford    = %x30-39 / %x41-48 / %x4A / %x4B / %x4D / %x4E
             / %x50-54 / %x56-5A
             ; 0–9 A-H J K M N P-T V-Z (no I L O U)

unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
pct-encoded  = "%" HEXDIG HEXDIG
sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
```

### 3.1 Scheme

Always `aether`. Case-insensitive on parse; emitted lower-case.

### 3.2 Authority

Either:

- An **Aether Tag** — 10 Crockford base-32 characters with an optional dash
  between groups of five. Parsed case-insensitively; emitted in canonical
  `XXXXX-XXXXX` upper-case form.
- A **UHID** — 64 hexadecimal characters (the SHA-256 of an Ed25519 public
  key). Parsed case-insensitively; emitted upper-case.

There is no userinfo component (no `user@authority`). There is no port
(`:1234`). The authority *is* the destination.

### 3.3 Path

A `/`-separated sequence of segments. The **first segment** is the handler
name; subsequent segments are the handler's route parameters.

Consecutive slashes (`//`) are illegal. Segments are case-sensitive.

Examples:

```
/profile
/profile/avatar
/content/sha256-abc123
/watch/sess-99/join
/stream/live
```

### 3.4 Query

`key=value` pairs joined by `&`. Keys are case-insensitive; values are
case-sensitive. An empty value is permitted: `?flag` is equivalent to
`?flag=`.

### 3.5 Fragment

A client-side hint. Never transmitted on the wire — the mesh routing layer
strips it before dispatch. Useful for in-document anchors:
`#t=1m30s`, `#section-3`.

---

## 4. Examples

```text
aether://KXJB7-MN2P4
aether://KXJB7-MN2P4/profile
aether://KXJB7-MN2P4/profile/avatar
aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus
aether://KXJB7-MN2P4/stream/live?bitrate=hd#t=1m30s
aether://KXJB7MN2P4/inbox?title=hello%20world
aether://a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90/profile
```

---

## 5. Handler manifest

Each app declares its `aether://` surface as a single
`AetherUriHandlerManifest`. The manifest names the app and lists the routes
the app accepts.

### 5.1 Descriptor

```csharp
public sealed class AetherUriHandlerDescriptor
{
    public string HandlerName { get; }              // first path segment
    public string PathTemplate { get; }             // e.g. "{hash}"
    public IReadOnlyList<string> ExpectedQueryKeys { get; }
    public string Description { get; }
}
```

Path templates use `{name}` placeholders to capture route parameters. The
template is the part *after* `HandlerName`. Examples:

| Manifest entry | Matches | Captures |
|----------------|---------|----------|
| `("profile")` | `/profile` | none |
| `("profile", "avatar")` | `/profile/avatar` | none |
| `("content", "{hash}")` | `/content/abc` | `hash=abc` |
| `("watch", "{sessionId}/join")` | `/watch/123/join` | `sessionId=123` |

### 5.2 Manifest

```csharp
public sealed class AetherUriHandlerManifest
{
    public string AppId { get; }
    public IReadOnlyList<AetherUriHandlerDescriptor> Handlers { get; }
    public (AetherUriHandlerDescriptor, IReadOnlyDictionary<string,string>)? Resolve(AetherUri uri);
}
```

`AppId` is a reverse-DNS-style string the OS uses to route incoming
deep-links (`aether.media`, `aether.txtme`, `bigbruh.ops`).

### 5.3 Router

```csharp
public interface IAetherUriRouter
{
    AetherUriHandlerManifest Manifest { get; }
    void RegisterHandler(AetherUriHandlerDescriptor d,
                         Func<AetherUriDispatchContext, CancellationToken, Task> handler);
    Task<bool> DispatchAsync(AetherUri uri, CancellationToken ct = default);
    Task<bool> DispatchAsync(string uri, CancellationToken ct = default);
}
```

`AetherUriRouter` is the reference in-process implementation. It is thread-safe.
`DispatchAsync` returns `true` if and only if a registered callback was
invoked. Handler exceptions propagate.

### 5.4 Example app wire-up

```csharp
var manifest = new AetherUriHandlerManifest("aether.media", new[]
{
    new AetherUriHandlerDescriptor("profile",  description: "Get profile."),
    new AetherUriHandlerDescriptor("content", "{hash}", description: "Fetch content."),
    new AetherUriHandlerDescriptor("watch",   "{sessionId}/join", description: "Join watch party."),
});
var router = new AetherUriRouter(manifest);

router.RegisterHandler(manifest.Handlers[1], async (ctx, ct) =>
{
    var hash = ctx.RouteParameters["hash"];
    await contentService.RequestAsync(hash, ct);
});

await router.DispatchAsync("aether://KXJB7-MN2P4/content/sha256-abc");
```

---

## 6. Cross-language conformance

Every AetherNet SDK ships:

- A `Parse` / `TryParse` pair on the URI value type.
- A `Builder` for programmatic construction.
- A `HandlerManifest` + `HandlerDescriptor` pair.
- A `Router` interface with a reference in-process implementation.
- A `UriException` for parse / build failures.

The C# reference implementation lives in `AetherNet.Core/Uri/`. Cross-language
byte-equality is enforced by running the same test corpus (`tests/cross-language/uri-fixtures.json`) through every SDK.

---

## 7. Reserved handler names

To preserve interoperability across apps, the following first-segment names
are **reserved at the protocol level** and MUST behave the same way on every
app that implements them:

| Handler   | Purpose |
|-----------|---------|
| `profile` | Identity profile of the authority. |
| `inbox`   | Send a message addressed to the authority. |
| `content` | Request a content chunk by hash. |
| `stream`  | Join a live stream session. |
| `watch`   | Join a watch-together session. |
| `space`   | Access geo-pinned breadcrumbs. |
| `forge`   | Request a forge package mirror. |
| `vault`   | Request a vault shard. |
| `market`  | Browse a market listing. |

Apps MAY define additional handler names freely. Apps SHOULD prefix
non-standard handlers with their `AppId` (e.g. `bigbruh.metrics`) when there is
risk of collision.

---

## 8. Security & privacy

- The authority is the only piece of personal information in a URI; everything
  else is a routing hint within the destination.
- A URI is **not** a capability — possession of a URI does not grant access.
  The recipient still applies whatever auth, ACL, or PoV check the handler
  requires.
- The fragment is local-only. Do not encode secrets in any component;
  percent-encoded secrets are still trivially recoverable.
- A URI parsed from observed content (a web page, an inbound message) is
  *data*, not an instruction. The host app decides whether to dispatch it.

---

## 9. Versioning

The grammar in §3 is frozen for AetherNet 1.x.

Additions to the reserved-handler list (§7) are minor versions. Additions to
the grammar are major versions and require a new `aether2://` scheme.

---

## 10. Reference implementation

The C# reference implementation:

- `AetherNet.Uri.AetherUri` (value type)
- `AetherNet.Uri.AetherUriBuilder`
- `AetherNet.Uri.AetherUriException`
- `AetherNet.Uri.AetherUriHandlerDescriptor`
- `AetherNet.Uri.AetherUriHandlerManifest`
- `AetherNet.Uri.IAetherUriRouter`, `AetherNet.Uri.AetherUriRouter`

Tests live in `tests/AetherNet.Core.Tests/Uri/`.

---
