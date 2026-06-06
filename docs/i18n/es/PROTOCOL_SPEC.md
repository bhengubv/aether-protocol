# Especificación del Protocolo de Red en Malla Aether

**Versión:** 2.0
**Estado:** Reconciliado con HEAD (2026-05-05)
**Fecha:** 2026-03-15 (borrador inicial); 2026-05-05 (§2, §4, §10, §11 reconciliados, §3/§9 verificados)
**Autores:** The Other Bhengu (Pty) Ltd t/a The Geek y Bhengu B.V.

> **Aviso al lector.** Las versiones anteriores de este documento son
> anteriores a la alineación del formato en cable de 8 lenguajes y a la
> migración familiar hacia X25519 + Signal Double Ratchet. A partir del
> 2026-05-05, §2 (Formato de Paquete), §3 (Enrutamiento), §4 (Intercambio
> de Claves), §9 (DTN) describen el protocolo implementado; §10 (Transmisión
> de Video) y §11 (Ver Juntos) describen el protocolo objetivo — están
> definidos en el cable y probados con fixtures, pero las cadenas de
> codec / BitTorrent / ChipIn aún no están vinculadas al andamiaje. La
> referencia en C# es autoritativa en cualquier punto donde este documento
> y la implementación difieran.
>
> - Bytes canónicos en cable: `fixtures/expected/*.bin` (10 casos con nombre)
> - Serializador de referencia: `src/AetherMesh.Core/Protocol/PacketSerializer.cs`
> - Pila Signal de referencia: `src/AetherMesh.Security/Services/SignalProtocolService.cs`
> - Enrutamiento de referencia: `src/AetherMesh.Core/Routing/RoutingService.cs`
> - DTN de referencia: `src/AetherMesh.Core/Dtn/DtnService.cs`
> - Prueba de interoperabilidad de cable entre lenguajes: `fixtures/README.md`
> - Prueba de interoperabilidad Signal entre lenguajes: `fixtures/signal/README.md`

---

## Tabla de Contenidos

1. [Resumen](#1-resumen)
2. [Formato de Paquete](#2-formato-de-paquete)
3. [Algoritmo de Enrutamiento](#3-algoritmo-de-enrutamiento)
4. [Intercambio de Claves](#4-intercambio-de-claves)
5. [Requisitos de la Capa de Transporte](#5-requisitos-de-la-capa-de-transporte)
6. [Protocolo de Descubrimiento](#6-protocolo-de-descubrimiento)
7. [Modelo de Seguridad](#7-modelo-de-seguridad)
8. [Difusión SOS](#8-difusión-sos)
9. [DTN Store-and-Forward](#9-dtn-store-and-forward)
10. [Transmisión de Video](#10-transmisión-de-video)
11. [Ver Juntos](#11-ver-juntos)

---

## 1. Resumen

Aether es un protocolo de red en malla descentralizado diseñado para entornos con conectividad a internet intermitente o inexistente. Proporciona enrutamiento de paquetes multi-salto sobre transportes de corto alcance heterogéneos (Bluetooth Low Energy, Wi-Fi Direct, NearLink), cifrado de extremo a extremo mediante un acuerdo de claves derivado de X3DH con un trinquete simétrico, entrega store-and-forward tolerante a retardos y un mecanismo de difusión SOS de emergencia. El protocolo es agnóstico al transporte: cualquier capa física que pueda enviar y recibir arreglos de bytes entre pares es un transporte Aether válido. Los nodos se identifican mediante Identificadores de Hardware Universal (UHIDs) y se autentican mediante claves de identidad Ed25519. Aether está pensado como una capa de red universal — cada aplicación del ecosistema registra servicios Aether, y los nodos sin conectividad a internet alcanzan la red más amplia a través de pares puente que conectan el tráfico de la malla con internet.

---

## 2. Formato de Paquete

> Reconciliado el 2026-05-05 con `src/AetherMesh.Core/Protocol/PacketSerializer.cs`
> y los 10 casos de fixture bajo `fixtures/expected/`.

### 2.1. Disposición en Cable de MeshPacket

Cada mensaje Aether se encapsula en un `MeshPacket`. Los campos aparecen en
el cable en **exactamente** este orden:

| Off | Field            | Type                            | Size       | Notas |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = sin firma (heredado), `2` = firmado (actual) |
| 1   | Type             | uint8                           | 1          | Enumeración del tipo de paquete (véase §2.4) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | Identificador de paquete para deduplicación. Orden de bytes **big-endian**, NO el Guid mixto-endian predeterminado de .NET. |
| 18  | Priority         | uint8                           | 1          | Nivel de prioridad (0 = normal, 255 = SOS). **El campo en cable es de 1 byte; los valores >255 deben ser limitados.** |
| 19  | Ttl              | int32, little-endian            | 4          | Tiempo de vida, decrementado en cada salto. **int32 de 4 bytes**, NO uint8 de 1 byte — son válidos valores hasta ~2³¹-1. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Milisegundos de época Unix (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | Longitud de `SourceUhid` en bytes UTF-8. Máximo 65535. |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | UHID del emisor; se permite vacío aunque es inusual. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | Longitud de `DestinationUhid` en bytes UTF-8. |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | UHID del destinatario; cadena vacía para difusión. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | Longitud de `PacketNonce` en bytes. Valor estándar: 8. |
| ... | PacketNonce      | bytes                           | P          | Nonce criptográficamente aleatorio para prevención de replay. |
| ... | Payload Len      | int32, little-endian            | 4          | Longitud de `Payload` en bytes. Los valores negativos son un error. |
| ... | Payload          | bytes                           | Q          | Datos de la aplicación. La interpretación depende de `Type`. |
| ... | Signature Len    | uint16, little-endian           | 2          | Longitud de `Signature` en bytes. 0 (sin firma) o 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | Firma Ed25519 sobre los datos firmables (véase §2.3). |

**Los anchos del prefijo de longitud** varían según el campo — `SourceUhid`, `DestinationUhid`,
`PacketNonce` y `Signature` usan prefijos de longitud de **2 bytes (uint16)**;
`Payload` usa un prefijo de longitud de **4 bytes (int32)** porque los payloads pueden superar
los 64 KiB.

### 2.2. Tamaño Mínimo de Paquete

Con todos los campos de longitud variable vacíos (UHIDs de longitud cero, nonce de longitud cero,
payload de longitud cero, firma de longitud cero), el tamaño en cable es:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

Las cifras de 50 bytes / 52 bytes en borradores anteriores de esta especificación eran incorrectas.

### 2.3. Diagrama del Formato en Cable

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

Para un ejemplo detallado, véase `fixtures/expected/basic_data.bin` (83 bytes,
entrada canónica en `fixtures/inputs.json`). Las implementaciones se validan
contra el corpus completo de fixtures — cualquier divergencia hace fallar el test
de verificación de fixtures entre lenguajes.

### 2.4. Construcción de Datos Firmables

La firma (campo `Signature` en el cable) se calcula sobre una secuencia de bytes
canónica separada — **no** sobre los bytes del cable en sí mismos. Esto permite
que la disposición del cable evolucione sin romper las firmas, y permite que los
nodos intermediarios verifiquen la integridad sin ver el payload en texto plano
(solo se firma su hash SHA-256).

La secuencia de bytes firmables es la concatenación:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> Nótese la divergencia deliberada respecto a la disposición en cable en §2.1: los datos
> firmables usan **int32 de 4 bytes** para `Type`, `Length`, `Ttl` y `Priority`,
> mientras que el cable usa 1 byte / 2 bytes / 4 bytes / 1 byte respectivamente.
> Esto es intencional — la forma firmable es portable entre lenguajes y
> usa campos de ancho fijo; la forma en cable es compacta para la economía de PDU BLE.
> Las implementaciones deben limitar `Priority` a `[0,255]` antes de codificar en
> bytes firmables; de lo contrario, el receptor (que ve el byte de cable 0..255)
> deriva un buffer firmable diferente y la verificación falla.

La implementación de referencia se encuentra en `src/AetherMesh.Security/Services/
PacketSigningService.cs::BuildSignableData` y es de lectura obligatoria para
la portación.

### 2.5. Tipos de Paquete

| Value | Name              | Direction     | Descripción |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | Solicitud de Ruta AODV |
| 2     | RouteReply        | Unicast       | Respuesta de Ruta AODV (DEBE ser firmada por el destino) |
| 3     | Data              | Unicast       | Datos de la aplicación |
| 4     | Ack               | Unicast       | Acuse de recibo de entrega |
| 5     | SosBroadcast      | Flood         | Difusión de emergencia (véase Sección 8) |
| 6     | SosAck            | Unicast       | Acuse de recibo SOS |
| 7     | ChannelMessage    | Multicast     | Mensaje de canal de grupo |
| 8     | ChunkRequest      | Unicast       | Solicitud de fragmento de contenido P2P |
| 9     | ChunkData         | Unicast       | Respuesta de fragmento de contenido P2P |
| 10    | Heartbeat         | Broadcast     | Señal periódica de actividad |
| 11    | StreamAnnounce    | Broadcast     | Anuncio de transmisión en vivo |
| 12    | StreamSegment     | Unicast/Tree  | Segmento de medios de transmisión en vivo |
| 13    | StreamSubscribe   | Unicast       | Solicitud para unirse al árbol de relay de transmisión |
| 14    | StreamUnsubscribe | Unicast       | Abandonar el árbol de relay de transmisión |
| 15    | VoicePtt          | Unicast       | Trama de voz push-to-talk |
| 16    | VoiceCall         | Unicast       | Trama de llamada de voz en tiempo real |
| 17    | VoiceSignaling    | Unicast       | Configuración/cierre de llamada de voz |
| 18    | DtnBundle         | Unicast       | Bundle DTN store-and-forward (véase Sección 9) |
| 19    | DtnCustodyAck     | Unicast       | Acuse de recibo de transferencia de custodia DTN |
| 20    | DtnDeliveryReceipt| Unicast       | Confirmación de entrega de extremo a extremo DTN |
| 21    | PresenceBeacon    | Broadcast     | Anuncio de presencia y disponibilidad |
| 22    | PresenceQuery     | Unicast       | Solicitud de estado de presencia |
| 23    | ProfileSync       | Unicast       | Sincronización de metadatos de perfil |
| 24    | TipPacket         | Unicast       | Propina de nodo (liquidada vía LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | Solicitud del bundle de pre-clave del par |
| 26    | PreKeyResponse    | Unicast       | Entrega del bundle de pre-clave |
| 27    | VideoCall         | Unicast       | Trama de video cifrado (unidad NAL H.264/H.265/VP8) |
| 28    | VideoSignaling    | Unicast       | Configuración de videollamada: oferta, respuesta, rechazo, fin, negociación de codec |
| 29    | WatchSync         | Unicast       | Comando de reproducción sincronizada: play, pausa, seek, velocidad |
| 30    | WatchReaction     | Multicast     | Reacción de emoji o voz con marca temporal durante ver juntos |
| 31    | VideoFrame        | Unicast/SFU   | Trama de video grupal (el relay SFU distribuye a los participantes) |
| 32    | ScreenShare       | Unicast       | Trama de compartir pantalla (misma cadena que video, marcada por separado) |
| 33    | WatchChunkRequest | Unicast       | Solicitud de fragmento de prioridad sesgada a la posición de reproducción |
| 34    | TorrentMetadata   | Multicast     | Intercambio de archivo .torrent de BitTorrent o metadatos de magnet link |

### 2.6. Capacidades del Nodo

Los nodos anuncian sus capacidades como un campo de bits:

| Bit | Value | Capability  | Descripción |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Transporte Bluetooth Low Energy disponible |
| 1   | 2     | WifiDirect  | Transporte Wi-Fi Direct disponible |
| 2   | 4     | Gateway     | Puerta de enlace a internet (conecta la malla con la red IP) |
| 3   | 8     | Relay       | Dispuesto a retransmitir paquetes para otros |
| 4   | 16    | Sos         | Capaz de difusión SOS |
| 5   | 32    | Streaming   | Capaz de relay de transmisión en vivo |
| 6   | 64    | Voice       | Capaz de relay de llamada de voz |
| 7   | 128   | DtnCarrier  | Portador DTN store-and-forward |
| 8   | 256   | NearLink    | Transporte NearLink disponible |
| 9   | 512   | Video       | Capaz de codificación/decodificación de video |

---

## 3. Algoritmo de Enrutamiento

Aether utiliza un protocolo de enrutamiento reactivo basado en el enrutamiento Ad-hoc On-demand Distance Vector (AODV), extendido con autenticación criptográfica de rutas y selección de rutas ponderada por QoS.

### 3.1. Solicitud de Ruta (RREQ)

Cuando un nodo necesita enviar un paquete a un destino para el que no tiene ruta, inicia una Solicitud de Ruta:

1. El originador crea un `MeshPacket` con `Type = RouteRequest`, establece `SourceUhid` como sí mismo, `DestinationUhid` como el destino, y `TTL = 7` (el valor predeterminado).
2. El paquete se difunde a todos los pares directamente conectados.
3. Cada nodo intermedio que recibe un RREQ:
   a. Comprueba si ya ha visto este RREQ por el `Id` del paquete. Si es así, descarta silenciosamente el paquete (deduplicación). La caché de deduplicación contiene hasta `DeduplicationCacheSize` entradas (predeterminado 10.000) y se borra completamente cuando se alcanza el límite.
   b. Instala una **ruta inversa** hacia el originador del RREQ. La ruta inversa registra el UHID del par desde el que se recibió el RREQ como el siguiente salto. El conteo de saltos se deriva de `DefaultTtl - packet.Ttl + 1`.
   c. Si ES el destino, genera un RREP (véase Sección 3.2).
   d. Si tiene una ruta válida existente hacia el destino, PUEDE generar un RREP en nombre del destino.
   e. De lo contrario, decrementa TTL y redifunde el RREQ.
4. El originador espera un RREP con un tiempo de espera de **5.000 ms** (`RouteTimeoutMs`). Si no llega ningún RREP, el descubrimiento de ruta falla.

### 3.2. Respuesta de Ruta (RREP)

Cuando el destino (o un nodo intermedio con una ruta válida) genera una Respuesta de Ruta:

1. Se crea un `MeshPacket` con `Type = RouteReply`, con `SourceUhid` establecido en el nodo destino y `DestinationUhid` establecido en el originador del RREQ.
2. **REQUISITO DE SEGURIDAD:** El RREP DEBE estar firmado por la clave de identidad Ed25519 del nodo destino. La firma cubre los datos firmables estándar (Sección 2.3). Esto previene el envenenamiento de rutas por parte de nodos intermediarios maliciosos.
3. El RREP se envía en unicast de vuelta a lo largo de la ruta inversa instalada durante la propagación del RREQ.
4. Cada nodo intermedio que reenvía el RREP:
   a. Verifica la firma del RREP contra la clave pública de la fuente reclamada (si se conoce). Si la verificación falla, el RREP se descarta y se registra una advertencia.
   b. Instala una **ruta directa** hacia la fuente del RREP (el nodo destino) con el emisor del RREP como siguiente salto.
   c. Decrementa TTL y reenvía hacia el originador del RREQ.
5. Cuando el RREP alcanza el originador, la solicitud de ruta pendiente (rastreada mediante `TaskCompletionSource`) se resuelve con la ruta instalada.

### 3.3. Mantenimiento de Rutas

- **Expiración basada en TTL:** Cada entrada de ruta lleva una marca temporal `ExpiresAt` establecida en `ahora + 300 segundos` (`RouteExpirySeconds`). Las rutas no se actualizan implícitamente; deben restablecerse mediante un nuevo ciclo RREQ/RREP tras la expiración.
- **Poda periódica:** El servicio de protocolo ejecuta un latido periódico (predeterminado cada 300 segundos). Durante cada ciclo, elimina las rutas expiradas tanto del `ConcurrentDictionary` en memoria como del almacén de respaldo SQLite.
- **Poda de dedup de RREQ:** El conjunto de IDs de RREQ vistos se borra cuando supera `DeduplicationCacheSize` (predeterminado 10.000) entradas.

### 3.4. Calidad de Ruta y QoS

Cada `RouteEntry` lleva un `QualityScore` en el rango [0, 100], inicializado en 50 para rutas recién descubiertas. La puntuación considera:

- **Conteo de saltos:** Menos saltos generalmente indica una ruta más rápida.
- **Latencia:** Tiempo de ida y vuelta medido cuando está disponible.
- **Fiabilidad del par:** La puntuación de fiabilidad del par del siguiente salto (véase Sección 3.5).

Los nodos que participan en el sistema de incentivos de propinas reciben un impulso de QoS en su puntuación de calidad de ruta. Esto es una preferencia suave: los no participantes siempre reciben servicio, pero los participantes consistentes pueden experimentar una selección de rutas marginalmente mejor. Los niveles de impulso son:

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. Puntuación de Fiabilidad de Par

A cada par conocido se le asigna una puntuación de fiabilidad en el rango [0, 100], inicializada en 50 (`DefaultReliabilityScore`). La puntuación se ajusta en función del comportamiento observado:

| Evento               | Delta |
|----------------------|-------|
| Relay exitoso        | +2    |
| Relay fallido        | -5    |
| Relay SOS            | +5    |
| Fragmento servido    | +1    |
| Fallo al servir fragmento | -10 |

Las puntuaciones de fiabilidad se persisten en SQLite y se cargan en memoria al inicio. La puntuación influye en la selección de rutas: se prefieren las rutas a través de pares más fiables.

---

## 4. Intercambio de Claves

> Reconciliado el 2026-05-05 con la implementación de referencia en C# en
> `src/AetherMesh.Security/Services/SignalProtocolService.cs` y el corpus de
> fixtures entre lenguajes bajo `fixtures/signal/`. La referencia en C#
> incluye X3DH + Double Ratchet completos (Signal §3 + §5) sobre X25519. Go,
> Python, TypeScript, Rust, Swift y Kotlin han sido portados al mismo
> envelope y son equivalentes en bytes a nivel de X3DH y fixture KDF_RK.
> C incluye solo los primitivos X25519 + KDF_RK + trinquete simétrico —
> suficiente para el verificador de fixtures, sin maquinaria de sesión completa aún.
> Donde esta sección difiera del código, el código es autoritativo;
> abra un issue en `OPEN_ISSUES.md`.

Aether implementa **X3DH** (Extended Triple Diffie-Hellman, Signal §3) para
el establecimiento asincrónico de sesiones, seguido inmediatamente por el
**Signal Double Ratchet** (Signal §5) para secreto adelante continuo y
seguridad post-compromiso. Todo el criptosistema de sesión opera sobre Curve25519:
**X25519** (RFC 7748) para ECDH y **Ed25519** (RFC 8032) para firma.

### 4.1. Claves de Identidad

Cada nodo genera **dos** pares de claves de largo plazo al primer inicio (sin XEdDSA;
el arreglo de doble clave más simple es lo que implementa cada implementación):

- **Par de claves Ed25519** — semilla de 32 bytes (privada), clave pública de 32 bytes.
  Utilizada para la firma de paquetes (§2.4), `SignedPreKeySignature` (§4.3),
  autenticación RREP (§3.2) y firmas de propinas.
- **Par de claves X25519** — claves privada y pública de 32 bytes sin procesar. Utilizadas para
  las cuatro operaciones DH de X3DH (§4.4).

Referencia: `SignalProtocolService.InitializeIdentityKeys`. Las claves privadas
viven solo en el dispositivo; las claves públicas se publican en `PreKeyBundle`.

Se respeta una ventana de migración P-256 → Ed25519 de 30 días para la
*verificación de firma* en paquetes entrantes únicamente — véase §7.5. Los bundles
de pre-clave en sí son exclusivamente X25519 en el cable.

### 4.2. Elección de Curva

X3DH y el Double Ratchet usan **X25519** exclusivamente. P-256 *no* se
usa en el establecimiento de sesión en ninguna implementación actual. Un borrador
anterior de esta especificación describía P-256 ECDH; ese texto es anterior
a la migración familiar del 2026-05-05 a X25519 y ya no es preciso.

### 4.3. Bundle de Pre-clave

Se publica un bundle de pre-clave para que un iniciador pueda establecer una
sesión sin que el respondedor esté en línea (Signal §3.4):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

Referencia: `AetherMesh.Security.Models.PreKeyBundle`. El contrato de forma en cable es
el mismo en los 8 lenguajes.

**Pool de pre-clave de un solo uso (OPK).** Cada respondedor mantiene un pool de
`OpkPoolSize` (predeterminado 100, siguiendo la guía publicada de Signal) OPKs X25519.
La generación del bundle extrae el siguiente ID no utilizado de una cola FIFO, luego
recarga el pool hasta su tamaño objetivo. Cada OPK se consume exactamente
una vez: el respondedor elimina y pone a cero la mitad privada en el primer
mensaje PreKey que haga referencia a su ID. Los iniciadores concurrentes que compiten
por el mismo ID de OPK verán exactamente un `EstablishResponderSession` exitoso
bajo `_preKeyLock`; el perdedor lanza `CryptographicException`.

Referencia: `SignalProtocolService.TopUpOpkPoolNoLock` (líneas 494–518),
`SignalProtocolService.EstablishResponderSession` (líneas 636–718). La semántica
del pool es ejercida por `tests/AetherMesh.Core.Tests/PreKeyPoolTests.cs`.

**Rotación de pre-clave firmada (SPK).** El SPK se genera de forma perezosa en la primera
llamada al bundle y se reutiliza en llamadas posteriores para que los iniciadores concurrentes
que obtengan bundles antes de que se ejecute X3DH no invaliden los bundles de los demás.
La rotación periódica del SPK (Signal §3.3 recomienda semanalmente) es una operación
explícita, no un efecto secundario de la generación del bundle.

Los IDs de pre-clave se extraen de `RandomNumberGenerator.GetInt32(1, int.MaxValue)`
con reintento explícito de colisión (hasta 64 intentos antes de lanzar).

### 4.4. Establecimiento de Sesión (X3DH)

El X3DH completo (Signal §3.3) se ejecuta en el lado del iniciador. Se calculan cuatro
operaciones DH sobre X25519:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

donde `IK_A` / `IK_B` son las claves de identidad X25519, `EK_A` es una
clave efímera X25519 nueva generada solo para esta sesión, `SPK_B` es la
pre-clave firmada del respondedor, y `OPK_B` es la pre-clave de un solo uso del respondedor.
La clave raíz inicial es:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

La constante `info` `aether-x3dh-root-v1` es idéntica en todas las
implementaciones y está fijada por `fixtures/signal/expected/x3dh_basic.json`
(campo `root_key_hex`).

Referencia: `SignalProtocolService.ProcessPreKeyBundleAsync` (líneas
554–626). Ruta de verificación:
caso `x3dh_basic` de `fixtures/signal/inputs.json` →
`fixtures/signal/expected/x3dh_basic.json`.

**Verificación del bundle.** Antes de que se ejecuten los DH, el iniciador verifica
`SignedPreKeySignature` contra `IdentityKey` usando Ed25519. Una verificación
fallida lanza `CryptographicException` y el bundle se descarta.
Los tamaños de las claves públicas se validan contra `X25519Service.PublicKeySize` (32);
los bundles malformados son rechazados.

**Cebado de sesión.** Al final de `ProcessPreKeyBundleAsync` se crea un
`SignalSession` con:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — integración canónica de Signal X3DH ↔
  Double Ratchet: la clave efímera X3DH del iniciador se convierte en su
  primer par de claves de trinquete DH (`DHs`).
- `RemoteEphemeralPub = SPK_B` — la pre-clave firmada del respondedor se
  trata como la clave de trinquete par inicial (`DHr`).
- `SendChainKey = null`, `RecvChainKey = null` — ambas claves de cadena se
  derivan de forma perezosa en el primer envío / primer recibo de trinquete DH.
- `PendingPreKeyMessage = true` — indica que la próxima llamada saliente a
  `EncryptAsync` DEBE emitir un mensaje PreKey (`MessageType=1`).

Todos los outputs DH y el secreto compartido concatenado se ponen a cero en el
bloque `finally` mediante `CryptographicOperations.ZeroMemory`.

**Negativa a enviar de forma insegura.** Si se llama a `EncryptAsync` para un par
sin sesión, la llamada lanza `InvalidOperationException`. No hay ruta de respaldo
derivada de UHID. Se espera que los hosts pongan el mensaje en cola
(véase `MessagingService` + `SignalMessageEnvelopeCipher`) y reintenten una vez
que se complete el establecimiento de sesión.

### 4.5. Double Ratchet (Signal §5)

Cada lado mantiene un par de claves de trinquete X25519 rotativo (`DHs`) y una copia
de la última clave pública de trinquete del par vista (`DHr`). En cada mensaje el
emisor publica su `DHs` público actual; cada vez que el receptor
observa un nuevo `DHr`, ejecuta un **paso de trinquete DH** que vuelve a derivar las claves
de la cadena mediante `KDF_RK(RK, DH(myDHs, newDHr))` — re-derivando tanto la clave raíz
como una nueva clave de cadena.

#### 4.5.1. KDF_RK

`KDF_RK` es HKDF-SHA256 sobre un bloque de 64 bytes, dividido 32+32 en la nueva
clave raíz y la nueva clave de cadena:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

Referencia: `SignalProtocolService.KdfRk` (líneas 857–868). Fijado por
caso `kdf_rk_basic` de `fixtures/signal/inputs.json` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Trinquete Simétrico

Por Signal §5.1, las claves de mensaje y las claves de cadena se derivan de una clave
de cadena usando HMAC-SHA256 con separación de dominio de un solo byte:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Referencia: `SignalProtocolService.RatchetChainKey` (líneas 876–881).
Fijado por los casos `ratchet_step_basic` y `ratchet_step_three_iterations`
de `fixtures/signal/inputs.json`.

El borrador anterior de esta especificación describía `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` y un avance de `chain_key` separado
mediante `HMAC(chain_key, 0x01)`. Eso no era Signal y nunca se implementó;
ha sido reemplazado por la división canónica 0x01/0x02.

#### 4.5.3. Paso de Trinquete DH en Recepción

Se activa cuando el `SenderEphemeralKeyX25519` del mensaje entrante difiere
del `RemoteEphemeralPub` en caché (comparación en tiempo constante).

1. Guardar el contador saliente como `PreviousChainCount` (Signal §5: PN) para que el
   par pueda calcular las claves omitidas a través del límite.
2. Restablecer `SendCounter` y `RecvCounter` a 0; instalar el nuevo
   `RemoteEphemeralPub`.
3. Derivar nueva cadena de recepción: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Poner a cero el antiguo privado de `myDHs`; generar un nuevo par de claves X25519.
5. Derivar nueva cadena de envío: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Referencia: `SignalProtocolService.DhRatchetReceive` (líneas 726–772).

#### 4.5.4. Derivación Perezosa de Cadena de Envío

El primer envío del iniciador ejecuta un **medio paso** en lugar de un trinquete DH
completo — X3DH ya colocó `DHs` y `DHr`, por lo que solo necesita derivarse la
cadena de envío:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` *no* se rota aquí. Solo se rota en un paso de trinquete DH genuino en el lado de recepción.

Referencia: `SignalProtocolService.DhRatchetSendOnly` (líneas 780–796).

#### 4.5.5. Claves de Mensaje Omitidas

Cuando los mensajes llegan desordenados, la clave de mensaje de cada contador omitido se
almacena en caché en `SkippedMessageKeys`, indexada por `(Hex(remoteEphPub):counter)`.
El enlace con la clave pública remota es esencial — los mensajes desordenados de una cadena
anterior (diferente `DHr`) aún pueden llegar después de un paso de trinquete DH y
necesitan su propio conjunto de claves por cadena.

Límites:

- Omitir más de `MaxSkippedKeys` (1000) entradas en un solo salto
  lanza `CryptographicException` y fuerza el restablecimiento de la sesión.
- Al cruzar un límite de trinquete DH, el receptor primero omite hasta
  `PreviousChainCount` claves en la cadena *antigua*, luego ejecuta el
  paso de trinquete DH antes de derivar claves en la nueva cadena.

Referencia: `SignalProtocolService.SkipMessageKeys` (líneas 804–830) y
el bucle de omisión en el descifrado (líneas 366–388).

### 4.6. Formato de Payload Cifrado

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

Referencia: `AetherMesh.Security.Models.EncryptedPayload` (líneas 55–66 de
`SecurityModels.cs`). El campo `InitiatorEphemeralKeyX25519` es un alias
de compatibilidad con versiones anteriores del envelope en cable pre-Double-Ratchet e
iguala a `SenderEphemeralKeyX25519` en mensajes PreKey; los nuevos consumidores
deben ignorarlo.

Parámetros AES-GCM: clave de 256 bits, nonce de 96 bits (`AesNonceSize = 12`),
etiqueta de 128 bits (`AesTagSize = 16`), etiqueta concatenada al texto cifrado.
Las claves de mensaje se ponen a cero en bloques `finally` inmediatamente después
del cifrado/descifrado AES-GCM.

### 4.7. Estado por Lenguaje

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | primitives only — `aethermesh_x25519_*`, `aethermesh_signal_kdf_rk` | not implemented | — | kdf_rk_basic only |

Los 7 lenguajes con capacidad de sesión (C# + Go + TypeScript + Python + Kotlin + Swift + Rust) incluyen el pool OPK FIFO de 100 claves con recarga perezosa y consumo protegido por cerrojo, coincidiendo con el contrato de referencia de C#. C incluye solo primitivos; la maquinaria de sesión completa se rastrea en el elemento 11 de `OPEN_ISSUES.md`.

---

## 5. Requisitos de la Capa de Transporte

Aether es agnóstico al transporte. Cualquier canal de comunicación físico que satisfaga el contrato de `ITransportService` puede participar en la malla.

### 5.1. Contrato de la Interfaz ITransportService

Cada implementación de transporte DEBE exponer lo siguiente:

**Propiedades:**

| Property           | Type   | Descripción |
|--------------------|--------|-------------|
| `Name`             | string | Identificador legible por humanos (p. ej., "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Si el transporte es actualmente utilizable en este dispositivo |
| `MaxBandwidthBps`  | int64  | Rendimiento máximo en bytes por segundo |
| `MaxRangeMeters`   | int32  | Alcance máximo de comunicación en metros |
| `PowerCostRelative`| int32  | Consumo de energía relativo (1 = bajo, 10 = alto) |
| `MaxConcurrentPeers` | int32 | Máximo de conexiones de pares simultáneas |

**Métodos:**

| Method         | Signature | Descripción |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Enviar un arreglo de bytes a un par específico. Devuelve true en caso de éxito. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Enviar un stream a un par (para transferencias grandes, voz, video). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Comprobar si hay una conexión activa con un par. |

**Eventos:**

| Event          | Signature | Descripción |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Se activa cuando llegan datos de un par. |

### 5.2. Algoritmo de Selección de Transporte

El `TransportManager` selecciona el transporte óptimo para cada paquete basándose en:

1. **Disponibilidad:** Solo se consideran los transportes donde `IsAvailable == true`.
2. **Tamaño del payload:** Si el tamaño del payload es igual o inferior a `BleMaxPayloadBytes` (1.024 bytes), se prefiere BLE por su eficiencia energética. Los payloads más grandes prefieren Wi-Fi Direct.
3. **Ponderación del costo de energía:** Entre los transportes disponibles, se prefieren los valores más bajos de `PowerCostRelative` para el tráfico habitual. Los paquetes de alta prioridad (SOS, voz) pueden anular esta preferencia.
4. **Conectividad del par:** Si un transporte ya tiene una conexión activa con el par objetivo (`IsConnected` devuelve true), se prefiere para evitar la sobrecarga de configuración de conexión.
5. **Respaldo:** Si ningún transporte local puede alcanzar el objetivo, el paquete se pone en cola para relay vía AetherMeshAPI.

### 5.3. Transportes de Referencia

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | Notas |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | Descubrimiento principal + paquetes pequeños |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | Transferencias grandes, streaming, voz |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon, alto rendimiento |

**Límite de payload BLE:** Los paquetes que superan 1.024 bytes (`BleMaxPayloadBytes`) se enrutan automáticamente a Wi-Fi Direct o NearLink. BLE se usa para anuncios de descubrimiento, paquetes de control pequeños (RREQ/RREP, beacons de presencia) y mensajería de bajo ancho de banda.

**Wi-Fi Direct**: el tiempo de espera de conexión es de 10.000 ms (`WifiDirectTimeoutMs`) con un máximo de 8 pares concurrentes (`MaxWifiDirectPeers`).

---

## 6. Protocolo de Descubrimiento

### 6.1. Publicidad BLE

Los nodos Aether se descubren principalmente a través de la publicidad BLE. Para evitar el rastreo persistente mediante identificadores estáticos, el protocolo emplea dos mecanismos de privacidad: Service UUIDs rotativos y Claves de Resolución de Identidad.

**Ciclo de publicidad:** 2 segundos de escaneo activo, 8 segundos inactivo (`BleScanOnMs`/`BleScanOffMs`). El intervalo de publicidad es de 1.000 ms (`BleAdvertiseIntervalMs`). Se añade un jitter aleatorio de 0-2.000 ms (`BleScanJitterMaxMs`) al intervalo de escaneo para evitar la detección de patrones de temporización.

**Tiempo de espera de par:** Un par no redescubierto en 30 segundos se considera perdido (evento `PeerLost`).

### 6.2. Service UUID Rotativo

Para evitar la toma de huellas digitales BLE a largo plazo, el Service UUID usado en los anuncios rota cada 15 minutos (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

El `rotation_key` es una clave de 32 bytes generada una vez por nodo y almacenada en almacenamiento seguro. Todos los nodos Aether que comparten la misma clave de rotación derivarán el mismo UUID para una ventana de tiempo dada, lo que permite el descubrimiento mutuo sin revelar un identificador permanente.

Se mantiene un UUID estático de respaldo (`A3E7-1001-0001-0000-000000000000`) durante 90 días durante la transición desde el esquema sin rotación.

### 6.3. Clave de Resolución de Identidad (IRK)

Cada nodo genera una Clave de Resolución de Identidad (IRK) de 128 bits almacenada en almacenamiento seguro. El IRK se comparte con pares de confianza durante el intercambio de claves.

**Generación de Dirección Privada Resoluble (RPA):**

1. Calcular `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 bytes).
2. Establecer los dos bits más significativos de `prand[0]` en `01` (indicador RPA según la especificación BLE).
3. Calcular `hash = AES-128-ECB(IRK, pad(prand))` donde `prand` ocupa los bytes 13-15 de una entrada de 16 bytes con relleno de ceros.
4. Construir RPA: `hash[0..2] || prand[0..2]` (6 bytes en total).

**Resolución de RPA:** Un nodo que posee el IRK de un par puede verificar si un RPA observado pertenece a ese par recalculando el hash desde el componente `prand` del RPA. El tiempo de resolución es aproximadamente O(N) donde N es el número de IRKs conocidos, con un rendimiento de referencia de ~0,1 ms para 100 pares.

El RPA rota en el mismo ciclo de 15 minutos que el Service UUID.

### 6.4. Proximidad Basada en Geohash

Los nodos codifican opcionalmente su ubicación como un geohash. Por privacidad, el geohash se trunca a 4 caracteres, proporcionando una resolución de aproximadamente 39 km x 20 km. Esta granularidad es suficiente para:

- Descubrimiento de canales basado en proximidad
- Enrutamiento epidémico DTN (replicar hacia el área del último geohash conocido del destinatario)
- Contexto geográfico de alertas SOS

El geohash de precisión completa nunca se transmite por la malla. Solo se comparte la forma truncada, y solo cuando el nivel de privacidad del nodo lo permite (`PrivacyLevel.Full` o `PrivacyLevel.Partial`).

---

## 7. Modelo de Seguridad

### 7.1. Modelo de Amenaza

Aether asume las siguientes capacidades del adversario:

- **Escucha pasiva:** El adversario puede observar todos los anuncios BLE y el tráfico de la malla dentro del alcance de radio.
- **Inyección activa:** El adversario puede inyectar, modificar o reproducir paquetes.
- **Ataque Sybil:** El adversario puede crear múltiples identidades de nodo falsas.
- **Denegación de servicio selectiva:** El adversario puede descartar selectivamente paquetes como nodo relay.

### 7.2. Qué Está Protegido

| Propiedad | Nivel de Protección | Mecanismo |
|----------|-----------------|-----------|
| Contenido del mensaje | Confidencialidad completa | AES-256-GCM con claves por mensaje (Sección 4.5) |
| Identidad del emisor | Parcial | UHID visible en cabeceras de paquete; la dirección BLE rota (Sección 6) |
| Identidad del receptor | Parcial | UHID de destino visible en paquetes enrutados; los paquetes de difusión tienen destino vacío |
| Metadatos de enrutamiento | Mínima | Los nodos intermedios ven los UHIDs de origen/destino y TTL |
| Ordenamiento de mensajes | Protegido | Los contadores en el trinquete simétrico previenen el reordenamiento |
| Integridad del mensaje | Completa | Firma Ed25519 en cada paquete (v2) |

### 7.3. Resistencia a Ataques

**Ataques de replay:**
Cada paquete lleva un nonce aleatorio criptográfico de 8 bytes y una marca temporal de precisión en milisegundos. Los nodos relay mantienen una caché de deduplicación de pares `(SenderUhid, NonceValue)` con un TTL de 5 minutos (`MaxPacketAgeSeconds = 300`). Un paquete con un nonce duplicado del mismo emisor se descarta. Los paquetes con marcas temporales de más de 5 minutos de antigüedad se rechazan independientemente del nonce.

La caché de dedup de nonce se limpia cada 60 segundos. Se eliminan las entradas expiradas (de más de 5 minutos).

**Ataque man-in-the-middle (MITM):**
- Los paquetes Route Reply DEBEN llevar una firma Ed25519 válida del nodo destino reclamado. Los nodos intermedios no pueden falsificar RREPs porque no poseen la clave privada del destino.
- Los bundles de pre-clave incluyen una `SignedPreKeySignature` (Ed25519) sobre el `SignedPreKey`, vinculando la clave ECDH efímera a la identidad de largo plazo.
- El establecimiento de sesión (Sección 4.4) vincula criptográficamente la sesión a las identidades de ambas partes a través del paso de verificación de pre-clave.

**Ataques Sybil:**
- La puntuación de fiabilidad de cada nodo comienza en 50 y se ajusta en función del comportamiento observado (Sección 3.5). Los nodos Sybil recién creados no tienen reputación acumulada.
- Los nodos con puntuaciones de fiabilidad bajas (aproximándose a 0) son depriorizados en la selección de rutas.
- El algoritmo de enrutamiento epidémico DTN usa la proximidad por geohash y el historial de éxito de relay para seleccionar objetivos de replicación, dificultando que los nodos Sybil atraigan tráfico sin contribuciones genuinas de relay.

**Ataques de inundación:**
- El TTL se decrementa en cada salto y los paquetes con TTL = 0 se descartan. El TTL predeterminado de 7 limita el radio de explosión de cualquier difusión.
- La deduplicación de RREQ por ID de paquete previene la amplificación a través de tormentas de difusión. La caché de dedup se vacía cuando supera `DeduplicationCacheSize` (predeterminado 10.000) entradas.
- Las difusiones SOS tienen un límite de 3 por hora por nodo (Sección 8).

### 7.4. Puesta a Cero de Claves

Todo el material criptográfico intermedio se pone a cero inmediatamente después de su uso:

- `sharedSecret` del acuerdo de claves ECDH: puesto a cero después de la derivación HKDF.
- `messageKey` del trinquete de cadena: puesto a cero después del cifrado/descifrado AES-GCM.
- `skippedKey` del descifrado fuera de orden: puesto a cero después de su uso y eliminado del mapa.
- `RootKey`, `SendChainKey`, `RecvChainKey` derivados: puestos a cero del contexto de establecimiento (la sesión retiene sus propias copias).

La puesta a cero usa `CryptographicOperations.ZeroMemory`, que garantiza que el compilador no la optimice.

### 7.5. Migración de P-256 a Ed25519

El protocolo admite una ventana de transición de 30 días desde claves de identidad ECDSA P-256 (Versión de Protocolo 1) a Ed25519 (Versión de Protocolo 2):

1. Los paquetes de Versión de Protocolo 1 (sin firma) se aceptan durante el período de transición.
2. La verificación de firma primero intenta Ed25519. Si la clave pública tiene más de 32 bytes (lo que indica una clave P-256 codificada en DER), recurre a la verificación P-256 ECDSA.
3. Después de la ventana de 30 días, los paquetes de Versión de Protocolo 1 son rechazados.
4. Los nodos que no hayan migrado deben reinicializarse con una nueva identidad Ed25519.

### 7.6. Conciencia de Jurisdicción

El protocolo define niveles de jurisdicción para manejar los distintos requisitos legales en torno al cifrado y la red en malla:

| Tier | Comportamiento | Jurisdicciones de Ejemplo |
|------|----------|-----------------------|
| 1    | Operación libre | South Africa, Kenya, Ghana |
| 2    | Operación modificada | Nigeria, India, EU, US, UK |
| 3    | Solo malla (alto riesgo) | China, Russia, Iran, UAE, Myanmar |
| 4    | Desconocido (solo malla por defecto) | Todos los demás |

La selección de nivel afecta la disponibilidad de funciones (p. ej., las funciones de propina/financieras pueden estar desactivadas en el Tier 3) pero no debilita el cifrado. El cifrado de extremo a extremo siempre se aplica independientemente de la jurisdicción.

---

## 8. Difusión SOS

El mecanismo SOS es una inundación de emergencia de doble ruta diseñada para situaciones en que un usuario está en peligro y necesita alcanzar pares de la malla cercanos y/o internet simultáneamente.

### 8.1. Parámetros de Difusión

| Parámetro | Valor | Descripción |
|-----------|-------|-------------|
| TTL       | 15    | El doble del valor predeterminado normal (7), garantizando una propagación más amplia |
| Priority  | 999   | Prioridad máxima; desplaza todo el demás tráfico en las colas de relay |
| Límite de tasa | 3/hora | Límite por nodo para prevenir abusos |
| Destino | vacío | Difusión a todos los pares (sin destino específico) |

### 8.2. Algoritmo de Inundación

1. El originador construye un paquete SOS con `Type = SosBroadcast`, `TTL = 15`, `Priority = 999` y un `DestinationUhid` vacío.
2. El payload está codificado en JSON y contiene:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **Despacho de doble ruta:** El SOS se envía simultáneamente mediante:
   - **Inundación de malla:** Difusión a todos los pares conectados a través de todos los transportes disponibles.
   - **Llamada API:** Enviado a AetherMeshAPI para distribución del lado del servidor y puente con PanikAPI (despacho por SMS/correo electrónico).
4. Ambas rutas son fire-and-forget entre sí. Si la llamada API falla, la inundación de malla procede de forma independiente.

### 8.3. Comportamiento de Relay

Cuando un nodo recibe un paquete SOS:

1. Comprobar deduplicación por `Id` del paquete. Si ya se ha visto, descartar silenciosamente.
2. Deserializar el payload y lanzar el evento `SosReceived` para la UI local.
3. Añadir la alerta a la lista de alertas activas.
4. Si `TTL > 1`, decrementar TTL y **redifundir a TODOS los pares** independientemente del estado de la tabla de enrutamiento. Los paquetes SOS omiten el enrutamiento normal — inundan incondicionalmente.

### 8.4. Limitación de Tasa

Cada nodo mantiene una ventana deslizante de marcas temporales de difusión recientes. Antes de iniciar un nuevo SOS:

1. Eliminar las entradas de más de 1 hora de antigüedad de la cola.
2. Si la cola contiene 3 o más entradas (`MaxSosBroadcastsPerHour`), la difusión es rechazada.
3. Si el despacho tiene éxito, se encola la marca temporal actual.

La limitación de tasa solo se aplica a la originación de difusiones SOS, no al relay.

### 8.5. Puente SOS-PanikAPI

Las difusiones SOS recibidas a través de la malla se pueden reenviar a PanikAPI para la respuesta de emergencia tradicional (SMS a contactos, alertas por correo electrónico). A la inversa, las sesiones de emergencia de PanikAPI pueden difundirse a la malla para concienciación comunitaria. La prevención de bucles se logra marcando el origen (`direct` vs `mesh_forward`) y un indicador `internet_forwarded` en las difusiones de malla.

---

## 9. DTN Store-and-Forward

El subsistema de Red Tolerante a Retardos (DTN) habilita la entrega de mensajes cuando no existe ningún camino de extremo a extremo entre el emisor y el destinatario. Los bundles se almacenan en nodos intermedios y se reenvían de forma oportunista a medida que la conectividad cambia.

### 9.1. Formato de Bundle

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. Ciclo de Vida del Bundle

1. **Creación:** El emisor crea un bundle con un payload cifrado (cifrado mediante la sesión Signal con el destinatario). `Status = Pending`, `CopyCount = 1`.
2. **Intento de entrega inmediata:** El emisor primero intenta el enrutamiento directo en malla (RREQ/RREP). Si existe una ruta, el bundle se entrega inmediatamente y `Status` transiciona a `Delivered`.
3. **Intento de relay por servidor:** Si el enrutamiento en malla falla, el emisor intenta hacer relay a través de AetherMeshAPI. Si el servidor puede alcanzar al destinatario (o poner el mensaje en cola), la entrega tiene éxito.
4. **Store-and-forward:** Si tanto el enrutamiento en malla como el relay por servidor fallan, el bundle permanece en almacenamiento local (estado `Pending`) esperando el siguiente escaneo de entrega.

### 9.3. Escaneo de Entrega

Un escaneo periódico se ejecuta cada 60 segundos (`DtnScanIntervalSeconds`):

1. Cargar todos los bundles pendientes desde SQLite (fuente de verdad).
2. Para cada bundle pendiente:
   a. Intentar ruta en malla hacia el destinatario.
   b. Intentar relay por servidor.
   c. Si ambos fallan y `CopyCount < MaxCopies`, intentar replicación epidémica (Sección 9.4).
3. Eliminar los bundles expirados (`ExpiresAt <= ahora`).

### 9.4. Enrutamiento Epidémico

Cuando la entrega directa y el relay por servidor fallan, los bundles se replican a pares cercanos usando enrutamiento epidémico:

1. El `EpidemicRoutingService` selecciona objetivos de replicación de la lista de pares actual.
2. La selección de objetivo considera:
   - **Proximidad por geohash:** Se prefieren los pares cuyo geohash está más cercano al último geohash conocido del destinatario.
   - **Historial de relay:** Se prefieren los pares con puntuaciones de fiabilidad más altas.
   - **Presupuesto de copias:** La replicación se detiene cuando `CopyCount >= MaxCopies` (predeterminado: 3).
3. Cada replicación envía un paquete `DtnBundle` al par seleccionado.
4. Al recibirlo, el servicio DTN del par invoca `AcceptCustodyAsync`.

### 9.5. Transferencia de Custodia

Cuando un nodo recibe un bundle DTN destinado a otro nodo:

1. **Comprobación de capacidad:** El nodo comprueba su conteo de bundles actual contra `DtnMaxBundlesPerNode` (50). Si está a plena capacidad, la custodia es rechazada.
2. **Aceptación:** El estado del bundle se establece en `InCustody`, el conteo de saltos se incrementa y el bundle se persiste en SQLite.
3. **Registro de custodia:** Se crea un `CustodyRecord` que documenta la transferencia (de, a, marca temporal).
4. **Incremento del conteo de copias:** El `CopyCount` del bundle se incrementa en el almacenamiento persistente.
5. **Acuse de recibo:** Se envía un paquete `DtnCustodyAck` de vuelta al nodo transferidor con `Accepted = true`.
6. El nodo aceptante se vuelve responsable de intentar la entrega en escaneos posteriores.

### 9.6. Recibo de Entrega

Cuando el destinatario previsto recibe un bundle DTN:

1. El estado del bundle se actualiza a `Delivered`.
2. Se envía un `DtnDeliveryReceipt` de vuelta al emisor original mediante enrutamiento en malla (con respaldo de relay por servidor):
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. Al recibir el recibo, el emisor elimina el bundle de su almacén y lanza el evento `BundleDelivered`.
4. El recibo también se sincroniza con AetherMeshAPI para análisis.

### 9.7. Expiración de Bundle

- El TTL predeterminado del bundle es de 72 horas (`DtnBundleTtlHours`).
- Los bundles expirados se limpian durante el escaneo periódico de entrega.
- Los bundles en estado `Expired` o `Delivered` se eliminan tanto de la caché en memoria como de SQLite.

### 9.8. Límites de Capacidad

| Parámetro               | Predeterminado | Descripción |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | Vida útil máxima del bundle |
| `DtnMaxCopies`          | 3       | Máximo de copias por bundle en la red |
| `DtnMaxBundlesPerNode`  | 50      | Máximo de bundles que un solo nodo transportará |
| `DtnScanIntervalSeconds`| 60      | Frecuencia del escaneo de entrega |

---

## 10. Transmisión de Video

> **Estado a partir del 2026-05-05 — diseño + andamiaje C#, sin cadena de codec en producción.** Los tipos de paquete `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) están
> definidos en el cable y van y vienen a través del corpus de fixtures entre lenguajes.
> El módulo C# `AetherMesh.Streaming` incluye interfaces, modelos y servicios skeleton
> (`StreamingService`, `VideoCallService`, `WatchTogetherService`)
> que conectan los costuras de enrutamiento/DI y el fan-out de segmentos en unicast — pero sin
> codificación/decodificación de video real vinculada a ellos. Los otros 7 lenguajes tienen
> solo tipos en cable. El documento de diseño prospectivo en
> `docs/adaptive-secure-streaming-spec.md` es la arquitectura objetivo.
> Trate la prosa a continuación como la especificación de lo que esos servicios IMPLEMENTARÁN;
> consulte `OPEN_ISSUES.md` para las brechas de preparación para producción.


Aether admite tres modos de video: videollamadas peer-to-peer, video grupal (participantes ilimitados con topología dinámica) y transmisión en vivo. Todas las tramas de video están cifradas con Signal Protocol y firmadas con Ed25519.

### 10.1. Matriz de Capacidades de Transporte

Antes de iniciar una videollamada, el originador consulta la capa de transporte para determinar la mejor conexión disponible al par. El transporte determina qué calidad de video es posible:

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | No (audio-only) | — | — | 64 Kbps | Sync packets only |
| NearLink | Light | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Full | 1080p | H.264 | 3000 Kbps | All modes |
| Internet | Full | 720p | H.264 | 1500 Kbps | All modes |
| CircleLink | No (audio-only) | — | — | 64 Kbps | Sync packets only |

Si el único transporte disponible es BLE o CircleLink, el servicio de videollamada se degrada automáticamente a una llamada de voz.

### 10.2. Codecs de Video

| Enum Value | Codec | Caso de Uso |
|------------|-------|----------|
| 0 | H.264 | Predeterminado. Ampliamente soportado, buena compresión. |
| 1 | H.265 | Mejor compresión. Usado en NearLink (ancho de banda limitado). |
| 2 | VP8 | Alternativa libre de royalties. |

### 10.3. Resoluciones de Video

| Enum Value | Resolution | Bitrate Típico |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. Flujo de Videollamada P2P

1. **Comprobación de capacidad**: El originador consulta `GetVideoCapabilityAsync(peerUhid)` para determinar el mejor transporte, resolución máxima y codec recomendado.
2. **Oferta**: El originador envía un paquete `VideoSignaling` (tipo 28) con `SignalType = Offer`, incluyendo el codec preferido, la resolución máxima y el bitrate máximo.
3. **Respuesta/Rechazo**: El llamado responde con `SignalType = Answer` (negociando el codec al mínimo común denominador) o `SignalType = Reject`.
4. **Llamada activa**: Ambos nodos intercambian paquetes `VideoCall` (tipo 27) que contienen unidades NAL H.264/H.265/VP8. Cada trama incluye un número de secuencia para el ordenamiento del buffer de jitter y un indicador de keyframe.
5. **Compartir pantalla**: Cualquiera de las partes puede alternar la compartición de pantalla. `VideoSignaling` con `SignalType = ScreenShareStart/Stop` notifica al par. Las tramas de compartir pantalla usan `PacketType.ScreenShare` (tipo 32) pero la misma cadena de procesamiento.
6. **Fin de llamada**: Cualquiera de las partes envía `VideoSignaling` con `SignalType = Bye`.

Todos los payloads de señalización y trama están cifrados con Signal Protocol (sesión X3DH). El payload cifrado se serializa como `EncryptedPayload` codificado en JSON dentro del campo `MeshPacket.Payload`.

### 10.5. Máquina de Estados de Videollamada

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

Estados: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. Video Grupal

Las sesiones de video grupal admiten participantes ilimitados. La topología se selecciona dinámicamente según el conteo de participantes:

- **FullMesh** (2-3 participantes): Cada participante envía un stream a cada otro participante. Simple, baja latencia.
- **SFU** (4+ participantes, umbral: `SfuThresholdParticipants = 4`): Un nodo es elegido como relay SFU. Cada participante envía un stream al relay, que lo distribuye a todos los demás. El nodo relay gana propinas a través de la capa de incentivos.

Los cambios de topología son automáticos: cuando el 4.° participante se une, la sesión transiciona de FullMesh a SFU. Cuando los participantes se van y el conteo baja de 4, vuelve a transicionar.

Las tramas de video grupal usan `PacketType.VideoFrame` (tipo 31). En modo SFU, las tramas se envían al UHID del nodo relay, que las redifunde.

### 10.7. Buffer de Jitter

El buffer de jitter de video opera independientemente del buffer de jitter de voz (que maneja tramas Opus de 20 ms):

- **Rango**: 60 ms mínimo, 500 ms máximo.
- **Profundidad adaptativa**: Rastrea el jitter entre tramas mediante Media Móvil Exponencial (EMA). La profundidad del buffer = 2× la estimación de jitter, limitada a [60, 500] ms.
- **Descarte consciente de keyframe**: Cuando el buffer se desborda, primero se descartan las tramas que no son keyframe (P/B). Las tramas I (keyframes) nunca se descartan — son necesarias para la recuperación del decodificador.
- **Manejo de brechas**: Cuando se detecta una brecha de secuencia, el buffer salta al siguiente keyframe disponible en lugar de esperar indefinidamente.

### 10.8. Tipos de Señalización de Video

| Enum Value | Type | Descripción |
|------------|------|-------------|
| 0 | Offer | Inicio de videollamada con preferencia de codec/resolución |
| 1 | Answer | Aceptación de llamada con parámetros negociados |
| 2 | Reject | Rechazo de llamada |
| 3 | Bye | Terminación de llamada |
| 4 | Upgrade | Solicitud de mayor calidad (p. ej., transporte mejorado) |
| 5 | Downgrade | Solicitud de menor calidad (p. ej., caída del ancho de banda) |
| 6 | ScreenShareStart | El par comenzó a compartir pantalla |
| 7 | ScreenShareStop | El par dejó de compartir pantalla |

### 10.9. Modelo de Cifrado

| Modo | Cifrado | Distribución de Claves |
|------|-----------|-----------------|
| Videollamada P2P | Signal Protocol por trama | Acuerdo de claves X3DH |
| Video grupal | Clave de canal grupal (AES-GCM) | Distribuida via Signal Protocol en la creación de sesión |
| Compartir pantalla | Igual que el modo de llamada padre | Heredada de la sesión de videollamada |

---

## 11. Ver Juntos

> **Estado a partir del 2026-05-05 — diseño + andamiaje C#, misma madurez que
> §10.** Los tipos de paquete `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) están definidos en el cable y
> probados con fixtures. `AetherMesh.Streaming.WatchTogetherService` proporciona el
> skeleton de coordinación (estado de sesión, propagación de comandos de sincronización vía
> `IMeshSender`, helpers de compensación de RTT); la ingesta de BitTorrent, la liquidación
> ChipIn SDPKT y la obtención de fragmentos de pares no están implementadas en ningún
> lenguaje. Trate la prosa a continuación como el protocolo objetivo; el documento de
> diseño prospectivo en `docs/adaptive-secure-streaming-spec.md` cubre el mismo
> terreno con más detalle.


Ver Juntos permite la reproducción sincronizada de medios en un grupo de pares de la malla. El host tiene control exclusivo sobre la reproducción (play, pausa, seek, velocidad). Los comandos de sincronización incluyen marcas temporales de reloj de pared para la compensación de RTT.

### 11.1. Modos de Visualización

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | Solo paquetes de sincronización (< 100 bytes cada uno) | Cualquiera (funciona sobre BLE) |
| 1 | StreamFromHost | Transferencia de fragmentos P2P (reutiliza P2pContentService) | WiFi Direct o Internet |
| 2 | BitTorrent | Malla + swarm externo vía nodos gateway | WiFi Direct o Internet |

### 11.2. Modo SharedFile

Ambos participantes tienen el mismo archivo (coincidido por hash de contenido SHA-256). Solo se intercambian paquetes `WatchSync`. Este es el modo más eficiente en ancho de banda y funciona sobre BLE.

1. El host crea una sesión de visualización con `contentHash` (SHA-256 del archivo).
2. Los participantes se unen y reportan `IsReady = true` cuando su reproductor está cargado.
3. La sesión comienza cuando TODOS los participantes reportan listos.
4. El host envía comandos de play/pausa/seek/velocidad como paquetes `WatchSync` (tipo 29).
5. Los receptores aplican la compensación de RTT: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. Modo StreamFromHost

Solo el host tiene el archivo. El host genera un `ContentManifest` (reutilizando el sistema de contenido P2P) y los participantes descargan fragmentos a través de la malla.

- La selección de fragmentos usa la estrategia `SequentialFromPosition` (no `RarestFirst`): prioriza los fragmentos adelante de la posición de reproducción actual, luego rellena para seeding.
- Objetivo de buffer: 30 segundos adelante (`WatchTogetherBufferAheadSeconds`).
- Auto-pausa: Si el buffer de CUALQUIER participante cae por debajo de 10 segundos (`WatchTogetherMinBufferSeconds`), la sesión hace pausa automática a todos los participantes con un comando de sincronización `BufferUnderrun`. La reproducción se reanuda cuando todos los participantes tienen buffer suficiente (`BufferReady`).
- A medida que los espectadores descargan fragmentos, se convierten en seeders para otros espectadores (enjambre al estilo BitTorrent dentro de la malla).

### 11.4. Modo BitTorrent

Un participante comparte un archivo `.torrent` o magnet link en el chat de grupo. El paquete `TorrentMetadata` (tipo 34) distribuye la información del torrent a todos los participantes de la sesión.

**Puente Malla-a-Swarm:**
- Los nodos gateway (nodos con internet) descargan piezas del swarm BitTorrent externo.
- Los nodos gateway recifran las piezas descargadas para distribución en malla y las hacen de semilla a los pares de la malla.
- Los pares de la malla sin internet reciben piezas de los nodos gateway y entre sí.
- El motor de contenido P2P traduce entre el modelo de piezas de BitTorrent y el modelo de fragmentos de Aether.

Una vez que se ha almacenado suficiente contenido en buffer, la reproducción de ver juntos comienza usando el mismo protocolo de sincronización que el modo SharedFile.

### 11.5. Máquina de Estados de Sesión de Visualización

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

Estados: `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. Tipos de Comandos de Sincronización

| Enum Value | Type | Descripción |
|------------|------|-------------|
| 0 | Play | Reanudar reproducción en la posición especificada |
| 1 | Pause | Pausar en la posición especificada |
| 2 | Seek | Saltar a la posición especificada |
| 3 | Speed | Cambiar la velocidad de reproducción |
| 4 | BufferUnderrun | Auto-pausa — el buffer de un participante está críticamente bajo |
| 5 | BufferReady | Reanudar — todos los participantes tienen buffer suficiente |

### 11.7. Compensación de RTT

Los comandos de sincronización incluyen un campo `WallClockMs` (milisegundos de época Unix). Cuando un receptor procesa un comando de sincronización:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Para comandos Play y BufferReady: `adjustedPosition = commandPosition + networkDelay`
4. Para comandos Pause y Seek: la posición se aplica exactamente (sin ajuste necesario ya que la reproducción se detiene/salta).

Esto garantiza que todos los participantes estén sincronizados dentro de la mitad del RTT de la red.

### 11.8. Reacciones

Los participantes pueden reaccionar al contenido durante la reproducción:

- **Reacciones de emoji**: Paquete `WatchReaction` (tipo 30) con `Type = Emoji`, llevando la cadena de emoji y la posición del medio en el momento de la reacción.
- **Comentarios de voz**: Paquete `WatchReaction` con `Type = VoiceComment`, llevando datos de audio codificados en Opus (máximo 10 segundos). Los datos de voz se incluyen en el campo `VoiceData` de la reacción.

Las reacciones se difunden a todos los participantes de la sesión. Están marcadas temporalmente con la posición del medio, lo que permite la visualización sincronizada con la reproducción.

### 11.9. ChipIn — Adquisición de Contenido Grupal

ChipIn permite a los miembros del grupo juntar fondos (en ZAR, liquidados via billeteras SDPKT a través de LedgerAPI) para adquirir colectivamente contenido para la visualización grupal.

**Máquina de estados:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

Estados: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**Flujo:**
1. El iniciador crea un `ChipInPool` con el monto objetivo y la descripción del contenido.
2. Los participantes contribuyen montos mediante transacciones de billetera SDPKT.
3. Cuando `CollectedAmount >= TargetAmount`, el estado transiciona a `Funded`.
4. El sistema adquiere el contenido (p. ej., inicia una descarga de BitTorrent).
5. Una vez que el contenido está disponible, el estado transiciona a `Acquired` y puede comenzar ver juntos.

Cada contribución se registra con un ID de transacción SDPKT para la pista de auditoría.

### 11.10. Modelo de Cifrado

| Modo | Cifrado | Distribución de Claves |
|------|-----------|-----------------|
| Comandos de sincronización de visualización | Clave de canal/conversación | Sesión de Signal Protocol existente |
| Fragmentos de contenido (StreamFromHost) | Clave de contenido por manifiesto | Distribuida via Signal Protocol |
| Piezas de BitTorrent | Recifradas en la ingesta | El gateway descarga texto plano del swarm, cifra para la malla |
| Reacciones de visualización | Clave de sesión | Derivada de la clave de conversación |

### 11.11. Banderas de Función

Todas las funciones de video y ver juntos están bloqueadas detrás de banderas de función (todas desactivadas de manera predeterminada):

| Flag | Parent | Descripción |
|------|--------|-------------|
| AETHERMESH_VIDEO_CALL | AETHERMESH_VOICE | Videollamadas P2P y grupales |
| AETHERMESH_VIDEO_GROUP | AETHERMESH_VIDEO_CALL | Sesiones de video con múltiples partes |
| AETHERMESH_SCREEN_SHARE | AETHERMESH_VIDEO_CALL | Compartir pantalla en videollamadas |
| AETHERMESH_WATCH_TOGETHER | AETHERMESH_CONTENT_P2P | Reproducción sincronizada de medios |
| AETHERMESH_WATCH_REACTIONS | AETHERMESH_WATCH_TOGETHER | Reacciones de emoji y voz |
| AETHERMESH_TORRENT_INGEST | AETHERMESH_CONTENT_P2P | Aceptación de archivos BitTorrent para distribución en malla |

Las banderas de función tienen dependencias padre: una bandera hijo solo puede activarse si su padre también está activado. Esto permite el despliegue progresivo.

---

## Apéndice A: Referencia de Constantes

Todas las constantes del protocolo están definidas en `ProtocolConstants` y se reproducen aquí como referencia:

### Enrutamiento
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### Descubrimiento BLE
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherMeshBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Seguridad
| Constant                  | Value  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constant                   | Value |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constant                  | Value  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transporte
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Latido
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### Presencia
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### Voz
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### Streaming
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### Video
| Constant                       | Value |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

---

## Apéndice B: Glosario

| Término | Definición |
|------|------------|
| **UHID** | Universal Hardware Identifier (Identificador de Hardware Universal). Una cadena única que identifica un nodo de la malla, derivada de la identidad del dispositivo y las claves criptográficas. |
| **RREQ** | Route Request (Solicitud de Ruta). Un paquete de difusión utilizado para descubrir un camino hacia un nodo destino. |
| **RREP** | Route Reply (Respuesta de Ruta). Un paquete unicast enviado de vuelta a lo largo de la ruta inversa establecida por un RREQ. |
| **IRK** | Identity Resolving Key (Clave de Resolución de Identidad). Una clave de 128 bits utilizada para generar y resolver Direcciones Privadas Resolubles BLE. |
| **RPA** | Resolvable Private Address (Dirección Privada Resoluble). Una dirección BLE de 6 bytes que rota periódicamente pero puede ser resuelta por los pares que poseen el IRK del emisor. |
| **X3DH** | Extended Triple Diffie-Hellman. Un protocolo de acuerdo de claves que habilita el establecimiento asincrónico de sesiones. |
| **DTN** | Delay-Tolerant Networking (Red Tolerante a Retardos). Un paradigma store-and-forward para entornos con conectividad intermitente. |
| **Gateway** | Un nodo de la malla que tiene conectividad a internet y conecta el tráfico de la malla con/desde servicios basados en IP. |
| **HKDF** | HMAC-based Key Derivation Function (Función de Derivación de Claves basada en HMAC). Utilizada para derivar múltiples claves de un único secreto compartido. |
| **Pre-key bundle** | Un conjunto de claves publicado que permite a un emisor establecer una sesión cifrada sin que el destinatario esté en línea. |
| **SFU** | Selective Forwarding Unit (Unidad de Reenvío Selectivo). Un nodo relay que recibe un stream de video de cada emisor y lo distribuye a todos los demás participantes, reduciendo el ancho de banda de carga por nodo. |
| **ChipIn** | Mecanismo de financiación grupal donde los participantes juntan fondos SDPKT para adquirir colectivamente contenido para la visualización grupal. |
| **NAL** | Network Abstraction Layer (Capa de Abstracción de Red). El formato de encapsulación utilizado por los codecs H.264 y H.265 para empaquetar tramas de video. |

---

## Apéndice C: Referencias

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
