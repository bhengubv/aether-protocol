# AetherNet.Browser

The mesh browser, as a library. Pages that have no URL and no server — written on a device, hosted by
that device, and handed from person to person like a card.

Drop it into any Blazor host:

```csharp
builder.Services.AddAetherBrowser();
```

```razor
<AetherBrowser />
```

That is a working browser: the owner's own pages, the cards they hold, an editor to write more. It
has no routes of its own, no dependency on a shell, and every place it touches the device is an
interface you fill. The same component is one tab of a sample app today and a system browser later.

---

## What a card is

A hosted page is **a card, not a page** — a portable, tradeable object. Two properties are
non-negotiable for hosting without servers, and HTML surrenders both.

It is rendered from a blob authored by **a stranger**, so it must be **safe**. And the same blob must
render on any device, **offline, years later, with the author long gone**, so it must be **portable**.

So a card is a signed JSON document, not markup:

- a **versioned** document — metadata plus an ordered list of typed **blocks** (heading, text, list,
  key-value, picture, link, tip jar, theme);
- every asset by **content hash, never a URL** — nothing phones home, the bytes come off the mesh;
- **author-signed** and **content-addressed** as a whole;
- **forward-compatible** — an older renderer skips a block it does not know rather than failing the
  card, so a newer author never breaks an older reader;
- drawn by **one renderer** — uniform, theme-aware, and **inert**: no execution, no fetch.

## What makes it a card rather than a link

A card is **held, kept offline, and re-served.**

Open somebody's address and you keep their card. It opens with no signal, with its author asleep,
unreachable or gone. And you can hand it to a third person who has never met the author — who still
verifies it against **the author's** key and hashes, not against yours.

That is the property a URL cannot have. Passing a card on proves nothing about whoever passed it and
everything about whoever wrote it, so accepting one from a stranger is safe, and **spread comes
unstuck from origin**: a card travels further than its author ever did.

## Conscientious by construction

Not policy — structure. There is no version of this library that tracks anybody, because there is
nothing to turn off.

| | How |
|---|---|
| A card cannot phone home | Assets are content hashes. There is no URL to fetch. |
| A card cannot execute | Typed blocks drawn by our renderer. Author text is never markup. |
| The page cannot reach anywhere | `default-src 'none'` — the browser enforces it, we do not promise it. |
| Photographs carry no location | Decoded, redrawn on a canvas, re-encoded. No EXIF survives, ever. |
| Creators get paid directly | A tip jar names the author's own address. Nobody is in the middle. |
| No third parties exist | There is no third party. There is no server at all. |

## Frugal on purpose

The people this is for have cheap phones and expensive data — or no data. Every budget here is set by
the slowest radio rather than the fastest:

- a picture is redrawn and shrunk to **120 KB** before it can leave the device — a blink over Wi-Fi
  Direct, about a minute over Bluetooth, and the author is shown that cost in seconds because it is
  paid by whoever opens the page;
- the masthead shader runs at **half resolution and twenty frames a second**, and stops when nothing
  is watching;
- a look carries **only the typefaces it actually uses**;
- a card is a few hundred bytes of JSON.

## The seams

Two interfaces, both small enough to implement in an afternoon.

**`ICardStore`** — where the device keeps the owner's pages and the cards they hold. Durability is the
contract, not a detail: a card is an object somebody owns, and a store that forgets on restart
satisfies every signature and breaks the only promise that matters.

**`IMeshLink`** — the radio, reduced to four facts and one verb: is there one, is it linked, what is
it called, has a packet arrived, send this packet. Nothing about channels or bandwidth, so it lifts
onto a device whose radios work differently.

The host also provides an `INodeIdentity` and an `IContentStore` from the protocol libraries. Those
belong to the device rather than to this component — the same identity signs your messages, and the
same content store holds everything else the device carries.

Everything else — the model, the renderer, the editor, the deck, the wire — is in here.

## What is in the box

| | |
|---|---|
| `AetherBrowser.razor` | Browse · Mine · Deck, and the editor. The whole surface. |
| `CardEditor.razor` | One surface: the page, its stylesheet, the preview, publish. |
| `CardText` | A card as a document and back, losslessly. The blocks fall out of what you wrote. |
| `CardDocument` / `CardBlock` | The document. Versioned, typed blocks, forward-compatible. |
| `CardPage` | The renderer. One document in, one inert page out. |
| `CardLook` | Finished designs — type, colour and scale together, not a palette. |
| `PageTemplate` | Starting shapes. Headings written, every claim left blank. |
| `MyPages` / `Deck` | What this device hosts, and what it holds. |
| `MeshWebService` | Publishing, resolving, holding, handing on. |

## Two rules worth knowing before you extend it

**A template may supply structure and never a claim.** Headings and the labels of facts arrive
written, because those are true whoever is writing. Every value is left empty. Most people publish
what they were given, so a template that pre-fills "Open 06:00 to 20:00" produces a network of shops
with invented opening hours.

**An offer is a reason to look, never a reason to believe.** When somebody hands you a card, what
crosses the wire is an address. The card behind it is fetched and verified like anything else.

## Licence

MIT. The bundled typefaces (Instrument Serif, Newsreader) are SIL Open Font License 1.1 — see
`wwwroot/fonts/OFL-1.1.txt`.
