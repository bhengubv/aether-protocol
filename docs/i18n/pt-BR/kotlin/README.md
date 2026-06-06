# Protocolo Aether - Implementação em Kotlin

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Uma implementação completa e pronta para produção em Kotlin do protocolo de rede em malha Aether, com compatibilidade total de formato de transmissão entre linguagens com a implementação de referência em C#.

## Visão Geral

Aether é um protocolo de rede em malha descentralizado para ambientes com conectividade à internet intermitente ou inexistente. Esta implementação em Kotlin fornece:

- **Compatibilidade de formato de transmissão** com C# (a serialização binária de pacotes corresponde exatamente)
- **Assinatura Ed25519** para autenticação e integridade de pacotes
- **Protocolo Signal** para criptografia de ponta a ponta (acordo de chaves X3DH, ratchet simétrico, AES-256-GCM)
- **Acordo de chaves ECDH P-256** para estabelecimento de sessão
- **Serialização/desserialização de pacotes** com inteiros multibyte little-endian
- **Proteção contra repetição** usando deduplicação de nonce
- **Abstração de transporte** para BLE, Wi-Fi Direct e mensagens em processo

## Estrutura do Projeto

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

## Compilação

### Pré-requisitos

- JDK 17 ou superior
- Gradle 8.0 ou superior

### Compilar

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Executar Demo

```bash
./gradlew run
```

A demo demonstra:
1. Geração de par de chaves Ed25519
2. Criação e troca de pacotes de pré-chaves
3. Estabelecimento de sessão do Protocolo Signal
4. Assinatura de pacotes com Ed25519
5. Serialização/desserialização de pacotes
6. Criptografia e descriptografia de mensagens
7. Proteção contra repetição
8. Mensagens via transporte em processo

## Componentes Principais

### 1. Serialização de Pacotes (`PacketSerializer`)

Formato de transmissão (little-endian):
- Versão do protocolo (1 byte)
- Tipo de pacote (1 byte)
- ID do pacote / UUID (16 bytes)
- Prioridade (1 byte)
- TTL (4 bytes, int32)
- TimestampMs (8 bytes, int64)
- SourceUhid (prefixo de comprimento de 2 bytes + bytes UTF-8)
- DestinationUhid (prefixo de comprimento de 2 bytes + bytes UTF-8)
- PacketNonce (prefixo de comprimento de 2 bytes + bytes)
- Payload (prefixo de comprimento de 4 bytes + bytes)
- Signature (prefixo de comprimento de 2 bytes + bytes)

Totalmente compatível com o `PacketSerializer` em C#.

### 2. Assinatura Ed25519 (`Ed25519Service`, `PacketSigning`)

- **Geração de chaves**: semente de chave privada de 32 bytes, chave pública de 32 bytes
- **Assinatura**: assinaturas de 64 bytes sobre dados assináveis determinísticos
- **Verificação**: Substitui ECDSA P-256 durante o período de migração
- **Formato dos dados assináveis**: Corresponde exatamente à especificação em C# (nonce do pacote, timestamp, tipo, UHIDs, hash do payload, TTL, prioridade)
- **Proteção contra repetição**: Deduplicação de nonce com TTL de 5 minutos

### 3. Protocolo Signal (`SignalProtocol`)

Implementa o acordo de chaves X3DH com ratchet simétrico:

**Estabelecimento de sessão:**
- Busca o pacote de pré-chaves do peer
- Verifica a assinatura do pacote com Ed25519
- Executa X3DH: DH(identidade local, pré-chave assinada remota) + DH(identidade local, pré-chave remota)
- Deriva a chave raiz e as chaves de cadeia usando HKDF-SHA256

**Criptografia/Descriptografia:**
- Ratchet simétrico com HMAC-SHA256
- AES-256-GCM com nonce aleatório de 12 bytes
- Chaves por mensagem com sigilo futuro
- Tratamento de mensagens fora de ordem (cache de chaves ignoradas, máx. 1000 chaves)

**Parâmetros:**
- Informação de derivação da chave raiz: `"aether-root-v1"`
- Informação de derivação da cadeia de envio: `"aether-chain-send-v1"`
- Informação de derivação da cadeia de recebimento: `"aether-chain-recv-v1"`
- Salt da chave de mensagem: `0x01`, salt da chave de cadeia: `0x02`

### 4. Abstração de Transporte (`TransportService`)

Interface para transportes físicos (BLE, Wi-Fi Direct, etc.):

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

**InProcessTransport:** Implementação de referência usando `ConcurrentHashMap` global para testes/demo.

### 5. Modelos de Domínio (`Models.kt`)

- **AetherMeshNode**: Identidade do nó com UHID, chave pública, capacidades, geohash
- **PeerInfo**: Peer conhecido com pontuação de confiabilidade e timestamp da última visualização
- **RouteEntry**: Entrada da tabela de roteamento com contagem de saltos e pontuação de qualidade
- **NodeCapabilities**: Campo de bits (BLE, Wi-Fi Direct, Gateway, Relay, SOS, Streaming, Voice, DTN)
- **DtnBundle**: Bundle de armazenamento e encaminhamento com expiração e contagem de cópias

## Constantes do Protocolo

Constantes principais (de `Constants.kt`):

| Categoria | Constante | Valor |
|----------|----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Tipos de Pacotes

Todos os 23 tipos de pacotes correspondem aos valores do enum em C# (1-23):

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

## Dependências

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — Suporte a formato de chaves
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await, Flow
- **org.slf4j:slf4j-api:2.0.9** — Registro de logs
- **kotlin-stdlib** — Biblioteca padrão Kotlin

## Exemplos de Uso

### Geração de Chaves

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Assinatura de Pacotes

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

### Serialização de Pacotes

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Criptografia com Protocolo Signal

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

## Compatibilidade entre Linguagens

Esta implementação mantém **compatibilidade exata de formato de transmissão** com a implementação de referência em C#:

- Formato binário de pacotes: layout little-endian idêntico
- Enum de tipo de pacote: valores correspondem exatamente ao enum em C# (1-23)
- Assinaturas Ed25519: compatível com NSec/libsodium
- ECDH P-256: curva padrão, compatível entre linguagens
- HKDF-SHA256: implementação padrão RFC 5869
- AES-256-GCM: padrão NIST com nonce de 12 bytes, tag de 16 bytes

Pacotes serializados em Kotlin podem ser desserializados em C# e vice-versa.

## Testes

A implementação inclui uma demo abrangente (`Demo.kt`) que exercita:

1. Geração de chaves e exportação de chave pública
2. Geração e troca de pacote de pré-chaves
3. Estabelecimento de sessão via Protocolo Signal
4. Criação, assinatura e serialização de pacotes
5. Desserialização de pacotes e verificação de assinatura
6. Criptografia e descriptografia de mensagens
7. Prevenção de ataques de repetição
8. Mensagens via transporte em processo

Execute com:
```bash
./gradlew run
```

## Considerações de Segurança

- **Zeragem de chaves**: Todo material criptográfico intermediário é zerado após o uso usando `CryptographicOperations.ZeroMemory` (equivalente em Kotlin: `fill(0)`)
- **Proteção contra repetição**: Deduplicação de nonce com TTL de 5 minutos previne ataques de repetição
- **Sigilo futuro**: Chaves por mensagem derivadas do ratchet de cadeia
- **Tratamento fora de ordem**: Cache de chaves ignoradas com máx. 1000 chaves para evitar esgotamento de memória
- **Autenticação RREP**: Pacotes de Resposta de Rota assinados pelo nó de destino
- **Confidencialidade do pacote**: Conteúdo das mensagens criptografado com AES-256-GCM

## Extensões Futuras

A implementação fornece ganchos para:

- **Transporte BLE** (interface `TransportService`)
- **Transporte Wi-Fi Direct** (mesma interface)
- **Roteamento epidêmico DTN** (modelo `DtnBundle` pronto)
- **Broadcast SOS** (tipo de pacote definido)
- **Beacons de presença** (tipo de pacote definido)
- **Voz e streaming** (tipos de pacotes definidos)
- **Double Ratchet** (quando transportes sempre ativos estiverem disponíveis)

## Documentação do Protocolo

Especificação completa do protocolo: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## Licença

SPDX-License-Identifier: MIT
