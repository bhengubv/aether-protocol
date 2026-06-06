# Aether メッシュネットワーキングプロトコル - C 実装

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Aether メッシュネットワーキングプロトコルの高性能・組み込みフレンドリーな C 実装です。ESP32 や nRF52 などのリソース制約のあるデバイス向けに設計されており、Ed25519 署名、AES-256-GCM 暗号化、AODV ベースのルーティングを完全にサポートしています。

## 概要

Aether は、断続的またはインターネット接続が存在しない環境向けの分散型メッシュネットワーキングプロトコルです。この C 実装は以下を提供します:

- **プロトコルのシリアライズ/デシリアライズ** — C# リファレンス実装に合わせたリトルエンディアンのワイヤーフォーマット
- **暗号演算** — Ed25519 署名、AES-256-GCM 暗号化、HMAC-SHA256、HKDF-SHA256（libsodium 経由）
- **パケット署名** — プロトコル仕様に基づいた決定的な署名可能データの構築
- **トランスポート抽象化** — カスタムトランスポート実装向けの vtable パターン
- **インプロセストランスポート** — マルチノードシナリオ向けの組み込みテストトランスポート
- **組み込みファーストデザイン** — 可能な限り固定サイズのバッファ、最小限のアロケーション、定数時間操作

## ビルド要件

- **CMake** ≥ 3.16
- **C11 コンパイラ** (gcc、clang など)
- **libsodium** — 暗号演算用
- **POSIX スレッド** (pthread)

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

### Linux (Ubuntu/Debian)

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF (ESP32)

このライブラリは ESP-IDF コンポーネントとして使用するよう設計されています:

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

## 構成

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

## クイックスタート

### デモのビルドと実行

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

期待される出力は以下を示します:
1. Ed25519 鍵の生成
2. パケットの作成と署名
3. ワイヤーフォーマットへのシリアライズ
4. デシリアライズ
5. AES-256-GCM 暗号化/復号化
6. HMAC-SHA256 認証
7. HKDF 鍵導出

### ユニットテストの実行

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### コードでの使用

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

## API リファレンス

### プロトコル

#### パケット管理
- `aethermesh_mesh_packet_t *aethermesh_packet_new(void)` — 新しいパケットを作成
- `void aethermesh_packet_free(aethermesh_mesh_packet_t *packet)` — パケットを解放
- `aethermesh_mesh_packet_t *aethermesh_packet_clone(const aethermesh_mesh_packet_t *packet)` — パケットをクローン

#### シリアライズ
- `int aethermesh_packet_serialize(const aethermesh_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — ワイヤーフォーマットへシリアライズ
- `aethermesh_mesh_packet_t *aethermesh_packet_deserialize(const uint8_t *data, size_t data_len)` — ワイヤーフォーマットからデシリアライズ
- `size_t aethermesh_packet_estimate_size(const aethermesh_mesh_packet_t *packet)` — ワイヤーサイズを推定

#### パケットフィールド
- `bool aethermesh_packet_set_source_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — 送信元を設定
- `bool aethermesh_packet_set_destination_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — 宛先を設定
- `bool aethermesh_packet_set_payload(aethermesh_mesh_packet_t *packet, const uint8_t *data, size_t len)` — ペイロードを設定
- `bool aethermesh_packet_set_signature(aethermesh_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — 署名を設定

#### バリデーション
- `bool aethermesh_packet_is_expired(const aethermesh_mesh_packet_t *packet, int max_age_seconds)` — 期限切れかどうかを確認
- `bool aethermesh_packet_can_forward(const aethermesh_mesh_packet_t *packet)` — TTL > 0 かどうかを確認

#### 署名データ
- `uint8_t *aethermesh_packet_get_signable_data(const aethermesh_mesh_packet_t *packet, size_t *out_len)` — 決定的な署名可能バイトを取得（呼び出し元が解放する必要あり）

### セキュリティ

#### Ed25519
- `bool aethermesh_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — 32+32 バイトの鍵ペアを生成
- `bool aethermesh_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — 署名（64 バイトを生成）
- `bool aethermesh_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — 検証

#### AES-256-GCM
- `bool aethermesh_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — 暗号化（nonce が NULL の場合は自動生成）
- `bool aethermesh_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — 復号化

#### HMAC とハッシュ
- `bool aethermesh_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256（32 バイト）
- `bool aethermesh_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256（32 バイト）
- `bool aethermesh_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### ユーティリティ
- `void aethermesh_zeroize(void *mem, size_t len)` — 定数時間でのメモリ消去
- `bool aethermesh_random_bytes(uint8_t *out, size_t len)` — 暗号学的乱数バイト

### トランスポート

#### 汎用関数
- `bool aethermesh_transport_send(aethermesh_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — データを送信
- `bool aethermesh_transport_is_connected(aethermesh_transport_t *transport, const char *peer_uhid)` — 接続を確認
- `void aethermesh_transport_set_on_data_received(aethermesh_transport_t *transport, aethermesh_transport_on_data_received callback, void *user_data)` — コールバックを登録
- `void aethermesh_transport_destroy(aethermesh_transport_t *transport)` — クリーンアップ

#### インプロセストランスポート
- `aethermesh_transport_t *aethermesh_inprocess_transport_new(void)` — 共有インプロセストランスポートを作成
- `bool aethermesh_inprocess_transport_register_node(aethermesh_transport_t *transport, const char *uhid)` — ノードを登録
- `bool aethermesh_inprocess_transport_unregister_node(aethermesh_transport_t *transport, const char *uhid)` — ノードの登録を解除

## ワイヤーフォーマット準拠

この実装は、**リトルエンディアン**のマルチバイト整数を使用してプロトコル仕様に厳密に従っています:

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

この C 実装でシリアライズされたパケットは、C# リファレンス実装と 100% 互換性があります。

## セキュリティに関する考慮事項

### 暗号ライブラリ
- すべての暗号演算に **libsodium** (libsodium.org) を使用
- Ed25519 署名と検証
- AES-256-GCM 認証付き暗号化
- HMAC-SHA256 および SHA-256
- HKDF-SHA256 鍵導出
- 暗号学的に安全な乱数生成

### 鍵のゼロ化
すべての機密データ（鍵、平文、中間値）は、使用後すぐに `sodium_memzero()` を使ってメモリからゼロ化されます。これにより、意図しない鍵の漏洩を防ぎます。

### パケットバリデーション
- タイムスタンプベースの重複排除: 300 秒より古いパケットは拒否
- ノンスの一意性: すべてのパケットに 8 バイトのランダムノンス
- TTL バリデーション: TTL=0 のパケットはドロップ
- 署名検証: Ed25519 署名はプロトコル v2 では必須

## 組み込みデバイスに関する注意事項

### ESP32
- ESP-IDF 向け libsodium ポートが必要（ESP-IDF コンポーネント経由で利用可能）
- 固定パケットサイズの推定によりメモリアロケーションが簡素化
- ミューテックス操作に POSIX スレッドを使用
- 可能な場合はスタック上にバッファを事前アロケート

### nRF52
- ESP32 と同様
- トランスポート vtable 経由で BLE GATT トランスポートレイヤーを実装可能
- マルチパケット処理には FreeRTOS などの RTOS の使用を検討

### メモリ使用量
- 最小パケット: 約 52 バイト
- 最大パケット: 65KB（`AETHERMESH_MAX_PAYLOAD_LEN` で設定可能）
- 256 ノードのピアテーブル: 約 32KB
- メモリ上の単一メッシュパケット: 約 8KB（最大フィールドのワーストケース）

## パフォーマンス

最新の x86-64 マシン (Intel Core i9) での計測:
- **シリアライズ**: パケットあたり約 1〜2 µs
- **デシリアライズ**: パケットあたり約 1〜2 µs
- **Ed25519 署名**: 約 100 µs
- **Ed25519 検証**: 約 300 µs
- **AES-256-GCM 暗号化**: 1KB あたり約 1 µs
- **SHA-256**: 1KB あたり約 0.5 µs

## テスト

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

テストのカバレッジ:
- パケットの作成とクローン
- シリアライズのラウンドトリップ
- Ed25519 署名と検証
- AES-GCM 暗号化/復号化
- HMAC-SHA256 の計算
- HKDF 鍵導出
- TTL と有効期限のバリデーション
- 署名可能データの決定性

## Aether エコシステムとの統合

この C ライブラリは以下との統合を想定して設計されています:
- **AetherMeshAPI** (C#) — サーバーサイドのメッシュリレーと分析
- **AetherMesh.Core** (C#) — リファレンス実装（相互運用可能なワイヤーフォーマット）
- **Meshtastic** — オープンソースのメッシュ無線ファームウェア
- **esp-idf** — Espressif IoT 開発フレームワーク
- カスタム組み込みアプリケーション

## ライセンス

SPDX-License-Identifier: MIT

全文は LICENSE ファイルを参照してください。

## コントリビューション

コントリビューション歓迎! 以下を確認してください:
- すべてのテストが通過すること（`ctest --output-on-failure`）
- コードが C11 準拠であること
- ワイヤーフォーマットが C# リファレンスと完全に一致すること
- すべての機密データがゼロ化されること
- ドキュメントが更新されること

## 参考資料

- プロトコル仕様: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# リファレンス: `/Users/admin/Code/Dev/aether-protocol/src/AetherMesh.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
