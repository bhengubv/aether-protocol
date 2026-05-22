# Aether Mesh Networking Protocol - Python 구현

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](README.md)

Aether 메시 네트워킹 프로토콜의 Python 구현으로, C# 레퍼런스 구현과 와이어 호환 암호화 연산을 제공합니다.

## 개요

Aether는 간헐적이거나 인터넷 연결이 없는 환경을 위해 설계된 분산형 메시 네트워킹 프로토콜입니다. 이 Python 패키지는 다음을 제공합니다:

- **Ed25519 서명**: PyNaCl을 사용한 키 생성, 서명 및 검증
- **Signal Protocol X3DH**: ECDH P-256을 사용한 비동기 키 교환
- **AES-256-GCM 암호화**: 12바이트 논스를 사용한 메시지별 대칭 암호화
- **HKDF-SHA256 키 유도**: 컨텍스트별 정보 문자열을 사용한 RFC 5869 규격 키 유도
- **대칭 래칫**: 전방 비밀성을 갖춘 HMAC-SHA256 기반 메시지 키 유도
- **패킷 직렬화**: C# 구현과 일치하는 리틀 엔디언 이진 와이어 형식
- **재전송 공격 방지**: 5분 TTL을 갖춘 논스 기반 중복 제거
- **인-프로세스 전송**: 메시 통신 테스트를 위한 목(mock) 전송

## 설치

### PyPI에서 (배포 후)
```bash
pip install aether-protocol
```

### 소스에서
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### 개발 의존성
```bash
pip install -e ".[dev]"
```

## 빠른 시작

```python
import asyncio
from aether.security.ed25519_service import Ed25519SigningService
from aether.security.signal_protocol import SignalProtocolService
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## 아키텍처

### 패키지 구조

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## 주요 기능

### 1. Ed25519 서명 서비스

암호화 연산에 PyNaCl(libsodium)을 사용합니다:

```python
from aether.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**키 크기:**
- 개인 키: 32바이트 (Ed25519 시드)
- 공개 키: 32바이트 (Ed25519 포인트)
- 서명: 64바이트

### 2. Signal Protocol

전방 비밀성을 위한 대칭 래칫을 갖춘 X3DH 키 교환을 구현합니다:

```python
from aether.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**키 유도:**
- 솔트 `"AetherSignal"`을 사용한 HKDF-SHA256
- 루트 키 정보: `"aether-root-v1"`
- 송신 체인 정보: `"aether-chain-send-v1"`
- 수신 체인 정보: `"aether-chain-recv-v1"`

**대칭 래칫:**
- 체인 키와 함께 HMAC-SHA256 사용
- 각 메시지마다 새로운 메시지 키를 유도하고 체인을 전진
- 순서 없는 전달을 위해 최대 1000개의 건너뛴 키 지원
- 임의의 12바이트 논스를 사용한 메시지별 암호화: AES-256-GCM

### 3. 패킷 직렬화

C# 구현과 일치하는 와이어 호환 이진 형식:

```python
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**와이어 형식 (리틀 엔디언):**
- 프로토콜 버전: 1바이트
- 패킷 유형: 1바이트
- 패킷 ID: 16바이트 (UUID)
- 우선순위: 1바이트
- TTL: 4바이트 (int32)
- TimestampMs: 8바이트 (int64)
- SourceUhid 길이: 2바이트 + UTF-8 데이터
- DestinationUhid 길이: 2바이트 + UTF-8 데이터
- PacketNonce 길이: 2바이트 + 데이터
- 페이로드 길이: 4바이트 + 데이터
- 서명 길이: 2바이트 + 데이터

### 4. 패킷 서명

Ed25519를 사용하여 패킷에 서명하고 재전송 공격을 탐지합니다:

```python
from aether.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**서명 가능한 데이터:**
프로토콜 사양 섹션 2.3에 따라 서명은 다음을 포함합니다:
- PacketNonce (8바이트)
- TimestampMs (8바이트, 리틀 엔디언 int64)
- Type (4바이트, 리틀 엔디언 int32)
- SourceUhid (길이 + UTF-8)
- DestinationUhid (길이 + UTF-8)
- SHA-256(Payload) (32바이트)
- Ttl (4바이트, 리틀 엔디언 int32)
- Priority (4바이트, 리틀 엔디언 int32)

**재전송 방지:**
- 확인된 (sender_uhid, nonce) 쌍의 캐시 유지
- 캐시 항목당 5분 TTL
- 60초마다 자동 정리

### 5. 전송 서비스

물리적 전송(BLE, Wi-Fi Direct 등)을 위한 추상 기반 클래스:

```python
from aether.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**InProcessTransport 기능:**
- 클래스 수준 전역 노드 레지스트리
- threading.Lock을 사용한 스레드 안전성
- 테스트 및 로컬 메시 시뮬레이션에 최적
- 속성: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## 상수 참조

모든 프로토콜 상수는 `aether/constants.py`에 정의되어 있습니다:

### 암호화
- `ED25519_PRIVATE_KEY_SIZE`: 32바이트
- `ED25519_PUBLIC_KEY_SIZE`: 32바이트
- `ED25519_SIGNATURE_SIZE`: 64바이트
- `AES_GCM_NONCE_SIZE`: 12바이트
- `AES_GCM_TAG_SIZE`: 16바이트
- `MAX_SKIPPED_KEYS`: 1000

### 라우팅
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN 저장 후 전달
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(전체 목록은 `constants.py` 참조)

## 데모 실행

모든 주요 기능을 다채로운 출력과 함께 시연합니다:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

데모 내용:
1. Ed25519 키 생성 및 서명
2. AetherNode를 사용한 노드 생성
3. Signal Protocol X3DH 키 교환
4. 메시지 암호화 및 복호화
5. 패킷 직렬화/역직렬화
6. 패킷 서명 및 재전송 공격 탐지
7. 인-프로세스 전송 통신
8. 완전한 종단 간 암호화 워크플로

## 의존성

### 런타임
- `pynacl>=1.5.0` - libsodium을 통한 Ed25519 서명
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### 개발
- `pytest>=7.4.0` - 테스트 프레임워크
- `pytest-asyncio>=0.21.0` - 비동기 테스트 지원
- `black>=23.0.0` - 코드 포매팅
- `mypy>=1.5.0` - 정적 타입 검사
- `ruff>=0.1.0` - 린팅

## 호환성

**Python 버전:** 3.10+

**플랫폼:** 크로스 플랫폼 (Windows, macOS, Linux)

**암호화 백엔드:** 시스템 libsodium 및 cryptography 라이브러리 백엔드를 사용하여 플랫폼 간 일관된 동작을 보장합니다.

## 프로토콜 참조

- **AODV 라우팅:** RFC 3561
- **X3DH 키 합의:** Signal Foundation, 2016년 11월
- **Double Ratchet:** Signal Foundation, 2016년 11월
- **HKDF:** RFC 5869 (HMAC 기반 추출 및 확장)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB 외, 2012년

## 보안 고려사항

### 키 제로화
사용 후 중간 암호화 자료를 제로화합니다:
- ECDH에서 나온 공유 비밀
- 대칭 래칫에서 나온 메시지 키
- 설정 컨텍스트에서 유도된 키 자료

Python에서는 진정한 인-플레이스 메모리 제로화에 한계가 있지만, 민감한 데이터는 사용 후 즉시 변수 범위에서 삭제됩니다.

### 위협 모델
Aether는 다음을 가정합니다:
- BLE/Wi-Fi에 대한 수동적 도청
- 능동적 패킷 주입 및 재전송
- 가짜 노드 생성을 통한 시빌 공격
- 선택적 서비스 거부

보호 수단:
- **기밀성:** 메시지별 AES-256-GCM 키
- **무결성:** Ed25519 패킷 서명
- **재전송 방지:** 논스 기반 중복 제거
- **전방 비밀성:** 메시지별 키를 갖춘 대칭 래칫
- **경로 인증:** 서명된 경로 응답

### 제한 사항
- 순서 없는 메시지 전달은 최대 1000개 메시지까지 지원
- 간격을 초과하는 메시지는 거부됨
- BLE 주소는 15분마다 교체됨 (Python에서 미구현)
- P-256에서 Ed25519로의 마이그레이션 기간은 30일 (폴백 미구현)

## 테스트

테스트 스위트 실행:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## 라이선스

MIT 라이선스 - 자세한 내용은 LICENSE 파일 참조

## 기여

개선 사항에 기여하려면:

1. 코드가 PEP 8 스타일을 따르는지 확인 (포매팅에 `black` 사용)
2. 모든 함수에 타입 힌트 추가
3. 공개 API에 독스트링 포함
4. 타입 검사를 위해 `mypy` 실행
5. 새로운 기능에 대한 테스트 추가

## 참조

- Aether 프로토콜 사양: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# 레퍼런스 구현: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
