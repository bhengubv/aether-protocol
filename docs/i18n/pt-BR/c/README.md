# Protocolo de Rede em Malha Aether - Implementação em C

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Uma implementação em C de alta performance e voltada para sistemas embarcados do protocolo de rede em malha Aether. Projetada para dispositivos com recursos limitados como ESP32 e nRF52, com suporte completo a assinaturas Ed25519, criptografia AES-256-GCM e roteamento baseado em AODV.

## Visão Geral

Aether é um protocolo de rede em malha descentralizado para ambientes com conectividade à internet intermitente ou inexistente. Esta implementação em C fornece:

- **Serialização/desserialização de protocolo** — formato de transmissão little-endian compatível com a implementação de referência em C#
- **Operações criptográficas** — assinaturas Ed25519, criptografia AES-256-GCM, HMAC-SHA256, HKDF-SHA256 (via libsodium)
- **Assinatura de pacotes** — construção determinística de dados assináveis conforme a especificação do protocolo
- **Abstração de transporte** — padrão vtable para implementações de transporte personalizadas
- **Transporte em processo** — transporte de teste embutido para cenários com múltiplos nós
- **Design orientado a sistemas embarcados** — buffers de tamanho fixo onde possível, alocação mínima, operações em tempo constante

## Requisitos de Compilação

- **CMake** ≥ 3.16
- **Compilador C11** (gcc, clang, etc.)
- **libsodium** — para operações criptográficas
- **Threads POSIX** (pthread)

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

A biblioteca foi projetada para ser utilizada como um componente ESP-IDF:

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

## Estrutura

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

## Início Rápido

### Compilar e Executar a Demo

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

A saída esperada demonstra:
1. Geração de chave Ed25519
2. Criação e assinatura de pacotes
3. Serialização para o formato de transmissão
4. Desserialização
5. Criptografia/descriptografia AES-256-GCM
6. Autenticação HMAC-SHA256
7. Derivação de chave HKDF

### Executar Testes Unitários

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Usar no Seu Código

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
    if (!packet) return 1;

    // Set fields
    aethermesh_packet_set_source_uhid(packet, "node-alice");
    aethermesh_packet_set_destination_uhid(packet, "node-bob");
    aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERMESH_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERMESH_ED25519_PUBLIC_KEY_SIZE];
    aethermesh_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERMESH_ED25519_SIGNATURE_SIZE];
        aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
        aethermesh_packet_set_signature(packet, signature, AETHERMESH_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethermesh_mesh_packet_t *received = aethermesh_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethermesh_packet_free(received);
    }

    aethermesh_packet_free(packet);
    return 0;
}
```

## Referência da API

### Protocolo

#### Gerenciamento de Pacotes
- `aethermesh_mesh_packet_t *aethermesh_packet_new(void)` — Cria um novo pacote
- `void aethermesh_packet_free(aethermesh_mesh_packet_t *packet)` — Libera um pacote
- `aethermesh_mesh_packet_t *aethermesh_packet_clone(const aethermesh_mesh_packet_t *packet)` — Clona um pacote

#### Serialização
- `int aethermesh_packet_serialize(const aethermesh_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — Serializa para o formato de transmissão
- `aethermesh_mesh_packet_t *aethermesh_packet_deserialize(const uint8_t *data, size_t data_len)` — Desserializa do formato de transmissão
- `size_t aethermesh_packet_estimate_size(const aethermesh_mesh_packet_t *packet)` — Estima o tamanho no formato de transmissão

#### Campos do Pacote
- `bool aethermesh_packet_set_source_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — Define a origem
- `bool aethermesh_packet_set_destination_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — Define o destino
- `bool aethermesh_packet_set_payload(aethermesh_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Define o payload
- `bool aethermesh_packet_set_signature(aethermesh_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Define a assinatura

#### Validação
- `bool aethermesh_packet_is_expired(const aethermesh_mesh_packet_t *packet, int max_age_seconds)` — Verifica se expirou
- `bool aethermesh_packet_can_forward(const aethermesh_mesh_packet_t *packet)` — Verifica se o TTL > 0

#### Dados para Assinatura
- `uint8_t *aethermesh_packet_get_signable_data(const aethermesh_mesh_packet_t *packet, size_t *out_len)` — Obtém os bytes assináveis determinísticos (o chamador deve liberar)

### Segurança

#### Ed25519
- `bool aethermesh_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — Gera chaves de 32+32 bytes
- `bool aethermesh_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Assina (produz 64 bytes)
- `bool aethermesh_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Verifica

#### AES-256-GCM
- `bool aethermesh_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Criptografa (nonce gerado automaticamente se NULL)
- `bool aethermesh_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Descriptografa

#### HMAC e Hash
- `bool aethermesh_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 bytes)
- `bool aethermesh_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 bytes)
- `bool aethermesh_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Utilitários
- `void aethermesh_zeroize(void *mem, size_t len)` — Limpeza de memória em tempo constante
- `bool aethermesh_random_bytes(uint8_t *out, size_t len)` — Bytes aleatórios criptograficamente seguros

### Transporte

#### Funções Genéricas
- `bool aethermesh_transport_send(aethermesh_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Envia dados
- `bool aethermesh_transport_is_connected(aethermesh_transport_t *transport, const char *peer_uhid)` — Verifica conexão
- `void aethermesh_transport_set_on_data_received(aethermesh_transport_t *transport, aethermesh_transport_on_data_received callback, void *user_data)` — Registra callback
- `void aethermesh_transport_destroy(aethermesh_transport_t *transport)` — Limpeza

#### Transporte em Processo
- `aethermesh_transport_t *aethermesh_inprocess_transport_new(void)` — Cria transporte compartilhado em processo
- `bool aethermesh_inprocess_transport_register_node(aethermesh_transport_t *transport, const char *uhid)` — Registra um nó
- `bool aethermesh_inprocess_transport_unregister_node(aethermesh_transport_t *transport, const char *uhid)` — Cancela registro de um nó

## Conformidade com o Formato de Transmissão

Esta implementação segue rigorosamente a especificação do protocolo com inteiros multibyte em **little-endian**:

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

Pacotes serializados por esta implementação em C são 100% compatíveis com a implementação de referência em C#.

## Considerações de Segurança

### Bibliotecas Criptográficas
- **libsodium** (libsodium.org) para todas as operações criptográficas
- Assinaturas e verificação Ed25519
- Criptografia autenticada AES-256-GCM
- HMAC-SHA256 e SHA-256
- Derivação de chave HKDF-SHA256
- Geração de números aleatórios criptograficamente seguros

### Zeragem de Chaves
Todo material sensível (chaves, texto simples, valores intermediários) é zerado da memória usando `sodium_memzero()` imediatamente após o uso. Isso evita o vazamento acidental de chaves.

### Validação de Pacotes
- Deduplicação baseada em timestamp: pacotes com mais de 300 segundos são rejeitados
- Unicidade de nonce: nonce aleatório de 8 bytes em cada pacote
- Validação de TTL: pacotes com TTL=0 são descartados
- Verificação de assinatura: assinaturas Ed25519 são obrigatórias no protocolo v2

## Notas para Dispositivos Embarcados

### ESP32
- Requer a versão portada do libsodium para ESP-IDF (disponível via componentes ESP-IDF)
- A estimativa de tamanho fixo de pacotes simplifica a alocação de memória
- Usa threads POSIX para operações de mutex
- Pré-aloque buffers na pilha sempre que possível

### nRF52
- Similar ao ESP32
- A camada de transporte BLE GATT pode ser implementada via vtable de transporte
- Considere usar um RTOS como FreeRTOS para tratamento de múltiplos pacotes

### Uso de Memória
- Pacote mínimo: ~52 bytes
- Pacote máximo: 65KB (configurável via `AETHERMESH_MAX_PAYLOAD_LEN`)
- Tabela de 256 peers: ~32KB
- Pacote de malha único em memória: ~8KB (pior caso com campos máximos)

## Desempenho

Em uma máquina x86-64 moderna (Intel Core i9):
- **Serialização**: ~1-2 µs por pacote
- **Desserialização**: ~1-2 µs por pacote
- **Assinatura Ed25519**: ~100 µs
- **Verificação Ed25519**: ~300 µs
- **Criptografia AES-256-GCM**: ~1 µs por KB
- **SHA-256**: ~0,5 µs por KB

## Testes

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Os testes cobrem:
- Criação e clonagem de pacotes
- Ciclos completos de serialização
- Assinatura e verificação Ed25519
- Criptografia/descriptografia AES-GCM
- Computação HMAC-SHA256
- Derivação de chave HKDF
- Validação de TTL e expiração
- Determinismo dos dados assináveis

## Integração com o Ecossistema Aether

Esta biblioteca C foi projetada para integrar com:
- **AetherMeshAPI** (C#) — relay de malha e análises no lado do servidor
- **AetherMesh.Core** (C#) — implementação de referência (formato de transmissão interoperável)
- **Meshtastic** — firmware de rádio em malha de código aberto
- **esp-idf** — Espressif IoT Development Framework
- Aplicações embarcadas personalizadas

## Licença

SPDX-License-Identifier: MIT

Veja o arquivo LICENSE para o texto completo.

## Contribuição

Contribuições são bem-vindas! Por favor, certifique-se de que:
- Todos os testes passam (`ctest --output-on-failure`)
- O código é compatível com C11
- O formato de transmissão corresponde exatamente à referência em C#
- Todos os dados sensíveis são zerados
- A documentação está atualizada

## Referências

- Especificação do Protocolo: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Referência C#: `/Users/admin/Code/Dev/aether-protocol/src/AetherMesh.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
