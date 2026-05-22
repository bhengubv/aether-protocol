# Aether 프로토콜 - Kotlin 구현

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](README.md)

C# 참조 구현과의 완전한 크로스 언어 와이어 포맷 호환성을 갖춘, 프로덕션 수준의 완전한 Aether 메시 네트워킹 프로토콜 Kotlin 구현입니다.

## 개요

Aether는 간헐적이거나 인터넷 연결이 없는 환경을 위한 탈중앙화 메시 네트워킹 프로토콜입니다. 이 Kotlin 구현은 다음을 제공합니다:

- **와이어 포맷 호환성** — C#과 호환 (바이너리 패킷 직렬화가 정확히 일치)
- **Ed25519 서명** — 패킷 인증 및 무결성
- **Signal 프로토콜** — 종단 간 암호화 (X3DH 키 합의, 대칭 래칫, AES-256-GCM)
- **ECDH P-256** — 세션 확립을 위한 키 합의
- **패킷 직렬화/역직렬화** — 리틀 엔디언 다중 바이트 정수 사용
- **재전송 방지** — 논스 중복 제거 사용
- **전송 추상화** — BLE, Wi-Fi Direct 및 인프로세스 메시징용

## 프로젝트 구조

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherNode, PeerInfo, DtnBundle, etc.)
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

## 빌드

### 사전 요구 사항

- JDK 17 이상
- Gradle 8.0 이상

### 컴파일

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### 데모 실행

```bash
./gradlew run
```

데모에서 보여주는 내용:
1. Ed25519 키 쌍 생성
2. 사전 키 번들 생성 및 교환
3. Signal 프로토콜 세션 확립
4. Ed25519를 사용한 패킷 서명
5. 패킷 직렬화/역직렬화
6. 메시지 암호화 및 복호화
7. 재전송 방지
8. 인프로세스 전송 메시징

## 주요 컴포넌트

### 1. 패킷 직렬화 (`PacketSerializer`)

와이어 포맷 (리틀 엔디언):
- 프로토콜 버전 (1 바이트)
- 패킷 유형 (1 바이트)
- 패킷 ID / UUID (16 바이트)
- 우선순위 (1 바이트)
- TTL (4 바이트, int32)
- TimestampMs (8 바이트, int64)
- SourceUhid (2바이트 길이 접두사 + UTF-8 바이트)
- DestinationUhid (2바이트 길이 접두사 + UTF-8 바이트)
- PacketNonce (2바이트 길이 접두사 + 바이트)
- 페이로드 (4바이트 길이 접두사 + 바이트)
- 서명 (2바이트 길이 접두사 + 바이트)

C# `PacketSerializer`와 완전히 호환됩니다.

### 2. Ed25519 서명 (`Ed25519Service`, `PacketSigning`)

- **키 생성**: 32바이트 개인키 시드, 32바이트 공개키
- **서명**: 결정론적 서명 가능 데이터에 대한 64바이트 서명
- **검증**: 마이그레이션 기간 동안 P-256 ECDSA 대체
- **서명 가능 데이터 포맷**: C# 명세와 정확히 일치 (패킷 논스, 타임스탬프, 유형, UHID, 페이로드 해시, TTL, 우선순위)
- **재전송 방지**: 5분 TTL 논스 중복 제거

### 3. Signal 프로토콜 (`SignalProtocol`)

대칭 래칫을 갖춘 X3DH 키 합의 구현:

**세션 확립:**
- 피어의 사전 키 번들 가져오기
- Ed25519로 번들 서명 검증
- X3DH 수행: DH(로컬 신원, 원격 서명된 사전 키) + DH(로컬 신원, 원격 사전 키)
- HKDF-SHA256을 사용하여 루트 키 및 체인 키 유도

**암호화/복호화:**
- HMAC-SHA256을 사용한 대칭 래칫
- 12바이트 임의 논스를 사용하는 AES-256-GCM
- 순방향 비밀성을 갖춘 메시지별 키
- 순서 어긋난 메시지 처리 (건너뛴 키 캐시, 최대 1000개 키)

**파라미터:**
- 루트 키 유도 info: `"aether-root-v1"`
- 송신 체인 유도 info: `"aether-chain-send-v1"`
- 수신 체인 유도 info: `"aether-chain-recv-v1"`
- 메시지 키 솔트: `0x01`, 체인 키 솔트: `0x02`

### 4. 전송 추상화 (`TransportService`)

물리적 전송을 위한 인터페이스 (BLE, Wi-Fi Direct 등):

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

**InProcessTransport:** 테스트/데모용 전역 `ConcurrentHashMap`을 사용하는 참조 구현.

### 5. 도메인 모델 (`Models.kt`)

- **AetherNode**: UHID, 공개키, 기능, 지오해시를 가진 노드 신원
- **PeerInfo**: 신뢰도 점수 및 마지막 확인 타임스탬프를 가진 알려진 피어
- **RouteEntry**: 홉 수 및 품질 점수를 가진 라우팅 테이블 항목
- **NodeCapabilities**: 비트 필드 (BLE, Wi-Fi Direct, 게이트웨이, 릴레이, SOS, 스트리밍, 음성, DTN)
- **DtnBundle**: 만료 및 복사 카운트를 가진 저장 및 전달 번들

## 프로토콜 상수

주요 상수 (`Constants.kt`):

| 카테고리 | 상수 | 값 |
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

## 패킷 유형

23개의 모든 패킷 유형이 C# 열거형 값 (1-23)과 일치합니다:

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

## 의존성

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — 키 포맷 지원
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — 비동기/대기, Flow
- **org.slf4j:slf4j-api:2.0.9** — 로깅
- **kotlin-stdlib** — Kotlin 표준 라이브러리

## 사용 예시

### 키 생성

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### 패킷 서명

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

### 패킷 직렬화

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Signal 프로토콜 암호화

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

## 크로스 언어 호환성

이 구현은 C# 참조 구현과 **정확한 와이어 포맷 호환성**을 유지합니다:

- 바이너리 패킷 포맷: 동일한 리틀 엔디언 레이아웃
- 패킷 유형 열거형: 값이 C# 열거형과 정확히 일치 (1-23)
- Ed25519 서명: NSec/libsodium과 호환
- ECDH P-256: 표준 곡선, 크로스 언어 호환
- HKDF-SHA256: RFC 5869 표준 구현
- AES-256-GCM: 12바이트 논스, 16바이트 태그를 사용하는 NIST 표준

Kotlin에서 직렬화된 패킷은 C#에서 역직렬화할 수 있으며, 그 반대도 가능합니다.

## 테스트

구현에는 다음을 실행하는 포괄적인 데모 (`Demo.kt`)가 포함되어 있습니다:

1. 키 생성 및 공개키 내보내기
2. 사전 키 번들 생성 및 교환
3. Signal 프로토콜을 통한 세션 확립
4. 패킷 생성, 서명 및 직렬화
5. 패킷 역직렬화 및 서명 검증
6. 메시지 암호화 및 복호화
7. 재전송 공격 방지
8. 인프로세스 전송 메시징

다음으로 실행:
```bash
./gradlew run
```

## 보안 고려 사항

- **키 초기화**: 모든 중간 암호화 자료는 `CryptographicOperations.ZeroMemory` 사용 후 초기화됨 (Kotlin 동등: `fill(0)`)
- **재전송 방지**: 5분 TTL을 가진 논스 중복 제거로 재전송 공격 방지
- **순방향 비밀성**: 체인 래칫에서 유도된 메시지별 키
- **순서 어긋난 처리**: 메모리 고갈을 방지하기 위한 최대 1000개 키의 건너뛴 키 캐시
- **RREP 인증**: 목적지 노드에 의해 서명된 경로 응답 패킷
- **패킷 기밀성**: AES-256-GCM으로 암호화된 메시지 내용

## 향후 확장

구현은 다음을 위한 훅을 제공합니다:

- **BLE 전송** (`TransportService` 인터페이스)
- **Wi-Fi Direct 전송** (동일한 인터페이스)
- **DTN 에피데믹 라우팅** (`DtnBundle` 모델 준비됨)
- **SOS 브로드캐스트** (패킷 유형 정의됨)
- **프레즌스 비콘** (패킷 유형 정의됨)
- **음성 및 스트리밍** (패킷 유형 정의됨)
- **Double Ratchet** (항상 켜진 전송이 가능할 때)

## 프로토콜 문서

전체 프로토콜 명세: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## 라이선스

SPDX-License-Identifier: MIT
