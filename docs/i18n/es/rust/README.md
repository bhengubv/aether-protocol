# Protocolo Aether — Implementación en Rust

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Implementación completa en Rust del protocolo de red en malla Aether, con compatibilidad de formato de cable con la implementación de referencia en C#.

## Descripción general

Este crate proporciona:

- **Serialización/deserialización de MeshPacket** — Formato binario de cable que coincide exactamente con C# PacketSerializer
- **Firma Ed25519** — Generación de claves de identidad, firma y verificación
- **Signal Protocol** — Acuerdo de claves basado en X3DH con trinquete simétrico para secreto hacia adelante
- **Servicio de firma de paquetes** — Deduplicación de nonces y verificación de vigencia
- **Transporte en proceso** — Red en malla simulada para pruebas y demostraciones

## Estructura del proyecto

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

## Características principales

### 1. Compatibilidad de formato de cable

El `PacketSerializer` produce una salida byte a byte idéntica a la implementación en C#:

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

Todos los enteros multibyte usan orden de bytes little-endian. Las longitudes de cadena van prefijadas con u16 (SourceUhid, DestinationUhid) o i32 (Payload, Signature) según lo especificado en la especificación del protocolo.

### 2. Tipos de paquetes

Los 26 tipos de paquetes de la especificación del protocolo están definidos:

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

### 3. Firma Ed25519

- Claves privadas de 32 bytes (semilla), claves públicas de 32 bytes, firmas de 64 bytes
- Usa `ed25519-dalek` para operaciones criptográficas
- Borrado seguro de claves tras su uso

### 4. Signal Protocol

Acuerdo de claves basado en X3DH con trinquete simétrico:

- **Acuerdo de claves:** ECDH P-256 usando pre-claves efímeras y firmadas
- **Derivación de claves:** HKDF-SHA256 con cadenas de información únicas
  - `aether-root-v1` — Clave raíz
  - `aether-chain-send-v1` — Clave de cadena de envío
  - `aether-chain-recv-v1` — Clave de cadena de recepción
- **Cifrado:** AES-256-GCM (nonce de 12 bytes, etiqueta de 16 bytes)
- **Trinquete:** Avance de clave de cadena simétrica con claves de mensaje basadas en contador
- **Manejo fuera de orden:** Hasta 1.000 claves de mensajes omitidos en caché

### 5. Servicio de firma de paquetes

- Generación de nonce aleatorio de 8 bytes
- Marcas de tiempo con precisión de milisegundos
- Validación de vigencia (ventana de 5 minutos)
- Deduplicación de nonces por remitente (previene repeticiones)
- Limpieza automática de entradas vencidas

### 6. Transporte en proceso

Red en malla simulada para pruebas:

- Registro estático de nodos usando HashMap concurrente
- Entrega de mensajes sin espera de confirmación
- Verificaciones de conectividad bidireccional entre pares
- Adecuado para demostraciones y pruebas unitarias

## Uso

### Generación básica de claves y firma

```rust
use aether_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Sesión de Signal Protocol

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

### Serialización de paquetes

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

### Firma de paquetes

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

### Transporte en proceso

```rust
use aether_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Ejecución de la demostración

```bash
cargo run --release
```

La demostración realiza los siguientes pasos:

1. Genera claves de identidad para Alice y Bob
2. Inicializa los servicios de Signal Protocol
3. Genera e intercambia paquetes de pre-claves
4. Establece sesiones cifradas
5. Intercambia mensajes cifrados
6. Crea y firma paquetes de malla
7. Verifica firmas de paquetes
8. Serializa y deserializa paquetes
9. Demuestra el transporte en proceso

## Constantes

Todas las constantes del protocolo están definidas en `src/constants.rs`, coincidiendo con la especificación de C#:

- Enrutamiento: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Seguridad: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Transporte: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- Voz/Transmisión: Diversas configuraciones de tasa de bits y búfer

## Dependencias

- `ed25519-dalek` — Firma Ed25519
- `x25519-dalek` — Acuerdo de claves X25519
- `aes-gcm` — Cifrado AES-256-GCM
- `hkdf` — Derivación de claves HKDF
- `sha2` — Hash SHA-256
- `hmac` — Operaciones HMAC
- `rand` — Generación de números aleatorios
- `uuid` — Generación y serialización de GUID
- `serde` + `serde_json` — Serialización
- `tokio` — Tiempo de ejecución asíncrono
- `async-trait` — Métodos de trait asíncronos

## Pruebas

Ejecutar todas las pruebas:

```bash
cargo test
```

Las pruebas cubren:

- Creación de paquetes y gestión de TTL
- Conversión de tipos de paquetes
- Ciclos completos de serialización/deserialización
- Generación de claves Ed25519 y verificación de firmas
- Establecimiento de sesiones y cifrado con Signal Protocol
- Firma de paquetes y validación de vigencia
- Conectividad del transporte en proceso

## Conformidad con el protocolo

Esta implementación sigue la especificación del protocolo Aether (Versión 2.0) con:

- ✅ Formato binario de cable (little-endian, prefijado con longitud)
- ✅ Los 26 tipos de paquetes
- ✅ Firma Ed25519 con deduplicación de nonces
- ✅ Acuerdo de claves X3DH con HKDF-SHA256
- ✅ Cifrado AES-256-GCM con nonce de 12 bytes
- ✅ Trinquete simétrico con manejo fuera de orden
- ✅ Generación y procesamiento de paquetes de pre-claves
- ✅ Construcción de datos firmables de paquetes (hash SHA-256 del payload)
- ✅ Abstracción de trait de transporte

## Notas

- El formato de cable usa orden de bytes little-endian en todo momento (coincidiendo con C# BinaryPrimitives.WriteInt32LittleEndian)
- Los prefijos de longitud de cadena usan u16 para UHIDs e i32 para payload/firma (coincidiendo con C# WriteUInt16/WriteInt32)
- Todo el material de clave criptográfica se borra tras su uso mediante el equivalente de `CryptographicOperations`
- La implementación de Signal Protocol usa HKDF con bytes de sal [0x01] y [0x02] para el avance de cadena (coincidiendo con el uso de HKDF en C#)
- La deduplicación de nonces usa un VecDeque por remitente con limpieza automática de entradas anteriores a 5 minutos
