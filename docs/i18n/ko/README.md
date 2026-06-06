```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

가까운 사람들과 파일, 메시지, 스트림을 공유하세요. Wi-Fi 불필요. 모바일 데이터 불필요. 회원가입 불필요. AirDrop과 비슷하지만, 모든 플랫폼의 모든 사람과 함께 작동합니다.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

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

## 전송 수단

각 전송 수단은 코드베이스 전체에서 사용되는 색상 이름이 있습니다. `IsAvailable`은 하드웨어가 차단된 경로를 게이팅합니다 — `TransportManager`는 이를 자동으로 건너뛰고 다음 사용 가능한 전송 수단으로 대체합니다.

| 색상 | 이름 | 범위 | 대역폭 | 상태 |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | Cellular HTTP relay | 무제한 | ~10 Mbps | ✅ Windows — 중계 서버는 `samples/AetherMesh.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE (`android/white/`); Windows: NDEF-over-BLE-GATT + ACR122U PC/SC 근사 (`Windows.Networking.Proximity`는 Win 11에서 제거됨) |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`; Windows + Android: SSAP-over-BLE 근사 (API 유사, 와이어 비호환) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ BLE LR을 통한 Meshtastic 와이어 형식 (~1.3 km); LoRa 모듈 존재 시 SX1276/SX1278로 라디오 교체 |

`TransportManager`의 우선순위: NearLink → BLE (≤ 1 KB) → Wi-Fi Direct → NFC → LoRa → HTTP Relay (최후 수단, `PowerCostRelative = 100`).

## 배포 티어

Aether는 블루투스 또는 Wi-Fi를 지원하는 모든 플랫폼에서 작동합니다. 사용 중인 티어는 대상 OS에 따라 결정됩니다.

---

### 표준 티어 — 모든 플랫폼

Android · Windows · Linux · macOS · iOS

Aether는 블루투스 또는 Wi-Fi 하드웨어가 있는 모든 기기에서 완전하게 실행됩니다. 라디오가 물리적으로 없는 경우, 각 차단된 전송 수단은 사용 가능한 것을 활용하여 근사됩니다:

- **NearLink (Aether Teal)** — 정식 Aether SLE 서비스 UUID (`61657468-6572-0003-0000-000000000000`)를 사용하는 BLE GATT를 통해 근사됩니다. SSAP 애플리케이션 프로토콜 계층은 API 측면에서 GATT와 동일합니다. 라디오 계층 (BPSK/QPSK/8PSK, Polar 코드, 1–4 MHz 채널)은 그렇지 않습니다 — 표준 티어를 실행하는 노드는 실제 NearLink 하드웨어와 원시 바이트를 교환할 수 없지만, 다른 표준 티어 Aether 노드와는 상호 운용됩니다.
- **LoRa (Aether Red)** — BLE 5.0 Coded PHY (S=8, 야외 ~1.3 km)를 통한 완전한 Meshtastic 와이어 형식을 사용하여 근사됩니다. 실제 LoRa 하드웨어와의 브리지 노드 연합은 자동으로 작동합니다 — 동일한 Meshtastic 패킷 형식이 모든 홉에서 변환 없이 사용됩니다.
- **NFC (Aether White)** — 탭-투-커넥트 의미론을 재현하는 RSSI 근접 게이트 (≥ −40 dBm ≈ 5–10 cm)가 포함된 NDEF-over-BLE-GATT를 통해 근사됩니다. Windows에서는 USB NFC 리더를 통한 PC/SC 경로도 지원됩니다.

그 외 모든 기능 — BLE, Wi-Fi Direct, HTTP 중계, Signal Protocol 보안 (X3DH + Double Ratchet), AODV 라우팅, DTN 저장-전달, SOS 방송, 음성, 스트리밍 — 은 네이티브이며 네이티브 티어와 동일합니다.

**이것은 완전히 사용 가능한 프로덕션급 배포입니다.** 대부분의 앱은 여기서 시작합니다.

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

## 빠른 시작

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherMesh.Demo.Console
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
aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
packet->type = AETHERMESH_PACKET_TYPE_DATA;
packet->ttl = 7;

aethermesh_packet_set_source_uhid(packet, "alice");
aethermesh_packet_set_destination_uhid(packet, "bob");
aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
aethermesh_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethermesh_packet_free(packet);
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
- ✅ **C: 완전한 Signal 세션** — `c/src/signal_protocol.c`의 `aethermesh_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`; `c/tests/test_signal_session.c`의 6개 양방향 E2E 테스트. 8개 언어 모두 이제 완전한 세션 기능 Signal Protocol 지원.

**완료 (C# 참조 구현체만):**
- ✅ **데모 9단계 — MessagingService + DTN 폴백 종단 간** — `samples/AetherMesh.Demo.Console`은 수신자가 오프라인일 때 DTN 저장-전달과 함께 실제 Signal 암호화 메시지를 진행합니다.
- ✅ **`AetherMesh.Messaging` ↔ `AetherMesh.Security` 브리지** — `SignalMessageEnvelopeCipher`가 메시지 계층을 기본적으로 종단 간 암호화합니다; Signal 세션이 없는 메시지는 큐에 저장되며 안전하지 않게 전송되지 않습니다.
- ✅ **어댑티브 비트레이트 스트리밍** — 프로파일 A (실시간), B (라이브 방송), C (VOD)에 대한 명세 지정 비트레이트 래더를 갖는 `AdaptiveBitrateController`. 퍼블리셔는 최고 지속 가능한 등급 (20% 여유)을 선택하고 하한선 이하가 되면 세그먼트 대신 `StreamAbandon` (`PacketType.StreamAbandon`)을 발행합니다. `IStreamingService`는 `UpdateBandwidthEstimate`와 `GetCurrentBitrateRung`을 노출합니다.
- ✅ **함께 보기: BitTorrent 수집 + ChipIn 그룹 펀딩** — `TorrentInfo` / `TorrentFile` 모델; `WatchTogetherService`가 `PacketType.TorrentMetadata`를 처리하고 `TorrentReceived`를 발생시킵니다. `ChipInPool` / `ChipInContribution` 상태 머신 (수집 중 → 펀딩됨 → 구매 중 → 획득 / 실패 / 환불); `IWatchTogetherService`의 `StartChipInAsync` / `ContributeAsync` / `GetChipIn`.
- ✅ **자동 SFU 중계를 통한 그룹 영상 통화** — `GroupVideoService` / `IGroupVideoService`. ≤ 3명 참가자에는 FullMesh 토폴로지; `SfuThresholdParticipants` (4)에서 `GroupVideoSignaling(SfuAssigned)`을 통한 중계 재배정으로 자동 SFU 전환. FullMesh에서 팬아웃, SFU 모드에서 중계 전용 전송. 시그널링 패킷 타입 `GroupVideoSignaling = 35`.
- ✅ **BLE GATT 전송 시뮬레이션** — `SimulatedBleGattTransportService` (`IBleTransportService`). `BleGattFramer`를 통한 GATT MTU 프레이밍 (1024 B/프레임, `[2B count][2B index][payload]`), 프로세스 내 정적 피어 레지스트리, 광고 방송. 모든 `BleMaxPayloadBytes` 제약 적용.
- ✅ **Wi-Fi Direct 전송 시뮬레이션** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). 명시적 `ConnectAsync`/`DisconnectAsync` 생명주기, 직접 대용량 페이로드 전달 (프레이밍 없음), 양방향 `PeerConnected`/`PeerDisconnected` 이벤트.
- ✅ **NearLink 전송 시뮬레이션** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096 B 프레임 MTU, 500개 피어 레지스트리, `ConnectedPeerCount`, 런타임에 설정 가능한 `IsAvailable`.
- ✅ **RF 가동 시뮬레이션 테스트** — 양방향 상호 운용성 테스트 (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` 왕복, WiFi Direct 64 KB 페이로드 전송. 소프트웨어 계층 완전 검증; 하드웨어 검증을 위한 물리적 기기 테스트 세션 필요.

**완료 (C# 전송 계층 — 모두 페일-패스트):**
- ✅ **BLE GATT 실제 전송** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT 서버). `samples/AetherMesh.BleRfTest/`의 완전한 RF 가동 테스트.
- ✅ **Wi-Fi Direct 실제 전송** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket 포트 8888) + `android/green/` (`WifiP2pManager`). `samples/AetherMesh.WifiDirectRfTest/`의 RF 테스트.
- ✅ **HTTP 중계 전송 (Aether Purple)** — 10초 롱폴링, `PowerCostRelative = 100`, 항상 최후 수단인 `HttpRelayTransportService`. `samples/AetherMesh.RelayServer/`의 중계 서버 (ASP.NET Core 미니멀 API, 포트 5200). `samples/AetherMesh.RelayRfTest/`의 RF 테스트.
- ✅ **NFC (Aether White)** — `android/white/`는 AID `F061657468657200`으로 `HostApduService`를 구현합니다. `WinNfcStubTransportService`는 두 가지 Windows 근사 경로를 문서화합니다: (1) RSSI 게이트 ≥ −40 dBm을 가진 NDEF-over-BLE-GATT (NFC 실리콘 없이 탭-투-커넥트 시뮬레이션, `IsAvailable = 블루투스 존재`); (2) `Windows.Devices.SmartCards` PC/SC를 통한 ACR122U USB 리더 (`IsAvailable = 비접촉식 리더 열거됨`). 업그레이드 경로: Microsoft가 1급 P2P NFC API를 제공하면 `ITransportService` 구현.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — `@kit.NearLinkKit`을 사용하는 완전한 HarmonyOS 5.0.1 (API 13) ArkTS 구현 (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); 런타임에 `isAvailable` 탐지. `WinNearLinkStubTransportService` + `android/teal/`은 SSAP-over-BLE 근사를 문서화합니다: Aether SLE 서비스 UUID `61657468-6572-0003-0000-000000000000`을 사용하는 BLE GATT — SSAP와 API 유사하지만 실제 NearLink 하드웨어와 와이어 비호환. 업그레이드 경로: BLE GATT 호출을 `ssapc_*`/`ssaps_*` SDK 호출로 교체; UUID 및 `TransportManager` 슬롯 변경 없음.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/`은 Meshtastic-over-BLE-LR 근사를 문서화합니다: 관리된 플러드 라우팅 및 RSSI 가중 경쟁 창을 갖는 BLE 5.0 Coded PHY S=8 (~1.3 km 야외)을 통한 완전한 Meshtastic 와이어 형식 (16바이트 헤더 + AES-256-CTR 프로토버프). 실제 LoRa 하드웨어와의 브리지 노드 연합은 자동으로 작동합니다 (동일한 Meshtastic 패킷 형식, 변환 없음). 업그레이드 경로: BLE LR 라디오를 SX1276/SX1278 AT-커맨드 또는 SPI 드라이버로 교체; 패킷 형식 및 라우팅 변경 없음.

**미결 — `OPEN_ISSUES.md`에서 추적 중:**
- 실제 하드웨어에서 RF 가동: 물리적 BLE / Wi-Fi Direct 기기에서 종단 간 양방향 상호 운용성 테스트 (시뮬레이션 테스트 통과; 하드웨어 테스트 세션 필요)
- NearLink: `harmonyos/teal/` 완료; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 하드웨어 필요 (NearLink 실리콘은 非Huawei 기기에 없음). Windows + Android는 SSAP-over-BLE 근사로 자동 폴백.
- LoRa / CircleLink: 진정한 LoRa 범위를 위해 라디오 모듈 필요. 없는 경우 Meshtastic 와이어 형식은 BLE LR (~1.3 km)로 전달되며 실제 LoRa 하드웨어와의 브리지 노드 연합이 가능합니다.

**아직 외부 기여 미오픈:**
- 프로토콜이 아직 활발히 개발 중입니다. 현재 외부 기여를 수락하지 않습니다.
- NearLink 전송 구현, Android/iOS 통합 예시, 추가 전송 백엔드, 성능 벤치마크, 프로토콜 퍼징은 내부적으로 추적 중이며 프로젝트가 안정적인 공개 기여 시점에 도달하면 공개될 예정입니다.

## 프로젝트 구조

```
aether-protocol/
  src/
    AetherMesh.Core/          프로토콜 모델, 상수, 패킷 직렬화
    AetherMesh.Security/      Signal Protocol, Ed25519, 패킷 서명
    AetherMesh.Transport/     전송 추상화, NearLink, 프로세스 내 시뮬레이터
    AetherMesh.Messaging/     메시지 처리 및 중계
    AetherMesh.Storage/       DTN 저장-전달 영속성
    AetherMesh.Streaming/     어댑티브 비트레이트 스트리밍, 영상 모델 및 인터페이스
    AetherMesh.Voice/         음성 통화 및 그룹 음성
    AetherMesh.Content/       콘텐츠 검증 및 청크 전송
  samples/
    AetherMesh.Demo.Console/  인터랙티브 데모
  tests/
    AetherMesh.Security.Tests/
    AetherMesh.Protocol.Tests/
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

## 확장 지점

프로토콜은 독립적으로 작동합니다. 다음 인터페이스로 원하는 경우 자체 백엔드를 연결할 수 있습니다:

- `IAetherMeshIncentiveProvider` — 트래픽을 중계하는 노드에 보상 (기본 no-op: 이타적 중계)
- `IAetherMeshBackendClient` — 인터넷 가용 시 서버와 동기화 (기본 no-op: 완전 오프라인)
- `IAetherMeshFeatureFlagProvider` — 런타임에 프로토콜 기능 토글 (기본 no-op: 모든 기능 활성화)

세 가지 모두 no-op 구현체가 제공됩니다. 제거해도 아무것도 중단되지 않습니다.

## 기여

외부 기여는 아직 오픈되지 않았습니다. 프로젝트가 아직 활발히 개발 중입니다. 공개 기여 창구를 발표할 때 다시 확인해 주십시오.

## 보안

책임 있는 공개 정책은 [SECURITY.md](SECURITY.md)를 참조하십시오.

## 라이선스

MIT 라이선스. [LICENSE](LICENSE) 참조.
