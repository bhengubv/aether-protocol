# Aether 协议 - Kotlin 实现

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Aether 网状网络协议完整的、可生产使用的 Kotlin 实现，与 C# 参考实现完全兼容跨语言线格式。

## 概述

Aether 是一种去中心化网状网络协议，适用于网络连接间歇性中断或完全缺失的环境。本 Kotlin 实现提供：

- **线格式兼容性** — 与 C# 完全匹配（二进制数据包序列化完全一致）
- **Ed25519 签名** — 用于数据包身份验证和完整性验证
- **Signal 协议** — 端对端加密（X3DH 密钥协商、对称棘轮、AES-256-GCM）
- **ECDH P-256** — 用于会话建立的密钥协商
- **数据包序列化/反序列化** — 小端字节序多字节整数
- **重放保护** — 通过随机数去重实现
- **传输抽象** — 支持 BLE、Wi-Fi Direct 及进程内消息传递

## 项目结构

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherMeshNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## 构建

### 前提条件

- JDK 17 或更高版本
- Gradle 8.0 或更高版本

### 编译

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### 运行演示程序

```bash
./gradlew run
```

演示程序展示：
1. Ed25519 密钥对生成
2. 预密钥包创建与交换
3. Signal 协议会话建立
4. 使用 Ed25519 进行数据包签名
5. 数据包序列化/反序列化
6. 消息加密与解密
7. 重放保护
8. 进程内传输消息传递

## 核心组件

### 1. 数据包序列化（`PacketSerializer`）

线格式（小端字节序）：
- 协议版本（1 字节）
- 数据包类型（1 字节）
- 数据包 ID / UUID（16 字节）
- 优先级（1 字节）
- TTL（4 字节，int32）
- TimestampMs（8 字节，int64）
- SourceUhid（2 字节长度前缀 + UTF-8 字节）
- DestinationUhid（2 字节长度前缀 + UTF-8 字节）
- PacketNonce（2 字节长度前缀 + 字节）
- Payload（4 字节长度前缀 + 字节）
- Signature（2 字节长度前缀 + 字节）

与 C# `PacketSerializer` 完全兼容。

### 2. Ed25519 签名（`Ed25519Service`、`PacketSigning`）

- **密钥生成**：32 字节私钥种子，32 字节公钥
- **签名**：64 字节签名，覆盖确定性可签名数据
- **验证**：在迁移期间替代 P-256 ECDSA
- **可签名数据格式**：与 C# 规范完全匹配（数据包 nonce、时间戳、类型、UHID、载荷哈希、TTL、优先级）
- **重放保护**：具有 5 分钟 TTL 的随机数去重

### 3. Signal 协议（`SignalProtocol`）

实现带对称棘轮的 X3DH 密钥协商：

**会话建立：**
- 获取对等方的预密钥包
- 使用 Ed25519 验证包签名
- 执行 X3DH：DH(本地身份, 远端签名预密钥) + DH(本地身份, 远端预密钥)
- 使用 HKDF-SHA256 派生根密钥和链密钥

**加密/解密：**
- 使用 HMAC-SHA256 的对称棘轮
- AES-256-GCM，12 字节随机 nonce
- 具有前向保密性的单消息密钥
- 乱序消息处理（跳过密钥缓存，最多 1000 个密钥）

**参数：**
- 根密钥派生信息：`"aether-root-v1"`
- 发送链派生信息：`"aether-chain-send-v1"`
- 接收链派生信息：`"aether-chain-recv-v1"`
- 消息密钥盐：`0x01`，链密钥盐：`0x02`

### 4. 传输抽象（`TransportService`）

物理传输（BLE、Wi-Fi Direct 等）的接口：

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport：** 使用全局 `ConcurrentHashMap` 的参考实现，用于测试/演示。

### 5. 领域模型（`Models.kt`）

- **AetherMeshNode**：带 UHID、公钥、能力和地理哈希的节点身份
- **PeerInfo**：带可靠性分数和最后可见时间戳的已知对等方
- **RouteEntry**：带跳数和质量分数的路由表条目
- **NodeCapabilities**：位字段（BLE、Wi-Fi Direct、网关、中继、SOS、流媒体、语音、DTN）
- **DtnBundle**：带过期时间和副本计数的存储转发包

## 协议常量

主要常量（来自 `Constants.kt`）：

| 类别 | 常量 | 值 |
|----------|----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## 数据包类型

全部 23 种数据包类型与 C# 枚举值匹配（1-23）：

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## 依赖

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519、ECDH P-256、AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — 密钥格式支持
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — 异步/等待、Flow
- **org.slf4j:slf4j-api:2.0.9** — 日志记录
- **kotlin-stdlib** — Kotlin 标准库

## 使用示例

### 密钥生成

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### 数据包签名

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### 数据包序列化

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Signal 协议加密

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## 跨语言兼容性

本实现与 C# 参考实现保持**完全的线格式兼容性**：

- 二进制数据包格式：完全相同的小端字节序布局
- 数据包类型枚举：值与 C# 枚举完全匹配（1-23）
- Ed25519 签名：与 NSec/libsodium 兼容
- ECDH P-256：标准曲线，跨语言兼容
- HKDF-SHA256：RFC 5869 标准实现
- AES-256-GCM：NIST 标准，12 字节 nonce，16 字节标签

在 Kotlin 中序列化的数据包可在 C# 中反序列化，反之亦然。

## 测试

本实现包含一个全面的演示程序（`Demo.kt`），涵盖：

1. 密钥生成与公钥导出
2. 预密钥包生成与交换
3. 通过 Signal 协议建立会话
4. 数据包创建、签名和序列化
5. 数据包反序列化和签名验证
6. 消息加密与解密
7. 重放攻击防护
8. 进程内传输消息传递

运行方式：
```bash
./gradlew run
```

## 安全注意事项

- **密钥清零**：所有中间密码学材料在使用后通过 `CryptographicOperations.ZeroMemory`（Kotlin 等效：`fill(0)`）清零
- **重放保护**：具有 5 分钟 TTL 的随机数去重，防止重放攻击
- **前向保密性**：从链棘轮派生的单消息密钥
- **乱序处理**：跳过密钥缓存最多 1000 个，防止内存耗尽
- **RREP 认证**：路由回复数据包由目标节点签名
- **数据包保密性**：消息内容使用 AES-256-GCM 加密

## 未来扩展

本实现为以下功能提供了扩展钩子：

- **BLE 传输**（`TransportService` 接口）
- **Wi-Fi Direct 传输**（相同接口）
- **DTN 流行病路由**（`DtnBundle` 模型已就绪）
- **SOS 广播**（数据包类型已定义）
- **存在感知信标**（数据包类型已定义）
- **语音和流媒体**（数据包类型已定义）
- **双棘轮**（当始终在线传输可用时）

## 协议文档

完整协议规范：`/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## 许可证

SPDX-License-Identifier: MIT
