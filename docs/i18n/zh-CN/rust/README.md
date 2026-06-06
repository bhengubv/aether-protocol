# Aether 协议 — Rust 实现

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Aether 网状网络协议的完整 Rust 实现，与 C# 参考实现的线格式完全兼容。

## 概述

本 crate 提供：

- **MeshPacket 序列化/反序列化** — 与 C# PacketSerializer 完全相同的二进制线格式
- **Ed25519 签名** — 身份密钥生成、签名与验证
- **Signal 协议** — 基于 X3DH 的密钥协商，带对称棘轮以实现前向保密性
- **数据包签名服务** — 随机数去重与新鲜度检查
- **进程内传输** — 用于测试和演示的模拟网状网络

## 项目结构

```
rust/
├── Cargo.toml                          # Crate manifest
├── src/
│   ├── lib.rs                          # Module declarations
│   ├── main.rs                         # Demo application
│   ├── constants.rs                    # Protocol constants
│   ├── models.rs                       # Core data structures
│   ├── protocol/
│   │   ├── mod.rs                      # MeshPacket, PacketType enum
│   │   └── serializer.rs               # Binary serialization (wire-compatible)
│   ├── security/
│   │   ├── mod.rs                      # Module declarations
│   │   ├── ed25519.rs                  # Ed25519 signing service
│   │   ├── signal_protocol.rs          # Signal Protocol implementation
│   │   └── packet_signing.rs           # Packet signing + nonce dedup
│   └── transport/
│       ├── mod.rs                      # TransportService trait
│       └── in_process.rs               # In-memory transport implementation
```

## 主要特性

### 1. 线格式兼容性

`PacketSerializer` 产生的输出与 C# 实现逐字节相同：

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (GUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (u16, LE)
[N bytes] SourceUhid (UTF-8)
[2 bytes] DestinationUhid length (u16, LE)
[N bytes] DestinationUhid (UTF-8)
[2 bytes] PacketNonce length (u16, LE)
[N bytes] PacketNonce
[4 bytes] Payload length (i32, LE)
[N bytes] Payload
[2 bytes] Signature length (u16, LE)
[N bytes] Signature
```

所有多字节整数使用小端字节序。字符串长度前缀：SourceUhid 和 DestinationUhid 使用 u16，Payload 和 Signature 使用 i32，与协议规范一致。

### 2. 数据包类型

协议规范中定义的全部 26 种数据包类型均已实现：

- RouteRequest (1)、RouteReply (2)、Data (3)、Ack (4)
- SosBroadcast (5)、SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8)、ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11)、StreamSegment (12)、StreamSubscribe (13)、StreamUnsubscribe (14)
- VoicePtt (15)、VoiceCall (16)、VoiceSignaling (17)
- DtnBundle (18)、DtnCustodyAck (19)、DtnDeliveryReceipt (20)
- PresenceBeacon (21)、PresenceQuery (22)、ProfileSync (23)
- TipPacket (24)、PreKeyRequest (25)、PreKeyResponse (26)

### 3. Ed25519 签名

- 32 字节私钥（种子）、32 字节公钥、64 字节签名
- 使用 `ed25519-dalek` 进行密码学操作
- 使用后安全清零密钥

### 4. Signal 协议

带对称棘轮的 X3DH 密钥协商：

- **密钥协商：** 使用临时密钥和签名预密钥的 ECDH P-256
- **密钥派生：** 带唯一信息字符串的 HKDF-SHA256
  - `aether-root-v1` — 根密钥
  - `aether-chain-send-v1` — 发送链密钥
  - `aether-chain-recv-v1` — 接收链密钥
- **加密：** AES-256-GCM（12 字节随机数，16 字节认证标签）
- **棘轮：** 基于计数器的消息密钥的对称链密钥推进
- **乱序处理：** 最多缓存 1000 个跳过的消息密钥

### 5. 数据包签名服务

- 随机 8 字节随机数生成
- 毫秒精度时间戳
- 新鲜度验证（5 分钟窗口）
- 每发送方的随机数去重（防止重放）
- 自动清理过期条目

### 6. 进程内传输

用于测试的模拟网状网络：

- 使用并发 HashMap 的节点静态注册表
- 即发即忘的消息投递
- 双向对等连接检查
- 适用于演示和单元测试

## 使用方法

### 基本密钥生成与签名

```rust
use aethernet_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Signal 协议会话

```rust
use aethernet_protocol::security::SignalProtocolService;

let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

// Bob publishes pre-key bundle
let bob_bundle = bob.generate_pre_key_bundle("bob-node")?;

// Alice processes bundle and establishes session
alice.process_pre_key_bundle(&bob_bundle)?;

// Alice encrypts message
let plaintext = b"Hello!";
let encrypted = alice.encrypt("bob-node", plaintext)?;

// Bob decrypts
let alice_bundle = alice.generate_pre_key_bundle("alice-node")?;
bob.process_pre_key_bundle(&alice_bundle)?;
let decrypted = bob.decrypt("alice-node", &encrypted)?;

assert_eq!(decrypted, plaintext);
```

### 数据包序列化

```rust
use aethernet_protocol::protocol::{MeshPacket, PacketType};
use aethernet_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### 数据包签名

```rust
use aethernet_protocol::security::PacketSigningService;
use aethernet_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### 进程内传输

```rust
use aethernet_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## 运行演示

```bash
cargo run --release
```

演示执行以下步骤：

1. 为 Alice 和 Bob 生成身份密钥
2. 初始化 Signal 协议服务
3. 生成并交换预密钥包
4. 建立加密会话
5. 交换加密消息
6. 创建并签名网状数据包
7. 验证数据包签名
8. 序列化与反序列化数据包
9. 演示进程内传输

## 常量

所有协议常量定义于 `src/constants.rs`，与 C# 规范匹配：

- 路由：DefaultTtl=7、SosTtl=15、RouteTimeoutMs=5000
- 安全：MaxPacketAgeSeconds=300、MaxSkippedKeys=1000
- 传输：BleMaxPayloadBytes=1024、WifiDirectTimeoutMs=10000
- DTN：DtnBundleTtlHours=72、DtnMaxCopies=3
- 语音/流媒体：各种码率和缓冲区配置

## 依赖项

- `ed25519-dalek` — Ed25519 签名
- `x25519-dalek` — X25519 密钥协商
- `aes-gcm` — AES-256-GCM 加密
- `hkdf` — HKDF 密钥派生
- `sha2` — SHA-256 哈希
- `hmac` — HMAC 操作
- `rand` — 随机数生成
- `uuid` — GUID 生成与序列化
- `serde` + `serde_json` — 序列化
- `tokio` — 异步运行时
- `async-trait` — 异步 trait 方法

## 测试

运行所有测试：

```bash
cargo test
```

测试覆盖范围：

- 数据包创建与 TTL 管理
- 数据包类型转换
- 序列化/反序列化往返
- Ed25519 密钥生成与签名验证
- Signal 协议会话建立与加密
- 数据包签名与新鲜度验证
- 进程内传输连接性

## 协议符合性

本实现遵循 Aether 协议规范（版本 2.0），包括：

- ✅ 二进制线格式（小端字节序，长度前缀）
- ✅ 全部 26 种数据包类型
- ✅ 带随机数去重的 Ed25519 签名
- ✅ 带 HKDF-SHA256 的 X3DH 密钥协商
- ✅ 带 12 字节随机数的 AES-256-GCM 加密
- ✅ 带乱序处理的对称棘轮
- ✅ 预密钥包生成与处理
- ✅ 数据包可签名数据构造（SHA-256 有效载荷哈希）
- ✅ 传输 trait 抽象

## 注意事项

- 线格式全程使用小端字节序（与 C# BinaryPrimitives.WriteInt32LittleEndian 匹配）
- 字符串长度前缀：UHID 使用 u16，payload/signature 使用 i32（与 C# WriteUInt16/WriteInt32 匹配）
- 所有密码学密钥材料使用等价于 `CryptographicOperations` 的方式在使用后清零
- Signal 协议实现在链棘轮中使用字节 [0x01] 和 [0x02] 作为盐值进行 HKDF 运算（与 C# HKDF 使用方式匹配）
- 随机数去重使用每发送方的 VecDeque，并自动清理 5 分钟以上的条目
