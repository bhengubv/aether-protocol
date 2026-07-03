# AetherNet — 오프라인 우선 메시 네트워킹 프로토콜

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet은 오픈소스 MIT 라이선스 메시 네트워킹 프로토콜**로, **인터넷, 서버, 회원가입 없이** 근처 사람들에게 메시지, 파일, 음성, 영상을 전송합니다. 기기들은 블루투스, Wi-Fi Direct, NearLink, LoRa를 통해 직접 연결됩니다; 수신자가 범위 밖에 있을 때 메시지는 다른 기기를 통해 홉하며 경로를 최대 72시간까지 기다립니다. **8개 프로그래밍 언어로 바이트 단위로 동일한 구현체**를 제공합니다 — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, C.

가까운 사람들과 파일, 메시지, 스트림을 공유하세요. Wi-Fi 불필요. 모바일 데이터 불필요. 회원가입 불필요. AirDrop과 비슷하지만, 모든 플랫폼의 모든 사람과 함께 작동합니다.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **하나의 프로토콜, 8개 언어, 와이어에서 동일합니다.** Aether는 **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, C**로 구현되어 있으며 — 모든 패킷이 이들 전체에 걸쳐 바이트 단위로 동일하고, CI의 공유 언어 간 픽스처 코퍼스로 강제됩니다. 8개 중 어느 언어로든 노드를 구축하세요; 나머지 모두와 상호 운용됩니다. 이 README는 11개의 인간 언어로도 제공됩니다 (위 링크).

## 무엇을 할 수 있나요?

**데이터 소비 없이 강의 노트를 공유하세요.**

스터디 그룹에서 누군가 스마트폰에 기출문제를 갖고 있습니다. Aether는 핫스팟, WhatsApp 그룹, 파일 크기 제한 없이 블루투스를 통해 해당 파일을 여러분의 기기로 직접 전송합니다. 그룹 내 누군가 범위 밖에 있어도 파일은 다른 기기를 통해 목적지에 도달할 때까지 전달됩니다. 메시지는 필요 시 최대 72시간 동안 경로를 기다립니다.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**주변에서 무슨 일이 일어나는지 확인하세요.**

캠퍼스 행사나 축제에 있습니다. Aether는 블루투스와 Wi-Fi Direct를 통해 주변 기기를 검색합니다. 앱 피드도 알고리즘도 없이 실제로 주변에 있는 것들을 볼 수 있습니다.

**신호가 없을 때 SOS를 보내세요.**

휴대폰에 수신 신호가 없습니다. Aether는 범위 내 모든 기기에 긴급 메시지를 방송하고, 해당 기기들이 이를 계속 전달합니다. 기지국이 필요하지 않습니다.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**비공개 그룹 채널을 만드세요.**

기숙사 층, 동아리, 프로젝트 팀을 위한 채널입니다. 인증된 구성원만 메시지를 읽거나 보낼 수 있습니다. 어떤 서버에도 대화 내용이 저장되지 않습니다.

**주변 사람들에게 물건을 판매하세요.**

교재를 판매 목록에 올리세요. 메시 범위 내를 지나가는 사람들이 볼 수 있습니다. 마켓플레이스 계정도, 수수료도 없이 — 오직 근접성만 있습니다.

**메시를 통해 함께 영화를 보세요.**

그룹 영화의 밤. 누군가 파일을 갖고 있습니다. Aether는 모든 기기에서 재생을 동기화합니다 — 재생, 일시정지, 탐색 — 모두 완벽하게 맞춰집니다. 파일을 갖고 있지 않은 사람이 있다면 메시가 P2P 스트림으로 실시간 배포합니다. 아무도 파일을 갖고 있지 않다면 모두 SDPKT로 함께 구매합니다.

## 작동 원리

기기들은 블루투스, Wi-Fi Direct, 또는 NearLink를 통해 직접 서로 통신합니다. 인터넷 연결, 서버, 중앙 인프라가 필요하지 않습니다.

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ End-to-End Encrypted (Signal Protocol) ======>|
       |                                                             |
       |  No internet. No servers. No ISP. Just devices talking.     |
```

메시지가 목적지에 직접 도달할 수 없는 경우, 다른 기기를 통해 전달됩니다. 중계 기기는 전달 중인 내용을 읽을 수 없습니다 — 모든 메시지는 AES-256-GCM으로 암호화됩니다. 모든 패킷은 Ed25519 신원 키로 서명되며, 위조된 패킷은 네트워크에서 폐기됩니다.

> **보안 성숙도 참고 사항 (출시 전 필독):** 실제 X3DH (4개의 X25519 DH), 완전한 Signal Double Ratchet (수신 시 DH-회전 단계, KDF_RK, 0x01/0x02 체인 래칫), 1회용 사전 키 풀 (기본 100개 OPK, FIFO, 잠금 보호)이 **8개 언어 모두**에 구현되어 있으며 `fixtures/signal/` 아래의 공유 언어 간 픽스처 코퍼스에 고정되어 있습니다. 남은 유일한 미결 사항은 실제 BLE 하드웨어에서의 물리적 RF 가동 테스트이며, `OPEN_ISSUES.md`에서 추적하고 있습니다.

계정, 전화번호, 이메일 모두 필요하지 않습니다. 키 쌍을 생성하면 네트워크에 참여할 수 있습니다.

```
  ┌─────────────────────────────────┐
  │         Your Application        │
  ├─────────────────────────────────┤
  │ Messaging · Streaming · Voice   │
  │ Video · Watch Together          │
  ├─────────────────────────────────┤
  │  Security: AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
  ├─────────────────────────────────┤
  │  Routing: AODV + DTN            │
  ├─────────────────────────────────┤
  │  Transport: BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**라우팅** — 서명된 경로 응답을 사용하는 AODV. 모든 경로 응답은 목적지의 Ed25519 키로 서명되므로, 어떤 기기도 자신이 아닌 목적지인 척할 수 없습니다.

**저장-전달(Store-and-forward)** — 활성 경로가 없을 때 패킷은 경로가 열릴 때까지 최대 72시간 동안 보관됩니다.

**전송 선택** — 프로토콜은 패킷별로 적합한 전송 수단을 선택합니다. 소규모 제어 메시지는 BLE로 전송됩니다. 대용량 파일 전송은 Wi-Fi Direct를 사용합니다. 가능한 경우 NearLink를 사용합니다.

**음성, 영상, 스트리밍** — 코덱 협상을 사용하는 영상 통화 (H.264/H.265/VP8), 전송 방식에 따른 품질 선택, 자동 SFU 중계를 통한 그룹 영상, RTT 보상을 포함한 동기화된 함께 보기, 그리고 어댑티브 비트레이트 스트리밍.

**재생 보호** — 5분 타임스탬프 신선도 창을 사용하는 난스(Nonce) 중복 제거.

## 제공되는 것 — 모든 서비스, 모든 언어로

Aether는 단순한 전송 수단이 아닙니다. 프로토콜이 예약한 모든 패킷 타입은 이제 **8개 언어 모두에서 실제로 작동하는 서비스**이며, 모든 것이 **바이트 단위로 동일한 와이어 패킷**으로 직렬화됩니다 — Go 노드가 구축한 패킷은 Swift, Rust, C, Python, TypeScript, Kotlin, C# 노드가 변경 없이 디코딩합니다. 각 서비스는 `fixtures/<service>/` 아래의 공유 언어 간 픽스처에 고정되어 있으며 언어별 단위 테스트로 검증되고, Swift와 C는 macOS 빌드 서버에서 추가로 검증됩니다.

| 기능 | 하는 일 | 패킷 타입 | 픽스처 | 8/8 |
|---|---|:-:|---|:-:|
| **프레즌스 비콘 & 쿼리** | "나 여기 있어" 알림과 "누가 주변에 있지?" 문의를 — 실제 신원이 아닌 **회전하는 키 유도 임시 ID**와 대략적인 지오해시로 수행 | 21, 22 | `fixtures/presence/` | ✅ |
| **하트비트** | 연결된 피어 간 경량 생존 유지 신호 | 10 | `fixtures/heartbeat/` | ✅ |
| **프로필 동기화** | 서명된 프로필 카드를 메시를 통해 피어와 교환 | 23 | `fixtures/profiles/` | ✅ |
| **임시 ID 알림** | 라우팅 ID가 회전한 후에도 친구가 여러분에게 도달할 수 있도록 현재 회전 중인 라우팅 ID를 비공개로 알림 | 56 | `fixtures/erid/` | ✅ |
| **사전 키 교환** | 한 번도 만난 적 없는 상대와 종단 간 세션을 부트스트랩하기 위해 Signal 사전 키 번들을 메시를 통해 요청하고 전달 | 25, 26 | `fixtures/prekey/` | ✅ |
| **채널** | 비공개, 구성원 전용 그룹 채널에 대한 서명된 메시지 | 7 | `fixtures/channels/` | ✅ |
| **푸시-투-토크** | 워키토키 음성 프레임 (불투명 인코딩 오디오 페이로드) | 15 | `fixtures/media/` | ✅ |
| **화면 공유** | 화면 공유 영상 프레임 (불투명 인코딩 영상 페이로드) | 32 | `fixtures/media/` | ✅ |
| **통화 제어** | 음성 및 영상 통화를 위한 벨울림 / 수락 / 거절 / 종료 시그널링 | 27 | `fixtures/videocall/` | ✅ |
| **SOS 확인** | 긴급 방송이 수신되었음을 발신자에게 확인 | 6 | `fixtures/sos/` | ✅ |
| **스페이스 브레드크럼** | "내 주변에 무엇이 있나" 계층을 위한 위치 태그가 지정된 검색 부스러기 | 40 | `fixtures/space/` | ✅ |
| **포지 알림** | 유도/포지된 콘텐츠 아티팩트를 메시에 광고 | 41 | `fixtures/forge/` | ✅ |
| **볼트 샤드 요청** | 소거 코드화된 저장 샤드를 가져오기 (N개 샤드 중 임의의 K개로 파일 재구성) | 42 | `fixtures/vaultshard/` | ✅ |
| **대역폭 측정** | 메시가 가장 굵은 파이프로 라우팅하도록 링크 처리량을 프로브 / 확인 / 가십 (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

이들은 이미 완성된 **메시징, 1:1 및 그룹 음성, 영상 통화, 라이브 스트리밍, 함께 보기, AODV 라우팅, DTN 저장-전달, SOS 플러드** 서비스 위에 놓입니다 — 이 또한 8개 언어 모두에 구현되어 있습니다.

> **여기서 "구축됨"이 정확히 무엇을 의미하는지.** 각 서비스는 자신의 와이어 패킷을 생성하고 처리하며, 올바른 이벤트를 발생시키고, 전체 언어 제품군이 일치해야 하는 바이트 수준 픽스처에 고정됩니다. 여러분의 애플리케이션은 서비스를 자신의 Signal 세션, 라우팅 테이블, 로컬 상태에 연결합니다. 이것은 프로토콜 계층입니다 — 코드, 테스트, 언어 간 바이트 픽스처로 입증된 — 그리고 다른 모든 것과 동일하게 정직한 RF 기반 위에 있습니다: 궁극적으로 라디오를 타는 모든 경로는 `OPEN_ISSUES.md`에서 추적되는 하드웨어 가동이 이루어지기 전까지 현장 미검증 상태입니다.

## 보안 및 프라이버시

와이어 서비스 제품군을 넘어, Aether는 작은 **보안 및 프라이버시 계층**을 함께 제공합니다 — 신원 키 관리와 링크 계층 추적 방지입니다. 다른 모든 것과 마찬가지로, 각각은 **8개 언어 모두**에 구현되어 있으며 `fixtures/<feature>/` 아래의 공유 언어 간 픽스처에 고정됩니다(Swift와 C는 macOS 빌드 서버에서 추가로 검증됨). 이들은 18개 와이어 서비스에 네 개가 더해진 것이 *아닙니다*: 그중 셋은 **새로운 와이어 패킷 타입을 전혀 정의하지 않으며**, 넷째는 새로운 예약 패킷이 아니라 **기존 DTN/메시 경로 내부에** 자신의 봉투를 실어 나릅니다.

| 기능 | 하는 일 | 계층 | 픽스처 | 8/8 |
|---|---|---|---|:-:|
| **복구 문구 백업** | 신원을 **24개 단어 BIP-39** 문구로 백업하고 어떤 기기에서든 복원합니다. 표준 BIP-39(공식 Trezor 벡터로 검증됨)이며, SHA-256 체크섬이 적용되어 잘못 입력한 단어는 조용히 틀리는 일 없이 *거부*됩니다. 서버도 수탁자도 없습니다 — 문구가 **곧** 신원입니다. | 로컬 | `fixtures/bip39/` | ✅ |
| **블루투스 추적 방지** | 회전하는 키 유도 BLE **서비스 UUID**(HMAC-SHA256, 15분 창)와 **해석 가능한 개인 주소**(IRK + RFC의 `ah` 함수, AES-128)를 유도합니다 — 수동 스캐너가 시간이나 장소를 가로질러 연결하지 못하도록 BLE 광고자가 필요로 하는 추적 방지 자료입니다. | 링크 계층 | `fixtures/bleprivacy/` | ✅ |
| **패닉 와이프** | 강요 상황에서 모든 신원 키를 안전하게 지우는 **강요 PIN**(SHA-256, 상수 시간 비교) — 무작위 값으로 덮어쓴 뒤 0으로 채움 — 복구할 것을 아무것도 남기지 않습니다. | 로컬 | `fixtures/panicwipe/` | ✅ |
| **다중 기기 동기화** | 여러분 *자신의* 기기 간 **탈중앙화, 서버리스** 동기화: Ed25519로 서명된 **DeviceLink**가 기기들을 페어링하고, 마지막-쓰기-우선 **SyncRecord** 봉투가 상태를 조정합니다 — 기존 DTN/메시 위에서 종단 간 암호화되어 전달되며, 클라우드 계정도 동기화 서버도 없습니다. | DTN 위에서 동작 | `fixtures/sync/` | ✅ |

**하나의 정직한 비대칭.** 다중 기기 `DeviceLink`는 Ed25519로 서명되며, 그 서명은 **8개 언어 중 7개에서 바이트 단위로 동일**합니다. Apple의 CryptoKit은 Ed25519 서명을 의도적으로 *무작위화*하므로 Swift에서는 그 64바이트 서명이 매번 다릅니다 — 하지만 **서명되는 본문은 바이트 단위로 동일**하고 모든 링크는 여전히 8개 SDK 전부에서 검증되므로, Swift는 서명 바이트 대등이 아니라 **검증** 대등에 도달합니다. 이는 플랫폼 암호의 성질이지 결함이 아니며, 이 네 기능을 통틀어 "바이트 단위로 동일"에 별표가 붙는 유일한 지점입니다. 전체 와이어 형식은 [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12에 있으며, 위협 모델은 [`THREAT_MODEL.md`](THREAT_MODEL.md)에 있습니다.

## 전송 수단

각 전송 수단은 코드베이스 전체에서 사용되는 색상 이름이 있습니다. `IsAvailable`은 하드웨어가 차단된 경로를 게이팅합니다 — `TransportManager`는 이를 자동으로 건너뛰고 다음 사용 가능한 전송 수단으로 대체합니다.

**상태 키:** ✅ 실제, 구축 및 검증됨 · ⏳ 실제, 검증 진행 중 · ⚠️ 일부 플랫폼에서 실제, 다른 플랫폼에서는 스텁 · ❌ 스텁 (아직 전송 코드 없음).

| 색상 | 이름 | 범위 | 대역폭 | 상태 |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ 실제 — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ 실제 — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC 중계 | 무제한 | ~10 Mbps | ✅ 실제 — Windows; 중계 서버는 `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | 인터넷 데이터 채널 | 무제한 | ~100 Mbps | ✅ 8개 언어 모두에서 실제 — **8개 모두에서 루프백 검증됨** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust 각각 두 피어가 실제 ICE 데이터 채널을 통해 바이트를 교환) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android에서 실제 (`android/white/`); Windows = 실제 BLE-GATT + RSSI −40 dBm 근접 근사 (`WinNfcBleTransportService`, net9/10 컴파일, 런타임 미검증) — `Windows.Networking.Proximity`는 Win 11에서 제거됨 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ HarmonyOS에서 실제 (`harmonyos/teal/`, `@kit.NearLinkKit` — 온디바이스 검증 대기 중); Android + Windows = 실제 SSAP-over-BLE 근사 (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; 컴파일 + 단위 테스트 검증, 런타임 미검증) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ 실제 RYLR SX127x/SX126x 시리얼 드라이버 (C#/Go/Rust/C의 `LoRaSerialTransport`; 컴파일됨, 런타임 미검증 — 물리적 모듈 필요); BLE Coded-PHY 브리지는 여전히 문서화된 설계 |

라디오 전송 수단은 플랫폼 코드가 존재하는 곳에서만 실제입니다 (C#/Windows, Kotlin/Android, HarmonyOS). 그 외에는 8개 언어 라이브러리 모두 테스트용 **프로세스 내 시뮬레이션** 전송 수단을 제공합니다 — **WebRTC는 이들 모두에 공통되는 최초의 실제 전송 수단입니다** (완성; 언어 전반에 걸쳐 루프백 검증됨).

우선순위는 전력 비용에 따릅니다: 라디오 메시가 선호되고, 그다음 직접 인터넷 경로로서 WebRTC, 최후 수단으로 HTTP/QUIC 중계입니다.

## 배포 티어

Aether는 블루투스 또는 Wi-Fi를 지원하는 모든 플랫폼에서 작동합니다. 사용 중인 티어는 대상 OS에 따라 결정됩니다.

---

### 표준 티어 — 모든 플랫폼

Android · Windows · Linux · macOS · iOS

Aether는 블루투스 또는 Wi-Fi 하드웨어가 있는 모든 기기에서 실행됩니다. 라디오가 물리적으로 없는 경우, 각 차단된 전송 수단은 사용 가능한 것을 활용하여 근사됩니다. 이 근사들은 이제 **실제 코드**입니다 (컴파일 검증됨; 2-기기 / 하드웨어 RF 테스트 대기 중 **런타임 미검증**):

- **NearLink (Aether Teal)** — Android (`android/teal/AetherNetSleService`)와 Windows (`WinNearLinkBleTransportService`)에서 실제 SSAP-over-BLE-GATT 근사 (Aether SLE UUID `61657468-6572-0003-…`); 컴파일 + 단위 테스트 검증, 런타임 미검증. 실제 NearLink 라디오는 HarmonyOS에만 존재합니다 (`harmonyos/teal/`, 온디바이스 검증 대기 중).
- **LoRa (Aether Red)** — 실제 RYLR SX127x/SX126x 시리얼 드라이버 (**8개 언어 모두**의 `LoRaSerialTransport` — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; 모든 포트 컴파일 검증됨, Mac 빌드 서버의 Swift + C 포함; 런타임 미검증 — 물리적 모듈 필요). Meshtastic-over-BLE-Coded-PHY 브리지 (~1.3 km)는 여전히 문서화된 설계로 남아 있습니다; 실제 장거리 LoRa에는 LoRa 지원 노드 (게이트웨이, SBC, 또는 LoRa 모듈이 있는 견고한 핸드셋)가 필요합니다.
- **NFC (Aether White)** — Android에서 실제 (HCE). Windows는 이제 실제 BLE-GATT + RSSI −40 dBm 근접 근사를 갖습니다 (`WinNfcBleTransportService`, net9/10 컴파일; 런타임 미검증); 리더가 있을 때 ACR122U PC/SC.

어디서나 실제이고 동일한 것: **BLE, Wi-Fi Direct, HTTP/QUIC 중계, WebRTC P2P 전송 (8개 언어 모두에서 루프백 검증됨)**, 그리고 Signal Protocol 보안 (X3DH + Double Ratchet), AODV 라우팅, DTN 저장-전달, SOS 방송, 음성, 스트리밍.

**정직한 상태:** BLE + Wi-Fi Direct + 중계는 프로덕션-실제입니다; **WebRTC P2P는 8개 언어 모두에서 실제이고 루프백 검증됨** (두 피어가 실제 ICE 데이터 채널을 통해 바이트를 교환 — Rust는 작동하는 UDP ICE를 갖춘 `.201` Linux 박스에서 확인됨); NearLink / LoRa / Windows-NFC 근사는 이제 컴파일되는 실제 코드이지만 (LoRa는 Mac 빌드 서버의 Swift + C 포함 8개 모두에서 컴파일 검증됨; NearLink-Android는 단위 테스트도 완료) **런타임 미검증**입니다 — 아직 하드웨어 / 2-기기 RF 테스트 없음. 이들은 코드상 메시에 참여합니다; 현장 입증된 RF를 기대하며 이 세 가지를 배포하지 마십시오.

---

### 네이티브 티어 — CircleOS / OpenHarmony

CircleOS · HarmonyOS · 모든 OpenHarmony 기반 OS

CircleOS는 OpenHarmony를 기반으로 구축되어 있으며, NearLink (SLE) 실리콘과 `@kit.NearLinkKit` SDK를 1급 OS 기능으로 제공합니다. NearLink 하드웨어가 있는 CircleOS 및 HarmonyOS 기기에서는 근사가 필요하지 않습니다 — `harmonyos/teal/`은 실제 SLE 라디오를 직접 사용합니다:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

이것은 단순히 표준 티어의 더 좋은 버전이 아닙니다. NearLink 계층에서는 본질적으로 다른 네트워크입니다:

| 기능 | 표준 티어 (BLE 근사) | 네이티브 티어 (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink 범위** | ~100 m (BLE) | **600 m** |
| **NearLink 대역폭** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink 지연시간** | ~10 ms (BLE) | **20 µs** |
| **NearLink 전력** | BLE 기준 | **BLE 5.0 대비 60% 절감** |
| **동시 NearLink 피어** | ~7 (BLE 연결 제한) | **500+** |
| **NearLink 소스** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | 실제 SLE 라디오 (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | 네이티브 | 네이티브 (동일) |
| **Signal Protocol 보안** | 완전 지원 | 완전 지원 (동일) |
| **라우팅 / DTN / SOS** | 완전 지원 | 완전 지원 (동일) |
| **Aether Tag 신원** | 지원 | 지원 (동일) |

---

### 티어 간 이동

코드 변경이 필요하지 않습니다. 티어는 각 전송 서비스의 `IsAvailable`에 의해 런타임에 결정됩니다:

1. NearLink 실리콘이 탑재된 CircleOS 또는 HarmonyOS 기기에서, NearLink 전송의 `IsAvailable`은 `true`를 반환합니다 (권한 확인 + 수동 스캔 시도를 통한 하드웨어 탐지).
2. `TransportManager`는 NearLink를 자동으로 우선순위 위치로 승격합니다 — 최소 전력 비용, 최대 대역폭.
3. 앱 코드, 패킷 형식, 라우팅 알고리즘, 보안 계층, Aether Tag는 두 티어 모두에서 동일합니다.

표준 티어 노드와 네이티브 티어 노드는 자유롭게 통신할 수 있습니다 — 동일한 와이어 형식, 동일한 Signal Protocol 세션, 동일한 Aether Tag를 공유합니다. 티어 차이는 NearLink 패킷에 사용되는 라디오에만 영향을 미치며, 그 위의 프로토콜에는 영향을 주지 않습니다.

---

> **내부적으로 이 티어들은 Asterix 변형(표준)과 Obelix 변형(네이티브)으로 불립니다.** Asterix는 사용 가능한 것으로 잘 작동합니다. 네이티브 NearLink가 탑재된 CircleOS에서 실행되는 Obelix는 마법 물약의 힘을 다시 마실 필요 없이 유지하는 것처럼 영구적으로 향상된 역량으로 작동합니다.

---

## 구현체

Aether는 스마트폰, 노트북, 태블릿, 마이크로컨트롤러에서 실행될 수 있도록 8개 언어로 구축되어 있습니다. 모든 구현체는 와이어 호환 패킷을 생성합니다 — Rust 노드가 암호화한 메시지는 Python 노드가 중계하고 Swift 노드가 복호화할 수 있습니다.

| 언어 | 디렉터리 | 와이어 형식 | 라우팅/DTN/SOS | X3DH | Double Ratchet | OPK 풀 | 음성/그룹 | 스트리밍/영상/함께 보기 |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

8개 언어 모두 바이트 단위로 동일한 와이어 패킷을 생성하며, CI에서 14개의 정식 와이어 형식 픽스처와 4개의 Signal 테스트 벡터로 검증됩니다 (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). 라우팅 (AODV 방식 RREQ/RREP), DTN 저장-전달, SOS 방송, 음성, 스트리밍, 보안 강화 서비스가 모든 언어에서 구현되어 있으며, 8개 구현체 전체에 걸쳐 **약 3,000개의 테스트**가 있습니다:

| 언어 | 테스트 수 | CI 플랫폼 |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **합계** | **~3,000** | |

언어 간 Signal 상호 운용성은 `fixtures/signal/`에 X3DH (`x3dh_basic`), 대칭 래칫 (`ratchet_step_basic`, `ratchet_step_three_iterations`), KDF_RK (`kdf_rk_basic`)에 대한 공유 테스트 벡터로 고정되어 있습니다. 모든 구현체는 해당 픽스처에 대해 바이트 단위로 동일한 출력을 생성해야 합니다. 8개 언어 모두 완전한 Signal 세션 (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`)을 지원합니다.

와이어 형식과 Signal을 넘어, **전체 와이어 서비스 제품군** — 프레즌스, 하트비트, 프로필 동기화, 임시 ID 알림, 사전 키 교환, 채널, 푸시-투-토크, 화면 공유, 통화 제어, SOS 확인, 스페이스 브레드크럼, 포지 알림, 볼트 샤드 요청, 대역폭 측정 (**제공되는 것 — 모든 서비스, 모든 언어로** 참조) — 도 마찬가지로 8개 언어 모두에 구현되어 있으며 자체 픽스처 (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, 그리고 형제 픽스처)에 고정되어 있습니다. 프로토콜 계층에서 C# 전용인 기능은 없습니다.

## 빠른 시작

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

데모는 8단계를 진행합니다: 세 노드 (Alice, Bob, Charlie)에 대한 Ed25519 신원 키 생성, Signal Protocol 세션 수립, 암호화된 메시지 전송, Charlie를 통한 메시지 중계 (Charlie는 내용을 읽을 수 없음), 바이너리 와이어 형식 표시, 5개의 연속 메시지에 걸친 전달 비밀성 시연. 출력은 색상 코드로 구분되며 단계 사이마다 일시 정지합니다.

**C#으로 메시지 보내기:**

```csharp
// Establish a Signal Protocol session
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Encrypt and send
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Create a signed packet
var packet = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = "alice",
    DestinationUhid = "bob",
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7
};
var wireBytes = PacketSerializer.Serialize(packet);
await transport.SendAsync("bob", wireBytes);
```

### Rust (1.70+)

```bash
cd rust && cargo run
```

데모는 두 노드에 대한 신원 키 생성, 사전 키 번들 교환, 암호화된 세션 수립, 양방향 암호화 메시지 전송, 메시 패킷 생성 및 서명, 서명 검증, 패킷 바이너리 와이어 형식 직렬화를 시연합니다. 프로세스 내 전송 계층도 시연합니다.

**Rust로 메시지 보내기:**

```rust
let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

let alice_bundle = alice.generate_pre_key_bundle("alice")?;
bob.process_pre_key_bundle(&alice_bundle)?;

let bob_bundle = bob.generate_pre_key_bundle("bob")?;
alice.process_pre_key_bundle(&bob_bundle)?;

let encrypted = alice.encrypt("bob", b"Hello Bob!")?;
let decrypted = bob.decrypt("alice", &encrypted)?;
```

### TypeScript (Node 18+, tsx)

```bash
cd typescript && npm install && npm run dev
```

데모는 시뮬레이션된 네트워크에서 두 노드를 생성하고, Ed25519 키를 생성하고, Signal Protocol 세션을 수립하고, 패킷을 생성 및 서명하고, C# 호환 바이너리 형식으로 직렬화하고, 비밀 메시지를 암호화하고, 다른 노드에서 복호화하고, 전송을 통해 전송하고, 왕복을 검증합니다.

**TypeScript로 메시지 보내기:**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Exchange bundle with peer
await signal.processPreKeyBundle(peerBundle);

const plaintext = new TextEncoder().encode("Hello!");
const encrypted = await signal.encrypt("peer-node", plaintext);

const packet = MeshPacket.create(PacketType.Data, "my-node");
packet.destinationUhid = "peer-node";
packet.payload = encrypted;

const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

const serialized = PacketSerializer.serialize(packet);
await transport.sendAsync("peer-node", serialized);
```

### Python (3.10+)

```bash
cd python && pip install -e . && python3 demo.py
```

데모는 8가지를 시연합니다: Ed25519 키 생성 및 변조 탐지, 기능이 있는 노드 생성, Signal Protocol X3DH 키 교환, AES-256-GCM 암호화 및 복호화, 패킷 직렬화, 재생 탐지를 포함한 패킷 서명, 프로세스 내 전송, 그리고 모든 계층을 결합한 완전한 종단 간 흐름.

**Python으로 메시지 보내기:**

```python
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

bob_bundle = await bob_signal.generate_pre_key_bundle("bob")
await alice_signal.process_pre_key_bundle(bob_bundle)

encrypted = await alice_signal.encrypt("bob", b"Hello Bob!")

packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="alice",
    destination_uhid="bob",
    payload=encrypted.ciphertext,
    ttl=7
)
signing_service.sign_packet(packet, alice_private_key)

serialized = PacketSerializer.serialize(packet)
await transport.send_async("bob", serialized)
```

### Go (1.22+)

```bash
cd go && go run ./cmd/demo/main.go
```

데모는 5가지를 시연합니다: 패킷 직렬화 왕복, 변조 탐지를 포함한 Ed25519 서명, 양방향 암호화 메시지와 함께하는 Signal Protocol 세션 수립, 두 피어 간 프로세스 내 전송, 그리고 재생 보호를 위한 난스 중복 제거.

**Go로 메시지 보내기:**

```go
alice, _ := security.NewSignalProtocolService()
bob, _ := security.NewSignalProtocolService()

aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
bob.ProcessPreKeyBundle(aliceBundle)

bobBundle, _ := bob.GeneratePreKeyBundle("bob")
alice.ProcessPreKeyBundle(bobBundle)

encrypted, _ := alice.Encrypt("bob", []byte("Hello Bob!"))
decrypted, _ := bob.Decrypt("alice", encrypted)
```

### Kotlin (JDK 17+, Gradle 8+)

```bash
cd kotlin && ./gradlew run
```

데모는 11단계를 진행합니다: 키 생성, 기능이 있는 노드 생성, Signal Protocol 초기화, 사전 키 번들 교환, 세션 수립, 패킷 생성 및 서명, 직렬화, 서명 검증을 포함한 역직렬화, 키 래칫을 사용한 종단 간 암호화, 재생 공격 탐지, 프로세스 내 전송.

**Kotlin으로 메시지 보내기:**

```kotlin
val aliceSignal = SignalProtocol()
val bobSignal = SignalProtocol()

val bobBundle = bobSignal.generatePreKeyBundle("bob")
aliceSignal.processPreKeyBundle(bobBundle)

val aliceBundle = aliceSignal.generatePreKeyBundle("alice")
bobSignal.processPreKeyBundle(aliceBundle)

val encrypted = aliceSignal.encrypt("bob", "Hello Bob!".toByteArray())
val decrypted = bobSignal.decrypt("alice", encrypted)
```

### Swift (5.9+, macOS 13+ / iOS 16+)

```bash
cd swift && swift run aether-demo
```

데모는 5가지 테스트를 실행합니다: 패킷 직렬화 왕복, 변조 거부를 포함한 Ed25519 서명, AES-256-GCM 암호화와 함께하는 Signal Protocol 세션 수립, 프로세스 내 전송 메시지 전달, 그리고 Alice가 패킷에 서명하고 전송 후 Bob이 검증하는 완전한 종단 간 흐름.

**Swift로 메시지 보내기:**

```swift
let aliceSignal = SignalProtocolService()
let bobSignal = SignalProtocolService()

let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: "bob")
try await aliceSignal.processPreKeyBundle(bobBundle)

var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice",
    destinationUhid: "bob",
    ttl: 7,
    payload: "Hello Bob!".data(using: .utf8)!
)

let signer = await PacketSigningService(
    privateKey: alicePrivateKey, publicKey: alicePublicKey)
try await signer.signPacket(&packet)

let serialized = PacketSerializer.serialize(packet)
await transport.sendAsync(peerUhid: "bob", data: serialized)
```

### C (CMake 3.16+, C11, libsodium)

```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

데모는 7가지를 시연합니다: Ed25519 키 생성, 패킷 생성 및 서명, 바이너리 와이어 형식으로 직렬화, 무결성 검사를 포함한 역직렬화, AES-256-GCM 암호화 및 복호화, HMAC-SHA256 메시지 인증, HKDF-SHA256 키 유도.

**C로 메시지 보내기:**

```c
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethernet_packet_free(packet);
```

## 로드맵

완성된 것과 다음 단계.

**완료 (언어 간 검증, 8개 구현체 모두):**
- 와이어 형식: 8개 언어에서 바이트 단위로 동일, CI의 14개 정식 픽스처 및 언어 간 어서션으로 고정 (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9개 작업 매트릭스 (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, 픽스처 무결성 작업 포함) `.github/workflows/ci.yml`.
- Ed25519 패킷 서명 및 검증
- AES-256-GCM 암호화
- HKDF / HMAC 키 유도 프리미티브
- 패킷 직렬화 + 서명 레이아웃 (LE + 4바이트 int32 필드)
- 프로세스 내 전송 시뮬레이터 (개발 및 테스트용)
- RREQ/RREP, 서명된 경로 응답, 중복 제거, TTL 전달을 포함한 AODV 방식 라우팅 서비스
- 보관 이전, 지오해시 인식 복제, 72시간 TTL을 포함한 DTN 저장-전달 서비스
- 플러드, 중복 제거, 자기 발원 방지, 속도 제한 (시간당 3회)을 포함한 SOS 방송 서비스
- 확장성 이음매: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (기본값 Noop)
- **약 3,000개의 테스트** (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — CI에서 모두 통과
- ✅ **실제 X3DH 임시 키 (8개 언어)** — 4개의 X25519 DH (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) 및 HKDF-SHA256 루트 유도. `fixtures/signal/expected/x3dh_basic.json`으로 고정.
- ✅ **전체 제품군 Double Ratchet 정렬** — 대칭 래칫의 HMAC-SHA256 + 0x01/0x02 도메인 분리, DH-래칫 단계의 HKDF-SHA256 KDF_RK, 수신 시 DH-회전을 포함한 완전한 Signal §5. `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` 픽스처로 검증.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 HEAD와 조화** — `docs/PROTOCOL_SPEC.md` 참조.

**완료 (8개 언어 모두):**
- ✅ **음성 통화 (1:1)** — 시그널링 상태 머신 (Offer/Answer/Hangup/Cancel/Timeout) + 바이너리 프레임 전송 (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). `IRoutingService`를 통한 경로 인식 전달.
- ✅ **그룹 음성** — 호스트 주도 멤버십 (초대/추방/퇴장), 프레임별 키 생성 필드, 현재 모든 구성원에 유니캐스트 팬아웃, 멤버십 변경 시 호스트 제어 키 교체.
- ✅ **라이브 스트리밍** — 퍼블리셔가 `StreamAnnounce` 방송; 구독자가 `StreamSubscribe` 전송; 바이너리 `StreamSegment` 프레임 (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes)이 각 구독자에게 유니캐스트.
- ✅ **영상 통화 (1:1)** — 시그널링에서 코덱/해상도/fps/비트레이트 협상, 키프레임 요청 및 품질 변경 신호, 음성 레이아웃에 맞는 바이너리 `VideoFrame` 형식.
- ✅ **함께 보기** — 호스트가 권위 있는 `WatchSync` (재생/일시정지/탐색/속도) 명령 발행; 팔로워가 RTT 보상으로 적용 (`position = positionMs + elapsed × playbackSpeed`); 파이어-앤-포겟 `WatchReaction`.
- ✅ **1회용 사전 키 (OPK) 풀** — 기본 100개, FIFO 발급, 지연 보충, 8개 언어 모두에서 잠금 보호 소비. 단일 OPK 동시성 위험 해결.
- ✅ **C: 완전한 Signal 세션** — `c/src/signal_protocol.c`의 `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`; `c/tests/test_signal_session.c`의 6개 양방향 E2E 테스트. 8개 언어 모두 이제 완전한 세션 기능 Signal Protocol 지원.

**완료 (8개 언어 모두 — 전체 와이어 서비스 제품군):**
- ✅ **예약된 모든 패킷 타입이 이제 8개 언어 모두에서 실제로 작동하는 바이트 단위 동일 서비스입니다.** 프레즌스 비콘/쿼리 (21/22), 하트비트 (10), 프로필 동기화 (23), 임시 라우팅 ID 알림 (56), 사전 키 교환 (25/26), 채널 (7), 푸시-투-토크 (15), 화면 공유 (32), 통화 제어 (27), SOS 확인 (6), 스페이스 브레드크럼 (40), 포지 알림 (41), 볼트 샤드 요청 (42), 대역폭 측정 / ABMF (53/54/55). 각각은 호스트가 자신의 Signal 세션과 라우팅 테이블에 연결하는 얇은 서비스 (생성 + 처리 + 이벤트)이며; 각각은 공유 언어 간 픽스처 (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`)에 고정되어 있고 언어별 단위 테스트로 검증되며, Swift와 C는 macOS 빌드 서버에서 검증됩니다. **제공되는 것 — 모든 서비스, 모든 언어로** 참조.

**완료 (C# 참조 구현체만):**
- ✅ **데모 9단계 — MessagingService + DTN 폴백 종단 간** — `samples/AetherNet.Demo.Console`은 수신자가 오프라인일 때 DTN 저장-전달과 함께 실제 Signal 암호화 메시지를 진행합니다.
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` 브리지** — `SignalMessageEnvelopeCipher`가 메시지 계층을 기본적으로 종단 간 암호화합니다; Signal 세션이 없는 메시지는 큐에 저장되며 안전하지 않게 전송되지 않습니다.
- ✅ **어댑티브 비트레이트 스트리밍** — 프로파일 A (실시간), B (라이브 방송), C (VOD)에 대한 명세 지정 비트레이트 래더를 갖는 `AdaptiveBitrateController`. 퍼블리셔는 최고 지속 가능한 등급 (20% 여유)을 선택하고 하한선 이하가 되면 세그먼트 대신 `StreamAbandon` (`PacketType.StreamAbandon`)을 발행합니다. `IStreamingService`는 `UpdateBandwidthEstimate`와 `GetCurrentBitrateRung`을 노출합니다.
- ✅ **함께 보기: BitTorrent 수집 + ChipIn 그룹 펀딩** — `TorrentInfo` / `TorrentFile` 모델; `WatchTogetherService`가 `PacketType.TorrentMetadata`를 처리하고 `TorrentReceived`를 발생시킵니다. `ChipInPool` / `ChipInContribution` 상태 머신 (수집 중 → 펀딩됨 → 구매 중 → 획득 / 실패 / 환불); `IWatchTogetherService`의 `StartChipInAsync` / `ContributeAsync` / `GetChipIn`.
- ✅ **자동 SFU 중계를 통한 그룹 영상 통화** — `GroupVideoService` / `IGroupVideoService`. ≤ 3명 참가자에는 FullMesh 토폴로지; `SfuThresholdParticipants` (4)에서 `GroupVideoSignaling(SfuAssigned)`을 통한 중계 재배정으로 자동 SFU 전환. FullMesh에서 팬아웃, SFU 모드에서 중계 전용 전송. 시그널링 패킷 타입 `GroupVideoSignaling = 35`.
- ✅ **BLE GATT 전송 시뮬레이션** — `SimulatedBleGattTransportService` (`IBleTransportService`). `BleGattFramer`를 통한 GATT MTU 프레이밍 (1024 B/프레임, `[2B count][2B index][payload]`), 프로세스 내 정적 피어 레지스트리, 광고 방송. 모든 `BleMaxPayloadBytes` 제약 적용.
- ✅ **Wi-Fi Direct 전송 시뮬레이션** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). 명시적 `ConnectAsync`/`DisconnectAsync` 생명주기, 직접 대용량 페이로드 전달 (프레이밍 없음), 양방향 `PeerConnected`/`PeerDisconnected` 이벤트.
- ✅ **NearLink 전송 시뮬레이션** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096 B 프레임 MTU, 500개 피어 레지스트리, `ConnectedPeerCount`, 런타임에 설정 가능한 `IsAvailable`.
- ✅ **RF 가동 시뮬레이션 테스트** — 양방향 상호 운용성 테스트 (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` 왕복, WiFi Direct 64 KB 페이로드 전송. 소프트웨어 계층 완전 검증; 하드웨어 검증을 위한 물리적 기기 테스트 세션 필요.

**완료 (C# 전송 계층 — 모두 페일-패스트):**
- ✅ **BLE GATT 실제 전송** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT 서버). `samples/AetherNet.BleRfTest/`의 완전한 RF 가동 테스트.
- ✅ **Wi-Fi Direct 실제 전송** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket 포트 8888) + `android/green/` (`WifiP2pManager`). `samples/AetherNet.WifiDirectRfTest/`의 RF 테스트.
- ✅ **HTTP 중계 전송 (Aether Purple)** — 10초 롱폴링, `PowerCostRelative = 100`, 항상 최후 수단인 `HttpRelayTransportService`. `samples/AetherNet.RelayServer/`의 중계 서버 (ASP.NET Core 미니멀 API, 포트 5200). `samples/AetherNet.RelayRfTest/`의 RF 테스트.
- ✅ **NFC (Aether White)** — `android/white/`는 AID `F061657468657200`으로 `HostApduService`를 구현합니다. `WinNfcStubTransportService`는 두 가지 Windows 근사 경로를 문서화합니다: (1) RSSI 게이트 ≥ −40 dBm을 가진 NDEF-over-BLE-GATT (NFC 실리콘 없이 탭-투-커넥트 시뮬레이션, `IsAvailable = 블루투스 존재`); (2) `Windows.Devices.SmartCards` PC/SC를 통한 ACR122U USB 리더 (`IsAvailable = 비접촉식 리더 열거됨`). 업그레이드 경로: Microsoft가 1급 P2P NFC API를 제공하면 `ITransportService` 구현.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — `@kit.NearLinkKit`을 사용하는 완전한 HarmonyOS 5.0.1 (API 13) ArkTS 구현 (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); 런타임에 `isAvailable` 탐지. `WinNearLinkStubTransportService` + `android/teal/`은 SSAP-over-BLE 근사를 문서화합니다: Aether SLE 서비스 UUID `61657468-6572-0003-0000-000000000000`을 사용하는 BLE GATT — SSAP와 API 유사하지만 실제 NearLink 하드웨어와 와이어 비호환. 업그레이드 경로: BLE GATT 호출을 `ssapc_*`/`ssaps_*` SDK 호출로 교체; UUID 및 `TransportManager` 슬롯 변경 없음.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/`은 Meshtastic-over-BLE-LR 근사를 문서화합니다: 관리된 플러드 라우팅 및 RSSI 가중 경쟁 창을 갖는 BLE 5.0 Coded PHY S=8 (~1.3 km 야외)을 통한 완전한 Meshtastic 와이어 형식 (16바이트 헤더 + AES-256-CTR 프로토버프). 실제 LoRa 하드웨어와의 브리지 노드 연합은 자동으로 작동합니다 (동일한 Meshtastic 패킷 형식, 변환 없음). 업그레이드 경로: BLE LR 라디오를 SX1276/SX1278 AT-커맨드 또는 SPI 드라이버로 교체; 패킷 형식 및 라우팅 변경 없음.

**미결 — `OPEN_ISSUES.md`에서 추적 중:**
- 실제 하드웨어에서 RF 가동: 물리적 BLE / Wi-Fi Direct 기기에서 종단 간 양방향 상호 운용성 테스트 (시뮬레이션 테스트 통과; 하드웨어 테스트 세션 필요)
- NearLink: `harmonyos/teal/` 완료; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 하드웨어 필요 (NearLink 실리콘은 非Huawei 기기에 없음). Windows + Android는 SSAP-over-BLE 근사로 자동 폴백.
- LoRa / CircleLink: 진정한 LoRa 범위를 위해 라디오 모듈 필요. 없는 경우 Meshtastic 와이어 형식은 BLE LR (~1.3 km)로 전달되며 실제 LoRa 하드웨어와의 브리지 노드 연합이 가능합니다.
- ✅ **(v1.2.0에서 해결됨)** 소비자 프로토콜 표면 (Wave 16/17) — 인바운드 번들을 위한 `IDtnService.BundleReceived` 이벤트 ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), 애플리케이션 계층 이름 지정/검색 디렉터리 ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), 작성자 팁 인터페이스 ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). 3가지 모두 바이트 동일 언어 간 픽스처와 함께 8개 언어에 걸쳐 부가적으로 제공됨. CHANGELOG 참조.

**아직 외부 기여 미오픈:**
- 프로토콜이 아직 활발히 개발 중입니다. 현재 외부 기여를 수락하지 않습니다.
- NearLink 전송 구현, Android/iOS 통합 예시, 추가 전송 백엔드, 성능 벤치마크, 프로토콜 퍼징은 내부적으로 추적 중이며 프로젝트가 안정적인 공개 기여 시점에 도달하면 공개될 예정입니다.

## 프로젝트 구조

```
aether-protocol/
  src/
    AetherNet.Core/          프로토콜 모델, 상수, 패킷 직렬화
    AetherNet.Security/      Signal Protocol, Ed25519, 패킷 서명
    AetherNet.Transport/     전송 추상화, NearLink, 프로세스 내 시뮬레이터
    AetherNet.Messaging/     메시지 처리 및 중계
    AetherNet.Storage/       DTN 저장-전달 영속성
    AetherNet.Streaming/     어댑티브 비트레이트 스트리밍, 영상 모델 및 인터페이스
    AetherNet.Voice/         음성 통화 및 그룹 음성
    AetherNet.Content/       콘텐츠 검증 및 청크 전송
  samples/
    AetherNet.Demo.Console/  인터랙티브 데모
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust 구현체
  typescript/             TypeScript 구현체
  python/                 Python 구현체
  go/                     Go 구현체
  kotlin/                 Kotlin/JVM 구현체
  swift/                  Swift 구현체
  c/                      C 구현체
  docs/
    PROTOCOL_SPEC.md      RFC 방식 프로토콜 사양
```

## 새 전송 수단 추가

`ITransportService`를 구현하십시오:

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implement SendAsync, IsConnected, DataReceived
}
```

DI에 등록하면 `TransportManager`가 자동으로 전송 선택에 포함하며, 전력 비용 순으로 정렬합니다.

## 비교

| 프로토콜 | 한계 | Aether의 장점 |
|----------|-----------|-----------------|
| **Briar** | Android 전용, Tor 의존 | 크로스 플랫폼, 순수 메시 |
| **Meshtastic** | LoRa 전용 (최대 30 kbps) | 다중 전송 (BLE + WiFi + NearLink), 음성 및 스트리밍 지원 |
| **Reticulum** | Python, 소규모 커뮤니티 | 8개 언어, 모두 와이어 호환 |
| **libp2p** | 인터넷 백본 가정 | 오프라인 우선, 인프라 없이 작동 |
| **Yggdrasil** | 오버레이 네트워크, 인터넷 필요 | 물리 계층 메시, 인터넷 없이 작동 |
| **Signal** | 메시 없음, 인터넷 필요 | 오프라인 작동, P2P, 메시 중계, 동일한 E2E 암호화 |

## 자주 묻는 질문

**AetherNet은 인터넷 없이 작동하나요?**
네 — 오프라인 우선입니다. 기기들은 블루투스, Wi-Fi Direct, NearLink, 또는 LoRa를 통해 직접 통신하고 다른 기기를 거쳐 홉 단위로 메시지를 중계하며, 인터넷 연결, 기지국, 서버가 필요하지 않습니다. 활성 경로가 없을 때 메시지는 하나가 열릴 때까지 최대 72시간 동안 보관됩니다 (지연 허용 저장-전달).

**종단 간 암호화되나요?**
네. AetherNet은 종단 간 암호화를 위해 Signal Protocol (X3DH 키 합의와 X25519 기반 Double Ratchet)을 사용하고, 메시지 페이로드에는 AES-256-GCM을, 모든 패킷에는 Ed25519 서명을 사용합니다. 메시지를 중계하는 기기는 그것을 읽을 수 없습니다.

**어떤 전송 수단을 사용하나요?**
블루투스 LE, Wi-Fi Direct, NearLink (SLE), LoRa/CircleLink 시리얼 라디오, HTTP/QUIC 중계, 그리고 직접 인터넷 피어 투 피어를 위한 WebRTC. 프로토콜은 패킷별로 사용 가능한 최소 전력 전송 수단을 자동으로 선택하고 다음 것으로 대체합니다.

**어떤 프로그래밍 언어로 제공되나요?**
8개 — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, C. 모든 구현체는 바이트 단위로 동일한 와이어 패킷을 생성하며, CI의 공유 언어 간 픽스처 코퍼스로 강제되므로, 한 언어로 구축한 패킷은 다른 어느 언어로든 변경 없이 디코딩됩니다.

**Meshtastic, Briar, Bridgefy와 어떻게 다른가요?**
Meshtastic은 LoRa 전용입니다; AetherNet은 다중 전송 (블루투스 + Wi-Fi + NearLink + LoRa)이며 메시지뿐만 아니라 음성, 영상, 스트리밍도 전달합니다. Briar는 Android 전용이며 Tor로 라우팅합니다; AetherNet은 크로스 플랫폼이며 순수 메시입니다. 폐쇄형 SDK와 달리 AetherNet은 MIT 라이선스이며 8개 언어로 공개적으로 구현되어 있습니다. 위의 비교 표에 세부 사항이 있습니다.

**프로덕션 준비가 되었나요?**
프로토콜 계층 — 와이어 형식, Signal 보안, 라우팅, DTN 저장-전달, 전체 서비스 제품군 — 은 8개 언어 모두에 걸쳐 구현되고 테스트되었습니다. 라디오 전송 수단은 플랫폼 코드가 존재하는 곳 (Windows와 Android의 블루투스와 Wi-Fi, 그리고 어디서나 WebRTC)에서 실제이며, 그 외에는 하드웨어 가동 전까지 현장 미검증 상태로 `OPEN_ISSUES.md`에서 정직하게 추적됩니다. 배포하기 전에 각 섹션의 상태 참고 사항을 읽으십시오.

**어떤 라이선스인가요?**
MIT — 상업적 및 오픈소스 사용에 무료. [LICENSE](LICENSE) 참조.

**AetherNet은 누가 만드나요?**
The Geek Network의 메시 생태계 뒤에 있는 오픈 프로토콜로 개발되며, 모바일 데이터가 있든 없든 작동하는 통신을 위해 남아프리카에서 구축되었습니다.

## 확장 지점

프로토콜은 독립적으로 작동합니다. 다음 인터페이스로 원하는 경우 자체 백엔드를 연결할 수 있습니다:

- `IAetherNetIncentiveProvider` — 트래픽을 중계하는 노드에 보상 (기본 no-op: 이타적 중계)
- `IAetherNetBackendClient` — 인터넷 가용 시 서버와 동기화 (기본 no-op: 완전 오프라인)
- `IAetherNetFeatureFlagProvider` — 런타임에 프로토콜 기능 토글 (기본 no-op: 모든 기능 활성화)

세 가지 모두 no-op 구현체가 제공됩니다. 제거해도 아무것도 중단되지 않습니다.

## 기여

외부 기여는 아직 오픈되지 않았습니다. 프로젝트가 아직 활발히 개발 중입니다. 공개 기여 창구를 발표할 때 다시 확인해 주십시오.

## 보안

책임 있는 공개 정책은 [SECURITY.md](SECURITY.md)를 참조하십시오.

## 라이선스

MIT 라이선스. [LICENSE](LICENSE) 참조.

## 번역

이 README는 영어로 유지 관리되며 [`docs/i18n/`](docs/i18n/) 아래에 10개의 추가 언어로 번역됩니다: Français, Español, العربية, 中文简体, 日本語, Deutsch, Português (BR), Русский, فارسی, 한국어. **영어 버전이 진실의 원천입니다** — 번역과 영어 텍스트가 일치하지 않는 경우 영어 텍스트가 권위를 가지며, 번역은 한두 릴리스 정도 뒤처질 수 있습니다. 설명된 프로토콜, 코드, 픽스처, 동작은 어느 언어로 읽든 동일합니다.
