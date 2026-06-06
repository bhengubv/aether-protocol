# Aether Mesh Protocol - TypeScript-Implementierung

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Eine vollständige TypeScript/Node.js-Implementierung des Aether-Mesh-Netzwerkprotokolls, vollständig drahtformat-kompatibel mit der C#-Referenzimplementierung.

## Funktionen

- **MeshPacket-Serialisierung**: Binäres Drahtformat, das dem C#-Format exakt entspricht (Little-Endian-Ganzzahlen, Längen-präfixierte Strings/Arrays)
- **Ed25519-Signierung**: Verwendung von TweetNaCl zur Signaturerzeugung und -verifizierung
- **Signal-Protokoll**: X3DH-Schlüsselaustausch mit HKDF-SHA256-Schlüsselableitung und AES-256-GCM-Verschlüsselung
- **Paketsignierung**: Vollständige Konstruktion signierbarer Daten gemäß Protokollspezifikation (Abschnitt 2.3)
- **In-Process-Transport**: Simuliertes Netzwerk für Tests und Demos
- **Symmetrischer Ratschet**: HMAC-SHA256-Chain-Key-Vorschub mit Out-of-Order-Nachrichtenunterstützung
- **Protokollkonstanten**: Alle 60+ Konstanten aus PROTOCOL_SPEC Abschnitt A

## Installation

```bash
npm install
```

## Verwendung

### Build

```bash
npm run build
```

### Demo ausführen

```bash
npm run dev
```

Die Demo:
1. Erstellt 2 Knoten in einem simulierten In-Process-Netzwerk
2. Erzeugt Ed25519-Schlüsselpaare
3. Richtet Signal-Protokoll-Sessions ein
4. Erstellt, signiert und verifiziert ein Paket
5. Serialisiert und deserialisiert Pakete
6. Verschlüsselt und entschlüsselt Nachrichten
7. Sendet Pakete über die Transportschicht

### API-Beispiele

#### Paketerstellung & Signierung

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Signal-Protokoll-Verschlüsselung

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### Paketserialisierung

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### In-Process-Transport

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## Protokollkonformität

### Drahtformat

Alle Mehrbyte-Ganzzahlen sind **Little-Endian**:
- Paket-ID: 16-Byte-UUID
- TTL, TimestampMs: int32/int64 LE
- String-Längen: uint16 LE (nicht uint32)
- Payload-Länge: int32 LE

### Paketsignierung (Abschnitt 2.3)

Format der signierbaren Daten:
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Signal-Protokoll (Abschnitt 4)

- **Schlüsselaustausch**: X3DH mit ECDH P-256
- **HKDF**: SHA256 mit Salt="AetherNetSignal"
- **Info-Strings**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Verschlüsselung**: AES-256-GCM mit 12-Byte-Nonce, 16-Byte-Tag
- **Chain-Ratschet**: HMAC-SHA256 mit Zähler-Vorschub

## Pakettypen

Alle 23 Pakettypen sind definiert:
- RouteRequest (1) - AODV-Routenanfrage
- RouteReply (2) - AODV-Routen-Antwort
- Data (3) - Anwendungsdaten
- Ack (4) - Auslieferungsbestätigung
- SosBroadcast (5) - Notfall-Broadcast
- ... und 18 weitere (siehe Protokollspezifikation)

## Sicherheitsfunktionen

- **Ed25519-Signaturen**: Alle Pakete gemäß v2-Protokoll signiert
- **AES-256-GCM**: Schlüssel pro Nachricht mit eindeutigen Nonces
- **Replay-Schutz**: 8-Byte-Zufalls-Nonce + Zeitstempel-Validierung
- **Forward Secrecy**: Symmetrischer Ratschet rückt Chain-Keys vor
- **Out-of-Order-Entschlüsselung**: Cache für übersprungene Nachrichtenschlüssel (bis zu 1000)

## Projektstruktur

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## Tests

Die Demo (`npm run dev`) übbt alle wesentlichen Funktionen:
- Paketerstellung und Serialisierung (Roundtrip)
- Ed25519-Schlüsselerzeugung und Signaturverifizierung
- Signal-Protokoll-Session-Aufbau
- Nachrichtenverschlüsselung und -entschlüsselung
- In-Process-Transportauslieferung

Für Unit-Tests kann Jest oder ein ähnlicher Test-Runner eingebunden werden.

## Kompatibilitätshinweise

- **C#-Drahtformat**: 100% kompatibel mit C# PacketSerializer
- **Signierte Pakete**: Protokollversion 2 mit Ed25519-Signaturen
- **HKDF-Ableitung**: Verwendung von @noble/hashes (reine JavaScript-Implementierung)
- **ECDH**: Eingebautes Node.js-Krypto-Modul (P-256-Kurve)

## Abhängigkeiten

- **tweetnacl**: Ed25519-Signaturen via TweetNaCl
- **@noble/hashes**: HKDF-SHA256-Schlüsselableitung
- **uuid**: UUID-Erzeugung und -Parsing
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## Lizenz

MIT - Einzelheiten siehe LICENSE-Datei

## Referenzen

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# Implementation](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
