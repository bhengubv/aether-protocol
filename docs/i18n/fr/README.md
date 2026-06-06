```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

Partagez des fichiers, des messages et des flux avec des personnes à proximité. Sans Wi-Fi. Sans données mobiles. Sans inscription. Comme AirDrop, sauf que ça fonctionne avec tout le monde, sur toutes les plateformes.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## Qu'est-ce que vous pouvez faire avec ?

**Partager des notes de cours sans consommer de données.**

Vous êtes dans un groupe d'étude. Quelqu'un a des annales sur son téléphone. Aether les envoie directement sur votre appareil via Bluetooth — pas de point d'accès, pas de groupe WhatsApp, pas de limite de taille de fichier. Si quelqu'un dans le groupe est hors de portée, le fichier transite par d'autres appareils jusqu'à l'atteindre. Les messages peuvent attendre jusqu'à 72 heures qu'une route se libère si nécessaire.

```
  [Vous] ──BLE──▶ [Ami] ──WiFi──▶ [Ami d'un ami]
    notes.pdf           relayé, chiffré
```

**Découvrez ce qui se passe autour de vous.**

Vous êtes à un événement sur un campus ou à un festival. Aether découvre les autres appareils à proximité via Bluetooth et Wi-Fi Direct — pas de fil d'actualité, pas d'algorithme. Vous voyez ce qui se trouve réellement autour de vous, pas ce qui est promu.

**Envoyez un SOS quand il n'y a pas de signal.**

Votre téléphone n'a pas de réception. Aether diffuse un message d'urgence à tous les appareils à portée, et ces appareils le transmettent à leur tour. Aucune tour cellulaire n'est nécessaire.

```
          ╭── [Téléphone B]
         ╱
  [SOS!] ───── [Téléphone C] ──── [Téléphone E]
         ╲
          ╰── [Téléphone D]

  Diffusion : atteint tous les appareils à portée
```

**Créez des canaux de groupe privés.**

Un canal pour votre étage de résidence, votre association, votre équipe de projet. Seuls les membres vérifiés peuvent lire ou envoyer des messages. Aucun serveur ne stocke la conversation.

**Vendez des choses aux personnes à proximité.**

Mettez un manuel en vente. Les personnes passant à portée du maillage le voient. Pas de compte sur une marketplace, pas de frais d'annonce — juste la proximité.

**Regardez un film ensemble, à travers le maillage.**

Votre groupe organise une soirée cinéma. Quelqu'un a le fichier. Aether synchronise la lecture sur chaque appareil — play, pause, avance — tous en parfaite synchronisation. Si seules certaines personnes ont le fichier, le maillage le distribue en temps réel sous forme de flux P2P. Tout le monde participe via SDPKT pour l'acheter si personne ne l'a.

## Comment ça fonctionne

Les appareils communiquent directement entre eux via Bluetooth, Wi-Fi Direct ou NearLink. Pas de connexion internet, pas de serveur, pas d'infrastructure centralisée.

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ Chiffré de bout en bout (Signal Protocol) ======>|
       |                                                             |
       |  Pas d'internet. Pas de serveurs. Pas de FAI. Juste des appareils qui communiquent.     |
```

Quand un message ne peut pas atteindre sa destination directement, il transite par d'autres appareils. Ces appareils relais ne peuvent pas lire ce qu'ils transportent — chaque message est chiffré avec AES-256-GCM. Chaque paquet est signé avec des clés d'identité Ed25519, et les paquets falsifiés sont rejetés par le réseau.

> **Note sur la maturité sécuritaire (à lire avant de déployer) :** Le vrai X3DH (4 DH X25519), le Double Ratchet Signal complet (étape de rotation DH à la réception, KDF_RK, ratchet de chaîne 0x01/0x02), et le pool de clés pré-partagées à usage unique (100 OPK par défaut, FIFO, protégé par verrou) sont implémentés dans **les 8 langages** et ancrés à un corpus de fixtures cross-langages partagé sous `fixtures/signal/`. Le seul point ouvert restant est la mise en service RF physique sur du matériel BLE réel (suivi dans `OPEN_ISSUES.md`).

Pas de comptes, pas de numéros de téléphone, pas d'e-mails. Vous générez une paire de clés et vous êtes sur le réseau.

```
  ┌─────────────────────────────────┐
  │         Votre Application       │
  ├─────────────────────────────────┤
  │ Messagerie · Streaming · Voix   │
  │ Vidéo · Regarder Ensemble       │
  ├─────────────────────────────────┤
  │  Sécurité : AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
  ├─────────────────────────────────┤
  │  Routage : AODV + DTN           │
  ├─────────────────────────────────┤
  │  Transport : BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**Routage** — AODV avec réponses de route signées. Chaque réponse de route est signée par la clé Ed25519 de la destination, de sorte qu'aucun appareil ne peut se faire passer pour une destination qu'il n'est pas.

**Stockage et transmission différée** — Quand il n'y a pas de route active, les paquets sont conservés jusqu'à 72 heures jusqu'à ce qu'un chemin s'ouvre.

**Sélection du transport** — Le protocole choisit le transport approprié par paquet. Les petits messages de contrôle passent par BLE. Les transferts volumineux utilisent Wi-Fi Direct. NearLink quand disponible.

**Voix, vidéo et streaming** — Appels vidéo avec négociation de codec (H.264/H.265/VP8), sélection de qualité selon le transport, vidéo de groupe avec relais SFU automatique, visionnage synchronisé avec compensation RTT, et streaming à débit adaptatif.

**Protection contre la relecture** — Déduplication des nonces avec une fenêtre de fraîcheur d'horodatage de 5 minutes.

## Transports

Chaque transport a un nom de couleur utilisé dans tout le code source. `IsAvailable` bloque les chemins matériellement indisponibles — le `TransportManager` les ignore automatiquement et bascule sur le transport disponible suivant.

| Couleur | Nom | Portée | Bande passante | Statut |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | Relais HTTP cellulaire | Illimité | ~10 Mbps | ✅ Windows — serveur relais dans `samples/AetherMesh.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE (`android/white/`) ; Windows : approximation NDEF-sur-BLE-GATT + ACR122U PC/SC (`Windows.Networking.Proximity` supprimé dans Win 11) |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit` ; Windows + Android : approximation SSAP-sur-BLE (compatible API, non compatible fil) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Format fil Meshtastic sur BLE LR (~1.3 km) ; remplacement radio par SX1276/SX1278 quand module LoRa présent |

Ordre de priorité dans `TransportManager` : NearLink → BLE (≤ 1 Ko) → Wi-Fi Direct → NFC → LoRa → Relais HTTP (dernier recours, `PowerCostRelative = 100`).

## Niveaux de déploiement

Aether fonctionne sur toute plateforme supportant Bluetooth ou Wi-Fi. Le niveau auquel vous vous trouvez dépend du système d'exploitation ciblé.

---

### Niveau standard — toute plateforme

Android · Windows · Linux · macOS · iOS

Aether s'exécute pleinement sur tout appareil disposant de matériel Bluetooth ou Wi-Fi. Là où une radio est physiquement absente, chaque transport bloqué est approximé à l'aide de ce qui est disponible :

- **NearLink (Aether Teal)** — approximé via BLE GATT en utilisant l'UUID de service SLE Aether canonique (`61657468-6572-0003-0000-000000000000`). La couche de protocole d'application SSAP est identique à l'API GATT. La couche radio (BPSK/QPSK/8PSK, codes Polar, canaux 1–4 MHz) ne l'est pas — les nœuds du niveau standard ne peuvent pas échanger des octets bruts avec du vrai matériel NearLink ; ils interopèrent avec d'autres nœuds Aether de niveau standard.
- **LoRa (Aether Red)** — approximé en utilisant le format fil Meshtastic complet sur BLE 5.0 Coded PHY (S=8, ~1,3 km en extérieur). La fédération de nœuds-pont avec du vrai matériel LoRa fonctionne automatiquement — le même format de paquet Meshtastic est utilisé sur tous les sauts sans traduction.
- **NFC (Aether White)** — approximé via NDEF-sur-BLE-GATT avec une barrière de proximité RSSI (≥ −40 dBm ≈ 5–10 cm) qui reproduit la sémantique de connexion par effleurement. Le chemin PC/SC via lecteur NFC USB est également supporté sur Windows.

Toutes les autres capacités — BLE, Wi-Fi Direct, relais HTTP, sécurité Signal Protocol (X3DH + Double Ratchet), routage AODV, stockage-et-transmission différée DTN, diffusion SOS, voix, streaming — sont natives et identiques au niveau natif.

**Il s'agit d'un déploiement entièrement capable, de qualité production.** La plupart des applications démarrent ici.

---

### Niveau natif — CircleOS / OpenHarmony

CircleOS · HarmonyOS · tout OS basé sur OpenHarmony

CircleOS est construit sur OpenHarmony, qui embarque du silicium NearLink (SLE) et le SDK `@kit.NearLinkKit` comme capacité OS de première classe. Sur les appareils CircleOS et HarmonyOS dotés de matériel NearLink, aucune approximation n'est nécessaire — `harmonyos/teal/` utilise directement la vraie radio SLE :

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Ce n'est pas simplement une meilleure version du niveau standard. Au niveau NearLink, c'est un réseau catégoriquement différent :

| Capacité | Niveau standard (approximation BLE) | Niveau natif (CircleOS / OpenHarmony) |
|---|---|---|
| **Portée NearLink** | ~100 m (BLE) | **600 m** |
| **Bande passante NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latence NearLink** | ~10 ms (BLE) | **20 µs** |
| **Consommation NearLink** | Référence BLE | **60% de moins que BLE 5.0** |
| **Pairs NearLink simultanés** | ~7 (limite de connexion BLE) | **500+** |
| **Source NearLink** | SSAP-sur-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Vraie radio SLE (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / Relais HTTP** | Natif | Natif (identique) |
| **Sécurité Signal Protocol** | Complète | Complète (identique) |
| **Routage / DTN / SOS** | Complet | Complet (identique) |
| **Identité Aether Tag** | Supportée | Supportée (identique) |

---

### Passage entre niveaux

Aucun changement de code n'est nécessaire. Le niveau est déterminé à l'exécution par `IsAvailable` sur chaque service de transport :

1. Sur un appareil CircleOS ou HarmonyOS avec du silicium NearLink, `IsAvailable` sur le transport NearLink retourne `true` (sondage matériel via vérification des permissions + tentative de scan passif).
2. `TransportManager` promeut automatiquement NearLink en position prioritaire — coût énergétique le plus bas, bande passante la plus haute.
3. Le code applicatif, le format fil, l'algorithme de routage, la couche de sécurité et les Aether Tags sont identiques sur les deux niveaux.

Un nœud du niveau standard et un nœud du niveau natif peuvent communiquer librement — ils partagent le même format fil, les mêmes sessions Signal Protocol, et les mêmes Aether Tags. La différence de niveau affecte uniquement la radio utilisée pour les paquets NearLink, pas le protocole au-dessus.

---

> **En interne, ces niveaux sont appelés la variante Asterix (standard) et la variante Obélix (natif).** Asterix fonctionne bien avec ce qui est disponible. Obélix — tournant sur CircleOS avec NearLink natif — opère à une capacité durablement élevée, à la manière dont Obélix porte la force de la potion magique sans avoir besoin d'en boire à nouveau.

---

## Implémentations

Aether est construit en 8 langages pour fonctionner sur les téléphones, ordinateurs portables, tablettes et microcontrôleurs. Toutes les implémentations produisent des paquets compatibles fil — un message chiffré par le nœud Rust peut être relayé par le nœud Python et déchiffré par le nœud Swift.

| Langage | Répertoire | Format fil | Routage/DTN/SOS | X3DH | Double Ratchet | Pool OPK | Voix/Groupe | Streaming/Vidéo/Regarder |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Les 8 langages produisent des paquets fil identiques octet par octet, vérifiés par 14 fixtures canoniques de format fil et 4 vecteurs de test Signal exécutés dans la CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Le routage (RREQ/RREP de style AODV), le stockage-et-transmission différée DTN, la diffusion SOS, la voix, le streaming, et les services de renforcement de la sécurité sont implémentés dans chaque langage avec **~3 000 tests** sur les 8 implémentations :

| Langage | Tests | Plateforme CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3 000** | |

L'interopérabilité Signal cross-langages est ancrée à `fixtures/signal/` avec des vecteurs de test partagés pour X3DH (`x3dh_basic`), le ratchet symétrique (`ratchet_step_basic`, `ratchet_step_three_iterations`), et KDF_RK (`kdf_rk_basic`). Chaque implémentation doit produire des sorties identiques octet par octet par rapport à ces fixtures. Les 8 langages embarquent désormais une session Signal complète (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

## Démarrage Rapide

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherMesh.Demo.Console
```

La démo vous guide à travers 8 étapes : génération de clés d'identité Ed25519 pour trois nœuds (Alice, Bob, Charlie), établissement de sessions Signal Protocol, envoi de messages chiffrés, relayage d'un message via Charlie (qui ne peut pas le lire), affichage du format fil binaire, et démonstration de la confidentialité persistante sur 5 messages consécutifs. La sortie est colorée et s'arrête entre les étapes.

**Envoyer un message en C# :**

```csharp
// Établir une session Signal Protocol
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Chiffrer et envoyer
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Créer un paquet signé
var packet = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = "alice",
    DestinationUhid = "bob",
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7
};
var wireBytes = PacketSerializer.Serialize(packet);
await transport.SendAsync("bob", wireBytes);
```

### Rust (1.70+)

```bash
cd rust && cargo run
```

La démo génère des clés d'identité pour deux nœuds, échange des bundles de pré-clés, établit des sessions chiffrées, envoie des messages chiffrés dans les deux sens, crée et signe des paquets maillage, vérifie les signatures, et sérialise les paquets au format fil binaire. Elle démontre également la couche de transport en cours de processus.

**Envoyer un message en Rust :**

```rust
let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

let alice_bundle = alice.generate_pre_key_bundle("alice")?;
bob.process_pre_key_bundle(&alice_bundle)?;

let bob_bundle = bob.generate_pre_key_bundle("bob")?;
alice.process_pre_key_bundle(&bob_bundle)?;

let encrypted = alice.encrypt("bob", b"Hello Bob!")?;
let decrypted = bob.decrypt("alice", &encrypted)?;
```

### TypeScript (Node 18+, tsx)

```bash
cd typescript && npm install && npm run dev
```

La démo crée deux nœuds dans un réseau simulé, génère des clés Ed25519, établit des sessions Signal Protocol, crée et signe un paquet, le sérialise au format binaire compatible C#, chiffre un message secret, le déchiffre sur l'autre nœud, l'envoie via le transport, et vérifie le voyage aller-retour.

**Envoyer un message en TypeScript :**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Échanger le bundle avec le pair
await signal.processPreKeyBundle(peerBundle);

const plaintext = new TextEncoder().encode("Hello!");
const encrypted = await signal.encrypt("peer-node", plaintext);

const packet = MeshPacket.create(PacketType.Data, "my-node");
packet.destinationUhid = "peer-node";
packet.payload = encrypted;

const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

const serialized = PacketSerializer.serialize(packet);
await transport.sendAsync("peer-node", serialized);
```

### Python (3.10+)

```bash
cd python && pip install -e . && python3 demo.py
```

La démo exécute 8 démonstrations : génération de clés Ed25519 et détection de falsification, création de nœuds avec capacités, échange de clés X3DH Signal Protocol, chiffrement et déchiffrement AES-256-GCM, sérialisation de paquets, signature de paquets avec détection de relecture, transport en cours de processus, et un flux complet de bout en bout combinant toutes les couches.

**Envoyer un message en Python :**

```python
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

bob_bundle = await bob_signal.generate_pre_key_bundle("bob")
await alice_signal.process_pre_key_bundle(bob_bundle)

encrypted = await alice_signal.encrypt("bob", b"Hello Bob!")

packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="alice",
    destination_uhid="bob",
    payload=encrypted.ciphertext,
    ttl=7
)
signing_service.sign_packet(packet, alice_private_key)

serialized = PacketSerializer.serialize(packet)
await transport.send_async("bob", serialized)
```

### Go (1.22+)

```bash
cd go && go run ./cmd/demo/main.go
```

La démo exécute 5 démonstrations : aller-retours de sérialisation de paquets, signature Ed25519 avec détection de falsification, établissement de session Signal Protocol avec messagerie chiffrée dans les deux sens, transport en cours de processus entre deux pairs, et déduplication de nonces pour la protection contre la relecture.

**Envoyer un message en Go :**

```go
alice, _ := security.NewSignalProtocolService()
bob, _ := security.NewSignalProtocolService()

aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
bob.ProcessPreKeyBundle(aliceBundle)

bobBundle, _ := bob.GeneratePreKeyBundle("bob")
alice.ProcessPreKeyBundle(bobBundle)

encrypted, _ := alice.Encrypt("bob", []byte("Hello Bob!"))
decrypted, _ := bob.Decrypt("alice", encrypted)
```

### Kotlin (JDK 17+, Gradle 8+)

```bash
cd kotlin && ./gradlew run
```

La démo parcourt 11 étapes : génération de clés, création de nœuds avec capacités, initialisation Signal Protocol, échange de bundles de pré-clés, établissement de session, création et signature de paquets, sérialisation, désérialisation avec vérification de signature, chiffrement de bout en bout avec rotation de clés, détection d'attaque par relecture, et transport en cours de processus.

**Envoyer un message en Kotlin :**

```kotlin
val aliceSignal = SignalProtocol()
val bobSignal = SignalProtocol()

val bobBundle = bobSignal.generatePreKeyBundle("bob")
aliceSignal.processPreKeyBundle(bobBundle)

val aliceBundle = aliceSignal.generatePreKeyBundle("alice")
bobSignal.processPreKeyBundle(aliceBundle)

val encrypted = aliceSignal.encrypt("bob", "Hello Bob!".toByteArray())
val decrypted = bobSignal.decrypt("alice", encrypted)
```

### Swift (5.9+, macOS 13+ / iOS 16+)

```bash
cd swift && swift run aether-demo
```

La démo exécute 5 tests : aller-retours de sérialisation de paquets, signature Ed25519 avec rejet de falsification, établissement de session Signal Protocol avec chiffrement AES-256-GCM, livraison de messages via transport en cours de processus, et un flux complet de bout en bout où Alice signe un paquet et Bob le vérifie après transport.

**Envoyer un message en Swift :**

```swift
let aliceSignal = SignalProtocolService()
let bobSignal = SignalProtocolService()

let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: "bob")
try await aliceSignal.processPreKeyBundle(bobBundle)

var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice",
    destinationUhid: "bob",
    ttl: 7,
    payload: "Hello Bob!".data(using: .utf8)!
)

let signer = await PacketSigningService(
    privateKey: alicePrivateKey, publicKey: alicePublicKey)
try await signer.signPacket(&packet)

let serialized = PacketSerializer.serialize(packet)
await transport.sendAsync(peerUhid: "bob", data: serialized)
```

### C (CMake 3.16+, C11, libsodium)

```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

La démo exécute 7 démonstrations : génération de clés Ed25519, création et signature de paquets, sérialisation au format fil binaire, désérialisation avec vérifications d'intégrité, chiffrement et déchiffrement AES-256-GCM, authentification de message HMAC-SHA256, et dérivation de clés HKDF-SHA256.

**Envoyer un message en C :**

```c
aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
packet->type = AETHERMESH_PACKET_TYPE_DATA;
packet->ttl = 7;

aethermesh_packet_set_source_uhid(packet, "alice");
aethermesh_packet_set_destination_uhid(packet, "bob");
aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Signer
size_t signable_len = 0;
uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
aethermesh_packet_set_signature(packet, signature, 64);
free(signable);

// Sérialiser et envoyer
uint8_t buffer[2048];
int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
// envoyer buffer[0..size-1] via le transport

aethermesh_packet_free(packet);
```

## Feuille de Route

Ce qui est construit et ce qui vient ensuite.

**Terminé (vérifié cross-langages, les 8 implémentations) :**
- Format fil : identique octet par octet sur 8 langages, ancré par 14 fixtures canoniques et assertions cross-langages dans la CI (`fixtures/expected/*.bin`)
- ✅ **CI GitHub Actions** — matrice à 9 tâches (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, plus tâche d'intégrité des fixtures) dans `.github/workflows/ci.yml`.
- Signature et vérification de paquets Ed25519
- Chiffrement AES-256-GCM
- Primitives de dérivation de clés HKDF / HMAC
- Sérialisation de paquets + disposition de signature (LE + champs int32 4 octets)
- Simulateur de transport en cours de processus (pour le développement et les tests)
- Service de routage inspiré AODV avec RREQ/RREP, réponses de route signées, déduplication, transfert TTL
- Service de stockage-et-transmission différée DTN avec transfert de garde, réplication géohash, TTL 72h
- Service de diffusion SOS avec inondation, déduplication, garde d'auto-origine, limite de débit (3/h)
- Points d'extensibilité : `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (implémentations Noop par défaut)
- **~3 000 tests** sur les 8 langages (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — tous verts dans la CI
- ✅ **Vraie clé éphémère X3DH (8 langages)** — 4 DH X25519 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) avec dérivation de racine HKDF-SHA256. Ancré par `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Alignement Double Ratchet à l'échelle de la famille** — Signal §5 complet avec HMAC-SHA256 + séparation de domaine 0x01/0x02 dans le ratchet symétrique, HKDF-SHA256 KDF_RK dans l'étape DH-ratchet, rotation DH à la réception. Vérifié par les fixtures `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 réconcilié avec HEAD** — voir `docs/PROTOCOL_SPEC.md`.

**Terminé (les 8 langages) :**
- ✅ **Appels vocaux (1-à-1)** — machine d'état de signalisation (Offer/Answer/Hangup/Cancel/Timeout) + transport de trames binaires (16 octets callId · 4 octets seq · 8 octets timestamp · 1 octet isSilence · N octets). Livraison consciente de la route via `IRoutingService`.
- ✅ **Voix de groupe** — adhésion pilotée par l'hôte (inviter/expulser/quitter), champ de génération de clé par trame, distribution unicast à tous les membres actuels, rotation de clé contrôlée par l'hôte lors d'un changement d'adhésion.
- ✅ **Streaming en direct** — l'éditeur diffuse `StreamAnnounce` ; les abonnés envoient `StreamSubscribe` ; trames binaires `StreamSegment` (16 octets streamId · 4 octets seq · 8 octets ts · 1 octet isKeyframe · N octets) en unicast vers chaque abonné.
- ✅ **Appels vidéo (1-à-1)** — négociation de codec/résolution/fps/débit dans la signalisation, signaux de demande d'image clé et de changement de qualité, format binaire `VideoFrame` correspondant à la disposition vocale.
- ✅ **Regarder Ensemble** — l'hôte émet des commandes `WatchSync` autoritatives (lecture/pause/avance/vitesse) ; les suiveurs les appliquent avec compensation RTT (`position = positionMs + elapsed × playbackSpeed`) ; `WatchReaction` en mode fire-and-forget.
- ✅ **Pool de clés pré-partagées à usage unique (OPK)** — 100 par défaut, émission FIFO, rechargement paresseux, consommation protégée par verrou dans les 8 langages. Résout le risque de concurrence sur l'OPK unique.
- ✅ **C : session Signal complète** — `aethermesh_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` dans `c/src/signal_protocol.c` ; 6 tests E2E à deux nœuds dans `c/tests/test_signal_session.c`. Les 8 langages disposent désormais d'un Signal Protocol complet capable de session.

**Terminé (référence C# uniquement) :**
- ✅ **Démo Étape 9 — MessagingService + DTN fallback de bout en bout** — `samples/AetherMesh.Demo.Console` présente la messagerie chiffrée par Signal réel avec stockage-et-transmission différée DTN quand le destinataire est hors ligne.
- ✅ **Pont `AetherMesh.Messaging` ↔ `AetherMesh.Security`** — `SignalMessageEnvelopeCipher` rend la couche de messagerie chiffrée de bout en bout par défaut ; les messages sans session Signal sont mis en file d'attente, jamais envoyés de façon non sécurisée.
- ✅ **Streaming à débit adaptatif** — `AdaptiveBitrateController` avec échelles de débit mandatées par la spec pour le Profil A (temps réel), B (diffusion en direct), et C (VOD). L'éditeur sélectionne le barreau le plus élevé soutenable (marge de 20%) et émet `StreamAbandon` (`PacketType.StreamAbandon`) au lieu d'un segment quand en dessous du plancher. `IStreamingService` expose `UpdateBandwidthEstimate` et `GetCurrentBitrateRung`.
- ✅ **Regarder Ensemble : ingestion BitTorrent + financement de groupe ChipIn** — modèles `TorrentInfo` / `TorrentFile` ; `WatchTogetherService` gère `PacketType.TorrentMetadata` et déclenche `TorrentReceived`. Machine d'état `ChipInPool` / `ChipInContribution` (Collecte → Financé → Achat → Acquis / Échoué / Remboursé) ; `StartChipInAsync` / `ContributeAsync` / `GetChipIn` sur `IWatchTogetherService`.
- ✅ **Appels vidéo de groupe avec relais SFU automatique** — `GroupVideoService` / `IGroupVideoService`. Topologie FullMesh pour ≤ 3 participants ; basculement automatique vers SFU à `SfuThresholdParticipants` (4) avec réassignation de relais via `GroupVideoSignaling(SfuAssigned)`. Distribution en FullMesh, envoi relais uniquement en mode SFU. Type de paquet de signalisation `GroupVideoSignaling = 35`.
- ✅ **Simulation de transport BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Tramage MTU GATT via `BleGattFramer` (1024 o/trame, `[2o count][2o index][payload]`), registre de pairs statique en cours de processus, diffusion publicitaire. Toutes les contraintes `BleMaxPayloadBytes` appliquées.
- ✅ **Simulation de transport Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Cycle de vie explicite `ConnectAsync`/`DisconnectAsync`, livraison directe de grande charge utile (sans tramage), événements bidirectionnels `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulation de transport NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU de trame 4096 o, registre de 500 pairs, `ConnectedPeerCount`, `IsAvailable` configurable à l'exécution.
- ✅ **Tests de simulation de mise en service RF** — Tests d'interopérabilité à deux nœuds (`SimulatedTransportTests`) : aller-retour `MeshPacket` BLE + NearLink, transfert de charge utile 64 Ko Wi-Fi Direct. Couche logicielle entièrement vérifiée ; session de laboratoire sur appareil physique nécessaire pour la validation sur matériel.

**Terminé (couche transport C# — tous fail-fast) :**
- ✅ **Vrai transport BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (serveur GATT Android). Test complet de mise en service RF dans `samples/AetherMesh.BleRfTest/`.
- ✅ **Vrai transport Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Test RF dans `samples/AetherMesh.WifiDirectRfTest/`.
- ✅ **Transport relais HTTP (Aether Purple)** — `HttpRelayTransportService` avec long-poll de 10 secondes, `PowerCostRelative = 100`, toujours en dernier recours. Serveur relais dans `samples/AetherMesh.RelayServer/` (API minimale ASP.NET Core, port 5200). Test RF dans `samples/AetherMesh.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implémente `HostApduService` avec AID `F061657468657200`. `WinNfcStubTransportService` documente deux chemins d'approximation Windows : (1) NDEF-sur-BLE-GATT avec barrière RSSI ≥ −40 dBm (simule la connexion par effleurement sans silicium NFC, `IsAvailable = Bluetooth présent`) ; (2) lecteur USB ACR122U via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = lecteur sans contact énuméré`). Chemin de mise à niveau : implémenter `ITransportService` quand Microsoft livrera une API NFC P2P first-party.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implémentation ArkTS HarmonyOS 5.0.1 (API 13) complète utilisant `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`) ; `isAvailable` sondé à l'exécution. `WinNearLinkStubTransportService` + `android/teal/` documentent l'approximation SSAP-sur-BLE : BLE GATT avec UUID de service SLE Aether `61657468-6572-0003-0000-000000000000` — compatible API avec SSAP, non compatible fil avec le vrai matériel NearLink. Chemin de mise à niveau : remplacer les appels BLE GATT par des appels SDK `ssapc_*`/`ssaps_*` ; UUIDs et slot `TransportManager` inchangés.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` documentent l'approximation Meshtastic-sur-BLE-LR : format fil Meshtastic complet (en-tête 16 octets + protobuf AES-256-CTR) sur BLE 5.0 Coded PHY S=8 (~1,3 km en extérieur), avec routage à inondation gérée et fenêtre de contention pondérée RSSI. La fédération de nœuds-pont avec du vrai matériel LoRa fonctionne automatiquement (même format de paquet Meshtastic, sans traduction). Chemin de mise à niveau : remplacer la radio BLE LR par un pilote SX1276/SX1278 AT-command ou SPI ; format de paquet et routage inchangés.

**Ouvert — suivi dans `OPEN_ISSUES.md` :**
- Mise en service RF sur matériel réel : test d'interopérabilité à deux nœuds de bout en bout sur des appareils BLE / Wi-Fi Direct physiques (les tests de simulation passent ; session de laboratoire matériel nécessaire)
- NearLink : `harmonyos/teal/` complet ; nécessite du matériel Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silicium NearLink absent sur les appareils non-Huawei). Windows + Android basculent automatiquement sur l'approximation SSAP-sur-BLE.
- LoRa / CircleLink : module radio requis pour la vraie portée LoRa. Sans module, le format fil Meshtastic est transporté sur BLE LR (~1,3 km) et la fédération de nœuds-pont avec du vrai matériel LoRa est disponible.

**Pas encore ouvert aux contributions externes :**
- Le protocole est encore en développement actif. Les contributions externes ne sont pas acceptées pour l'instant.
- L'implémentation du transport NearLink, les exemples d'intégration Android/iOS, les backends de transport additionnels, les benchmarks de performance et le fuzzing de protocole sont suivis en interne et seront ouverts quand le projet atteindra un point de contribution publique stable.

## Structure du Projet

```
aether-protocol/
  src/
    AetherMesh.Core/          Modèles de protocole, constantes, sérialisation de paquets
    AetherMesh.Security/      Signal Protocol, Ed25519, signature de paquets
    AetherMesh.Transport/     Abstractions de transport, NearLink, simulateur en cours de processus
    AetherMesh.Messaging/     Gestion des messages et relayage
    AetherMesh.Storage/       Persistance du stockage-et-transmission différée DTN
    AetherMesh.Streaming/     Streaming à débit adaptatif, modèles et interfaces vidéo
    AetherMesh.Voice/         Appels vocaux et voix de groupe
    AetherMesh.Content/       Vérification de contenu et transfert fragmenté
  samples/
    AetherMesh.Demo.Console/  Démo interactive
  tests/
    AetherMesh.Security.Tests/
    AetherMesh.Protocol.Tests/
  rust/                   Implémentation Rust
  typescript/             Implémentation TypeScript
  python/                 Implémentation Python
  go/                     Implémentation Go
  kotlin/                 Implémentation Kotlin/JVM
  swift/                  Implémentation Swift
  c/                      Implémentation C
  docs/
    PROTOCOL_SPEC.md      Spécification de protocole au format RFC
```

## Ajouter un Nouveau Transport

Implémenter `ITransportService` :

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implémenter SendAsync, IsConnected, DataReceived
}
```

Enregistrez-le dans l'injection de dépendances et `TransportManager` l'inclura automatiquement dans la sélection du transport, trié par coût énergétique.

## Comparaison

| Protocole | Limitation | Avantage Aether |
|----------|-----------|-----------------|
| **Briar** | Android uniquement, dépend de Tor | Multi-plateforme, maillage pur |
| **Meshtastic** | LoRa uniquement (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), capable voix et streaming |
| **Reticulum** | Python, petite communauté | 8 langages, compatibles fil entre tous |
| **libp2p** | Suppose une dorsale internet | Hors-ligne en premier, fonctionne sans infrastructure |
| **Yggdrasil** | Réseau superposé, nécessite internet | Maillage de couche physique, fonctionne sans internet |
| **Signal** | Pas de maillage, nécessite internet | Fonctionne hors ligne, P2P, relais maillage, même chiffrement E2E |

## Points d'Extension

Le protocole fonctionne de manière autonome. Ces interfaces vous permettent de brancher votre propre backend si vous en souhaitez un :

- `IAetherMeshIncentiveProvider` — récompenser les nœuds qui relayent le trafic (noop par défaut : relayage altruiste)
- `IAetherMeshBackendClient` — synchroniser avec un serveur quand internet est disponible (noop par défaut : entièrement hors ligne)
- `IAetherMeshFeatureFlagProvider` — activer/désactiver les fonctionnalités du protocole à l'exécution (noop par défaut : tout activé)

Les trois sont livrés avec des implémentations noop. Retirez-les et rien ne se casse.

## Contribution

Les contributions externes ne sont pas encore ouvertes. Le projet est encore en développement actif. Revenez quand nous annoncerons une fenêtre de contribution publique.

## Sécurité

Voir [SECURITY.md](SECURITY.md) pour la politique de divulgation responsable.

## Licence

Licence MIT. Voir [LICENSE](LICENSE).
