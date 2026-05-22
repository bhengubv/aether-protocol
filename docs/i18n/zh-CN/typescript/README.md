# Aether 网状协议 - TypeScript 实现

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Aether 网状网络协议的完整 TypeScript/Node.js 实现，与 C# 参考实现的线格式完全兼容。

## 特性

- **MeshPacket 序列化**：与 C# 完全一致的二进制线格式（小端字节序整数，带长度前缀的字符串/数组）
- **Ed25519 签名**：使用 TweetNaCl 进行签名生成与验证
- **Signal 协议**：X3DH 密钥交换，带 HKDF-SHA256 密钥派生和 AES-256-GCM 加密
- **数据包签名**：按照协议规范（第 2.3 节）构造完整的可签名数据
- **进程内传输**：用于测试和演示的模拟网络
- **对称棘轮**：基于 HMAC-SHA256 的链密钥推进，支持乱序消息
- **协议常量**：来自 PROTOCOL_SPEC 附录 A 的 60+ 个常量

## 安装

```bash
npm install
```

## 使用方法

### 构建

```bash
npm run build
```

### 运行演示

```bash
npm run dev
```

演示内容：
1. 在进程内模拟网络中创建 2 个节点
2. 生成 Ed25519 密钥对
3. 建立 Signal 协议会话
4. 创建、签名并验证数据包
5. 序列化与反序列化数据包
6. 加密与解密消息
7. 通过传输层发送数据包

### API 示例

#### 数据包创建与签名

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Signal 协议加密

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### 数据包序列化

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### 进程内传输

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## 协议符合性

### 线格式

所有多字节整数均为**小端字节序**：
- 数据包 ID：16 字节 UUID
- TTL、TimestampMs：int32/int64 小端字节序
- 字符串长度：uint16 小端字节序（非 uint32）
- 有效载荷长度：int32 小端字节序

### 数据包签名（第 2.3 节）

可签名数据格式：
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Signal 协议（第 4 节）

- **密钥交换**：带 ECDH P-256 的 X3DH
- **HKDF**：SHA256，盐值为 "AetherSignal"
- **信息字符串**："aether-root-v1"、"aether-chain-send-v1"、"aether-chain-recv-v1"
- **加密**：AES-256-GCM，12 字节随机数，16 字节认证标签
- **链棘轮**：带计数器推进的 HMAC-SHA256

## 数据包类型

定义了全部 23 种数据包类型：
- RouteRequest (1) - AODV 路由请求
- RouteReply (2) - AODV 路由应答
- Data (3) - 应用数据
- Ack (4) - 投递确认
- SosBroadcast (5) - 紧急广播
- ... 及另外 18 种（见协议规范）

## 安全特性

- **Ed25519 签名**：所有数据包按 v2 协议签名
- **AES-256-GCM**：每条消息使用唯一随机数的独立密钥
- **重放防护**：8 字节随机随机数 + 时间戳验证
- **前向保密性**：对称棘轮推进链密钥
- **乱序解密**：跳过消息密钥缓存（最多 1000 个）

## 项目结构

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## 测试

演示程序（`npm run dev`）覆盖所有主要特性：
- 数据包创建与序列化（往返）
- Ed25519 密钥生成与签名验证
- Signal 协议会话建立
- 消息加密与解密
- 进程内传输投递

如需单元测试，可使用 Jest 或类似测试框架进行扩展。

## 兼容性说明

- **C# 线格式**：与 C# PacketSerializer 100% 兼容
- **带签名的数据包**：带 Ed25519 签名的协议版本 2
- **HKDF 派生**：使用 @noble/hashes（纯 JavaScript 实现）
- **ECDH**：Node.js 内置 crypto 模块（P-256 曲线）

## 依赖项

- **tweetnacl**：通过 TweetNaCl 实现 Ed25519 签名
- **@noble/hashes**：HKDF-SHA256 密钥派生
- **uuid**：UUID 生成与解析
- **node crypto**：AES-256-GCM、HMAC-SHA256、ECDH

## 许可证

MIT - 详见 LICENSE 文件

## 参考资料

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# 实现](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
