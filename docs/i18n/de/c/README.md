# Aether Mesh-Netzwerkprotokoll – C-Implementierung

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Eine hochperformante, eingebettete C-Implementierung des Aether-Mesh-Netzwerkprotokolls, optimiert für ressourcenbeschränkte Geräte wie ESP32 und nRF52. Vollständige Unterstützung für Ed25519-Signaturen, AES-256-GCM-Verschlüsselung und AODV-basiertes Routing.

## Überblick

Aether ist ein dezentrales Mesh-Netzwerkprotokoll für Umgebungen mit intermittierender oder fehlender Internetverbindung. Diese C-Implementierung bietet:

- **Protokoll-Serialisierung/Deserialisierung** — Little-Endian-Drahtformat, das der C#-Referenzimplementierung entspricht
- **Kryptografische Operationen** — Ed25519-Signaturen, AES-256-GCM-Verschlüsselung, HMAC-SHA256, HKDF-SHA256 (via libsodium)
- **Paketsignierung** — deterministische Konstruktion signierbarer Daten gemäß Protokollspezifikation
- **Transportabstraktion** — vtable-Muster für benutzerdefinierte Transportimplementierungen
- **In-Process-Transport** — eingebauter Test-Transport für Multi-Knoten-Szenarien
- **Embedded-First-Design** — feste Puffergrössen wo möglich, minimale Allokation, zeitkonstante Operationen

## Build-Voraussetzungen

- **CMake** ≥ 3.16
- **C11-Compiler** (gcc, clang usw.)
- **libsodium** — für kryptografische Operationen
- **POSIX-Threads** (pthread)

### macOS

```bash
# Install libsodium using Homebrew
brew install libsodium

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### Linux (Ubuntu/Debian)

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF (ESP32)

Die Bibliothek ist als ESP-IDF-Komponente konzipiert:

```bash
# In your ESP-IDF project components directory
cp -r /Users/admin/Code/Dev/aether-protocol/c/include aether
cp -r /Users/admin/Code/Dev/aether-protocol/c/src aether/

# Create idf_component.yml
cat > aether/idf_component.yml << 'EOF'
version: "1.0.0"
description: "Aether Mesh Networking Protocol"
dependencies:
  libsodium: "*"
EOF

# In your project's CMakeLists.txt
idf_component_register(
    INCLUDE_DIRS "aether/include"
    SRCS "aether/src/protocol.c" "aether/src/security.c" "aether/src/transport_inprocess.c"
    REQUIRES libsodium pthread
)
```

## Struktur

```
c/
├── include/aether/
│   ├── constants.h       # Protocol constants and limits
│   ├── protocol.h        # Packet structure and serialization
│   ├── security.h        # Cryptographic operations
│   └── transport.h       # Transport abstraction
├── src/
│   ├── protocol.c        # Serialization implementation
│   ├── security.c        # Cryptography using libsodium
│   ├── transport_inprocess.c  # In-process test transport
│   └── demo.c            # Example usage
├── tests/
│   ├── CMakeLists.txt
│   └── test_protocol.c   # Unit tests
├── CMakeLists.txt
└── README.md
```

## Schnellstart

### Demo erstellen und ausführen

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

Die erwartete Ausgabe demonstriert:
1. Ed25519-Schlüsselerzeugung
2. Paketerstellung und -signierung
3. Serialisierung in das Drahtformat
4. Deserialisierung
5. AES-256-GCM-Ver-/Entschlüsselung
6. HMAC-SHA256-Authentifizierung
7. HKDF-Schlüsselableitung

### Unit-Tests ausführen

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Verwendung im eigenen Code

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    if (!packet) return 1;

    // Set fields
    aethernet_packet_set_source_uhid(packet, "node-alice");
    aethernet_packet_set_destination_uhid(packet, "node-bob");
    aethernet_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    aethernet_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];
        aethernet_ed25519_sign(private_key, signable, signable_len, signature);
        aethernet_packet_set_signature(packet, signature, AETHERNET_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethernet_mesh_packet_t *received = aethernet_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethernet_packet_free(received);
    }

    aethernet_packet_free(packet);
    return 0;
}
```

## API-Referenz

### Protokoll

#### Paketverwaltung
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — Neues Paket erstellen
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — Paket freigeben
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — Paket klonen

#### Serialisierung
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — In Drahtformat serialisieren
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — Aus Drahtformat deserialisieren
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — Drahtgrösse schätzen

#### Paketfelder
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Quelle setzen
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Ziel setzen
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Nutzlast setzen
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Signatur setzen

#### Validierung
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — Ablauf prüfen
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — TTL > 0 prüfen

#### Signierdaten
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — Deterministische signierbare Bytes abrufen (Aufrufer muss freigeben)

### Sicherheit

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — 32+32-Byte-Schlüssel erzeugen
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Signieren (erzeugt 64 Bytes)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Verifizieren

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Verschlüsseln (Nonce wird automatisch erzeugt, wenn NULL)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Entschlüsseln

#### HMAC & Hash
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 Bytes)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 Bytes)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Hilfsfunktionen
- `void aethernet_zeroize(void *mem, size_t len)` — Zeitkonstantes Speicher-Nullsetzen
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — Kryptografisch zufällige Bytes

### Transport

#### Allgemeine Funktionen
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Daten senden
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — Verbindung prüfen
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — Callback registrieren
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — Aufräumen

#### In-Process-Transport
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — Gemeinsamen In-Process-Transport erstellen
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — Knoten registrieren
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — Knoten deregistrieren

## Drahtformat-Konformität

Diese Implementierung folgt der Protokollspezifikation strikt mit **Little-Endian**-Mehrbyte-Ganzzahlen:

```
[1] protocol_version
[1] type
[16] packet_id (UUID bytes)
[1] priority
[4] ttl (little-endian int32)
[8] timestamp_ms (little-endian int64)
[2] source_uhid_len (little-endian uint16)
[N] source_uhid (UTF-8)
[2] destination_uhid_len (little-endian uint16)
[N] destination_uhid (UTF-8)
[2] nonce_len (little-endian uint16)
[N] packet_nonce
[4] payload_len (little-endian int32)
[N] payload
[2] signature_len (little-endian uint16)
[N] signature (Ed25519, 64 bytes)
```

Von dieser C-Implementierung serialisierte Pakete sind zu 100 % mit der C#-Referenzimplementierung kompatibel.

## Sicherheitshinweise

### Kryptografische Bibliotheken
- **libsodium** (libsodium.org) für alle kryptografischen Operationen
- Ed25519-Signaturen und -Verifizierung
- AES-256-GCM authentifizierte Verschlüsselung
- HMAC-SHA256 und SHA-256
- HKDF-SHA256-Schlüsselableitung
- Kryptografisch sichere Zufallszahlenerzeugung

### Schlüssel-Nullsetzen
Sämtliches sensibles Material (Schlüssel, Klartext, Zwischenwerte) wird unmittelbar nach der Verwendung mittels `sodium_memzero()` aus dem Speicher gelöscht. Dies verhindert unbeabsichtigtes Schlüsselleck.

### Paketvalidierung
- Zeitstempelbasierte Deduplizierung: Pakete, die älter als 300 Sekunden sind, werden abgelehnt
- Nonce-Eindeutigkeit: 8-Byte-Zufalls-Nonce in jedem Paket
- TTL-Validierung: Pakete mit TTL=0 werden verworfen
- Signaturverifizierung: Ed25519-Signaturen sind in Protokollversion 2 obligatorisch

## Hinweise für eingebettete Geräte

### ESP32
- Erfordert den libsodium-Port für ESP-IDF (verfügbar über ESP-IDF-Komponenten)
- Feste Paketgrössenabschätzung vereinfacht die Speicherallokation
- Verwendet POSIX-Threads für Mutex-Operationen
- Puffer nach Möglichkeit auf dem Stack vorab allokieren

### nRF52
- Ähnlich wie ESP32
- BLE-GATT-Transportschicht kann über das Transport-vtable implementiert werden
- Einsatz eines RTOS wie FreeRTOS für die Verarbeitung mehrerer Pakete empfohlen

### Speicherverbrauch
- Minimales Paket: ~52 Bytes
- Maximales Paket: 65 KB (konfigurierbar über `AETHERNET_MAX_PAYLOAD_LEN`)
- Eine Peer-Tabelle mit 256 Einträgen: ~32 KB
- Ein einzelnes Mesh-Paket im Speicher: ~8 KB (Worst-Case mit maximalen Feldern)

## Leistung

Auf einem modernen x86-64-Rechner (Intel Core i9):
- **Serialisierung**: ~1–2 µs pro Paket
- **Deserialisierung**: ~1–2 µs pro Paket
- **Ed25519-Signieren**: ~100 µs
- **Ed25519-Verifizieren**: ~300 µs
- **AES-256-GCM-Verschlüsselung**: ~1 µs pro KB
- **SHA-256**: ~0,5 µs pro KB

## Tests

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Die Tests umfassen:
- Paketerstellung und -klonung
- Serialisierungs-Roundtrips
- Ed25519-Signierung und -Verifizierung
- AES-GCM-Ver-/Entschlüsselung
- HMAC-SHA256-Berechnung
- HKDF-Schlüsselableitung
- TTL- und Ablaufvalidierung
- Determinismus der signierbaren Daten

## Integration in das Aether-Ökosystem

Diese C-Bibliothek ist für die Integration mit folgenden Komponenten konzipiert:
- **AetherNetAPI** (C#) — serverseitiges Mesh-Relay und Analytik
- **AetherNet.Core** (C#) — Referenzimplementierung (interoperables Drahtformat)
- **Meshtastic** — Open-Source-Mesh-Radio-Firmware
- **esp-idf** — Espressif IoT Development Framework
- Benutzerdefinierte eingebettete Anwendungen

## Lizenz

SPDX-License-Identifier: MIT

Vollständiger Lizenztext in der LICENSE-Datei.

## Mitwirken

Beiträge sind willkommen! Bitte stellen Sie sicher, dass:
- Alle Tests bestehen (`ctest --output-on-failure`)
- Der Code C11-konform ist
- Das Drahtformat exakt mit der C#-Referenz übereinstimmt
- Alle sensiblen Daten nullgesetzt werden
- Die Dokumentation aktualisiert wird

## Referenzen

- Protokollspezifikation: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C#-Referenz: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
