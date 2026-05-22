# Aether Protocol — Rust-Implementierung

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Vollständige Rust-Implementierung des Aether-Mesh-Netzwerkprotokolls mit Drahtformat-Kompatibilität zur C#-Referenzimplementierung.

## Übersicht

Dieses Crate bietet:

- **MeshPacket-Serialisierung/-Deserialisierung** — Binäres Drahtformat, das dem C#-PacketSerializer exakt entspricht
- **Ed25519-Signierung** — Erzeugung von Identitätsschlüsseln, Signierung und Verifizierung
- **Signal-Protokoll** — X3DH-basierte Schlüsselvereinbarung mit symmetrischem Ratschet für Forward Secrecy
- **Paketsignierdienst** — Nonce-Deduplizierung und Frischheitsprüfungen
- **In-Process-Transport** — Simuliertes Mesh-Netzwerk für Tests und Demos

## Projektstruktur

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

## Hauptfunktionen

### 1. Drahtformat-Kompatibilität

Der `PacketSerializer` erzeugt eine byte-für-byte identische Ausgabe wie die C#-Implementierung:

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

Alle Mehrbyte-Ganzzahlen verwenden Little-Endian-Bytereihenfolge. String-Längen sind mit u16 (SourceUhid, DestinationUhid) bzw. i32 (Payload, Signature) präfixiert, wie in der Protokollspezifikation angegeben.

### 2. Pakettypen

Alle 26 Pakettypen aus der Protokollspezifikation sind definiert:

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

### 3. Ed25519-Signierung

- 32-Byte-Private-Keys (Seed), 32-Byte-Public-Keys, 64-Byte-Signaturen
- Verwendet `ed25519-dalek` für kryptografische Operationen
- Sichere Schlüssel-Nullsetzung nach der Verwendung

### 4. Signal-Protokoll

X3DH-basierte Schlüsselvereinbarung mit symmetrischem Ratschet:

- **Schlüsselvereinbarung:** ECDH P-256 mit ephemeren und signierten Pre-Keys
- **Schlüsselableitung:** HKDF-SHA256 mit eindeutigen Info-Strings
  - `aether-root-v1` — Root-Key
  - `aether-chain-send-v1` — Sende-Chain-Key
  - `aether-chain-recv-v1` — Empfangs-Chain-Key
- **Verschlüsselung:** AES-256-GCM (12-Byte-Nonce, 16-Byte-Tag)
- **Ratschet:** Symmetrischer Chain-Key-Vorschub mit zählerbasierter Nachrichtenschlüsselableitung
- **Out-of-Order-Verarbeitung:** Bis zu 1.000 übersprungene Nachrichtenschlüssel im Cache

### 5. Paketsignierdienst

- Zufällige 8-Byte-Nonce-Erzeugung
- Zeitstempel mit Millisekunden-Präzision
- Frischheitsvalidierung (5-Minuten-Fenster)
- Nonce-Deduplizierung pro Absender (verhindert Replays)
- Automatische Bereinigung abgelaufener Einträge

### 6. In-Process-Transport

Simuliertes Mesh-Netzwerk für Tests:

- Statisches Knotenregister mittels concurrent HashMap
- Fire-and-Forget-Nachrichtenauslieferung
- Bidirektionale Peer-Verbindungsprüfungen
- Geeignet für Demos und Unit-Tests

## Verwendung

### Grundlegende Schlüsselerzeugung und Signierung

```rust
use aether_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Signal-Protokoll-Session

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

### Paketserialisierung

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

### Paketsignierung

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

### In-Process-Transport

```rust
use aether_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Demo ausführen

```bash
cargo run --release
```

Die Demo führt folgende Schritte aus:

1. Erzeugt Identitätsschlüssel für Alice und Bob
2. Initialisiert Signal-Protokoll-Dienste
3. Erzeugt und tauscht Pre-Key-Bundles aus
4. Richtet verschlüsselte Sessions ein
5. Tauscht verschlüsselte Nachrichten aus
6. Erstellt und signiert Mesh-Pakete
7. Verifiziert Paketsignaturen
8. Serialisiert und deserialisiert Pakete
9. Demonstriert In-Process-Transport

## Konstanten

Alle Protokollkonstanten sind in `src/constants.rs` definiert und stimmen mit der C#-Spezifikation überein:

- Routing: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Sicherheit: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Transport: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- Sprache/Stream: Verschiedene Bitrate- und Pufferkonfigurationen

## Abhängigkeiten

- `ed25519-dalek` — Ed25519-Signierung
- `x25519-dalek` — X25519-Schlüsselvereinbarung
- `aes-gcm` — AES-256-GCM-Verschlüsselung
- `hkdf` — HKDF-Schlüsselableitung
- `sha2` — SHA-256-Hashing
- `hmac` — HMAC-Operationen
- `rand` — Zufallszahlenerzeugung
- `uuid` — GUID-Erzeugung und -Serialisierung
- `serde` + `serde_json` — Serialisierung
- `tokio` — Async-Laufzeit
- `async-trait` — Async-Trait-Methoden

## Tests

Alle Tests ausführen:

```bash
cargo test
```

Tests decken ab:

- Paketerstellung und TTL-Verwaltung
- Pakettypkonvertierung
- Serialisierungs-/Deserialisierungs-Roundtrips
- Ed25519-Schlüsselerzeugung und Signaturverifizierung
- Signal-Protokoll-Session-Aufbau und -Verschlüsselung
- Paketsignierung und Frischheitsvalidierung
- In-Process-Transportkonnektivität

## Protokollkonformität

Diese Implementierung folgt der Aether-Protokollspezifikation (Version 2.0) mit:

- ✅ Binäres Drahtformat (Little-Endian, Längen-präfixiert)
- ✅ Alle 26 Pakettypen
- ✅ Ed25519-Signierung mit Nonce-Deduplizierung
- ✅ X3DH-Schlüsselvereinbarung mit HKDF-SHA256
- ✅ AES-256-GCM-Verschlüsselung mit 12-Byte-Nonce
- ✅ Symmetrischer Ratschet mit Out-of-Order-Verarbeitung
- ✅ Pre-Key-Bundle-Erzeugung und -Verarbeitung
- ✅ Konstruktion signierbarer Paketdaten (SHA-256-Payload-Hash)
- ✅ Transport-Trait-Abstraktion

## Hinweise

- Das Drahtformat verwendet durchgehend Little-Endian-Bytereihenfolge (entspricht C# BinaryPrimitives.WriteInt32LittleEndian)
- String-Längenpräfixe verwenden u16 für UHIDs, i32 für Payload/Signatur (entspricht C# WriteUInt16/WriteInt32)
- Sämtliches kryptografisches Schlüsselmaterial wird nach der Verwendung mittels des `CryptographicOperations`-Äquivalents auf null gesetzt
- Die Signal-Protokoll-Implementierung verwendet HKDF mit Salt-Bytes [0x01] und [0x02] für das Chain-Ratcheting (entspricht C#-HKDF-Verwendung)
- Nonce-Deduplizierung verwendet eine absendergebundene VecDeque mit automatischer Bereinigung von Einträgen, die älter als 5 Minuten sind
