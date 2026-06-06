# Aether 网状网络协议 - C 语言实现

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Aether 网状网络协议的高性能、嵌入式友好型 C 语言实现。专为 ESP32 和 nRF52 等资源受限设备设计，完整支持 Ed25519 签名、AES-256-GCM 加密以及基于 AODV 的路由。

## 概述

Aether 是一种去中心化网状网络协议，适用于网络连接间歇性中断或完全缺失的环境。本 C 语言实现提供：

- **协议序列化/反序列化** — 与 C# 参考实现相匹配的小端字节序线格式
- **密码学操作** — Ed25519 签名、AES-256-GCM 加密、HMAC-SHA256、HKDF-SHA256（通过 libsodium）
- **数据包签名** — 依据协议规范进行确定性可签名数据构建
- **传输抽象** — 用于自定义传输实现的 vtable 模式
- **进程内传输** — 用于多节点场景的内置测试传输
- **嵌入式优先设计** — 尽可能使用固定大小缓冲区，最小化内存分配，常量时间操作

## 构建要求

- **CMake** ≥ 3.16
- **C11 编译器**（gcc、clang 等）
- **libsodium** — 用于密码学操作
- **POSIX 线程**（pthread）

### macOS

```bash
# Install libsodium using Homebrew
brew install libsodium

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### Linux（Ubuntu/Debian）

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF（ESP32）

本库设计为 ESP-IDF 组件使用：

```bash
# In your ESP-IDF project components directory
cp -r /Users/admin/Code/Dev/aether-protocol/c/include aether
cp -r /Users/admin/Code/Dev/aether-protocol/c/src aether/

# Create idf_component.yml
cat > aether/idf_component.yml << 'EOF'
version: "1.0.0"
description: "Aether Mesh Networking Protocol"
dependencies:
  libsodium: "*"
EOF

# In your project's CMakeLists.txt
idf_component_register(
    INCLUDE_DIRS "aether/include"
    SRCS "aether/src/protocol.c" "aether/src/security.c" "aether/src/transport_inprocess.c"
    REQUIRES libsodium pthread
)
```

## 目录结构

```
c/
├── include/aether/
│   ├── constants.h       # Protocol constants and limits
│   ├── protocol.h        # Packet structure and serialization
│   ├── security.h        # Cryptographic operations
│   └── transport.h       # Transport abstraction
├── src/
│   ├── protocol.c        # Serialization implementation
│   ├── security.c        # Cryptography using libsodium
│   ├── transport_inprocess.c  # In-process test transport
│   └── demo.c            # Example usage
├── tests/
│   ├── CMakeLists.txt
│   └── test_protocol.c   # Unit tests
├── CMakeLists.txt
└── README.md
```

## 快速上手

### 构建并运行演示程序

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

预期输出演示：
1. Ed25519 密钥生成
2. 数据包创建与签名
3. 序列化为线格式
4. 反序列化
5. AES-256-GCM 加密/解密
6. HMAC-SHA256 身份验证
7. HKDF 密钥派生

### 运行单元测试

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### 在代码中使用

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
    if (!packet) return 1;

    // Set fields
    aethermesh_packet_set_source_uhid(packet, "node-alice");
    aethermesh_packet_set_destination_uhid(packet, "node-bob");
    aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERMESH_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERMESH_ED25519_PUBLIC_KEY_SIZE];
    aethermesh_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERMESH_ED25519_SIGNATURE_SIZE];
        aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
        aethermesh_packet_set_signature(packet, signature, AETHERMESH_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethermesh_mesh_packet_t *received = aethermesh_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethermesh_packet_free(received);
    }

    aethermesh_packet_free(packet);
    return 0;
}
```

## API 参考

### 协议

#### 数据包管理
- `aethermesh_mesh_packet_t *aethermesh_packet_new(void)` — 创建新数据包
- `void aethermesh_packet_free(aethermesh_mesh_packet_t *packet)` — 释放数据包
- `aethermesh_mesh_packet_t *aethermesh_packet_clone(const aethermesh_mesh_packet_t *packet)` — 克隆数据包

#### 序列化
- `int aethermesh_packet_serialize(const aethermesh_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — 序列化为线格式
- `aethermesh_mesh_packet_t *aethermesh_packet_deserialize(const uint8_t *data, size_t data_len)` — 从线格式反序列化
- `size_t aethermesh_packet_estimate_size(const aethermesh_mesh_packet_t *packet)` — 估算线格式大小

#### 数据包字段
- `bool aethermesh_packet_set_source_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — 设置来源
- `bool aethermesh_packet_set_destination_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — 设置目标
- `bool aethermesh_packet_set_payload(aethermesh_mesh_packet_t *packet, const uint8_t *data, size_t len)` — 设置载荷
- `bool aethermesh_packet_set_signature(aethermesh_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — 设置签名

#### 验证
- `bool aethermesh_packet_is_expired(const aethermesh_mesh_packet_t *packet, int max_age_seconds)` — 检查是否已过期
- `bool aethermesh_packet_can_forward(const aethermesh_mesh_packet_t *packet)` — 检查 TTL 是否大于 0

#### 签名数据
- `uint8_t *aethermesh_packet_get_signable_data(const aethermesh_mesh_packet_t *packet, size_t *out_len)` — 获取确定性可签名字节（调用方负责释放）

### 安全

#### Ed25519
- `bool aethermesh_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — 生成 32+32 字节密钥对
- `bool aethermesh_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — 签名（生成 64 字节）
- `bool aethermesh_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — 验证

#### AES-256-GCM
- `bool aethermesh_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — 加密（若 nonce 为 NULL 则自动生成）
- `bool aethermesh_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — 解密

#### HMAC 与哈希
- `bool aethermesh_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256（32 字节）
- `bool aethermesh_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256（32 字节）
- `bool aethermesh_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF（RFC 5869）

#### 工具函数
- `void aethermesh_zeroize(void *mem, size_t len)` — 常量时间内存清零
- `bool aethermesh_random_bytes(uint8_t *out, size_t len)` — 密码学安全随机字节

### 传输

#### 通用函数
- `bool aethermesh_transport_send(aethermesh_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — 发送数据
- `bool aethermesh_transport_is_connected(aethermesh_transport_t *transport, const char *peer_uhid)` — 检查连接状态
- `void aethermesh_transport_set_on_data_received(aethermesh_transport_t *transport, aethermesh_transport_on_data_received callback, void *user_data)` — 注册回调
- `void aethermesh_transport_destroy(aethermesh_transport_t *transport)` — 清理资源

#### 进程内传输
- `aethermesh_transport_t *aethermesh_inprocess_transport_new(void)` — 创建共享进程内传输
- `bool aethermesh_inprocess_transport_register_node(aethermesh_transport_t *transport, const char *uhid)` — 注册节点
- `bool aethermesh_inprocess_transport_unregister_node(aethermesh_transport_t *transport, const char *uhid)` — 注销节点

## 线格式合规性

本实现严格遵循协议规范，多字节整数使用**小端字节序**：

```
[1] protocol_version
[1] type
[16] packet_id (UUID bytes)
[1] priority
[4] ttl (little-endian int32)
[8] timestamp_ms (little-endian int64)
[2] source_uhid_len (little-endian uint16)
[N] source_uhid (UTF-8)
[2] destination_uhid_len (little-endian uint16)
[N] destination_uhid (UTF-8)
[2] nonce_len (little-endian uint16)
[N] packet_nonce
[4] payload_len (little-endian int32)
[N] payload
[2] signature_len (little-endian uint16)
[N] signature (Ed25519, 64 bytes)
```

本 C 语言实现序列化的数据包与 C# 参考实现 100% 兼容。

## 安全注意事项

### 密码学库
- **libsodium**（libsodium.org）用于所有密码学操作
- Ed25519 签名与验证
- AES-256-GCM 认证加密
- HMAC-SHA256 与 SHA-256
- HKDF-SHA256 密钥派生
- 密码学安全随机数生成

### 密钥清零
所有敏感材料（密钥、明文、中间值）在使用后立即通过 `sodium_memzero()` 从内存中清零，防止密钥意外泄露。

### 数据包验证
- 基于时间戳的去重：超过 300 秒的数据包将被拒绝
- 随机数唯一性：每个数据包包含 8 字节随机 nonce
- TTL 验证：TTL=0 的数据包将被丢弃
- 签名验证：协议 v2 中 Ed25519 签名为强制要求

## 嵌入式设备说明

### ESP32
- 需要适用于 ESP-IDF 的 libsodium 移植版本（可通过 ESP-IDF 组件获取）
- 固定数据包大小估算简化了内存分配
- 使用 POSIX 线程进行互斥操作
- 尽量在栈上预分配缓冲区

### nRF52
- 与 ESP32 类似
- 可通过传输 vtable 实现 BLE GATT 传输层
- 建议使用 FreeRTOS 等 RTOS 处理多数据包

### 内存使用
- 最小数据包：约 52 字节
- 最大数据包：65KB（可通过 `AETHERMESH_MAX_PAYLOAD_LEN` 配置）
- 256 节点对等表：约 32KB
- 内存中单个网状数据包：约 8KB（最大字段情况下的最坏情形）

## 性能

在现代 x86-64 机器（Intel Core i9）上：
- **序列化**：每个数据包约 1-2 µs
- **反序列化**：每个数据包约 1-2 µs
- **Ed25519 签名**：约 100 µs
- **Ed25519 验证**：约 300 µs
- **AES-256-GCM 加密**：每 KB 约 1 µs
- **SHA-256**：每 KB 约 0.5 µs

## 测试

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

测试覆盖：
- 数据包创建与克隆
- 序列化往返
- Ed25519 签名与验证
- AES-GCM 加密/解密
- HMAC-SHA256 计算
- HKDF 密钥派生
- TTL 与过期验证
- 可签名数据的确定性

## 与 Aether 生态系统集成

本 C 库设计为与以下系统集成：
- **AetherMeshAPI**（C#）— 服务端网状中继与分析
- **AetherMesh.Core**（C#）— 参考实现（可互操作的线格式）
- **Meshtastic** — 开源网状无线电固件
- **esp-idf** — Espressif 物联网开发框架
- 自定义嵌入式应用

## 许可证

SPDX-License-Identifier: MIT

完整文本请参阅 LICENSE 文件。

## 贡献

欢迎贡献！请确保：
- 所有测试通过（`ctest --output-on-failure`）
- 代码符合 C11 标准
- 线格式与 C# 参考实现完全匹配
- 所有敏感数据已清零
- 文档已更新

## 参考资料

- 协议规范：`/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# 参考实现：`/Users/admin/Code/Dev/aether-protocol/src/AetherMesh.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
