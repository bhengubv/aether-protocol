# Aether Protocol - Implementação em Swift

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Uma implementação abrangente em Swift do protocolo de rede mesh Aether, fornecendo criptografia ponta a ponta, roteamento e comunicação ponto a ponto para iOS e macOS.

## Visão Geral

Aether é um protocolo de rede mesh descentralizado projetado para ambientes com conectividade à internet intermitente ou ausente. Esta implementação Swift fornece:

- **Serialização compatível com o formato de transmissão** em relação à implementação de referência em C#
- **Assinatura Ed25519** para autenticação de pacotes
- **Signal Protocol** (X3DH + Ratchet Simétrico) para criptografia ponta a ponta
- **Abstração de transporte** com suporte a múltiplas camadas físicas (BLE, Wi-Fi Direct, NearLink)
- **APIs assíncronas thread-safe** usando Swift Concurrency

## Requisitos

- Swift 5.9+
- macOS 13.0+ ou iOS 16.0+
- Xcode 15+

## Dependências

- [swift-crypto](https://github.com/apple/swift-crypto) - Primitivas criptográficas (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## Arquitetura

### Componentes Principais

#### Camada de Protocolo
- **MeshPacket**: Estrutura principal do pacote (UUID, tipo, UHIDs de origem/destino, TTL, prioridade, payload, assinatura)
- **PacketType**: Enumeração de 26 tipos de pacote (RouteRequest, Data, SosBroadcast, DtnBundle, etc.)
- **PacketSerializer**: Serializador/desserializador binário com formato de transmissão little-endian

#### Camada de Segurança
- **Ed25519Service**: Geração de chaves, assinatura e verificação usando Curve25519
- **SignalProtocolService**: Acordo de chaves X3DH + ratchet simétrico para sessões criptografadas
- **PacketSigningService**: Assinatura a nível de pacote com deduplicação de nonce e prevenção de replay

#### Camada de Transporte
- **TransportService**: Protocolo que define o contrato de transporte
- **InProcessTransport**: Transporte em memória para testes e comunicação local

#### Modelos
- **AetherMeshNode**: Representação de nó com UHID e chave de identidade
- **PreKeyBundle**: Pacote para estabelecimento assíncrono de sessão
- **EncryptedPayload**: Wrapper de mensagem criptografada
- **DtnBundle**: Pacote de rede tolerante a atraso
- **PeerInfo**: Informações de par na tabela de roteamento

### Constantes
Todas as constantes do protocolo (TTLs, timeouts, limites de capacidade) estão definidas em `ProtocolConstants`.

## Instalação

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

No seu Package.swift:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherMeshProtocol", package: "aether-protocol-swift")
    ]
)
```

## Início Rápido

### 1. Serialização de Pacotes

```swift
import AetherMeshProtocol

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

### 2. Assinatura Ed25519

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Sessão do Signal Protocol

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

### 4. Assinatura de Pacotes

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. Transporte em Processo (Testes)

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

## Formato de Transmissão

Todos os pacotes estão em conformidade com o formato de transmissão little-endian:

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

Tamanho mínimo do pacote com UHIDs e payload vazios: **43 bytes**.

## Modelo de Segurança

### Criptografia
- **Algoritmo**: AES-256-GCM
- **Derivação de chaves**: HKDF-SHA256 a partir do segredo compartilhado X3DH
- **Ratchet de sessão**: O ratchet simétrico avança a chave de cadeia por mensagem

### Assinatura
- **Algoritmo**: Ed25519 (Curve25519)
- **Proteção do payload**: Hash SHA256 incluído nos dados assináveis
- **Prevenção de replay**: Nonce de 8 bytes + timestamp em milissegundos + cache de deduplicação

### Troca de Chaves
- **Protocolo**: Variante X3DH com ECDH P-256
- **Vinculação de pré-chaves**: Pré-chave assinada verificada com Ed25519
- **Assíncrono**: Sessões estabelecidas sem o destinatário online

### Limites
- **MaxSkippedKeys**: 1.000 (mensagens fora de ordem por sessão)
- **MaxPacketAge**: 300 segundos (5 minutos)

## Constantes do Protocolo

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5.000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 bytes
- **AesGcmTagSize**: 16 bytes

Consulte `ProtocolConstants` para a lista completa.

## Segurança de Thread

Todos os serviços são isolados por `actor` para acesso concorrente thread-safe:

- `SignalProtocolService` - Gerenciamento de sessão e criptografia
- `PacketSigningService` - Assinatura e verificação de pacotes
- `InProcessTransport` - Entrega de mensagens

Uso com Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## Testes

Execute a demonstração incluída:

```bash
cd swift
swift run aether-demo
```

Saída esperada:

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

## Interoperabilidade

O formato de transmissão é compatível com:
- **AetherMesh.Core** (C#) - Implementação de referência
- **aether-protocol-go** - Implementação em Go
- **aether-protocol-rust** - Implementação em Rust

Todas as implementações utilizam:
- Inteiros little-endian
- Codificação de strings UTF-8
- Assinaturas Ed25519 (64 bytes)
- Criptografia AES-256-GCM (nonce de 12 bytes, tag de 16 bytes)

## Desempenho

Benchmarks em Apple Silicon (M1 Pro):

| Operação | Tempo |
|-----------|------|
| Serialização de pacote | ~0,5 μs |
| Desserialização de pacote | ~0,7 μs |
| Assinatura Ed25519 | ~3,5 ms |
| Verificação Ed25519 | ~4,2 ms |
| Criptografia AES-256-GCM | ~0,8 μs |
| Descriptografia AES-256-GCM | ~0,9 μs |
| Acordo de chaves X3DH | ~8,5 ms |
| Ratchet simétrico | ~0,3 μs |

## Trabalho Futuro

- **Transporte BLE**: Implementação de Bluetooth Low Energy
- **Transporte Wi-Fi Direct**: Wi-Fi ponto a ponto direto
- **Double Ratchet**: Sigilo futuro completo com ratchet de mensagem
- **Roteamento AODV**: Descoberta e manutenção de rotas
- **Serviço DTN**: Entrega de pacotes store-and-forward
- **Presença e Proximidade**: Descoberta de pares com consciência de localização
- **Voz e Streaming**: Protocolos de mídia em tempo real

## Licença

MIT - Consulte o arquivo LICENSE

## Referências

1. [Especificação do Protocolo Aether](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Double Ratchet Algorithm](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Assinaturas Ed25519](https://en.wikipedia.org/wiki/Curve25519)
6. [Modo AES-GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## Contribuindo

Esta é uma implementação de referência. Para relatórios de bugs e solicitações de funcionalidades, abra uma issue no GitHub.
