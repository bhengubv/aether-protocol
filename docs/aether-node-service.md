# The Aether Node Service

**Status:** Built — the contract, host, client SDK and Android binding exist, and AetherNetService runs on two phones with Aether as its thin client (§9 for what is verified and what is open)
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
behalf. The private key is minted once and lives in one place. The one form in
which it crosses into an app is the 24-word recovery phrase, and only for a person
to write it down: the app asks for it after the phone has confirmed its owner (§5).

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
   too narrow to carry a 54 MB APK. It hands over the sending phone's Wi-Fi Direct
   network and then a link on that network, and the two phones transfer the APK
   directly: no store, no internet, no Google — and no server. A central download
   server is deliberately not a path. Every share is tens of megabytes, so one
   server carrying them all falls over on a busy day; and it would be the one
   centre in a network built not to have one.

   Touch My Blood hands over the consumer app alone, which is right. The app then
   asks for AetherNetService before anything else (`NodeInstallFlow`): it is found
   on SleptOn, The Geek Network's store, by package name; downloaded; checked to be
   AetherNetService signed with the same key as the app asking — a release's hash
   is not known ahead, the key is (`AndroidNodePackageVerifier`); and handed to the
   phone's own installer in an install session (`AndroidNodePackageInstaller`). A
   live release therefore always includes publishing AetherNetService to SleptOn.

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
  in the SDK; that name is a sample-app type.) A report, not a picker. Each radio
  says why it cannot be used, in plain words (`Reason`), whether the person can fix
  that (`Fixable`), and whether what is missing is the permission the phone keeps
  for the service (`NeedsPermission`) — see §7 for where that is granted. The same
  report lists those permissions (`Permissions`: each `ServicePermission` with its
  name as the phone shows it, whether it is allowed, and what it is for), so an app
  can show them; the service has no screen of its own to show them on.
- **Whom to meet**: the app hands over its contacts (`MeetAsync`). The service keeps
  no address book of its own; the people are the app's, and the radios only need to
  know whom to keep reachable.
- **The AetherNet switch**: any connected app can switch the nearby radios on or off
  for the whole device (`SetNearbyAsync`; the state is `NodeLinkStatus.NearbyOn`).
  Off, only the internet leg runs. It is the device's switch, like the cable the
  service is, so it holds for every app; the service keeps it in a file beside the
  identity and restarts to apply it, since a radio once stopped cannot start again in
  the same process. Connected apps reconnect by themselves.

**Held inside the service** — never crosses the boundary:

- The 32-byte Ed25519 private key and its secret store.
- Restore and adopt-seed (`INodeIdentityRecovery`). Restoring from an app is not
  built yet: the service mints on first start, and adopting over a live identity
  is refused by design, so restore needs its own path.
- Radio selection and transport negotiation.

**The one exception — the recovery phrase.** AetherNetService has no screen, so
it cannot show the 24 words itself. An app asks for them
(`GetRecoveryPhraseAsync`), only after the phone's own fingerprint, PIN or pattern
screen has confirmed its owner (`IOwnerCheck`; on Android `AndroidOwnerCheck`),
shows them, and keeps nothing. A phone with no screen lock is refused.

**Per-app authorization.** The contract carries a per-app grant model
(`GrantState` / `AppGrant` / `IGrantStore`), but AetherNetService admits every app
that binds to it (`OpenGrantStore`): it is a network cable with no gate of its own,
and no screen to approve on. The phone's lock is the gate (below).

**The phone's lock is the gate.** Security is upstream: AetherNetService keeps no
gate and no screen of its own — no access to the phone, no access to the service.
The phone's own **biometric, pattern, or code** is what stands in front of it, and
for the one operation that hands out key material (the recovery phrase) the asking
app puts that same check in front of the person first.
The code already models the locked state: `INodeIdentityRecovery` throws
`NodeIdentityUnavailableException` ("this phone is locked — unlock and try
again") rather than serving a key. A *distinct duress code* triggers the panic
wipe (§8) instead of unlocking.

```csharp
// The platform-neutral surface a bound consumer sees (src/AetherNetNodeService/).
// A subset of INodeIdentity + a messaging/presence slice: no key access, no
// key-derivation; the recovery phrase only on request, after the phone confirms
// its owner. Task (not ValueTask) and a callback interface (not a C# event) so
// every member proxies across a process boundary.
public interface IAetherNodeClient
{
    Task<AetherNetTag> GetTagAsync(CancellationToken ct = default);
    Task<byte[]> GetPublicKeyAsync(CancellationToken ct = default);
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken ct = default);
    Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken ct = default);
    Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken ct = default);   // whom to keep reachable

    Task<NodeLinkStatus> GetLinkAsync(CancellationToken ct = default);   // linked?, radio, each radio and why not
    Task<string> GetRecoveryPhraseAsync(CancellationToken ct = default); // the 24 words — owner confirmed first
    IDisposable Subscribe(IAetherNodeEvents listener);                   // inbound + link + grant + delivered
}
```

**Staying connected.** The app does not have to notice the service going away. On
Android, `BoundNodeClient` connects on first use and, when the service dies —
killed for memory, crashed, or updated — connects again by itself:

- a bind, and the first call on it, get 20 s; a binding Android declares dead
  (`OnBindingDied`, sent when the service's app is replaced) or empty
  (`OnNullBinding`) is dropped and tried again, instead of waited on forever;
- while anything is listening it keeps trying, waiting 1 s, then 2, 4 … up to 30 s.
  A service that dies soon after it is reached (within 30 s) is not restarted in a
  tight loop — the wait keeps growing until a connection lasts;
- every new connection takes every subscription with it and tells the new service
  the contacts it was last given, so messages, delivery receipts and the radios'
  meetings carry on;
- while the service cannot be reached, calls fail as `NodeUnavailable` — never as
  Android's `DeadObjectException`, and never as "absent", so an app is never tempted
  to mint an identity of its own. Nothing a caller keeps (such as the device's tag)
  may remember that failure: the next ask asks again.

---

## 6. Identity guarantees

- **Same tag everywhere.** Every bound app resolves the same Aether Tag because
  there is one key and one minter. `aether://<tag>` names the *device*, not an app.
- **Mint once.** The service mints on first run (or adopts a restored / handed-off
  seed); consumers never mint.
- **Recovery in a single place.** The back-up phrase comes out of the one store in
  the service (shown by the app that asked); restore and silent same-signature
  hand-off stay in the service — so a restored device reproduces the exact tag
  every app already knew.

---

## 7. Platform mapping & scope boundary

- **Android:** the Node Service is **AetherNetService** — its own app,
  `com.bhengubv.aethernetservice`, with no launcher and no screen. It exposes one
  exported bound `Service`, `com.bhengubv.aethernet.service` (action
  `com.bhengubv.aethernet.service.BIND`), declared only by AetherNetService; a
  consumer declares none. Calls are Binder transactions carrying `NodeWire`
  payloads (`AetherNetNodeService.Ipc`), not AIDL.
  - A consumer targeting Android 11+ must declare the service in its manifest's
    `<queries>` (`<package android:name="com.bhengubv.aethernetservice"/>` and the
    `BIND` action), or Android hides it: the bind is refused as "BLOCKED" and the
    consumer sees no service at all.
  - The radios' runtime permission belongs to AetherNetService, and a service with
    no screen can never show the phone's "Allow?" prompt; the phone keeps permissions
    per app, so a consumer cannot grant it either. The consumer shows the
    service's permissions from `NodeLinkStatus.Permissions` — always, not only while
    one is missing — and each opens AetherNetService's own page in the phone's
    settings (`IAetherNetServiceSettings`; on Android
    `AndroidAetherNetServiceSettings`), where the person allows it once — "Nearby
    devices" on Android 13+, "Nearby devices and Location" on 12, "Location" before;
    "Notifications" from 13. Until then only the Wi-Fi the phone is already on carries
    traffic.
  - The phone tells an app nothing when one of its permissions is allowed, so while
    one is missing the service looks again every 3 s (`PermissionWatch`); when it is
    allowed, the service brings up the radio it held back — no restart — and pushes
    the new report to every connected app.
  - Two more keep the service running, and the list carries them with the page each
    is changed on (`ServicePermission.Page`). "Battery": the consumer raises the
    phone's own prompt to let AetherNetService always run in the background — the
    phone only checks that AetherNetService declares
    `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`, not which app asks, so no screen is
    needed. "App launch", on Huawei and Honor phones: the maker's page for whether the
    phone may start AetherNetService again after stopping it. The phone does not say
    how that is set (`Known` is false), so the consumer says so and opens the page.
  - A radio never touches its stack without that permission — it reports what it
    needs and stays off. Leaving it to the stack to refuse was not safe: on Android 16
    a Bluetooth GATT server opened without permission sometimes crashed the whole
    service instead of throwing.
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

- The private key never crosses the bind boundary as a key. It does cross as the
  24-word recovery phrase, to an app that asks. The service cannot see whether that
  app confirmed the owner first — it has no screen to check with — so the
  protection is the phone itself: only an app installed on the phone, on an
  unlocked phone, can ask. That is the trade for a service with no UI.
- Every app that can bind is admitted — there is no per-app approval step (§5).
  What stands in front of the service is the phone itself: only an app installed on
  it, on an unlocked phone, can reach it. Installing the node is a user action.
- The phone's lock is the wall — biometric, pattern, or code. A separate duress
  code wipes instead of unlocking.
- A duress / panic wipe centralizes too: erasing the one node's key and store
  revokes every app at once, because they only ever borrowed it.
- A request arriving from a consumer is *data*: the service applies its own auth,
  rate, and grant checks. An app cannot instruct the node outside its grant.

---

## 9. Reference implementation status

Honest state today (2026-09-30).

**Built** — the design is no longer a proposal; the runtime and the app are two
installed processes:

- `AetherNetNodeService` — the **bind contract** (`IAetherNodeClient` /
  `IAetherNodeEvents`, the DTOs, the grant model, the typed error contract
  preserving *unavailable ≠ absent*, the versioned handshake), pinned by
  `tests/cross-language/node-fixtures.json`.
- `AetherNetNodeService.Ipc` — `NodeWire`, the Binder payload codec. Fields added
  later are optional, so an older service still decodes.
- `AetherNetNodeService.Host` — `AetherNodeService`, the host over the real SDK
  (identity, messaging, link, meeting, recovery), and `MeetingHost`, which picks the
  contact the pair-by-pair radios point at: the lowest-sorting one who is actually
  here.
- `AetherNetNodeService.Android` — the bound service (`AetherNodeAndroidService`,
  `NodeServiceBinder`), the consumer side (`BinderNodeClient`, `BoundNodeClient`,
  `AndroidNodeConnector`), `AndroidOwnerCheck` and `AndroidAetherNetServiceSettings`.
- `AetherNetNodeService.Client` — `NodeBinder` (detect → install → bind),
  `NodeBackedMessaging`, `NodeClientIdentity`, `NodeClientRecovery`, `IOwnerCheck`,
  `IAetherNetServiceSettings`.
- `src/AetherNetService` — **AetherNetService**, the service app: no UI; it owns the
  identity, the radios and the Signal sessions.
- The sample app (**Aether**) is now a thin client of it.

**Verified on two phones** (Huawei P30 lite, Android 10; a Pixel on Circle OS,
Android 16), 2026-09-30:

- chat through AetherNetService on each phone, both ways, over the Wi-Fi both are
  on — received and confirmed delivered in about 3 s each way; 15 messages each way
  at once, all 30 received once and confirmed delivered in 7 s;
- an open Aether reconnects by itself when AetherNetService is updated underneath
  it, and hands the new service its contacts — same Aether process before and after,
  on both phones;
- the Wi-Fi link stays up while something else on the network connects to the
  meeting port and hangs up (an adb port scan, once a minute: 181 of 181 seconds up);
- the phone's own fingerprint / PIN / pattern sheet comes up before the recovery
  words, and cancelling it shows nothing (P30).

**Verified on the P30**, 2026-10-02:

- "Let AetherNet find phones nearby" in Aether's Settings said "allow Location for
  AetherNetService" and opened AetherNetService's App info page; once Location was
  allowed there, and the service restarted, 4 of its 7 radios were up instead of 2,
  and the line went away;
- AetherNetService shows Aether's logo on its App info page;
- Aether's Settings list AetherNetService's permissions at all times — Location
  allowed, Battery not, App launch not known;
- the AetherNet switch: off, the service logged "nearby radios switched off —
  restarting to apply it", was started again 80 ms later, and logged "internet only, no
  nearby radio", and Aether showed "AetherNet is off — internet only, for every app on
  this phone"; on again, Wi-Fi Direct was linking and Bluetooth listening 3 s later;
- "Battery" brought up the phone's own "Ignore battery optimizations? Allow the app
  AetherNetService to stay connected in the background?" over Aether;
- "App launch" opened the phone's Battery page, with App launch on it — Huawei's own
  App launch page refuses other apps on EMUI 10 (`signature|privileged`);
- Aether asking for AetherNetService, against a stand-in for SleptOn on the dev PC
  (Debug-only: offered once on a phone that has it, so the install lands as an
  update): the wrong app was refused and never reached the installer; the real one
  came down (38 MB, 8 s), passed the signing check, and the phone's installer
  opened — first asking to allow installs from Aether.

**Built, not yet run on a phone:**

- radios asking for their permission before touching their stacks (the fix for
  AetherNetService dying on some cold starts on the Pixel);
- Aether recovering when AetherNetService dies as it starts;
- a permission allowed on the phone bringing its radio up without a restart
  (`PermissionWatch`);
- the AetherNet switch in the setup wizard.

**Open:**

- Bluetooth, Wi-Fi Direct and Wi-Fi Aware stay off until AetherNetService is given its
  permission once (§7); until then two phones reach each other only on the same Wi-Fi.
- On the P30, EMUI's low-memory killer stops AetherNetService even while it runs as a
  foreground service (four times in five minutes on 2026-10-02, with the phone short of
  memory), and once no app is bound to it nothing starts it again — the service asks
  to be restarted (`START_STICKY`), and EMUI does not. Aether now offers "Battery"
  and "App launch" for it; whether those keep it running there is not yet shown.
- AetherNetService is not on SleptOn yet, so Aether's request for it (§4) has only
  run against a stand-in; and not yet on a phone that truly lacks the service.
- Restoring from the 24 words through an app: the service mints on first start, and
  adopting over a live identity is refused by design, so restore needs its own path.
- The 8-language port of the contract and fixtures.

---

## 10. Out of scope

- The wire / packet format (unchanged — PROTOCOL_SPEC).
- Aether Tag derivation (unchanged — `aether-uri-scheme.md` §3.2).
- Any downstream app's adoption timeline or UI.
- Money / value flows, which remain on the central pipeline regardless of how the
  node is packaged.

---
