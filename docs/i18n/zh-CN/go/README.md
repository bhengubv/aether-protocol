# Aether 协议 - Go 语言实现

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](../../ar/go/README.md) · [中文简体](README.md) · [日本語](../../ja/go/README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](../../ru/go/README.md) · [فارسی](../../fa/go/README.md) · [한국어](../../ko/go/README.md)

Aether 网状网络协议的完整 Go 语言实现，与 C# 参考实现在线格式上完全兼容。

## 概述

本模块实现了 Aether 去中心化网状网络协议，适用于网络连接间歇性中断或完全缺失的环境。提供：

- **数据包序列化**：与 C# 参考实现兼容的二进制线格式（小端字节序编码）
- **Ed25519 签名**：密码学数据包身份验证
- **Signal 协议**：X3DH 密钥协商 + 对称棘轮，用于端对端加密
- **数据包签名服务**：具有 5 分钟 TTL 的随机数去重，用于防重放攻击
- **进程内传输**：基于内存的传输，用于测试和进程间通信
- **模型**：AetherNode、PeerInfo、RouteEntry、DtnBundle、SosAlert 结构
- **协议常量**：所有路由、发现、安全和传输常量

## 模块结构

```
aether-protocol/go/
├── go.mod                          # Module definition
├── go.sum                           # Dependency checksums
├── README.md                        # This file
│
├── protocol/
│   ├── packet.go                   # MeshPacket struct, PacketType constants
│   └── serializer.go               # Binary serialization (little-endian)
│
├── security/
│   ├── ed25519.go                  # Ed25519 signing/verification
│   ├── signal_protocol.go          # Signal Protocol (X3DH + ratchet)
│   ├── packet_signing.go           # Nonce deduplication service
│   └── models.go                   # PreKeyBundle, EncryptedPayload, SignalSession
│
├── transport/
│   ├── transport.go                # TransportService interface
│   └── in_process.go               # In-memory transport implementation
│
├── models/
│   └── models.go                   # Domain models (Node, Route, DtnBundle, etc.)
│
├── constants/
│   └── constants.go                # Protocol constants
│
└── cmd/demo/
    └── main.go                      # Comprehensive demo program
```

## 主要功能

### 1. 数据包序列化（小端字节序）

线格式与 C# 完全匹配，所有多字节整数均使用小端字节序编码：

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (UUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (uint16, LE)
[N bytes] SourceUhid (UTF-8)
... (destination, nonce, payload, signature)
```

**示例：**
```go
serializer := &protocol.PacketSerializer{}
packet := protocol.NewMeshPacket()
packet.Type = protocol.Data
packet.SourceUhid = "node-alice"
packet.DestinationUhid = "node-bob"
packet.Payload = []byte("Hello!")

data, err := serializer.Serialize(packet)      // Binary format
recovered, err := serializer.Deserialize(data) // Round-trip
```

### 2. Ed25519 签名与验证

- **密钥格式**：32 字节种子（私钥）、32 字节公钥、64 字节签名
- **标准库**：使用 `crypto/ed25519`（无外部依赖）

**示例：**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. Signal 协议（X3DH + 对称棘轮）

实现端对端加密的 Signal 协议：

- **密钥协商**：使用 `crypto/ecdh` 的 ECDH P-256
- **密钥派生**：使用 `golang.org/x/crypto/hkdf` 的 HKDF-SHA256
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **加密**：AES-256-GCM，12 字节 nonce，16 字节标签
- **棘轮推进**：HMAC-SHA256 链推进
- **乱序处理**：跳过消息密钥（最多 1000 个）

**示例：**
```go
aliceService, _ := security.NewSignalProtocolService()
bobService, _ := security.NewSignalProtocolService()

// Alice generates pre-key bundle
aliceBundle, _ := aliceService.GeneratePreKeyBundle("alice")

// Bob establishes session with Alice
bobService.ProcessPreKeyBundle(aliceBundle)

// Alice establishes session with Bob
bobBundle, _ := bobService.GeneratePreKeyBundle("bob")
aliceService.ProcessPreKeyBundle(bobBundle)

// End-to-end encrypted messaging
plaintext := []byte("Secret message")
encrypted, _ := aliceService.Encrypt("bob", plaintext)
decrypted, _ := bobService.Decrypt("alice", encrypted)
```

### 4. 数据包签名与随机数去重

通过随机数缓存 5 分钟 TTL 防止重放攻击：

```go
signer := security.NewPacketSigningService(300) // 300 seconds TTL
defer signer.Close()

// Compute signable data (SHA256 of payload + header fields)
signableData := signer.ComputeSignableData(
    nonce, timestamp, packetType, sourceUhid, destUhid, payload, ttl, priority)

// Track nonces for deduplication
signer.RecordNonce(sourceUhid, nonce)
isDuplicate := signer.IsNonceSeen(sourceUhid, nonce)
```

### 5. 进程内传输

基于内存的传输，用于测试和本地节点通信：

```go
inProcTransport := transport.NewInProcessTransport()

// Register peers
aliceRx, _ := inProcTransport.RegisterPeer("alice", 10) // buffered channel
bobRx, _ := inProcTransport.RegisterPeer("bob", 10)

// Send and receive
ctx := context.Background()
inProcTransport.SendAsync(ctx, "bob", []byte("Hello!"))
message := <-bobRx

// Properties
fmt.Println(inProcTransport.Name())                // "InProcess"
fmt.Println(inProcTransport.IsAvailable())         // true
fmt.Println(inProcTransport.MaxBandwidthBps())     // 1000000
fmt.Println(inProcTransport.IsConnected("bob"))    // true
```

### 6. 领域模型

网状网络的完整结构：

```go
// Node in the mesh
node := &models.AetherNode{
    UHID: "node-alice-001",
    IdentityKey: publicKey,
    Capabilities: models.CapabilityBLE | models.CapabilityRelay,
    IsLocal: true,
}

// Route to destination
route := &models.RouteEntry{
    DestinationUhid: "node-bob",
    NextHop: "node-bob",
    HopCount: 1,
    ExpiresAt: time.Now().Add(5 * time.Minute),
    QualityScore: 85,
}

// DTN bundle for store-and-forward
bundle := &models.DtnBundle{
    ID: uuid.New().String(),
    SenderUhid: "alice",
    RecipientUhid: "bob",
    Priority: models.DtnPriorityHigh,
    Status: models.DtnStatusPending,
}

// Emergency alert
alert := &models.SosAlert{
    SenderUhid: "alice",
    Message: "Emergency! Need help!",
    Latitude: -33.9249,
    Longitude: 18.4241,
}
```

## 协议常量

协议规范附录 A 中的所有常量：

```go
// Routing
DefaultTtl = 7
SosTtl = 15
RouteTimeoutMs = 5000

// BLE Discovery
BleScanOnMs = 2000
BleScanOffMs = 8000
BleUuidRotationSeconds = 900

// Security
MaxPacketAgeSeconds = 300
MaxSkippedKeys = 1000
AesGcmNonceSize = 12
AesGcmTagSize = 16

// DTN
DtnBundleTtlHours = 72
DtnMaxCopies = 3
DtnMaxBundlesPerNode = 50

// Voice, Streaming, Presence constants...
```

## 运行演示程序

演示程序展示所有主要功能：

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**演示输出：**
```
========================================
Aether Protocol - Go Implementation Demo
========================================

[ DEMO 1: Packet Serialization ]
  Original Packet: [Data] ... src=node-alice-001 dst=node-bob-001
  Payload: Hello, Aether!
  Serialized size: 95 bytes
  Deserialized Packet: [Data] ...
  Payload: Hello, Aether!
  ✓ Round-trip serialization successful!

[ DEMO 2: Ed25519 Signing ]
  Generated Ed25519 Key Pair:
    Private Key (seed): 32 bytes
    Public Key: 32 bytes
  Signed message: Important mesh packet signature
  Signature: 64 bytes
  Signature verification: true
  Verification with tampered data: false (should be false)
  ✓ Ed25519 signing verification successful!

[ DEMO 3: Signal Protocol - Session Establishment ]
  Creating Signal Protocol services for Alice and Bob...
  ✓ Alice generated pre-key bundle
  ✓ Bob established session with Alice
  ✓ Bob generated pre-key bundle
  ✓ Alice established session with Bob
  ✓ Alice encrypted message: Hello Bob, this is Alice!
    Ciphertext: 41 bytes
  ✓ Bob decrypted message: Hello Bob, this is Alice!
  ✓ Bob encrypted message: Hi Alice, I received your message!
  ✓ Alice decrypted message: Hi Alice, I received your message!
  ✓ Signal Protocol end-to-end encryption successful!

[ DEMO 4: In-Process Transport ]
  Transport: InProcess
  Available: true
  Max Bandwidth: 1000000 bps
  Max Range: 100 meters
  ✓ Registered peer: alice
  ✓ Registered peer: bob
  ✓ Alice sent: Hello Bob! (success: true)
  ✓ Bob received: Hello Bob!
  ✓ Bob sent: Hi Alice! (success: true)
  ✓ Alice received: Hi Alice!
  Alice connected to bob: true
  Bob connected to alice: true
  ✓ In-process transport successful!

[ DEMO 5: Packet Signing & Nonce Deduplication ]
  Computed signable data: 152 bytes
  ✓ Recorded nonce for replay prevention
  Nonce seen (should be true): true
  Different nonce seen (should be false): false
  ✓ Nonce deduplication working correctly!

========================================
All demos completed successfully!
========================================
```

## 线格式兼容性

所有序列化均使用**小端字节序编码**以匹配 C# 参考实现：

- **整数**：`encoding/binary.LittleEndian`
- **UUID**：标准 16 字节 UUID 格式
- **字符串**：UTF-8 编码，带 2 字节（uint16）或 4 字节（uint32）长度前缀
- **字节**：长度前缀（2 字节或 4 字节）后跟原始数据

这确保了在 Go 与 C# 实现之间交换数据包时的逐字节兼容性。

## 依赖

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

所有密码学原语使用 Go 标准库（`crypto/*`）以及用于 HKDF 和 ECDH P-256 的 `golang.org/x/crypto`。

## 安全特性

1. **密钥清零**：所有中间密钥通过 `ZeroMemory()` 安全清零
2. **无回退加密**：消息需要已建立的会话；不存在基于 UHID 的回退机制
3. **重放防护**：8 字节 nonce + 时间戳 + 5 分钟去重缓存
4. **计数器间隙**：乱序消息支持最多 MaxSkippedKeys（1000）个跳过的密钥
5. **签名验证**：所有路由回复和预密钥包均通过 Ed25519 验证

## 性能说明

- **数据包序列化**：每个数据包约 1-2µs（使用 100 字节载荷测试）
- **Ed25519 签名**：每次签名约 50µs
- **Signal 协议加密**：每条消息约 100µs
- **随机数去重清理**：后台 goroutine 每 60 秒运行一次

## 测试

演示程序展示：
- 数据包往返序列化
- Ed25519 签名验证
- Signal 协议会话建立
- 端对端加密/解密
- 进程内传输通信
- 随机数去重

所有操作在适当位置使用 `sync.RWMutex` 和 `sync.Map` 保证 goroutine 安全。

## 实现说明

1. **UUID 格式**：使用 `github.com/google/uuid` 确保 RFC 4122 合规
2. **密钥管理**：无外部密钥存储；密钥在演示中保留于内存。生产环境应使用安全存储。
3. **传输接口**：可扩展以支持 BLE、Wi-Fi Direct 及其他物理层
4. **Signal 会话**：在本实现中按对等方持久化，无数据库支持
5. **错误处理**：所有密码学操作均返回错误；调用方必须处理失败情况

## 未来扩展

- [ ] SQLite 路由和会话持久化
- [ ] BLE 传输实现
- [ ] Wi-Fi Direct 传输实现
- [ ] AODV 路由协议实现
- [ ] DTN 流行病路由
- [ ] 存在感知与发现信标服务
- [ ] 语音和流媒体支持
- [ ] 双棘轮算法，提供更高保证的前向保密性

## 许可证

SPDX-License-Identifier: MIT
