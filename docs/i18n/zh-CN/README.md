```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

与附近的人共享文件、消息和数据流。无需 WiFi，无需移动数据，无需注册。就像 AirDrop，但它可以与所有人、在所有平台上使用。

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## 你能用它做什么？

**无需消耗流量共享课堂笔记。**

你在一个学习小组里。某人手机里有历年真题。Aether 通过蓝牙直接将文件发送到你的设备——无需热点，无需 WhatsApp 群组，无文件大小限制。如果小组中有人不在范围内，文件会通过其他设备跳转，直到到达对方。如有需要，消息最多等待 72 小时寻找路由。

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**了解你周围正在发生的事。**

你在校园活动或节日现场。Aether 通过蓝牙和 WiFi Direct 发现附近的其他设备——无 app 推送，无算法干预。你看到的是真实在你身边的内容，而非被推广的内容。

**在没有信号的情况下发送 SOS。**

你的手机没有信号。Aether 向范围内的每台设备广播紧急消息，这些设备再将其继续传递。无需基站。

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**创建私人群组频道。**

为你的宿舍楼层、社团或项目团队创建频道。只有经过验证的成员才能读取或发送消息。没有服务器存储对话内容。

**向附近的人出售物品。**

挂出一本二手教材。走进网格范围内的人都能看到它。无需市场账号，无需上架费——仅凭距离即可。

**跨网格一起看电影。**

你的小组要一起看电影之夜。某人有文件。Aether 在每台设备之间同步播放——播放、暂停、跳转——全部步调一致。如果只有部分人有文件，网格会以 P2P 流的方式实时分发。如果没人有文件，大家可以通过 SDPKT 共同出资购买。

## 工作原理

设备通过蓝牙、WiFi Direct 或 NearLink 直接相互通信。无需互联网连接，无服务器，无中央基础设施。

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

当消息无法直接到达目的地时，它会通过其他设备跳转。这些中继设备无法读取所传输的内容——每条消息都使用 AES-256-GCM 加密。每个数据包都由 Ed25519 身份密钥签名，伪造的数据包会被网络丢弃。

> **安全成熟度说明（发布前请阅读）：** 真正的 X3DH（4 个 X25519 DH）、完整的 Signal 双棘轮（接收时的 DH 轮换步骤、KDF_RK、0x01/0x02 链棘轮）以及一次性预密钥池（默认 100 个 OPK、FIFO、锁保护）已在**全部 8 种语言**中实现，并在 `fixtures/signal/` 下固定至共享跨语言测试用例库。唯一尚待完成的事项是在真实 BLE 硬件上进行物理 RF 启动测试（追踪于 `OPEN_ISSUES.md`）。

无账号，无电话号码，无电子邮件。生成密钥对即可接入网络。

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

**路由** — 带签名路由回复的 AODV。每个路由回复均由目标节点的 Ed25519 密钥签名，因此没有设备可以冒充不属于自己的目标。

**存储转发** — 当没有活跃路由时，数据包最多保留 72 小时，直到路径开通。

**传输选择** — 协议为每个数据包选择合适的传输方式。小型控制消息通过 BLE 传输，大批量传输使用 WiFi Direct，有条件时使用 NearLink。

**语音、视频和流媒体** — 支持编解码器协商（H.264/H.265/VP8）的视频通话、传输感知质量选择、带自动 SFU 中继的群组视频，以及带 RTT 补偿的同步观影和自适应码率流媒体。

**重放保护** — 使用 5 分钟时间戳新鲜度窗口的随机数去重。

## 传输方式

每种传输方式在代码库中都有一个颜色名称。`IsAvailable` 会屏蔽硬件不支持的路径——`TransportManager` 会自动跳过这些路径并回退到下一个可用传输方式。

| 颜色 | 名称 | 范围 | 带宽 | 状态 |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android（`android/blue/`） |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android（`android/green/`） |
| 🟣 Aether Purple | 蜂窝 HTTP 中继 | 无限 | ~10 Mbps | ✅ Windows——中继服务器位于 `samples/AetherNet.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE（`android/white/`）；Windows：NDEF-over-BLE-GATT + ACR122U PC/SC 近似（`Windows.Networking.Proximity` 在 Win 11 中已移除） |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`；Windows + Android：SSAP-over-BLE 近似（API 兼容，非线路兼容） |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ BLE LR 上的 Meshtastic 线路格式（~1.3 km）；存在 LoRa 模块时切换至 SX1276/SX1278 |

`TransportManager` 中的优先级顺序：NearLink → BLE（≤ 1 KB） → Wi-Fi Direct → NFC → LoRa → HTTP 中继（最后手段，`PowerCostRelative = 100`）。

## 部署层级

Aether 可在任何支持蓝牙或 Wi-Fi 的平台上运行。你所处的层级取决于目标操作系统。

---

### 标准层 — 任意平台

Android · Windows · Linux · macOS · iOS

Aether 可在任何具备蓝牙或 Wi-Fi 硬件的设备上完整运行。当某个无线电物理上不存在时，每个被屏蔽的传输方式都会通过现有的硬件进行近似：

- **NearLink（Aether Teal）** — 使用规范 Aether SLE 服务 UUID（`61657468-6572-0003-0000-000000000000`）通过 BLE GATT 进行近似。SSAP 应用协议层与 GATT 在 API 上完全相同。无线电层（BPSK/QPSK/8PSK、Polar 码、1–4 MHz 信道）则不同——运行标准层的节点无法与真实 NearLink 硬件交换原始字节；它们可与其他标准层 Aether 节点互通。
- **LoRa（Aether Red）** — 使用 BLE 5.0 编码物理层（S=8，室外约 1.3 km）上的完整 Meshtastic 线路格式进行近似。与真实 LoRa 硬件的桥接节点联合自动工作——相同的 Meshtastic 数据包格式无需转换即可覆盖所有跳数。
- **NFC（Aether White）** — 通过带 RSSI 接近门限（≥ −40 dBm ≈ 5–10 cm）的 NDEF-over-BLE-GATT 进行近似，重现轻触连接语义。Windows 上也支持通过 USB NFC 读卡器的 PC/SC 路径。

所有其他功能——BLE、Wi-Fi Direct、HTTP 中继、Signal Protocol 安全性（X3DH + 双棘轮）、AODV 路由、DTN 存储转发、SOS 广播、语音、流媒体——均为原生实现，与原生层完全相同。

**这是一个功能完整的生产级部署。** 大多数应用从这里开始。

---

### 原生层 — CircleOS / OpenHarmony

CircleOS · HarmonyOS · 任何基于 OpenHarmony 的操作系统

CircleOS 基于 OpenHarmony 构建，该系统内置 NearLink（SLE）芯片以及 `@kit.NearLinkKit` SDK 作为一等操作系统能力。在具有 NearLink 硬件的 CircleOS 和 HarmonyOS 设备上，无需近似——`harmonyos/teal/` 直接使用真实 SLE 无线电：

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

这不仅仅是标准层的更好版本。在 NearLink 层，这是一个截然不同的网络：

| 能力 | 标准层（BLE 近似） | 原生层（CircleOS / OpenHarmony） |
|---|---|---|
| **NearLink 范围** | ~100 m（BLE） | **600 m** |
| **NearLink 带宽** | ~1 Mbps（BLE） | **12 Mbps** |
| **NearLink 延迟** | ~10 ms（BLE） | **20 µs** |
| **NearLink 功耗** | BLE 基准 | **比 BLE 5.0 低 60%** |
| **并发 NearLink 节点** | ~7（BLE 连接限制） | **500+** |
| **NearLink 来源** | SSAP-over-BLE（`android/teal/`，`WinNearLinkStubTransportService`） | 真实 SLE 无线电（`harmonyos/teal/`，`@kit.NearLinkKit`） |
| **BLE / Wi-Fi Direct / HTTP 中继** | 原生 | 原生（相同） |
| **Signal Protocol 安全性** | 完整 | 完整（相同） |
| **路由 / DTN / SOS** | 完整 | 完整（相同） |
| **Aether Tag 身份** | 支持 | 支持（相同） |

---

### 层级切换

无需修改代码。层级在运行时由每个传输服务的 `IsAvailable` 决定：

1. 在具有 NearLink 芯片的 CircleOS 或 HarmonyOS 设备上，NearLink 传输的 `IsAvailable` 返回 `true`（通过权限检查 + 被动扫描尝试进行硬件探测）。
2. `TransportManager` 自动将 NearLink 提升至优先位置——最低功耗，最高带宽。
3. 应用代码、数据包格式、路由算法、安全层和 Aether Tags 在两个层级之间完全相同。

标准层节点和原生层节点可以自由通信——它们共享相同的线路格式、相同的 Signal Protocol 会话和相同的 Aether Tags。层级差异仅影响 NearLink 数据包所使用的无线电，而不影响其上层的协议。

---

> **内部将这些层级分别称为 Asterix 变体（标准层）和 Obelix 变体（原生层）。** Asterix 善用现有条件。Obelix——运行在具有原生 NearLink 的 CircleOS 上——以永久提升的能力运作，就像 Obelix 携带着魔法药水的力量，无需再次饮用。

---

## 实现

Aether 以 8 种语言构建，可运行于手机、笔记本电脑、平板电脑和微控制器。所有实现产生线路兼容的数据包——由 Rust 节点加密的消息可由 Python 节点中继，并由 Swift 节点解密。

| 语言 | 目录 | 线路格式 | 路由/DTN/SOS | X3DH | 双棘轮 | OPK 池 | 语音/群组 | 流媒体/视频/观影 |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

全部 8 种语言产生字节完全相同的线路数据包，通过 CI 中运行的 14 个规范线路格式测试用例和 4 个 Signal 测试向量验证（`fixtures/expected/*.bin`，`fixtures/signal/expected/*.json`）。路由（AODV 风格 RREQ/RREP）、DTN 存储转发、SOS 广播、语音、流媒体以及安全加固服务在每种语言中均已实现，所有 8 种实现共有约 **3,000 个测试**：

| 语言 | 测试数 | CI 平台 |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **总计** | **~3,000** | |

跨语言 Signal 互操作性以 `fixtures/signal/` 为基准，包含 X3DH（`x3dh_basic`）、对称棘轮（`ratchet_step_basic`、`ratchet_step_three_iterations`）和 KDF_RK（`kdf_rk_basic`）的共享测试向量。每种实现都必须针对这些测试用例产生字节完全相同的输出。所有 8 种语言现已完整实现 Signal 会话（`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`）。

## 快速入门

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

演示分 8 步进行：为三个节点（Alice、Bob、Charlie）生成 Ed25519 身份密钥，建立 Signal Protocol 会话，发送加密消息，通过 Charlie 中继消息（Charlie 无法读取内容），显示二进制线路格式，并在 5 条连续消息中演示前向保密性。输出带有颜色编码，并在步骤之间暂停。

**用 C# 发送消息：**

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

演示为两个节点生成身份密钥，交换预密钥包，建立加密会话，双向发送加密消息，创建并签名网格数据包，验证签名，以及将数据包序列化为二进制线路格式。同时演示进程内传输层。

**用 Rust 发送消息：**

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

演示在模拟网络中创建两个节点，生成 Ed25519 密钥，建立 Signal Protocol 会话，创建并签名数据包，将其序列化为 C# 兼容的二进制格式，加密秘密消息，在另一节点解密，通过传输层发送，并验证往返过程。

**用 TypeScript 发送消息：**

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

演示运行 8 个示例：Ed25519 密钥生成和篡改检测、带功能的节点创建、Signal Protocol X3DH 密钥交换、AES-256-GCM 加解密、数据包序列化、带重放检测的数据包签名、进程内传输，以及结合所有层的完整端到端流程。

**用 Python 发送消息：**

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

演示运行 5 个示例：数据包序列化往返、带篡改检测的 Ed25519 签名、双向加密消息的 Signal Protocol 会话建立、两节点间的进程内传输，以及用于重放保护的随机数去重。

**用 Go 发送消息：**

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

演示分 11 步进行：密钥生成、带功能的节点创建、Signal Protocol 初始化、预密钥包交换、会话建立、数据包创建和签名、序列化、带签名验证的反序列化、带密钥棘轮的端到端加密、重放攻击检测，以及进程内传输。

**用 Kotlin 发送消息：**

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

演示运行 5 个测试：数据包序列化往返、带篡改拒绝的 Ed25519 签名、带 AES-256-GCM 加密的 Signal Protocol 会话建立、进程内传输消息投递，以及 Alice 签名数据包、Bob 在传输后验证的完整端到端流程。

**用 Swift 发送消息：**

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

演示运行 7 个示例：Ed25519 密钥生成、数据包创建和签名、序列化为二进制线路格式、带完整性检查的反序列化、AES-256-GCM 加解密、HMAC-SHA256 消息认证，以及 HKDF-SHA256 密钥派生。

**用 C 发送消息：**

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

## 路线图

已完成的内容与下一步计划。

**已完成（经跨语言验证，全部 8 种实现）：**
- 线路格式：跨 8 种语言字节完全相同，由 14 个规范测试用例和 CI 中的跨语言断言锚定（`fixtures/expected/*.bin`）
- ✅ **GitHub Actions CI** — 9 个任务矩阵（C#/.NET 10、Go 1.22、TypeScript/Node 20、Python 3.12、Kotlin/JVM 21、Swift/macOS-14、Rust stable、C/GCC，以及测试用例完整性任务）位于 `.github/workflows/ci.yml`
- Ed25519 数据包签名和验证
- AES-256-GCM 加密
- HKDF / HMAC 密钥派生原语
- 数据包序列化 + 签名布局（LE + 4 字节 int32 字段）
- 进程内传输模拟器（用于开发和测试）
- 带 RREQ/RREP、签名路由回复、去重、TTL 转发的 AODV 启发式路由服务
- 带保管权转移、地理哈希感知复制、72 小时 TTL 的 DTN 存储转发服务
- 带洪泛、去重、自源防护、速率限制（3 次/小时）的 SOS 广播服务
- 可扩展性接缝：`IncentiveProvider`、`BackendClient`、`FeatureFlagProvider`（Noop 默认值）
- **约 3,000 个测试**，覆盖全部 8 种语言（C# 530、TypeScript 459、Kotlin 457、Go 423、Python 387、Swift 295、C 253、Rust ~195）——在 CI 中全部通过
- ✅ **真正的 X3DH 临时密钥（8 种语言）** — 4 个 X25519 DH（`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`）以及 HKDF-SHA256 根派生。由 `fixtures/signal/expected/x3dh_basic.json` 固定。
- ✅ **全系列双棘轮对齐** — 完整的 Signal §5，对称棘轮中带 HMAC-SHA256 + 0x01/0x02 域分隔，DH 棘轮步骤中的 HKDF-SHA256 KDF_RK，接收时的 DH 轮换。通过 `ratchet_step_basic`、`ratchet_step_three_iterations`、`kdf_rk_basic` 测试用例验证。
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 与 HEAD 对齐** — 参见 `docs/PROTOCOL_SPEC.md`。

**已完成（全部 8 种语言）：**
- ✅ **语音通话（一对一）** — 信令状态机（Offer/Answer/Hangup/Cancel/Timeout）+ 二进制帧传输（16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes）。通过 `IRoutingService` 进行路由感知投递。
- ✅ **群组语音** — 主机驱动的成员管理（邀请/踢出/离开）、每帧密钥生成字段、对所有当前成员的单播扇出、主机在成员变更时控制密钥轮换。
- ✅ **直播流媒体** — 发布者广播 `StreamAnnounce`；订阅者发送 `StreamSubscribe`；二进制 `StreamSegment` 帧（16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes）单播至每个订阅者。
- ✅ **视频通话（一对一）** — 信令中的编解码器/分辨率/帧率/码率协商，关键帧请求和质量变更信号，与语音布局匹配的二进制 `VideoFrame` 格式。
- ✅ **共同观影** — 主机发出权威性 `WatchSync`（播放/暂停/跳转/速度）命令；跟随者以 RTT 补偿应用（`position = positionMs + elapsed × playbackSpeed`）；即发即忘的 `WatchReaction`。
- ✅ **一次性预密钥（OPK）池** — 默认 100 个，FIFO 发放，懒惰补充，全部 8 种语言的锁保护消费。解决单 OPK 并发风险。
- ✅ **C：完整 Signal 会话** — `c/src/signal_protocol.c` 中的 `aethernet_signal_service_init`、`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`；`c/tests/test_signal_session.c` 中的 6 个双节点端到端测试。所有 8 种语言现已具备完整会话能力的 Signal Protocol。

**已完成（仅 C# 参考实现）：**
- ✅ **演示步骤 9 — MessagingService + DTN 回退端到端** — `samples/AetherNet.Demo.Console` 演示在接收方离线时使用 DTN 存储转发的真实 Signal 加密消息传递。
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` 桥接** — `SignalMessageEnvelopeCipher` 使消息层默认端到端加密；没有 Signal 会话的消息会进入队列，永不以明文发送。
- ✅ **自适应码率流媒体** — 带规范码率阶梯的 `AdaptiveBitrateController`（配置文件 A 实时、B 直播广播、C 点播）。发布者选择最高可持续档次（20% 余量），低于最低档时发出 `StreamAbandon`（`PacketType.StreamAbandon`）而非片段。`IStreamingService` 暴露 `UpdateBandwidthEstimate` 和 `GetCurrentBitrateRung`。
- ✅ **共同观影：BitTorrent 导入 + ChipIn 群组众筹** — `TorrentInfo` / `TorrentFile` 模型；`WatchTogetherService` 处理 `PacketType.TorrentMetadata` 并触发 `TorrentReceived`。`ChipInPool` / `ChipInContribution` 状态机（收集中→已筹够→购买中→已获得/失败/退款）；`IWatchTogetherService` 上的 `StartChipInAsync` / `ContributeAsync` / `GetChipIn`。
- ✅ **带自动 SFU 中继的群组视频通话** — `GroupVideoService` / `IGroupVideoService`。≤ 3 名参与者使用全网格拓扑；在 `SfuThresholdParticipants`（4）处自动切换到 SFU，通过 `GroupVideoSignaling(SfuAssigned)` 重新分配中继。全网格模式下扇出，SFU 模式下仅发送中继。信令数据包类型 `GroupVideoSignaling = 35`。
- ✅ **BLE GATT 传输模拟** — `SimulatedBleGattTransportService`（`IBleTransportService`）。通过 `BleGattFramer` 进行 GATT MTU 分帧（1024 B/帧，`[2B count][2B index][payload]`），进程内静态节点注册表，广告广播。所有 `BleMaxPayloadBytes` 约束均已执行。
- ✅ **Wi-Fi Direct 传输模拟** — `SimulatedWifiDirectTransportService`（`IWifiDirectService`）。显式 `ConnectAsync`/`DisconnectAsync` 生命周期，直接大负载投递（无分帧），双向 `PeerConnected`/`PeerDisconnected` 事件。
- ✅ **NearLink 传输模拟** — `SimulatedNearLinkTransportService`（`INearLinkTransportService`）。4096 B 帧 MTU，500 节点注册表，`ConnectedPeerCount`，运行时可设置 `IsAvailable`。
- ✅ **RF 启动模拟测试** — 双节点互操作测试（`SimulatedTransportTests`）：BLE + NearLink `MeshPacket` 往返，WiFi Direct 64 KB 负载传输。软件层完全验证；需要硬件设备实验室进行板上验证。

**已完成（C# 传输层——全部快速失败）：**
- ✅ **BLE GATT 真实传输** — `WinBleGattTransportService`（Windows WinRT）+ `android/blue/`（Android GATT 服务器）。完整 RF 启动测试位于 `samples/AetherNet.BleRfTest/`。
- ✅ **Wi-Fi Direct 真实传输** — `WinWifiDirectTransportService`（WinRT，`WiFiDirectAdvertisementPublisher` + TCP StreamSocket 端口 8888）+ `android/green/`（`WifiP2pManager`）。RF 测试位于 `samples/AetherNet.WifiDirectRfTest/`。
- ✅ **HTTP 中继传输（Aether Purple）** — 带 10 秒长轮询、`PowerCostRelative = 100`、始终作为最后手段的 `HttpRelayTransportService`。中继服务器位于 `samples/AetherNet.RelayServer/`（ASP.NET Core minimal API，端口 5200）。RF 测试位于 `samples/AetherNet.RelayRfTest/`。
- ✅ **NFC（Aether White）** — `android/white/` 实现带 AID `F061657468657200` 的 `HostApduService`。`WinNfcStubTransportService` 记录了两种 Windows 近似路径：(1) 带 RSSI 门限 ≥ −40 dBm 的 NDEF-over-BLE-GATT（无 NFC 芯片时模拟轻触连接，`IsAvailable = 蓝牙可用`）；(2) 通过 `Windows.Devices.SmartCards` PC/SC 使用 ACR122U USB 读卡器（`IsAvailable = 已枚举非接触式读卡器`）。升级路径：当 Microsoft 推出第一方 P2P NFC API 时实现 `ITransportService`。
- ✅ **NearLink（Aether Teal）** — **`harmonyos/teal/`** — 使用 `@kit.NearLinkKit`（`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`）的完整 HarmonyOS 5.0.1（API 13）ArkTS 实现；`isAvailable` 在运行时探测。`WinNearLinkStubTransportService` + `android/teal/` 记录了 SSAP-over-BLE 近似：带 Aether SLE 服务 UUID `61657468-6572-0003-0000-000000000000` 的 BLE GATT——API 与 SSAP 类似，与真实 NearLink 硬件不具有线路兼容性。升级路径：将 BLE GATT 调用替换为 `ssapc_*`/`ssaps_*` SDK 调用；UUID 和 `TransportManager` 插槽不变。
- ✅ **LoRa / CircleLink（Aether Red）** — `LoRaCircleLinkStub` + `android/red/` 记录了 Meshtastic-over-BLE-LR 近似：BLE 5.0 编码物理层 S=8（室外约 1.3 km）上的完整 Meshtastic 线路格式（16 字节头 + AES-256-CTR protobuf），带托管洪泛路由和 RSSI 加权竞争窗口。与真实 LoRa 硬件的桥接节点联合自动工作（相同的 Meshtastic 数据包格式，无需转换）。升级路径：将 BLE LR 无线电替换为 SX1276/SX1278 AT 命令或 SPI 驱动；数据包格式和路由不变。

**开放中——追踪于 `OPEN_ISSUES.md`：**
- 在真实硬件上进行 RF 启动：在物理 BLE / Wi-Fi Direct 设备上进行端到端双节点互操作测试（模拟测试通过；需要硬件实验室会话）
- NearLink：`harmonyos/teal/` 已完成；需要华为 Mate 60/70 / Pura 70 Pro+ / Mate X6 硬件（非华为设备不具备 NearLink 芯片）。Windows + Android 自动回退至 SSAP-over-BLE 近似。
- LoRa / CircleLink：真正的 LoRa 范围需要无线电模块。没有无线电模块时，Meshtastic 线路格式通过 BLE LR（~1.3 km）传输，并支持与真实 LoRa 硬件的桥接节点联合。

**尚未开放外部贡献：**
- 协议仍处于积极开发阶段。目前不接受外部贡献。
- NearLink 传输实现、Android/iOS 集成示例、额外传输后端、性能基准测试以及协议模糊测试在内部追踪，将在项目达到稳定公开贡献点时开放。

## 项目结构

```
aether-protocol/
  src/
    AetherNet.Core/          Protocol models, constants, packet serialization
    AetherNet.Security/      Signal Protocol, Ed25519, packet signing
    AetherNet.Transport/     Transport abstractions, NearLink, in-process simulator
    AetherNet.Messaging/     Message handling and relay
    AetherNet.Storage/       DTN store-and-forward persistence
    AetherNet.Streaming/     Adaptive bitrate streaming, video models and interfaces
    AetherNet.Voice/         Voice calls and group voice
    AetherNet.Content/       Content verification and chunked transfer
  samples/
    AetherNet.Demo.Console/  Interactive demo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust implementation
  typescript/             TypeScript implementation
  python/                 Python implementation
  go/                     Go implementation
  kotlin/                 Kotlin/JVM implementation
  swift/                  Swift implementation
  c/                      C implementation
  docs/
    PROTOCOL_SPEC.md      RFC-style protocol specification
```

## 添加新的传输方式

实现 `ITransportService`：

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

在 DI 中注册，`TransportManager` 会自动将其纳入传输选择，按功耗排序。

## 与其他协议的对比

| 协议 | 局限性 | Aether 的优势 |
|----------|-----------|-----------------|
| **Briar** | 仅限 Android，依赖 Tor | 跨平台，纯网格 |
| **Meshtastic** | 仅限 LoRa（最高 30 kbps） | 多传输（BLE + WiFi + NearLink），支持语音和流媒体 |
| **Reticulum** | Python，社区规模小 | 8 种语言，全部线路兼容 |
| **libp2p** | 假设存在互联网骨干 | 离线优先，零基础设施可用 |
| **Yggdrasil** | 覆盖网络，需要互联网 | 物理层网格，无需互联网 |
| **Signal** | 无网格，需要互联网 | 可离线工作，P2P，网格中继，相同的端到端加密 |

## 扩展点

协议可独立工作。这些接口允许你在需要时接入自己的后端：

- `IAetherNetIncentiveProvider` — 奖励中继流量的节点（无操作默认值：利他中继）
- `IAetherNetBackendClient` — 有互联网时与服务器同步（无操作默认值：完全离线）
- `IAetherNetFeatureFlagProvider` — 在运行时切换协议功能（无操作默认值：全部启用）

三者都附带无操作实现。移除它们，一切照常运行。

## 贡献

外部贡献尚未开放。项目仍处于积极开发阶段。请等待我们宣布公开贡献窗口时再来查看。

## 安全

负责任披露政策请参见 [SECURITY.md](SECURITY.md)。

## 许可证

MIT 许可证。参见 [LICENSE](LICENSE)。
