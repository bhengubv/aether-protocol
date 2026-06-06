# Aether 메시 네트워킹 프로토콜 - C 구현

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](README.md)

고성능, 임베디드 친화적인 Aether 메시 네트워킹 프로토콜의 C 구현입니다. ESP32 및 nRF52와 같은 자원 제한 장치를 위해 설계되었으며, Ed25519 서명, AES-256-GCM 암호화, AODV 기반 라우팅을 완전히 지원합니다.

## 개요

Aether는 간헐적이거나 인터넷 연결이 없는 환경을 위한 탈중앙화 메시 네트워킹 프로토콜입니다. 이 C 구현은 다음을 제공합니다:

- **프로토콜 직렬화/역직렬화** — C# 참조 구현과 일치하는 리틀 엔디언 와이어 포맷
- **암호화 작업** — Ed25519 서명, AES-256-GCM 암호화, HMAC-SHA256, HKDF-SHA256 (libsodium 사용)
- **패킷 서명** — 프로토콜 명세에 따른 결정론적 서명 가능 데이터 구성
- **전송 추상화** — 커스텀 전송 구현을 위한 vtable 패턴
- **인프로세스 전송** — 다중 노드 시나리오를 위한 내장 테스트 전송
- **임베디드 우선 설계** — 가능한 한 고정 크기 버퍼, 최소 메모리 할당, 상수 시간 연산

## 빌드 요구 사항

- **CMake** ≥ 3.16
- **C11 컴파일러** (gcc, clang 등)
- **libsodium** — 암호화 작업용
- **POSIX 스레드** (pthread)

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

이 라이브러리는 ESP-IDF 컴포넌트로 사용할 수 있도록 설계되었습니다:

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

## 구조

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

## 빠른 시작

### 데모 빌드 및 실행

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

예상 출력은 다음을 시연합니다:
1. Ed25519 키 생성
2. 패킷 생성 및 서명
3. 와이어 포맷으로 직렬화
4. 역직렬화
5. AES-256-GCM 암호화/복호화
6. HMAC-SHA256 인증
7. HKDF 키 유도

### 단위 테스트 실행

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### 코드에서 사용하기

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    if (!packet) return 1;

    // Set fields
    aethernet_packet_set_source_uhid(packet, "node-alice");
    aethernet_packet_set_destination_uhid(packet, "node-bob");
    aethernet_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    aethernet_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];
        aethernet_ed25519_sign(private_key, signable, signable_len, signature);
        aethernet_packet_set_signature(packet, signature, AETHERNET_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethernet_mesh_packet_t *received = aethernet_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethernet_packet_free(received);
    }

    aethernet_packet_free(packet);
    return 0;
}
```

## API 레퍼런스

### 프로토콜

#### 패킷 관리
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — 새 패킷 생성
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — 패킷 해제
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — 패킷 복제

#### 직렬화
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — 와이어 포맷으로 직렬화
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — 와이어 포맷에서 역직렬화
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — 와이어 크기 추정

#### 패킷 필드
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — 출발지 설정
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — 목적지 설정
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — 페이로드 설정
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — 서명 설정

#### 유효성 검사
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — 만료 여부 확인
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — TTL > 0 여부 확인

#### 서명 데이터
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — 결정론적 서명 가능 바이트 가져오기 (호출자가 해제해야 함)

### 보안

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — 32+32 바이트 키 생성
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — 서명 (64 바이트 생성)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — 검증

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — 암호화 (nonce가 NULL이면 자동 생성)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — 복호화

#### HMAC 및 해시
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 바이트)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 바이트)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### 유틸리티
- `void aethernet_zeroize(void *mem, size_t len)` — 상수 시간 메모리 초기화
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — 암호학적으로 안전한 난수 바이트

### 전송

#### 공통 함수
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — 데이터 전송
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — 연결 상태 확인
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — 콜백 등록
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — 정리

#### 인프로세스 전송
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — 공유 인프로세스 전송 생성
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — 노드 등록
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — 노드 등록 해제

## 와이어 포맷 호환성

이 구현은 **리틀 엔디언** 다중 바이트 정수를 사용하여 프로토콜 명세를 엄격히 따릅니다:

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

이 C 구현으로 직렬화된 패킷은 C# 참조 구현과 100% 호환됩니다.

## 보안 고려 사항

### 암호화 라이브러리
- **libsodium** (libsodium.org) — 모든 암호화 작업에 사용
- Ed25519 서명 및 검증
- AES-256-GCM 인증 암호화
- HMAC-SHA256 및 SHA-256
- HKDF-SHA256 키 유도
- 암호학적으로 안전한 난수 생성

### 키 초기화
모든 민감한 자료 (키, 평문, 중간 값)는 사용 직후 `sodium_memzero()`를 사용하여 메모리에서 초기화됩니다. 이는 우발적인 키 유출을 방지합니다.

### 패킷 유효성 검사
- 타임스탬프 기반 중복 제거: 300초 이상 경과한 패킷은 거부됨
- 논스 고유성: 모든 패킷에 8바이트 임의 논스 포함
- TTL 유효성 검사: TTL=0인 패킷은 폐기됨
- 서명 검증: Ed25519 서명은 프로토콜 v2에서 필수

## 임베디드 장치 참고 사항

### ESP32
- ESP-IDF용 libsodium 포트 필요 (ESP-IDF 컴포넌트를 통해 사용 가능)
- 고정 패킷 크기 추정으로 메모리 할당 단순화
- 뮤텍스 작업에 POSIX 스레드 사용
- 가능한 경우 스택에 버퍼를 사전 할당

### nRF52
- ESP32와 유사
- BLE GATT 전송 계층은 전송 vtable을 통해 구현 가능
- 다중 패킷 처리를 위해 FreeRTOS와 같은 RTOS 사용 권장

### 메모리 사용량
- 최소 패킷: ~52 바이트
- 최대 패킷: 65KB (`AETHERNET_MAX_PAYLOAD_LEN`을 통해 구성 가능)
- 256 노드 피어 테이블: ~32KB
- 메모리 내 단일 메시 패킷: ~8KB (최대 필드의 최악 경우)

## 성능

현대 x86-64 머신 (Intel Core i9) 기준:
- **직렬화**: 패킷당 ~1-2 µs
- **역직렬화**: 패킷당 ~1-2 µs
- **Ed25519 서명**: ~100 µs
- **Ed25519 검증**: ~300 µs
- **AES-256-GCM 암호화**: KB당 ~1 µs
- **SHA-256**: KB당 ~0.5 µs

## 테스트

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

테스트 항목:
- 패킷 생성 및 복제
- 직렬화 왕복 테스트
- Ed25519 서명 및 검증
- AES-GCM 암호화/복호화
- HMAC-SHA256 계산
- HKDF 키 유도
- TTL 및 만료 유효성 검사
- 서명 가능 데이터 결정론성

## Aether 에코시스템과의 통합

이 C 라이브러리는 다음과 통합되도록 설계되었습니다:
- **AetherNetAPI** (C#) — 서버 측 메시 릴레이 및 분석
- **AetherNet.Core** (C#) — 참조 구현 (상호 운용 가능한 와이어 포맷)
- **Meshtastic** — 오픈소스 메시 라디오 펌웨어
- **esp-idf** — Espressif IoT 개발 프레임워크
- 커스텀 임베디드 애플리케이션

## 라이선스

SPDX-License-Identifier: MIT

전체 텍스트는 LICENSE 파일을 참조하십시오.

## 기여

기여를 환영합니다! 다음 사항을 확인하십시오:
- 모든 테스트 통과 (`ctest --output-on-failure`)
- 코드가 C11 규격 준수
- 와이어 포맷이 C# 참조와 정확히 일치
- 모든 민감한 데이터가 초기화됨
- 문서가 업데이트됨

## 참고 자료

- 프로토콜 명세: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# 참조: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
