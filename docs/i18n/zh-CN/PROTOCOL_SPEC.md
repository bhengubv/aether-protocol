# Aether 网状网络协议规范

**版本：** 2.0
**状态：** 已与 HEAD 对齐（2026-05-05）
**日期：** 2026-03-15（初稿）；2026-05-05（§2、§4、§10、§11 已对齐，§3/§9 已验证）
**作者：** The Other Bhengu (Pty) Ltd t/a The Geek 及 Bhengu B.V.

> **读者须知。** 本文档早期草稿早于 8 种语言线格式对齐以及全系列向 X25519 +
> Signal Double Ratchet 迁移之前。截至 2026-05-05，§2（数据包格式）、§3
> （路由）、§4（密钥交换）、§9（DTN）描述的是已实现的协议；§10（视频流）和
> §11（同步观看）描述的是目标协议——它们已完成线格式定义和夹具测试，但
> 编解码器 / BitTorrent / ChipIn 管道尚未绑定到脚手架。在本文档与实现存在
> 分歧的所有情况下，C# 参考实现具有权威性。
>
> - 规范线字节：`fixtures/expected/*.bin`（10 个命名用例）
> - 参考序列化器：`src/Aether.Core/Protocol/PacketSerializer.cs`
> - 参考 Signal 栈：`src/Aether.Security/Services/SignalProtocolService.cs`
> - 参考路由：`src/Aether.Core/Routing/RoutingService.cs`
> - 参考 DTN：`src/Aether.Core/Dtn/DtnService.cs`
> - 跨语言线互操作证明：`fixtures/README.md`
> - 跨语言 Signal 互操作证明：`fixtures/signal/README.md`

---

## 目录

1. [摘要](#1-摘要)
2. [数据包格式](#2-数据包格式)
3. [路由算法](#3-路由算法)
4. [密钥交换](#4-密钥交换)
5. [传输层要求](#5-传输层要求)
6. [发现协议](#6-发现协议)
7. [安全模型](#7-安全模型)
8. [SOS 广播](#8-sos-广播)
9. [DTN 存储转发](#9-dtn-存储转发)
10. [视频流](#10-视频流)
11. [同步观看](#11-同步观看)

---

## 1. 摘要

Aether 是一种去中心化网状网络协议，专为网络连接断断续续或完全缺失的环境而设计。它提供以下功能：通过异构短距离传输（蓝牙低功耗、Wi-Fi Direct、NearLink）实现多跳数据包路由；使用基于 X3DH 的密钥协商与对称棘轮机制的端到端加密；延迟容忍的存储转发传输；以及紧急 SOS 泛洪机制。该协议与传输层无关：任何能够在对等节点之间发送和接收字节数组的物理层都是有效的 Aether 传输。节点通过通用硬件标识符（UHID）进行标识，并通过 Ed25519 身份密钥进行认证。Aether 旨在作为通用网络层——生态系统中的每个应用程序均注册 Aether 服务，没有互联网连接的节点通过将网格流量桥接到互联网的网关对等节点来访问更广泛的网络。

---

## 2. 数据包格式

> 已于 2026-05-05 针对 `src/Aether.Core/Protocol/PacketSerializer.cs`
> 及 `fixtures/expected/` 下的 10 个夹具用例进行对齐。

### 2.1. MeshPacket 线格式布局

每条 Aether 消息均封装在 `MeshPacket` 中。各字段在线上按**完全**如下所示的顺序出现：

| Off | Field            | Type                            | Size       | Notes |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = 未签名（旧版），`2` = 已签名（当前版） |
| 1   | Type             | uint8                           | 1          | 数据包类型枚举（见 §2.4） |
| 2   | Id               | UUID, RFC 4122 大端序           | 16         | 用于去重的数据包标识符。**大端序**字节序，非 .NET 的混合端序 Guid 默认值。 |
| 18  | Priority         | uint8                           | 1          | 优先级（0 = 普通，255 = SOS）。**线字段为 1 字节；超过 255 的值必须截断。** |
| 19  | Ttl              | int32, 小端序                   | 4          | 生存时间，每跳递减。**4 字节 int32**，非 1 字节 uint8——最大有效值约为 2³¹-1。 |
| 23  | TimestampMs      | int64, 小端序                   | 8          | Unix 纪元毫秒（UTC）。 |
| 31  | SourceUhid Len   | uint16, 小端序                  | 2          | `SourceUhid` 的 UTF-8 字节长度。最大 65535。 |
| 33  | SourceUhid       | UTF-8 字节                      | N          | 发送方的 UHID；允许为空，但不常见。 |
| 33+N | DestinationUhid Len | uint16, 小端序             | 2          | `DestinationUhid` 的 UTF-8 字节长度。 |
| ... | DestinationUhid  | UTF-8 字节                      | M          | 接收方的 UHID；广播时为空字符串。 |
| ... | PacketNonce Len  | uint16, 小端序                  | 2          | `PacketNonce` 的字节长度。标准值：8。 |
| ... | PacketNonce      | bytes                           | P          | 用于防重放的密码学随机数。 |
| ... | Payload Len      | int32, 小端序                   | 4          | `Payload` 的字节长度。负值为错误。 |
| ... | Payload          | bytes                           | Q          | 应用数据。解释取决于 `Type`。 |
| ... | Signature Len    | uint16, 小端序                  | 2          | `Signature` 的字节长度。0（未签名）或 64（Ed25519）。 |
| ... | Signature        | bytes                           | R          | 对可签名数据的 Ed25519 签名（见 §2.3）。 |

**长度前缀宽度**因字段而异——`SourceUhid`、`DestinationUhid`、
`PacketNonce` 和 `Signature` 使用 **2 字节（uint16）** 长度前缀；
`Payload` 使用 **4 字节（int32）** 长度前缀，因为有效载荷可能超过 64 KiB。

### 2.2. 最小数据包大小

当每个可变长度字段均为空（零长度 UHID、零长度随机数、零长度有效载荷、零长度签名）时，线大小为：

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

本规范早期草稿中的 50 字节 / 52 字节数字是不正确的。

### 2.3. 线格式图

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

有关具体示例，请参阅 `fixtures/expected/basic_data.bin`（83 字节，
`fixtures/inputs.json` 中的规范输入）。各实现均针对完整夹具语料库进行验证——
任何偏差都将导致跨语言夹具验证器测试失败。

### 2.4. 可签名数据构造

签名（线上的 `Signature` 字段）是对一个单独的规范字节序列计算的——**不是**对线字节本身计算的。这样做的目的是允许线布局演进而不破坏签名，同时让中间节点无需查看明文有效载荷即可验证完整性（仅对其 SHA-256 哈希签名）。

可签名字节序列是如下内容的拼接：

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

> 请注意与 §2.1 线布局的刻意差异：可签名数据对 `Type`、`Length`、`Ttl` 和 `Priority` 使用 **4 字节 int32**，而线格式分别使用 1 字节 / 2 字节 / 4 字节 / 1 字节。这是有意为之——可签名格式跨语言可移植且使用固定宽度字段；线格式为 BLE PDU 节省空间而紧凑设计。实现必须在编码为可签名字节之前将 `Priority` 截断到 `[0,255]`，否则接收方（从线字节读取 0..255）会推导出不同的可签名缓冲区，导致验证失败。

参考实现位于 `src/Aether.Security/Services/
PacketSigningService.cs::BuildSignableData`，移植时必读。

### 2.5. 数据包类型

| Value | Name              | Direction     | Description |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV 路由请求 |
| 2     | RouteReply        | Unicast       | AODV 路由回复（必须由目标节点签名） |
| 3     | Data              | Unicast       | 应用数据 |
| 4     | Ack               | Unicast       | 传递确认 |
| 5     | SosBroadcast      | Flood         | 紧急广播（见第 8 节） |
| 6     | SosAck            | Unicast       | SOS 确认 |
| 7     | ChannelMessage    | Multicast     | 群组频道消息 |
| 8     | ChunkRequest      | Unicast       | P2P 内容块请求 |
| 9     | ChunkData         | Unicast       | P2P 内容块响应 |
| 10    | Heartbeat         | Broadcast     | 周期性存活信号 |
| 11    | StreamAnnounce    | Broadcast     | 直播流通告 |
| 12    | StreamSegment     | Unicast/Tree  | 直播流媒体片段 |
| 13    | StreamSubscribe   | Unicast       | 申请加入流中继树 |
| 14    | StreamUnsubscribe | Unicast       | 离开流中继树 |
| 15    | VoicePtt          | Unicast       | 即按即说语音帧 |
| 16    | VoiceCall         | Unicast       | 实时语音通话帧 |
| 17    | VoiceSignaling    | Unicast       | 语音通话建立/拆除信令 |
| 18    | DtnBundle         | Unicast       | DTN 存储转发包（见第 9 节） |
| 19    | DtnCustodyAck     | Unicast       | DTN 托管转移确认 |
| 20    | DtnDeliveryReceipt| Unicast       | DTN 端到端传递确认 |
| 21    | PresenceBeacon    | Broadcast     | 在线状态与可用性通告 |
| 22    | PresenceQuery     | Unicast       | 在线状态查询请求 |
| 23    | ProfileSync       | Unicast       | 个人资料元数据同步 |
| 24    | TipPacket         | Unicast       | 节点小费（通过 LedgerAPI 结算） |
| 25    | PreKeyRequest     | Unicast       | 请求对等节点的预密钥包 |
| 26    | PreKeyResponse    | Unicast       | 预密钥包传递 |
| 27    | VideoCall         | Unicast       | 加密视频帧（H.264/H.265/VP8 NAL 单元） |
| 28    | VideoSignaling    | Unicast       | 视频通话建立信令：offer、answer、reject、bye、编解码器协商 |
| 29    | WatchSync         | Unicast       | 同步播放命令：播放、暂停、跳转、速度 |
| 30    | WatchReaction     | Multicast     | 同步观看期间的带时间戳表情或语音反应 |
| 31    | VideoFrame        | Unicast/SFU   | 群组视频帧（SFU 中继节点分发给参与者） |
| 32    | ScreenShare       | Unicast       | 屏幕共享帧（与视频使用相同管道，单独标记） |
| 33    | WatchChunkRequest | Unicast       | 偏向播放位置的优先块请求 |
| 34    | TorrentMetadata   | Multicast     | BitTorrent .torrent 文件或磁力链接元数据交换 |

### 2.6. 节点能力

节点以位域形式通告其能力：

| Bit | Value | Capability  | Description |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | 蓝牙低功耗传输可用 |
| 1   | 2     | WifiDirect  | Wi-Fi Direct 传输可用 |
| 2   | 4     | Gateway     | 互联网网关（将网格流量桥接到 IP 网络） |
| 3   | 8     | Relay       | 愿意为其他节点中继数据包 |
| 4   | 16    | Sos         | 具备 SOS 广播能力 |
| 5   | 32    | Streaming   | 具备直播中继能力 |
| 6   | 64    | Voice       | 具备语音通话中继能力 |
| 7   | 128   | DtnCarrier  | DTN 存储转发载体 |
| 8   | 256   | NearLink    | NearLink 传输可用 |
| 9   | 512   | Video       | 具备视频编解码能力 |

---

## 3. 路由算法

Aether 使用基于按需距离矢量（AODV）路由的反应式路由协议，并在此基础上扩展了密码学路由认证和 QoS 加权路由选择。

### 3.1. 路由请求（RREQ）

当节点需要向没有已知路由的目标发送数据包时，它将发起路由请求：

1. 发起方创建一个 `MeshPacket`，`Type = RouteRequest`，`SourceUhid` 设为自身，`DestinationUhid` 设为目标，`TTL = 7`（默认值）。
2. 将数据包广播给所有直接连接的对等节点。
3. 收到 RREQ 的每个中间节点：
   a. 通过数据包 `Id` 检查是否已见过该 RREQ。如果已见过，则静默丢弃该数据包（去重）。去重缓存最多保存 `DeduplicationCacheSize` 个条目（默认 10,000），达到上限后将完全清空。
   b. 为 RREQ 发起方安装**反向路由**。反向路由记录接收到该 RREQ 的对等节点的 UHID 作为下一跳。跳数由 `DefaultTtl - packet.Ttl + 1` 推导。
   c. 如果该节点**是**目标，则生成 RREP（见第 3.2 节）。
   d. 如果该节点已有到目标的有效路由，则**可以**代表目标生成 RREP。
   e. 否则，递减 TTL 并重新广播 RREQ。
4. 发起方等待 RREP，超时为 **5,000 毫秒**（`RouteTimeoutMs`）。如果没有 RREP 到达，路由发现失败。

### 3.2. 路由回复（RREP）

当目标（或拥有有效路由的中间节点）生成路由回复时：

1. 创建一个 `MeshPacket`，`Type = RouteReply`，`SourceUhid` 设为目标节点，`DestinationUhid` 设为 RREQ 发起方。
2. **安全要求：** RREP **必须**由目标节点的 Ed25519 身份密钥签名。签名覆盖标准可签名数据（第 2.3 节）。这可防止恶意中间节点的路由投毒攻击。
3. RREP 通过 RREQ 传播过程中安装的反向路由进行单播回传。
4. 转发 RREP 的每个中间节点：
   a. 根据已声明来源的公钥验证 RREP 签名（如果已知）。如果验证失败，则丢弃 RREP 并记录警告。
   b. 以 RREP 发送方为下一跳，安装指向 RREP 源（目标节点）的**正向路由**。
   c. 递减 TTL 并向 RREQ 发起方转发。
5. 当 RREP 到达发起方时，通过 `TaskCompletionSource` 跟踪的待处理路由请求将以已安装的路由完成解析。

### 3.3. 路由维护

- **基于 TTL 的过期：** 每个路由条目携带一个 `ExpiresAt` 时间戳，设置为 `now + 300 秒`（`RouteExpirySeconds`）。路由不会隐式刷新；它们必须在过期后通过新的 RREQ/RREP 周期重新建立。
- **周期性清理：** 协议服务运行周期性心跳（默认每 300 秒一次）。在每个周期内，它从内存中的 `ConcurrentDictionary` 和 SQLite 后备存储中删除过期路由。
- **RREQ 去重清理：** 当已见 RREQ ID 集合超过 `DeduplicationCacheSize`（默认 10,000）个条目时，将被清空。

### 3.4. 路由质量与 QoS

每个 `RouteEntry` 携带范围为 [0, 100] 的 `QualityScore`，新发现的路由初始化为 50。评分考虑以下因素：

- **跳数：** 更少的跳数通常意味着更快的路由。
- **延迟：** 可用时测量的往返时间。
- **对等节点可靠性：** 下一跳对等节点的可靠性评分（见第 3.5 节）。

参与小费激励系统的节点将获得路由质量评分加成。这是一种软优先：非小费用户始终获得服务，但持续给予小费的用户可能会在路由选择上获得边际优势。加成层级如下：

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. 对等节点可靠性评分

每个已知对等节点被分配一个范围为 [0, 100] 的可靠性评分，初始化为 50（`DefaultReliabilityScore`）。评分根据观察到的行为进行调整：

| Event                | Delta |
|----------------------|-------|
| Successful relay     | +2    |
| Failed relay         | -5    |
| SOS relay            | +5    |
| Chunk served         | +1    |
| Chunk serve failure  | -10   |

可靠性评分持久化到 SQLite，并在启动时加载到内存中。评分影响路由选择：经过更可靠对等节点的路由优先选用。

---

## 4. 密钥交换

> 已于 2026-05-05 针对 `src/Aether.Security/Services/SignalProtocolService.cs`
> 的 C# 参考实现及 `fixtures/signal/` 下的跨语言夹具语料库进行对齐。C# 参考
> 实现提供了完整的 X3DH + Double Ratchet（Signal §3 + §5）over X25519。Go、
> Python、TypeScript、Rust、Swift 和 Kotlin 已移植到相同的封装格式，在
> X3DH 和 KDF_RK 夹具层面字节等价。C 仅提供 X25519 + KDF_RK + 对称棘轮
> 原语——对于夹具验证器已足够，尚无完整的会话机制。当本节内容与代码存在
> 分歧时，代码具有权威性；请在 `OPEN_ISSUES.md` 中提交 issue。

Aether 实现了 **X3DH**（扩展三重 Diffie-Hellman，Signal §3）用于异步会话建立，并紧接着使用 **Signal Double Ratchet**（Signal §5）用于持续的前向保密和后妥协安全。所有会话加密均运行在 Curve25519 之上：**X25519**（RFC 7748）用于 ECDH，**Ed25519**（RFC 8032）用于签名。

### 4.1. 身份密钥

每个节点在首次启动时生成**两个**长期密钥对（无 XEdDSA；更简单的双密钥安排是所有实现的实际做法）：

- **Ed25519 密钥对** —— 32 字节种子（私钥），32 字节公钥。
  用于数据包签名（§2.4）、`SignedPreKeySignature`（§4.3）、
  RREP 认证（§3.2）和小费签名。
- **X25519 密钥对** —— 32 字节原始私钥和公钥。用于
  四次 X3DH DH 运算（§4.4）。

参考：`SignalProtocolService.InitializeIdentityKeys`。私钥仅保留在设备上；公钥发布在 `PreKeyBundle` 中。

对于入站数据包的*签名验证*，支持 30 天的 P-256 → Ed25519 迁移窗口——见 §7.5。预密钥包本身在线上仅使用 X25519。

### 4.2. 曲线选择

X3DH 和 Double Ratchet 专用 **X25519**。当前任何实现中，P-256 均**不**用于会话建立。本规范早期草稿描述了 P-256 ECDH；该文本早于 2026-05-05 全系列向 X25519 的迁移，已不再准确。

### 4.3. 预密钥包

预密钥包被发布，使得发起方可以在响应方不在线的情况下建立会话（Signal §3.4）：

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

参考：`Aether.Security.Models.PreKeyBundle`。线形状契约在全部 8 种语言中相同。

**一次性预密钥（OPK）池。** 每个响应方维护一个包含 `OpkPoolSize`（默认 100，与 Signal 公开指南一致）个 X25519 OPK 的池。包生成时从 FIFO 队列弹出下一个未使用的 id，然后将池补充回目标大小。每个 OPK 仅消耗一次：响应方在第一条引用其 id 的 PreKey 消息上删除并清零私钥的一半。竞争同一 OPK id 的并发发起方将在 `_preKeyLock` 保护下看到恰好一个 `EstablishResponderSession` 成功；竞争失败者将引发 `CryptographicException`。

参考：`SignalProtocolService.TopUpOpkPoolNoLock`（第 494–518 行），
`SignalProtocolService.EstablishResponderSession`（第 636–718 行）。池语义由 `tests/Aether.Core.Tests/PreKeyPoolTests.cs` 测试。

**已签名预密钥（SPK）轮换。** SPK 在第一次包调用时延迟生成，并在后续调用中重复使用，这样在 X3DH 运行之前获取包的并发发起方不会使彼此的包失效。周期性的 SPK 轮换（Signal §3.3 建议每周）是显式操作，而非包生成的副作用。

预密钥 id 从 `RandomNumberGenerator.GetInt32(1, int.MaxValue)` 中抽取，并进行显式碰撞重试（最多 64 次，之后引发异常）。

### 4.4. 会话建立（X3DH）

完整的 X3DH（Signal §3.3）在发起方侧运行。在 X25519 上计算四次 DH 运算：

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

其中 `IK_A` / `IK_B` 是 X25519 身份密钥，`EK_A` 是仅为本次会话生成的新鲜 X25519 临时密钥，`SPK_B` 是响应方的已签名预密钥，`OPK_B` 是响应方的一次性预密钥。初始根密钥为：

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

`info` 常量 `aether-x3dh-root-v1` 在所有实现中完全一致，并由 `fixtures/signal/expected/x3dh_basic.json`（字段 `root_key_hex`）固定。

参考：`SignalProtocolService.ProcessPreKeyBundleAsync`（第 554–626 行）。验证路径：
`fixtures/signal/inputs.json` 用例 `x3dh_basic` →
`fixtures/signal/expected/x3dh_basic.json`。

**包验证。** 在任何 DH 运算运行之前，发起方使用 Ed25519 对照 `IdentityKey` 验证 `SignedPreKeySignature`。验证失败时引发 `CryptographicException`，丢弃该包。公钥大小根据 `X25519Service.PublicKeySize`（32）进行验证；格式错误的包将被拒绝。

**会话启动。** 在 `ProcessPreKeyBundleAsync` 结束时，创建一个 `SignalSession`，内容为：

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` —— Signal 规范的 X3DH ↔
  Double Ratchet 集成：发起方的 X3DH 临时密钥成为其第一个 DH 棘轮密钥对（`DHs`）。
- `RemoteEphemeralPub = SPK_B` —— 响应方的已签名预密钥被视为初始对等棘轮密钥（`DHr`）。
- `SendChainKey = null`，`RecvChainKey = null` —— 两个链密钥均在第一次发送/第一次 DH 棘轮接收时延迟派生。
- `PendingPreKeyMessage = true` —— 标记下一次出站 `EncryptAsync` 调用必须发出 PreKey 消息（`MessageType=1`）。

所有 DH 输出和拼接的共享密钥均通过 `CryptographicOperations.ZeroMemory` 在 `finally` 块中清零。

**拒绝不安全发送。** 如果对一个没有会话的对等节点调用 `EncryptAsync`，则该调用将抛出 `InvalidOperationException`。不存在基于 UHID 的回退路径。宿主应排队消息（见 `MessagingService` + `SignalMessageEnvelopeCipher`），并在会话建立完成后重试。

### 4.5. Double Ratchet（Signal §5）

每一方维护一个旋转的 X25519 棘轮密钥对（`DHs`）和对等节点最后所见棘轮公钥的副本（`DHr`）。发送方在每条消息中发布其当前 `DHs` 公钥；每当接收方观察到新的 `DHr` 时，它运行一个 **DH 棘轮步骤**，通过 `KDF_RK(RK, DH(myDHs, newDHr))` 重新生成链密钥——同时重新派生根密钥和新的链密钥。

#### 4.5.1. KDF_RK

`KDF_RK` 是对 64 字节块的 HKDF-SHA256，以 32+32 的方式分割为新的根密钥和新的链密钥：

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

参考：`SignalProtocolService.KdfRk`（第 857–868 行）。由
`fixtures/signal/inputs.json` 用例 `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json` 固定。

#### 4.5.2. 对称棘轮

根据 Signal §5.1，消息密钥和链密钥使用 HMAC-SHA256 并以单字节域分隔符从链密钥派生：

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

参考：`SignalProtocolService.RatchetChainKey`（第 876–881 行）。由
`fixtures/signal/inputs.json` 用例 `ratchet_step_basic` 和
`ratchet_step_three_iterations` 固定。

本规范早期草稿描述了 `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` 以及独立的 `chain_key
通过 HMAC(chain_key, 0x01) 推进`。那是非 Signal 标准且从未实现；已替换为规范的 0x01/0x02 分割方式。

#### 4.5.3. 接收侧的 DH 棘轮步骤

当入站消息的 `SenderEphemeralKeyX25519` 与缓存的 `RemoteEphemeralPub` 不同（恒定时间比较）时触发。

1. 将出站计数器保存为 `PreviousChainCount`（Signal §5: PN），以便对等节点可以计算跨边界的跳过密钥。
2. 将 `SendCounter` 和 `RecvCounter` 重置为 0；安装新的 `RemoteEphemeralPub`。
3. 派生新的接收链：`(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`。
4. 清零旧的 `myDHs` 私钥；生成新的 X25519 密钥对。
5. 派生新的发送链：`(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`。

参考：`SignalProtocolService.DhRatchetReceive`（第 726–772 行）。

#### 4.5.4. 延迟发送链派生

发起方的第一次发送运行**半步**而非完整的 DH 棘轮——X3DH 已放置了 `DHs` 和 `DHr`，因此只需派生发送链：

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

此处 `DHs` *不会*轮换。它仅在真正的接收侧 DH 棘轮步骤上轮换。

参考：`SignalProtocolService.DhRatchetSendOnly`（第 780–796 行）。

#### 4.5.5. 跳过的消息密钥

当消息乱序到达时，每个跳过计数器的消息密钥缓存在 `SkippedMessageKeys` 中，以 `(Hex(remoteEphPub):counter)` 为键。远程公钥绑定至关重要——来自先前链（不同 `DHr`）的乱序消息在 DH 棘轮步骤之后仍可能到达，需要各自独立的每链密钥集。

限制：

- 在单个间隙中跳过超过 `MaxSkippedKeys`（1000）个条目将引发 `CryptographicException` 并强制重新建立会话。
- 跨越 DH 棘轮边界时，接收方首先在*旧*链上跳过至 `PreviousChainCount` 个密钥，然后运行 DH 棘轮步骤，再在新链上派生密钥。

参考：`SignalProtocolService.SkipMessageKeys`（第 804–830 行）和
解密中的跳过循环（第 366–388 行）。

### 4.6. 加密有效载荷格式

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

参考：`Aether.Security.Models.EncryptedPayload`（`SecurityModels.cs` 第 55–66 行）。`InitiatorEphemeralKeyX25519` 字段是 Double Ratchet 前线格式的向后兼容别名，在 PreKey 消息中等于 `SenderEphemeralKeyX25519`；新消费者应忽略它。

AES-GCM 参数：256 位密钥，96 位随机数（`AesNonceSize = 12`），128 位标签（`AesTagSize = 16`），标签拼接到密文之后。消息密钥在 AES-GCM 加解密后立即在 `finally` 块中清零。

### 4.7. 各语言状态

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | primitives only — `aether_x25519_*`, `aether_signal_kdf_rk` | not implemented | — | kdf_rk_basic only |

全部 7 种支持会话的语言（C# + Go + TypeScript + Python + Kotlin + Swift + Rust）均提供具有延迟补充和锁保护消费的 100 密钥 FIFO OPK 池，与 C# 参考契约一致。C 仅提供原语；完整会话机制在 `OPEN_ISSUES.md` 条目 11 中跟踪。

---

## 5. 传输层要求

Aether 与传输层无关。任何满足 `ITransportService` 契约的物理通信信道都可以参与网格。

### 5.1. ITransportService 接口契约

每个传输实现必须公开以下内容：

**属性：**

| Property           | Type   | Description |
|--------------------|--------|-------------|
| `Name`             | string | 人类可读标识符（例如 "BLE"、"Wi-Fi Direct"、"NearLink"） |
| `IsAvailable`      | bool   | 传输在本设备上是否当前可用 |
| `MaxBandwidthBps`  | int64  | 最大吞吐量，单位字节/秒 |
| `MaxRangeMeters`   | int32  | 最大通信距离，单位米 |
| `PowerCostRelative`| int32  | 相对功耗（1 = 低，10 = 高） |
| `MaxConcurrentPeers` | int32 | 最大同时对等连接数 |

**方法：**

| Method         | Signature | Description |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | 向特定对等节点发送字节数组。成功时返回 true。 |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | 向对等节点发送流（用于大文件传输、语音、视频）。 |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | 检查到对等节点的连接是否活跃。 |

**事件：**

| Event          | Signature | Description |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | 当来自对等节点的数据到达时触发。 |

### 5.2. 传输选择算法

`TransportManager` 根据以下条件为每个数据包选择最优传输：

1. **可用性：** 仅考虑 `IsAvailable == true` 的传输。
2. **有效载荷大小：** 如果有效载荷大小不超过 `BleMaxPayloadBytes`（1,024 字节），则优先选择 BLE 以节省功耗。较大的有效载荷优先选择 Wi-Fi Direct。
3. **功耗权重：** 在可用传输中，较低的 `PowerCostRelative` 值对于常规流量更优先。高优先级数据包（SOS、语音）可能覆盖此偏好。
4. **对等连接：** 如果某传输已有到目标对等节点的活跃连接（`IsConnected` 返回 true），则优先选择该传输以避免连接建立开销。
5. **回退：** 如果本地没有传输可以到达目标，则将数据包排队通过 AetherAPI 进行服务器中继。

### 5.3. 参考传输

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | Notes |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | 主要用于发现和小数据包 |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | 大文件传输、流媒体、语音 |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | 华为/海思，高吞吐量 |

**BLE 有效载荷限制：** 超过 1,024 字节（`BleMaxPayloadBytes`）的数据包将自动路由到 Wi-Fi Direct 或 NearLink。BLE 用于发现通告、小型控制数据包（RREQ/RREP、存在信标）和低带宽消息。

**Wi-Fi Direct** 连接超时为 10,000 毫秒（`WifiDirectTimeoutMs`），最大并发对等节点数为 8（`MaxWifiDirectPeers`）。

---

## 6. 发现协议

### 6.1. BLE 广播

Aether 节点主要通过 BLE 广播发现彼此。为防止通过静态标识符进行持续跟踪，协议采用两种隐私机制：轮换服务 UUID 和身份解析密钥。

**广播周期：** 扫描开启 2 秒，关闭 8 秒（`BleScanOnMs`/`BleScanOffMs`）。广播间隔为 1,000 毫秒（`BleAdvertiseIntervalMs`）。扫描间隔会添加 0-2,000 毫秒的随机抖动（`BleScanJitterMaxMs`）以防止时序模式检测。

**对等超时：** 在 30 秒内未重新发现的对等节点将被视为丢失（`PeerLost` 事件）。

### 6.2. 轮换服务 UUID

为防止长期 BLE 指纹识别，广播中使用的服务 UUID 每 15 分钟轮换一次（`BleUuidRotationSeconds = 900`）：

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key` 是每个节点生成一次并存储在安全存储中的 32 字节密钥。所有共享相同轮换密钥的 Aether 节点将为给定时间窗口派生相同的 UUID，从而实现相互发现而不暴露永久标识符。

在从非轮换方案过渡期间，将维护一个静态回退 UUID（`A3E7-1001-0001-0000-000000000000`）90 天。

### 6.3. 身份解析密钥（IRK）

每个节点生成一个存储在安全存储中的 128 位身份解析密钥（IRK）。IRK 在密钥交换过程中与受信任的对等节点共享。

**可解析私有地址（RPA）生成：**

1. 计算 `prand = HMAC-SHA256(IRK, window_bytes)[0..2]`（3 字节）。
2. 将 `prand[0]` 的最高两位设置为 `01`（BLE 规范的 RPA 标志）。
3. 计算 `hash = AES-128-ECB(IRK, pad(prand))`，其中 `prand` 占据 16 字节零填充输入的第 13-15 字节。
4. 构造 RPA：`hash[0..2] || prand[0..2]`（共 6 字节）。

**RPA 解析：** 拥有对等节点 IRK 的节点可以通过从 RPA 的 `prand` 分量重新计算哈希来验证观察到的 RPA 是否属于该对等节点。解析时间约为 O(N)，其中 N 是已知 IRK 的数量，100 个对等节点基准测试约 0.1 毫秒。

RPA 与服务 UUID 在相同的 15 分钟周期内轮换。

### 6.4. 基于 Geohash 的邻近度

节点可选择将其位置编码为 geohash。出于隐私考虑，geohash 被截断为 4 个字符，提供约 39 公里 x 20 公里的分辨率。这种粒度足以用于：

- 基于邻近度的频道发现
- DTN 流行病路由（向接收方最后已知 geohash 区域复制）
- SOS 警报地理上下文

完整精度的 geohash 永远不会通过网格传输。仅在节点隐私级别允许时（`PrivacyLevel.Full` 或 `PrivacyLevel.Partial`）共享截断形式。

---

## 7. 安全模型

### 7.1. 威胁模型

Aether 假设攻击者具备以下能力：

- **被动窃听：** 攻击者可以观察无线范围内所有 BLE 广播和网格流量。
- **主动注入：** 攻击者可以注入、修改或重放数据包。
- **女巫攻击：** 攻击者可以创建多个虚假节点身份。
- **选择性拒绝服务：** 攻击者作为中继节点时可以选择性地丢弃数据包。

### 7.2. 受保护内容

| Property | Protection Level | Mechanism |
|----------|-----------------|-----------|
| 消息内容 | 完全保密 | AES-256-GCM 与每消息密钥（第 4.5 节） |
| 发送方身份 | 部分 | UHID 在数据包头中可见；BLE 地址轮换（第 6 节） |
| 接收方身份 | 部分 | 目标 UHID 在路由数据包中可见；广播数据包的目标为空 |
| 路由元数据 | 最小 | 中间节点可见源/目标 UHID 和 TTL |
| 消息排序 | 受保护 | 对称棘轮中的计数器防止重排序 |
| 消息完整性 | 完全 | 每个数据包上的 Ed25519 签名（v2） |

### 7.3. 攻击抵抗

**重放攻击：**
每个数据包携带一个 8 字节的密码学随机数和毫秒精度的时间戳。中继节点维护 `(SenderUhid, NonceValue)` 对的去重缓存，TTL 为 5 分钟（`MaxPacketAgeSeconds = 300`）。来自同一发送方的重复随机数数据包将被丢弃。时间戳早于 5 分钟的数据包无论随机数如何都将被拒绝。

随机数去重缓存每 60 秒清理一次。已过期（早于 5 分钟）的条目将被删除。

**中间人攻击（MITM）：**
- 路由回复数据包必须携带来自声称目标节点的有效 Ed25519 签名。中间节点无法伪造 RREP，因为它们不持有目标的私钥。
- 预密钥包包含 `SignedPreKeySignature`（Ed25519），覆盖 `SignedPreKey`，将临时 ECDH 密钥绑定到长期身份。
- 会话建立（第 4.4 节）通过预密钥验证步骤，将会话与双方身份密码学绑定。

**女巫攻击：**
- 每个节点的可靠性评分从 50 开始，根据观察到的行为进行调整（第 3.5 节）。新创建的女巫节点没有积累的信誉。
- 可靠性评分低（接近 0）的节点在路由选择中被降低优先级。
- DTN 流行病路由算法使用 geohash 邻近度和中继成功历史来选择复制目标，使得女巫节点更难在不做出真实中继贡献的情况下吸引流量。

**泛洪攻击：**
- TTL 在每跳递减，TTL = 0 的数据包将被丢弃。默认 TTL 为 7，限制了任何广播的影响半径。
- 通过数据包 ID 进行 RREQ 去重，防止广播风暴引起的放大效应。当去重缓存超过 `DeduplicationCacheSize`（默认 10,000）个条目时将被清空。
- SOS 广播每个节点每小时限制 3 次（第 8 节）。

### 7.4. 密钥清零

所有中间加密材料在使用后立即清零：

- ECDH 密钥协商的 `sharedSecret`：在 HKDF 派生后清零。
- 链棘轮的 `messageKey`：在 AES-GCM 加解密后清零。
- 乱序解密的 `skippedKey`：使用后清零并从映射中删除。
- 派生的 `RootKey`、`SendChainKey`、`RecvChainKey`：从建立上下文中清零（会话保留自己的副本）。

清零使用 `CryptographicOperations.ZeroMemory`，该方法保证不会被编译器优化掉。

### 7.5. P-256 到 Ed25519 的迁移

协议支持从 ECDSA P-256 身份密钥（协议版本 1）到 Ed25519（协议版本 2）的 30 天过渡窗口：

1. 在过渡期间接受协议版本 1 数据包（未签名）。
2. 签名验证首先尝试 Ed25519。如果公钥长于 32 字节（表明是 DER 编码的 P-256 密钥），则回退到 P-256 ECDSA 验证。
3. 30 天窗口结束后，协议版本 1 数据包将被拒绝。
4. 未迁移的节点必须使用新的 Ed25519 身份重新初始化。

### 7.6. 司法管辖区意识

协议定义了司法管辖区层级，以处理围绕加密和网状网络的不同法律要求：

| Tier | Behavior | Example Jurisdictions |
|------|----------|-----------------------|
| 1    | 自由运行 | South Africa, Kenya, Ghana |
| 2    | 修改运行 | Nigeria, India, EU, US, UK |
| 3    | 仅网格（高风险） | China, Russia, Iran, UAE, Myanmar |
| 4    | 未知（默认仅网格） | All others |

层级选择影响功能可用性（例如，小费/金融功能在第 3 层可能被禁用），但不会削弱加密。端到端加密始终在任何司法管辖区下均会应用。

---

## 8. SOS 广播

SOS 机制是一种双路径紧急泛洪，设计用于用户处于危险中且需要同时联系附近的网格对等节点和/或互联网的情况。

### 8.1. 广播参数

| Parameter | Value | Description |
|-----------|-------|-------------|
| TTL       | 15    | 正常默认值（7）的两倍，确保更广泛的传播 |
| Priority  | 999   | 最高优先级；抢占中继队列中所有其他流量 |
| Rate limit| 3/hour| 每节点每小时限制，防止滥用 |
| Destination| empty | 广播给所有对等节点（无特定目标） |

### 8.2. 泛洪算法

1. 发起方构造一个 SOS 数据包，`Type = SosBroadcast`，`TTL = 15`，`Priority = 999`，`DestinationUhid` 为空。
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
   - **网格泛洪：** 通过所有可用传输广播给所有连接的对等节点。
   - **API 调用：** 发送至 AetherAPI 进行服务器端分发并桥接到 PanikAPI（短信/邮件分发）。
4. 两条路径相对于彼此均为即发即忘。如果 API 调用失败，网格泛洪将独立继续。

### 8.3. 中继行为

当节点收到 SOS 数据包时：

1. 通过数据包 `Id` 检查去重。如果已见过，则静默丢弃。
2. 反序列化有效载荷并为本地 UI 触发 `SosReceived` 事件。
3. 将警报添加到活跃警报列表。
4. 如果 `TTL > 1`，则递减 TTL 并**向所有对等节点重新广播**，无论路由表状态如何。SOS 数据包绕过正常路由——它们无条件泛洪。

### 8.4. 速率限制

每个节点维护最近广播时间戳的滑动窗口。在发起新的 SOS 之前：

1. 从队列中清除超过 1 小时的条目。
2. 如果队列包含 3 个或更多条目（`MaxSosBroadcastsPerHour`），则广播被拒绝。
3. 成功分发后，将当前时间戳入队。

速率限制仅适用于发起 SOS 广播，不适用于中继。

### 8.5. SOS-PanikAPI 桥接

通过网格收到的 SOS 广播可以转发到 PanikAPI 以进行传统紧急响应（向联系人发送短信、邮件警报）。相反，PanikAPI 紧急会话可以广播到网格以进行社区感知。通过标记来源（`direct` 与 `mesh_forward`）以及网格广播上的 `internet_forwarded` 标志来实现环路防止。

---

## 9. DTN 存储转发

延迟容忍网络（DTN）子系统在发送方和接收方之间不存在端到端路径时实现消息传递。包存储在中间节点上，并在连接变化时机会性地转发。

### 9.1. 包格式

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

### 9.2. 包生命周期

1. **创建：** 发送方创建一个带有加密有效载荷的包（通过与接收方的 Signal 会话加密）。`Status = Pending`，`CopyCount = 1`。
2. **立即传递尝试：** 发送方首先尝试直接网格路由（RREQ/RREP）。如果路由存在，则立即传递包，`Status` 转换为 `Delivered`。
3. **服务器中继尝试：** 如果网格路由失败，发送方尝试通过 AetherAPI 中继。如果服务器可以到达接收方（或将消息排队），则传递成功。
4. **存储转发：** 如果网格和服务器中继均失败，包留在本地存储（`Pending` 状态）等待下次传递扫描。

### 9.3. 传递扫描

周期性扫描每 60 秒运行一次（`DtnScanIntervalSeconds`）：

1. 从 SQLite（真实来源）加载所有待处理的包。
2. 对每个待处理的包：
   a. 尝试网格路由到接收方。
   b. 尝试服务器中继。
   c. 如果两者都失败且 `CopyCount < MaxCopies`，则尝试流行病复制（第 9.4 节）。
3. 删除已过期的包（`ExpiresAt <= now`）。

### 9.4. 流行病路由

当直接传递和服务器中继均失败时，使用流行病路由将包复制到附近的对等节点：

1. `EpidemicRoutingService` 从当前对等节点列表中选择复制目标。
2. 目标选择考虑：
   - **Geohash 邻近度：** geohash 更接近接收方最后已知 geohash 的对等节点优先。
   - **中继历史：** 可靠性评分较高的对等节点优先。
   - **副本预算：** 当 `CopyCount >= MaxCopies`（默认：3）时，复制停止。
3. 每次复制都向选定的对等节点发送一个 `DtnBundle` 数据包。
4. 收到后，对等节点的 DTN 服务调用 `AcceptCustodyAsync`。

### 9.5. 托管转移

当节点收到用于另一个节点的 DTN 包时：

1. **容量检查：** 节点检查其当前包数量是否超过 `DtnMaxBundlesPerNode`（50）。如果已满，则拒绝托管。
2. **接受：** 包状态设置为 `InCustody`，跳数递增，包持久化到 SQLite。
3. **托管记录：** 创建一个 `CustodyRecord` 记录转移（来源、目标、时间戳）。
4. **副本计数递增：** 包的 `CopyCount` 在持久存储中递增。
5. **确认：** 向转移节点发送一个 `DtnCustodyAck` 数据包，`Accepted = true`。
6. 接受节点负责在后续扫描中尝试传递。

### 9.6. 传递回执

当预期接收方收到 DTN 包时：

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
4. 回执也同步到 AetherAPI 进行分析。

### 9.7. 包过期

- 默认包 TTL 为 72 小时（`DtnBundleTtlHours`）。
- 过期包在周期性传递扫描期间被清理。
- 处于 `Expired` 或 `Delivered` 状态的包将从内存缓存和 SQLite 中删除。

### 9.8. 容量限制

| Parameter               | Default | Description |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | 最大包生命周期 |
| `DtnMaxCopies`          | 3       | 网络中每个包的最大副本数 |
| `DtnMaxBundlesPerNode`  | 50      | 单个节点承载的最大包数 |
| `DtnScanIntervalSeconds`| 60      | 传递扫描频率 |

---

## 10. 视频流

> **截至 2026-05-05 的状态——设计 + C# 脚手架，尚无实际编解码器管道。** 数据包类型 `StreamAnnounce`（11）、`StreamSegment`（12）、`StreamSubscribe`（13）、`StreamUnsubscribe`（14）、`VideoCall`（27）、`VideoSignaling`（28）、`VideoFrame`（31）、`ScreenShare`（32）已完成线格式定义，并通过跨语言夹具语料库进行往返测试。C# `Aether.Streaming` 模块提供接口、模型和骨架服务（`StreamingService`、`VideoCallService`、`WatchTogetherService`），这些服务连接路由/DI 接缝和单播片段扇出——但尚未绑定实际的视频编解码器。其他 7 种语言仅有线类型。`docs/adaptive-secure-streaming-spec.md` 的前向设计文档是目标架构。将以下文字视为这些服务**将要**实现的规范；请查阅 `OPEN_ISSUES.md` 了解生产就绪差距。


Aether 支持三种视频模式：点对点视频通话、群组视频（无限参与者，动态拓扑）和直播。所有视频帧均使用 Signal 协议加密并以 Ed25519 签名。

### 10.1. 传输能力矩阵

在发起视频通话之前，发起方查询传输层以确定到对等节点的最佳可用连接。传输决定了可能的视频质量：

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | 不支持（仅音频） | — | — | 64 Kbps | 仅同步数据包 |
| NearLink | 轻量 | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | 完整 | 1080p | H.264 | 3000 Kbps | 所有模式 |
| Internet | 完整 | 720p | H.264 | 1500 Kbps | 所有模式 |
| CircleLink | 不支持（仅音频） | — | — | 64 Kbps | 仅同步数据包 |

如果唯一可用的传输是 BLE 或 CircleLink，视频通话服务将自动降级为语音通话。

### 10.2. 视频编解码器

| Enum Value | Codec | Use Case |
|------------|-------|----------|
| 0 | H.264 | 默认。广泛支持，压缩效果好。 |
| 1 | H.265 | 更好的压缩。用于带宽受限的 NearLink。 |
| 2 | VP8 | 免版税替代方案。 |

### 10.3. 视频分辨率

| Enum Value | Resolution | Typical Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P 视频通话流程

1. **能力检查：** 发起方调用 `GetVideoCapabilityAsync(peerUhid)` 确定最佳传输、最大分辨率和推荐编解码器。
2. **Offer：** 发起方发送一个 `VideoSignaling` 数据包（类型 28），`SignalType = Offer`，包含首选编解码器、最大分辨率和最大比特率。
3. **Answer/Reject：** 被叫方以 `SignalType = Answer`（将编解码器协商为最低公分母）或 `SignalType = Reject` 响应。
4. **活跃通话：** 双方交换包含 H.264/H.265/VP8 NAL 单元的 `VideoCall` 数据包（类型 27）。每帧包含用于抖动缓冲排序的序列号和关键帧标志。
5. **屏幕共享：** 任一方均可切换屏幕共享。带 `SignalType = ScreenShareStart/Stop` 的 `VideoSignaling` 通知对等节点。屏幕共享帧使用 `PacketType.ScreenShare`（类型 32），但使用相同的处理管道。
6. **结束通话：** 任一方发送带 `SignalType = Bye` 的 `VideoSignaling`。

所有信令和帧有效载荷均使用 Signal 协议（X3DH 会话）加密。加密有效载荷在 `MeshPacket.Payload` 字段中以 JSON 编码的 `EncryptedPayload` 序列化。

### 10.5. 视频通话状态机

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

状态：`Initiating(0)`、`Ringing(1)`、`Active(2)`、`OnHold(3)`、`Ended(4)`、`Failed(5)`、`Rejected(6)`。

### 10.6. 群组视频

群组视频会话支持无限参与者。拓扑根据参与者数量动态选择：

- **FullMesh**（2-3 名参与者）：每个参与者向其他每个参与者各发送一条流。简单，低延迟。
- **SFU**（4+ 名参与者，阈值：`SfuThresholdParticipants = 4`）：选举一个节点作为 SFU 中继。每个参与者向中继发送一条流，中继将其分发给所有其他参与者。中继节点通过激励层获得小费。

拓扑切换是自动的：当第 4 名参与者加入时，会话从 FullMesh 过渡到 SFU。当参与者离开且数量降至 4 以下时，则过渡回来。

群组视频帧使用 `PacketType.VideoFrame`（类型 31）。在 SFU 模式下，帧发送到中继节点的 UHID，由中继节点重新广播。

### 10.7. 抖动缓冲

视频抖动缓冲独立于语音抖动缓冲（处理 20 毫秒 Opus 帧）运行：

- **范围：** 最小 60 毫秒，最大 500 毫秒。
- **自适应深度：** 通过指数移动平均（EMA）跟踪帧间抖动。缓冲深度 = 2× 抖动估计值，截断到 [60, 500] 毫秒。
- **关键帧感知丢弃：** 当缓冲区溢出时，非关键帧（P/B 帧）优先丢弃。I 帧（关键帧）永远不丢弃——它们是解码器恢复所必需的。
- **间隙处理：** 当检测到序列间隙时，缓冲区跳到下一个可用的关键帧，而不是无限等待。

### 10.8. 视频信令类型

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Offer | 带有编解码器/分辨率偏好的视频通话发起 |
| 1 | Answer | 带有协商参数的通话接受 |
| 2 | Reject | 通话拒绝 |
| 3 | Bye | 通话终止 |
| 4 | Upgrade | 请求更高质量（例如传输改善） |
| 5 | Downgrade | 请求更低质量（例如带宽下降） |
| 6 | ScreenShareStart | 对等节点开始共享屏幕 |
| 7 | ScreenShareStop | 对等节点停止共享屏幕 |

### 10.9. 加密模型

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| P2P 视频通话 | 每帧使用 Signal 协议 | X3DH 密钥协商 |
| 群组视频 | 群组频道密钥（AES-GCM） | 在会话创建时通过 Signal 协议分发 |
| 屏幕共享 | 与父通话模式相同 | 继承自视频通话会话 |

---

## 11. 同步观看

> **截至 2026-05-05 的状态——设计 + C# 脚手架，成熟度与 §10 相同。** 数据包类型 `WatchSync`（29）、`WatchReaction`（30）、`WatchChunkRequest`（33）、`TorrentMetadata`（34）已完成线格式定义和夹具测试。`Aether.Streaming.WatchTogetherService` 提供协调骨架（会话状态、通过 `IMeshSender` 传播同步命令、RTT 补偿辅助函数）；BitTorrent 摄取、ChipIn SDPKT 结算和从对等节点获取块在任何语言中均未实现。将以下文字视为目标协议；`docs/adaptive-secure-streaming-spec.md` 的前向设计文档以更多细节覆盖相同内容。


同步观看支持在一组网格对等节点之间同步媒体播放。主机对播放（播放、暂停、跳转、速度）拥有独占控制权。同步命令包含用于 RTT 补偿的墙钟时间戳。

### 11.1. 观看模式

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | 仅同步数据包（每个 < 100 字节） | 任意（可通过 BLE 工作） |
| 1 | StreamFromHost | P2P 块传输（复用 P2pContentService） | WiFi Direct 或 Internet |
| 2 | BitTorrent | 通过网关节点的网格 + 外部群 | WiFi Direct 或 Internet |

### 11.2. SharedFile 模式

两个参与者拥有相同的文件（通过 SHA-256 内容哈希匹配）。仅交换 `WatchSync` 数据包。这是带宽效率最高的模式，可通过 BLE 工作。

1. 主机使用 `contentHash`（文件的 SHA-256）创建观看会话。
2. 参与者加入，并在其播放器加载完成时报告 `IsReady = true`。
3. 当**所有**参与者报告就绪后，会话开始。
4. 主机以 `WatchSync` 数据包（类型 29）发送播放/暂停/跳转/速度命令。
5. 接收方应用 RTT 补偿：`adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`。

### 11.3. StreamFromHost 模式

只有主机拥有该文件。主机生成一个 `ContentManifest`（复用 P2P 内容系统），参与者通过网格下载块。

- 块选择使用 `SequentialFromPosition` 策略（而非 `RarestFirst`）：优先选择当前播放位置之后的块，然后回填用于种子。
- 缓冲目标：提前 30 秒（`WatchTogetherBufferAheadSeconds`）。
- 自动暂停：如果**任何**参与者的缓冲区低于 10 秒（`WatchTogetherMinBufferSeconds`），会话将使用 `BufferUnderrun` 同步命令自动暂停所有参与者。当所有参与者都有足够的缓冲区时（`BufferReady`），播放恢复。
- 随着观看者下载块，他们成为其他观看者的种子节点（网格内的 BitTorrent 风格群传播）。

### 11.4. BitTorrent 模式

参与者在群聊中共享 `.torrent` 文件或磁力链接。`TorrentMetadata` 数据包（类型 34）将种子信息分发给所有会话参与者。

**网格到群的桥接：**
- 网关节点（有互联网的节点）从外部 BitTorrent 群下载片段。
- 网关节点为网格分发重新加密已下载的片段，并向网格对等节点播种。
- 没有互联网的网格对等节点从网关节点和彼此接收片段。
- P2P 内容引擎在 BitTorrent 的片段模型和 Aether 的块模型之间进行转换。

一旦缓冲了足够的内容，同步观看播放就使用与 SharedFile 模式相同的同步协议开始。

### 11.5. 观看会话状态机

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

状态：`WaitingForReady(0)`、`Buffering(1)`、`Playing(2)`、`Paused(3)`、`Ended(4)`。

### 11.6. 同步命令类型

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Play | 在指定位置恢复播放 |
| 1 | Pause | 在指定位置暂停 |
| 2 | Seek | 跳转到指定位置 |
| 3 | Speed | 更改播放速度 |
| 4 | BufferUnderrun | 自动暂停——参与者的缓冲区严重不足 |
| 5 | BufferReady | 恢复——所有参与者都有足够的缓冲区 |

### 11.7. RTT 补偿

同步命令包含一个 `WallClockMs` 字段（Unix 纪元毫秒）。当接收方处理同步命令时：

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. 对于 Play 和 BufferReady 命令：`adjustedPosition = commandPosition + networkDelay`
4. 对于 Pause 和 Seek 命令：精确应用位置（无需调整，因为播放正在停止/跳转）。

这确保所有参与者在半个网络 RTT 内保持同步。

### 11.8. 反应

参与者可以在播放过程中对内容作出反应：

- **表情反应：** 带有 `Type = Emoji` 的 `WatchReaction` 数据包（类型 30），携带表情字符串和反应时的媒体位置。
- **语音评论：** 带有 `Type = VoiceComment` 的 `WatchReaction` 数据包，携带 Opus 编码的音频数据（最长 10 秒）。语音数据包含在反应的 `VoiceData` 字段中。

反应广播给所有会话参与者。它们以媒体位置为时间戳，允许与回放同步显示。

### 11.9. ChipIn——群组内容获取

ChipIn 使群组成员能够集资（以 ZAR 计价，通过 LedgerAPI 经 SDPKT 钱包结算）以集体获取内容进行群组观看。

**状态机：**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

状态：`Collecting(0)`、`Funded(1)`、`Purchasing(2)`、`Acquired(3)`、`Failed(4)`、`Refunded(5)`。

**流程：**
1. 发起方创建一个带有目标金额和内容描述的 `ChipInPool`。
2. 参与者通过 SDPKT 钱包交易贡献金额。
3. 当 `CollectedAmount >= TargetAmount` 时，状态转换为 `Funded`。
4. 系统获取内容（例如，发起 BitTorrent 下载）。
5. 内容可用后，状态转换为 `Acquired`，同步观看可以开始。

每笔贡献均记录 SDPKT 交易 ID 以供审计追踪。

### 11.10. 加密模型

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| 观看同步命令 | 频道/会话密钥 | 现有 Signal 协议会话 |
| 内容块（StreamFromHost） | 每清单内容密钥 | 通过 Signal 协议分发 |
| BitTorrent 片段 | 摄取时重新加密 | 网关从群下载明文，为网格加密 |
| 观看反应 | 会话密钥 | 从会话密钥派生 |

### 11.11. 功能标志

所有视频和同步观看功能均由功能标志控制（默认全部禁用）：

| Flag | Parent | Description |
|------|--------|-------------|
| AETHER_VIDEO_CALL | AETHER_VOICE | P2P 和群组视频通话 |
| AETHER_VIDEO_GROUP | AETHER_VIDEO_CALL | 多方视频会话 |
| AETHER_SCREEN_SHARE | AETHER_VIDEO_CALL | 视频通话中的屏幕共享 |
| AETHER_WATCH_TOGETHER | AETHER_CONTENT_P2P | 同步媒体播放 |
| AETHER_WATCH_REACTIONS | AETHER_WATCH_TOGETHER | 表情和语音反应 |
| AETHER_TORRENT_INGEST | AETHER_CONTENT_P2P | 接受 BitTorrent 文件用于网格分发 |

功能标志具有父级依赖：子标志只有在其父级也启用的情况下才能启用。这允许渐进式发布。

---

## 附录 A：常量参考

所有协议常量均在 `ProtocolConstants` 中定义，此处为参考重录：

### 路由
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE 发现
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### 安全
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

### 传输
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### 心跳
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### 在线状态
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### 语音
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### 流媒体
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### 视频
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

## 附录 B：术语表

| Term | Definition |
|------|------------|
| **UHID** | 通用硬件标识符（Universal Hardware Identifier）。标识网格节点的唯一字符串，从设备身份和密码学密钥派生。 |
| **RREQ** | 路由请求（Route Request）。用于发现到目标节点路径的广播数据包。 |
| **RREP** | 路由回复（Route Reply）。沿 RREQ 建立的反向路由回传的单播数据包。 |
| **IRK** | 身份解析密钥（Identity Resolving Key）。用于生成和解析 BLE 可解析私有地址的 128 位密钥。 |
| **RPA** | 可解析私有地址（Resolvable Private Address）。周期性轮换的 6 字节 BLE 地址，但持有发送方 IRK 的对等节点可以解析。 |
| **X3DH** | 扩展三重 Diffie-Hellman（Extended Triple Diffie-Hellman）。一种支持异步会话建立的密钥协商协议。 |
| **DTN** | 延迟容忍网络（Delay-Tolerant Networking）。一种用于间歇性连接环境的存储转发范式。 |
| **Gateway** | 网关。拥有互联网连接并在网格流量和基于 IP 的服务之间进行桥接的网格节点。 |
| **HKDF** | 基于 HMAC 的密钥派生函数（HMAC-based Key Derivation Function）。用于从单个共享密钥派生多个密钥。 |
| **Pre-key bundle** | 预密钥包。一组已发布的密钥，允许发送方在接收方不在线的情况下建立加密会话。 |
| **SFU** | 选择性转发单元（Selective Forwarding Unit）。从每个发送方接收一条视频流并将其分发给所有其他参与者的中继节点，减少每个节点的上传带宽。 |
| **ChipIn** | 群组集资机制，参与者汇集 SDPKT 资金以集体获取内容进行群组观看。 |
| **NAL** | 网络抽象层（Network Abstraction Layer）。H.264 和 H.265 编解码器用于打包视频帧的封装格式。 |

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
