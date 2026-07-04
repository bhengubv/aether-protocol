# AetherNet — 离线优先的网格网络协议

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet 是一个开源的、采用 MIT 许可证的网格网络协议**，用于向附近的人发送消息、文件、语音和视频——**无需互联网、无需服务器、无需注册**。设备通过蓝牙、Wi-Fi Direct、NearLink 和 LoRa 直接连接；当接收方不在范围内时，消息会通过其他设备跳转，并等待最长 72 小时以寻找路由。它以**八种编程语言提供字节完全相同的实现**——C#、Rust、TypeScript、Python、Go、Kotlin、Swift 和 C。

与附近的人共享文件、消息和数据流。无需 WiFi，无需移动数据，无需注册。就像 AirDrop，但它可以与所有人、在所有平台上使用。

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **一个协议，八种语言，线路上完全一致。** Aether 以 **C#、Rust、TypeScript、Python、Go、Kotlin、Swift 和 C** 实现——每个数据包在所有这些语言中都是字节完全相同的，并由 CI 中的共享跨语言测试用例库强制保证。用这八种语言中的任意一种构建你的节点；它都能与其他所有语言互通。本 README 也提供 11 种人类语言版本（上方链接）。

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

## 你能获得什么 — 每种服务，在每种语言中

Aether 不仅仅是一种传输方式。协议保留的每个数据包类型现在都是**全部 8 种语言中真实可用的服务**，且每一种都序列化为**字节完全相同的线路数据包**——由 Go 节点构建的数据包可被 Swift、Rust、C、Python、TypeScript、Kotlin 或 C# 节点原样解码。每种服务都固定至 `fixtures/<service>/` 下的共享跨语言测试用例，并由各语言的单元测试执行，其中 Swift 和 C 还额外在 macOS 构建服务器上验证。

| 能力 | 功能说明 | 数据包类型 | 测试用例 | 8/8 |
|---|---|:-:|---|:-:|
| **在场信标与查询** | 宣告“我在这里”并询问“周围有谁？”——通过**轮换的、密钥派生的临时 ID**（而非你的真实身份）加上粗粒度 geohash 进行 | 21, 22 | `fixtures/presence/` | ✅ |
| **心跳** | 已连接对等节点之间的轻量存活保活 | 10 | `fixtures/heartbeat/` | ✅ |
| **资料同步** | 通过网格与对等节点交换已签名的资料卡 | 23 | `fixtures/profiles/` | ✅ |
| **临时 ID 通告** | 私密地告知好友你当前轮换的路由 ID，以便在其轮换后仍能联系到你 | 56 | `fixtures/erid/` | ✅ |
| **预密钥交换** | 通过网格请求并递送 Signal 预密钥包，以便与从未谋面的人引导建立端到端会话 | 25, 26 | `fixtures/prekey/` | ✅ |
| **频道** | 发往私密的、仅限成员的群组频道的已签名消息 | 7 | `fixtures/channels/` | ✅ |
| **一键通话** | 对讲机语音帧（不透明的编码音频负载） | 15 | `fixtures/media/` | ✅ |
| **屏幕共享** | 屏幕共享视频帧（不透明的编码视频负载） | 32 | `fixtures/media/` | ✅ |
| **通话控制** | 语音和视频通话的振铃/接听/拒绝/挂断信令 | 27 | `fixtures/videocall/` | ✅ |
| **SOS 确认** | 向发送方确认其紧急广播已被接收 | 6 | `fixtures/sos/` | ✅ |
| **空间路标** | 用于“我周围有什么”层的位置标记发现碎片 | 40 | `fixtures/space/` | ✅ |
| **锻造通告** | 向网格广告一个派生/锻造的内容工件 | 41 | `fixtures/forge/` | ✅ |
| **保险库分片请求** | 获取一个纠删码存储分片（任意 N 个分片中的 K 个即可重建文件） | 42 | `fixtures/vaultshard/` | ✅ |
| **带宽测量** | 探测/确认/传播链路吞吐量，使网格路由经由最粗的管道（ABMF） | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

这些服务位于已经完成的**消息传递、一对一和群组语音、视频通话、直播、共同观影、AODV 路由、DTN 存储转发以及 SOS 洪泛**服务之上——它们同样在全部 8 种语言中实现。

> **此处“已构建”的确切含义。** 每种服务都会产生并处理其线路数据包、触发正确的事件，并固定至整个语言家族都必须匹配的字节级测试用例。你的应用负责将服务连接到它的 Signal 会话、路由表和本地状态。这是协议层——已在代码、测试和跨语言字节测试用例中得到证明——与其他所有部分建立在同样诚实的 RF 基础之上：任何最终经由无线电传输的路径，在 `OPEN_ISSUES.md` 中追踪的硬件启动完成之前，都属于现场未经验证。

## 安全与隐私

除线路服务套件之外，Aether 还提供一个小型的**安全与隐私层**——身份密钥管理与链路层反追踪。与其他一切一样，每一项都在**全部 8 种语言**中实现，并固定至 `fixtures/<feature>/` 下的共享跨语言测试用例（Swift 和 C 还额外在 macOS 构建服务器上验证）。这些*并非* 18 个线路服务之外再多出的四个：其中三个根本**不定义任何新的线路数据包类型**，第四个则将自己的信封承载于**现有的 DTN/网格路径之内**，而非作为一个新的保留数据包。

| 能力 | 功能说明 | 层 | 测试用例 | 8/8 |
|---|---|---|---|:-:|
| **恢复助记词备份** | 将身份备份为**24 个词的 BIP-39** 助记词，并在任意设备上恢复。标准 BIP-39（已对照官方 Trezor 向量验证），带 SHA-256 校验和，因此拼错的词会被*拒绝*，绝不会悄然出错。无服务器、无托管方——助记词**即是**身份本身。 | 本地 | `fixtures/bip39/` | ✅ |
| **蓝牙追踪防护** | 派生一个轮换的、密钥派生的 BLE **服务 UUID**（HMAC-SHA256，15 分钟窗口）以及**可解析私有地址**（IRK + RFC 的 `ah` 函数，AES-128）——BLE 广播方所需的反追踪材料，使被动扫描器无法跨时间或地点将其关联。 | 链路层 | `fixtures/bleprivacy/` | ✅ |
| **紧急擦除** | 一个**胁迫 PIN**（SHA-256，恒定时间比较），在受胁迫时安全擦除每一个身份密钥——先以随机数覆写再清零——不留任何可恢复之物。 | 本地 | `fixtures/panicwipe/` | ✅ |
| **多设备同步** | 在你*自己的*设备之间进行**去中心化、无服务器**的同步：一个 Ed25519 签名的 **DeviceLink** 将它们配对，而后写入者胜的 **SyncRecord** 信封调和状态——通过现有的 DTN/网格进行端到端加密承载，无云端账户、无同步服务器。 | 承载于 DTN | `fixtures/sync/` | ✅ |

**一处诚实的不对称。** 多设备的 `DeviceLink` 由 Ed25519 签名，且该签名在**8 种语言中的 7 种里字节完全相同**。Apple 的 CryptoKit 故意*随机化* Ed25519 签名，因此在 Swift 上那 64 个签名字节每次都不同——但**被签名的主体是字节完全相同的**，且每个链接在全部 8 个 SDK 上仍能验证通过，因此 Swift 达到的是**验证**对等，而非签名字节对等。这是一项平台密码学特性，而非缺陷，也是这四项功能中唯一让“字节完全相同”带星号之处。完整的线路格式见 [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12；威胁模型见 [`THREAT_MODEL.md`](THREAT_MODEL.md)。

## 传输方式

每种传输方式在代码库中都有一个颜色名称。`IsAvailable` 会屏蔽硬件不支持的路径——`TransportManager` 会自动跳过这些路径并回退到下一个可用传输方式。

**状态图例：** ✅ 真实、已构建并验证 · ⏳ 真实，验证进行中 · ⚠️ 在部分平台上真实，在其他平台上为桩实现 · ❌ 桩实现（尚无传输代码）。

| 颜色 | 名称 | 范围 | 带宽 | 状态 |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ 真实 — Windows（WinRT）+ Android（`android/blue/`） |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ 真实 — Windows（WinRT）+ Android（`android/green/`） |
| 🟣 Aether Purple | HTTP / QUIC 中继 | 无限 | ~10 Mbps | ✅ 真实 — Windows；中继服务器位于 `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | 互联网数据通道 | 无限 | ~100 Mbps | ✅ 全部 8 种语言中真实 — **在全部 8 种语言中经环回验证**（C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust 各自让两个对等节点通过真实的 ICE 数据通道交换字节） |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ 在 Android 上真实（`android/white/`）；Windows = 真实的 BLE-GATT + RSSI −40 dBm 接近近似（`WinNfcBleTransportService`，可编译 net9/10，运行时未验证）——`Windows.Networking.Proximity` 在 Win 11 中已移除 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ 在 HarmonyOS 上真实（`harmonyos/teal/`，`@kit.NearLinkKit` — 待设备端验证）；Android + Windows = 真实的 SSAP-over-BLE 近似（`android/teal/AetherNetSleService`、`WinNearLinkBleTransportService`；已通过编译 + 单元测试验证，运行时未验证） |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ 真实的 RYLR SX127x/SX126x 串口驱动（`LoRaSerialTransport`，C#/Go/Rust/C；可编译，运行时未验证——需要物理模块）；BLE Coded-PHY 桥接仍为文档化的设计 |

只有在存在平台代码的地方，无线电传输才是真实的（C#/Windows、Kotlin/Android、HarmonyOS）。其余情况下，这八个语言库为测试提供一个**进程内模拟**传输——**WebRTC 是它们共有的第一个真实传输**（已完成；跨各语言经环回验证）。

优先级依功耗排序：优先使用无线电网格，然后是作为直接互联网路径的 WebRTC，最后才是 HTTP/QUIC 中继。

## 部署层级

Aether 可在任何支持蓝牙或 Wi-Fi 的平台上运行。你所处的层级取决于目标操作系统。

---

### 标准层 — 任意平台

Android · Windows · Linux · macOS · iOS

Aether 可在任何具备蓝牙或 Wi-Fi 硬件的设备上运行。当某个无线电物理上不存在时，每个被屏蔽的传输方式都会通过现有的硬件进行近似。这些近似现在是**真实代码**（已通过编译验证；在完成 2 设备/硬件 RF 测试之前**运行时未验证**）：

- **NearLink（Aether Teal）** — 在 Android（`android/teal/AetherNetSleService`）和 Windows（`WinNearLinkBleTransportService`）上的真实 SSAP-over-BLE-GATT 近似（Aether SLE UUID `61657468-6572-0003-…`）；已通过编译 + 单元测试验证，运行时未验证。真实的 NearLink 无线电仅存在于 HarmonyOS 上（`harmonyos/teal/`，待设备端验证）。
- **LoRa（Aether Red）** — 真实的 RYLR SX127x/SX126x 串口驱动（`LoRaSerialTransport`，见于**全部 8 种语言**——C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin；每个移植版本均通过编译验证，包括 Mac 构建服务器上的 Swift + C；运行时未验证——需要物理模块）。Meshtastic-over-BLE-Coded-PHY 桥接（~1.3 km）仍为文档化的设计；真正的远距离 LoRa 需要具备 LoRa 能力的节点（网关、SBC 或带 LoRa 模块的三防手持设备）。
- **NFC（Aether White）** — 在 Android 上真实（HCE）。Windows 现已具备真实的 BLE-GATT + RSSI −40 dBm 接近近似（`WinNfcBleTransportService`，可编译 net9/10；运行时未验证）；存在读卡器时使用 ACR122U PC/SC。

哪些部分在各处均为真实且完全相同：**BLE、Wi-Fi Direct、HTTP/QUIC 中继以及 WebRTC P2P 传输（在全部 8 种语言中经环回验证）**，外加 Signal Protocol 安全性（X3DH + 双棘轮）、AODV 路由、DTN 存储转发、SOS 广播、语音和流媒体。

**诚实的状态：** BLE + Wi-Fi Direct + 中继为生产级真实实现；**WebRTC P2P 真实且在全部 8 种语言中经环回验证**（两个对等节点通过真实的 ICE 数据通道交换字节——Rust 已在具备可用 UDP ICE 的 `.201` Linux 机器上确认）；NearLink / LoRa / Windows 上的 NFC 近似现在是可编译的真实代码（LoRa 在全部 8 种语言中通过编译验证，包括 Mac 构建服务器上的 Swift + C；NearLink-Android 还通过了单元测试），但**运行时未验证**——尚无硬件/2 设备 RF 测试。它们在代码层面参与网格；不要指望这三者具备现场验证过的 RF 而部署它们。

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

全部 8 种语言产生字节完全相同的线路数据包，通过 CI 中运行的 17 个规范线路格式测试用例和 6 个 Signal 测试向量验证（`fixtures/expected/*.bin`，`fixtures/signal/expected/*.json`）。路由（AODV 风格 RREQ/RREP）、DTN 存储转发、SOS 广播、语音、流媒体以及安全加固服务在每种语言中均已实现，所有 8 种实现共有约 **3,000 个测试**：

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

跨语言 Signal 互操作性以 `fixtures/signal/` 为基准，包含 X3DH（`x3dh_basic`）、对称棘轮（`ratchet_step_basic`、`ratchet_step_three_iterations`）、KDF_RK（`kdf_rk_basic`）以及完整的 X3DH 会话往返（`x3dh_session_msg1`、`x3dh_session_reply`）的共享测试向量。每种实现都必须针对这些测试用例产生字节完全相同的输出。所有 8 种语言现已完整实现 Signal 会话（`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`）。

除线路格式和 Signal 之外，**整套线路服务套件**——在场、心跳、资料同步、临时 ID 通告、预密钥交换、频道、一键通话、屏幕共享、通话控制、SOS 确认、空间路标、锻造通告、保险库分片请求以及带宽测量（参见**你能获得什么**）——同样在全部 8 种语言中实现，并固定至各自的测试用例（`fixtures/presence/`、`fixtures/media/`、`fixtures/bandwidth/`、`fixtures/prekey/`、`fixtures/videocall/`、`fixtures/vaultshard/` 及同类）。在协议层，没有任何功能是仅限 C# 的。

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
- 线路格式：跨 8 种语言字节完全相同，由 17 个规范测试用例和 CI 中的跨语言断言锚定（`fixtures/expected/*.bin`）
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

**已完成（全部 8 种语言——完整的线路服务套件）：**
- ✅ **每个保留的数据包类型现在都是全部 8 种语言中真实、字节完全相同的服务。** 在场信标/查询（21/22）、心跳（10）、资料同步（23）、临时路由 ID 通告（56）、预密钥交换（25/26）、频道（7）、一键通话（15）、屏幕共享（32）、通话控制（27）、SOS 确认（6）、空间路标（40）、锻造通告（41）、保险库分片请求（42）以及带宽测量 / ABMF（53/54/55）。每一种都是一个精简服务（产生 + 处理 + 事件），由宿主将其连接到自己的 Signal 会话和路由表；每一种都固定至共享的跨语言测试用例（`fixtures/presence/`、`fixtures/media/`、`fixtures/bandwidth/`、`fixtures/prekey/`、`fixtures/videocall/`、`fixtures/vaultshard/`、`fixtures/channels/`、`fixtures/profiles/`、`fixtures/heartbeat/`、`fixtures/erid/`、`fixtures/space/`、`fixtures/forge/`、`fixtures/sos/`），并由各语言的单元测试执行，其中 Swift 和 C 在 macOS 构建服务器上验证。参见**你能获得什么**。

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
- ✅ **（已解决 v1.2.0）** 消费者协议表面（Wave 16/17）——用于入站捆绑包的 `IDtnService.BundleReceived` 事件（[#59](https://github.com/bhengubv/aether-protocol/issues/59)）、应用层命名/发现目录（[#60](https://github.com/bhengubv/aether-protocol/issues/60)）、作者打赏接口（[#61](https://github.com/bhengubv/aether-protocol/issues/61)）。全部 3 项以增量方式跨 8 种语言交付，并具备字节相等的跨语言测试用例。参见 CHANGELOG。

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

## 常见问题

**AetherNet 没有互联网也能工作吗？**
可以——它是离线优先的。设备通过蓝牙、Wi-Fi Direct、NearLink 或 LoRa 直接通信，并通过其他设备逐跳中继消息，无需互联网连接、基站或服务器。当没有活跃路由时，消息会被保留（延迟容忍的存储转发）最长 72 小时，直到有路由开通。

**它是端到端加密的吗？**
是的。AetherNet 使用 Signal Protocol（X3DH 密钥协商加上基于 X25519 的双棘轮）进行端到端加密，使用 AES-256-GCM 加密消息负载，并在每个数据包上使用 Ed25519 签名。中继消息的设备无法读取消息内容。

**它使用哪些传输方式？**
蓝牙 LE、Wi-Fi Direct、NearLink（SLE）、LoRa/CircleLink 串口无线电、HTTP/QUIC 中继，以及用于直接互联网点对点的 WebRTC。协议会为每个数据包自动选择功耗最低的可用传输方式，并回退到下一个。

**它有哪些编程语言的实现？**
八种——C#、Rust、TypeScript、Python、Go、Kotlin、Swift 和 C。每种实现都产生字节完全相同的线路数据包，并由 CI 中的共享跨语言测试用例库强制保证，因此由一种语言构建的数据包可被任何其他语言原样解码。

**它与 Meshtastic、Briar 或 Bridgefy 有何不同？**
Meshtastic 仅支持 LoRa；AetherNet 是多传输的（蓝牙 + Wi-Fi + NearLink + LoRa），除消息外还承载语音、视频和流媒体。Briar 仅限 Android 且经由 Tor 路由；AetherNet 是跨平台的纯网格。与封闭的 SDK 不同，AetherNet 采用 MIT 许可证，并以八种语言开放实现。上方的对比表有详细信息。

**它可以用于生产环境吗？**
协议层——线路格式、Signal 安全性、路由、DTN 存储转发以及完整的服务套件——已在全部八种语言中实现并测试。无线电传输在存在平台代码的地方是真实的（Windows 和 Android 上的蓝牙和 Wi-Fi，以及各处的 WebRTC），在其他地方尚待硬件启动而现场未经验证，这些都在 `OPEN_ISSUES.md` 中如实追踪。部署前请阅读每个部分中的状态说明。

**它采用什么许可证？**
MIT——可免费用于商业和开源用途。参见 [LICENSE](LICENSE)。

**AetherNet 由谁构建？**
它是作为 The Geek Network 网格生态系统背后的开放协议而开发的，在南非构建，旨在实现有或没有移动数据都能工作的通信。

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

## 翻译

本 README 以英文维护，并翻译为 [`docs/i18n/`](docs/i18n/) 下的另外 10 种语言：Français、Español、العربية、中文简体、日本語、Deutsch、Português (BR)、Русский、فارسی 和 한국어。**英文版本为权威来源**——当翻译与英文文本不一致时，以英文文本为准，翻译可能滞后一到两个版本。无论你阅读哪种语言，所描述的协议、代码、测试用例和行为都是完全相同的。
