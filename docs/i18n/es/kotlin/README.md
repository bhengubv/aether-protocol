# Protocolo Aether - Implementación en Kotlin

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Una implementación completa y lista para producción en Kotlin del protocolo de redes en malla Aether, con total compatibilidad de formato de cable entre lenguajes respecto a la implementación de referencia en C#.

## Descripción General

Aether es un protocolo de redes en malla descentralizado para entornos con conectividad a Internet intermitente o inexistente. Esta implementación en Kotlin proporciona:

- **Compatibilidad de formato de cable** con C# (la serialización binaria de paquetes coincide exactamente)
- **Firma Ed25519** para autenticación e integridad de paquetes
- **Protocolo Signal** para cifrado de extremo a extremo (acuerdo de claves X3DH, trinquete simétrico, AES-256-GCM)
- **Acuerdo de claves ECDH P-256** para establecimiento de sesión
- **Serialización/deserialización de paquetes** con enteros multibyte en little-endian
- **Protección contra repetición** mediante deduplicación de nonces
- **Abstracción de transporte** para BLE, Wi-Fi Direct y mensajería en proceso

## Estructura del Proyecto

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherMeshNode, PeerInfo, DtnBundle, etc.)
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

## Compilación

### Requisitos Previos

- JDK 17 o superior
- Gradle 8.0 o superior

### Compilar

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Ejecutar la Demostración

```bash
./gradlew run
```

La demostración muestra:
1. Generación de pares de claves Ed25519
2. Creación e intercambio de bundles de pre-clave
3. Establecimiento de sesión con el Protocolo Signal
4. Firma de paquetes con Ed25519
5. Serialización/deserialización de paquetes
6. Cifrado y descifrado de mensajes
7. Protección contra repetición
8. Mensajería mediante transporte en proceso

## Componentes Principales

### 1. Serialización de Paquetes (`PacketSerializer`)

Formato de cable (little-endian):
- Versión del protocolo (1 byte)
- Tipo de paquete (1 byte)
- ID de paquete / UUID (16 bytes)
- Prioridad (1 byte)
- TTL (4 bytes, int32)
- TimestampMs (8 bytes, int64)
- SourceUhid (prefijo de longitud de 2 bytes + bytes UTF-8)
- DestinationUhid (prefijo de longitud de 2 bytes + bytes UTF-8)
- PacketNonce (prefijo de longitud de 2 bytes + bytes)
- Payload (prefijo de longitud de 4 bytes + bytes)
- Signature (prefijo de longitud de 2 bytes + bytes)

Totalmente compatible con `PacketSerializer` en C#.

### 2. Firma Ed25519 (`Ed25519Service`, `PacketSigning`)

- **Generación de claves**: semilla de clave privada de 32 bytes, clave pública de 32 bytes
- **Firma**: firmas de 64 bytes sobre datos firmables deterministas
- **Verificación**: Reemplaza P-256 ECDSA durante el período de migración
- **Formato de datos firmables**: Coincide exactamente con la especificación de C# (nonce del paquete, marca de tiempo, tipo, UHIDs, hash del payload, TTL, prioridad)
- **Protección contra repetición**: Deduplicación de nonces con TTL de 5 minutos

### 3. Protocolo Signal (`SignalProtocol`)

Implementa el acuerdo de claves X3DH con trinquete simétrico:

**Establecimiento de sesión:**
- Obtiene el bundle de pre-clave del par
- Verifica la firma del bundle con Ed25519
- Realiza X3DH: DH(identidad local, pre-clave firmada remota) + DH(identidad local, pre-clave remota)
- Deriva la clave raíz y las claves de cadena usando HKDF-SHA256

**Cifrado/Descifrado:**
- Trinquete simétrico con HMAC-SHA256
- AES-256-GCM con nonce aleatorio de 12 bytes
- Claves por mensaje con secreto hacia adelante
- Manejo de mensajes fuera de orden (caché de claves omitidas, máx. 1000 claves)

**Parámetros:**
- Información de derivación de clave raíz: `"aether-root-v1"`
- Información de derivación de cadena de envío: `"aether-chain-send-v1"`
- Información de derivación de cadena de recepción: `"aether-chain-recv-v1"`
- Sal de clave de mensaje: `0x01`, sal de clave de cadena: `0x02`

### 4. Abstracción de Transporte (`TransportService`)

Interfaz para transportes físicos (BLE, Wi-Fi Direct, etc.):

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

**InProcessTransport:** Implementación de referencia usando `ConcurrentHashMap` global para pruebas y demostración.

### 5. Modelos de Dominio (`Models.kt`)

- **AetherMeshNode**: Identidad del nodo con UHID, clave pública, capacidades, geohash
- **PeerInfo**: Par conocido con puntuación de fiabilidad y marca de tiempo del último avistamiento
- **RouteEntry**: Entrada de tabla de enrutamiento con conteo de saltos y puntuación de calidad
- **NodeCapabilities**: Campo de bits (BLE, Wi-Fi Direct, Gateway, Relay, SOS, Streaming, Voice, DTN)
- **DtnBundle**: Bundle de almacenamiento y reenvío con expiración y conteo de copias

## Constantes del Protocolo

Constantes clave (de `Constants.kt`):

| Categoría | Constante | Valor |
|-----------|-----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Tipos de Paquetes

Los 23 tipos de paquetes coinciden con los valores del enum en C# (1-23):

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

## Dependencias

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — Soporte de formato de clave
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await, Flow
- **org.slf4j:slf4j-api:2.0.9** — Registro de eventos
- **kotlin-stdlib** — Biblioteca estándar de Kotlin

## Ejemplos de Uso

### Generación de Claves

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Firma de Paquetes

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

### Serialización de Paquetes

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Cifrado con Protocolo Signal

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

## Compatibilidad Entre Lenguajes

Esta implementación mantiene **compatibilidad exacta de formato de cable** con la implementación de referencia en C#:

- Formato de paquete binario: disposición little-endian idéntica
- Enum de tipo de paquete: los valores coinciden exactamente con el enum en C# (1-23)
- Firmas Ed25519: compatibles con NSec/libsodium
- ECDH P-256: curva estándar, compatible entre lenguajes
- HKDF-SHA256: implementación estándar RFC 5869
- AES-256-GCM: estándar NIST con nonce de 12 bytes y tag de 16 bytes

Los paquetes serializados en Kotlin pueden deserializarse en C# y viceversa.

## Pruebas

La implementación incluye una demostración completa (`Demo.kt`) que ejercita:

1. Generación de claves y exportación de clave pública
2. Generación e intercambio de bundle de pre-clave
3. Establecimiento de sesión mediante Protocolo Signal
4. Creación, firma y serialización de paquetes
5. Deserialización de paquetes y verificación de firma
6. Cifrado y descifrado de mensajes
7. Prevención de ataques de repetición
8. Mensajería mediante transporte en proceso

Ejecutar con:
```bash
./gradlew run
```

## Consideraciones de Seguridad

- **Borrado de claves**: Todo el material criptográfico intermedio se borra después de su uso usando `CryptographicOperations.ZeroMemory` (equivalente en Kotlin: `fill(0)`)
- **Protección contra repetición**: La deduplicación de nonces con TTL de 5 minutos previene ataques de repetición
- **Secreto hacia adelante**: Claves por mensaje derivadas del trinquete de cadena
- **Manejo fuera de orden**: Caché de claves omitidas con máx. 1000 claves para prevenir agotamiento de memoria
- **Autenticación RREP**: Los paquetes de respuesta de ruta son firmados por el nodo de destino
- **Confidencialidad del paquete**: El contenido del mensaje está cifrado con AES-256-GCM

## Extensiones Futuras

La implementación proporciona ganchos para:

- **Transporte BLE** (interfaz `TransportService`)
- **Transporte Wi-Fi Direct** (misma interfaz)
- **Enrutamiento epidémico DTN** (modelo `DtnBundle` listo)
- **Difusión SOS** (tipo de paquete definido)
- **Balizas de presencia** (tipo de paquete definido)
- **Voz y streaming** (tipos de paquetes definidos)
- **Double Ratchet** (cuando los transportes siempre activos estén disponibles)

## Documentación del Protocolo

Especificación completa del protocolo: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## Licencia

SPDX-License-Identifier: MIT
