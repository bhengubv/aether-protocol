# Protocolo Aether - Implementación en Swift

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Una implementación completa en Swift del protocolo de red en malla Aether, que proporciona cifrado de extremo a extremo, enrutamiento y comunicación entre pares para iOS y macOS.

## Descripción general

Aether es un protocolo de red en malla descentralizado diseñado para entornos con conectividad a internet intermitente o nula. Esta implementación en Swift ofrece:

- **Serialización compatible a nivel de cable** con la implementación de referencia en C#
- **Firma Ed25519** para autenticación de paquetes
- **Signal Protocol** (X3DH + Trinquete Simétrico) para cifrado de extremo a extremo
- **Abstracción de transporte** compatible con múltiples capas físicas (BLE, Wi-Fi Direct, NearLink)
- **APIs asíncronas seguras para hilos** usando Swift Concurrency

## Requisitos

- Swift 5.9+
- macOS 13.0+ o iOS 16.0+
- Xcode 15+

## Dependencias

- [swift-crypto](https://github.com/apple/swift-crypto) - Primitivas criptográficas (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## Arquitectura

### Componentes principales

#### Capa de protocolo
- **MeshPacket**: Estructura central del paquete (UUID, tipo, UHIDs de origen/destino, TTL, prioridad, carga útil, firma)
- **PacketType**: Enumeración de 26 tipos de paquetes (RouteRequest, Data, SosBroadcast, DtnBundle, etc.)
- **PacketSerializer**: Serializador/deserializador binario con formato de cable little-endian

#### Capa de seguridad
- **Ed25519Service**: Generación de claves, firma y verificación usando Curve25519
- **SignalProtocolService**: Acuerdo de claves X3DH + trinquete simétrico para sesiones cifradas
- **PacketSigningService**: Firma a nivel de paquete con deduplicación de nonces y prevención de repetición

#### Capa de transporte
- **TransportService**: Protocolo que define el contrato de transporte
- **InProcessTransport**: Transporte en memoria para pruebas y comunicación local

#### Modelos
- **AetherNode**: Representación de nodo con UHID y clave de identidad
- **PreKeyBundle**: Paquete para establecimiento de sesión asíncrono
- **EncryptedPayload**: Envoltorio de mensaje cifrado
- **DtnBundle**: Paquete de red tolerante a retardos
- **PeerInfo**: Información de par en la tabla de enrutamiento

### Constantes
Todas las constantes del protocolo (TTLs, tiempos de espera, límites de capacidad) están definidas en `ProtocolConstants`.

## Instalación

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

En su Package.swift:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherProtocol", package: "aether-protocol-swift")
    ]
)
```

## Inicio rápido

### 1. Serialización de paquetes

```swift
import AetherProtocol

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

### 2. Firma Ed25519

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Sesión de Signal Protocol

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

### 4. Firma de paquetes

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. Transporte en proceso (Pruebas)

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

## Formato de cable

Todos los paquetes se ajustan al formato de cable little-endian:

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

Tamaño mínimo del paquete con UHIDs y carga útil vacíos: **43 bytes**.

## Modelo de seguridad

### Cifrado
- **Algoritmo**: AES-256-GCM
- **Derivación de claves**: HKDF-SHA256 desde el secreto compartido X3DH
- **Trinquete de sesión**: El trinquete simétrico avanza la clave de cadena por mensaje

### Firma
- **Algoritmo**: Ed25519 (Curve25519)
- **Protección de carga útil**: Hash SHA256 incluido en los datos firmables
- **Prevención de repetición**: Nonce de 8 bytes + marca de tiempo en milisegundos + caché de deduplicación

### Intercambio de claves
- **Protocolo**: Variante X3DH con ECDH P-256
- **Vinculación de pre-clave**: Pre-clave firmada verificada con Ed25519
- **Asíncrono**: Sesiones establecidas sin necesidad de que el destinatario esté en línea

### Límites
- **MaxSkippedKeys**: 1.000 (mensajes fuera de orden por sesión)
- **MaxPacketAge**: 300 segundos (5 minutos)

## Constantes del protocolo

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5.000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 bytes
- **AesGcmTagSize**: 16 bytes

Consulte `ProtocolConstants` para la lista completa.

## Seguridad de hilos

Todos los servicios están aislados como `actor` para acceso concurrente seguro:

- `SignalProtocolService` - Gestión de sesiones y cifrado
- `PacketSigningService` - Firma y verificación de paquetes
- `InProcessTransport` - Entrega de mensajes

Uso con Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## Pruebas

Ejecutar la demostración incluida:

```bash
cd swift
swift run aether-demo
```

Salida esperada:

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

## Interoperabilidad

El formato de cable es compatible con:
- **Aether.Core** (C#) - Implementación de referencia
- **aether-protocol-go** - Implementación en Go
- **aether-protocol-rust** - Implementación en Rust

Todas las implementaciones usan:
- Enteros little-endian
- Codificación de cadenas UTF-8
- Firmas Ed25519 (64 bytes)
- Cifrado AES-256-GCM (nonce de 12 bytes, etiqueta de 16 bytes)

## Rendimiento

Benchmarks en Apple Silicon (M1 Pro):

| Operación | Tiempo |
|-----------|--------|
| Serialización de paquetes | ~0,5 μs |
| Deserialización de paquetes | ~0,7 μs |
| Firma Ed25519 | ~3,5 ms |
| Verificación Ed25519 | ~4,2 ms |
| Cifrado AES-256-GCM | ~0,8 μs |
| Descifrado AES-256-GCM | ~0,9 μs |
| Acuerdo de claves X3DH | ~8,5 ms |
| Trinquete simétrico | ~0,3 μs |

## Trabajo futuro

- **Transporte BLE**: Implementación de Bluetooth Low Energy
- **Transporte Wi-Fi Direct**: Wi-Fi directo entre pares
- **Double Ratchet**: Secreto hacia adelante completo con trinquete de mensajes
- **Enrutamiento AODV**: Descubrimiento y mantenimiento de rutas
- **Servicio DTN**: Entrega de paquetes store-and-forward
- **Presencia y proximidad**: Descubrimiento de pares con conciencia de ubicación
- **Voz y transmisión**: Protocolos de medios en tiempo real

## Licencia

MIT - Consulte el archivo LICENSE

## Referencias

1. [Especificación del protocolo Aether](../docs/PROTOCOL_SPEC.md)
2. [Triple Diffie-Hellman extendido (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Algoritmo Double Ratchet](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Firmas Ed25519](https://en.wikipedia.org/wiki/Curve25519)
6. [Modo AES-GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## Contribuciones

Esta es una implementación de referencia. Para reportes de errores y solicitudes de características, por favor abra un issue en GitHub.
