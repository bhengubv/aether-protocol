# Aether Protocol — Rust 実装

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

C# リファレンス実装とのワイヤー形式互換性を特徴とする、Aether メッシュネットワーキングプロトコルの完全な Rust 実装です。

## 概要

このクレートが提供するもの:

- **MeshPacket のシリアライゼーション/デシリアライゼーション** — C# PacketSerializer と完全に一致するバイナリワイヤー形式
- **Ed25519 署名** — アイデンティティ鍵の生成、署名、および検証
- **Signal Protocol** — 前方秘匿性のための対称ラチェットを伴う X3DH ベースの鍵合意
- **パケット署名サービス** — ノンスの重複排除と鮮度チェック
- **プロセス内トランスポート** — テストおよびデモ用のシミュレートされたメッシュネットワーク

## プロジェクト構造

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

## 主要機能

### 1. ワイヤー形式互換性

`PacketSerializer` は C# 実装とバイト単位で同一の出力を生成します:

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

マルチバイト整数はすべてリトルエンディアンのバイト順を使用します。文字列の長さは、プロトコル仕様に従い u16 (SourceUhid、DestinationUhid) または i32 (Payload、Signature) のプレフィックスが付きます。

### 2. パケットタイプ

プロトコル仕様に定義されたすべての 26 のパケットタイプが定義されています:

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

### 3. Ed25519 署名

- 32 バイトの秘密鍵（シード）、32 バイトの公開鍵、64 バイトの署名
- 暗号化操作に `ed25519-dalek` を使用
- 使用後の安全な鍵のゼロ化

### 4. Signal Protocol

対称ラチェットを伴う X3DH ベースの鍵合意:

- **鍵合意:** エフェメラル鍵と署名済みプリキーを使用した ECDH P-256
- **鍵導出:** 固有の情報文字列を使用した HKDF-SHA256
  - `aether-root-v1` — ルート鍵
  - `aether-chain-send-v1` — 送信チェーン鍵
  - `aether-chain-recv-v1` — 受信チェーン鍵
- **暗号化:** AES-256-GCM（12 バイトのノンス、16 バイトのタグ）
- **ラチェット:** カウンターベースのメッセージ鍵による対称チェーン鍵の前進
- **順不同処理:** 最大 1,000 個のスキップされたメッセージ鍵をキャッシュ

### 5. パケット署名サービス

- ランダムな 8 バイトのノンス生成
- ミリ秒精度のタイムスタンプ
- 鮮度検証（5 分間のウィンドウ）
- 送信者ごとのノンスの重複排除（リプレイを防止）
- 期限切れエントリの自動クリーンアップ

### 6. プロセス内トランスポート

テスト用のシミュレートされたメッシュネットワーク:

- 並行 HashMap を使用したノードの静的レジストリ
- ファイア・アンド・フォーゲット型のメッセージ配信
- 双方向ピア接続チェック
- デモおよびユニットテストに適切

## 使用方法

### 基本的な鍵の生成と署名

```rust
use aethermesh_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Signal Protocol セッション

```rust
use aethermesh_protocol::security::SignalProtocolService;

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

### パケットシリアライゼーション

```rust
use aethermesh_protocol::protocol::{MeshPacket, PacketType};
use aethermesh_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### パケット署名

```rust
use aethermesh_protocol::security::PacketSigningService;
use aethermesh_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### プロセス内トランスポート

```rust
use aethermesh_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## デモの実行

```bash
cargo run --release
```

デモは以下の手順を実行します:

1. Alice と Bob のアイデンティティ鍵を生成
2. Signal Protocol サービスを初期化
3. プリキーバンドルを生成して交換
4. 暗号化セッションを確立
5. 暗号化されたメッセージを交換
6. メッシュパケットを作成して署名
7. パケット署名を検証
8. パケットをシリアライズおよびデシリアライズ
9. プロセス内トランスポートをデモンストレーション

## 定数

すべてのプロトコル定数は `src/constants.rs` で定義されており、C# 仕様に一致します:

- ルーティング: DefaultTtl=7、SosTtl=15、RouteTimeoutMs=5000
- セキュリティ: MaxPacketAgeSeconds=300、MaxSkippedKeys=1000
- トランスポート: BleMaxPayloadBytes=1024、WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72、DtnMaxCopies=3
- 音声/ストリーム: さまざまなビットレートおよびバッファの設定

## 依存関係

- `ed25519-dalek` — Ed25519 署名
- `x25519-dalek` — X25519 鍵合意
- `aes-gcm` — AES-256-GCM 暗号化
- `hkdf` — HKDF 鍵導出
- `sha2` — SHA-256 ハッシュ
- `hmac` — HMAC 操作
- `rand` — 乱数生成
- `uuid` — GUID の生成とシリアライゼーション
- `serde` + `serde_json` — シリアライゼーション
- `tokio` — 非同期ランタイム
- `async-trait` — 非同期トレイトメソッド

## テスト

すべてのテストを実行します:

```bash
cargo test
```

テストの対象:

- パケットの作成と TTL 管理
- パケットタイプの変換
- シリアライゼーション/デシリアライゼーションのラウンドトリップ
- Ed25519 鍵の生成と署名の検証
- Signal Protocol セッションの確立と暗号化
- パケット署名と鮮度検証
- プロセス内トランスポートの接続性

## プロトコル準拠

この実装は Aether プロトコル仕様（バージョン 2.0）に従い、以下をサポートします:

- ✅ バイナリワイヤー形式（リトルエンディアン、長さプレフィックス）
- ✅ 全 26 パケットタイプ
- ✅ ノンスの重複排除を伴う Ed25519 署名
- ✅ HKDF-SHA256 を使用した X3DH 鍵合意
- ✅ 12 バイトのノンスを使用した AES-256-GCM 暗号化
- ✅ 順不同処理を伴う対称ラチェット
- ✅ プリキーバンドルの生成と処理
- ✅ パケット署名対象データの構築（SHA-256 ペイロードハッシュ）
- ✅ トランスポートトレイトの抽象化

## 注意事項

- ワイヤー形式は全体を通してリトルエンディアンのバイト順を使用します（C# の BinaryPrimitives.WriteInt32LittleEndian に一致）
- 文字列の長さプレフィックスは UHID に u16 を使用し、ペイロード/署名には i32 を使用します（C# の WriteUInt16/WriteInt32 に一致）
- すべての暗号化鍵マテリアルは使用後に `CryptographicOperations` 相当でゼロ化されます
- Signal Protocol 実装は、チェーンラチェット処理にソルトバイト [0x01] および [0x02] を使用した HKDF を使用します（C# の HKDF 使用に一致）
- ノンスの重複排除は、5 分以上経過したエントリを自動的にクリーンアップする送信者ごとの VecDeque を使用します
