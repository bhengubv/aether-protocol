# Démarrage Rapide — intégrer Aether dans votre application .NET en 5 minutes

Ce guide vous emmène d'un `Program.cs` vide à deux nœuds — Alice et Bob —
échangeant un message chiffré de bout en bout. Tout compile sur HEAD
(`b8b3d22`) de [`bhengubv/aether-protocol`](../) sur .NET 10.

> Vous cherchez l'architecture complète ? Voir [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> Vous cherchez ce que le chiffrement protège et ne protège pas ? Voir
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Les limitations connues sont suivies dans
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. Installation

Les bibliothèques Aether ne sont pas encore publiées sur NuGet. Pour l'instant, utilisez une
`<ProjectReference>` vers le dépôt local :

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/Aether.DependencyInjection/Aether.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/Aether.Storage/Aether.Storage.csproj" />
</ItemGroup>
```

`Aether.DependencyInjection` tire transitivement `Aether.Core`,
`Aether.Security`, `Aether.Messaging`, `Aether.Transport`, `Aether.Streaming`,
`Aether.Voice`, et `Aether.Content` — tout ce dont vous avez besoin pour la pile
de messagerie. `Aether.Storage` est une dépendance séparée uniquement si vous souhaitez
une persistance sur disque (voir Section 6).

Une fois le paquet publié sur NuGet, cela devient :

```bash
dotnet add package Aether.DependencyInjection
dotnet add package Aether.Storage   # optionnel, pour la persistance
```

Les APIs du paquet ne changeront pas entre le flux de référence de projet et le
flux NuGet.

---

## 2. Câblage — enregistrement complet de la pile canonique

L'extension DI `AddAetherProtocol(...)` retourne un constructeur fluent. Chaque
capacité est opt-in : un hôte qui n'a besoin que du routage chaîne `.AddRouting()`
et s'arrête là. Voici la pile complète qu'un adoptant typique souhaite.

```csharp
using Aether.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // prérequis côté hôte pour AddHealthChecks() ci-dessous
builder.Services
    .AddAetherProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (enregistre ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // RREQ/RREP de style AODV + InMemoryRouteStore
    .AddDtn()                                // garde de stockage-et-transmission différée 72h + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // inondation d'urgence
    .AddMessaging()                          // messages chiffrés 1-à-1, nécessite AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // simulateur en mémoire (remplacer par BLE / Wi-Fi Direct en production)
    .AddHealthChecks();                      // quatre enregistrements IHealthCheck au niveau protocole

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherProtocol` et chaque méthode chaînée sont idempotentes sur le même
`IServiceCollection` — les appeler deux fois ne provoque pas de double enregistrement. L'ordre
compte en un endroit : `AddMessaging()` lance `InvalidOperationException` si
`AddSignalProtocol()` ou `AddRouting()` n'a pas été appelé d'abord.

`InProcessTransport` est destiné aux tests et démos. En production, vous implémentez
`Aether.Transport.Abstractions.ITransportService` pour votre couche physique (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) et enregistrez un `IMeshSender` qui
transfère les paquets vers celle-ci. Les services Routage/DTN/Messagerie s'exécutent alors inchangés
par-dessus.

---

## 3. Établir une session

X3DH est asymétrique. L'**initiateur** traite un bundle publié par le
**répondeur** ; la session du répondeur s'établit automatiquement quand il reçoit
le premier message chiffré de l'initiateur (un "message PreKey").

```csharp
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publie un bundle : clé d'identité + clé pré-signée + une clé à usage unique.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice traite le bundle. Quatre DH X25519 s'exécutent ; la clé racine résultante
// amorce sa chaîne d'envoi Double Ratchet.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — s'établit automatiquement au premier message reçu
```

`PreKeyBundle` est un DTO simple. Les hôtes le publient comme bon leur semble —
directement de pair à pair sur le maillage (types de paquets `PreKeyRequest` / `PreKeyResponse`,
voir PROTOCOL_SPEC §2.5), via un annuaire backend, ou remis en main propre. Le
protocole ne impose pas de transport pour les bundles.

---

## 4. Envoyer et recevoir

Le chemin de bout en bout le plus court (sans DI, sans routage, juste le chiffreur) :

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Acheminer le texte chiffré via votre transport. Chez Bob :
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

En production, vous enveloppez le texte chiffré dans un `MeshPacket`, le signez avec
`PacketSigningService.SignPacketAsync`, et laissez `MessagingService.SendAsync`
gérer le routage, les nouvelles tentatives et le basculement DTN :

```csharp
using Aether.Messaging;
using Aether.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent a déjà été déchiffré par la couche de messagerie.
    Console.WriteLine($"De {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("bonjour d'Alice"));
// handed == true  -> le texte chiffré est sorti via le maillage, le DTN, ou le relais backend
// handed == false -> mis en file d'attente dans la boîte d'envoi ; ProcessOutboxAsync fera de nouvelles tentatives
```

`MessagingService` met les messages en file d'attente — ne les envoie jamais en clair — quand aucune
session Signal n'existe encore avec le destinataire. Abonnez-vous à `SessionRequired`
pour savoir quand récupérer le bundle de pré-clés d'un pair et appeler
`alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. Aller-retour à deux nœuds en 50 lignes

Ceci est un script exécutable. Copiez dans `Program.cs`, ajoutez une `<ProjectReference>`
vers `Aether.Security.csproj` (qui tire `Aether.Core` et la crypto BCL),
et `dotnet run`.

```csharp
using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publie un bundle ; Alice le traite. Après ça, Alice peut chiffrer
// pour Bob ; la session de Bob s'établit automatiquement quand il déchiffre le premier
// message d'Alice (qui porte les métadonnées X3DH comme un "message PreKey").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Production : sérialisez `outbound` (ou enveloppez dans un MeshPacket et appelez
// PacketSigningService.SignPacketAsync) et envoyez les octets via votre
// transport. Le récepteur reconstruit EncryptedPayload et appelle
// DecryptAsync. Ici les deux nœuds partagent un processus donc on passe juste
// l'enregistrement directement.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob a reçu : \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (la session est maintenant active dans les deux sens) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice a reçu : \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

Sortie attendue :

```
Bob a reçu : "hello bob"
Alice a reçu : "ack"
```

Pour une démo de bout en bout plus riche — incluant la signature de paquets, le relayage
multi-sauts via Charlie, MessagingService et le basculement de garde DTN — exécutez la console fournie :

```bash
dotnet run --project samples/Aether.Demo.Console
```

L'étape de garde DTN (Étape 9 de la démo) est le schéma canonique pour
le câblage en production : `MessagingService` + `RoutingService` + `DtnService`
composés contre un adaptateur `IMeshSender` sur le vrai transport.

---

## 6. Persistance (magasin clé-valeur)

Par défaut, `SignalProtocolService` conserve chaque session, clé d'identité, clé
pré-signée et clé pré-partagée à usage unique en mémoire de processus. Un crash signifie : identité perdue
(impossible de déchiffrer toute session précédente), pool OPK perdu (le X3DH répondeur commence
à échouer pour les nouveaux initiateurs), état Double Ratchet perdu (la confidentialité persistante est
intacte mais l'ordre des messages est cassé).

`Aether.Storage.FileSystemKeyValueStore` est un `IKeyValueStore` minimal sur disque
(un fichier par entrée, renommage de fichier temporaire atomique). Câblez-le
via les adaptateurs `KeyValue*Store` :

```csharp
using Aether.Storage;
using Aether.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Branchez le même magasin KV dans LES DEUX adaptateurs pour que l'identité, les sessions, et
// les pré-clés survivent toutes à un redémarrage.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore est interne — KeyValueSignalSessionStore est aussi interne.
// Dans un hôte Wave-3+, enregistrez le constructeur SignalProtocolService conscient de l'état persistant
// via votre racine de composition (ou remplacez l'enregistrement AddSignalProtocol() par défaut par votre propre usine).
```

`FileSystemKeyValueStore` est intentionnellement simple : pas de compaction, pas de
transactions cross-clés, pas de chiffrement au repos. Pour le chiffrement au repos, superposez
`EncryptedKeyValueStore` sur le système de fichiers (ou votre propre KV) et fournissez un
`IDataAtRestKeyProvider` — l'hôte possède l'enveloppe de clé, pas le protocole.

Vous pouvez également enregistrer un `IRouteStore`, `IDtnBundleStore`, et
`IMessageStore` non par défaut dans le conteneur DI avant de chaîner
`.AddRouting()` / `.AddDtn()` / `.AddMessaging()` — le constructeur utilise
`TryAdd*` et respecte ce que vous avez mis dans le conteneur en premier. Les
adaptateurs `KeyValueRouteStore`, `KeyValueDtnBundleStore`, et `KeyValueMessageStore`
dans `Aether.Storage` couvrent ces emplacements contre n'importe quel `IKeyValueStore`.

---

## 7. Observabilité

Aether embarque une instrumentation OpenTelemetry de première classe. Abonnez-vous à un
compteur et une source d'activité — les deux sont des chaînes stables et les bibliothèques
ne dépendent d'aucun SDK OTel spécifique :

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Aether.Protocol"))
    .WithTracing(t => t.AddSource("Aether.Protocol"));
```

Ce que vous obtenez :

- **Compteurs** : `aether.messages.encrypted`, `aether.messages.decrypted`,
  `aether.signatures.validated`, `aether.signatures.rejected`,
  `aether.nonces.replayed`, `aether.timestamps.stale`,
  `aether.sessions.established`, `aether.ratchet.dh_steps`,
  `aether.route.requests_emitted`, `aether.route.replies_received`,
  `aether.route.cache_hits`, `aether.dtn.bundles_accepted`,
  `aether.dtn.bundles_delivered`, `aether.dtn.bundles_expired`,
  `aether.sos.broadcasts`, `aether.sos.rebroadcasts_suppressed`,
  `aether.messaging.messages_sent`, `aether.messaging.messages_queued`,
  `aether.messaging.dtn_fallback`.
- **Histogrammes** (ms) : `aether.encrypt.latency`, `aether.decrypt.latency`,
  `aether.route.lookup_latency`, `aether.sign.verify_latency`.
- **Activités** avec balises UHID dépersonnalisées :
  `Aether.Encrypt`, `Aether.Decrypt`, `Aether.DhRatchet.Step`,
  `Aether.Sign.Packet`, `Aether.Verify.Packet`, plus les spans de routage et DTN.

Quand aucun écouteur n'est attaché, les chemins chauds n'allouent rien — le compteur `Add`
se dégrade en lecture volatile et `StartActivity` retourne `null`.

L'inventaire complet des instruments et le contrat de données personnelles se trouvent dans
`src/Aether.Core/Diagnostics/AetherTelemetry.cs`.

---

## 8. Bilans de santé

`AddHealthChecks()` (la méthode du constructeur Aether) enregistre quatre vérifications au niveau protocole
contre le `HealthCheckService` de l'hôte. Chacune écrit des `data` structurées
utiles pour les tableaux de bord.

| Nom de la vérification              | Ce qu'elle surveille                                       | Sain → Dégradé → Défaillant                                   |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10 000 → ≥ 10 000 → ≥ 50 000 (par défaut ; réglable)         |
| `aether-dtn`                | bundles actifs en garde                                    | < 80% capacité → ≥ 80% → ≥ `DtnMaxBundlesPerNode`              |
| `aether-signal`             | OPK disponibles + nombre de sessions actives               | plancher OPK → défaillant sous `MinAvailableOpks` (par défaut 10) ; plafond de sessions → dégradé au-dessus de 1 000 |
| `aether-messaging-outbox`   | profondeur de la boîte d'envoi en attente + croissance entre échantillons | < 100 → ≥ 100 → ≥ 100 ET en croissance                        |

Réglez via les sacs `AetherOptions.Routing`, `Dtn`, `Signal`, et `Messaging`. L'hôte
doit appeler `services.AddHealthChecks()` avant le `.AddHealthChecks()` du constructeur Aether
pour que les enregistrements soient visibles par `MapHealthChecks(...)`.

---

## 9. Que faire ensuite

- **`docs/PROTOCOL_SPEC.md`** — format fil, routage, échange de clés, DTN,
  table complète des types de paquets, et l'algorithme canonique `BuildSignableData`.
- **`docs/THREAT_MODEL.md`** — ce que le chiffrement défend, ce qui est
  explicitement hors de portée, et les hypothèses sur lesquelles reposent les affirmations de sécurité.
- **`OPEN_ISSUES.md`** — limitations connues, éléments de feuille de route suivis, et l'écart
  du mécanisme de session en langage C.
- **`SECURITY.md`** — politique de divulgation responsable.
- **`samples/Aether.Demo.Console/Program.cs`** — présentation exécutable en 9 étapes de bout en bout.
  L'Étape 9 (MessagingService + DTN) est le schéma de câblage en production.
- **`fixtures/signal/`** — vecteurs de test cross-langages. Si vous portez
  Aether vers un autre langage, ce sont les sorties ancrées octet par octet que votre
  implémentation doit produire.

Vous avez trouvé un bogue ? Déposez-le sur GitHub. Vous avez trouvé une vulnérabilité ? Voir `SECURITY.md`.
