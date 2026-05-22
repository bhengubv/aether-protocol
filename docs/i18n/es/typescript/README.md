# Protocolo de Malla Aether - Implementación en TypeScript

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Una implementación completa en TypeScript/Node.js del protocolo de red en malla Aether, totalmente compatible con el formato de cable de la implementación de referencia en C#.

## Características

- **Serialización de MeshPacket**: Formato binario de cable que coincide exactamente con C# (enteros little-endian, cadenas/arreglos prefijados con longitud)
- **Firma Ed25519**: Usando TweetNaCl para generación y verificación de firmas
- **Signal Protocol**: Intercambio de claves X3DH con derivación de claves HKDF-SHA256 y cifrado AES-256-GCM
- **Firma de paquetes**: Construcción completa de datos firmables según la especificación del protocolo (Sección 2.3)
- **Transporte en proceso**: Red simulada para pruebas y demostraciones
- **Trinquete simétrico**: Avance de clave de cadena HMAC-SHA256 con soporte de mensajes fuera de orden
- **Constantes del protocolo**: Más de 60 constantes de la Sección A de PROTOCOL_SPEC

## Instalación

```bash
npm install
```

## Uso

### Compilar

```bash
npm run build
```

### Ejecutar demostración

```bash
npm run dev
```

La demostración:
1. Crea 2 nodos en una red simulada en proceso
2. Genera pares de claves Ed25519
3. Establece sesiones del protocolo Signal
4. Crea, firma y verifica un paquete
5. Serializa y deserializa paquetes
6. Cifra y descifra mensajes
7. Envía paquetes a través de la capa de transporte

### Ejemplos de API

#### Creación y firma de paquetes

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

#### Cifrado con Signal Protocol

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

#### Serialización de paquetes

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### Transporte en proceso

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

## Conformidad con el protocolo

### Formato de cable

Todos los enteros multibyte usan **little-endian**:
- ID de paquete: UUID de 16 bytes
- TTL, TimestampMs: int32/int64 LE
- Longitudes de cadena: uint16 LE (no uint32)
- Longitud de carga útil: int32 LE

### Firma de paquetes (Sección 2.3)

Formato de datos firmables:
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

### Signal Protocol (Sección 4)

- **Intercambio de claves**: X3DH con ECDH P-256
- **HKDF**: SHA256 con sal="AetherSignal"
- **Cadenas de información**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Cifrado**: AES-256-GCM con nonce de 12 bytes, etiqueta de 16 bytes
- **Trinquete de cadena**: HMAC-SHA256 con avance de contador

## Tipos de paquetes

Los 23 tipos de paquetes definidos:
- RouteRequest (1) - Solicitud de ruta AODV
- RouteReply (2) - Respuesta de ruta AODV
- Data (3) - Datos de aplicación
- Ack (4) - Acuse de recibo de entrega
- SosBroadcast (5) - Difusión de emergencia
- ... y 18 más (véase la especificación del protocolo)

## Características de seguridad

- **Firmas Ed25519**: Todos los paquetes firmados según el protocolo v2
- **AES-256-GCM**: Claves por mensaje con nonces únicos
- **Prevención de repetición**: Nonce aleatorio de 8 bytes + validación de marca de tiempo
- **Secreto hacia adelante**: El trinquete simétrico avanza las claves de cadena
- **Descifrado fuera de orden**: Caché de claves de mensajes omitidos (hasta 1000)

## Estructura del proyecto

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

## Pruebas

La demostración (`npm run dev`) ejercita todas las características principales:
- Creación y serialización de paquetes (ida y vuelta)
- Generación de claves Ed25519 y verificación de firmas
- Establecimiento de sesión con Signal Protocol
- Cifrado y descifrado de mensajes
- Entrega a través del transporte en proceso

Para pruebas unitarias, extienda con Jest o un ejecutor de pruebas similar.

## Notas de compatibilidad

- **Formato de cable C#**: 100% compatible con C# PacketSerializer
- **Paquetes firmados**: Versión de protocolo 2 con firmas Ed25519
- **Derivación HKDF**: Usando @noble/hashes (implementación pura en JavaScript)
- **ECDH**: Módulo crypto integrado de Node.js (curva P-256)

## Dependencias

- **tweetnacl**: Firmas Ed25519 vía TweetNaCl
- **@noble/hashes**: Derivación de claves HKDF-SHA256
- **uuid**: Generación y análisis de UUID
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## Licencia

MIT - Consulte el archivo LICENSE

## Referencias

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [Implementación en C#](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
