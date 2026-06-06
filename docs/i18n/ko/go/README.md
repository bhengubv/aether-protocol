# Aether 프로토콜 - Go 구현

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](../../ar/go/README.md) · [中文简体](../../zh-CN/go/README.md) · [日本語](../../ja/go/README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](../../ru/go/README.md) · [فارسی](../../fa/go/README.md) · [한국어](README.md)

C# 참조 구현과 와이어 호환성을 갖춘 Aether 메시 네트워킹 프로토콜의 완전한 Go 구현입니다.

## 개요

이 모듈은 간헐적이거나 인터넷 연결이 없는 환경을 위한 Aether 탈중앙화 메시 네트워킹 프로토콜을 구현합니다. 다음을 제공합니다:

- **패킷 직렬화**: C# 참조 구현과 호환되는 바이너리 와이어 포맷 (리틀 엔디언 인코딩)
- **Ed25519 서명**: 암호화 패킷 인증
- **Signal 프로토콜**: 종단 간 암호화를 위한 X3DH 키 합의 + 대칭 래칫
- **패킷 서명 서비스**: 재전송 방지를 위한 5분 TTL 논스 중복 제거
- **인프로세스 전송**: 테스트 및 프로세스 간 통신을 위한 메모리 기반 전송
- **모델**: AetherNetNode, PeerInfo, RouteEntry, DtnBundle, SosAlert 구조체
- **프로토콜 상수**: 모든 라우팅, 탐색, 보안 및 전송 상수

## 모듈 구조

```
aether-protocol/go/
├── go.mod                          # Module definition
├── go.sum                           # Dependency checksums
├── README.md                        # This file
│
├── protocol/
│   ├── packet.go                   # MeshPacket struct, PacketType constants
│   └── serializer.go               # Binary serialization (little-endian)
│
├── security/
│   ├── ed25519.go                  # Ed25519 signing/verification
│   ├── signal_protocol.go          # Signal Protocol (X3DH + ratchet)
│   ├── packet_signing.go           # Nonce deduplication service
│   └── models.go                   # PreKeyBundle, EncryptedPayload, SignalSession
│
├── transport/
│   ├── transport.go                # TransportService interface
│   └── in_process.go               # In-memory transport implementation
│
├── models/
│   └── models.go                   # Domain models (Node, Route, DtnBundle, etc.)
│
├── constants/
│   └── constants.go                # Protocol constants
│
└── cmd/demo/
    └── main.go                      # Comprehensive demo program
```

## 주요 기능

### 1. 패킷 직렬화 (리틀 엔디언)

모든 다중 바이트 정수에 리틀 엔디언 인코딩을 사용하여 C#과 정확히 일치하는 와이어 포맷:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (UUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (uint16, LE)
[N bytes] SourceUhid (UTF-8)
... (destination, nonce, payload, signature)
```

**예시:**
```go
serializer := &protocol.PacketSerializer{}
packet := protocol.NewMeshPacket()
packet.Type = protocol.Data
packet.SourceUhid = "node-alice"
packet.DestinationUhid = "node-bob"
packet.Payload = []byte("Hello!")

data, err := serializer.Serialize(packet)      // Binary format
recovered, err := serializer.Deserialize(data) // Round-trip
```

### 2. Ed25519 서명 및 검증

- **키 포맷**: 32바이트 시드 (개인키), 32바이트 공개키, 64바이트 서명
- **표준 라이브러리**: 외부 의존성 없이 `crypto/ed25519` 사용

**예시:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. Signal 프로토콜 (X3DH + 대칭 래칫)

종단 간 암호화를 위한 Signal 프로토콜 구현:

- **키 합의**: `crypto/ecdh`를 사용한 ECDH P-256
- **키 유도**: `golang.org/x/crypto/hkdf`를 사용한 HKDF-SHA256
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **암호화**: 12바이트 논스, 16바이트 태그를 사용하는 AES-256-GCM
- **래칫**: HMAC-SHA256 체인 진행
- **순서 무관 처리**: 건너뛴 메시지 키 (최대 1000개)

**예시:**
```go
aliceService, _ := security.NewSignalProtocolService()
bobService, _ := security.NewSignalProtocolService()

// Alice generates pre-key bundle
aliceBundle, _ := aliceService.GeneratePreKeyBundle("alice")

// Bob establishes session with Alice
bobService.ProcessPreKeyBundle(aliceBundle)

// Alice establishes session with Bob
bobBundle, _ := bobService.GeneratePreKeyBundle("bob")
aliceService.ProcessPreKeyBundle(bobBundle)

// End-to-end encrypted messaging
plaintext := []byte("Secret message")
encrypted, _ := aliceService.Encrypt("bob", plaintext)
decrypted, _ := bobService.Decrypt("alice", encrypted)
```

### 4. 패킷 서명 및 논스 중복 제거

논스 캐시에 5분 TTL을 적용하여 재전송 공격 방지:

```go
signer := security.NewPacketSigningService(300) // 300 seconds TTL
defer signer.Close()

// Compute signable data (SHA256 of payload + header fields)
signableData := signer.ComputeSignableData(
    nonce, timestamp, packetType, sourceUhid, destUhid, payload, ttl, priority)

// Track nonces for deduplication
signer.RecordNonce(sourceUhid, nonce)
isDuplicate := signer.IsNonceSeen(sourceUhid, nonce)
```

### 5. 인프로세스 전송

테스트 및 로컬 노드 통신을 위한 메모리 기반 전송:

```go
inProcTransport := transport.NewInProcessTransport()

// Register peers
aliceRx, _ := inProcTransport.RegisterPeer("alice", 10) // buffered channel
bobRx, _ := inProcTransport.RegisterPeer("bob", 10)

// Send and receive
ctx := context.Background()
inProcTransport.SendAsync(ctx, "bob", []byte("Hello!"))
message := <-bobRx

// Properties
fmt.Println(inProcTransport.Name())                // "InProcess"
fmt.Println(inProcTransport.IsAvailable())         // true
fmt.Println(inProcTransport.MaxBandwidthBps())     // 1000000
fmt.Println(inProcTransport.IsConnected("bob"))    // true
```

### 6. 도메인 모델

메시 네트워킹을 위한 완전한 구조체:

```go
// Node in the mesh
node := &models.AetherNetNode{
    UHID: "node-alice-001",
    IdentityKey: publicKey,
    Capabilities: models.CapabilityBLE | models.CapabilityRelay,
    IsLocal: true,
}

// Route to destination
route := &models.RouteEntry{
    DestinationUhid: "node-bob",
    NextHop: "node-bob",
    HopCount: 1,
    ExpiresAt: time.Now().Add(5 * time.Minute),
    QualityScore: 85,
}

// DTN bundle for store-and-forward
bundle := &models.DtnBundle{
    ID: uuid.New().String(),
    SenderUhid: "alice",
    RecipientUhid: "bob",
    Priority: models.DtnPriorityHigh,
    Status: models.DtnStatusPending,
}

// Emergency alert
alert := &models.SosAlert{
    SenderUhid: "alice",
    Message: "Emergency! Need help!",
    Latitude: -33.9249,
    Longitude: 18.4241,
}
```

## 프로토콜 상수

프로토콜 명세 (부록 A)의 모든 상수:

```go
// Routing
DefaultTtl = 7
SosTtl = 15
RouteTimeoutMs = 5000

// BLE Discovery
BleScanOnMs = 2000
BleScanOffMs = 8000
BleUuidRotationSeconds = 900

// Security
MaxPacketAgeSeconds = 300
MaxSkippedKeys = 1000
AesGcmNonceSize = 12
AesGcmTagSize = 16

// DTN
DtnBundleTtlHours = 72
DtnMaxCopies = 3
DtnMaxBundlesPerNode = 50

// Voice, Streaming, Presence constants...
```

## 데모 실행

데모 프로그램은 모든 주요 기능을 보여줍니다:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**데모 출력:**
```
========================================
Aether Protocol - Go Implementation Demo
========================================

[ DEMO 1: Packet Serialization ]
  Original Packet: [Data] ... src=node-alice-001 dst=node-bob-001
  Payload: Hello, Aether!
  Serialized size: 95 bytes
  Deserialized Packet: [Data] ...
  Payload: Hello, Aether!
  ✓ Round-trip serialization successful!

[ DEMO 2: Ed25519 Signing ]
  Generated Ed25519 Key Pair:
    Private Key (seed): 32 bytes
    Public Key: 32 bytes
  Signed message: Important mesh packet signature
  Signature: 64 bytes
  Signature verification: true
  Verification with tampered data: false (should be false)
  ✓ Ed25519 signing verification successful!

[ DEMO 3: Signal Protocol - Session Establishment ]
  Creating Signal Protocol services for Alice and Bob...
  ✓ Alice generated pre-key bundle
  ✓ Bob established session with Alice
  ✓ Bob generated pre-key bundle
  ✓ Alice established session with Bob
  ✓ Alice encrypted message: Hello Bob, this is Alice!
    Ciphertext: 41 bytes
  ✓ Bob decrypted message: Hello Bob, this is Alice!
  ✓ Bob encrypted message: Hi Alice, I received your message!
  ✓ Alice decrypted message: Hi Alice, I received your message!
  ✓ Signal Protocol end-to-end encryption successful!

[ DEMO 4: In-Process Transport ]
  Transport: InProcess
  Available: true
  Max Bandwidth: 1000000 bps
  Max Range: 100 meters
  ✓ Registered peer: alice
  ✓ Registered peer: bob
  ✓ Alice sent: Hello Bob! (success: true)
  ✓ Bob received: Hello Bob!
  ✓ Bob sent: Hi Alice! (success: true)
  ✓ Alice received: Hi Alice!
  Alice connected to bob: true
  Bob connected to alice: true
  ✓ In-process transport successful!

[ DEMO 5: Packet Signing & Nonce Deduplication ]
  Computed signable data: 152 bytes
  ✓ Recorded nonce for replay prevention
  Nonce seen (should be true): true
  Different nonce seen (should be false): false
  ✓ Nonce deduplication working correctly!

========================================
All demos completed successfully!
========================================
```

## 와이어 포맷 호환성

모든 직렬화는 C# 참조 구현과 일치하도록 **리틀 엔디언 인코딩**을 사용합니다:

- **정수**: `encoding/binary.LittleEndian`
- **UUID**: 표준 16바이트 UUID 포맷
- **문자열**: 2바이트 (uint16) 또는 4바이트 (uint32) 길이 접두사와 함께 UTF-8 인코딩
- **바이트**: 길이 접두사 (2바이트 또는 4바이트) 뒤에 원시 데이터

이를 통해 Go와 C# 구현 간에 패킷을 교환할 때 바이트 단위의 호환성을 보장합니다.

## 의존성

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

모든 암호화 기본 요소는 HKDF 및 ECDH P-256을 위한 `golang.org/x/crypto`와 함께 Go 표준 라이브러리 (`crypto/*`)를 사용합니다.

## 보안 기능

1. **키 초기화**: 모든 중간 키는 `ZeroMemory()`로 안전하게 초기화됨
2. **폴백 암호화 없음**: 메시지는 확립된 세션 필요; UHID 유도 폴백 없음
3. **재전송 방지**: 8바이트 논스 + 타임스탬프 + 5분 중복 제거 캐시
4. **카운터 갭**: 순서 어긋난 메시지는 MaxSkippedKeys (1000)까지 지원
5. **서명 검증**: 모든 경로 응답 및 사전 키 번들은 Ed25519로 검증됨

## 성능 참고 사항

- **패킷 직렬화**: 패킷당 ~1-2µs (100바이트 페이로드 기준 테스트)
- **Ed25519 서명**: 서명당 ~50µs
- **Signal 프로토콜 암호화**: 메시지당 ~100µs
- **논스 중복 제거 정리**: 백그라운드 고루틴이 60초마다 실행

## 테스트

데모 프로그램에서 다음을 시연합니다:
- ✓ 패킷 왕복 직렬화
- ✓ Ed25519 서명 검증
- ✓ Signal 프로토콜 세션 확립
- ✓ 종단 간 암호화/복호화
- ✓ 인프로세스 전송 통신
- ✓ 논스 중복 제거

모든 작업은 적절한 경우 `sync.RWMutex` 및 `sync.Map`을 사용하여 고루틴 안전성을 보장합니다.

## 구현 참고 사항

1. **UUID 포맷**: RFC 4122 준수를 위해 `github.com/google/uuid` 사용
2. **키 관리**: 외부 키 저장소 없음; 데모용으로 메모리에 키 보관. 프로덕션에서는 보안 저장소 사용 권장
3. **전송 인터페이스**: BLE, Wi-Fi Direct 및 기타 물리 계층으로 확장 가능
4. **Signal 세션**: 이 구현에서는 데이터베이스 백업 없이 피어별로 유지
5. **오류 처리**: 모든 암호화 작업은 오류를 반환하며, 호출자가 실패를 처리해야 함

## 향후 개선 사항

- [ ] 경로 및 세션을 위한 SQLite 영속성
- [ ] BLE 전송 구현
- [ ] Wi-Fi Direct 전송 구현
- [ ] AODV 라우팅 프로토콜 구현
- [ ] DTN 에피데믹 라우팅
- [ ] 프레즌스 및 탐색 비콘 서비스
- [ ] 음성 및 스트리밍 지원
- [ ] 더 높은 수준의 순방향 비밀성을 위한 Double Ratchet 알고리즘

## 라이선스

SPDX-License-Identifier: MIT
