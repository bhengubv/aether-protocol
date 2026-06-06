# Aether Mesh Protocol - TypeScript 구현

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](README.md)

C# 레퍼런스 구현과 완전한 와이어 형식 호환성을 갖춘 Aether 메시 네트워킹 프로토콜의 완전한 TypeScript/Node.js 구현입니다.

## 기능

- **MeshPacket 직렬화**: C#과 정확히 일치하는 이진 와이어 형식 (리틀 엔디언 정수, 길이 접두사 문자열/배열)
- **Ed25519 서명**: 서명 생성 및 검증에 TweetNaCl 사용
- **Signal Protocol**: HKDF-SHA256 키 유도 및 AES-256-GCM 암호화를 갖춘 X3DH 키 교환
- **패킷 서명**: 프로토콜 사양 (섹션 2.3)에 따른 완전한 서명 가능 데이터 구성
- **인-프로세스 전송**: 테스트 및 데모를 위한 시뮬레이션 네트워크
- **대칭 래칫**: 순서 없는 메시지 지원을 갖춘 HMAC-SHA256 체인 키 전진
- **프로토콜 상수**: PROTOCOL_SPEC 섹션 A의 60개 이상 상수 모두

## 설치

```bash
npm install
```

## 사용법

### 빌드

```bash
npm run build
```

### 데모 실행

```bash
npm run dev
```

데모 동작:
1. 인-프로세스 시뮬레이션 네트워크에 2개 노드 생성
2. Ed25519 키 쌍 생성
3. Signal 프로토콜 세션 설정
4. 패킷 생성, 서명 및 검증
5. 패킷 직렬화 및 역직렬화
6. 메시지 암호화 및 복호화
7. 전송 레이어를 통한 패킷 전송

### API 예제

#### 패킷 생성 및 서명

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Signal Protocol 암호화

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### 패킷 직렬화

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### 인-프로세스 전송

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## 프로토콜 준수

### 와이어 형식

모든 멀티바이트 정수는 **리틀 엔디언**:
- 패킷 ID: 16바이트 UUID
- TTL, TimestampMs: int32/int64 LE
- 문자열 길이: uint16 LE (uint32 아님)
- 페이로드 길이: int32 LE

### 패킷 서명 (섹션 2.3)

서명 가능 데이터 형식:
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Signal Protocol (섹션 4)

- **키 교환**: ECDH P-256을 사용한 X3DH
- **HKDF**: salt="AetherMeshSignal"을 사용한 SHA256
- **정보 문자열**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **암호화**: 12바이트 논스, 16바이트 태그를 사용한 AES-256-GCM
- **체인 래칫**: 카운터 전진을 갖춘 HMAC-SHA256

## 패킷 유형

23가지 패킷 유형 모두 정의됨:
- RouteRequest (1) - AODV 경로 요청
- RouteReply (2) - AODV 경로 응답
- Data (3) - 애플리케이션 데이터
- Ack (4) - 전달 확인
- SosBroadcast (5) - 긴급 방송
- ... 그리고 18가지 더 (프로토콜 사양 참조)

## 보안 기능

- **Ed25519 서명**: v2 프로토콜에 따라 모든 패킷에 서명
- **AES-256-GCM**: 고유 논스를 사용한 메시지별 키
- **재전송 방지**: 8바이트 임의 논스 + 타임스탬프 검증
- **전방 비밀성**: 대칭 래칫이 체인 키를 전진
- **순서 없는 복호화**: 건너뛴 메시지 키 캐싱 (최대 1000개)

## 프로젝트 구조

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## 테스트

데모 (`npm run dev`)는 모든 주요 기능을 실행합니다:
- 패킷 생성 및 직렬화 (왕복)
- Ed25519 키 생성 및 서명 검증
- Signal 프로토콜 세션 설정
- 메시지 암호화 및 복호화
- 인-프로세스 전송 전달

단위 테스트는 Jest 또는 유사한 테스트 러너를 사용하여 확장하세요.

## 호환성 참고 사항

- **C# 와이어 형식**: C# PacketSerializer와 100% 호환
- **서명된 패킷**: Ed25519 서명을 갖춘 프로토콜 버전 2
- **HKDF 유도**: @noble/hashes 사용 (순수 JavaScript 구현)
- **ECDH**: Node.js 내장 crypto 모듈 (P-256 곡선)

## 의존성

- **tweetnacl**: TweetNaCl을 통한 Ed25519 서명
- **@noble/hashes**: HKDF-SHA256 키 유도
- **uuid**: UUID 생성 및 파싱
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## 라이선스

MIT - 자세한 내용은 LICENSE 파일 참조

## 참조

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# Implementation](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
