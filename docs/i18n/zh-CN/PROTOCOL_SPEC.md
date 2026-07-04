# Aether 网状网络协议规范

**版本：** 2.0
**状态：** 已与 HEAD 对齐（2026-05-05）
**日期：** 2026-03-15（初稿）；2026-05-05（§2、§4、§10、§11 已对齐，§3/§9 已验证）
**作者：** The Other Bhengu (Pty) Ltd t/a The Geek 及 Bhengu B.V.

> **读者提示。** 本文档的早期草稿早于 8 语言线路格式对齐以及全系列移植至 X25519 + Signal Double Ratchet 之前。截至 2026-05-05，§2（数据包格式）、§3（路由）、§4（密钥交换）、§9（DTN）描述已实现的协议；§10（视频流）和 §11（一起观看）描述目标协议——它们已完成线路定义和夹具测试，但编解码器 / BitTorrent / ChipIn 流水线尚未绑定到脚手架。在本文档与实现存在分歧的地方，C# 参考实现具有权威性。
>
> - 规范线路字节：`fixtures/expected/*.bin`（17 个命名用例）
> - 参考序列化器：`src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - 参考 Signal 栈：`src/AetherNet.Security/Services/SignalProtocolService.cs`
> - 参考路由：`src/AetherNet.Core/Routing/RoutingService.cs`
> - 参考 DTN：`src/AetherNet.Core/Dtn/DtnService.cs`
> - 跨语言线路互操作证明：`fixtures/README.md`
> - 跨语言 Signal 互操作证明：`fixtures/signal/README.md`

---

## 目录

1. [摘要](#1-abstract)
2. [数据包格式](#2-packet-format)
3. [路由算法](#3-routing-algorithm)
4. [密钥交换](#4-key-exchange)
5. [传输层要求](#5-transport-layer-requirements)
6. [发现协议](#6-discovery-protocol)
7. [安全模型](#7-security-model)
8. [SOS 广播](#8-sos-broadcast)
9. [DTN 存储转发](#9-dtn-store-and-forward)
10. [视频流](#10-video-streaming)
11. [一起观看](#11-watch-together)
12. [安全与隐私层](#12-security--privacy-layer)

---

## 1. 摘要

Aether 是一种去中心化网状网络协议，专为网络连接间歇或完全缺失的环境设计。它提供以下能力：通过异构短距离传输（蓝牙低功耗、Wi-Fi Direct、NearLink）实现多跳数据包路由；使用基于 X3DH 派生的密钥协商与对称棘轮的端到端加密；支持延迟容忍的存储转发交付；以及紧急 SOS 洪泛机制。该协议与传输层无关：任何能够在对等体之间发送和接收字节数组的物理层均可作为有效的 Aether 传输。节点通过通用硬件标识符（UHID）标识，并通过 Ed25519 身份密钥进行认证。Aether 旨在作为通用网络层——生态系统中的每个应用程序都注册 Aether 服务，没有互联网连接的节点通过将网格流量桥接到互联网的网关对等体访问更广泛的网络。

---

## 2. 数据包格式

> 已于 2026-05-05 对照 `src/AetherNet.Core/Protocol/PacketSerializer.cs` 及 `fixtures/expected/` 下的 17 个夹具用例进行对齐。

### 2.1 MeshPacket 线路布局

每条 Aether 消息都封装在一个 `MeshPacket` 中。各字段在线路上按**以下确切顺序**出现：

| 偏移 | 字段 | 类型 | 大小 | 说明 |
|-----|------------------|---------------------------------|------------|-------|
| 0 | ProtocolVersion | uint8 | 1 | `1` = 未签名（旧版），`2` = 已签名（当前） |
| 1 | Type | uint8 | 1 | 数据包类型枚举（见 §2.4） |
| 2 | Id | UUID，RFC 4122 大端 | 16 | 用于去重的数据包标识符。使用**大端**字节序，而非 .NET 默认的混合字节序 Guid。 |
| 18 | Priority | uint8 | 1 | 优先级（0 = 普通，255 = SOS）。**线路字段为 1 字节；超过 255 的值必须被截断。** |
| 19 | Ttl | int32，小端 | 4 | 存活时间，每跳递减。**4 字节 int32**，而非 1 字节 uint8——最大有效值约为 2³¹-1。 |
| 23 | TimestampMs | int64，小端 | 8 | Unix 纪元毫秒（UTC）。 |
| 31 | SourceUhid Len | uint16，小端 | 2 | `SourceUhid` 的 UTF-8 字节长度。最大 65535。 |
| 33 | SourceUhid | UTF-8 字节 | N | 发送方的 UHID；允许为空但不常见。 |
| 33+N | DestinationUhid Len | uint16，小端 | 2 | `DestinationUhid` 的 UTF-8 字节长度。 |
| ... | DestinationUhid | UTF-8 字节 | M | 接收方的 UHID；广播时为空字符串。 |
| ... | PacketNonce Len | uint16，小端 | 2 | `PacketNonce` 的字节长度。标准值：8。 |
| ... | PacketNonce | bytes | P | 用于防重放的密码学随机随机数。 |
| ... | Payload Len | int32，小端 | 4 | `Payload` 的字节长度。负值为错误。 |
| ... | Payload | bytes | Q | 应用数据。解释取决于 `Type`。 |
| ... | Signature Len | uint16，小端 | 2 | `Signature` 的字节长度。0（未签名）或 64（Ed25519）。 |
| ... | Signature | bytes | R | 覆盖可签名数据的 Ed25519 签名（见 §2.3）。 |

**长度前缀宽度**因字段而异——`SourceUhid`、`DestinationUhid`、`PacketNonce` 和 `Signature` 使用 **2 字节（uint16）** 长度前缀；`Payload` 使用 **4 字节（int32）** 长度前缀，因为有效载荷可能超过 64 KiB。

### 2.2 最小数据包大小

当所有可变长度字段均为空（零长度 UHID、零长度随机数、零长度有效载荷、零长度签名）时，线路大小为：

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

本规范早期草稿中的 50 字节 / 52 字节数据有误。

### 2.3 线路格式图

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

具体示例请参见 `fixtures/expected/basic_data.bin`（83 字节，`fixtures/inputs.json` 中的规范输入）。各实现将针对完整夹具语料库进行验证——任何偏差都会导致跨语言夹具验证测试失败。

### 2.4 可签名数据构造

签名（线路上的 `Signature` 字段）计算于独立的规范字节序列之上——**而非**线路字节本身。这允许线路布局演进而不破坏签名，并允许中间节点在不查看明文有效载荷的情况下验证完整性（仅对其 SHA-256 哈希进行签名）。

可签名字节序列为以下内容的拼接：

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

> 注意与 §2.1 线路布局的刻意差异：可签名数据对 `Type`、`Length`、`Ttl` 和 `Priority` 使用 **4 字节 int32**，而线路分别使用 1 字节 / 2 字节 / 4 字节 / 1 字节。这是有意为之——可签名形式跨语言可移植，使用固定宽度字段；线路形式为 BLE PDU 经济性而设计得紧凑。实现必须在编码可签名字节前将 `Priority` 截断到 `[0,255]`，否则接收方（在线路字节中看到 0..255）会推导出不同的可签名缓冲区，导致验证失败。

参考实现位于 `src/AetherNet.Security/Services/PacketSigningService.cs::BuildSignableData`，移植时必须阅读。

### 2.5 数据包类型

| 值 | 名称 | 方向 | 描述 |
|-------|-------------------|---------------|-------------|
| 1 | RouteRequest | 广播 | AODV 路由请求 |
| 2 | RouteReply | 单播 | AODV 路由回复（必须由目标节点签名） |
| 3 | Data | 单播 | 应用数据 |
| 4 | Ack | 单播 | 投递确认 |
| 5 | SosBroadcast | 洪泛 | 紧急广播（见第 8 节） |
| 6 | SosAck | 单播 | SOS 确认 |
| 7 | ChannelMessage | 组播 | 群组频道消息 |
| 8 | ChunkRequest | 单播 | P2P 内容块请求 |
| 9 | ChunkData | 单播 | P2P 内容块响应 |
| 10 | Heartbeat | 广播 | 周期性存活信号 |
| 11 | StreamAnnounce | 广播 | 直播流公告 |
| 12 | StreamSegment | 单播/树状 | 直播流媒体片段 |
| 13 | StreamSubscribe | 单播 | 加入流中继树请求 |
| 14 | StreamUnsubscribe | 单播 | 离开流中继树 |
| 15 | VoicePtt | 单播 | 按住通话语音帧 |
| 16 | VoiceCall | 单播 | 实时语音通话帧 |
| 17 | VoiceSignaling | 单播 | 语音通话建立/拆除 |
| 18 | DtnBundle | 单播 | DTN 存储转发包（见第 9 节） |
| 19 | DtnCustodyAck | 单播 | DTN 托管转移确认 |
| 20 | DtnDeliveryReceipt | 单播 | DTN 端到端投递确认 |
| 21 | PresenceBeacon | 广播 | 存在和可用性公告 |
| 22 | PresenceQuery | 单播 | 存在状态请求 |
| 23 | ProfileSync | 单播 | 个人资料元数据同步 |
| 24 | TipPacket | 单播 | 节点打赏（通过 LedgerAPI 结算） |
| 25 | PreKeyRequest | 单播 | 请求对等体的预密钥包 |
| 26 | PreKeyResponse | 单播 | 预密钥包投递 |
| 27 | VideoCall | 单播 | 加密视频帧（H.264/H.265/VP8 NAL 单元） |
| 28 | VideoSignaling | 单播 | 视频通话建立：提议、应答、拒绝、再见、编解码器协商 |
| 29 | WatchSync | 单播 | 同步播放命令：播放、暂停、跳转、速度 |
| 30 | WatchReaction | 组播 | 观看期间带时间戳的表情或语音反应 |
| 31 | VideoFrame | 单播/SFU | 群组视频帧（SFU 中继分发给参与者） |
| 32 | ScreenShare | 单播 | 屏幕共享帧（与视频使用相同流水线，但单独标记） |
| 33 | WatchChunkRequest | 单播 | 偏向播放位置的优先块请求 |
| 34 | TorrentMetadata | 组播 | BitTorrent .torrent 文件或磁力链接元数据交换 |

### 2.6 节点能力

节点以位字段形式公告其能力：

| 位 | 值 | 能力 | 描述 |
|-----|-------|-------------|-------------|
| 0 | 1 | Ble | 蓝牙低功耗传输可用 |
| 1 | 2 | WifiDirect | Wi-Fi Direct 传输可用 |
| 2 | 4 | Gateway | 互联网网关（将网格流量桥接到 IP 网络） |
| 3 | 8 | Relay | 愿意为他人中继数据包 |
| 4 | 16 | Sos | 支持 SOS 广播 |
| 5 | 32 | Streaming | 支持直播流中继 |
| 6 | 64 | Voice | 支持语音通话中继 |
| 7 | 128 | DtnCarrier | DTN 存储转发载体 |
| 8 | 256 | NearLink | NearLink 传输可用 |
| 9 | 512 | Video | 支持视频编解码 |

---

## 3. 路由算法

Aether 使用基于按需距离向量路由（AODV）的反应式路由协议，并扩展了密码学路由认证和 QoS 加权路由选择。

### 3.1 路由请求（RREQ）

当节点需要向没有已知路由的目标发送数据包时，它发起路由请求：

1. 发起方创建一个 `Type = RouteRequest` 的 `MeshPacket`，将 `SourceUhid` 设为自身，`DestinationUhid` 设为目标，`TTL = 7`（默认值）。
2. 数据包广播给所有直接连接的对等体。
3. 收到 RREQ 的每个中间节点：
   a. 通过数据包 `Id` 检查是否已见过此 RREQ。若已见过，则静默丢弃（去重）。去重缓存最多容纳 `DeduplicationCacheSize` 条条目（默认 10,000），达到上限后完全清空。
   b. 安装到 RREQ 发起方的**反向路由**。反向路由将接收到 RREQ 的对等体的 UHID 记录为下一跳。跳数由 `DefaultTtl - packet.Ttl + 1` 推导。
   c. 若它就是目标节点，则生成 RREP（见第 3.2 节）。
   d. 若它已有到目标的有效路由，则可以代表目标生成 RREP。
   e. 否则，递减 TTL 并重新广播 RREQ。
4. 发起方以 **5,000 ms** 超时（`RouteTimeoutMs`）等待 RREP。若未收到 RREP，则路由发现失败。

### 3.2 路由回复（RREP）

当目标（或具有有效路由的中间节点）生成路由回复时：

1. 创建 `Type = RouteReply` 的 `MeshPacket`，`SourceUhid` 设为目标节点，`DestinationUhid` 设为 RREQ 发起方。
2. **安全要求：** RREP 必须由目标节点的 Ed25519 身份密钥签名。签名覆盖标准可签名数据（第 2.3 节）。这防止恶意中间节点进行路由投毒。
3. RREP 通过 RREQ 传播期间安装的反向路由进行单播回传。
4. 转发 RREP 的每个中间节点：
   a. 针对声称的源方公钥验证 RREP 签名（如已知）。若验证失败，则丢弃 RREP 并记录警告。
   b. 安装到 RREP 源方（目标节点）的**前向路由**，将 RREP 的发送方作为下一跳。
   c. 递减 TTL 并向 RREQ 发起方转发。
5. 当 RREP 到达发起方时，通过 `TaskCompletionSource` 追踪的待处理路由请求以已安装路由解决。

### 3.3 路由维护

- **基于 TTL 的过期：** 每条路由条目携带 `ExpiresAt` 时间戳，设为 `now + 300 秒`（`RouteExpirySeconds`）。路由不会隐式刷新；过期后必须通过新的 RREQ/RREP 循环重新建立。
- **周期性清理：** 协议服务运行周期性心跳（默认每 300 秒一次）。每次循环中，从内存 `ConcurrentDictionary` 和 SQLite 存储中删除过期路由。
- **RREQ 去重清理：** 当已见 RREQ ID 集合超过 `DeduplicationCacheSize`（默认 10,000）条时清空。

### 3.4 路由质量与 QoS

每个 `RouteEntry` 携带 [0, 100] 范围内的 `QualityScore`，新发现路由初始化为 50。分数考虑以下因素：

- **跳数：** 通常跳数越少表示路由越快。
- **延迟：** 可用时的往返时间测量值。
- **对等体可靠性：** 下一跳对等体的可靠性分数（见第 3.5 节）。

参与打赏激励系统的节点将获得路由质量分数提升。这是一种软偏好：非打赏者始终可获得服务，但持续打赏者可能在路由选择上略有优势。提升层级为：

| 层级 | 一致性阈值 | QoS 提升 |
|---------|-----------------------|-----------|
| Bronze | 25 | +5 |
| Silver | 50 | +10 |
| Gold | 75 | +20 |

### 3.5 对等体可靠性评分

每个已知对等体被分配 [0, 100] 范围内的可靠性分数，初始化为 50（`DefaultReliabilityScore`）。分数根据观察到的行为调整：

| 事件 | 增量 |
|----------------------|-------|
| 成功中继 | +2 |
| 中继失败 | -5 |
| SOS 中继 | +5 |
| 块服务成功 | +1 |
| 块服务失败 | -10 |

可靠性分数持久化到 SQLite 并在启动时加载到内存中。分数影响路由选择：优先选择经过可靠性更高对等体的路由。

---

## 4. 密钥交换

> 已于 2026-05-05 对照 `src/AetherNet.Security/Services/SignalProtocolService.cs` 的 C# 参考实现及 `fixtures/signal/` 下的跨语言夹具语料库进行对齐。C# 参考实现通过 X25519 提供完整的 X3DH + Double Ratchet（Signal §3 + §5）。Go、Python、TypeScript、Rust、Swift 和 Kotlin 均已移植到相同的信封，在 X3DH 和 KDF_RK 夹具级别字节等效。C 语言现在也提供了完整的会话机制（`c/src/signal_protocol.c` 中的 X3DH + OPK/SPK 生命周期 + 双棘轮，并在 `c/tests/test_signal_session.c` 中有双节点端到端测试），而不仅仅是原语。在本节与代码存在分歧的地方，代码具有权威性；请在 `OPEN_ISSUES.md` 中提交问题。

Aether 实现 **X3DH**（扩展三次 Diffie-Hellman，Signal §3）用于异步会话建立，随后使用 **Signal Double Ratchet**（Signal §5）实现持续的前向保密和后妥协安全。所有会话密码学均基于 Curve25519：**X25519**（RFC 7748）用于 ECDH，**Ed25519**（RFC 8032）用于签名。

### 4.1 身份密钥

每个节点在首次启动时生成**两个**长期密钥对（无 XEdDSA；更简单的双密钥安排是每个实现所采用的）：

- **Ed25519 密钥对** — 32 字节种子（私钥），32 字节公钥。用于数据包签名（§2.4）、`SignedPreKeySignature`（§4.3）、RREP 认证（§3.2）和打赏签名。
- **X25519 密钥对** — 32 字节原始私钥和公钥。用于四次 X3DH DH 操作（§4.4）。

参考：`SignalProtocolService.InitializeIdentityKeys`。私钥仅保存在设备上；公钥在 `PreKeyBundle` 中发布。

仅针对入站数据包的**签名验证**提供 30 天 P-256 → Ed25519 迁移窗口——见 §7.5。预密钥包本身在线路上仅使用 X25519。

### 4.2 曲线选择

X3DH 和 Double Ratchet 专用 **X25519**。当前任何实现均未在会话建立中使用 P-256。本规范早期草稿描述了 P-256 ECDH；该文本早于 2026-05-05 全系列迁移至 X25519，已不再准确。

### 4.3 预密钥包

发布预密钥包以便发起方在响应方不在线时建立会话（Signal §3.4）：

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

参考：`AetherNet.Security.Models.PreKeyBundle`。线路形状契约在所有 8 种语言中相同。

**一次性预密钥（OPK）池。** 每个响应方维护包含 `OpkPoolSize`（默认 100，与 Signal 发布的指导一致）个 X25519 OPK 的池。包生成时从 FIFO 队列中弹出下一个未使用的 id，然后将池补充到目标大小。每个 OPK 只被消耗一次：响应方在第一个引用其 id 的 PreKey 消息时删除并清零私钥的一半。在 `_preKeyLock` 下，争用同一 OPK id 的并发发起方中只有一个 `EstablishResponderSession` 能成功；失败者抛出 `CryptographicException`。

参考：`SignalProtocolService.TopUpOpkPoolNoLock`（行 494-518），`SignalProtocolService.EstablishResponderSession`（行 636-718）。池语义由 `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs` 验证。

**已签名预密钥（SPK）轮换。** SPK 在首次调用包时懒惰生成，并在后续调用中复用，以防止在 X3DH 运行前并发获取包的发起方互相使彼此的包失效。定期 SPK 轮换（Signal §3.3 建议每周）是显式操作，而非包生成的副作用。

预密钥 id 使用 `RandomNumberGenerator.GetInt32(1, int.MaxValue)` 生成，并进行显式碰撞重试（最多 64 次后抛出异常）。

### 4.4 会话建立（X3DH）

完整的 X3DH（Signal §3.3）在发起方侧运行。通过 X25519 计算四次 DH 操作：

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

其中 `IK_A` / `IK_B` 为 X25519 身份密钥，`EK_A` 为仅为本次会话生成的新鲜 X25519 临时密钥，`SPK_B` 为响应方的已签名预密钥，`OPK_B` 为响应方的一次性预密钥。初始根密钥为：

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

`info` 常量 `aether-x3dh-root-v1` 在每个实现中完全相同，由 `fixtures/signal/expected/x3dh_basic.json`（字段 `root_key_hex`）固定。

参考：`SignalProtocolService.ProcessPreKeyBundleAsync`（行 554-626）。验证路径：`fixtures/signal/inputs.json` 用例 `x3dh_basic` → `fixtures/signal/expected/x3dh_basic.json`。

**包验证。** 在任何 DH 操作运行之前，发起方使用 Ed25519 针对 `IdentityKey` 验证 `SignedPreKeySignature`。验证失败时抛出 `CryptographicException` 并丢弃该包。公钥大小针对 `X25519Service.PublicKeySize`（32）进行验证；格式错误的包被拒绝。

**会话初始化。** 在 `ProcessPreKeyBundleAsync` 结束时创建 `SignalSession`，包含：

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal 规范的 X3DH ↔ Double-Ratchet 集成：发起方的 X3DH 临时密钥成为其第一个 DH-ratchet 密钥对（`DHs`）。
- `RemoteEphemeralPub = SPK_B` — 响应方的已签名预密钥被视为初始对等方 ratchet 密钥（`DHr`）。
- `SendChainKey = null`，`RecvChainKey = null` — 两个链密钥均在首次发送 / 首次 DH-ratchet 接收时懒惰派生。
- `PendingPreKeyMessage = true` — 标记下一次出站 `EncryptAsync` 调用必须发出 PreKey 消息（`MessageType=1`）。

所有 DH 输出和拼接的共享密钥均通过 `finally` 块中的 `CryptographicOperations.ZeroMemory` 清零。

**拒绝不安全发送。** 如果对没有会话的对等体调用 `EncryptAsync`，调用将抛出 `InvalidOperationException`。不存在基于 UHID 的回退路径。宿主应当将消息排队（见 `MessagingService` + `SignalMessageEnvelopeCipher`）并在会话建立完成后重试。

### 4.5 Double Ratchet（Signal §5）

每侧维护一个轮换的 X25519 ratchet 密钥对（`DHs`）以及对等方最后一次看到的 ratchet 公钥副本（`DHr`）。发送方在每条消息中发布其当前 `DHs` 公钥；每当接收方观察到新的 `DHr` 时，它执行 **DH-ratchet 步骤**，通过 `KDF_RK(RK, DH(myDHs, newDHr))` 重新派生链密钥——同时重新派生根密钥和新的链密钥。

#### 4.5.1 KDF_RK

`KDF_RK` 是 HKDF-SHA256 对 64 字节块的操作，分割为 32+32 字节分别作为新根密钥和新链密钥：

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

参考：`SignalProtocolService.KdfRk`（行 857-868）。由 `fixtures/signal/inputs.json` 用例 `kdf_rk_basic` → `fixtures/signal/expected/kdf_rk_basic.json` 固定。

#### 4.5.2 对称棘轮

按照 Signal §5.1，消息密钥和链密钥使用 HMAC-SHA256 通过单字节域分离从链密钥派生：

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

参考：`SignalProtocolService.RatchetChainKey`（行 876-881）。由 `fixtures/signal/inputs.json` 用例 `ratchet_step_basic` 和 `ratchet_step_three_iterations` 固定。

本规范早期草稿描述了 `messageKey = HMAC-SHA256(chain_key, counter_bytes)` 以及通过 `HMAC(chain_key, 0x01)` 推进链密钥的方案。该方案不符合 Signal 规范且从未实现；已被规范的 0x01/0x02 分割所取代。

#### 4.5.3 接收时的 DH-Ratchet 步骤

当入站消息的 `SenderEphemeralKeyX25519` 与缓存的 `RemoteEphemeralPub` 不同时触发（常量时间比较）。

1. 将出站计数器保存为 `PreviousChainCount`（Signal §5：PN），以便对等方可以计算跨边界的跳过密钥。
2. 将 `SendCounter` 和 `RecvCounter` 重置为 0；安装新的 `RemoteEphemeralPub`。
3. 派生新接收链：`(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`。
4. 清零旧的 `myDHs` 私钥；生成新的 X25519 密钥对。
5. 派生新发送链：`(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`。

参考：`SignalProtocolService.DhRatchetReceive`（行 726-772）。

#### 4.5.4 懒惰发送链派生

发起方的首次发送执行**半步**而非完整的 DH-ratchet——X3DH 已经设置了 `DHs` 和 `DHr`，因此只需派生发送链：

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

此处**不**轮换 `DHs`。它仅在真正的接收侧 DH-ratchet 步骤时才轮换。

参考：`SignalProtocolService.DhRatchetSendOnly`（行 780-796）。

#### 4.5.5 跳过消息密钥

当消息乱序到达时，每个跳过计数器的消息密钥缓存在 `SkippedMessageKeys` 中，以 `(Hex(remoteEphPub):counter)` 为键。远端公钥绑定至关重要——来自先前链（不同 `DHr`）的乱序消息在 DH-ratchet 步骤之后仍可能到达，需要各自的每链密钥集。

限制：

- 在单个间隙中跳过超过 `MaxSkippedKeys`（1000）条时抛出 `CryptographicException` 并强制重新建立会话。
- 跨越 DH-ratchet 边界时，接收方首先在*旧*链上跳过到 `PreviousChainCount` 个密钥，然后运行 DH-ratchet 步骤，再在新链上派生密钥。

参考：`SignalProtocolService.SkipMessageKeys`（行 804-830）和解密中的跳过循环（行 366-388）。

### 4.6 加密有效载荷格式

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

参考：`AetherNet.Security.Models.EncryptedPayload`（`SecurityModels.cs` 行 55-66）。`InitiatorEphemeralKeyX25519` 字段是预 Double-Ratchet 线路信封的向后兼容别名，在 PreKey 消息中等于 `SenderEphemeralKeyX25519`；新的消费者应忽略它。

AES-GCM 参数：256 位密钥，96 位随机数（`AesNonceSize = 12`），128 位标签（`AesTagSize = 16`），标签拼接到密文后。消息密钥在 AES-GCM 加密/解密后立即通过 `finally` 块清零。

### 4.7 各语言状态

| 语言 | X3DH（4 次 DH） | Double Ratchet | OPK 池 | 夹具验证 |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET) | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C | 完整 | 完整（§5） | 池，默认 100 | x3dh_basic, ratchet_*, kdf_rk_basic |

所有 8 种语言（C# + Go + TypeScript + Python + Kotlin + Swift + Rust + C）均包含完整的 X3DH + 双棘轮会话服务，以及带懒惰补充和锁保护消耗的 100 密钥 FIFO OPK 池，与 C# 参考契约匹配。C 语言的会话服务位于 `c/src/signal_protocol.c`，双节点端到端测试位于 `c/tests/test_signal_session.c`。

---

## 5. 传输层要求

Aether 与传输层无关。任何满足 `ITransportService` 契约的物理通信信道均可参与网格。

### 5.1 ITransportService 接口契约

每个传输实现必须暴露以下内容：

**属性：**

| 属性 | 类型 | 描述 |
|--------------------|--------|-------------|
| `Name` | string | 人类可读的标识符（如 "BLE"、"Wi-Fi Direct"、"NearLink"） |
| `IsAvailable` | bool | 传输当前在此设备上是否可用 |
| `MaxBandwidthBps` | int64 | 每秒最大吞吐量（字节） |
| `MaxRangeMeters` | int32 | 最大通信距离（米） |
| `PowerCostRelative` | int32 | 相对功耗（1 = 低，10 = 高） |
| `MaxConcurrentPeers` | int32 | 最大同时对等体连接数 |

**方法：**

| 方法 | 签名 | 描述 |
|----------------|-----------|-------------|
| `SendAsync` | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | 向特定对等体发送字节数组。成功返回 true。 |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | 向对等体发送流（用于大型传输、语音、视频）。 |
| `IsConnected` | `bool IsConnected(string peerUhid)` | 检查与对等体的连接是否活跃。 |

**事件：**

| 事件 | 签名 | 描述 |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | 从对等体接收到数据时触发。 |

### 5.2 传输选择算法

`TransportManager` 根据以下条件为每个数据包选择最优传输：

1. **可用性：** 仅考虑 `IsAvailable == true` 的传输。
2. **有效载荷大小：** 若有效载荷大小不超过 `BleMaxPayloadBytes`（1,024 字节），则优先选择 BLE 以节省功耗。更大的有效载荷优先选择 Wi-Fi Direct。
3. **功耗权重：** 在可用传输中，常规流量优先选择较低的 `PowerCostRelative` 值。高优先级数据包（SOS、语音）可能覆盖此偏好。
4. **对等体连通性：** 如果传输已与目标对等体建立活跃连接（`IsConnected` 返回 true），则优先选择它以避免连接建立开销。
5. **回退：** 若没有本地传输可达目标，则通过 AetherNetAPI 将数据包排队进行服务器中继。

### 5.3 参考传输

| 传输 | 最大带宽 | 最大距离 | 功耗 | 最大对等体数 | 说明 |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0 | ~2 Mbps | 100m | 1 | 7 | 主要发现 + 小数据包 |
| Wi-Fi Direct | ~250 Mbps | 200m | 5 | 8 | 大型传输、流媒体、语音 |
| NearLink | ~900 Mbps | 200m | 3 | 16 | 华为/海思，高吞吐量 |

**BLE 有效载荷限制：** 超过 1,024 字节（`BleMaxPayloadBytes`）的数据包自动路由到 Wi-Fi Direct 或 NearLink。BLE 用于发现广播、小型控制数据包（RREQ/RREP、存在信标）和低带宽消息。

**Wi-Fi Direct** 连接超时为 10,000 ms（`WifiDirectTimeoutMs`），最多 8 个并发对等体（`MaxWifiDirectPeers`）。

---

## 6. 发现协议

### 6.1 BLE 广播

Aether 节点主要通过 BLE 广播相互发现。为防止通过静态标识符进行持久追踪，协议采用两种隐私机制：轮换服务 UUID 和身份解析密钥。

**广播周期：** 扫描开启 2 秒，关闭 8 秒（`BleScanOnMs`/`BleScanOffMs`）。广播间隔为 1,000 ms（`BleAdvertiseIntervalMs`）。在扫描间隔上添加 0-2,000 ms 的随机抖动（`BleScanJitterMaxMs`）以防止时序模式检测。

**对等体超时：** 30 秒内未重新发现的对等体被视为丢失（`PeerLost` 事件）。

### 6.2 轮换服务 UUID

为防止长期 BLE 指纹识别，广播中使用的服务 UUID 每 15 分钟轮换一次（`BleUuidRotationSeconds = 900`）：

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key` 是每个节点一次性生成并存储在安全存储中的 32 字节密钥。共享相同轮换密钥的所有 Aether 节点将在给定时间窗口内推导出相同的 UUID，从而在不暴露永久标识符的情况下实现相互发现。

在从非轮换方案过渡期间维护静态回退 UUID（`A3E7-1001-0001-0000-000000000000`），有效期 90 天。

### 6.3 身份解析密钥（IRK）

每个节点生成一个存储在安全存储中的 128 位身份解析密钥（IRK）。IRK 在密钥交换期间与可信对等体共享。

**可解析私有地址（RPA）生成：**

1. 计算 `prand = HMAC-SHA256(IRK, window_bytes)[0..2]`（3 字节）。
2. 将 `prand[0]` 的两个最高有效位设为 `01`（按 BLE 规范的 RPA 标志）。
3. 计算 `hash = AES-128-ECB(IRK, pad(prand))`，其中 `prand` 占据 16 字节零填充输入的字节 13-15。
4. 构造 RPA：`hash[0..2] || prand[0..2]`（共 6 字节）。

**RPA 解析：** 持有对等体 IRK 的节点可以通过从 RPA 的 `prand` 分量重新计算哈希来验证观察到的 RPA 是否属于该对等体。解析时间约为 O(N)，其中 N 为已知 IRK 数量，100 个对等体时基准约为 ~0.1ms。

RPA 与服务 UUID 采用相同的 15 分钟周期轮换。

### 6.4 基于 Geohash 的邻近感知

节点可选择将其位置编码为 geohash。出于隐私考虑，geohash 被截断为 4 个字符，提供约 39km x 20km 的分辨率。此精度足以支持：

- 基于邻近的频道发现
- DTN 流行病路由（向接收方最后已知的 geohash 区域复制）
- SOS 告警地理上下文

全精度 geohash 绝不通过网格传输。仅共享截断形式，且仅在节点的隐私级别允许时（`PrivacyLevel.Full` 或 `PrivacyLevel.Partial`）。

---

## 7. 安全模型

### 7.1 威胁模型

Aether 假设对手具备以下能力：

- **被动窃听：** 对手可以观察无线电范围内的所有 BLE 广播和网格流量。
- **主动注入：** 对手可以注入、修改或重放数据包。
- **Sybil 攻击：** 对手可以创建多个虚假节点身份。
- **选择性拒绝服务：** 对手作为中继节点可以选择性地丢弃数据包。

### 7.2 受保护的内容

| 属性 | 保护级别 | 机制 |
|----------|-----------------|-----------|
| 消息内容 | 完全保密 | 使用每条消息密钥的 AES-256-GCM（第 4.5 节） |
| 发送方身份 | 部分 | UHID 在数据包头部可见；BLE 地址轮换（第 6 节） |
| 接收方身份 | 部分 | 目标 UHID 在路由数据包中可见；广播数据包目标为空 |
| 路由元数据 | 最低 | 中间节点可见源/目标 UHID 和 TTL |
| 消息顺序 | 受保护 | 对称棘轮中的计数器防止重排序 |
| 消息完整性 | 完全 | 每个数据包上的 Ed25519 签名（v2） |

### 7.3 攻击抵抗

**重放攻击：**
每个数据包携带 8 字节密码学随机随机数和毫秒精度时间戳。中继节点维护 `(SenderUhid, NonceValue)` 对的去重缓存，TTL 为 5 分钟（`MaxPacketAgeSeconds = 300`）。来自同一发送方的重复随机数数据包被丢弃。时间戳早于 5 分钟的数据包无论随机数如何均被拒绝。

随机数去重缓存每 60 秒清理一次。过期条目（超过 5 分钟）被删除。

**中间人（MITM）：**
- 路由回复数据包必须携带来自声称目标节点的有效 Ed25519 签名。中间节点无法伪造 RREP，因为它们不持有目标的私钥。
- 预密钥包包含覆盖 `SignedPreKey` 的 `SignedPreKeySignature`（Ed25519），将临时 ECDH 密钥绑定到长期身份。
- 会话建立（第 4.4 节）通过预密钥验证步骤将会话密码学绑定到双方身份。

**Sybil 攻击：**
- 每个节点的可靠性分数从 50 开始，根据观察到的行为调整（第 3.5 节）。新创建的 Sybil 节点没有积累的声誉。
- 可靠性分数接近 0 的节点在路由选择中被降优先级。
- DTN 流行病路由算法使用 geohash 邻近度和中继成功历史来选择复制目标，使 Sybil 节点更难在没有真实中继贡献的情况下吸引流量。

**洪泛攻击：**
- TTL 在每跳递减，TTL = 0 的数据包被丢弃。默认 TTL 为 7，限制了任何广播的影响范围。
- 通过数据包 ID 进行的 RREQ 去重防止了广播风暴引起的放大效应。去重缓存超过 `DeduplicationCacheSize`（默认 10,000）条时被刷新。
- SOS 广播每个节点每小时限 3 次（第 8 节）。

### 7.4 密钥清零

所有中间密码学材料在使用后立即清零：

- ECDH 密钥协商产生的 `sharedSecret`：在 HKDF 派生后清零。
- 链棘轮产生的 `messageKey`：在 AES-GCM 加密/解密后清零。
- 乱序解密产生的 `skippedKey`：使用后清零并从映射中删除。
- 派生的 `RootKey`、`SendChainKey`、`RecvChainKey`：从建立上下文中清零（会话保留自己的副本）。

清零使用 `CryptographicOperations.ZeroMemory`，保证不被编译器优化掉。

### 7.5 P-256 到 Ed25519 迁移

协议支持从 ECDSA P-256 身份密钥（协议版本 1）到 Ed25519（协议版本 2）的 30 天过渡窗口：

1. 过渡期内接受协议版本 1 数据包（未签名）。
2. 签名验证首先尝试 Ed25519。如果公钥长度超过 32 字节（表明是 DER 编码的 P-256 密钥），则回退到 P-256 ECDSA 验证。
3. 30 天窗口结束后，拒绝协议版本 1 数据包。
4. 尚未迁移的节点必须以新的 Ed25519 身份重新初始化。

### 7.6 司法管辖意识

协议定义司法管辖层级以处理加密和网状网络的不同法律要求：

| 层级 | 行为 | 示例司法管辖区 |
|------|----------|-----------------------|
| 1 | 自由运营 | 南非、肯尼亚、加纳 |
| 2 | 修改运营 | 尼日利亚、印度、欧盟、美国、英国 |
| 3 | 仅网格（高风险） | 中国、俄罗斯、伊朗、阿联酋、缅甸 |
| 4 | 未知（默认仅网格） | 所有其他 |

层级选择影响功能可用性（例如，打赏/金融功能在第 3 层可能被禁用），但不会削弱加密。无论司法管辖区如何，端到端加密始终应用。

---

## 8. SOS 广播

SOS 机制是一种双路径紧急洪泛，专为用户处于危险且需要同时到达附近网格对等体和/或互联网的情况设计。

### 8.1 广播参数

| 参数 | 值 | 描述 |
|-----------|-------|-------------|
| TTL | 15 | 正常默认值（7）的两倍，确保更广泛的传播 |
| Priority | 999 | 最高优先级；抢占中继队列中的所有其他流量 |
| 速率限制 | 3/小时 | 每节点限制以防止滥用 |
| 目标 | 空 | 广播给所有对等体（无特定目标） |

### 8.2 洪泛算法

1. 发起方构造 `Type = SosBroadcast`、`TTL = 15`、`Priority = 999`、`DestinationUhid` 为空的 SOS 数据包。
2. 有效载荷为 JSON 编码，包含：
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
3. **双路径分发：** SOS 同时通过以下方式发送：
   - **网格洪泛：** 通过所有可用传输广播给所有已连接对等体。
   - **API 调用：** 发送到 AetherNetAPI 进行服务器端分发并桥接到 PanikAPI（短信/邮件分发）。
4. 两条路径相互独立地触发即忘。若 API 调用失败，网格洪泛独立继续。

### 8.3 中继行为

当节点收到 SOS 数据包时：

1. 通过数据包 `Id` 检查去重。若已见过，则静默丢弃。
2. 反序列化有效载荷并为本地 UI 触发 `SosReceived` 事件。
3. 将告警添加到活跃告警列表。
4. 若 `TTL > 1`，递减 TTL 并**重新广播给所有对等体**，无论路由表状态如何。SOS 数据包绕过正常路由——它们无条件洪泛。

### 8.4 速率限制

每个节点维护最近广播时间戳的滑动窗口。在发起新 SOS 之前：

1. 从队列中清除超过 1 小时的条目。
2. 若队列包含 3 条或更多条目（`MaxSosBroadcastsPerHour`），则拒绝广播。
3. 成功分发后，将当前时间戳加入队列。

速率限制仅适用于发起 SOS 广播，不适用于中继。

### 8.5 SOS-PanikAPI 桥接

通过网格收到的 SOS 广播可以转发到 PanikAPI 进行传统紧急响应（向联系人发送短信、邮件告警）。反之，PanikAPI 紧急会话可以广播到网格以提高社区意识。通过标记来源（`direct` 与 `mesh_forward`）以及网格广播上的 `internet_forwarded` 标志实现循环防止。

---

## 9. DTN 存储转发

延迟容忍网络（DTN）子系统使消息能在发送方和接收方之间不存在端到端路径时完成投递。包被存储在中间节点上，随着连通性变化机会性地转发。

### 9.1 包格式

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

### 9.2 包生命周期

1. **创建：** 发送方使用加密有效载荷（通过与接收方的 Signal 会话加密）创建包。`Status = Pending`，`CopyCount = 1`。
2. **立即投递尝试：** 发送方首先尝试直接网格路由（RREQ/RREP）。若路由存在，包立即投递，`Status` 转换为 `Delivered`。
3. **服务器中继尝试：** 若网格路由失败，发送方尝试通过 AetherNetAPI 中继。若服务器能到达接收方（或将消息排队），投递成功。
4. **存储转发：** 若网格和服务器中继均失败，包留在本地存储（`Pending` 状态）等待下次投递扫描。

### 9.3 投递扫描

周期性扫描每 60 秒运行一次（`DtnScanIntervalSeconds`）：

1. 从 SQLite（权威来源）加载所有待处理包。
2. 对每个待处理包：
   a. 尝试网格路由到接收方。
   b. 尝试服务器中继。
   c. 若两者均失败且 `CopyCount < MaxCopies`，尝试流行病复制（第 9.4 节）。
3. 删除过期包（`ExpiresAt <= now`）。

### 9.4 流行病路由

当直接投递和服务器中继均失败时，使用流行病路由将包复制到附近对等体：

1. `EpidemicRoutingService` 从当前对等体列表中选择复制目标。
2. 目标选择考虑：
   - **Geohash 邻近度：** 优先选择 geohash 更接近接收方最后已知 geohash 的对等体。
   - **中继历史：** 优先选择可靠性分数更高的对等体。
   - **副本预算：** 当 `CopyCount >= MaxCopies`（默认：3）时停止复制。
3. 每次复制向选定对等体发送 `DtnBundle` 数据包。
4. 收到后，对等体的 DTN 服务调用 `AcceptCustodyAsync`。

### 9.5 托管转移

当节点收到针对其他节点的 DTN 包时：

1. **容量检查：** 节点检查当前包数量是否超过 `DtnMaxBundlesPerNode`（50）。若已满，则拒绝托管。
2. **接受：** 包状态设为 `InCustody`，跳数递增，包持久化到 SQLite。
3. **托管记录：** 创建记录转移的 `CustodyRecord`（来自、去往、时间戳）。
4. **副本数递增：** 持久化存储中包的 `CopyCount` 递增。
5. **确认：** 向转移节点发送 `DtnCustodyAck` 数据包，`Accepted = true`。
6. 接受节点负责在后续扫描中尝试投递。

### 9.6 投递回执

当预定接收方收到 DTN 包时：

1. 包状态更新为 `Delivered`。
2. 通过网格路由（带服务器中继回退）向原始发送方发送 `DtnDeliveryReceipt`：
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. 收到回执后，发送方从其存储中删除包并触发 `BundleDelivered` 事件。
4. 回执也同步到 AetherNetAPI 用于分析。

### 9.7 包过期

- 默认包 TTL 为 72 小时（`DtnBundleTtlHours`）。
- 过期包在周期性投递扫描期间清理。
- `Expired` 或 `Delivered` 状态的包从内存缓存和 SQLite 中删除。

### 9.8 容量限制

| 参数 | 默认值 | 描述 |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours` | 72 | 最大包生存时间 |
| `DtnMaxCopies` | 3 | 网络中每个包的最大副本数 |
| `DtnMaxBundlesPerNode` | 50 | 单个节点携带的最大包数 |
| `DtnScanIntervalSeconds` | 60 | 投递扫描频率 |

---

## 10. 视频流

> **截至 2026-05-05 的状态——设计 + C# 脚手架，无正式上线的编解码器流水线。** 数据包类型 `StreamAnnounce`（11）、`StreamSegment`（12）、`StreamSubscribe`（13）、`StreamUnsubscribe`（14）、`VideoCall`（27）、`VideoSignaling`（28）、`VideoFrame`（31）、`ScreenShare`（32）均已完成线路定义，并通过跨语言夹具语料库的往返测试。C# `AetherNet.Streaming` 模块提供接口、模型和骨架服务（`StreamingService`、`VideoCallService`、`WatchTogetherService`），这些服务连接路由/DI 接缝和单播片段扇出——但没有实际的视频编解码器绑定。其他 7 种语言仅有线路类型。`docs/adaptive-secure-streaming-spec.md` 的前瞻设计文档是目标架构。将以下文字视为这些服务**将**实现内容的规范；生产就绪差距请参见 `OPEN_ISSUES.md`。

Aether 支持三种视频模式：点对点视频通话、群组视频（无限参与者，动态拓扑）和直播。所有视频帧均使用 Signal 协议加密，并用 Ed25519 签名。

### 10.1 传输能力矩阵

在发起视频通话之前，发起方查询传输层以确定与对等体的最佳可用连接。传输决定了可能的视频质量：

| 传输 | 视频支持 | 最大分辨率 | 推荐编解码器 | 最大码率 | 一起观看 |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | 否（仅音频） | — | — | 64 Kbps | 仅同步数据包 |
| NearLink | 轻量 | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | 完整 | 1080p | H.264 | 3000 Kbps | 所有模式 |
| Internet | 完整 | 720p | H.264 | 1500 Kbps | 所有模式 |
| CircleLink | 否（仅音频） | — | — | 64 Kbps | 仅同步数据包 |

若唯一可用传输为 BLE 或 CircleLink，视频通话服务自动降级为语音通话。

### 10.2 视频编解码器

| 枚举值 | 编解码器 | 用途 |
|------------|-------|----------|
| 0 | H.264 | 默认。广泛支持，压缩效果好。 |
| 1 | H.265 | 更好的压缩。用于 NearLink（带宽受限）。 |
| 2 | VP8 | 无版权费替代方案。 |

### 10.3 视频分辨率

| 枚举值 | 分辨率 | 典型码率 |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4 P2P 视频通话流程

1. **能力检查**：发起方调用 `GetVideoCapabilityAsync(peerUhid)` 确定最佳传输、最大分辨率和推荐编解码器。
2. **提议**：发起方发送 `VideoSignaling` 数据包（类型 28），`SignalType = Offer`，包含首选编解码器、最大分辨率和最大码率。
3. **应答/拒绝**：被叫方以 `SignalType = Answer`（将编解码器协商到最低公分母）或 `SignalType = Reject` 响应。
4. **活跃通话**：双方交换包含 H.264/H.265/VP8 NAL 单元的 `VideoCall` 数据包（类型 27）。每帧包含用于抖动缓冲排序的序列号和关键帧标志。
5. **屏幕共享**：任一方可切换屏幕共享。带 `SignalType = ScreenShareStart/Stop` 的 `VideoSignaling` 通知对等体。屏幕共享帧使用 `PacketType.ScreenShare`（类型 32），但采用相同的处理流水线。
6. **结束通话**：任一方发送带 `SignalType = Bye` 的 `VideoSignaling`。

所有信令和帧有效载荷均使用 Signal 协议（X3DH 会话）加密。加密有效载荷在 `MeshPacket.Payload` 字段中序列化为 JSON 编码的 `EncryptedPayload`。

### 10.5 视频通话状态机

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

状态：`Initiating(0)`、`Ringing(1)`、`Active(2)`、`OnHold(3)`、`Ended(4)`、`Failed(5)`、`Rejected(6)`。

### 10.6 群组视频

群组视频会话支持无限参与者。拓扑根据参与者数量动态选择：

- **FullMesh**（2-3 参与者）：每个参与者向所有其他参与者发送一路流。简单，低延迟。
- **SFU**（4+ 参与者，阈值：`SfuThresholdParticipants = 4`）：选举一个节点作为 SFU 中继。每个参与者向中继发送一路流，由中继分发给所有其他人。中继节点通过激励层获得打赏。

拓扑自动切换：当第 4 位参与者加入时，会话从 FullMesh 过渡到 SFU。当参与者离开且数量降至 4 以下时，过渡回来。

群组视频帧使用 `PacketType.VideoFrame`（类型 31）。在 SFU 模式下，帧发送到中继节点的 UHID，由其重新广播。

### 10.7 抖动缓冲

视频抖动缓冲独立于语音抖动缓冲（处理 20ms Opus 帧）运行：

- **范围**：最小 60ms，最大 500ms。
- **自适应深度**：通过指数移动平均（EMA）追踪帧间抖动。缓冲深度 = 2× 抖动估计值，夹缩到 [60, 500] ms。
- **关键帧感知丢弃**：当缓冲溢出时，非关键帧（P/B 帧）优先被丢弃。I 帧（关键帧）永不丢弃——它们是解码器恢复所必需的。
- **间隙处理**：检测到序列间隙时，缓冲跳到下一个可用关键帧而非无限等待。

### 10.8 视频信令类型

| 枚举值 | 类型 | 描述 |
|------------|------|-------------|
| 0 | Offer | 带编解码器/分辨率偏好的视频通话发起 |
| 1 | Answer | 带协商参数的通话接受 |
| 2 | Reject | 通话拒绝 |
| 3 | Bye | 通话终止 |
| 4 | Upgrade | 请求更高质量（如传输改善） |
| 5 | Downgrade | 请求更低质量（如带宽下降） |
| 6 | ScreenShareStart | 对等体开始共享屏幕 |
| 7 | ScreenShareStop | 对等体停止共享屏幕 |

### 10.9 加密模型

| 模式 | 加密 | 密钥分发 |
|------|-----------|-----------------|
| P2P 视频通话 | 每帧 Signal 协议 | X3DH 密钥协商 |
| 群组视频 | 群组频道密钥（AES-GCM） | 会话创建时通过 Signal 协议分发 |
| 屏幕共享 | 与父通话模式相同 | 继承自视频通话会话 |

---

## 11. 一起观看

> **截至 2026-05-05 的状态——设计 + C# 脚手架，成熟度与 §10 相同。** 数据包类型 `WatchSync`（29）、`WatchReaction`（30）、`WatchChunkRequest`（33）、`TorrentMetadata`（34）均已完成线路定义和夹具测试。`AetherNet.Streaming.WatchTogetherService` 提供协调骨架（会话状态、通过 `IMeshSender` 传播同步命令、RTT 补偿辅助工具）；BitTorrent 摄取、ChipIn SDPKT 结算和从对等体获取块在任何语言中均未实现。将以下文字视为目标协议；`docs/adaptive-secure-streaming-spec.md` 的前瞻设计文档以更多细节涵盖相同内容。

一起观看（Watch Together）使一组网格对等体能够同步媒体播放。主持人对播放拥有独占控制权（播放、暂停、跳转、速度）。同步命令包含用于 RTT 补偿的挂钟时间戳。

### 11.1 观看模式

| 枚举值 | 模式 | 数据流 | 传输要求 |
|------------|------|-----------|----------------------|
| 0 | SharedFile | 仅同步数据包（每个 < 100 字节） | 任意（通过 BLE 可用） |
| 1 | StreamFromHost | P2P 块传输（复用 P2pContentService） | WiFi Direct 或 Internet |
| 2 | BitTorrent | 网格 + 通过网关节点的外部群组 | WiFi Direct 或 Internet |

### 11.2 SharedFile 模式

双方持有相同文件（通过 SHA-256 内容哈希匹配）。仅交换 `WatchSync` 数据包。这是带宽效率最高的模式，通过 BLE 即可运行。

1. 主持人使用 `contentHash`（文件的 SHA-256）创建观看会话。
2. 参与者加入，当播放器加载完成后报告 `IsReady = true`。
3. 所有参与者报告就绪后会话开始。
4. 主持人以 `WatchSync` 数据包（类型 29）发送播放/暂停/跳转/速度命令。
5. 接收方应用 RTT 补偿：`adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`。

### 11.3 StreamFromHost 模式

仅主持人拥有文件。主持人生成 `ContentManifest`（复用 P2P 内容系统），参与者通过网格下载块。

- 块选择使用 `SequentialFromPosition` 策略（而非 `RarestFirst`）：优先获取当前播放位置之后的块，然后回填以供分享。
- 缓冲目标：超前 30 秒（`WatchTogetherBufferAheadSeconds`）。
- 自动暂停：若**任一**参与者的缓冲低于 10 秒（`WatchTogetherMinBufferSeconds`），会话使用 `BufferUnderrun` 同步命令自动暂停所有参与者。当所有参与者缓冲充足（`BufferReady`）时恢复播放。
- 随着观看者下载块，他们成为其他观看者的分享者（类 BitTorrent 群组内网格）。

### 11.4 BitTorrent 模式

参与者在群组聊天中共享 `.torrent` 文件或磁力链接。`TorrentMetadata` 数据包（类型 34）将种子信息分发给所有会话参与者。

**网格到群组桥接：**
- 网关节点（有互联网的节点）从外部 BitTorrent 群组下载片段。
- 网关节点重新加密下载的片段以进行网格分发，并向网格对等体分享。
- 没有互联网的网格对等体从网关节点和彼此处接收片段。
- P2P 内容引擎在 BitTorrent 的片段模型和 Aether 的块模型之间进行转换。

缓冲足够内容后，使用与 SharedFile 模式相同的同步协议开始一起观看播放。

### 11.5 观看会话状态机

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

状态：`WaitingForReady(0)`、`Buffering(1)`、`Playing(2)`、`Paused(3)`、`Ended(4)`。

### 11.6 同步命令类型

| 枚举值 | 类型 | 描述 |
|------------|------|-------------|
| 0 | Play | 从指定位置恢复播放 |
| 1 | Pause | 在指定位置暂停 |
| 2 | Seek | 跳转到指定位置 |
| 3 | Speed | 更改播放速度 |
| 4 | BufferUnderrun | 自动暂停——参与者缓冲严重不足 |
| 5 | BufferReady | 恢复——所有参与者缓冲充足 |

### 11.7 RTT 补偿

同步命令包含 `WallClockMs` 字段（Unix 纪元毫秒）。接收方处理同步命令时：

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. 对于 Play 和 BufferReady 命令：`adjustedPosition = commandPosition + networkDelay`
4. 对于 Pause 和 Seek 命令：精确应用位置（无需调整，因为播放正在停止/跳转）。

这确保所有参与者在半个网络 RTT 内同步。

### 11.8 反应

参与者可以在播放过程中对内容做出反应：

- **表情反应**：带 `Type = Emoji` 的 `WatchReaction` 数据包（类型 30），携带表情字符串和反应时的媒体位置。
- **语音评论**：带 `Type = VoiceComment` 的 `WatchReaction` 数据包，携带 Opus 编码的音频数据（最长 10 秒）。语音数据包含在反应的 `VoiceData` 字段中。

反应广播给所有会话参与者。它们带有媒体位置时间戳，支持播放同步显示。

### 11.9 ChipIn — 群组内容获取

ChipIn 使群组成员能够集资（以 ZAR 计价，通过 LedgerAPI 经 SDPKT 钱包结算）以共同获取内容用于群组观看。

**状态机：**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

状态：`Collecting(0)`、`Funded(1)`、`Purchasing(2)`、`Acquired(3)`、`Failed(4)`、`Refunded(5)`。

**流程：**
1. 发起方创建带目标金额和内容描述的 `ChipInPool`。
2. 参与者通过 SDPKT 钱包交易贡献金额。
3. 当 `CollectedAmount >= TargetAmount` 时，状态转换为 `Funded`。
4. 系统获取内容（例如发起 BitTorrent 下载）。
5. 内容可用后，状态转换为 `Acquired`，可以开始一起观看。

每笔贡献记录 SDPKT 交易 ID 以供审计追踪。

### 11.10 加密模型

| 模式 | 加密 | 密钥分发 |
|------|-----------|-----------------|
| 观看同步命令 | 频道/对话密钥 | 现有 Signal 协议会话 |
| 内容块（StreamFromHost） | 每个清单的内容密钥 | 通过 Signal 协议分发 |
| BitTorrent 片段 | 摄取时重新加密 | 网关从群组下载明文，为网格加密 |
| 观看反应 | 会话密钥 | 从对话密钥派生 |

### 11.11 功能开关

所有视频和一起观看功能均由功能开关控制（默认全部禁用）：

| 开关 | 父级 | 描述 |
|------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | P2P 和群组视频通话 |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | 多方视频会话 |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | 视频通话中的屏幕共享 |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | 同步媒体播放 |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | 表情和语音反应 |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | BitTorrent 文件接受用于网格分发 |

功能开关具有父级依赖：子级开关只有在其父级也启用时才能启用。这允许渐进式部署。

---

## 12. 安全与隐私层

> 在 2.3.0 中新增。参考实现：`src/AetherNet.Security/Backup/`（恢复短语）、`src/AetherNet.Security/Privacy/`（BLE 追踪保护、紧急擦除）以及 `src/AetherNet.Security/Sync/`（多设备同步）。跨语言字节向量：`fixtures/bip39/`、`fixtures/bleprivacy/`、`fixtures/panicwipe/`、`fixtures/sync/`。

本层为附加层，独立于 §2 中的数据包套件。仅 **多设备同步**（§12.1–12.2）和 **BLE 追踪保护地址方案**（§12.3）具有字节 / 空中格式；**恢复短语备份**（§12.4）与 **紧急擦除**（§12.5）仅为本地功能，此处为完整性而规定。所有这些在全部八种语言中均以字节相同的方式实现，唯一的例外是 §12.1 中提到的 Ed25519 签名。

### 12.1 DeviceLink（设备配对）

`DeviceLink` 是一个由 Ed25519 签名的断言，声明某设备的公钥属于某个身份，用于配对用户自己的设备以进行多设备同步。**签名主体**为：

| 偏移 | 字段 | 类型 | 大小 | 说明 |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01`；读取时拒绝任何其他值 |
| 1 | device_id_len | uint16, little-endian | 2 | `device_id` 的 UTF-8 字节长度 |
| 3 | device_id | UTF-8 字节 | N | 关联设备的标识符 |
| 3+N | device_public_key | bytes | 32 | 关联设备的 Ed25519 公钥 |
| 35+N | issued_at_ms | int64, little-endian | 8 | Unix 纪元毫秒 |

序列化的 `DeviceLink` 为签名主体后跟随一个覆盖该主体的 **64 字节 Ed25519 签名**，使用*身份*私钥计算。验证时重新计算主体并根据身份公钥核对签名。

> **签名字节一致性例外。** 签名主体与验证结果在全部八种语言中一致，且 64 个签名**字节**在其中七种语言中字节相同。Apple 的 CryptoKit 会随机化 Ed25519 签名（RFC 8032 §8 的加盐签名），因此 Swift 签名在每次调用时都不同，但仍然有效且可交叉验证。互操作必须依赖*验证*，绝不可依赖比较签名字节。

### 12.2 SyncRecord（最后写入者获胜的同步信封）

`SyncRecord` 是对用户自己的多设备状态的一次复制变更，按最后写入者获胜进行协调。记录在现有的 DTN/网格路径内以端到端加密方式传输（`encrypted_payload` 为不透明密文）——它们**不是**一种新的 `MeshPacket` 类型。

| 偏移 | 字段 | 类型 | 大小 | 说明 |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01` |
| 1 | record_id | UUID, RFC 4122 big-endian | 16 | 与 §2.1 相同的 big-endian 约定 |
| 17 | op | uint8 | 1 | `0`=Upsert，`1`=Delete，`2`=Read；拒绝大于 2 的值 |
| 18 | logical_clock | int64, little-endian | 8 | 每设备单调计数器 |
| 26 | created_at_ms | int64, little-endian | 8 | Unix 纪元毫秒 |
| 34 | device_id_len | uint16, little-endian | 2 | UTF-8 字节长度 |
| 36 | device_id | UTF-8 字节 | N | 发起设备 |
| 36+N | item_id_len | uint16, little-endian | 2 | UTF-8 字节长度 |
| 38+N | item_id | UTF-8 字节 | M | 正在同步的逻辑键 |
| 38+N+M | payload_len | int32, little-endian | 4 | 密文长度；拒绝负值 |
| 42+N+M | encrypted_payload | bytes | payload_len | 不透明的端到端密文 |

**协调（最后写入者获胜）。** 对于同一 `item_id` 的两条记录，按顺序比较直至其中一项不同来选出胜者：`created_at_ms`，然后 `logical_clock`，然后 `device_id`（序数字节比较），然后 `record_id`（big-endian 字节比较）。该顺序是全序且确定性的，因此无论到达顺序如何，每台设备都会收敛到同一胜者。

### 12.3 BLE 追踪保护

两种推导使设备能够在不被被动扫描器追踪的情况下进行广播。二者均为固定到 `fixtures/bleprivacy/` 的纯函数；在空中发送它们是宿主 BLE 栈的职责。

- **轮换服务 UUID。** `window = floor(unix_time_seconds / 900)`（15 分钟纪元）。广播的 128 位服务 UUID 为 `HMAC-SHA256(ble_rotation_key, LE_int64(window))` 的前 16 字节。记录该 UUID 的扫描器在没有轮换密钥的情况下无法关联两个窗口。
- **可解析私有地址（RPA）。** 按照 Bluetooth 的 `ah` 函数：`hash = ah(IRK, prand)`，其中 `ah` 是对 24 位 `prand`（填充至 128 位）进行的 AES-128，并取低 24 位。48 位地址为 `hash(24) || prand(24)`，将 `prand` 的最高两位设为 `0b01` 以标记其为可解析。持有 IRK 的对等体通过重新计算 `ah` 并比较哈希来解析该地址。

### 12.4 恢复短语备份（本地）

身份是一个 Ed25519 密钥对，其 32 字节私有种子（256 位）在官方英文词表上编码为 **24 词 BIP-39** 助记词，带有标准 SHA-256 校验和（键入错误的单词会使校验和失败并被拒绝，而不是无声地产生不同的身份）。这是标准 BIP-39——已对照官方 Trezor 测试向量验证，并在全部八种语言中逐字节复现——因此该短语可在任何设备上恢复身份，无需服务器或托管方。不存在线路格式；该短语绝不接触网络。

### 12.5 紧急擦除（本地）

在胁迫之下，**胁迫 PIN**——以恒定时间对照存储的 `SHA-256(pin)` 进行比较——会触发对所有身份密钥材料的安全擦除：每个缓冲区先以随机字节覆盖再清零，遍历一份固定的身份密钥名称清单（身份密钥对、设备盐、DRK，以及来自 §12.3 的 BLE 轮换密钥 / IRK）。不存在线路格式；该操作完全在设备本地进行。

---

## 附录 A：常量参考

所有协议常量均在 `ProtocolConstants` 中定义，此处复制以供参考：

### 路由
| 常量 | 值 |
|-----------------------|--------|
| DefaultTtl | 7 |
| SosTtl | 15 |
| RouteTimeoutMs | 5000 |
| RouteExpirySeconds | 300 |

### BLE 发现
| 常量 | 值 |
|---------------------------|--------|
| BleDiscoveryIntervalMs | 10000 |
| BleScanOnMs | 2000 |
| BleScanOffMs | 8000 |
| BleAdvertiseIntervalMs | 1000 |
| BleUuidRotationSeconds | 900 |
| BleScanJitterMaxMs | 2000 |
| AetherNetBleServiceUuid | A3E7-1001-0001-0000-000000000000 |

### 安全
| 常量 | 值 |
|---------------------------|--------|
| PacketNonceSize | 8 |
| MaxPacketAgeSeconds | 300 |
| ProtocolVersionUnsigned | 1 |
| ProtocolVersionSigned | 2 |
| MaxSkippedKeys | 1000 |
| AES-GCM Nonce Size | 12 |
| AES-GCM Tag Size | 16 |

### SOS
| 常量 | 值 |
|----------------------------|-------|
| SosTtl | 15 |
| SosPriority | 255 |
| MaxSosBroadcastsPerHour | 3 |

### DTN
| 常量 | 值 |
|---------------------------|--------|
| DtnBundleTtlHours | 72 |
| DtnMaxCopies | 3 |
| DtnMaxBundlesPerNode | 50 |
| DtnScanIntervalSeconds | 60 |

### 传输
| 常量 | 值 |
|---------------------------|---------|
| BleMaxPayloadBytes | 1024 |
| DefaultChunkSizeBytes | 8192 |
| MaxChunkSizeBytes | 1048576 |
| WifiDirectTimeoutMs | 10000 |
| MaxWifiDirectPeers | 8 |

### 心跳
| 常量 | 值 |
|-------------------------------|-------|
| HeartbeatIntervalSeconds | 300 |
| NodeOfflineThresholdSeconds | 900 |

### 存在
| 常量 | 值 |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs | 15000 |
| PresenceTimeoutSeconds | 60 |
| EphemeralIdRotationMinutes | 15 |
| ProximityEventDebounceSeconds | 30 |

### 语音
| 常量 | 值 |
|---------------------------|-------|
| VoiceFrameDurationMs | 20 |
| PttMaxDurationSeconds | 60 |
| JitterBufferMinMs | 20 |
| JitterBufferMaxMs | 200 |
| OpusDefaultBitrateKbps | 64 |
| MaxGroupVoiceMembers | 8 |

### 流媒体
| 常量 | 值 |
|-----------------------------|-------|
| DefaultSegmentDurationMs | 3000 |
| MaxStreamTreeFanout | 4 |
| MaxStreamRelayHops | 3 |
| StreamSegmentBufferSize | 10 |
| BleAudioBitrateKbps | 64 |
| WifiDirectVideoBitrateKbps | 500 |

### 视频
| 常量 | 值 |
|--------------------------------|-------|
| VideoFrameDurationMs | 33 |
| VideoJitterBufferMinMs | 60 |
| VideoJitterBufferMaxMs | 500 |
| WatchTogetherBufferAheadSeconds | 30 |
| WatchTogetherMinBufferSeconds | 10 |
| NearLink360pBitrateKbps | 800 |
| Internet1080pBitrateKbps | 3000 |
| SfuThresholdParticipants | 4 |
| ScreenShareFrameDurationMs | 100 |

---

## 附录 B：术语表

| 术语 | 定义 |
|------|------------|
| **UHID** | 通用硬件标识符。标识网格节点的唯一字符串，由设备身份和密码密钥派生。 |
| **RREQ** | 路由请求。用于发现到目标节点路径的广播数据包。 |
| **RREP** | 路由回复。沿 RREQ 建立的反向路由回传的单播数据包。 |
| **IRK** | 身份解析密钥。128 位密钥，用于生成和解析 BLE 可解析私有地址。 |
| **RPA** | 可解析私有地址。周期性轮换的 6 字节 BLE 地址，但持有发送方 IRK 的对等体可解析它。 |
| **X3DH** | 扩展三次 Diffie-Hellman。一种密钥协商协议，支持异步会话建立。 |
| **DTN** | 延迟容忍网络。一种用于间歇连接环境的存储转发范式。 |
| **网关** | 具有互联网连接的网格节点，在网格流量和基于 IP 的服务之间架桥。 |
| **HKDF** | 基于 HMAC 的密钥派生函数。用于从单个共享密钥派生多个密钥。 |
| **预密钥包** | 一组已发布的密钥，允许发送方在接收方不在线的情况下建立加密会话。 |
| **SFU** | 选择性转发单元。从每个发送方接收一路视频流并将其分发给所有其他参与者的中继节点，减少每个节点的上行带宽。 |
| **ChipIn** | 群组集资机制，参与者共同使用 SDPKT 资金通过 LedgerAPI 集体获取内容用于群组观看。 |
| **NAL** | 网络抽象层。H.264 和 H.265 编解码器用于封装视频帧的格式。 |

---

## 附录 C：参考文献

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
