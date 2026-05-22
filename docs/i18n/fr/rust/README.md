# Protocole Aether — Implémentation Rust

[English](../../../../rust/README.md) · [Français](README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Implémentation Rust complète du protocole de réseau maillé Aether, avec une compatibilité de format réseau avec l'implémentation de référence C#.

## Vue d'ensemble

Ce crate fournit :

- **Sérialisation/désérialisation MeshPacket** — Format binaire réseau correspondant exactement à C# PacketSerializer
- **Signature Ed25519** — Génération de clé d'identité, signature et vérification
- **Protocole Signal** — Accord de clé basé sur X3DH avec cliquet symétrique pour la confidentialité persistante
- **Service de signature de paquets** — Déduplication des nonces et vérifications de fraîcheur
- **Transport en cours de processus** — Réseau maillé simulé pour les tests et les démonstrations

## Structure du projet

```
rust/
├── Cargo.toml                          # Crate manifest
├── src/
│   ├── lib.rs                          # Module declarations
│   ├── main.rs                         # Demo application
│   ├── constants.rs                    # Protocol constants
│   ├── models.rs                       # Core data structures
│   ├── protocol/
│   │   ├── mod.rs                      # MeshPacket, PacketType enum
│   │   └── serializer.rs               # Binary serialization (wire-compatible)
│   ├── security/
│   │   ├── mod.rs                      # Module declarations
│   │   ├── ed25519.rs                  # Ed25519 signing service
│   │   ├── signal_protocol.rs          # Signal Protocol implementation
│   │   └── packet_signing.rs           # Packet signing + nonce dedup
│   └── transport/
│       ├── mod.rs                      # TransportService trait
│       └── in_process.rs               # In-memory transport implementation
```

## Fonctionnalités clés

### 1. Compatibilité du format réseau

Le `PacketSerializer` produit une sortie identique octet par octet à l'implémentation C# :

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (GUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (u16, LE)
[N bytes] SourceUhid (UTF-8)
[2 bytes] DestinationUhid length (u16, LE)
[N bytes] DestinationUhid (UTF-8)
[2 bytes] PacketNonce length (u16, LE)
[N bytes] PacketNonce
[4 bytes] Payload length (i32, LE)
[N bytes] Payload
[2 bytes] Signature length (u16, LE)
[N bytes] Signature
```

Tous les entiers multi-octets utilisent l'ordre d'octets petit-boutiste. Les longueurs de chaînes sont préfixées par u16 (SourceUhid, DestinationUhid) ou i32 (Payload, Signature) comme spécifié dans la spécification du protocole.

### 2. Types de paquets

Les 26 types de paquets de la spécification du protocole sont définis :

- RouteRequest (1), RouteReply (2), Data (3), Ack (4)
- SosBroadcast (5), SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8), ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11), StreamSegment (12), StreamSubscribe (13), StreamUnsubscribe (14)
- VoicePtt (15), VoiceCall (16), VoiceSignaling (17)
- DtnBundle (18), DtnCustodyAck (19), DtnDeliveryReceipt (20)
- PresenceBeacon (21), PresenceQuery (22), ProfileSync (23)
- TipPacket (24), PreKeyRequest (25), PreKeyResponse (26)

### 3. Signature Ed25519

- Clés privées de 32 octets (graine), clés publiques de 32 octets, signatures de 64 octets
- Utilise `ed25519-dalek` pour les opérations cryptographiques
- Effacement sécurisé des clés après utilisation

### 4. Protocole Signal

Accord de clé basé sur X3DH avec cliquet symétrique :

- **Accord de clé :** ECDH P-256 utilisant des clés pré-générées éphémères et signées
- **Dérivation de clé :** HKDF-SHA256 avec des chaînes d'information uniques
  - `aether-root-v1` — Clé racine
  - `aether-chain-send-v1` — Clé de chaîne d'envoi
  - `aether-chain-recv-v1` — Clé de chaîne de réception
- **Chiffrement :** AES-256-GCM (nonce de 12 octets, étiquette de 16 octets)
- **Cliquet :** Avancement de la clé de chaîne symétrique avec clés de message basées sur un compteur
- **Gestion hors ordre :** Jusqu'à 1 000 clés de messages ignorées mises en cache

### 5. Service de signature de paquets

- Génération de nonce aléatoire de 8 octets
- Horodatages à précision milliseconde
- Validation de fraîcheur (fenêtre de 5 minutes)
- Déduplication des nonces par expéditeur (prévient les rejeux)
- Nettoyage automatique des entrées expirées

### 6. Transport en cours de processus

Réseau maillé simulé pour les tests :

- Registre statique des nœuds utilisant une HashMap concurrente
- Livraison de messages de type « fire-and-forget »
- Vérifications de connectivité bidirectionnelle entre pairs
- Convient aux démonstrations et aux tests unitaires

## Utilisation

### Génération de clé de base et signature

```rust
use aether_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Session du protocole Signal

```rust
use aether_protocol::security::SignalProtocolService;

let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

// Bob publishes pre-key bundle
let bob_bundle = bob.generate_pre_key_bundle("bob-node")?;

// Alice processes bundle and establishes session
alice.process_pre_key_bundle(&bob_bundle)?;

// Alice encrypts message
let plaintext = b"Hello!";
let encrypted = alice.encrypt("bob-node", plaintext)?;

// Bob decrypts
let alice_bundle = alice.generate_pre_key_bundle("alice-node")?;
bob.process_pre_key_bundle(&alice_bundle)?;
let decrypted = bob.decrypt("alice-node", &encrypted)?;

assert_eq!(decrypted, plaintext);
```

### Sérialisation des paquets

```rust
use aether_protocol::protocol::{MeshPacket, PacketType};
use aether_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### Signature des paquets

```rust
use aether_protocol::security::PacketSigningService;
use aether_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### Transport en cours de processus

```rust
use aether_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Exécution de la démonstration

```bash
cargo run --release
```

La démonstration effectue les étapes suivantes :

1. Génère les clés d'identité pour Alice et Bob
2. Initialise les services du protocole Signal
3. Génère et échange des paquets de pré-clés
4. Établit des sessions chiffrées
5. Échange des messages chiffrés
6. Crée et signe des paquets maillés
7. Vérifie les signatures des paquets
8. Sérialise et désérialise des paquets
9. Démontre le transport en cours de processus

## Constantes

Toutes les constantes du protocole sont définies dans `src/constants.rs`, correspondant à la spécification C# :

- Routage : DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Sécurité : MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Transport : BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN : DtnBundleTtlHours=72, DtnMaxCopies=3
- Voix/Flux : Diverses configurations de débit binaire et de tampon

## Dépendances

- `ed25519-dalek` — Signature Ed25519
- `x25519-dalek` — Accord de clé X25519
- `aes-gcm` — Chiffrement AES-256-GCM
- `hkdf` — Dérivation de clé HKDF
- `sha2` — Hachage SHA-256
- `hmac` — Opérations HMAC
- `rand` — Génération de nombres aléatoires
- `uuid` — Génération et sérialisation de GUID
- `serde` + `serde_json` — Sérialisation
- `tokio` — Exécution asynchrone
- `async-trait` — Méthodes de trait asynchrones

## Tests

Exécuter tous les tests :

```bash
cargo test
```

Les tests couvrent :

- Création de paquets et gestion du TTL
- Conversion des types de paquets
- Aller-retours de sérialisation/désérialisation
- Génération de clés Ed25519 et vérification des signatures
- Établissement de session du protocole Signal et chiffrement
- Signature des paquets et validation de la fraîcheur
- Connectivité du transport en cours de processus

## Conformité au protocole

Cette implémentation respecte la spécification du protocole Aether (Version 2.0) avec :

- ✅ Format binaire réseau (petit-boutiste, préfixé par la longueur)
- ✅ Les 26 types de paquets
- ✅ Signature Ed25519 avec déduplication des nonces
- ✅ Accord de clé X3DH avec HKDF-SHA256
- ✅ Chiffrement AES-256-GCM avec nonce de 12 octets
- ✅ Cliquet symétrique avec gestion hors ordre
- ✅ Génération et traitement des paquets de pré-clés
- ✅ Construction des données signables des paquets (hachage SHA-256 de la charge utile)
- ✅ Abstraction de trait de transport

## Notes

- Le format réseau utilise l'ordre d'octets petit-boutiste tout au long (correspondant à C# BinaryPrimitives.WriteInt32LittleEndian)
- Les préfixes de longueur de chaîne utilisent u16 pour les UHID, i32 pour la charge utile/signature (correspondant à C# WriteUInt16/WriteInt32)
- Tout le matériel de clé cryptographique est effacé après utilisation via l'équivalent de `CryptographicOperations`
- L'implémentation du protocole Signal utilise HKDF avec les octets de sel [0x01] et [0x02] pour le cliquet de chaîne (correspondant à l'utilisation HKDF de C#)
- La déduplication des nonces utilise un VecDeque par expéditeur avec nettoyage automatique des entrées de plus de 5 minutes
