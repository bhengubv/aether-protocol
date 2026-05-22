# Aether Mesh Networking Protocol - Python-Implementierung

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Eine Python-Implementierung des Aether-Mesh-Netzwerkprotokolls, die drahtkompatible kryptografische Operationen mit der C#-Referenzimplementierung bereitstellt.

## Übersicht

Aether ist ein dezentrales Mesh-Netzwerkprotokoll, das für Umgebungen mit unterbrochener oder fehlender Internetverbindung konzipiert wurde. Dieses Python-Paket bietet:

- **Ed25519-Signierung**: Schlüsselerzeugung, Signierung und Verifizierung mittels PyNaCl
- **Signal-Protokoll X3DH**: Asynchroner Schlüsselaustausch mit ECDH P-256
- **AES-256-GCM-Verschlüsselung**: Symmetrische Verschlüsselung pro Nachricht mit 12-Byte-Nonces
- **HKDF-SHA256-Schlüsselableitung**: RFC-5869-konforme Schlüsselableitung mit kontextspezifischen Info-Strings
- **Symmetrischer Ratschet**: HMAC-SHA256-basierte Nachrichtenschlüsselableitung mit Forward Secrecy
- **Paketserialisierung**: Binäres Drahtformat in Little-Endian-Bytereihenfolge, kompatibel mit der C#-Implementierung
- **Schutz vor Replay-Angriffen**: Nonce-basierte Deduplizierung mit 5-Minuten-TTL
- **In-Process-Transport**: Mock-Transport zum Testen der Mesh-Kommunikation

## Installation

### Aus PyPI (nach Veröffentlichung)
```bash
pip install aether-protocol
```

### Aus dem Quellcode
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Entwicklungsabhängigkeiten
```bash
pip install -e ".[dev]"
```

## Schnellstart

```python
import asyncio
from aether.security.ed25519_service import Ed25519SigningService
from aether.security.signal_protocol import SignalProtocolService
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Architektur

### Paketstruktur

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## Hauptfunktionen

### 1. Ed25519-Signierdienst

Verwendet PyNaCl (libsodium) für kryptografische Operationen:

```python
from aether.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Schlüsselgrößen:**
- Privater Schlüssel: 32 Bytes (Ed25519-Seed)
- Öffentlicher Schlüssel: 32 Bytes (Ed25519-Punkt)
- Signatur: 64 Bytes

### 2. Signal-Protokoll

Implementiert den X3DH-Schlüsselaustausch mit symmetrischem Ratschet für Forward Secrecy:

```python
from aether.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**Schlüsselableitung:**
- Verwendet HKDF-SHA256 mit Salt: `"AetherSignal"`
- Root-Key-Info: `"aether-root-v1"`
- Sende-Chain-Info: `"aether-chain-send-v1"`
- Empfangs-Chain-Info: `"aether-chain-recv-v1"`

**Symmetrischer Ratschet:**
- Verwendet HMAC-SHA256 mit dem Chain-Key
- Leitet neue Nachrichtenschlüssel ab und rückt die Chain mit jeder Nachricht vor
- Unterstützt bis zu 1000 übersprungene Schlüssel für die Auslieferung außer der Reihe
- Nachrichtenverschlüsselung: AES-256-GCM mit zufälligem 12-Byte-Nonce

### 3. Paketserialisierung

Binäres Drahtformat, kompatibel mit der C#-Implementierung:

```python
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**Drahtformat (Little-Endian):**
- Protokollversion: 1 Byte
- Pakettyp: 1 Byte
- Paket-ID: 16 Bytes (UUID)
- Priorität: 1 Byte
- TTL: 4 Bytes (int32)
- TimestampMs: 8 Bytes (int64)
- SourceUhid-Länge: 2 Bytes + UTF-8-Daten
- DestinationUhid-Länge: 2 Bytes + UTF-8-Daten
- PacketNonce-Länge: 2 Bytes + Daten
- Payload-Länge: 4 Bytes + Daten
- Signaturlänge: 2 Bytes + Daten

### 4. Paketsignierung

Signiert Pakete mittels Ed25519 und erkennt Replay-Angriffe:

```python
from aether.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Signierbare Daten:**
Gemäß Protokollspezifikation Abschnitt 2.3 umfasst die Signatur:
- PacketNonce (8 Bytes)
- TimestampMs (8 Bytes, Little-Endian int64)
- Type (4 Bytes, Little-Endian int32)
- SourceUhid (Länge + UTF-8)
- DestinationUhid (Länge + UTF-8)
- SHA-256(Payload) (32 Bytes)
- Ttl (4 Bytes, Little-Endian int32)
- Priority (4 Bytes, Little-Endian int32)

**Replay-Schutz:**
- Führt einen Cache der gesehenen (sender_uhid, nonce)-Paare
- TTL von 5 Minuten pro Cache-Eintrag
- Automatische Bereinigung alle 60 Sekunden

### 5. Transportdienste

Abstrakte Basisklasse für physische Transporte (BLE, Wi-Fi Direct usw.):

```python
from aether.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**InProcessTransport-Merkmale:**
- Globales Klassenregister der Knoten
- Thread-sicher mittels threading.Lock
- Ideal für Tests und lokale Mesh-Simulation
- Eigenschaften: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Konstantenreferenz

Alle Protokollkonstanten sind in `aether/constants.py` definiert:

### Kryptografie
- `ED25519_PRIVATE_KEY_SIZE`: 32 Bytes
- `ED25519_PUBLIC_KEY_SIZE`: 32 Bytes
- `ED25519_SIGNATURE_SIZE`: 64 Bytes
- `AES_GCM_NONCE_SIZE`: 12 Bytes
- `AES_GCM_TAG_SIZE`: 16 Bytes
- `MAX_SKIPPED_KEYS`: 1000

### Routing
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(Vollständige Liste siehe `constants.py`)

## Demo ausführen

Demonstriert alle wesentlichen Funktionen mit farbiger Ausgabe:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

Die Demo umfasst:
1. Ed25519-Schlüsselerzeugung und -Signierung
2. Knotenerstellung mit AetherNode
3. Signal-Protokoll X3DH-Schlüsselaustausch
4. Nachrichtenverschlüsselung und -entschlüsselung
5. Paketserialisierung/-deserialisierung
6. Paketsignierung und Erkennung von Replay-Angriffen
7. In-Process-Transportkommunikation
8. Vollständiger Ende-zu-Ende-Verschlüsselungsworkflow

## Abhängigkeiten

### Laufzeit
- `pynacl>=1.5.0` - Ed25519-Signierung via libsodium
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Entwicklung
- `pytest>=7.4.0` - Test-Framework
- `pytest-asyncio>=0.21.0` - Unterstützung für asynchrone Tests
- `black>=23.0.0` - Code-Formatierung
- `mypy>=1.5.0` - Statische Typprüfung
- `ruff>=0.1.0` - Linting

## Kompatibilität

**Python-Version:** 3.10+

**Plattform:** Plattformübergreifend (Windows, macOS, Linux)

**Kryptografisches Backend:** Verwendet System-libsodium und die Backends der cryptography-Bibliothek, was ein konsistentes Verhalten auf allen Plattformen gewährleistet.

## Protokollreferenzen

- **AODV-Routing:** RFC 3561
- **X3DH-Schlüsselvereinbarung:** Signal Foundation, November 2016
- **Double Ratchet:** Signal Foundation, November 2016
- **HKDF:** RFC 5869 (HMAC-basiertes Extract-and-Expand)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB et al., 2012

## Sicherheitserwägungen

### Schlüssel-Nullsetzung
Intermediäres kryptografisches Material wird nach der Verwendung auf null gesetzt:
- Gemeinsame Geheimnisse aus ECDH
- Nachrichtenschlüssel aus dem symmetrischen Ratschet
- Abgeleitetes Schlüsselmaterial im Einrichtungskontext

In Python ist eine echte In-place-Speicher-Nullsetzung begrenzt, aber sensible Daten werden unmittelbar nach der Verwendung aus dem Variablenbereich entfernt.

### Bedrohungsmodell
Aether geht von folgenden Bedrohungen aus:
- Passives Abhören über BLE/Wi-Fi
- Aktive Paketeinschleusung und Replay-Angriffe
- Sybil-Angriffe über gefälschte Knotenerstellung
- Selektive Dienstverweigerung

Schutzmaßnahmen umfassen:
- **Vertraulichkeit:** AES-256-GCM-Schlüssel pro Nachricht
- **Integrität:** Ed25519-Paketsignaturen
- **Replay-Schutz:** Nonce-basierte Deduplizierung
- **Forward Secrecy:** Symmetrischer Ratschet mit Schlüsseln pro Nachricht
- **Routenauthentifizierung:** Signierte Route Replies

### Einschränkungen
- Die Auslieferung von Nachrichten außer der Reihe wird bis zu 1000 Nachrichten unterstützt
- Nachrichten jenseits der Lücke werden abgelehnt
- BLE-Adressen rotieren alle 15 Minuten (nicht in Python implementiert)
- Das Migrationsfenster von P-256 zu Ed25519 beträgt 30 Tage (Fallback noch nicht implementiert)

## Tests

Testsuite ausführen:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## Lizenz

MIT-Lizenz - Einzelheiten siehe LICENSE-Datei

## Mitwirken

Zur Einreichung von Verbesserungen:

1. Sicherstellen, dass der Code dem PEP-8-Stil entspricht (Formatierung mit `black`)
2. Typenhinweise zu allen Funktionen hinzufügen
3. Docstrings für öffentliche APIs einfügen
4. `mypy` für die Typprüfung ausführen
5. Tests für neue Funktionen hinzufügen

## Referenzen

- Aether Protocol Spec: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# Reference Implementation: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
