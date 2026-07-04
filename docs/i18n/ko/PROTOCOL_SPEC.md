# Aether Mesh Networking Protocol Specification

**Version:** 2.0
**Status:** Reconciled with HEAD (2026-05-05)
**Date:** 2026-03-15 (초안 작성); 2026-05-05 (§2, §4, §10, §11 일치 확인, §3/§9 검증 완료)
**Authors:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **독자 안내.** 이 문서의 이전 초안들은 8개 언어 와이어 포맷 정렬 및
> X25519 + Signal Double Ratchet로의 전체 이식 이전에 작성된 것입니다.
> 2026-05-05 기준으로 §2 (패킷 포맷), §3 (라우팅), §4 (키 교환), §9 (DTN)는
> 구현된 프로토콜을 기술하며, §10 (비디오 스트리밍) 및 §11 (Watch Together)는
> 목표 프로토콜을 기술합니다 — 이들은 와이어 정의와 픽스처 테스트가 완료되었지만
> 코덱 / BitTorrent / ChipIn 파이프라인은 아직 스캐폴딩에 연결되지 않았습니다.
> 이 문서와 구현이 일치하지 않는 경우 C# 레퍼런스가 최종 권위를 갖습니다.
>
> - 표준 와이어 바이트: `fixtures/expected/*.bin` (10개 지정 케이스)
> - 레퍼런스 직렬화기: `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - 레퍼런스 Signal 스택: `src/AetherNet.Security/Services/SignalProtocolService.cs`
> - 레퍼런스 라우팅: `src/AetherNet.Core/Routing/RoutingService.cs`
> - 레퍼런스 DTN: `src/AetherNet.Core/Dtn/DtnService.cs`
> - 크로스 언어 와이어 상호운용성 증명: `fixtures/README.md`
> - 크로스 언어 Signal 상호운용성 증명: `fixtures/signal/README.md`

---

## 목차

1. [요약](#1-요약)
2. [패킷 포맷](#2-패킷-포맷)
3. [라우팅 알고리즘](#3-라우팅-알고리즘)
4. [키 교환](#4-키-교환)
5. [전송 계층 요구사항](#5-전송-계층-요구사항)
6. [디스커버리 프로토콜](#6-디스커버리-프로토콜)
7. [보안 모델](#7-보안-모델)
8. [SOS 브로드캐스트](#8-sos-브로드캐스트)
9. [DTN 저장 후 전달](#9-dtn-저장-후-전달)
10. [비디오 스트리밍](#10-비디오-스트리밍)
11. [Watch Together](#11-watch-together)
12. [보안 및 프라이버시 계층](#12-security--privacy-layer)

---

## 1. 요약

Aether는 인터넷 연결이 불안정하거나 전혀 없는 환경을 위해 설계된 탈중앙화 메시 네트워킹 프로토콜입니다. 이 프로토콜은 이기종 근거리 전송 수단(Bluetooth Low Energy, Wi-Fi Direct, NearLink)을 통한 멀티홉 패킷 라우팅, 대칭 래칫을 동반한 X3DH 파생 키 합의를 사용하는 종단 간 암호화, 지연 허용 저장-후-전달 방식의 메시지 배달, 그리고 긴급 SOS 플러드 메커니즘을 제공합니다. 이 프로토콜은 전송 계층에 독립적입니다. 즉, 피어 간에 바이트 배열을 송수신할 수 있는 모든 물리 계층이 유효한 Aether 전송 수단이 됩니다. 노드는 UHID(Universal Hardware Identifier)로 식별되며 Ed25519 아이덴티티 키를 통해 인증됩니다. Aether는 범용 네트워크 계층으로 설계되었으며, 생태계 내 모든 애플리케이션이 Aether 서비스를 등록하고, 인터넷에 연결되지 않은 노드는 메시 트래픽을 인터넷으로 연결하는 게이트웨이 피어를 통해 더 넓은 네트워크에 접근합니다.

---

## 2. 패킷 포맷

> 2026-05-05에 `src/AetherNet.Core/Protocol/PacketSerializer.cs` 및
> `fixtures/expected/` 하위의 10개 픽스처 케이스와 대조하여 일치 확인 완료.

### 2.1. MeshPacket 와이어 레이아웃

모든 Aether 메시지는 `MeshPacket`에 캡슐화됩니다. 필드는 와이어상에서 **정확히** 다음 순서로 나타납니다:

| Off | Field            | Type                            | Size       | 설명 |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = 서명 없음 (레거시), `2` = 서명 있음 (현재) |
| 1   | Type             | uint8                           | 1          | 패킷 타입 열거형 (§2.4 참조) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | 중복 제거를 위한 패킷 식별자. **빅 엔디언** 바이트 순서 — .NET의 혼합 엔디언 Guid 기본값이 아님. |
| 18  | Priority         | uint8                           | 1          | 우선순위 수준 (0 = 일반, 255 = SOS). **와이어 필드는 1바이트이며, 255를 초과하는 값은 클램프 처리해야 합니다.** |
| 19  | Ttl              | int32, little-endian            | 4          | 홉마다 감소되는 TTL. **4바이트 int32** — 1바이트 uint8 아님 — ~2³¹-1까지의 값이 유효합니다. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Unix 에포크 밀리초 (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | `SourceUhid`의 UTF-8 바이트 길이. 최대 65535. |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | 송신자의 UHID. 빈 값도 허용되나 일반적이지 않습니다. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | `DestinationUhid`의 UTF-8 바이트 길이. |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | 수신자의 UHID. 브로드캐스트의 경우 빈 문자열. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | `PacketNonce`의 바이트 길이. 표준 값: 8. |
| ... | PacketNonce      | bytes                           | P          | 재전송 공격 방지를 위한 암호학적 랜덤 논스. |
| ... | Payload Len      | int32, little-endian            | 4          | `Payload`의 바이트 길이. 음수 값은 오류입니다. |
| ... | Payload          | bytes                           | Q          | 애플리케이션 데이터. 해석 방식은 `Type`에 따라 달라집니다. |
| ... | Signature Len    | uint16, little-endian           | 2          | `Signature`의 바이트 길이. 0 (서명 없음) 또는 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | 서명 가능 데이터에 대한 Ed25519 서명 (§2.3 참조). |

**길이 프리픽스 너비**는 필드마다 다릅니다 — `SourceUhid`, `DestinationUhid`,
`PacketNonce`, `Signature`는 **2바이트 (uint16)** 길이 프리픽스를 사용하며,
`Payload`는 페이로드가 64 KiB를 초과할 수 있으므로 **4바이트 (int32)** 길이 프리픽스를 사용합니다.

### 2.2. 최소 패킷 크기

모든 가변 길이 필드가 비어 있을 때(UHID 길이 0, 논스 길이 0,
페이로드 길이 0, 서명 길이 0), 와이어 크기는 다음과 같습니다:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

이전 버전의 사양에 나와 있던 50바이트 / 52바이트 수치는 잘못된 것입니다.

### 2.3. 와이어 포맷 다이어그램

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

실제 예시는 `fixtures/expected/basic_data.bin` (83바이트,
`fixtures/inputs.json`에 있는 표준 입력)을 참조하시기 바랍니다. 구현체는 전체 픽스처 모음에 대해 검증됩니다 — 크로스 언어 픽스처 검증기 테스트에서 하나라도 다르면 실패입니다.

### 2.4. 서명 가능 데이터 구성

와이어상의 `Signature` 필드에 기록되는 서명은 와이어 바이트 자체가 아니라
별도의 표준 바이트 시퀀스에 대해 계산됩니다. 이렇게 하면 와이어 레이아웃이
변경되어도 서명이 유지되며, 중계 노드가 평문 페이로드를 보지 않고도
무결성을 검증할 수 있습니다(서명에는 페이로드의 SHA-256 해시만 포함됩니다).

서명 가능 바이트 시퀀스는 다음의 연결입니다:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> §2.1의 와이어 레이아웃과 의도적으로 다른 점에 유의하시기 바랍니다:
> 서명 가능 데이터는 `Type`, `Length`, `Ttl`, `Priority`에 **4바이트 int32**를 사용하는 반면,
> 와이어는 각각 1바이트 / 2바이트 / 4바이트 / 1바이트를 사용합니다.
> 이는 의도적인 설계입니다 — 서명 가능 형식은 언어 간 이식성을 위해 고정 너비 필드를 사용하며,
> 와이어 형식은 BLE PDU 효율을 위해 컴팩트하게 설계되었습니다.
> 구현체는 서명 가능 바이트로 인코딩하기 전에 `Priority`를 `[0,255]`로 클램프해야 합니다.
> 그렇지 않으면 수신자(와이어 바이트 0..255를 확인)가 서명 가능 버퍼를 다르게 계산하여 검증에 실패합니다.

레퍼런스 구현은 `src/AetherNet.Security/Services/
PacketSigningService.cs::BuildSignableData`에 있으며 이식 시 반드시 참고해야 합니다.

### 2.5. 패킷 타입

| Value | Name              | Direction     | 설명 |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV 라우트 요청 |
| 2     | RouteReply        | Unicast       | AODV 라우트 응답 (목적지에 의해 반드시 서명되어야 합니다) |
| 3     | Data              | Unicast       | 애플리케이션 데이터 |
| 4     | Ack               | Unicast       | 전달 확인 응답 |
| 5     | SosBroadcast      | Flood         | 긴급 브로드캐스트 (§8 참조) |
| 6     | SosAck            | Unicast       | SOS 확인 응답 |
| 7     | ChannelMessage    | Multicast     | 그룹 채널 메시지 |
| 8     | ChunkRequest      | Unicast       | P2P 콘텐츠 청크 요청 |
| 9     | ChunkData         | Unicast       | P2P 콘텐츠 청크 응답 |
| 10    | Heartbeat         | Broadcast     | 주기적 생존 신호 |
| 11    | StreamAnnounce    | Broadcast     | 라이브 스트림 광고 |
| 12    | StreamSegment     | Unicast/Tree  | 라이브 스트림 미디어 세그먼트 |
| 13    | StreamSubscribe   | Unicast       | 스트림 릴레이 트리 참여 요청 |
| 14    | StreamUnsubscribe | Unicast       | 스트림 릴레이 트리 이탈 |
| 15    | VoicePtt          | Unicast       | Push-to-talk 음성 프레임 |
| 16    | VoiceCall         | Unicast       | 실시간 음성 통화 프레임 |
| 17    | VoiceSignaling    | Unicast       | 음성 통화 설정/해제 |
| 18    | DtnBundle         | Unicast       | DTN 저장-후-전달 번들 (§9 참조) |
| 19    | DtnCustodyAck     | Unicast       | DTN 커스터디 이전 확인 응답 |
| 20    | DtnDeliveryReceipt| Unicast       | DTN 종단 간 전달 확인 |
| 21    | PresenceBeacon    | Broadcast     | 현재 상태 및 가용성 알림 |
| 22    | PresenceQuery     | Unicast       | 현재 상태 조회 요청 |
| 23    | ProfileSync       | Unicast       | 프로필 메타데이터 동기화 |
| 24    | TipPacket         | Unicast       | 노드 팁 (LedgerAPI를 통해 정산) |
| 25    | PreKeyRequest     | Unicast       | 피어의 프리 키 번들 요청 |
| 26    | PreKeyResponse    | Unicast       | 프리 키 번들 전달 |
| 27    | VideoCall         | Unicast       | 암호화된 비디오 프레임 (H.264/H.265/VP8 NAL 유닛) |
| 28    | VideoSignaling    | Unicast       | 비디오 통화 설정: offer, answer, reject, bye, 코덱 협상 |
| 29    | WatchSync         | Unicast       | 동기화 재생 명령: play, pause, seek, speed |
| 30    | WatchReaction     | Multicast     | Watch-together 중 타임스탬프가 붙은 이모지 또는 음성 반응 |
| 31    | VideoFrame        | Unicast/SFU   | 그룹 비디오 프레임 (SFU 릴레이가 참가자들에게 배포) |
| 32    | ScreenShare       | Unicast       | 화면 공유 프레임 (비디오와 동일한 파이프라인, 별도 플래그) |
| 33    | WatchChunkRequest | Unicast       | 재생 위치에 편향된 우선순위 청크 요청 |
| 34    | TorrentMetadata   | Multicast     | BitTorrent .torrent 파일 또는 마그넷 링크 메타데이터 교환 |

### 2.6. 노드 기능

노드는 비트 필드로 자신의 기능을 광고합니다:

| Bit | Value | Capability  | 설명 |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth Low Energy 전송 수단 사용 가능 |
| 1   | 2     | WifiDirect  | Wi-Fi Direct 전송 수단 사용 가능 |
| 2   | 4     | Gateway     | 인터넷 게이트웨이 (메시를 IP 네트워크로 연결) |
| 3   | 8     | Relay       | 다른 노드를 위해 패킷을 중계할 의향이 있음 |
| 4   | 16    | Sos         | SOS 브로드캐스트 가능 |
| 5   | 32    | Streaming   | 라이브 스트리밍 릴레이 가능 |
| 6   | 64    | Voice       | 음성 통화 릴레이 가능 |
| 7   | 128   | DtnCarrier  | DTN 저장-후-전달 캐리어 |
| 8   | 256   | NearLink    | NearLink 전송 수단 사용 가능 |
| 9   | 512   | Video       | 비디오 인코딩/디코딩 가능 |

---

## 3. 라우팅 알고리즘

Aether는 암호학적 라우트 인증 및 QoS 가중 라우트 선택으로 확장된 AODV(Ad-hoc On-demand Distance Vector) 라우팅 프로토콜에 기반한 반응형 라우팅 프로토콜을 사용합니다.

### 3.1. 라우트 요청 (RREQ)

노드가 라우트를 알 수 없는 목적지로 패킷을 전송해야 할 때 라우트 요청을 시작합니다:

1. 발신자는 `Type = RouteRequest`인 `MeshPacket`을 생성하고, `SourceUhid`를 자신으로, `DestinationUhid`를 목적지로, `TTL = 7` (기본값)로 설정합니다.
2. 패킷은 직접 연결된 모든 피어에게 브로드캐스트됩니다.
3. RREQ를 수신한 각 중간 노드는 다음을 수행합니다:
   a. 패킷 `Id`로 이미 이 RREQ를 본 적이 있는지 확인합니다. 그렇다면 패킷을 묵묵히 폐기합니다 (중복 제거). 중복 제거 캐시는 최대 `DeduplicationCacheSize`개 (기본 10,000)의 항목을 보유하며 한도에 도달하면 완전히 초기화됩니다.
   b. RREQ 발신자로의 **역방향 라우트**를 설치합니다. 역방향 라우트는 RREQ를 수신한 피어의 UHID를 다음 홉으로 기록합니다. 홉 수는 `DefaultTtl - packet.Ttl + 1`로 계산됩니다.
   c. 자신이 목적지인 경우 RREP를 생성합니다 (§3.2 참조).
   d. 목적지로의 유효한 라우트를 보유하고 있는 경우 목적지를 대신하여 RREP를 생성할 수 있습니다.
   e. 그 외의 경우 TTL을 감소시키고 RREQ를 다시 브로드캐스트합니다.
4. 발신자는 RREP를 **5,000 ms** (`RouteTimeoutMs`) 타임아웃으로 기다립니다. RREP가 도착하지 않으면 라우트 탐색이 실패합니다.

### 3.2. 라우트 응답 (RREP)

목적지(또는 유효한 라우트를 보유한 중간 노드)가 라우트 응답을 생성할 때:

1. `Type = RouteReply`인 `MeshPacket`을 생성하고, `SourceUhid`를 목적지 노드로, `DestinationUhid`를 RREQ 발신자로 설정합니다.
2. **보안 요구사항:** RREP는 반드시 목적지 노드의 Ed25519 아이덴티티 키로 서명되어야 합니다. 서명은 표준 서명 가능 데이터(§2.3)를 포함합니다. 이는 악의적인 중간 노드에 의한 라우트 오염을 방지합니다.
3. RREP는 RREQ 전파 중에 설치된 역방향 라우트를 따라 유니캐스트로 반환됩니다.
4. RREP를 전달하는 각 중간 노드는 다음을 수행합니다:
   a. 알려진 경우 주장된 발신자의 공개 키에 대해 RREP 서명을 검증합니다. 검증에 실패하면 RREP를 폐기하고 경고를 기록합니다.
   b. RREP 발신자(목적지 노드)로의 **순방향 라우트**를 설치하며 RREP 송신자를 다음 홉으로 설정합니다.
   c. TTL을 감소시키고 RREQ 발신자 방향으로 전달합니다.
5. RREP가 발신자에 도달하면 대기 중인 라우트 요청(`TaskCompletionSource`를 통해 추적)이 설치된 라우트와 함께 해결됩니다.

### 3.3. 라우트 유지 관리

- **TTL 기반 만료:** 모든 라우트 항목에는 `ExpiresAt` 타임스탬프가 있으며 `now + 300초` (`RouteExpirySeconds`)로 설정됩니다. 라우트는 암묵적으로 갱신되지 않으며 만료 후에는 새로운 RREQ/RREP 사이클을 통해 재설정해야 합니다.
- **주기적 정리:** 프로토콜 서비스는 주기적인 하트비트(기본 300초마다)를 실행합니다. 각 사이클에서 인메모리 `ConcurrentDictionary` 및 SQLite 백업 저장소 모두에서 만료된 라우트를 제거합니다.
- **RREQ 중복 제거 정리:** RREQ ID 집합은 `DeduplicationCacheSize` (기본 10,000)개 항목을 초과하면 초기화됩니다.

### 3.4. 라우트 품질 및 QoS

각 `RouteEntry`는 [0, 100] 범위의 `QualityScore`를 가지며 새로 탐색된 라우트의 경우 50으로 초기화됩니다. 점수는 다음을 고려합니다:

- **홉 수:** 홉이 적을수록 일반적으로 빠른 라우트를 의미합니다.
- **지연 시간:** 가능한 경우 측정된 왕복 시간.
- **피어 신뢰성:** 다음 홉 피어의 신뢰성 점수 (§3.5 참조).

팁 인센티브 시스템에 참여하는 노드는 라우트 품질 점수에 QoS 가산점을 받습니다. 이는 유연한 우선순위입니다: 팁을 제공하지 않는 노드도 항상 서비스를 받지만, 꾸준히 팁을 제공하는 노드는 라우트 선택에서 약간의 이점을 누릴 수 있습니다. 가산점 등급은 다음과 같습니다:

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. 피어 신뢰성 점수

알려진 모든 피어에게는 [0, 100] 범위의 신뢰성 점수가 부여되며 50 (`DefaultReliabilityScore`)으로 초기화됩니다. 점수는 관찰된 동작에 따라 조정됩니다:

| Event                | Delta |
|----------------------|-------|
| Successful relay     | +2    |
| Failed relay         | -5    |
| SOS relay            | +5    |
| Chunk served         | +1    |
| Chunk serve failure  | -10   |

신뢰성 점수는 SQLite에 저장되며 시작 시 메모리로 로드됩니다. 점수는 라우트 선택에 영향을 미치며 더 신뢰할 수 있는 피어를 통하는 라우트가 선호됩니다.

---

## 4. 키 교환

> 2026-05-05에 `src/AetherNet.Security/Services/SignalProtocolService.cs`의 C# 레퍼런스 구현 및
> `fixtures/signal/` 하위의 크로스 언어 픽스처 모음과 대조하여 일치 확인 완료.
> C# 레퍼런스는 X25519를 기반으로 하는 완전한 X3DH + Double Ratchet (Signal §3 + §5)를 제공합니다.
> Go, Python, TypeScript, Rust, Swift, Kotlin은 동일한 엔벨로프로 이식되었으며
> X3DH 및 KDF_RK 픽스처 수준에서 바이트 동등성이 확인되었습니다.
> C도 이제 프리미티브만이 아니라 완전한 세션 기계 장치를 제공합니다
>(`c/src/signal_protocol.c`의 X3DH + OPK/SPK 수명 주기 + Double Ratchet,
> `c/tests/test_signal_session.c`의 2-노드 E2E 테스트 포함).
> 이 섹션이 코드와 일치하지 않는 경우 코드가 최종 권위를 갖습니다;
> `OPEN_ISSUES.md`에 이슈를 등록하시기 바랍니다.

Aether는 비동기 세션 설정을 위해 **X3DH** (Extended Triple Diffie-Hellman, Signal §3)를 구현하며,
지속적인 순방향 비밀성 및 사후 침해 복구를 위해 즉시 **Signal Double Ratchet** (Signal §5)를 뒤따릅니다.
모든 세션 암호화는 Curve25519를 기반으로 합니다:
ECDH를 위한 **X25519** (RFC 7748) 및 서명을 위한 **Ed25519** (RFC 8032).

### 4.1. 아이덴티티 키

각 노드는 최초 실행 시 **두 개**의 장기 키 쌍을 생성합니다 (XEdDSA 없음;
더 단순한 이중 키 배열이 모든 구현에서 사용됩니다):

- **Ed25519 키 쌍** — 32바이트 시드 (비공개), 32바이트 공개 키.
  패킷 서명 (§2.4), `SignedPreKeySignature` (§4.3),
  RREP 인증 (§3.2), 팁 서명에 사용됩니다.
- **X25519 키 쌍** — 32바이트 원시 비공개 및 공개 키. 네 가지 X3DH DH 연산 (§4.4)에 사용됩니다.

레퍼런스: `SignalProtocolService.InitializeIdentityKeys`. 비공개 키는
장치에만 저장되며, 공개 키는 `PreKeyBundle`에 게시됩니다.

인바운드 패킷의 *서명 검증*에만 30일 P-256 → Ed25519 마이그레이션 창이 적용됩니다 — §7.5 참조.
프리 키 번들 자체는 와이어에서 X25519 전용입니다.

### 4.2. 곡선 선택

X3DH 및 Double Ratchet는 **X25519**만을 사용합니다. P-256은 현재 구현에서
세션 설정에 사용되지 않습니다. 이 사양의 이전 초안에서는 P-256 ECDH를 기술하였으나,
해당 내용은 2026-05-05 전체 X25519 이식 이전의 내용이므로 더 이상 유효하지 않습니다.

### 4.3. 프리 키 번들

프리 키 번들은 응답자가 온라인 상태가 아니더라도 발신자가 세션을 설정할 수 있도록
게시됩니다 (Signal §3.4):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

레퍼런스: `AetherNet.Security.Models.PreKeyBundle`. 와이어 형상 계약은
모든 8개 언어에서 동일합니다.

**일회성 프리 키 (OPK) 풀.** 각 응답자는 `OpkPoolSize` (기본 100, Signal 공개 가이드라인 반영)개의 X25519
OPK 풀을 유지합니다. 번들 생성 시 FIFO 큐에서 다음으로 미사용인 id를 꺼내고 풀을 목표 크기로 보충합니다.
각 OPK는 정확히 한 번만 사용됩니다: 응답자는 해당 id를 참조하는 첫 번째 PreKey 메시지에서
비공개 절반을 제거하고 영점화합니다. 동일한 OPK id를 위해 경쟁하는 동시 발신자들은 `_preKeyLock` 하에서
정확히 하나의 `EstablishResponderSession`만 성공하며, 실패한 측은 `CryptographicException`을 발생시킵니다.

레퍼런스: `SignalProtocolService.TopUpOpkPoolNoLock` (494–518번 줄),
`SignalProtocolService.EstablishResponderSession` (636–718번 줄). 풀 동작은
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`에서 검증됩니다.

**서명된 프리 키 (SPK) 교체.** SPK는 첫 번째 번들 호출 시 지연 생성되며 이후 호출에서 재사용됩니다.
이를 통해 X3DH 실행 전에 번들을 가져오는 동시 발신자들이 서로의 번들을 무효화하지 않습니다.
주기적인 SPK 교체 (Signal §3.3에서는 매주 권장)는 번들 생성의 부수 효과가 아닌 명시적인 작업입니다.

프리 키 id는 `RandomNumberGenerator.GetInt32(1, int.MaxValue)`에서 가져오며
충돌 시 명시적 재시도를 수행합니다 (최대 64번 시도 후 예외 발생).

### 4.4. 세션 설정 (X3DH)

완전한 X3DH (Signal §3.3)는 발신자 측에서 실행됩니다. X25519를 통해
네 가지 DH 연산을 계산합니다:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

여기서 `IK_A` / `IK_B`는 X25519 아이덴티티 키, `EK_A`는 이 세션만을 위해 새로 생성된
X25519 에피머럴, `SPK_B`는 응답자의 서명된 프리 키, `OPK_B`는 응답자의 일회성 프리 키입니다.
초기 루트 키는 다음과 같습니다:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

`info` 상수 `aether-x3dh-root-v1`은 모든 구현에서 동일하며
`fixtures/signal/expected/x3dh_basic.json` (`root_key_hex` 필드)에 고정되어 있습니다.

레퍼런스: `SignalProtocolService.ProcessPreKeyBundleAsync` (554–626번 줄).
검증 경로:
`fixtures/signal/inputs.json` 케이스 `x3dh_basic` →
`fixtures/signal/expected/x3dh_basic.json`.

**번들 검증.** DH 연산 실행 전 발신자는 Ed25519를 사용하여 `IdentityKey`에 대해
`SignedPreKeySignature`를 검증합니다. 검증 실패 시 `CryptographicException`이 발생하고 번들이 폐기됩니다.
공개 키 크기는 `X25519Service.PublicKeySize` (32)에 대해 검증되며
형식이 잘못된 번들은 거부됩니다.

**세션 초기화.** `ProcessPreKeyBundleAsync` 마지막에 다음으로 `SignalSession`이 생성됩니다:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal 표준 X3DH ↔
  Double-Ratchet 통합: 발신자의 X3DH 에피머럴이 첫 번째 DH-래칫 키 쌍 (`DHs`)이 됩니다.
- `RemoteEphemeralPub = SPK_B` — 응답자의 서명된 프리 키는
  초기 피어 래칫 키 (`DHr`)로 처리됩니다.
- `SendChainKey = null`, `RecvChainKey = null` — 두 체인 키는 첫 번째 전송 / 첫 번째 DH-래칫 수신 시 지연 파생됩니다.
- `PendingPreKeyMessage = true` — 다음 아웃바운드 `EncryptAsync` 호출이 PreKey 메시지 (`MessageType=1`)를 내보내야 함을 표시합니다.

모든 DH 출력과 연결된 공유 비밀은 `finally` 블록에서 `CryptographicOperations.ZeroMemory`를 통해 영점화됩니다.

**비안전 전송 거부.** 세션이 없는 피어에 대해 `EncryptAsync`가 호출되면
`InvalidOperationException`이 발생합니다. UHID 파생 폴백 경로는 없습니다.
호스트는 메시지를 큐에 대기시키고 (`MessagingService` + `SignalMessageEnvelopeCipher` 참조)
세션 설정이 완료된 후 재시도해야 합니다.

### 4.5. Double Ratchet (Signal §5)

각 측은 회전하는 X25519 래칫 키 쌍 (`DHs`)과 피어의 마지막으로 확인된 래칫 공개 키 (`DHr`)를 유지합니다.
모든 메시지에서 송신자는 현재 `DHs` 공개 키를 게시하며, 수신자가 새로운 `DHr`을 관찰하면
`KDF_RK(RK, DH(myDHs, newDHr))`를 통해 체인을 다시 키잉하는 **DH-래칫 단계**를 실행합니다 — 루트 키와 새로운 체인 키를 모두 재파생합니다.

#### 4.5.1. KDF_RK

`KDF_RK`는 64바이트 블록에 대한 HKDF-SHA256으로, 새 루트 키와 새 체인 키로 32+32로 분할됩니다:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

레퍼런스: `SignalProtocolService.KdfRk` (857–868번 줄).
`fixtures/signal/inputs.json` 케이스 `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json`에 고정되어 있습니다.

#### 4.5.2. 대칭 래칫

Signal §5.1에 따라 메시지 키와 체인 키는 1바이트 도메인 분리를 사용한 HMAC-SHA256으로
체인 키에서 파생됩니다:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

레퍼런스: `SignalProtocolService.RatchetChainKey` (876–881번 줄).
`fixtures/signal/inputs.json` 케이스 `ratchet_step_basic` 및
`ratchet_step_three_iterations`에 고정되어 있습니다.

이전 버전의 사양에서는 `messageKey = HMAC-SHA256(chain_key, counter_bytes)` 및
별도의 `chain_key advance via HMAC(chain_key, 0x01)`을 기술하였습니다.
이는 비Signal 방식이며 구현된 적이 없었고, 표준 0x01/0x02 분할로 교체되었습니다.

#### 4.5.3. 수신 시 DH-래칫 단계

인바운드 메시지의 `SenderEphemeralKeyX25519`가 캐시된 `RemoteEphemeralPub`과
다를 때 트리거됩니다 (상수 시간 비교).

1. 피어가 경계를 넘어 건너뛴 키를 계산할 수 있도록 아웃바운드 카운터를 `PreviousChainCount` (Signal §5: PN)로 저장합니다.
2. `SendCounter`와 `RecvCounter`를 0으로 초기화하고 새 `RemoteEphemeralPub`을 설치합니다.
3. 새 수신 체인 파생: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. 이전 `myDHs` 비공개 키를 영점화하고 새 X25519 키 쌍을 생성합니다.
5. 새 전송 체인 파생: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

레퍼런스: `SignalProtocolService.DhRatchetReceive` (726–772번 줄).

#### 4.5.4. 지연 전송 체인 파생

발신자의 첫 번째 전송은 완전한 DH-래칫이 아닌 **반 단계**를 실행합니다 — X3DH가 이미
`DHs`와 `DHr`를 배치했으므로 전송 체인만 파생하면 됩니다:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs`는 여기서 교체되지 않습니다. 실제 수신 측 DH-래칫 단계에서만 교체됩니다.

레퍼런스: `SignalProtocolService.DhRatchetSendOnly` (780–796번 줄).

#### 4.5.5. 건너뛴 메시지 키

메시지가 순서 없이 도착하면 각 건너뛴 카운터의 메시지 키가
`SkippedMessageKeys`에 `(Hex(remoteEphPub):counter)` 키로 캐시됩니다.
원격 공개 키 바인딩이 필수적입니다 — DH-래칫 단계 이후에도 이전 체인 (다른 `DHr`)의
순서 없는 메시지가 도착할 수 있으며 각 체인에 대한 고유 키 집합이 필요합니다.

한도:

- 단일 간격에서 `MaxSkippedKeys` (1000)개를 초과하여 건너뛰면
  `CryptographicException`이 발생하고 세션 재설정이 강제됩니다.
- DH-래칫 경계를 넘을 때 수신자는 먼저 *이전* 체인에서 `PreviousChainCount`까지 키를 건너뛴 다음
  DH-래칫 단계를 실행하고 새 체인의 키를 파생합니다.

레퍼런스: `SignalProtocolService.SkipMessageKeys` (804–830번 줄) 및
복호화 내 건너뛰기 루프 (366–388번 줄).

### 4.6. 암호화된 페이로드 포맷

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

레퍼런스: `AetherNet.Security.Models.EncryptedPayload` (`SecurityModels.cs` 55–66번 줄).
`InitiatorEphemeralKeyX25519` 필드는 Double-Ratchet 이전 와이어 엔벨로프의 하위 호환성 별칭으로
PreKey 메시지에서 `SenderEphemeralKeyX25519`와 동일합니다; 새 소비자는 이를 무시해야 합니다.

AES-GCM 파라미터: 256비트 키, 96비트 논스 (`AesNonceSize = 12`),
128비트 태그 (`AesTagSize = 16`), 암호문에 태그 연결.
메시지 키는 AES-GCM 암호화/복호화 직후 `finally` 블록에서 영점화됩니다.

### 4.7. 언어별 현황

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |

8개 언어 (C# + Go + TypeScript + Python + Kotlin + Swift + Rust + C) 모두 C# 레퍼런스 계약과 일치하는 완전한 X3DH + Double Ratchet 세션 서비스와, 지연 보충 및 잠금 보호 소비 방식의 100-키 FIFO OPK 풀을 제공합니다. C 세션 서비스는 `c/src/signal_protocol.c`에 있으며 2-노드 E2E 테스트는 `c/tests/test_signal_session.c`에 있습니다.

---

## 5. 전송 계층 요구사항

Aether는 전송 계층에 독립적입니다. `ITransportService` 계약을 충족하는 모든 물리적 통신 채널이 메시에 참여할 수 있습니다.

### 5.1. ITransportService 인터페이스 계약

모든 전송 구현은 다음을 반드시 노출해야 합니다:

**속성:**

| Property           | Type   | 설명 |
|--------------------|--------|-------------|
| `Name`             | string | 사람이 읽을 수 있는 식별자 (예: "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | 현재 이 장치에서 전송 수단 사용 가능 여부 |
| `MaxBandwidthBps`  | int64  | 초당 바이트 단위의 최대 처리량 |
| `MaxRangeMeters`   | int32  | 최대 통신 거리 (미터) |
| `PowerCostRelative`| int32  | 상대적 전력 소비 (1 = 낮음, 10 = 높음) |
| `MaxConcurrentPeers` | int32 | 최대 동시 피어 연결 수 |

**메서드:**

| Method         | Signature | 설명 |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | 특정 피어에게 바이트 배열을 전송합니다. 성공 시 true를 반환합니다. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | 피어에게 스트림을 전송합니다 (대용량 전송, 음성, 비디오). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | 피어에 대한 연결이 활성 상태인지 확인합니다. |

**이벤트:**

| Event          | Signature | 설명 |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | 피어로부터 데이터가 도착하면 발생합니다. |

### 5.2. 전송 수단 선택 알고리즘

`TransportManager`는 다음을 기반으로 각 패킷에 최적의 전송 수단을 선택합니다:

1. **가용성:** `IsAvailable == true`인 전송 수단만 고려됩니다.
2. **페이로드 크기:** 페이로드 크기가 `BleMaxPayloadBytes` (1,024바이트) 이하이면 전력 효율을 위해 BLE가 선호됩니다. 더 큰 페이로드는 Wi-Fi Direct를 선호합니다.
3. **전력 비용 가중:** 가용한 전송 수단 중 일반 트래픽에는 `PowerCostRelative` 값이 낮은 것이 선호됩니다. 고우선순위 패킷 (SOS, 음성)은 이 선호도를 재정의할 수 있습니다.
4. **피어 연결성:** 전송 수단이 이미 목적지 피어에 대한 활성 연결을 보유하고 있으면 (`IsConnected`가 true를 반환) 연결 설정 오버헤드를 피하기 위해 선호됩니다.
5. **폴백:** 로컬 전송 수단이 목적지에 도달할 수 없으면 패킷은 AetherNetAPI를 통한 서버 릴레이를 위해 큐에 대기합니다.

### 5.3. 레퍼런스 전송 수단

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | 설명 |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | 기본 디스커버리 + 소형 패킷 |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | 대용량 전송, 스트리밍, 음성 |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon, 고처리량 |

**BLE 페이로드 한도:** 1,024바이트 (`BleMaxPayloadBytes`)를 초과하는 패킷은 자동으로 Wi-Fi Direct 또는 NearLink로 라우팅됩니다. BLE는 디스커버리 광고, 소형 제어 패킷 (RREQ/RREP, 프레즌스 비콘), 저대역폭 메시징에 사용됩니다.

**Wi-Fi Direct** 연결 타임아웃은 10,000 ms (`WifiDirectTimeoutMs`)이며 최대 8개의 동시 피어 (`MaxWifiDirectPeers`)를 지원합니다.

---

## 6. 디스커버리 프로토콜

### 6.1. BLE 광고

Aether 노드는 주로 BLE 광고를 통해 서로를 발견합니다. 정적 식별자를 통한 지속적인 추적을 방지하기 위해 프로토콜은 두 가지 프라이버시 메커니즘을 사용합니다: 서비스 UUID 교체와 아이덴티티 해결 키(IRK).

**광고 주기:** 2초 스캔 활성, 8초 비활성 (`BleScanOnMs`/`BleScanOffMs`). 광고 간격은 1,000 ms (`BleAdvertiseIntervalMs`). 타이밍 패턴 감지를 방지하기 위해 스캔 간격에 0~2,000 ms의 랜덤 지터 (`BleScanJitterMaxMs`)가 추가됩니다.

**피어 타임아웃:** 30초 이내에 재발견되지 않는 피어는 lost 상태로 간주됩니다 (`PeerLost` 이벤트).

### 6.2. 서비스 UUID 교체

장기적인 BLE 핑거프린팅을 방지하기 위해 광고에 사용되는 서비스 UUID는 15분마다 교체됩니다 (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key`는 노드당 한 번 생성되어 보안 저장소에 저장되는 32바이트 키입니다.
동일한 교체 키를 공유하는 모든 Aether 노드는 주어진 시간 창에서 동일한 UUID를 도출하여
영구 식별자를 노출하지 않고 상호 발견을 가능하게 합니다.

비교체 방식에서 전환 중인 90일 동안 정적 폴백 UUID (`A3E7-1001-0001-0000-000000000000`)가 유지됩니다.

### 6.3. 아이덴티티 해결 키 (IRK)

각 노드는 보안 저장소에 저장되는 128비트 아이덴티티 해결 키 (IRK)를 생성합니다. IRK는 키 교환 중 신뢰하는 피어와 공유됩니다.

**해결 가능 개인 주소 (RPA) 생성:**

1. `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3바이트) 계산.
2. `prand[0]`의 두 최상위 비트를 `01`로 설정 (BLE 규격의 RPA 플래그).
3. `hash = AES-128-ECB(IRK, pad(prand))`를 계산하되 `prand`는 16바이트 제로 패딩 입력의 13~15바이트를 차지합니다.
4. RPA 구성: `hash[0..2] || prand[0..2]` (총 6바이트).

**RPA 해결:** 피어의 IRK를 보유한 노드는 RPA의 `prand` 성분에서 해시를 재계산하여 관찰된 RPA가 해당 피어에 속하는지 확인할 수 있습니다. 해결 시간은 알려진 IRK 수 N에 대해 O(N)이며, 100개 피어 기준 약 0.1ms입니다.

RPA는 서비스 UUID와 동일한 15분 주기로 교체됩니다.

### 6.4. 지오해시 기반 근접성

노드는 선택적으로 위치를 지오해시로 인코딩합니다. 프라이버시를 위해 지오해시는 4자로 잘리며, 약 39km x 20km의 해상도를 제공합니다. 이 세분화는 다음에 충분합니다:

- 근접성 기반 채널 디스커버리
- DTN 전염성 라우팅 (수신자의 마지막 알려진 지오해시 영역을 향해 복제)
- SOS 경보 지리적 컨텍스트

전체 정밀도 지오해시는 메시를 통해 전송되지 않습니다. 잘린 형식만 공유되며, 노드의 프라이버시 수준이 허용하는 경우에만 공유됩니다 (`PrivacyLevel.Full` 또는 `PrivacyLevel.Partial`).

---

## 7. 보안 모델

### 7.1. 위협 모델

Aether는 다음 적 능력을 가정합니다:

- **수동 도청:** 적이 라디오 범위 내의 모든 BLE 광고 및 메시 트래픽을 관찰할 수 있습니다.
- **능동적 주입:** 적이 패킷을 주입, 수정 또는 재전송할 수 있습니다.
- **시빌 공격:** 적이 여러 가짜 노드 아이덴티티를 생성할 수 있습니다.
- **선택적 서비스 거부:** 적이 릴레이 노드로서 선택적으로 패킷을 폐기할 수 있습니다.

### 7.2. 보호되는 내용

| Property | Protection Level | Mechanism |
|----------|-----------------|-----------|
| 메시지 내용 | 완전한 기밀성 | 메시지별 키를 사용한 AES-256-GCM (§4.5) |
| 송신자 아이덴티티 | 부분적 | UHID가 패킷 헤더에 보임; BLE 주소는 교체됨 (§6) |
| 수신자 아이덴티티 | 부분적 | 라우팅된 패킷에서 목적지 UHID가 보임; 브로드캐스트 패킷은 목적지가 비어 있음 |
| 라우팅 메타데이터 | 최소 | 중간 노드는 소스/목적지 UHID와 TTL을 볼 수 있음 |
| 메시지 순서 | 보호됨 | 대칭 래칫의 카운터가 순서 변경을 방지 |
| 메시지 무결성 | 완전 | 모든 패킷에 Ed25519 서명 (v2) |

### 7.3. 공격 저항성

**재전송 공격:**
각 패킷은 8바이트 암호학적 랜덤 논스와 밀리초 정밀도 타임스탬프를 포함합니다. 릴레이 노드는 5분 TTL (`MaxPacketAgeSeconds = 300`)로 `(SenderUhid, NonceValue)` 쌍의 중복 제거 캐시를 유지합니다. 동일 송신자로부터 중복 논스를 가진 패킷은 폐기됩니다. 5분보다 오래된 타임스탬프가 있는 패킷은 논스에 관계없이 거부됩니다.

논스 중복 제거 캐시는 60초마다 정리됩니다. 만료된 항목 (5분 이상)은 제거됩니다.

**중간자 공격 (MITM):**
- 라우트 응답 패킷은 반드시 주장된 목적지 노드의 유효한 Ed25519 서명을 포함해야 합니다. 중간 노드는 목적지의 비공개 키를 보유하지 않으므로 RREP를 위조할 수 없습니다.
- 프리 키 번들에는 `SignedPreKey`에 대한 `SignedPreKeySignature` (Ed25519)가 포함되며, 에피머럴 ECDH 키를 장기 아이덴티티에 바인딩합니다.
- 세션 설정 (§4.4)은 프리 키 검증 단계를 통해 양 당사자의 아이덴티티에 암호학적으로 세션을 바인딩합니다.

**시빌 공격:**
- 각 노드의 신뢰성 점수는 50에서 시작하여 관찰된 동작에 따라 조정됩니다 (§3.5). 새로 생성된 시빌 노드는 축적된 평판이 없습니다.
- 낮은 신뢰성 점수 (0에 근접)를 가진 노드는 라우트 선택에서 우선순위가 낮아집니다.
- DTN 전염성 라우팅 알고리즘은 지오해시 근접성과 릴레이 성공 이력을 사용하여 복제 대상을 선택하므로, 시빌 노드가 실제 릴레이 기여 없이 트래픽을 유인하기 어렵습니다.

**플러딩 공격:**
- TTL은 각 홉에서 감소하며 TTL = 0인 패킷은 폐기됩니다. 기본 TTL 7은 어떤 브로드캐스트의 영향 반경도 제한합니다.
- 패킷 ID에 의한 RREQ 중복 제거는 브로드캐스트 폭풍을 통한 증폭을 방지합니다. 중복 제거 캐시는 `DeduplicationCacheSize` (기본 10,000)개 항목을 초과하면 초기화됩니다.
- SOS 브로드캐스트는 노드당 시간당 3회로 속도 제한됩니다 (§8).

### 7.4. 키 영점화

모든 중간 암호화 자료는 사용 직후 즉시 영점화됩니다:

- ECDH 키 합의의 `sharedSecret`: HKDF 파생 후 영점화.
- 체인 래칫의 `messageKey`: AES-GCM 암호화/복호화 후 영점화.
- 순서 없는 복호화의 `skippedKey`: 사용 후 영점화 및 맵에서 제거.
- 파생된 `RootKey`, `SendChainKey`, `RecvChainKey`: 설정 컨텍스트에서 영점화 (세션은 자체 복사본을 유지).

영점화는 컴파일러에 의해 최적화되지 않음이 보장된 `CryptographicOperations.ZeroMemory`를 사용합니다.

### 7.5. P-256에서 Ed25519로 마이그레이션

프로토콜은 ECDSA P-256 아이덴티티 키 (프로토콜 버전 1)에서 Ed25519 (프로토콜 버전 2)로의 30일 전환 창을 지원합니다:

1. 전환 기간 중 프로토콜 버전 1 패킷 (서명 없음)이 수락됩니다.
2. 서명 검증은 먼저 Ed25519를 시도합니다. 공개 키가 32바이트보다 길면 (DER 인코딩된 P-256 키를 나타냄) P-256 ECDSA 검증으로 폴백합니다.
3. 30일 창이 지나면 프로토콜 버전 1 패킷은 거부됩니다.
4. 마이그레이션하지 않은 노드는 새 Ed25519 아이덴티티로 재초기화해야 합니다.

### 7.6. 관할권 인식

프로토콜은 암호화 및 메시 네트워킹에 관한 다양한 법적 요구사항을 처리하기 위해 관할권 등급을 정의합니다:

| Tier | 동작 | Example Jurisdictions |
|------|----------|-----------------------|
| 1    | 자유롭게 운영 | South Africa, Kenya, Ghana |
| 2    | 수정된 운영 | Nigeria, India, EU, US, UK |
| 3    | 메시 전용 (고위험) | China, Russia, Iran, UAE, Myanmar |
| 4    | 알 수 없음 (기본 메시 전용) | 그 외 모든 국가 |

등급 선택은 기능 가용성에 영향을 줍니다 (예: 팁/금융 기능은 Tier 3에서 비활성화될 수 있음). 하지만 암호화 수준을 약화시키지는 않습니다. 종단 간 암호화는 관할권에 관계없이 항상 적용됩니다.

---

## 8. SOS 브로드캐스트

SOS 메커니즘은 사용자가 위험에 처해 근처 메시 피어 및/또는 인터넷에 동시에 연락해야 하는 상황을 위해 설계된 이중 경로 긴급 플러드입니다.

### 8.1. 브로드캐스트 파라미터

| Parameter | Value | 설명 |
|-----------|-------|-------------|
| TTL       | 15    | 일반 기본값 (7)의 두 배로, 더 넓은 전파를 보장합니다 |
| Priority  | 999   | 최대 우선순위; 릴레이 큐의 모든 다른 트래픽보다 선행합니다 |
| Rate limit| 3/hour| 남용 방지를 위한 노드당 제한 |
| Destination| empty | 모든 피어에게 브로드캐스트 (특정 목적지 없음) |

### 8.2. 플러드 알고리즘

1. 발신자는 `Type = SosBroadcast`, `TTL = 15`, `Priority = 999`, 빈 `DestinationUhid`로 SOS 패킷을 구성합니다.
2. 페이로드는 JSON으로 인코딩되며 다음을 포함합니다:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **이중 경로 전송:** SOS는 동시에 다음을 통해 전송됩니다:
   - **메시 플러드:** 모든 가용한 전송 수단을 통해 연결된 모든 피어에게 브로드캐스트.
   - **API 호출:** 서버 측 배포 및 PanikAPI 연결(SMS/이메일 전송)을 위해 AetherNetAPI로 전송.
4. 두 경로는 서로에 대해 fire-and-forget입니다. API 호출이 실패해도 메시 플러드는 독립적으로 진행됩니다.

### 8.3. 릴레이 동작

노드가 SOS 패킷을 수신하면:

1. 패킷 `Id`로 중복 여부를 확인합니다. 이미 수신한 경우 묵묵히 폐기합니다.
2. 페이로드를 역직렬화하고 로컬 UI를 위해 `SosReceived` 이벤트를 발생시킵니다.
3. 활성 경보 목록에 경보를 추가합니다.
4. `TTL > 1`이면 TTL을 감소시키고 라우팅 테이블 상태에 관계없이 **모든 피어에게 다시 브로드캐스트**합니다. SOS 패킷은 일반 라우팅을 우회하며 무조건 플러드됩니다.

### 8.4. 속도 제한

각 노드는 최근 브로드캐스트 타임스탬프의 슬라이딩 창을 유지합니다. 새 SOS 시작 전:

1. 큐에서 1시간보다 오래된 항목을 정리합니다.
2. 큐에 3개 이상의 항목이 있으면 (`MaxSosBroadcastsPerHour`) 브로드캐스트가 거부됩니다.
3. 성공적으로 전송되면 현재 타임스탬프를 큐에 추가합니다.

속도 제한은 SOS 브로드캐스트 발신에만 적용되며 릴레이에는 적용되지 않습니다.

### 8.5. SOS-PanikAPI 연결

메시를 통해 수신된 SOS 브로드캐스트는 전통적인 긴급 대응 (연락처에 SMS, 이메일 경보)을 위해 PanikAPI로 전달될 수 있습니다. 반대로 PanikAPI 긴급 세션은 커뮤니티 인식을 위해 메시에 브로드캐스트될 수 있습니다. 루프 방지는 소스 표시 (`direct` 대 `mesh_forward`) 및 메시 브로드캐스트의 `internet_forwarded` 플래그를 통해 달성됩니다.

---

## 9. DTN 저장 후 전달

DTN(Delay-Tolerant Networking) 하위 시스템은 송신자와 수신자 사이에 종단 간 경로가 없을 때 메시지 전달을 가능하게 합니다. 번들은 중간 노드에 저장되며 연결 상태가 변경될 때 기회적으로 전달됩니다.

### 9.1. 번들 포맷

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. 번들 생명 주기

1. **생성:** 송신자는 암호화된 페이로드 (수신자와의 Signal 세션을 통해 암호화)로 번들을 생성합니다. `Status = Pending`, `CopyCount = 1`.
2. **즉시 전달 시도:** 송신자는 먼저 직접 메시 라우팅 (RREQ/RREP)을 시도합니다. 라우트가 있으면 번들이 즉시 전달되고 `Status`가 `Delivered`로 전환됩니다.
3. **서버 릴레이 시도:** 메시 라우팅이 실패하면 송신자는 AetherNetAPI를 통한 릴레이를 시도합니다. 서버가 수신자에 도달할 수 있으면 (또는 메시지를 큐에 넣을 수 있으면) 전달에 성공합니다.
4. **저장 후 전달:** 메시와 서버 릴레이 모두 실패하면 번들은 다음 전달 스캔을 기다리며 로컬 저장소 (`Pending` 상태)에 남습니다.

### 9.3. 전달 스캔

주기적인 스캔이 60초마다 실행됩니다 (`DtnScanIntervalSeconds`):

1. SQLite(진실의 원천)에서 모든 대기 중인 번들을 로드합니다.
2. 각 대기 중인 번들에 대해:
   a. 수신자로의 메시 라우트를 시도합니다.
   b. 서버 릴레이를 시도합니다.
   c. 둘 다 실패하고 `CopyCount < MaxCopies`이면 전염성 복제를 시도합니다 (§9.4).
3. 만료된 번들 (`ExpiresAt <= now`)을 제거합니다.

### 9.4. 전염성 라우팅

직접 전달과 서버 릴레이 모두 실패하면 번들은 전염성 라우팅을 사용하여 근처 피어에게 복제됩니다:

1. `EpidemicRoutingService`는 현재 피어 목록에서 복제 대상을 선택합니다.
2. 대상 선택은 다음을 고려합니다:
   - **지오해시 근접성:** 수신자의 마지막 알려진 지오해시에 더 가까운 지오해시를 가진 피어가 선호됩니다.
   - **릴레이 이력:** 더 높은 신뢰성 점수를 가진 피어가 선호됩니다.
   - **복사본 예산:** `CopyCount >= MaxCopies` (기본: 3)에 도달하면 복제가 중단됩니다.
3. 각 복제는 선택된 피어에게 `DtnBundle` 패킷을 전송합니다.
4. 수신 시 피어의 DTN 서비스가 `AcceptCustodyAsync`를 호출합니다.

### 9.5. 커스터디 이전

노드가 다른 노드를 위한 DTN 번들을 수신하면:

1. **용량 확인:** 노드는 현재 번들 수를 `DtnMaxBundlesPerNode` (50)에 대해 확인합니다. 용량이 초과되면 커스터디가 거부됩니다.
2. **수락:** 번들 상태가 `InCustody`로 설정되고, 홉 수가 증가하며, 번들이 SQLite에 저장됩니다.
3. **커스터디 기록:** 이전을 문서화하는 `CustodyRecord`가 생성됩니다 (from, to, timestamp).
4. **복사본 수 증가:** 번들의 `CopyCount`가 영구 저장소에서 증가됩니다.
5. **확인 응답:** `Accepted = true`인 `DtnCustodyAck` 패킷이 이전 노드로 반환됩니다.
6. 수락 노드는 이후 스캔에서 전달을 시도할 책임을 갖습니다.

### 9.6. 전달 영수증

의도된 수신자가 DTN 번들을 수신하면:

1. 번들 상태가 `Delivered`로 업데이트됩니다.
2. `DtnDeliveryReceipt`가 메시 라우팅을 통해 (서버 릴레이 폴백과 함께) 원래 송신자에게 반환됩니다:
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. 영수증을 수신하면 송신자는 저장소에서 번들을 제거하고 `BundleDelivered` 이벤트를 발생시킵니다.
4. 영수증은 분석을 위해 AetherNetAPI에도 동기화됩니다.

### 9.7. 번들 만료

- 기본 번들 TTL은 72시간입니다 (`DtnBundleTtlHours`).
- 만료된 번들은 주기적인 전달 스캔 중에 정리됩니다.
- `Expired` 또는 `Delivered` 상태의 번들은 인메모리 캐시와 SQLite 모두에서 제거됩니다.

### 9.8. 용량 한도

| Parameter               | Default | 설명 |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | 최대 번들 수명 |
| `DtnMaxCopies`          | 3       | 네트워크 전체 번들당 최대 복사본 수 |
| `DtnMaxBundlesPerNode`  | 50      | 단일 노드가 보유할 최대 번들 수 |
| `DtnScanIntervalSeconds`| 60      | 전달 스캔 빈도 |

---

## 10. 비디오 스트리밍

> **2026-05-05 현재 상태 — 설계 + C# 스캐폴딩, 실제 코덱 파이프라인 없음.**
> 패킷 타입 `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32)는
> 와이어 정의가 완료되었으며 크로스 언어 픽스처 모음을 통해 왕복 검증되었습니다.
> C# `AetherNet.Streaming` 모듈은 인터페이스, 모델, 스켈레톤 서비스
> (`StreamingService`, `VideoCallService`, `WatchTogetherService`)를 제공하며
> 라우팅/DI 이음매와 유니캐스트 세그먼트 팬아웃을 연결합니다 — 하지만 실제
> 비디오 인코딩/디코딩은 연결되어 있지 않습니다. 나머지 7개 언어는 와이어 타입만 있습니다.
> `docs/adaptive-secure-streaming-spec.md`의 순방향 설계 문서가 목표 아키텍처입니다.
> 아래 산문은 해당 서비스가 구현할 내용의 사양으로 취급하시고,
> 프로덕션 준비 상태의 차이점은 `OPEN_ISSUES.md`를 참고하시기 바랍니다.

Aether는 세 가지 비디오 모드를 지원합니다: P2P 비디오 통화, 그룹 비디오 (동적 토폴로지를 가진 무제한 참가자), 라이브 브로드캐스트. 모든 비디오 프레임은 Signal 프로토콜로 암호화되고 Ed25519로 서명됩니다.

### 10.1. 전송 기능 매트릭스

비디오 통화를 시작하기 전에 발신자는 전송 계층에 쿼리하여 피어에 대한 최적 연결을 결정합니다. 전송 수단은 어떤 품질의 비디오가 가능한지를 결정합니다:

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | No (audio-only) | — | — | 64 Kbps | Sync packets only |
| NearLink | Light | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Full | 1080p | H.264 | 3000 Kbps | All modes |
| Internet | Full | 720p | H.264 | 1500 Kbps | All modes |
| CircleLink | No (audio-only) | — | — | 64 Kbps | Sync packets only |

사용 가능한 유일한 전송 수단이 BLE 또는 CircleLink인 경우 비디오 통화 서비스는 자동으로 음성 통화로 다운그레이드됩니다.

### 10.2. 비디오 코덱

| Enum Value | Codec | 사용 사례 |
|------------|-------|----------|
| 0 | H.264 | 기본값. 광범위하게 지원되며 압축률이 좋습니다. |
| 1 | H.265 | 더 나은 압축. 대역폭이 제한된 NearLink에 사용됩니다. |
| 2 | VP8 | 로열티 무료 대안. |

### 10.3. 비디오 해상도

| Enum Value | Resolution | Typical Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P 비디오 통화 흐름

1. **기능 확인**: 발신자가 `GetVideoCapabilityAsync(peerUhid)`를 쿼리하여 최적 전송 수단, 최대 해상도, 권장 코덱을 결정합니다.
2. **Offer**: 발신자가 선호 코덱, 최대 해상도, 최대 비트레이트를 포함하여 `SignalType = Offer`로 `VideoSignaling` 패킷 (타입 28)을 전송합니다.
3. **Answer/Reject**: 수신자가 `SignalType = Answer` (최소공배수 코덱으로 협상) 또는 `SignalType = Reject`로 응답합니다.
4. **활성 통화**: 두 노드가 H.264/H.265/VP8 NAL 유닛을 포함하는 `VideoCall` 패킷 (타입 27)을 교환합니다. 각 프레임에는 지터 버퍼 순서를 위한 시퀀스 번호와 키프레임 플래그가 포함됩니다.
5. **화면 공유**: 어느 쪽이든 화면 공유를 전환할 수 있습니다. `SignalType = ScreenShareStart/Stop`인 `VideoSignaling`이 피어에게 알립니다. 화면 공유 프레임은 `PacketType.ScreenShare` (타입 32)를 사용하지만 동일한 처리 파이프라인을 사용합니다.
6. **통화 종료**: 어느 쪽이든 `SignalType = Bye`로 `VideoSignaling`을 전송합니다.

모든 시그널링 및 프레임 페이로드는 Signal 프로토콜 (X3DH 세션)로 암호화됩니다. 암호화된 페이로드는 `MeshPacket.Payload` 필드 내에 JSON으로 인코딩된 `EncryptedPayload`로 직렬화됩니다.

### 10.5. 비디오 통화 상태 머신

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

상태: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. 그룹 비디오

그룹 비디오 세션은 무제한 참가자를 지원합니다. 토폴로지는 참가자 수에 따라 동적으로 선택됩니다:

- **FullMesh** (2~3명 참가자): 각 참가자가 다른 모든 참가자에게 하나의 스트림을 전송합니다. 단순하고 지연 시간이 낮습니다.
- **SFU** (4명 이상 참가자, 임계값: `SfuThresholdParticipants = 4`): 하나의 노드가 SFU 릴레이로 선출됩니다. 각 참가자가 릴레이에 하나의 스트림을 전송하고 릴레이가 모든 다른 참가자에게 배포합니다. 릴레이 노드는 인센티브 계층을 통해 팁을 받습니다.

토폴로지 전환은 자동입니다: 4번째 참가자가 참여하면 세션이 FullMesh에서 SFU로 전환됩니다. 참가자가 떠나 수가 4명 미만으로 줄어들면 다시 전환됩니다.

그룹 비디오 프레임은 `PacketType.VideoFrame` (타입 31)을 사용합니다. SFU 모드에서 프레임은 릴레이 노드의 UHID로 전송되며 릴레이가 다시 브로드캐스트합니다.

### 10.7. 지터 버퍼

비디오 지터 버퍼는 음성 지터 버퍼 (20ms Opus 프레임을 처리)와 독립적으로 작동합니다:

- **범위**: 최소 60ms, 최대 500ms.
- **적응형 깊이**: 지수 이동 평균 (EMA)으로 프레임 간 지터를 추적합니다. 버퍼 깊이 = 지터 추정치의 2배, [60, 500] ms 범위로 클램프됩니다.
- **키프레임 인식 폐기**: 버퍼가 오버플로될 때 비키프레임 (P/B) 프레임이 먼저 폐기됩니다. I-프레임 (키프레임)은 절대 폐기되지 않습니다 — 디코더 복구에 필요합니다.
- **갭 처리**: 시퀀스 갭이 감지되면 버퍼는 무기한 기다리지 않고 다음 가용한 키프레임으로 건너뜁니다.

### 10.8. 비디오 시그널링 타입

| Enum Value | Type | 설명 |
|------------|------|-------------|
| 0 | Offer | 코덱/해상도 선호도를 포함한 비디오 통화 시작 |
| 1 | Answer | 협상된 파라미터로 통화 수락 |
| 2 | Reject | 통화 거절 |
| 3 | Bye | 통화 종료 |
| 4 | Upgrade | 더 높은 품질 요청 (예: 전송 수단 개선) |
| 5 | Downgrade | 더 낮은 품질 요청 (예: 대역폭 감소) |
| 6 | ScreenShareStart | 피어가 화면 공유를 시작함 |
| 7 | ScreenShareStop | 피어가 화면 공유를 중단함 |

### 10.9. 암호화 모델

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| P2P 비디오 통화 | 프레임별 Signal 프로토콜 | X3DH 키 합의 |
| 그룹 비디오 | 그룹 채널 키 (AES-GCM) | 세션 생성 시 Signal 프로토콜을 통해 배포 |
| 화면 공유 | 부모 통화 모드와 동일 | 비디오 통화 세션에서 상속 |

---

## 11. Watch Together

> **2026-05-05 현재 상태 — 설계 + C# 스캐폴딩, §10과 동일한 성숙도.**
> 패킷 타입 `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34)는 와이어 정의가 완료되었으며
> 픽스처 테스트가 완료되었습니다. `AetherNet.Streaming.WatchTogetherService`는
> 조율 스켈레톤 (세션 상태, `IMeshSender`를 통한 동기화 명령 전파, RTT 보상 헬퍼)을 제공합니다;
> BitTorrent 수집, ChipIn SDPKT 정산, 피어로부터의 청크 가져오기는
> 어느 언어에서도 구현되지 않았습니다. 아래 산문은 목표 프로토콜로 취급하시고,
> `docs/adaptive-secure-streaming-spec.md`의 순방향 설계 문서에서 더 자세한 내용을 확인하시기 바랍니다.

Watch Together는 메시 피어 그룹 간의 동기화된 미디어 재생을 가능하게 합니다. 호스트는 재생 (재생, 일시 정지, 탐색, 속도)에 대한 독점적 제어권을 갖습니다. 동기화 명령에는 RTT 보상을 위한 벽시계 타임스탬프가 포함됩니다.

### 11.1. Watch 모드

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | 동기화 패킷만 (각 100바이트 미만) | 어떤 것이든 가능 (BLE에서도 작동) |
| 1 | StreamFromHost | P2P 청크 전송 (P2pContentService 재사용) | WiFi Direct 또는 Internet |
| 2 | BitTorrent | 게이트웨이 노드를 통한 메시 + 외부 스웜 | WiFi Direct 또는 Internet |

### 11.2. SharedFile 모드

두 참가자 모두 동일한 파일을 보유합니다 (SHA-256 내용 해시로 일치). `WatchSync` 패킷만 교환됩니다. 이것이 가장 대역폭 효율적인 모드이며 BLE에서도 작동합니다.

1. 호스트가 `contentHash` (파일의 SHA-256)로 watch 세션을 생성합니다.
2. 참가자들이 참여하고 플레이어가 로드되면 `IsReady = true`를 보고합니다.
3. 모든 참가자가 ready를 보고하면 세션이 시작됩니다.
4. 호스트가 `WatchSync` 패킷 (타입 29)으로 재생/일시정지/탐색/속도 명령을 전송합니다.
5. 수신자는 RTT 보상을 적용합니다: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. StreamFromHost 모드

호스트만 파일을 보유합니다. 호스트는 `ContentManifest`를 생성하고 (P2P 콘텐츠 시스템 재사용) 참가자들이 메시를 통해 청크를 다운로드합니다.

- 청크 선택은 `SequentialFromPosition` 전략을 사용합니다 (`RarestFirst` 아님): 현재 재생 위치 앞의 청크를 우선시하고 시딩을 위해 나머지를 백필합니다.
- 버퍼 목표: 30초 앞 (`WatchTogetherBufferAheadSeconds`).
- 자동 일시 정지: 어느 참가자의 버퍼라도 10초 미만으로 떨어지면 (`WatchTogetherMinBufferSeconds`) 세션이 모든 참가자를 `BufferUnderrun` 동기화 명령으로 자동 일시 정지합니다. 모든 참가자가 충분한 버퍼를 확보하면 재생이 재개됩니다 (`BufferReady`).
- 뷰어가 청크를 다운로드하면 다른 뷰어를 위한 시더가 됩니다 (메시 내 BitTorrent 방식 스워밍).

### 11.4. BitTorrent 모드

참가자가 그룹 채팅에서 `.torrent` 파일이나 마그넷 링크를 공유합니다. `TorrentMetadata` 패킷 (타입 34)이 모든 세션 참가자에게 토렌트 정보를 배포합니다.

**메시-스웜 연결:**
- 게이트웨이 노드 (인터넷을 가진 노드)가 외부 BitTorrent 스웜에서 피스를 다운로드합니다.
- 게이트웨이 노드가 다운로드된 피스를 메시 배포를 위해 재암호화하고 메시 피어에게 시드합니다.
- 인터넷이 없는 메시 피어는 게이트웨이 노드와 서로에게서 피스를 받습니다.
- P2P 콘텐츠 엔진이 BitTorrent의 피스 모델과 Aether의 청크 모델 간을 변환합니다.

충분한 콘텐츠가 버퍼링되면 watch-together 재생이 SharedFile 모드와 동일한 동기화 프로토콜을 사용하여 시작됩니다.

### 11.5. Watch 세션 상태 머신

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

상태: `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. 동기화 명령 타입

| Enum Value | Type | 설명 |
|------------|------|-------------|
| 0 | Play | 지정된 위치에서 재생 재개 |
| 1 | Pause | 지정된 위치에서 일시 정지 |
| 2 | Seek | 지정된 위치로 이동 |
| 3 | Speed | 재생 속도 변경 |
| 4 | BufferUnderrun | 자동 일시 정지 — 참가자의 버퍼가 치명적으로 낮음 |
| 5 | BufferReady | 재개 — 모든 참가자가 충분한 버퍼를 보유함 |

### 11.7. RTT 보상

동기화 명령에는 `WallClockMs` 필드 (Unix 에포크 밀리초)가 포함됩니다. 수신자가 동기화 명령을 처리할 때:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Play 및 BufferReady 명령의 경우: `adjustedPosition = commandPosition + networkDelay`
4. Pause 및 Seek 명령의 경우: 위치를 그대로 적용합니다 (재생이 중단/이동하므로 조정 불필요).

이를 통해 모든 참가자가 네트워크 RTT의 절반 이내로 동기화됩니다.

### 11.8. 반응

참가자는 재생 중 콘텐츠에 반응할 수 있습니다:

- **이모지 반응**: `Type = Emoji`인 `WatchReaction` 패킷 (타입 30)으로, 이모지 문자열과 반응 시점의 미디어 위치를 포함합니다.
- **음성 댓글**: `Type = VoiceComment`인 `WatchReaction` 패킷으로, Opus로 인코딩된 오디오 데이터를 포함합니다 (최대 10초). 음성 데이터는 반응의 `VoiceData` 필드에 포함됩니다.

반응은 모든 세션 참가자에게 브로드캐스트됩니다. 반응에는 미디어 위치의 타임스탬프가 붙어 재생 동기화 표시를 가능하게 합니다.

### 11.9. ChipIn — 그룹 콘텐츠 획득

ChipIn은 그룹 멤버들이 공동 시청을 위한 콘텐츠를 공동으로 구매하기 위해 자금을 모을 수 있도록 합니다 (ZAR 단위, LedgerAPI를 통해 SDPKT 지갑으로 정산).

**상태 머신:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

상태: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**흐름:**
1. 발신자가 목표 금액과 콘텐츠 설명으로 `ChipInPool`을 생성합니다.
2. 참가자들이 SDPKT 지갑 거래를 통해 금액을 기여합니다.
3. `CollectedAmount >= TargetAmount`가 되면 상태가 `Funded`로 전환됩니다.
4. 시스템이 콘텐츠를 획득합니다 (예: BitTorrent 다운로드 시작).
5. 콘텐츠가 가용하게 되면 상태가 `Acquired`로 전환되고 watch-together가 시작될 수 있습니다.

각 기여는 감사 추적을 위해 SDPKT 거래 ID와 함께 기록됩니다.

### 11.10. 암호화 모델

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| Watch 동기화 명령 | 채널/대화 키 | 기존 Signal 프로토콜 세션 |
| 콘텐츠 청크 (StreamFromHost) | 매니페스트당 콘텐츠 키 | Signal 프로토콜을 통해 배포 |
| BitTorrent 피스 | 수집 시 재암호화 | 게이트웨이가 스웜에서 평문으로 다운로드 후 메시를 위해 암호화 |
| Watch 반응 | 세션 키 | 대화 키에서 파생 |

### 11.11. 기능 플래그

모든 비디오 및 watch-together 기능은 기능 플래그 뒤에 게이트됩니다 (기본적으로 모두 비활성화):

| Flag | Parent | 설명 |
|------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | P2P 및 그룹 비디오 통화 |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | 다중 참가자 비디오 세션 |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | 비디오 통화 중 화면 공유 |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | 동기화된 미디어 재생 |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | 이모지 및 음성 반응 |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | 메시 배포를 위한 BitTorrent 파일 수락 |

기능 플래그에는 부모 종속성이 있습니다: 자식 플래그는 부모도 활성화된 경우에만 활성화할 수 있습니다. 이를 통해 점진적인 롤아웃이 가능합니다.

---

## 12. 보안 및 프라이버시 계층

> 2.3.0에서 추가됨. 레퍼런스 구현: `src/AetherNet.Security/Backup/` (복구 문구), `src/AetherNet.Security/Privacy/` (BLE 추적 방지, 패닉 와이프), 그리고 `src/AetherNet.Security/Sync/` (다중 장치 동기화). 언어 간 바이트 벡터: `fixtures/bip39/`, `fixtures/bleprivacy/`, `fixtures/panicwipe/`, `fixtures/sync/`.

이 계층은 부가적이며 §2의 패킷 스위트와 독립적입니다. **다중 장치 동기화**(§12.1–12.2)와 **BLE 추적 방지 주소 방식**(§12.3)만이 바이트 / 온에어 형식을 가집니다. **복구 문구 백업**(§12.4)과 **패닉 와이프**(§12.5)는 로컬 전용이며 완전성을 위해 여기에 명시합니다. 이들은 모두 8개 언어 전체에서 바이트 단위로 동일하게 구현되어 있으며, 유일한 예외는 §12.1에서 언급한 Ed25519 서명입니다.

### 12.1. DeviceLink (장치 페어링)

`DeviceLink`는 어떤 장치의 공개 키가 특정 아이덴티티에 속함을 나타내는 Ed25519 서명된 어서션으로, 다중 장치 동기화를 위해 사용자 자신의 장치를 페어링하는 데 사용됩니다. **서명 대상 본문**은 다음과 같습니다:

| Off | Field | Type | Size | 설명 |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01`. 읽을 때 그 외의 값은 거부 |
| 1 | device_id_len | uint16, little-endian | 2 | `device_id`의 UTF-8 바이트 길이 |
| 3 | device_id | UTF-8 bytes | N | 연결되는 장치의 식별자 |
| 3+N | device_public_key | bytes | 32 | 연결되는 장치의 Ed25519 공개 키 |
| 35+N | issued_at_ms | int64, little-endian | 8 | Unix 에포크 밀리초 |

직렬화된 `DeviceLink`는 서명 대상 본문에 이어 그 본문에 대한 **64바이트 Ed25519 서명**이 뒤따르며, *아이덴티티* 개인 키로 계산됩니다. 검증은 본문을 재계산하고 아이덴티티 공개 키에 대해 서명을 확인합니다.

> **서명 바이트 일치 예외.** 서명 대상 본문과 검증 결과는 8개 언어 전체에서 동일하며, 64개의 서명 **바이트**는 그중 7개 언어에서 바이트 단위로 동일합니다. Apple의 CryptoKit은 Ed25519 서명을 무작위화하므로(RFC 8032 §8의 헤지 서명), Swift 서명은 호출할 때마다 다르지만 유효하고 상호 검증 가능한 상태를 유지합니다. 상호 운용성은 서명 바이트 비교가 아니라 반드시 *검증*에 의존해야 합니다.

### 12.2. SyncRecord (마지막 쓰기 우선 동기화 엔벨로프)

`SyncRecord`는 사용자 자신의 다중 장치 상태에 대한 하나의 복제된 변경으로, 마지막 쓰기 우선으로 조정됩니다. 레코드는 기존 DTN/메시 경로 내부에서 E2E 암호화되어 이동합니다(`encrypted_payload`는 불투명한 암호문). 이들은 새로운 `MeshPacket` 타입이 **아닙니다**.

| Off | Field | Type | Size | 설명 |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01` |
| 1 | record_id | UUID, RFC 4122 big-endian | 16 | §2.1과 동일한 big-endian 규약 |
| 17 | op | uint8 | 1 | `0`=Upsert, `1`=Delete, `2`=Read. 2보다 큰 값은 거부 |
| 18 | logical_clock | int64, little-endian | 8 | 장치별 단조 카운터 |
| 26 | created_at_ms | int64, little-endian | 8 | Unix 에포크 밀리초 |
| 34 | device_id_len | uint16, little-endian | 2 | UTF-8 바이트 길이 |
| 36 | device_id | UTF-8 bytes | N | 발신 장치 |
| 36+N | item_id_len | uint16, little-endian | 2 | UTF-8 바이트 길이 |
| 38+N | item_id | UTF-8 bytes | M | 동기화되는 논리 키 |
| 38+N+M | payload_len | int32, little-endian | 4 | 암호문 길이. 음수 값은 거부 |
| 42+N+M | encrypted_payload | bytes | payload_len | 불투명한 E2E 암호문 |

**조정 (마지막 쓰기 우선).** 동일한 `item_id`에 대한 두 레코드 사이에서, 하나가 달라질 때까지 순서대로 비교하여 승자를 선택합니다: `created_at_ms`, 그다음 `logical_clock`, 그다음 `device_id`(서수 바이트 비교), 그다음 `record_id`(big-endian 바이트 비교). 이 순서는 전순서이고 결정적이므로, 도착 순서와 무관하게 모든 장치가 동일한 승자로 수렴합니다.

### 12.3. BLE 추적 방지

두 가지 유도를 통해 장치는 수동 스캐너에 추적되지 않으면서 애드버타이즈할 수 있습니다. 둘 다 `fixtures/bleprivacy/`에 고정된 순수 함수이며, 이들을 온에어로 방출하는 것은 호스트 BLE 스택의 역할입니다.

- **로테이팅 서비스 UUID.** `window = floor(unix_time_seconds / 900)` (15분 에포크). 애드버타이즈되는 128비트 서비스 UUID는 `HMAC-SHA256(ble_rotation_key, LE_int64(window))`의 처음 16바이트입니다. UUID를 기록하는 스캐너는 로테이션 키 없이는 두 윈도우를 연결할 수 없습니다.
- **해결 가능 비공개 주소 (RPA).** Bluetooth의 `ah` 함수를 따릅니다: `hash = ah(IRK, prand)`. 여기서 `ah`는 24비트 `prand`(128비트로 패딩)에 대한 AES-128이며 하위 24비트를 취합니다. 48비트 주소는 `hash(24) || prand(24)`이고, `prand`의 상위 2비트를 `0b01`로 설정하여 해결 가능함을 표시합니다. IRK를 보유한 피어는 `ah`를 재계산하고 해시를 비교하여 주소를 해결합니다.

### 12.4. 복구 문구 백업 (로컬)

아이덴티티는 Ed25519 키 쌍이며, 그 32바이트 개인 시드(256비트)는 표준 SHA-256 체크섬과 함께 공식 영어 워드리스트 상의 **24단어 BIP-39** 니모닉으로 인코딩됩니다(잘못 입력된 단어는 체크섬에 실패하여, 조용히 다른 아이덴티티를 생성하는 대신 거부됩니다). 이는 표준 BIP-39이며——공식 Trezor 테스트 벡터에 대해 검증되고 8개 언어 전체에서 바이트 단위로 재현됩니다——따라서 이 문구는 서버나 커스터디언 없이 어떤 장치에서든 아이덴티티를 복원합니다. 와이어 형식은 없습니다. 문구는 네트워크에 결코 닿지 않습니다.

### 12.5. 패닉 와이프 (로컬)

강압 하에서, **강압 PIN**——저장된 `SHA-256(pin)`에 대해 상수 시간으로 비교됩니다——은 모든 아이덴티티 키 자료의 안전한 소거를 트리거합니다: 각 버퍼는 무작위 바이트로 덮어쓴 후 0으로 채워지며, 아이덴티티 키 이름의 고정된 매니페스트(아이덴티티 키 쌍, 장치 솔트, DRK, 그리고 §12.3의 BLE 로테이션 키 / IRK)에 걸쳐 수행됩니다. 와이어 형식은 없습니다. 이 작업은 완전히 장치 로컬입니다.

---

## 부록 A: 상수 참조

모든 프로토콜 상수는 `ProtocolConstants`에 정의되어 있으며 참조를 위해 여기에 재현합니다:

### 라우팅
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE 디스커버리
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherNetBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### 보안
| Constant                  | Value  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constant                   | Value |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constant                  | Value  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### 전송
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### 하트비트
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### 프레즌스
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### 음성
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### 스트리밍
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### 비디오
| Constant                       | Value |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

---

## 부록 B: 용어 사전

| Term | Definition |
|------|------------|
| **UHID** | Universal Hardware Identifier. 메시 노드를 식별하는 고유 문자열로, 장치 아이덴티티와 암호화 키에서 파생됩니다. |
| **RREQ** | Route Request. 목적지 노드로의 경로를 탐색하는 데 사용되는 브로드캐스트 패킷. |
| **RREP** | Route Reply. RREQ에 의해 설정된 역방향 라우트를 따라 반환되는 유니캐스트 패킷. |
| **IRK** | Identity Resolving Key. BLE 해결 가능 개인 주소를 생성하고 해결하는 데 사용되는 128비트 키. |
| **RPA** | Resolvable Private Address. 주기적으로 교체되지만 송신자의 IRK를 보유한 피어가 해결할 수 있는 6바이트 BLE 주소. |
| **X3DH** | Extended Triple Diffie-Hellman. 비동기 세션 설정을 가능하게 하는 키 합의 프로토콜. |
| **DTN** | Delay-Tolerant Networking. 간헐적 연결 환경을 위한 저장-후-전달 패러다임. |
| **Gateway** | 인터넷 연결을 가진 메시 노드로, 메시 트래픽과 IP 기반 서비스 간을 연결합니다. |
| **HKDF** | HMAC-based Key Derivation Function. 단일 공유 비밀에서 여러 키를 파생하는 데 사용됩니다. |
| **Pre-key bundle** | 수신자가 온라인 상태가 아니더라도 송신자가 암호화된 세션을 설정할 수 있도록 게시된 키 집합. |
| **SFU** | Selective Forwarding Unit. 각 송신자로부터 하나의 비디오 스트림을 수신하고 다른 모든 참가자에게 배포하여 노드당 업로드 대역폭을 줄이는 릴레이 노드. |
| **ChipIn** | 참가자들이 공동 시청을 위한 콘텐츠를 공동으로 구매하기 위해 SDPKT 자금을 모으는 그룹 자금 조달 메커니즘. |
| **NAL** | Network Abstraction Layer. 비디오 프레임을 패킷화하기 위해 H.264 및 H.265 코덱에서 사용하는 캡슐화 포맷. |

---

## 부록 C: 참고 문헌

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
