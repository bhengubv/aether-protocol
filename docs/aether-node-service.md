# The Aether Node Service

**Status:** Draft — the bind contract (`AetherNet.Node`) has landed; the host, client SDK and platform binding are proposed
**License:** MIT
**Owner:** The Other Bhengu (Pty) Ltd t/a The Geek Network

The **Aether Node Service** is AetherNet packaged the way a platform runtime is
packaged: a single, standalone, user-installed service that owns the device's
one node identity and its mesh transports, and that every other app *binds to*
rather than embeds.

Think of how DirectX, Android System WebView, or **OpenKeychain** ship — a
component installed once that many apps depend on and call into. AetherNet ships
the same way: one APK *is* the node; every messenger, market app, or tool on the
device is a thin client that asks the node to sign, address, and send on its
behalf. The private key is minted once, lives in one place, and never crosses
into any consumer app.

---

## 1. Why — the "one person, many tags" problem

AetherNet already states the rule that a device has exactly one node identity
(PROTOCOL_SPEC §7): one Ed25519 key, one UHID, one Aether Tag, addressable as
`aether://<tag>` (see [`aether-uri-scheme.md`](aether-uri-scheme.md)).

But nothing *structurally* enforces it. Today every app links the full AetherNet
stack in-process, so every app:

- mints **its own** Ed25519 key in **its own** secret store, and therefore
- presents a **different** Aether Tag.

The same human is then several unrelated nodes on one phone — `KXJB7-…` in the
messenger, `9QF2M-…` in the market app — and a contact who "added you" in one app
cannot reach you in another. The identity-portability primitive
(`INodeIdentityRecovery`, see §5) lets a key be *carried* between stores, but it
cannot stop an app from minting a fresh one. Convention is not a guarantee.

The Node Service removes the ability to diverge. If consumer apps carry **no**
key-minting stack — only a client that binds to the one installed node — then
every app on the device presents the **same tag by construction**. You could not
obtain a second identity if you tried.

---

## 2. Design principles

| # | Principle | Consequence |
|---|-----------|-------------|
| 1 | One device, one node, one tag — *by construction* | Only the Node Service can mint. Consumer apps hold no key material and cannot create an identity. |
| 2 | The key never leaves the service | Consumers get `Sign` (bytes in, signature out), the tag, and send/receive — never the private key, and never a derived key. A *subset* of `INodeIdentity`'s closed surface. |
| 3 | User-authorized, never silent | Installing the node, and each app's access to it, is an explicit user grant. This is not mandatory middleware. |
| 4 | Installable offline | A missing node can be installed peer-to-peer (Touch My Blood) — no store, no Google, no internet. |
| 5 | Apps are thin clients | A consumer declares intent ("I need the mesh") and binds. All protocol logic stays in the service. |
| 6 | Transport stays hidden | Consumers never choose a radio; the service negotiates the best per-peer link. |

---

## 3. The packaging model

AetherNet is distributed as its **own APK** — a standalone app/service — and
consumer apps have a *depends-on* relationship to it rather than a *contains*
relationship. This is a well-trodden pattern; the precedents differ mainly in
how sovereign they are.

| Precedent | What it proves | How ours differs |
|-----------|----------------|------------------|
| **OpenKeychain** (OpenPGP API) | A standalone FOSS app can hold private keys while other apps bind for crypto, with per-app user consent and the key never leaving. | Ours owns *transport* (the mesh), not only crypto. |
| Android System WebView | A shared component many apps render through, updated in one place. | Ours is user-authorized and offline-installable, not a mandatory system package. |
| Google Play Services | The *shape*: an installed service apps depend on. | The anti-pattern on sovereignty — proprietary, mandatory, Google. We are the deliberate opposite. |
| DirectX redistributable | The desktop precedent: a runtime installed once, apps link a stable API. | Cross-app IPC, not merely a local DLL load. |

**OpenKeychain is the precedent that matters.** It demonstrates the sovereign,
degoogled version of this model working in production: keys in one app, other
apps bound for operations, the user in control of every grant. The Node Service
is that, extended from "sign/decrypt" to "sign, address, and reach the mesh".

---

## 4. Lifecycle: detect → authorize → install → bind

A consumer app that wants the mesh moves through:

1. **Detect** — is a compatible Node Service installed? (Query the bind surface
   / package.)
2. **Offer** — if not, the app explains and offers to enable AetherNet. Off by
   default; nothing happens without the user.
3. **Authorize + install** — the user consents; the node APK is installed. NFC
   ("Touch My Blood") is the near-field *pointer*, not the payload — a tap is far
   too narrow to carry a 54 MB APK, so it hands over an NDEF URI and the bytes
   follow one of two ways:
   - **From a reachable distribution** — the tapped URL serves the node APK, and
     the OS image itself, from one endpoint. The reference Circle OS deployment
     serves both at `nfc.circleos.co.za`; any distribution endpoint works, and
     the fetch can ride the mesh through a gateway peer when there is no direct
     internet.
   - **Fully peer-to-peer** — when nothing is reachable at all, the tap bootstraps
     a Wi-Fi Direct link and the two phones transfer the APK directly: no store,
     no internet, no Google.

   Installing an APK is always a user action — the app requests, the user
   approves, the app never installs silently.
4. **Grant** — on first bind the user authorizes *this app* to link by clearing
   a local-auth gate — **biometric, pattern, or code**. The grant is per-app and
   revocable, the way OpenKeychain grants an app access to a key.
5. **Bind** — the app connects and uses the contract in §5. The node is already
   running, or the service starts on demand.

A consumer must render each state honestly: node **absent**, install
**declined**, **awaiting-grant**, **bound**, grant **revoked**. No state pretends
the node is present when it is not.

---

## 5. The bind contract

The contract is the existing in-process identity and mesh interfaces projected
across a process boundary. What crosses and what does not is the whole point.

**Exposed** — the consumer may call:

- **Identity** (read + use, never extract): the node's Aether Tag / UHID /
  public key; `Sign(bytes)` — bytes in, signature out, the key stays. This is a
  *subset* of `INodeIdentity`'s closed surface. `DeriveKeyAsync` is deliberately
  **not** exposed: it returns purpose-bound key material (e.g. the ERID-routing
  key), and a consumer holding it could compute the node's rotating wire address,
  so any derived-key operation runs *inside* the node, never in a bound app.
  There is no `GetPrivateKey` — the interface never had one.
- **Messaging**: send / receive addressed by Aether Tag. `IMessagingService`
  addresses by **UHID string** internally — the tag `Value`, or a rotating ERID
  the node resolves via `IWireAddressResolver` — so the client passes tags and the
  node resolves them; the service owns the Signal session state so one pair keeps
  **one** ratchet across every app that talks to that peer.
- **Presence / connectivity** (read-only): is the node linked, over which radio,
  how many radios are up — a `NodeLinkStatus` synthesized from the SDK's
  `IMeshLink` / `MeshWebService` / `RadioChoice`. (There is no single `IRadioMesh`
  in the SDK; that name is a sample-app type.) A report, not a picker.

**Held inside the service** — never crosses the boundary:

- The 32-byte Ed25519 private key and its secret store.
- Recovery / portability (`INodeIdentityRecovery`): export-phrase, restore,
  adopt-seed — these touch key material and stay behind the user-auth wall,
  performed *in the node app*, never by a consumer.
- Radio selection and transport negotiation.

**Per-app authorization.** The service records which apps hold a grant; a grant
is revocable; a revoked app falls back to *awaiting-grant*. Signing on behalf of
an app is attributable to that app.

**The user unlocks the node.** The private key is reachable only after the user
clears a local-auth gate — **biometric, pattern, or code** — so linking an app,
and every privileged operation (sign, export, adopt), happens *behind that gate*.
The code already models the locked state: `INodeIdentityRecovery` throws
`NodeIdentityUnavailableException` ("this phone is locked — unlock and try
again") rather than serving a key. A *distinct duress code* triggers the panic
wipe (§8) instead of unlocking.

```csharp
// The platform-neutral surface a bound consumer sees (src/AetherNet.Node/).
// A subset of INodeIdentity + a messaging/presence slice: no key access, no
// key-derivation, no recovery. Task (not ValueTask) and a callback interface
// (not a C# event) so every member proxies across a process boundary.
public interface IAetherNodeClient
{
    Task<AetherNetTag> GetTagAsync(CancellationToken ct = default);
    Task<byte[]> GetPublicKeyAsync(CancellationToken ct = default);
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken ct = default);

    Task<NodeLinkStatus> GetLinkAsync(CancellationToken ct = default);   // linked?, radio, radios-up
    IDisposable Subscribe(IAetherNodeEvents listener);                   // inbound + link + grant changes
}
```

---

## 6. Identity guarantees

- **Same tag everywhere.** Every bound app resolves the same Aether Tag because
  there is one key and one minter. `aether://<tag>` names the *device*, not an app.
- **Mint once.** The service mints on first run (or adopts a restored / handed-off
  seed); consumers never mint.
- **Recovery in a single place.** Back-up phrase, restore, and silent
  same-signature hand-off all happen in the node app over the one store — so a
  restored device reproduces the exact tag every app already knew.

---

## 7. Platform mapping & scope boundary

- **Android:** the Node Service is an installed app exposing a bound `Service`
  (AIDL) or a `ContentProvider` as its bind surface; consumers bind with an
  explicit intent and the user's per-app grant. Cross-app calls are Android IPC.
- **Other platforms:** the same contract; the host mechanism differs (a desktop
  service over a named pipe; an iOS app-group + XPC where the sandbox permits).
  The contract is platform-neutral; the host is platform code.

What **aether-protocol** ships (this repo):

- the **bind contract** — the platform-neutral interface, its DTOs, and a
  versioned handshake;
- a **reference Node Service host** — the sample app grown into the standalone
  node that exposes the contract;
- a **client SDK** — `detect → offer → (install) → grant → bind`, with the
  honest state machine of §4.

What lives **downstream / in the OS** (not this repo):

- the decision of *when* a given app enables AetherNet;
- Circle OS choosing to preinstall or privilege the node;
- any consumer app's own UI around the enable / grant flow.

This is the repo's existing split taken to its conclusion: the product is the SDK
in `src/AetherNet.*`, the sample app is a thin client. The Node Service simply
makes the thin client and the runtime **different installed processes** instead
of one fused app.

---

## 8. Security & privacy

- The private key never crosses the bind boundary; a compromised consumer app
  cannot exfiltrate the identity — only ask it to sign, within that app's grant.
- Every app's access is an explicit, revocable user grant; installing the node is
  a user action.
- The key lives behind a local-auth wall — biometric, pattern, or code. Nothing
  signs, links, or exports until the user clears it; a separate duress code wipes
  instead of unlocking.
- A duress / panic wipe centralizes too: erasing the one node's key and store
  revokes every app at once, because they only ever borrowed it.
- A request arriving from a consumer is *data*: the service applies its own auth,
  rate, and grant checks. An app cannot instruct the node outside its grant.

---

## 9. Reference implementation status

Honest state today (2026-09-18).

**Exists in-process** (the raw material this design rearranges):

- `INodeIdentity` / `INodeIdentityStore` / `NodeIdentity` — the closed identity
  surface and the mint-once-adopt-forever store.
- `INodeIdentityRecovery` / `NodeIdentityRecovery` — export / restore / adopt-seed
  portability.
- `IMeshLink` / `MeshWebService` / `RadioChoice` (there is no SDK `IRadioMesh`),
  `IMessagingService`, and the per-link transport negotiation — the mesh the
  service would own.
- The sample app hosts all of the above **in its own process** — today it is a
  consumer *and* the runtime fused into one app, which is exactly what this
  design unfuses.

**Landed** (`AetherNet.Node` — contract only, `net9.0;net10.0`):

- the cross-process **bind contract** — `IAetherNodeClient` / `IAetherNodeEvents`,
  the DTOs (`NodeLinkStatus`, `InboundMessage`, `OutboundResult`), the per-app
  **grant** model (`GrantState` / `AppGrant`), the typed **error contract**
  (`AetherNodeErrorCode`, preserving *unavailable ≠ absent*), and a **versioned
  handshake** (`AetherNodeHello` / `Ack` + capability negotiation), pinned
  byte-identical by `tests/cross-language/node-fixtures.json` (28 tests green).

**Proposed** (not yet built):

- a reference **host** implementing `IAetherNodeClient` over the real SDK, in-process first;
- the Android bound-`Service` (AIDL) **cross-process host** + client proxy;
- the **client SDK** (detect / install / grant / bind) and the NFC-distribution wiring;
- the per-app **grant store** persistence and the biometric / pattern / code gate;
- the 8-language port of the contract + fixtures.

The contract compiles and its tests pass; the host and client remain to be built
against it.

---

## 10. Out of scope

- The wire / packet format (unchanged — PROTOCOL_SPEC).
- Aether Tag derivation (unchanged — `aether-uri-scheme.md` §3.2).
- Any downstream app's adoption timeline or UI.
- Money / value flows, which remain on the central pipeline regardless of how the
  node is packaged.

---
