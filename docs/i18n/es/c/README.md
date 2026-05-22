# Protocolo de Redes en Malla Aether - Implementación en C

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Una implementación en C de alto rendimiento y adecuada para sistemas embebidos del protocolo de redes en malla Aether. Diseñada para dispositivos con recursos limitados como ESP32 y nRF52, con soporte completo para firma Ed25519, cifrado AES-256-GCM y enrutamiento basado en AODV.

## Descripción General

Aether es un protocolo de redes en malla descentralizado para entornos con conectividad a Internet intermitente o inexistente. Esta implementación en C proporciona:

- **Serialización/deserialización del protocolo** — formato de cable little-endian compatible con la implementación de referencia en C#
- **Operaciones criptográficas** — firmas Ed25519, cifrado AES-256-GCM, HMAC-SHA256, HKDF-SHA256 (mediante libsodium)
- **Firma de paquetes** — construcción determinista de datos firmables según la especificación del protocolo
- **Abstracción de transporte** — patrón vtable para implementaciones de transporte personalizadas
- **Transporte en proceso** — transporte de prueba integrado para escenarios con múltiples nodos
- **Diseño orientado a sistemas embebidos** — búferes de tamaño fijo donde sea posible, asignación mínima, operaciones en tiempo constante

## Requisitos de Compilación

- **CMake** ≥ 3.16
- **Compilador C11** (gcc, clang, etc.)
- **libsodium** — para operaciones criptográficas
- **POSIX threads** (pthread)

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

La biblioteca está diseñada para usarse como componente de ESP-IDF:

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

## Estructura

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

## Inicio Rápido

### Compilar y Ejecutar la Demostración

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

La salida esperada muestra:
1. Generación de claves Ed25519
2. Creación y firma de paquetes
3. Serialización al formato de cable
4. Deserialización
5. Cifrado/descifrado AES-256-GCM
6. Autenticación HMAC-SHA256
7. Derivación de claves HKDF

### Ejecutar Pruebas Unitarias

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Uso en tu Código

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aether_mesh_packet_t *packet = aether_packet_new();
    if (!packet) return 1;

    // Set fields
    aether_packet_set_source_uhid(packet, "node-alice");
    aether_packet_set_destination_uhid(packet, "node-bob");
    aether_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aether_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHER_ED25519_SIGNATURE_SIZE];
        aether_ed25519_sign(private_key, signable, signable_len, signature);
        aether_packet_set_signature(packet, signature, AETHER_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aether_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aether_mesh_packet_t *received = aether_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aether_packet_free(received);
    }

    aether_packet_free(packet);
    return 0;
}
```

## Referencia de la API

### Protocolo

#### Gestión de Paquetes
- `aether_mesh_packet_t *aether_packet_new(void)` — Crear un nuevo paquete
- `void aether_packet_free(aether_mesh_packet_t *packet)` — Liberar un paquete
- `aether_mesh_packet_t *aether_packet_clone(const aether_mesh_packet_t *packet)` — Clonar un paquete

#### Serialización
- `int aether_packet_serialize(const aether_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — Serializar al formato de cable
- `aether_mesh_packet_t *aether_packet_deserialize(const uint8_t *data, size_t data_len)` — Deserializar desde el formato de cable
- `size_t aether_packet_estimate_size(const aether_mesh_packet_t *packet)` — Estimar el tamaño en el cable

#### Campos del Paquete
- `bool aether_packet_set_source_uhid(aether_mesh_packet_t *packet, const char *uhid)` — Establecer origen
- `bool aether_packet_set_destination_uhid(aether_mesh_packet_t *packet, const char *uhid)` — Establecer destino
- `bool aether_packet_set_payload(aether_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Establecer carga útil
- `bool aether_packet_set_signature(aether_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Establecer firma

#### Validación
- `bool aether_packet_is_expired(const aether_mesh_packet_t *packet, int max_age_seconds)` — Comprobar si ha expirado
- `bool aether_packet_can_forward(const aether_mesh_packet_t *packet)` — Comprobar si el TTL > 0

#### Datos de Firma
- `uint8_t *aether_packet_get_signable_data(const aether_mesh_packet_t *packet, size_t *out_len)` — Obtener los bytes deterministas firmables (el llamador debe liberar la memoria)

### Seguridad

#### Ed25519
- `bool aether_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — Generar claves de 32+32 bytes
- `bool aether_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Firmar (produce 64 bytes)
- `bool aether_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Verificar

#### AES-256-GCM
- `bool aether_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Cifrar (nonce generado automáticamente si es NULL)
- `bool aether_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Descifrar

#### HMAC y Hash
- `bool aether_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 bytes)
- `bool aether_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 bytes)
- `bool aether_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Utilidades
- `void aether_zeroize(void *mem, size_t len)` — Borrado de memoria en tiempo constante
- `bool aether_random_bytes(uint8_t *out, size_t len)` — Bytes criptográficamente aleatorios

### Transporte

#### Funciones Genéricas
- `bool aether_transport_send(aether_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Enviar datos
- `bool aether_transport_is_connected(aether_transport_t *transport, const char *peer_uhid)` — Comprobar conexión
- `void aether_transport_set_on_data_received(aether_transport_t *transport, aether_transport_on_data_received callback, void *user_data)` — Registrar callback
- `void aether_transport_destroy(aether_transport_t *transport)` — Limpieza

#### Transporte en Proceso
- `aether_transport_t *aether_inprocess_transport_new(void)` — Crear transporte en proceso compartido
- `bool aether_inprocess_transport_register_node(aether_transport_t *transport, const char *uhid)` — Registrar un nodo
- `bool aether_inprocess_transport_unregister_node(aether_transport_t *transport, const char *uhid)` — Dar de baja un nodo

## Conformidad con el Formato de Cable

Esta implementación sigue estrictamente la especificación del protocolo con enteros multibyte en **little-endian**:

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

Los paquetes serializados por esta implementación en C son 100% compatibles con la implementación de referencia en C#.

## Consideraciones de Seguridad

### Bibliotecas Criptográficas
- **libsodium** (libsodium.org) para todas las operaciones criptográficas
- Firmas y verificación Ed25519
- Cifrado autenticado AES-256-GCM
- HMAC-SHA256 y SHA-256
- Derivación de claves HKDF-SHA256
- Generación criptográficamente segura de números aleatorios

### Borrado de Claves
Todo el material sensible (claves, texto plano, valores intermedios) se borra de la memoria mediante `sodium_memzero()` inmediatamente después de su uso. Esto previene fugas accidentales de claves.

### Validación de Paquetes
- Deduplicación basada en marca de tiempo: los paquetes con más de 300 segundos de antigüedad son rechazados
- Unicidad del nonce: nonce aleatorio de 8 bytes en cada paquete
- Validación de TTL: los paquetes con TTL=0 son descartados
- Verificación de firma: las firmas Ed25519 son obligatorias en la versión 2 del protocolo

## Notas para Dispositivos Embebidos

### ESP32
- Requiere el puerto de libsodium para ESP-IDF (disponible mediante los componentes de ESP-IDF)
- La estimación de tamaño fijo de paquete simplifica la asignación de memoria
- Utiliza hilos POSIX para las operaciones de mutex
- Pre-asignar búferes en la pila cuando sea posible

### nRF52
- Similar al ESP32
- La capa de transporte BLE GATT puede implementarse mediante el vtable de transporte
- Considerar el uso de un RTOS como FreeRTOS para el manejo de múltiples paquetes

### Uso de Memoria
- Paquete mínimo: ~52 bytes
- Paquete máximo: 65 KB (configurable mediante `AETHER_MAX_PAYLOAD_LEN`)
- Tabla de 256 pares: ~32 KB
- Un paquete de malla en memoria: ~8 KB (peor caso con todos los campos al máximo)

## Rendimiento

En una máquina moderna x86-64 (Intel Core i9):
- **Serialización**: ~1-2 µs por paquete
- **Deserialización**: ~1-2 µs por paquete
- **Firma Ed25519**: ~100 µs
- **Verificación Ed25519**: ~300 µs
- **Cifrado AES-256-GCM**: ~1 µs por KB
- **SHA-256**: ~0.5 µs por KB

## Pruebas

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Las pruebas cubren:
- Creación y clonación de paquetes
- Ciclos completos de serialización
- Firma y verificación Ed25519
- Cifrado/descifrado AES-GCM
- Cálculo de HMAC-SHA256
- Derivación de claves HKDF
- Validación de TTL y expiración
- Determinismo de los datos firmables

## Integración con el Ecosistema Aether

Esta biblioteca en C está diseñada para integrarse con:
- **AetherAPI** (C#) — relay de malla del lado del servidor y análisis
- **Aether.Core** (C#) — implementación de referencia (formato de cable interoperable)
- **Meshtastic** — firmware de radio en malla de código abierto
- **esp-idf** — Espressif IoT Development Framework
- Aplicaciones embebidas personalizadas

## Licencia

SPDX-License-Identifier: MIT

Consulta el archivo LICENSE para el texto completo.

## Contribuciones

¡Las contribuciones son bienvenidas! Por favor asegúrate de que:
- Todas las pruebas pasen (`ctest --output-on-failure`)
- El código sea conforme a C11
- El formato de cable coincida exactamente con la referencia en C#
- Todos los datos sensibles sean borrados
- La documentación esté actualizada

## Referencias

- Especificación del Protocolo: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Referencia en C#: `/Users/admin/Code/Dev/aether-protocol/src/Aether.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
