# Aether Protocol — Implementação em Rust

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Implementação completa em Rust do protocolo de rede mesh Aether, com compatibilidade de formato de transmissão em relação à implementação de referência em C#.

## Visão Geral

Este crate fornece:

- **Serialização/desserialização de MeshPacket** — Formato binário de transmissão idêntico ao PacketSerializer em C#
- **Assinatura Ed25519** — Geração de chave de identidade, assinatura e verificação
- **Signal Protocol** — Acordo de chaves baseado em X3DH com ratchet simétrico para sigilo futuro
- **Serviço de assinatura de pacotes** — Deduplicação de nonce e verificações de atualidade
- **Transporte em processo** — Rede mesh simulada para testes e demonstrações

## Estrutura do Projeto

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

## Funcionalidades Principais

### 1. Compatibilidade com o Formato de Transmissão

O `PacketSerializer` produz saída byte a byte idêntica à implementação em C#:

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

Todos os inteiros de múltiplos bytes utilizam ordem de bytes little-endian. Os comprimentos das strings são prefixados com u16 (SourceUhid, DestinationUhid) ou i32 (Payload, Signature), conforme especificado na especificação do protocolo.

### 2. Tipos de Pacote

Todos os 26 tipos de pacote da especificação do protocolo estão definidos:

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

### 3. Assinatura Ed25519

- Chaves privadas de 32 bytes (semente), chaves públicas de 32 bytes, assinaturas de 64 bytes
- Utiliza `ed25519-dalek` para operações criptográficas
- Zeragem segura de chaves após o uso

### 4. Signal Protocol

Acordo de chaves baseado em X3DH com ratchet simétrico:

- **Acordo de chaves:** ECDH P-256 com pré-chaves efêmeras e assinadas
- **Derivação de chaves:** HKDF-SHA256 com strings de informação únicas
  - `aether-root-v1` — Chave raiz
  - `aether-chain-send-v1` — Chave de cadeia de envio
  - `aether-chain-recv-v1` — Chave de cadeia de recebimento
- **Criptografia:** AES-256-GCM (nonce de 12 bytes, tag de 16 bytes)
- **Ratchet:** Avanço da chave de cadeia simétrica com chaves de mensagem baseadas em contador
- **Tratamento fora de ordem:** Até 1.000 chaves de mensagem ignoradas em cache

### 5. Serviço de Assinatura de Pacotes

- Geração aleatória de nonce de 8 bytes
- Timestamps com precisão de milissegundos
- Validação de atualidade (janela de 5 minutos)
- Deduplicação de nonce por remetente (previne replays)
- Limpeza automática de entradas expiradas

### 6. Transporte em Processo

Rede mesh simulada para testes:

- Registro estático de nós usando HashMap concorrente
- Entrega de mensagens fire-and-forget
- Verificações de conectividade bidirecional entre pares
- Adequado para demonstrações e testes unitários

## Uso

### Geração Básica de Chaves e Assinatura

```rust
use aethermesh_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Sessão do Signal Protocol

```rust
use aethermesh_protocol::security::SignalProtocolService;

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

### Serialização de Pacotes

```rust
use aethermesh_protocol::protocol::{MeshPacket, PacketType};
use aethermesh_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### Assinatura de Pacotes

```rust
use aethermesh_protocol::security::PacketSigningService;
use aethermesh_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### Transporte em Processo

```rust
use aethermesh_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Executando a Demonstração

```bash
cargo run --release
```

A demonstração realiza as seguintes etapas:

1. Gera chaves de identidade para Alice e Bob
2. Inicializa serviços do Signal Protocol
3. Gera e troca pré-chaves
4. Estabelece sessões criptografadas
5. Troca mensagens criptografadas
6. Cria e assina pacotes mesh
7. Verifica assinaturas de pacotes
8. Serializa e desserializa pacotes
9. Demonstra o transporte em processo

## Constantes

Todas as constantes do protocolo estão definidas em `src/constants.rs`, correspondendo à especificação em C#:

- Roteamento: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Segurança: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Transporte: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- Voz/Streaming: Diversas configurações de bitrate e buffer

## Dependências

- `ed25519-dalek` — Assinatura Ed25519
- `x25519-dalek` — Acordo de chaves X25519
- `aes-gcm` — Criptografia AES-256-GCM
- `hkdf` — Derivação de chaves HKDF
- `sha2` — Hash SHA-256
- `hmac` — Operações HMAC
- `rand` — Geração de números aleatórios
- `uuid` — Geração e serialização de GUID
- `serde` + `serde_json` — Serialização
- `tokio` — Runtime assíncrono
- `async-trait` — Métodos de trait assíncronos

## Testes

Execute todos os testes:

```bash
cargo test
```

Os testes cobrem:

- Criação de pacotes e gerenciamento de TTL
- Conversão de tipos de pacote
- Ciclos completos de serialização/desserialização
- Geração de chaves Ed25519 e verificação de assinatura
- Estabelecimento de sessão e criptografia do Signal Protocol
- Assinatura de pacotes e validação de atualidade
- Conectividade do transporte em processo

## Conformidade com o Protocolo

Esta implementação segue a especificação do protocolo Aether (Versão 2.0) com:

- ✅ Formato binário de transmissão (little-endian, prefixado por comprimento)
- ✅ Todos os 26 tipos de pacote
- ✅ Assinatura Ed25519 com deduplicação de nonce
- ✅ Acordo de chaves X3DH com HKDF-SHA256
- ✅ Criptografia AES-256-GCM com nonce de 12 bytes
- ✅ Ratchet simétrico com tratamento fora de ordem
- ✅ Geração e processamento de pacote de pré-chaves
- ✅ Construção de dados assináveis do pacote (hash SHA-256 do payload)
- ✅ Abstração de trait de transporte

## Notas

- O formato de transmissão utiliza ordem de bytes little-endian em todo o protocolo (compatível com C# BinaryPrimitives.WriteInt32LittleEndian)
- Os prefixos de comprimento de strings usam u16 para UHIDs e i32 para payload/assinatura (compatível com C# WriteUInt16/WriteInt32)
- Todo o material de chave criptográfica é zerado após o uso via equivalente a `CryptographicOperations`
- A implementação do Signal Protocol usa HKDF com bytes de salt [0x01] e [0x02] para o ratchet de cadeia (compatível com o uso de HKDF em C#)
- A deduplicação de nonce usa um VecDeque por remetente com limpeza automática de entradas com mais de 5 minutos
