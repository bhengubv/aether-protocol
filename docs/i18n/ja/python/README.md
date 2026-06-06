# Aether メッシュネットワーキングプロトコル - Python 実装

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

C# リファレンス実装とワイヤー互換の暗号化操作を提供する、Aether メッシュネットワーキングプロトコルの Python 実装です。

## 概要

Aether は、断続的またはインターネット接続が存在しない環境向けに設計された、分散型メッシュネットワーキングプロトコルです。この Python パッケージは以下を提供します:

- **Ed25519 署名**: PyNaCl を使用した鍵の生成、署名、および検証
- **Signal Protocol X3DH**: ECDH P-256 による非同期鍵交換
- **AES-256-GCM 暗号化**: 12 バイトのノンスによるメッセージごとの対称暗号化
- **HKDF-SHA256 鍵導出**: コンテキスト固有の情報文字列を使用した RFC 5869 準拠の鍵導出
- **対称ラチェット**: 前方秘匿性を持つ HMAC-SHA256 ベースのメッセージ鍵導出
- **パケットシリアライゼーション**: C# 実装に一致するリトルエンディアンのバイナリワイヤー形式
- **リプレイアタック防止**: 5 分間の TTL を持つノンスベースの重複排除
- **プロセス内トランスポート**: メッシュ通信のテスト用モックトランスポート

## インストール

### PyPI から（公開後）
```bash
pip install aether-protocol
```

### ソースから
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### 開発用依存関係
```bash
pip install -e ".[dev]"
```

## クイックスタート

```python
import asyncio
from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.signal_protocol import SignalProtocolService
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## アーキテクチャ

### パッケージ構造

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherMeshNode, PeerInfo, RouteEntry)
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

## 主要機能

### 1. Ed25519 署名サービス

暗号化操作に PyNaCl (libsodium) を使用します:

```python
from aethermesh.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**鍵のサイズ:**
- 秘密鍵: 32 バイト（Ed25519 シード）
- 公開鍵: 32 バイト（Ed25519 点）
- 署名: 64 バイト

### 2. Signal Protocol

前方秘匿性のための対称ラチェットを伴う X3DH 鍵交換を実装します:

```python
from aethermesh.security.signal_protocol import SignalProtocolService

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

**鍵導出:**
- ソルト `"AetherMeshSignal"` を使用した HKDF-SHA256
- ルート鍵情報: `"aether-root-v1"`
- 送信チェーン情報: `"aether-chain-send-v1"`
- 受信チェーン情報: `"aether-chain-recv-v1"`

**対称ラチェット:**
- チェーン鍵に HMAC-SHA256 を使用
- 各メッセージで新しいメッセージ鍵を導出し、チェーンを前進させる
- 順不同配信のためにスキップされた鍵を最大 1000 個サポート
- メッセージごとの暗号化: ランダムな 12 バイトのノンスを使用した AES-256-GCM

### 3. パケットシリアライゼーション

C# 実装に一致するワイヤー互換のバイナリ形式:

```python
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

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

**ワイヤー形式（リトルエンディアン）:**
- プロトコルバージョン: 1 バイト
- パケットタイプ: 1 バイト
- パケット ID: 16 バイト（UUID）
- 優先度: 1 バイト
- TTL: 4 バイト（int32）
- TimestampMs: 8 バイト（int64）
- SourceUhid の長さ: 2 バイト + UTF-8 データ
- DestinationUhid の長さ: 2 バイト + UTF-8 データ
- PacketNonce の長さ: 2 バイト + データ
- ペイロードの長さ: 4 バイト + データ
- 署名の長さ: 2 バイト + データ

### 4. パケット署名

Ed25519 を使用してパケットに署名し、リプレイアタックを検出します:

```python
from aethermesh.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**署名対象データ:**
プロトコル仕様セクション 2.3 に従い、署名は以下を対象とします:
- PacketNonce（8 バイト）
- TimestampMs（8 バイト、リトルエンディアン int64）
- Type（4 バイト、リトルエンディアン int32）
- SourceUhid（長さ + UTF-8）
- DestinationUhid（長さ + UTF-8）
- SHA-256(Payload)（32 バイト）
- Ttl（4 バイト、リトルエンディアン int32）
- Priority（4 バイト、リトルエンディアン int32）

**リプレイ防止:**
- 確認済みの (sender_uhid, nonce) ペアのキャッシュを維持
- キャッシュエントリごとに 5 分間の TTL
- 60 秒ごとに自動クリーンアップ

### 5. トランスポートサービス

物理トランスポート（BLE、Wi-Fi Direct など）向けの抽象基底クラス:

```python
from aethermesh.transport.in_process import InProcessTransport

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

**InProcessTransport の機能:**
- クラスレベルのノードのグローバルレジストリ
- threading.Lock によるスレッドセーフ
- テストおよびローカルメッシュシミュレーションに最適
- プロパティ: name、is_available、max_bandwidth_bps、max_range_meters、power_cost_relative、max_concurrent_peers

## 定数リファレンス

すべてのプロトコル定数は `aether/constants.py` で定義されています:

### 暗号化
- `ED25519_PRIVATE_KEY_SIZE`: 32 バイト
- `ED25519_PUBLIC_KEY_SIZE`: 32 バイト
- `ED25519_SIGNATURE_SIZE`: 64 バイト
- `AES_GCM_NONCE_SIZE`: 12 バイト
- `AES_GCM_TAG_SIZE`: 16 バイト
- `MAX_SKIPPED_KEYS`: 1000

### ルーティング
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN ストアアンドフォワード
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

（完全なリストは `constants.py` を参照）

## デモの実行

カラフルな出力ですべての主要機能をデモンストレーションします:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

デモの内容:
1. Ed25519 鍵の生成と署名
2. AetherMeshNode を使用したノードの作成
3. Signal Protocol X3DH 鍵交換
4. メッセージの暗号化と復号化
5. パケットのシリアライゼーション/デシリアライゼーション
6. パケット署名とリプレイアタック検出
7. プロセス内トランスポート通信
8. エンドツーエンドの完全な暗号化ワークフロー

## 依存関係

### ランタイム
- `pynacl>=1.5.0` - libsodium による Ed25519 署名
- `cryptography>=41.0.0` - ECDH P-256、HKDF-SHA256、AES-256-GCM、HMAC-SHA256

### 開発用
- `pytest>=7.4.0` - テストフレームワーク
- `pytest-asyncio>=0.21.0` - 非同期テストのサポート
- `black>=23.0.0` - コードフォーマット
- `mypy>=1.5.0` - 静的型チェック
- `ruff>=0.1.0` - リンティング

## 互換性

**Python バージョン:** 3.10+

**プラットフォーム:** クロスプラットフォーム（Windows、macOS、Linux）

**暗号化バックエンド:** システムの libsodium および cryptography ライブラリのバックエンドを使用し、プラットフォーム間で一貫した動作を保証します。

## プロトコルリファレンス

- **AODV ルーティング:** RFC 3561
- **X3DH 鍵合意:** Signal Foundation、2016 年 11 月
- **Double Ratchet:** Signal Foundation、2016 年 11 月
- **HKDF:** RFC 5869（HMAC ベースの抽出・展開）
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB ら、2012 年

## セキュリティに関する考慮事項

### 鍵のゼロ化
中間的な暗号化材料は使用後にゼロ化されます:
- ECDH からの共有シークレット
- 対称ラチェットからのメッセージ鍵
- 確立コンテキスト内の導出された鍵マテリアル

Python では、真のインプレースメモリゼロ化は制限されていますが、機密データは使用直後に変数スコープからクリアされます。

### 脅威モデル
Aether は以下を想定しています:
- BLE/Wi-Fi への受動的な盗聴
- アクティブなパケットインジェクションとリプレイ
- 偽のノード作成によるシビル攻撃
- 選択的なサービス妨害

以下の保護が含まれます:
- **機密性:** メッセージごとのキーを使用した AES-256-GCM
- **整合性:** Ed25519 パケット署名
- **リプレイ防止:** ノンスベースの重複排除
- **前方秘匿性:** メッセージごとのキーを使用した対称ラチェット
- **ルート認証:** 署名付きルートリプライ

### 制限事項
- 順不同のメッセージ配信は最大 1000 メッセージまでサポート
- ギャップを超えたメッセージは拒否されます
- BLE アドレスは 15 分ごとにローテーション（Python では未実装）
- P-256 から Ed25519 への移行ウィンドウは 30 日間（フォールバックはまだ未実装）

## テスト

テストスイートを実行します:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## ライセンス

MIT ライセンス - 詳細は LICENSE ファイルを参照してください

## コントリビューション

改善に貢献するには:

1. コードが PEP 8 スタイルに従っていることを確認する（フォーマットには `black` を使用）
2. すべての関数に型ヒントを追加する
3. パブリック API にドックストリングを含める
4. 型チェックのために `mypy` を実行する
5. 新機能のテストを追加する

## リファレンス

- Aether Protocol Spec: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# Reference Implementation: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
