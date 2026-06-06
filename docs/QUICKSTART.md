# Quickstart — wire Aether into your .NET app in 5 minutes

This guide takes you from a blank `Program.cs` to two nodes — Alice and Bob —
exchanging an end-to-end-encrypted message. Everything compiles against HEAD
(`b8b3d22`) of [`bhengubv/aether-protocol`](../) on .NET 10.

> Looking for the full architecture? See [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> Looking for what the crypto does and does not protect against? See
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Known limitations are tracked in
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. Install

The Aether libraries are not yet published on NuGet. For now, take a
`<ProjectReference>` to the local repo:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` transitively pulls in `AetherNet.Core`,
`AetherNet.Security`, `AetherNet.Messaging`, `AetherNet.Transport`, `AetherNet.Streaming`,
`AetherNet.Voice`, and `AetherNet.Content` — everything you need for the messaging
stack. `AetherNet.Storage` is a separate dependency only if you want disk-backed
persistence (see Section 6).

Once the package ships on NuGet, this becomes:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # optional, for persistence
```

The package APIs will not change between the project-reference flow and the
NuGet flow.

---

## 2. Wire it up — canonical full-stack registration

The DI extension `AddAetherNetProtocol(...)` returns a fluent builder. Each
capability is opt-in: a host that only needs routing chains `.AddRouting()`
and stops there. Below is the full stack a typical adopter wants.

```csharp
using AetherNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // host-side prerequisite for AddHealthChecks() below
builder.Services
    .AddAetherNetProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (registers ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // AODV-style RREQ/RREP + InMemoryRouteStore
    .AddDtn()                                // 72h store-and-forward custody + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // emergency flood
    .AddMessaging()                          // 1-to-1 encrypted messages, requires AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // in-memory simulator (replace with BLE / Wi-Fi Direct in production)
    .AddHealthChecks();                      // four protocol-level IHealthCheck registrations

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherNetProtocol` and every chained method are idempotent on the same
`IServiceCollection` — calling them twice does not double-register. The order
matters in one place: `AddMessaging()` throws `InvalidOperationException` if
either `AddSignalProtocol()` or `AddRouting()` was not called first.

The `InProcessTransport` is for tests and demos. In production you implement
`AetherNet.Transport.Abstractions.ITransportService` for your physical layer (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) and register an `IMeshSender` that
bridges packets onto it. The Routing/DTN/Messaging services then run unchanged
on top.

---

## 3. Establish a session

X3DH is asymmetric. The **initiator** processes a published bundle from the
**responder**; the responder's session auto-establishes when it receives the
initiator's first encrypted message (a "PreKey message").

```csharp
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle: identity key + signed pre-key + one one-time pre-key.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice processes the bundle. Four X25519 DHs run; the resulting root key
// seeds her Double Ratchet sending chain.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — auto-establishes on first received message
```

`PreKeyBundle` is a plain DTO. Hosts publish it however they like — directly
peer-to-peer over the mesh (`PreKeyRequest` / `PreKeyResponse` packet types,
see PROTOCOL_SPEC §2.5), via a backend directory, or hand-delivered. The
protocol does not mandate a transport for bundles.

---

## 4. Send and receive

The shortest end-to-end path (no DI, no routing, just the cipher):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

In production you wrap the ciphertext in a `MeshPacket`, sign it with
`PacketSigningService.SignPacketAsync`, and let `MessagingService.SendAsync`
handle routing, retries, and DTN fallback:

```csharp
using AetherNet.Messaging;
using AetherNet.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent has already been decrypted by the messaging layer.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> ciphertext exited via the mesh, DTN, or backend relay
// handed == false -> queued in the outbox; ProcessOutboxAsync will retry
```

`MessagingService` queues messages — never sends them in cleartext — when no
Signal session yet exists with the recipient. Subscribe to `SessionRequired`
to know when to fetch a peer's pre-key bundle and call
`alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. Two-node round trip in 50 lines

This is a runnable script. Copy into `Program.cs`, add a `<ProjectReference>`
to `AetherNet.Security.csproj` (which pulls in `AetherNet.Core` and the BCL
crypto), and `dotnet run`.

```csharp
using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle; Alice processes it. After this, Alice can encrypt
// to Bob; Bob's session auto-establishes when he decrypts Alice's first
// message (which carries X3DH metadata as a "PreKey message").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Production: serialize `outbound` (or wrap in a MeshPacket and call
// PacketSigningService.SignPacketAsync) and ship the bytes over your
// transport. The receiver reconstructs the EncryptedPayload and calls
// DecryptAsync. Here both nodes share a process so we just hand the
// record across.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (session is now live in both directions) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

Expected output:

```
Bob got: "hello bob"
Alice got: "ack"
```

For a richer end-to-end demo — including packet signing, multi-hop relay
through Charlie, MessagingService, and DTN custody fallback — run the bundled
console:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

The DTN custody step (Step 9 of the demo) is the canonical pattern for
production wiring: `MessagingService` + `RoutingService` + `DtnService`
composed against an `IMeshSender` adapter over the real transport.

---

## 6. Persistence (key-value store)

By default `SignalProtocolService` keeps every session, identity key, signed
pre-key, and one-time pre-key in process memory. A crash means: lost identity
(can't decrypt any prior session), lost OPK pool (responder X3DH starts
failing for new initiators), lost Double Ratchet state (forward secrecy is
intact but message ordering breaks).

`AetherNet.Storage.FileSystemKeyValueStore` is a minimal disk-backed
`IKeyValueStore` (one file per entry, atomic temp-file rename). Wire it
through the `KeyValue*Store` adapters:

```csharp
using AetherNet.Storage;
using AetherNet.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Plug the same KV store into BOTH adapters so identity, sessions, and
// pre-keys all survive a restart.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore is internal — KeyValueSignalSessionStore is also internal.
// In a Wave-3+ host, register the persistent-state-aware SignalProtocolService
// constructor through your composition root (or replace the default
// AddSignalProtocol() registration with your own factory).
```

`FileSystemKeyValueStore` is intentionally simple: no compaction, no
cross-key transactions, no encryption-at-rest. For encryption-at-rest layer
`EncryptedKeyValueStore` over the file system (or your own KV) and supply an
`IDataAtRestKeyProvider` — the host owns the key wrapper, not the protocol.

You can also register a non-default `IRouteStore`, `IDtnBundleStore`, and
`IMessageStore` against the DI container before chaining
`.AddRouting()` / `.AddDtn()` / `.AddMessaging()` — the builder uses
`TryAdd*` and respects whatever you put in the container first. The
`KeyValueRouteStore`, `KeyValueDtnBundleStore`, and `KeyValueMessageStore`
adapters in `AetherNet.Storage` cover those slots against any `IKeyValueStore`.

---

## 7. Observability

Aether ships first-class OpenTelemetry instrumentation. Subscribe to one
meter and one activity source — both are stable strings and the libraries
don't depend on any specific OTel SDK:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

What you get:

- **Counters**: `aethernet.messages.encrypted`, `aethernet.messages.decrypted`,
  `aethernet.signatures.validated`, `aethernet.signatures.rejected`,
  `aethernet.nonces.replayed`, `aethernet.timestamps.stale`,
  `aethernet.sessions.established`, `aethernet.ratchet.dh_steps`,
  `aethernet.route.requests_emitted`, `aethernet.route.replies_received`,
  `aethernet.route.cache_hits`, `aethernet.dtn.bundles_accepted`,
  `aethernet.dtn.bundles_delivered`, `aethernet.dtn.bundles_expired`,
  `aethernet.sos.broadcasts`, `aethernet.sos.rebroadcasts_suppressed`,
  `aethernet.messaging.messages_sent`, `aethernet.messaging.messages_queued`,
  `aethernet.messaging.dtn_fallback`.
- **Histograms** (ms): `aethernet.encrypt.latency`, `aethernet.decrypt.latency`,
  `aethernet.route.lookup_latency`, `aethernet.sign.verify_latency`.
- **Activities** with PII-sanitized UHID tags:
  `AetherNet.Encrypt`, `AetherNet.Decrypt`, `AetherNet.DhRatchet.Step`,
  `AetherNet.Sign.Packet`, `AetherNet.Verify.Packet`, plus routing and DTN spans.

When no listener is attached the hot paths allocate nothing — counter `Add`
degrades to a volatile read and `StartActivity` returns `null`.

The full instrument inventory and PII contract live in
`src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs`.

---

## 8. Health checks

`AddHealthChecks()` (the Aether builder method) registers four protocol-level
checks against the host's `HealthCheckService`. Each writes structured `data`
useful for dashboards.

| Check name                  | What it watches                                            | Healthy → Degraded → Unhealthy                                |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10 000 → ≥ 10 000 → ≥ 50 000 (defaults; tunable)             |
| `aether-dtn`                | active bundles in custody                                  | < 80% capacity → ≥ 80% → ≥ `DtnMaxBundlesPerNode`              |
| `aether-signal`             | available OPKs + active session count                      | OPK floor → unhealthy below `MinAvailableOpks` (default 10); session ceiling → degraded above 1 000 |
| `aether-messaging-outbox`   | pending outbox depth + growth between samples              | < 100 → ≥ 100 → ≥ 100 AND growing                              |

Tune via `AetherNetOptions.Routing`, `Dtn`, `Signal`, and `Messaging` bags. The
host must call `services.AddHealthChecks()` before the Aether builder's
`.AddHealthChecks()` for the registrations to be visible to
`MapHealthChecks(...)`.

---

## 9. Where to next

- **`docs/PROTOCOL_SPEC.md`** — wire format, routing, key exchange, DTN, full
  packet-type table, and the canonical `BuildSignableData` algorithm.
- **`docs/THREAT_MODEL.md`** — what the crypto defends against, what is
  explicitly out of scope, and the assumptions the security claims rely on.
- **`OPEN_ISSUES.md`** — known limitations, tracked roadmap items, and the
  C-language session-machinery gap.
- **`SECURITY.md`** — responsible-disclosure policy.
- **`samples/AetherNet.Demo.Console/Program.cs`** — runnable 9-step end-to-end
  walk-through. Step 9 (MessagingService + DTN) is the production wiring
  pattern.
- **`fixtures/signal/`** — cross-language test vectors. If you're porting
  Aether to another language, these are the byte-pinned outputs your
  implementation must match.

Found a bug? File it on GitHub. Found a vulnerability? See `SECURITY.md`.
