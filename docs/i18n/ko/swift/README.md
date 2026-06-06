# Aether Protocol - Swift 구현

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](README.md)

iOS 및 macOS를 위한 종단 간 암호화, 라우팅, 피어 투 피어 통신을 제공하는 Aether 메시 네트워킹 프로토콜의 포괄적인 Swift 구현입니다.

## 개요

Aether는 간헐적이거나 인터넷 연결이 없는 환경을 위해 설계된 분산형 메시 네트워킹 프로토콜입니다. 이 Swift 구현은 다음을 제공합니다:

- **와이어 호환 직렬화** — C# 레퍼런스 구현과 호환
- **Ed25519 서명** — 패킷 인증을 위한 서명
- **Signal Protocol** (X3DH + 대칭 래칫) — 종단 간 암호화
- **전송 추상화** — 여러 물리적 레이어 지원 (BLE, Wi-Fi Direct, NearLink)
- **스레드 안전 비동기 API** — Swift Concurrency 사용

## 요구 사항

- Swift 5.9+
- macOS 13.0+ 또는 iOS 16.0+
- Xcode 15+

## 의존성

- [swift-crypto](https://github.com/apple/swift-crypto) - 암호화 기본 요소 (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## 아키텍처

### 핵심 구성 요소

#### 프로토콜 레이어
- **MeshPacket**: 핵심 패킷 구조 (UUID, 유형, 소스/목적지 UHID, TTL, 우선순위, 페이로드, 서명)
- **PacketType**: 26가지 패킷 유형의 열거형 (RouteRequest, Data, SosBroadcast, DtnBundle 등)
- **PacketSerializer**: 리틀 엔디언 와이어 형식을 갖춘 이진 직렬화기/역직렬화기

#### 보안 레이어
- **Ed25519Service**: Curve25519를 사용한 키 생성, 서명 및 검증
- **SignalProtocolService**: 암호화된 세션을 위한 X3DH 키 합의 + 대칭 래칫
- **PacketSigningService**: 논스 중복 제거 및 재전송 방지를 갖춘 패킷 수준 서명

#### 전송 레이어
- **TransportService**: 전송 계약을 정의하는 프로토콜
- **InProcessTransport**: 테스트 및 로컬 통신을 위한 인-메모리 전송

#### 모델
- **AetherNetNode**: UHID 및 신원 키를 갖춘 노드 표현
- **PreKeyBundle**: 비동기 세션 설정을 위한 번들
- **EncryptedPayload**: 암호화된 메시지 래퍼
- **DtnBundle**: 지연 허용 네트워킹 번들
- **PeerInfo**: 라우팅 테이블 피어 정보

### 상수
모든 프로토콜 상수 (TTL, 타임아웃, 용량 한도)는 `ProtocolConstants`에 정의되어 있습니다.

## 설치

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

Package.swift에서:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherNetProtocol", package: "aether-protocol-swift")
    ]
)
```

## 빠른 시작

### 1. 패킷 직렬화

```swift
import AetherNetProtocol

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

### 2. Ed25519 서명

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Signal Protocol 세션

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

### 4. 패킷 서명

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. 인-프로세스 전송 (테스트)

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

## 와이어 형식

모든 패킷은 리틀 엔디언 와이어 형식을 따릅니다:

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

빈 UHID와 페이로드를 가진 최소 패킷 크기: **43바이트**.

## 보안 모델

### 암호화
- **알고리즘**: AES-256-GCM
- **키 유도**: X3DH 공유 비밀에서 HKDF-SHA256
- **세션 래칫**: 대칭 래칫이 메시지당 체인 키를 전진

### 서명
- **알고리즘**: Ed25519 (Curve25519)
- **페이로드 보호**: SHA256 해시가 서명 가능 데이터에 포함
- **재전송 방지**: 8바이트 논스 + 밀리초 타임스탬프 + 중복 제거 캐시

### 키 교환
- **프로토콜**: ECDH P-256을 사용한 X3DH 변형
- **사전 키 바인딩**: Ed25519로 서명된 사전 키 검증
- **비동기**: 수신자 온라인 없이 세션 설정

### 한도
- **MaxSkippedKeys**: 1,000 (세션당 순서 없는 메시지)
- **MaxPacketAge**: 300초 (5분)

## 프로토콜 상수

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5,000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12바이트
- **AesGcmTagSize**: 16바이트

전체 목록은 `ProtocolConstants` 참조.

## 스레드 안전성

모든 서비스는 스레드 안전한 동시 접근을 위해 `actor`로 격리되어 있습니다:

- `SignalProtocolService` - 세션 관리 및 암호화
- `PacketSigningService` - 패킷 서명 및 검증
- `InProcessTransport` - 메시지 전달

Swift Concurrency와의 사용:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## 테스트

포함된 데모 실행:

```bash
cd swift
swift run aether-demo
```

예상 출력:

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

## 상호 운용성

와이어 형식은 다음과 호환됩니다:
- **AetherNet.Core** (C#) - 레퍼런스 구현
- **aether-protocol-go** - Go 구현
- **aether-protocol-rust** - Rust 구현

모든 구현은 다음을 사용합니다:
- 리틀 엔디언 정수
- UTF-8 문자열 인코딩
- Ed25519 서명 (64바이트)
- AES-256-GCM 암호화 (12바이트 논스, 16바이트 태그)

## 성능

Apple Silicon (M1 Pro) 벤치마크:

| 연산 | 시간 |
|-----------|------|
| 패킷 직렬화 | ~0.5 μs |
| 패킷 역직렬화 | ~0.7 μs |
| Ed25519 서명 | ~3.5 ms |
| Ed25519 검증 | ~4.2 ms |
| AES-256-GCM 암호화 | ~0.8 μs |
| AES-256-GCM 복호화 | ~0.9 μs |
| X3DH 키 합의 | ~8.5 ms |
| 대칭 래칫 | ~0.3 μs |

## 향후 작업

- **BLE 전송**: Bluetooth Low Energy 구현
- **Wi-Fi Direct 전송**: 직접 피어 투 피어 Wi-Fi
- **Double Ratchet**: 메시지 래칫을 갖춘 완전한 전방 비밀성
- **AODV 라우팅**: 경로 발견 및 유지
- **DTN 서비스**: 저장 후 전달 번들 전송
- **현재 상태 및 근접성**: 위치 인식 피어 발견
- **음성 및 스트리밍**: 실시간 미디어 프로토콜

## 라이선스

MIT - 자세한 내용은 LICENSE 파일 참조

## 참조

1. [Aether 프로토콜 사양](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Double Ratchet Algorithm](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Ed25519 Signatures](https://en.wikipedia.org/wiki/Curve25519)
6. [AES-GCM Mode](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## 기여

이것은 레퍼런스 구현입니다. 버그 리포트 및 기능 요청은 GitHub에 이슈를 열어주세요.
