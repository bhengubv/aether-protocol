# Aether 网状网络协议 - Python 实现

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Aether 网状网络协议的 Python 实现，与 C# 参考实现完全兼容，提供一致的密码学操作。

## 概述

Aether 是一种去中心化网状网络协议，专为网络连接断断续续或完全缺失的环境而设计。本 Python 包提供：

- **Ed25519 签名**：使用 PyNaCl 进行密钥生成、签名与验证
- **Signal 协议 X3DH**：基于 ECDH P-256 的异步密钥交换
- **AES-256-GCM 加密**：每条消息使用 12 字节随机数进行对称加密
- **HKDF-SHA256 密钥派生**：符合 RFC 5869 标准、使用上下文特定信息字符串的密钥派生
- **对称棘轮**：基于 HMAC-SHA256 的消息密钥派生，具备前向保密性
- **数据包序列化**：与 C# 实现兼容的小端字节序二进制线格式
- **重放攻击防护**：基于随机数的去重，生存时间为 5 分钟
- **进程内传输**：用于测试网状通信的模拟传输

## 安装

### 从 PyPI 安装（发布后可用）
```bash
pip install aether-protocol
```

### 从源码安装
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### 安装开发依赖
```bash
pip install -e ".[dev]"
```

## 快速入门

```python
import asyncio
from aethernet.security.ed25519_service import Ed25519SigningService
from aethernet.security.signal_protocol import SignalProtocolService
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## 架构

### 包结构

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNetNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## 主要特性

### 1. Ed25519 签名服务

使用 PyNaCl（libsodium）进行密码学操作：

```python
from aethernet.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**密钥长度：**
- 私钥：32 字节（Ed25519 种子）
- 公钥：32 字节（Ed25519 点）
- 签名：64 字节

### 2. Signal 协议

实现了带对称棘轮的 X3DH 密钥交换，以提供前向保密性：

```python
from aethernet.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**密钥派生：**
- 使用 HKDF-SHA256，盐值为 `"AetherNetSignal"`
- 根密钥信息字符串：`"aether-root-v1"`
- 发送链信息字符串：`"aether-chain-send-v1"`
- 接收链信息字符串：`"aether-chain-recv-v1"`

**对称棘轮：**
- 使用链密钥进行 HMAC-SHA256 运算
- 每条消息派生新的消息密钥并推进链
- 支持最多 1000 个跳过密钥以处理乱序投递
- 每条消息加密：AES-256-GCM，使用随机 12 字节随机数

### 3. 数据包序列化

与 C# 实现兼容的二进制线格式：

```python
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**线格式（小端字节序）：**
- 协议版本：1 字节
- 数据包类型：1 字节
- 数据包 ID：16 字节（UUID）
- 优先级：1 字节
- TTL：4 字节（int32）
- TimestampMs：8 字节（int64）
- SourceUhid 长度：2 字节 + UTF-8 数据
- DestinationUhid 长度：2 字节 + UTF-8 数据
- PacketNonce 长度：2 字节 + 数据
- Payload 长度：4 字节 + 数据
- Signature 长度：2 字节 + 数据

### 4. 数据包签名

使用 Ed25519 对数据包进行签名并检测重放攻击：

```python
from aethernet.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**可签名数据：**
根据协议规范第 2.3 节，签名覆盖以下内容：
- PacketNonce（8 字节）
- TimestampMs（8 字节，小端字节序 int64）
- Type（4 字节，小端字节序 int32）
- SourceUhid（长度 + UTF-8）
- DestinationUhid（长度 + UTF-8）
- SHA-256(Payload)（32 字节）
- Ttl（4 字节，小端字节序 int32）
- Priority（4 字节，小端字节序 int32）

**重放防护：**
- 维护已见 (sender_uhid, nonce) 对的缓存
- 每个缓存条目生存时间为 5 分钟
- 每 60 秒自动清理一次

### 5. 传输服务

物理传输层（BLE、Wi-Fi Direct 等）的抽象基类：

```python
from aethernet.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**InProcessTransport 特性：**
- 类级全局节点注册表
- 使用 threading.Lock 实现线程安全
- 非常适合测试和本地网状模拟
- 属性：name、is_available、max_bandwidth_bps、max_range_meters、power_cost_relative、max_concurrent_peers

## 常量参考

所有协议常量定义于 `aether/constants.py`：

### 密码学
- `ED25519_PRIVATE_KEY_SIZE`：32 字节
- `ED25519_PUBLIC_KEY_SIZE`：32 字节
- `ED25519_SIGNATURE_SIZE`：64 字节
- `AES_GCM_NONCE_SIZE`：12 字节
- `AES_GCM_TAG_SIZE`：16 字节
- `MAX_SKIPPED_KEYS`：1000

### 路由
- `DEFAULT_TTL`：7
- `SOS_TTL`：15
- `ROUTE_TIMEOUT_MS`：5000
- `ROUTE_EXPIRY_SECONDS`：300

### DTN 存储转发
- `DTN_BUNDLE_TTL_HOURS`：72
- `DTN_MAX_COPIES`：3
- `DTN_MAX_BUNDLES_PER_NODE`：50
- `DTN_SCAN_INTERVAL_SECONDS`：60

（完整列表见 `constants.py`）

## 运行演示

演示所有主要特性，并以彩色输出呈现：

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

演示内容：
1. Ed25519 密钥生成与签名
2. 使用 AetherNetNode 创建节点
3. Signal 协议 X3DH 密钥交换
4. 消息加密与解密
5. 数据包序列化/反序列化
6. 数据包签名与重放攻击检测
7. 进程内传输通信
8. 完整的端到端加密工作流

## 依赖项

### 运行时
- `pynacl>=1.5.0` - 通过 libsodium 实现 Ed25519 签名
- `cryptography>=41.0.0` - ECDH P-256、HKDF-SHA256、AES-256-GCM、HMAC-SHA256

### 开发
- `pytest>=7.4.0` - 测试框架
- `pytest-asyncio>=0.21.0` - 异步测试支持
- `black>=23.0.0` - 代码格式化
- `mypy>=1.5.0` - 静态类型检查
- `ruff>=0.1.0` - 代码检查

## 兼容性

**Python 版本：** 3.10+

**平台：** 跨平台（Windows、macOS、Linux）

**密码学后端：** 使用系统 libsodium 和 cryptography 库后端，确保跨平台行为一致。

## 协议参考

- **AODV 路由：** RFC 3561
- **X3DH 密钥协商：** Signal Foundation，2016 年 11 月
- **双棘轮：** Signal Foundation，2016 年 11 月
- **HKDF：** RFC 5869（基于 HMAC 的提取-扩展）
- **AES-GCM：** NIST SP 800-38D
- **Ed25519：** DJB 等人，2012 年

## 安全注意事项

### 密钥清零
使用后立即清零中间密码学材料：
- ECDH 共享密钥
- 对称棘轮中的消息密钥
- 建立上下文中派生的密钥材料

在 Python 中，真正的内存原地清零能力有限，但敏感数据在使用后会立即从变量作用域中清除。

### 威胁模型
Aether 假设存在以下威胁：
- 对 BLE/Wi-Fi 的被动窃听
- 主动数据包注入和重放
- 通过伪造节点创建的女巫攻击
- 选择性拒绝服务

防护措施包括：
- **机密性：** 每条消息使用 AES-256-GCM 密钥
- **完整性：** Ed25519 数据包签名
- **重放防护：** 基于随机数的去重
- **前向保密性：** 带每消息密钥的对称棘轮
- **路由认证：** 签名的路由应答

### 限制
- 支持最多 1000 条消息的乱序投递
- 超出该范围的消息将被拒绝
- BLE 地址每 15 分钟轮换一次（Python 中未实现）
- P-256 到 Ed25519 的迁移窗口为 30 天（回退机制尚未实现）

## 测试

运行测试套件：

```bash
pytest -v
pytest --asyncio-mode=auto
```

## 许可证

MIT 许可证 - 详见 LICENSE 文件

## 贡献

如需贡献改进：

1. 确保代码遵循 PEP 8 风格（使用 `black` 格式化）
2. 为所有函数添加类型注解
3. 为公共 API 包含文档字符串
4. 运行 `mypy` 进行类型检查
5. 为新特性添加测试

## 参考资料

- Aether 协议规范：`/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# 参考实现：`/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.：https://thegeeknetwork.dev
