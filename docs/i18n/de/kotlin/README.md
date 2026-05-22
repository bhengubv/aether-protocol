# Aether-Protokoll – Kotlin-Implementierung

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Eine vollständige, produktionsreife Kotlin-Implementierung des Aether-Mesh-Netzwerkprotokolls mit vollständiger sprachübergreifender Drahtformat-Kompatibilität zur C#-Referenzimplementierung.

## Überblick

Aether ist ein dezentrales Mesh-Netzwerkprotokoll für Umgebungen mit intermittierender oder fehlender Internetverbindung. Diese Kotlin-Implementierung bietet:

- **Drahtformat-Kompatibilität** mit C# (binäre Paketserialisierung stimmt exakt überein)
- **Ed25519-Signierung** für Paketauthentifizierung und -integrität
- **Signal-Protokoll** für Ende-zu-Ende-Verschlüsselung (X3DH-Schlüsselvereinbarung, symmetrischer Ratchet, AES-256-GCM)
- **ECDH P-256**-Schlüsselvereinbarung für den Sitzungsaufbau
- **Paketserialisierung/-deserialisierung** mit Little-Endian-Mehrbyte-Ganzzahlen
- **Replay-Schutz** durch Nonce-Deduplizierung
- **Transportabstraktion** für BLE, Wi-Fi Direct und In-Process-Messaging

## Projektstruktur

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## Build

### Voraussetzungen

- JDK 17 oder höher
- Gradle 8.0 oder höher

### Kompilieren

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Demo ausführen

```bash
./gradlew run
```

Die Demo demonstriert:
1. Erzeugung von Ed25519-Schlüsselpaaren
2. Erstellung und Austausch von Pre-Key-Bundles
3. Aufbau von Signal-Protokoll-Sitzungen
4. Paketsignierung mit Ed25519
5. Paketserialisierung/-deserialisierung
6. Nachrichtenverschlüsselung und -entschlüsselung
7. Replay-Schutz
8. In-Process-Transport-Messaging

## Hauptkomponenten

### 1. Paketserialisierung (`PacketSerializer`)

Drahtformat (Little-Endian):
- Protokollversion (1 Byte)
- Pakettyp (1 Byte)
- Paket-ID / UUID (16 Bytes)
- Priorität (1 Byte)
- TTL (4 Bytes, int32)
- TimestampMs (8 Bytes, int64)
- SourceUhid (2-Byte-Längenpräfix + UTF-8-Bytes)
- DestinationUhid (2-Byte-Längenpräfix + UTF-8-Bytes)
- PacketNonce (2-Byte-Längenpräfix + Bytes)
- Payload (4-Byte-Längenpräfix + Bytes)
- Signature (2-Byte-Längenpräfix + Bytes)

Vollständig kompatibel mit dem C#-`PacketSerializer`.

### 2. Ed25519-Signierung (`Ed25519Service`, `PacketSigning`)

- **Schlüsselerzeugung**: 32-Byte privater Schlüssel-Seed, 32-Byte öffentlicher Schlüssel
- **Signierung**: 64-Byte-Signaturen über deterministische signierbare Daten
- **Verifizierung**: Ersetzt P-256-ECDSA während der Migrationsphase
- **Format signierbarer Daten**: Entspricht exakt der C#-Spezifikation (Paket-Nonce, Zeitstempel, Typ, UHIDs, Payload-Hash, TTL, Priorität)
- **Replay-Schutz**: Nonce-Deduplizierung mit 5-minütigem TTL

### 3. Signal-Protokoll (`SignalProtocol`)

Implementiert X3DH-Schlüsselvereinbarung mit symmetrischem Ratchet:

**Sitzungsaufbau:**
- Abruf des Pre-Key-Bundles des Peers
- Verifizierung der Bundle-Signatur mit Ed25519
- Durchführung von X3DH: DH(lokale Identität, entfernte signierte Pre-Key) + DH(lokale Identität, entfernte Pre-Key)
- Ableitung von Root-Key und Chain-Keys via HKDF-SHA256

**Verschlüsselung/Entschlüsselung:**
- Symmetrischer Ratchet mit HMAC-SHA256
- AES-256-GCM mit 12-Byte zufälliger Nonce
- Pro-Nachrichten-Schlüssel mit Forward Secrecy
- Behandlung ausser-der-Reihe-Nachrichten (Skipped-Key-Cache, max. 1000 Schlüssel)

**Parameter:**
- Root-Key-Ableitungs-Info: `"aether-root-v1"`
- Send-Chain-Ableitungs-Info: `"aether-chain-send-v1"`
- Recv-Chain-Ableitungs-Info: `"aether-chain-recv-v1"`
- Nachrichten-Key-Salt: `0x01`, Chain-Key-Salt: `0x02`

### 4. Transportabstraktion (`TransportService`)

Interface für physische Transporte (BLE, Wi-Fi Direct usw.):

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport:** Referenzimplementierung mit globaler `ConcurrentHashMap` für Tests und Demos.

### 5. Domänenmodelle (`Models.kt`)

- **AetherNode**: Knotenidentität mit UHID, öffentlichem Schlüssel, Fähigkeiten, Geohash
- **PeerInfo**: Bekannter Peer mit Zuverlässigkeitsbewertung und Last-Seen-Zeitstempel
- **RouteEntry**: Routing-Tabelleneintrag mit Hop-Anzahl und Qualitätsbewertung
- **NodeCapabilities**: Bitfeld (BLE, Wi-Fi Direct, Gateway, Relay, SOS, Streaming, Voice, DTN)
- **DtnBundle**: Store-and-Forward-Bundle mit Ablaufzeit und Kopieranzahl

## Protokollkonstanten

Wesentliche Konstanten (aus `Constants.kt`):

| Kategorie | Konstante | Wert |
|-----------|-----------|------|
| Paket | DEFAULT_TTL | 7 |
| Paket | PACKET_NONCE_SIZE | 8 |
| Sicherheit | MAX_SKIPPED_KEYS | 1000 |
| Sicherheit | AES_GCM_NONCE_SIZE | 12 |
| Sicherheit | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Pakettypen

Alle 23 Pakettypen entsprechen den C#-Enum-Werten (1–23):

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## Abhängigkeiten

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — Schlüsselformat-Unterstützung
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/Await, Flow
- **org.slf4j:slf4j-api:2.0.9** — Logging
- **kotlin-stdlib** — Kotlin-Standardbibliothek

## Verwendungsbeispiele

### Schlüsselerzeugung

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Paketsignierung

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### Paketserialisierung

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Signal-Protokoll-Verschlüsselung

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## Sprachübergreifende Kompatibilität

Diese Implementierung gewährleistet **exakte Drahtformat-Kompatibilität** mit der C#-Referenzimplementierung:

- Binäres Paketformat: identisches Little-Endian-Layout
- Pakettyp-Enum: Werte stimmen exakt mit dem C#-Enum überein (1–23)
- Ed25519-Signaturen: kompatibel mit NSec/libsodium
- ECDH P-256: Standardkurve, sprachübergreifend kompatibel
- HKDF-SHA256: RFC-5869-Standardimplementierung
- AES-256-GCM: NIST-Standard mit 12-Byte-Nonce, 16-Byte-Tag

In Kotlin serialisierte Pakete können in C# deserialisiert werden und umgekehrt.

## Tests

Die Implementierung enthält eine umfassende Demo (`Demo.kt`), die folgendes abdeckt:

1. Schlüsselerzeugung und Export des öffentlichen Schlüssels
2. Erzeugung und Austausch von Pre-Key-Bundles
3. Sitzungsaufbau über das Signal-Protokoll
4. Paketerstellung, -signierung und -serialisierung
5. Paketdeserialisierung und Signaturverifizierung
6. Nachrichtenverschlüsselung und -entschlüsselung
7. Replay-Angriffsschutz
8. In-Process-Transport-Messaging

Ausführen mit:
```bash
./gradlew run
```

## Sicherheitshinweise

- **Schlüssel-Nullsetzen**: Sämtliches kryptografische Zwischenmaterial wird nach der Verwendung mit `CryptographicOperations.ZeroMemory` (Kotlin-Äquivalent: `fill(0)`) gelöscht
- **Replay-Schutz**: Nonce-Deduplizierung mit 5-minütigem TTL verhindert Replay-Angriffe
- **Forward Secrecy**: Pro-Nachrichten-Schlüssel aus dem Chain-Ratchet abgeleitet
- **Ausser-der-Reihe-Behandlung**: Skipped-Key-Cache mit max. 1000 Schlüsseln zur Vermeidung von Speichererschöpfung
- **RREP-Authentifizierung**: Route-Reply-Pakete vom Zielknoten signiert
- **Paketvertraulichkeit**: Nachrichteninhalt mit AES-256-GCM verschlüsselt

## Zukünftige Erweiterungen

Die Implementierung bietet Einstiegspunkte für:

- **BLE-Transport** (`TransportService`-Interface)
- **Wi-Fi-Direct-Transport** (gleiches Interface)
- **DTN-Epidemie-Routing** (`DtnBundle`-Modell vorhanden)
- **SOS-Broadcast** (Pakettyp definiert)
- **Präsenz-Beacons** (Pakettyp definiert)
- **Sprache und Streaming** (Pakettypen definiert)
- **Double Ratchet** (wenn Always-on-Transporte verfügbar sind)

## Protokolldokumentation

Vollständige Protokollspezifikation: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## Lizenz

SPDX-License-Identifier: MIT
