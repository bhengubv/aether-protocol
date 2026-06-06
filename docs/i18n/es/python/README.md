# Protocolo de Red en Malla Aether - Implementación en Python

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Una implementación en Python del protocolo de red en malla Aether, que proporciona operaciones criptográficas compatibles a nivel de cable con la implementación de referencia en C#.

## Descripción general

Aether es un protocolo de red en malla descentralizado diseñado para entornos con conectividad a internet intermitente o nula. Este paquete de Python ofrece:

- **Firma Ed25519**: Generación de claves, firma y verificación usando PyNaCl
- **Signal Protocol X3DH**: Intercambio de claves asíncrono con ECDH P-256
- **Cifrado AES-256-GCM**: Cifrado simétrico por mensaje con nonces de 12 bytes
- **Derivación de claves HKDF-SHA256**: Derivación de claves conforme a RFC 5869 con cadenas de información específicas por contexto
- **Trinquete simétrico**: Derivación de claves de mensaje basada en HMAC-SHA256 con secreto hacia adelante
- **Serialización de paquetes**: Formato binario de cable en little-endian compatible con la implementación en C#
- **Prevención de ataques de repetición**: Deduplicación basada en nonce con TTL de 5 minutos
- **Transporte en proceso**: Transporte simulado para pruebas de comunicación en malla

## Instalación

### Desde PyPI (cuando se publique)
```bash
pip install aether-protocol
```

### Desde el código fuente
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Dependencias de desarrollo
```bash
pip install -e ".[dev]"
```

## Inicio rápido

```python
import asyncio
from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.signal_protocol import SignalProtocolService
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Arquitectura

### Estructura del paquete

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherMeshNode, PeerInfo, RouteEntry)
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

## Características principales

### 1. Servicio de firma Ed25519

Utiliza PyNaCl (libsodium) para operaciones criptográficas:

```python
from aethermesh.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Tamaños de clave:**
- Clave privada: 32 bytes (semilla Ed25519)
- Clave pública: 32 bytes (punto Ed25519)
- Firma: 64 bytes

### 2. Signal Protocol

Implementa el intercambio de claves X3DH con trinquete simétrico para secreto hacia adelante:

```python
from aethermesh.security.signal_protocol import SignalProtocolService

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

**Derivación de claves:**
- Usa HKDF-SHA256 con sal: `"AetherMeshSignal"`
- Info de clave raíz: `"aether-root-v1"`
- Info de cadena de envío: `"aether-chain-send-v1"`
- Info de cadena de recepción: `"aether-chain-recv-v1"`

**Trinquete simétrico:**
- Usa HMAC-SHA256 con la clave de cadena
- Deriva nuevas claves de mensaje y avanza la cadena con cada mensaje
- Admite hasta 1000 claves omitidas para entrega fuera de orden
- Cifrado por mensaje: AES-256-GCM con nonce aleatorio de 12 bytes

### 3. Serialización de paquetes

Formato binario compatible a nivel de cable con la implementación en C#:

```python
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

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

**Formato de cable (Little-Endian):**
- Versión de protocolo: 1 byte
- Tipo de paquete: 1 byte
- ID de paquete: 16 bytes (UUID)
- Prioridad: 1 byte
- TTL: 4 bytes (int32)
- TimestampMs: 8 bytes (int64)
- Longitud de SourceUhid: 2 bytes + datos UTF-8
- Longitud de DestinationUhid: 2 bytes + datos UTF-8
- Longitud de PacketNonce: 2 bytes + datos
- Longitud de carga útil: 4 bytes + datos
- Longitud de firma: 2 bytes + datos

### 4. Firma de paquetes

Firma paquetes usando Ed25519 y detecta ataques de repetición:

```python
from aethermesh.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Datos firmables:**
Según la sección 2.3 de la especificación del protocolo, la firma cubre:
- PacketNonce (8 bytes)
- TimestampMs (8 bytes, int64 little-endian)
- Type (4 bytes, int32 little-endian)
- SourceUhid (longitud + UTF-8)
- DestinationUhid (longitud + UTF-8)
- SHA-256(Payload) (32 bytes)
- Ttl (4 bytes, int32 little-endian)
- Priority (4 bytes, int32 little-endian)

**Prevención de repetición:**
- Mantiene caché de pares (sender_uhid, nonce) vistos
- TTL de 5 minutos por entrada de caché
- Limpieza automática cada 60 segundos

### 5. Servicios de transporte

Clase base abstracta para transportes físicos (BLE, Wi-Fi Direct, etc.):

```python
from aethermesh.transport.in_process import InProcessTransport

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

**Características de InProcessTransport:**
- Registro global de nodos a nivel de clase
- Seguro para hilos con threading.Lock
- Ideal para pruebas y simulación de malla local
- Propiedades: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Referencia de constantes

Todas las constantes del protocolo están definidas en `aether/constants.py`:

### Criptografía
- `ED25519_PRIVATE_KEY_SIZE`: 32 bytes
- `ED25519_PUBLIC_KEY_SIZE`: 32 bytes
- `ED25519_SIGNATURE_SIZE`: 64 bytes
- `AES_GCM_NONCE_SIZE`: 12 bytes
- `AES_GCM_TAG_SIZE`: 16 bytes
- `MAX_SKIPPED_KEYS`: 1000

### Enrutamiento
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(Ver `constants.py` para la lista completa)

## Ejecución de la demostración

Demuestra todas las características principales con salida en color:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

La demostración cubre:
1. Generación de claves Ed25519 y firma
2. Creación de nodos con AetherMeshNode
3. Intercambio de claves Signal Protocol X3DH
4. Cifrado y descifrado de mensajes
5. Serialización/deserialización de paquetes
6. Firma de paquetes y detección de ataques de repetición
7. Comunicación a través del transporte en proceso
8. Flujo completo de cifrado de extremo a extremo

## Dependencias

### Tiempo de ejecución
- `pynacl>=1.5.0` - Firma Ed25519 vía libsodium
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Desarrollo
- `pytest>=7.4.0` - Marco de pruebas
- `pytest-asyncio>=0.21.0` - Soporte de pruebas asíncronas
- `black>=23.0.0` - Formato de código
- `mypy>=1.5.0` - Verificación estática de tipos
- `ruff>=0.1.0` - Análisis estático

## Compatibilidad

**Versión de Python:** 3.10+

**Plataforma:** Multiplataforma (Windows, macOS, Linux)

**Backend criptográfico:** Utiliza los backends del sistema libsodium y la biblioteca cryptography, garantizando un comportamiento consistente entre plataformas.

## Referencias del protocolo

- **Enrutamiento AODV:** RFC 3561
- **Acuerdo de claves X3DH:** Signal Foundation, noviembre de 2016
- **Double Ratchet:** Signal Foundation, noviembre de 2016
- **HKDF:** RFC 5869 (Extract-and-Expand basado en HMAC)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB et al., 2012

## Consideraciones de seguridad

### Borrado de claves
El material criptográfico intermedio se borra tras su uso:
- Secretos compartidos de ECDH
- Claves de mensaje del trinquete simétrico
- Material de clave derivado en el contexto de establecimiento

En Python, el borrado real en memoria está limitado, pero los datos sensibles se eliminan del ámbito de la variable inmediatamente después de su uso.

### Modelo de amenazas
Aether asume:
- Escucha pasiva en BLE/Wi-Fi
- Inyección activa de paquetes y repetición
- Ataques Sybil mediante creación de nodos falsos
- Denegación de servicio selectiva

Las protecciones incluyen:
- **Confidencialidad:** Claves por mensaje AES-256-GCM
- **Integridad:** Firmas de paquetes Ed25519
- **Prevención de repetición:** Deduplicación basada en nonce
- **Secreto hacia adelante:** Trinquete simétrico con claves por mensaje
- **Autenticación de rutas:** Respuestas de ruta firmadas

### Limitaciones
- La entrega de mensajes fuera de orden se admite hasta 1000 mensajes
- Los mensajes más allá de ese umbral son rechazados
- Las direcciones BLE rotan cada 15 minutos (no implementado en Python)
- La ventana de migración de P-256 a Ed25519 es de 30 días (respaldo aún no implementado)

## Pruebas

Ejecutar el conjunto de pruebas:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## Licencia

Licencia MIT - Consulte el archivo LICENSE para más detalles

## Contribuciones

Para contribuir con mejoras:

1. Asegúrese de que el código siga el estilo PEP 8 (use `black` para el formato)
2. Agregue anotaciones de tipo a todas las funciones
3. Incluya docstrings para las APIs públicas
4. Ejecute `mypy` para la verificación de tipos
5. Agregue pruebas para las nuevas características

## Referencias

- Especificación del protocolo Aether: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Implementación de referencia en C#: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
