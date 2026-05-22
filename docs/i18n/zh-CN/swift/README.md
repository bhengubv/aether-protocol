# Aether 协议 - Swift 实现

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Aether 网状网络协议的完整 Swift 实现，为 iOS 和 macOS 提供端到端加密、路由和点对点通信能力。

## 概述

Aether 是一种去中心化网状网络协议，专为网络连接断断续续或完全缺失的环境而设计。本 Swift 实现提供：

- 与 C# 参考实现**线格式兼容的序列化**
- 用于数据包身份验证的 **Ed25519 签名**
- 用于端到端加密的 **Signal 协议**（X3DH + 对称棘轮）
- 支持多种物理层（BLE、Wi-Fi Direct、NearLink）的**传输抽象**
- 使用 Swift Concurrency 的**线程安全异步 API**

## 环境要求

- Swift 5.9+
- macOS 13.0+ 或 iOS 16.0+
- Xcode 15+

## 依赖项

- [swift-crypto](https://github.com/apple/swift-crypto) - 密码学原语（Ed25519、P-256 ECDH、AES-GCM、HKDF、SHA-256）

## 架构

### 核心组件

#### 协议层
- **MeshPacket**：核心数据包结构（UUID、类型、源/目标 UHID、TTL、优先级、有效载荷、签名）
- **PacketType**：26 种数据包类型的枚举（RouteRequest、Data、SosBroadcast、DtnBundle 等）
- **PacketSerializer**：带小端字节序线格式的二进制序列化/反序列化器

#### 安全层
- **Ed25519Service**：使用 Curve25519 的密钥生成、签名与验证
- **SignalProtocolService**：X3DH 密钥协商 + 对称棘轮，用于加密会话
- **PacketSigningService**：带随机数去重和重放防护的数据包级签名

#### 传输层
- **TransportService**：定义传输契约的协议
- **InProcessTransport**：用于测试和本地通信的内存传输

#### 数据模型
- **AetherNode**：带 UHID 和身份密钥的节点表示
- **PreKeyBundle**：用于异步会话建立的包
- **EncryptedPayload**：加密消息包装器
- **DtnBundle**：延迟容忍网络包
- **PeerInfo**：路由表对等信息

### 常量
所有协议常量（TTL、超时、容量限制）均定义于 `ProtocolConstants`。

## 安装

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

在您的 Package.swift 中：

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherProtocol", package: "aether-protocol-swift")
    ]
)
```

## 快速入门

### 1. 数据包序列化

```swift
import AetherProtocol

// Create a packet
var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice-node",
    destinationUhid: "bob-node",
    payload: "Hello, Aether!".data(using: .utf8)!
)

// Serialize to bytes
let serialized = PacketSerializer.serialize(packet)

// Deserialize
let deserialized = try PacketSerializer.deserialize(serialized)
```

### 2. Ed25519 签名

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Signal 协议会话

```swift
let alice = SignalProtocolService()
let bob = SignalProtocolService()

// Key exchange: Bob publishes pre-key bundle
let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob-node")

// Alice processes Bob's bundle and establishes session
try await alice.processPreKeyBundle(bobBundle)

// Alice encrypts message
let encrypted = try await alice.encrypt(
    peerUhid: "bob-node",
    plaintext: "Secret message".data(using: .utf8)!
)

// For Bob to decrypt, he also needs Alice's bundle
let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice-node")
try await bob.processPreKeyBundle(aliceBundle)

// Bob decrypts
let decrypted = try await bob.decrypt(peerUhid: "alice-node", payload: encrypted)
```

### 4. 数据包签名

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. 进程内传输（测试）

```swift
let alice = InProcessTransport(uhid: "alice")
let bob = InProcessTransport(uhid: "bob")

// Set up data received callback
await bob.onDataReceived { senderUhid, data in
    print("Received \(data.count) bytes from \(senderUhid)")
}

// Send message
let success = await alice.sendAsync(
    peerUhid: "bob",
    data: "Hello".data(using: .utf8)!,
    cancellationToken: nil
)
```

## 线格式

所有数据包遵循小端字节序线格式：

```
[1 byte]   Protocol version (2 = signed)
[1 byte]   Packet type
[16 bytes] Packet ID (UUID)
[1 byte]   Priority
[4 bytes]  TTL (Int32)
[8 bytes]  TimestampMs (Int64)
[2 bytes]  SourceUhid length (UInt16)
[N bytes]  SourceUhid (UTF-8)
[2 bytes]  DestinationUhid length (UInt16)
[N bytes]  DestinationUhid (UTF-8)
[2 bytes]  PacketNonce length (UInt16)
[N bytes]  PacketNonce (8 bytes)
[4 bytes]  Payload length (Int32)
[N bytes]  Payload
[2 bytes]  Signature length (UInt16)
[N bytes]  Signature (64 bytes Ed25519)
```

空 UHID 和空有效载荷的最小数据包大小：**43 字节**。

## 安全模型

### 加密
- **算法**：AES-256-GCM
- **密钥派生**：从 X3DH 共享密钥进行 HKDF-SHA256
- **会话棘轮**：对称棘轮每条消息推进链密钥

### 签名
- **算法**：Ed25519（Curve25519）
- **有效载荷保护**：SHA256 哈希包含在可签名数据中
- **重放防护**：8 字节随机数 + 毫秒时间戳 + 去重缓存

### 密钥交换
- **协议**：带 ECDH P-256 的 X3DH 变体
- **预密钥绑定**：用 Ed25519 验证已签名的预密钥
- **异步性**：无需接收方在线即可建立会话

### 限制
- **MaxSkippedKeys**：1000（每会话乱序消息数）
- **MaxPacketAge**：300 秒（5 分钟）

## 协议常量

- **DefaultTtl**：7
- **SosTtl**：15
- **RouteTimeoutMs**：5,000
- **RouteExpirySeconds**：300
- **DtnBundleTtlHours**：72
- **DtnMaxCopies**：3
- **AesGcmNonceSize**：12 字节
- **AesGcmTagSize**：16 字节

完整列表见 `ProtocolConstants`。

## 线程安全

所有服务均为 `actor` 隔离，以实现线程安全的并发访问：

- `SignalProtocolService` - 会话管理与加密
- `PacketSigningService` - 数据包签名与验证
- `InProcessTransport` - 消息投递

与 Swift Concurrency 结合使用：

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## 测试

运行内置演示：

```bash
cd swift
swift run aether-demo
```

预期输出：

```
=== Aether Protocol Demo ===

Test 1: Packet Serialization
---
Original packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
Serialized size: XX bytes
Deserialized packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
✓ Serialization/Deserialization successful

Test 2: Ed25519 Signing
...

Test 5: End-to-End Messaging (Full Stack)
...
✓ End-to-end messaging test successful

=== All Tests Completed ===
```

## 互操作性

线格式兼容以下实现：
- **Aether.Core**（C#）- 参考实现
- **aether-protocol-go** - Go 实现
- **aether-protocol-rust** - Rust 实现

所有实现均使用：
- 小端字节序整数
- UTF-8 字符串编码
- Ed25519 签名（64 字节）
- AES-256-GCM 加密（12 字节随机数，16 字节认证标签）

## 性能

Apple Silicon（M1 Pro）上的基准测试：

| 操作 | 耗时 |
|------|------|
| 数据包序列化 | ~0.5 μs |
| 数据包反序列化 | ~0.7 μs |
| Ed25519 签名 | ~3.5 ms |
| Ed25519 验证 | ~4.2 ms |
| AES-256-GCM 加密 | ~0.8 μs |
| AES-256-GCM 解密 | ~0.9 μs |
| X3DH 密钥协商 | ~8.5 ms |
| 对称棘轮 | ~0.3 μs |

## 未来工作

- **BLE 传输**：蓝牙低功耗实现
- **Wi-Fi Direct 传输**：直接点对点 Wi-Fi
- **双棘轮**：带消息棘轮的完整前向保密性
- **AODV 路由**：路由发现与维护
- **DTN 服务**：存储转发包投递
- **存在感与邻近性**：基于位置的对等发现
- **语音与流媒体**：实时媒体协议

## 许可证

MIT - 详见 LICENSE 文件

## 参考资料

1. [Aether 协议规范](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [双棘轮算法](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869：HKDF](https://tools.ietf.org/html/rfc5869)
5. [Ed25519 签名](https://en.wikipedia.org/wiki/Curve25519)
6. [AES-GCM 模式](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## 贡献

这是一个参考实现。如需报告错误或提出功能请求，请在 GitHub 上提交 issue。
