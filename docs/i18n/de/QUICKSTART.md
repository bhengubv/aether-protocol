# Schnellstart — Aether in 5 Minuten in Ihre .NET-App einbinden

Diese Anleitung führt Sie von einer leeren `Program.cs` zu zwei Knoten — Alice und Bob —,
die eine Ende-zu-Ende-verschlüsselte Nachricht austauschen. Alles kompiliert gegen HEAD
(`b8b3d22`) von [`bhengubv/aether-protocol`](../) auf .NET 10.

> Suchen Sie die vollständige Architektur? Siehe [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> Suchen Sie, was die Kryptographie schützt und was nicht? Siehe
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Bekannte Einschränkungen werden in
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md) verfolgt.

---

## 1. Installation

Die Aether-Bibliotheken sind noch nicht auf NuGet veröffentlicht. Verwenden Sie vorerst
einen `<ProjectReference>` auf das lokale Repository:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` zieht transitiv `AetherNet.Core`,
`AetherNet.Security`, `AetherNet.Messaging`, `AetherNet.Transport`, `AetherNet.Streaming`,
`AetherNet.Voice` und `AetherNet.Content` ein — alles, was Sie für den Messaging-Stack benötigen.
`AetherNet.Storage` ist eine separate Abhängigkeit, nur wenn Sie disk-gestützte
Persistenz möchten (siehe Abschnitt 6).

Sobald das Paket auf NuGet erscheint, wird daraus:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # optional, für Persistenz
```

Die Paket-APIs ändern sich zwischen dem Projekt-Referenz-Ablauf und dem
NuGet-Ablauf nicht.

---

## 2. Einrichten — kanonische Vollstack-Registrierung

Die DI-Erweiterung `AddAetherNetProtocol(...)` gibt einen Fluent-Builder zurück. Jede
Fähigkeit ist opt-in: Ein Host, der nur Routing benötigt, verkettet `.AddRouting()`
und hört dort auf. Unten ist der vollständige Stack, den ein typischer Anwender möchte.

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

`AddAetherNetProtocol` und jede verkettete Methode sind idempotent auf derselben
`IServiceCollection` — zweimaliges Aufrufen führt zu keiner Doppelregistrierung. Die Reihenfolge
ist an einer Stelle wichtig: `AddMessaging()` wirft `InvalidOperationException`, wenn
entweder `AddSignalProtocol()` oder `AddRouting()` nicht zuerst aufgerufen wurde.

Der `InProcessTransport` ist für Tests und Demos. In der Produktion implementieren Sie
`AetherNet.Transport.Abstractions.ITransportService` für Ihre physische Schicht (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) und registrieren einen `IMeshSender`, der
Pakete darauf überbrückt. Die Routing-/DTN-/Messaging-Dienste laufen dann unverändert
darüber.

---

## 3. Eine Sitzung aufbauen

X3DH ist asymmetrisch. Der **Initiator** verarbeitet ein veröffentlichtes Bundle vom
**Responder**; die Sitzung des Responders wird automatisch aufgebaut, wenn er die
erste verschlüsselte Nachricht des Initiators empfängt (eine „PreKey-Nachricht").

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

`PreKeyBundle` ist ein einfaches DTO. Hosts veröffentlichen es nach Belieben — direkt
peer-to-peer über das Mesh (`PreKeyRequest`/`PreKeyResponse`-Pakettypen,
siehe PROTOCOL_SPEC §2.5), über ein Backend-Verzeichnis oder persönlich übergeben. Das
Protokoll schreibt keinen Transport für Bundles vor.

---

## 4. Senden und Empfangen

Der kürzeste Ende-zu-Ende-Pfad (kein DI, kein Routing, nur die Cipher):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

In der Produktion verpacken Sie den Chiffretext in ein `MeshPacket`, signieren es mit
`PacketSigningService.SignPacketAsync` und lassen `MessagingService.SendAsync`
Routing, Wiederholungsversuche und DTN-Fallback übernehmen:

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

`MessagingService` stellt Nachrichten in die Warteschlange — sendet sie nie im Klartext —, wenn noch keine
Signal-Sitzung mit dem Empfänger besteht. Abonnieren Sie `SessionRequired`,
um zu erfahren, wann ein Pre-Key-Bundle eines Peers abgerufen und
`alice.ProcessPreKeyBundleAsync(...)` aufgerufen werden soll.

---

## 5. Zwei-Knoten-Hin-und-Rücklauf in 50 Zeilen

Dies ist ein ausführbares Skript. In `Program.cs` einfügen, einen `<ProjectReference>`
auf `AetherNet.Security.csproj` hinzufügen (der `AetherNet.Core` und die BCL-Krypto einzieht),
und `dotnet run` ausführen.

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

Erwartete Ausgabe:

```
Bob got: "hello bob"
Alice got: "ack"
```

Für eine umfangreichere Ende-zu-Ende-Demo — einschließlich Paketsignierung, Multi-Hop-Relay
durch Charlie, MessagingService und DTN-Custody-Fallback — führen Sie die mitgelieferte
Konsole aus:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Der DTN-Custody-Schritt (Schritt 9 der Demo) ist das kanonische Muster für
die Produktionsverdrahtung: `MessagingService` + `RoutingService` + `DtnService`
zusammengesetzt gegen einen `IMeshSender`-Adapter über den echten Transport.

---

## 6. Persistenz (Schlüssel-Wert-Speicher)

Standardmäßig hält `SignalProtocolService` jede Sitzung, jeden Identitätsschlüssel, signierten
Pre-Key und Einmal-Pre-Key im Prozessspeicher. Ein Absturz bedeutet: verlorene Identität
(keine vorherige Sitzung entschlüsselbar), verlorener OPK-Pool (Responder-X3DH beginnt
für neue Initiatoren zu versagen), verlorener Double-Ratchet-Zustand (Forward Secrecy ist
intakt, aber die Nachrichtenreihenfolge bricht zusammen).

`AetherNet.Storage.FileSystemKeyValueStore` ist ein minimaler disk-gestützter
`IKeyValueStore` (eine Datei pro Eintrag, atomares Temp-Datei-Umbenennen). Verdrahten Sie ihn
durch die `KeyValue*Store`-Adapter:

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

`FileSystemKeyValueStore` ist absichtlich einfach: keine Komprimierung, keine
sitzungsübergreifenden Transaktionen, keine Verschlüsselung im Ruhezustand. Für die Verschlüsselung im Ruhezustand
`EncryptedKeyValueStore` über das Dateisystem (oder Ihr eigenes KV) schichten und einen
`IDataAtRestKeyProvider` bereitstellen — der Host besitzt den Schlüssel-Wrapper, nicht das Protokoll.

Sie können auch einen nicht standardmäßigen `IRouteStore`, `IDtnBundleStore` und
`IMessageStore` gegen den DI-Container registrieren, bevor
`.AddRouting()`/`.AddDtn()`/`.AddMessaging()` verkettet wird — der Builder verwendet
`TryAdd*` und respektiert, was Sie zuerst in den Container gestellt haben. Die
`KeyValueRouteStore`-, `KeyValueDtnBundleStore`- und `KeyValueMessageStore`-
Adapter in `AetherNet.Storage` decken diese Slots gegen jeden `IKeyValueStore` ab.

---

## 7. Beobachtbarkeit

Aether wird mit erstklassiger OpenTelemetry-Instrumentierung geliefert. Abonnieren Sie einen
Meter und eine Aktivitätsquelle — beide sind stabile Strings, und die Bibliotheken
hängen von keinem spezifischen OTel-SDK ab:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

Was Sie erhalten:

- **Zähler**: `aethernet.messages.encrypted`, `aethernet.messages.decrypted`,
  `aethernet.signatures.validated`, `aethernet.signatures.rejected`,
  `aethernet.nonces.replayed`, `aethernet.timestamps.stale`,
  `aethernet.sessions.established`, `aethernet.ratchet.dh_steps`,
  `aethernet.route.requests_emitted`, `aethernet.route.replies_received`,
  `aethernet.route.cache_hits`, `aethernet.dtn.bundles_accepted`,
  `aethernet.dtn.bundles_delivered`, `aethernet.dtn.bundles_expired`,
  `aethernet.sos.broadcasts`, `aethernet.sos.rebroadcasts_suppressed`,
  `aethernet.messaging.messages_sent`, `aethernet.messaging.messages_queued`,
  `aethernet.messaging.dtn_fallback`.
- **Histogramme** (ms): `aethernet.encrypt.latency`, `aethernet.decrypt.latency`,
  `aethernet.route.lookup_latency`, `aethernet.sign.verify_latency`.
- **Aktivitäten** mit PII-bereinigten UHID-Tags:
  `AetherNet.Encrypt`, `AetherNet.Decrypt`, `AetherNet.DhRatchet.Step`,
  `AetherNet.Sign.Packet`, `AetherNet.Verify.Packet`, sowie Routing- und DTN-Spans.

Wenn kein Listener angehängt ist, allozieren die Hot Paths nichts — `counter.Add`
degradiert zu einem volatilen Lesezugriff und `StartActivity` gibt `null` zurück.

Das vollständige Instrumentenverzeichnis und der PII-Vertrag befinden sich in
`src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs`.

---

## 8. Health Checks

`AddHealthChecks()` (die Aether-Builder-Methode) registriert vier protokollstufen
Prüfungen gegen den `HealthCheckService` des Hosts. Jede schreibt strukturierte `data`,
nützlich für Dashboards.

| Prüfungsname | Was überwacht wird | Gesund → Degradiert → Ungesund |
|---|---|---|
| `aether-routing` | `IRoutingService.GetAllRoutes().Count` | < 10 000 → ≥ 10 000 → ≥ 50 000 (Standardwerte; einstellbar) |
| `aether-dtn` | aktive Bundles in Custody | < 80 % Kapazität → ≥ 80 % → ≥ `DtnMaxBundlesPerNode` |
| `aether-signal` | verfügbare OPKs + aktive Sitzungsanzahl | OPK-Boden → ungesund unter `MinAvailableOpks` (Standard 10); Sitzungsdecke → degradiert über 1 000 |
| `aether-messaging-outbox` | ausstehende Outbox-Tiefe + Wachstum zwischen Abtastungen | < 100 → ≥ 100 → ≥ 100 UND wachsend |

Abstimmen über `AetherNetOptions.Routing`-, `Dtn`-, `Signal`- und `Messaging`-Bags. Der
Host muss `services.AddHealthChecks()` vor dem `AddHealthChecks()` des Aether-Builders
aufrufen, damit die Registrierungen für `MapHealthChecks(...)` sichtbar sind.

---

## 9. Wie geht es weiter

- **`docs/PROTOCOL_SPEC.md`** — Leitungsformat, Routing, Schlüsselaustausch, DTN, vollständige
  Pakettypentabelle und der kanonische `BuildSignableData`-Algorithmus.
- **`docs/THREAT_MODEL.md`** — Was die Kryptographie schützt, was ausdrücklich außerhalb des Geltungsbereichs liegt und welche Annahmen die Sicherheitsaussagen stützen.
- **`OPEN_ISSUES.md`** — Bekannte Einschränkungen, verfolgte Roadmap-Elemente und die
  C-Sprachen-Sitzungsmaschinerie-Lücke.
- **`SECURITY.md`** — Richtlinie zur verantwortungsvollen Offenlegung.
- **`samples/AetherNet.Demo.Console/Program.cs`** — Ausführbarer 9-Schritt-Ende-zu-Ende-
  Durchlauf. Schritt 9 (MessagingService + DTN) ist das Produktionsverdrahtungsmuster.
- **`fixtures/signal/`** — Sprachübergreifende Testvektoren. Wenn Sie Aether in eine andere Sprache portieren, sind dies die byte-gepinnten Ausgaben, die Ihre Implementierung erzeugen muss.

Einen Fehler gefunden? Melden Sie ihn auf GitHub. Eine Schwachstelle gefunden? Siehe `SECURITY.md`.
