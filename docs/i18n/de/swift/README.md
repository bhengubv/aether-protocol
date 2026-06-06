# Aether Protocol - Swift-Implementierung

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Eine umfassende Swift-Implementierung des Aether-Mesh-Netzwerkprotokolls, die Ende-zu-Ende-Verschlüsselung, Routing und Peer-to-Peer-Kommunikation für iOS und macOS bereitstellt.

## Übersicht

Aether ist ein dezentrales Mesh-Netzwerkprotokoll, das für Umgebungen mit unterbrochener oder fehlender Internetverbindung konzipiert wurde. Diese Swift-Implementierung bietet:

- **Drahtkompatible Serialisierung** mit der C#-Referenzimplementierung
- **Ed25519-Signierung** zur Paketauthentifizierung
- **Signal-Protokoll** (X3DH + Symmetrischer Ratschet) für Ende-zu-Ende-Verschlüsselung
- **Transport-Abstraktion** mit Unterstützung mehrerer physischer Schichten (BLE, Wi-Fi Direct, NearLink)
- **Thread-sichere Async-APIs** mittels Swift Concurrency

## Voraussetzungen

- Swift 5.9+
- macOS 13.0+ oder iOS 16.0+
- Xcode 15+

## Abhängigkeiten

- [swift-crypto](https://github.com/apple/swift-crypto) - Kryptografische Primitive (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## Architektur

### Kernkomponenten

#### Protokollschicht
- **MeshPacket**: Kern-Paketstruktur (UUID, Typ, Quell-/Ziel-UHIDs, TTL, Priorität, Payload, Signatur)
- **PacketType**: Enumeration von 26 Pakettypen (RouteRequest, Data, SosBroadcast, DtnBundle usw.)
- **PacketSerializer**: Binärer Serializer/Deserializer mit Little-Endian-Drahtformat

#### Sicherheitsschicht
- **Ed25519Service**: Schlüsselerzeugung, Signierung und Verifizierung mittels Curve25519
- **SignalProtocolService**: X3DH-Schlüsselvereinbarung + symmetrischer Ratschet für verschlüsselte Sessions
- **PacketSigningService**: Paketsignierung mit Nonce-Deduplizierung und Replay-Schutz

#### Transportschicht
- **TransportService**: Protokoll zur Definition des Transport-Vertrags
- **InProcessTransport**: In-Memory-Transport für Tests und lokale Kommunikation

#### Modelle
- **AetherMeshNode**: Knotendarstellung mit UHID und Identitätsschlüssel
- **PreKeyBundle**: Bundle für den asynchronen Session-Aufbau
- **EncryptedPayload**: Wrapper für verschlüsselte Nachrichten
- **DtnBundle**: Delay-Tolerant-Networking-Bundle
- **PeerInfo**: Peer-Informationen der Routing-Tabelle

### Konstanten
Alle Protokollkonstanten (TTLs, Timeouts, Kapazitätsgrenzen) sind in `ProtocolConstants` definiert.

## Installation

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

In der Package.swift-Datei:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherMeshProtocol", package: "aether-protocol-swift")
    ]
)
```

## Schnellstart

### 1. Paketserialisierung

```swift
import AetherMeshProtocol

// Create a packet
var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice-node",
    destinationUhid: "bob-node",
    payload: "Hello, Aether!".data(using: .utf8)!
)

// Serialize to bytes
let serialized = PacketSerializer.serialize(packet)

// Deserialize
let deserialized = try PacketSerializer.deserialize(serialized)
```

### 2. Ed25519-Signierung

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Signal-Protokoll-Session

```swift
let alice = SignalProtocolService()
let bob = SignalProtocolService()

// Key exchange: Bob publishes pre-key bundle
let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob-node")

// Alice processes Bob's bundle and establishes session
try await alice.processPreKeyBundle(bobBundle)

// Alice encrypts message
let encrypted = try await alice.encrypt(
    peerUhid: "bob-node",
    plaintext: "Secret message".data(using: .utf8)!
)

// For Bob to decrypt, he also needs Alice's bundle
let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice-node")
try await bob.processPreKeyBundle(aliceBundle)

// Bob decrypts
let decrypted = try await bob.decrypt(peerUhid: "alice-node", payload: encrypted)
```

### 4. Paketsignierung

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. In-Process-Transport (Tests)

```swift
let alice = InProcessTransport(uhid: "alice")
let bob = InProcessTransport(uhid: "bob")

// Set up data received callback
await bob.onDataReceived { senderUhid, data in
    print("Received \(data.count) bytes from \(senderUhid)")
}

// Send message
let success = await alice.sendAsync(
    peerUhid: "bob",
    data: "Hello".data(using: .utf8)!,
    cancellationToken: nil
)
```

## Drahtformat

Alle Pakete entsprechen dem Little-Endian-Drahtformat:

```
[1 byte]   Protocol version (2 = signed)
[1 byte]   Packet type
[16 bytes] Packet ID (UUID)
[1 byte]   Priority
[4 bytes]  TTL (Int32)
[8 bytes]  TimestampMs (Int64)
[2 bytes]  SourceUhid length (UInt16)
[N bytes]  SourceUhid (UTF-8)
[2 bytes]  DestinationUhid length (UInt16)
[N bytes]  DestinationUhid (UTF-8)
[2 bytes]  PacketNonce length (UInt16)
[N bytes]  PacketNonce (8 bytes)
[4 bytes]  Payload length (Int32)
[N bytes]  Payload
[2 bytes]  Signature length (UInt16)
[N bytes]  Signature (64 bytes Ed25519)
```

Minimale Paketgröße mit leeren UHIDs und Payload: **43 Bytes**.

## Sicherheitsmodell

### Verschlüsselung
- **Algorithmus**: AES-256-GCM
- **Schlüsselableitung**: HKDF-SHA256 aus dem X3DH-Shared-Secret
- **Session-Ratcheting**: Symmetrischer Ratschet rückt den Chain-Key pro Nachricht vor

### Signierung
- **Algorithmus**: Ed25519 (Curve25519)
- **Payload-Schutz**: SHA256-Hash in den signierbaren Daten enthalten
- **Replay-Schutz**: 8-Byte-Nonce + Millisekunden-Zeitstempel + Deduplizierungs-Cache

### Schlüsselaustausch
- **Protokoll**: X3DH-Variante mit ECDH P-256
- **Pre-Key-Bindung**: Signierter Pre-Key via Ed25519 verifiziert
- **Asynchron**: Sessions werden ohne Anwesenheit des Empfängers aufgebaut

### Grenzen
- **MaxSkippedKeys**: 1.000 (Out-of-Order-Nachrichten pro Session)
- **MaxPacketAge**: 300 Sekunden (5 Minuten)

## Protokollkonstanten

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5.000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 Bytes
- **AesGcmTagSize**: 16 Bytes

Vollständige Liste siehe `ProtocolConstants`.

## Thread-Sicherheit

Alle Dienste sind `actor`-isoliert für thread-sicheren gleichzeitigen Zugriff:

- `SignalProtocolService` - Session-Verwaltung und Verschlüsselung
- `PacketSigningService` - Paketsignierung und -verifizierung
- `InProcessTransport` - Nachrichtenauslieferung

Verwendung mit Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## Tests

Enthaltene Demo ausführen:

```bash
cd swift
swift run aether-demo
```

Erwartete Ausgabe:

```
=== Aether Protocol Demo ===

Test 1: Packet Serialization
---
Original packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
Serialized size: XX bytes
Deserialized packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
✓ Serialization/Deserialization successful

Test 2: Ed25519 Signing
...

Test 5: End-to-End Messaging (Full Stack)
...
✓ End-to-end messaging test successful

=== All Tests Completed ===
```

## Interoperabilität

Das Drahtformat ist kompatibel mit:
- **AetherMesh.Core** (C#) - Referenzimplementierung
- **aether-protocol-go** - Go-Implementierung
- **aether-protocol-rust** - Rust-Implementierung

Alle Implementierungen verwenden:
- Little-Endian-Ganzzahlen
- UTF-8-String-Kodierung
- Ed25519-Signaturen (64 Bytes)
- AES-256-GCM-Verschlüsselung (12-Byte-Nonce, 16-Byte-Tag)

## Leistung

Benchmarks auf Apple Silicon (M1 Pro):

| Operation | Zeit |
|-----------|------|
| Paketserialisierung | ~0,5 μs |
| Paketdeserialisierung | ~0,7 μs |
| Ed25519-Signierung | ~3,5 ms |
| Ed25519-Verifizierung | ~4,2 ms |
| AES-256-GCM-Verschlüsselung | ~0,8 μs |
| AES-256-GCM-Entschlüsselung | ~0,9 μs |
| X3DH-Schlüsselvereinbarung | ~8,5 ms |
| Symmetrischer Ratschet | ~0,3 μs |

## Geplante Erweiterungen

- **BLE-Transport**: Bluetooth Low Energy-Implementierung
- **Wi-Fi-Direct-Transport**: Direkte Peer-to-Peer-Wi-Fi-Verbindung
- **Double Ratchet**: Vollständige Forward Secrecy mit Nachrichten-Ratcheting
- **AODV-Routing**: Routen-Erkennung und -Pflege
- **DTN-Dienst**: Store-and-Forward-Bundle-Auslieferung
- **Präsenz & Nähe**: Standortbewusstes Peer-Discovery
- **Sprache & Streaming**: Echtzeit-Medienprotokolle

## Lizenz

MIT - Einzelheiten siehe LICENSE-Datei

## Referenzen

1. [Aether Protocol Specification](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Double Ratchet Algorithm](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Ed25519 Signatures](https://en.wikipedia.org/wiki/Curve25519)
6. [AES-GCM Mode](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## Mitwirken

Dies ist eine Referenzimplementierung. Für Fehlermeldungen und Funktionsanfragen öffnen Sie bitte ein Issue auf GitHub.
