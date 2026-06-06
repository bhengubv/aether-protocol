# Aether Protocol — Rust 구현

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](README.md)

Aether 메시 네트워킹 프로토콜의 완전한 Rust 구현으로, C# 레퍼런스 구현과 와이어 형식 호환성을 제공합니다.

## 개요

이 크레이트는 다음을 제공합니다:

- **MeshPacket 직렬화/역직렬화** — C# PacketSerializer와 정확히 일치하는 이진 와이어 형식
- **Ed25519 서명** — 신원 키 생성, 서명 및 검증
- **Signal Protocol** — 전방 비밀성을 위한 대칭 래칫을 갖춘 X3DH 기반 키 합의
- **패킷 서명 서비스** — 논스 중복 제거 및 신선도 검사
- **인-프로세스 전송** — 테스트 및 데모를 위한 시뮬레이션 메시 네트워크

## 프로젝트 구조

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

## 주요 기능

### 1. 와이어 형식 호환성

`PacketSerializer`는 C# 구현과 바이트 단위로 동일한 출력을 생성합니다:

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

모든 멀티바이트 정수는 리틀 엔디언 바이트 순서를 사용합니다. 문자열 길이는 프로토콜 사양에 명시된 대로 u16(SourceUhid, DestinationUhid) 또는 i32(Payload, Signature)로 접두사가 붙습니다.

### 2. 패킷 유형

프로토콜 사양의 26가지 패킷 유형이 모두 정의되어 있습니다:

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

### 3. Ed25519 서명

- 32바이트 개인 키(시드), 32바이트 공개 키, 64바이트 서명
- 암호화 연산에 `ed25519-dalek` 사용
- 사용 후 안전한 키 제로화

### 4. Signal Protocol

대칭 래칫을 갖춘 X3DH 기반 키 합의:

- **키 합의:** 임시 + 서명된 사전 키를 사용한 ECDH P-256
- **키 유도:** 고유 정보 문자열을 사용한 HKDF-SHA256
  - `aether-root-v1` — 루트 키
  - `aether-chain-send-v1` — 송신 체인 키
  - `aether-chain-recv-v1` — 수신 체인 키
- **암호화:** AES-256-GCM (12바이트 논스, 16바이트 태그)
- **래칫:** 카운터 기반 메시지 키를 사용한 대칭 체인 키 전진
- **순서 없는 처리:** 최대 1,000개의 건너뛴 메시지 키 캐시

### 5. 패킷 서명 서비스

- 임의 8바이트 논스 생성
- 밀리초 정밀도 타임스탬프
- 신선도 검증 (5분 창)
- 발신자별 논스 중복 제거 (재전송 방지)
- 만료된 항목 자동 정리

### 6. 인-프로세스 전송

테스트를 위한 시뮬레이션 메시 네트워크:

- 동시 HashMap을 사용한 노드의 정적 레지스트리
- 발사 후 망각(fire-and-forget) 메시지 전달
- 양방향 피어 연결 확인
- 데모 및 단위 테스트에 적합

## 사용법

### 기본 키 생성 및 서명

```rust
use aethermesh_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Signal Protocol 세션

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

### 패킷 직렬화

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

### 패킷 서명

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

### 인-프로세스 전송

```rust
use aethermesh_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## 데모 실행

```bash
cargo run --release
```

데모는 다음 단계를 수행합니다:

1. Alice와 Bob의 신원 키 생성
2. Signal Protocol 서비스 초기화
3. 사전 키 번들 생성 및 교환
4. 암호화된 세션 설정
5. 암호화된 메시지 교환
6. 메시 패킷 생성 및 서명
7. 패킷 서명 검증
8. 패킷 직렬화 및 역직렬화
9. 인-프로세스 전송 시연

## 상수

모든 프로토콜 상수는 `src/constants.rs`에 정의되어 있으며 C# 사양과 일치합니다:

- 라우팅: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- 보안: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- 전송: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- 음성/스트림: 다양한 비트레이트 및 버퍼 구성

## 의존성

- `ed25519-dalek` — Ed25519 서명
- `x25519-dalek` — X25519 키 합의
- `aes-gcm` — AES-256-GCM 암호화
- `hkdf` — HKDF 키 유도
- `sha2` — SHA-256 해싱
- `hmac` — HMAC 연산
- `rand` — 난수 생성
- `uuid` — GUID 생성 및 직렬화
- `serde` + `serde_json` — 직렬화
- `tokio` — 비동기 런타임
- `async-trait` — 비동기 트레이트 메서드

## 테스트

모든 테스트 실행:

```bash
cargo test
```

테스트 항목:

- 패킷 생성 및 TTL 관리
- 패킷 유형 변환
- 직렬화/역직렬화 왕복
- Ed25519 키 생성 및 서명 검증
- Signal Protocol 세션 설정 및 암호화
- 패킷 서명 및 신선도 검증
- 인-프로세스 전송 연결

## 프로토콜 준수

이 구현은 Aether 프로토콜 사양 (버전 2.0)을 따르며 다음을 지원합니다:

- ✅ 이진 와이어 형식 (리틀 엔디언, 길이 접두사)
- ✅ 26가지 패킷 유형 모두
- ✅ 논스 중복 제거를 갖춘 Ed25519 서명
- ✅ HKDF-SHA256을 사용한 X3DH 키 합의
- ✅ 12바이트 논스를 사용한 AES-256-GCM 암호화
- ✅ 순서 없는 처리를 갖춘 대칭 래칫
- ✅ 사전 키 번들 생성 및 처리
- ✅ 패킷 서명 가능 데이터 구성 (SHA-256 페이로드 해시)
- ✅ 전송 트레이트 추상화

## 참고 사항

- 와이어 형식은 전체에 걸쳐 리틀 엔디언 바이트 순서를 사용합니다 (C# BinaryPrimitives.WriteInt32LittleEndian과 일치)
- 문자열 길이 접두사는 UHID에 u16을, 페이로드/서명에 i32를 사용합니다 (C# WriteUInt16/WriteInt32와 일치)
- 모든 암호화 키 자료는 `CryptographicOperations` 동등물을 통해 사용 후 제로화됩니다
- Signal Protocol 구현은 체인 래칫에 솔트 바이트 [0x01] 및 [0x02]와 함께 HKDF를 사용합니다 (C# HKDF 사용과 일치)
- 논스 중복 제거는 발신자별 VecDeque를 사용하며 5분보다 오래된 항목을 자동으로 정리합니다
