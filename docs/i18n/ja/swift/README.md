# Aether Protocol - Swift 実装

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

iOS および macOS 向けにエンドツーエンド暗号化、ルーティング、ピアツーピア通信を提供する、Aether メッシュネットワーキングプロトコルの包括的な Swift 実装です。

## 概要

Aether は、断続的またはインターネット接続が存在しない環境向けに設計された、分散型メッシュネットワーキングプロトコルです。この Swift 実装は以下を提供します:

- C# リファレンス実装との**ワイヤー互換シリアライゼーション**
- パケット認証のための **Ed25519 署名**
- エンドツーエンド暗号化のための **Signal Protocol**（X3DH + 対称ラチェット）
- 複数の物理レイヤー（BLE、Wi-Fi Direct、NearLink）をサポートする**トランスポート抽象化**
- Swift Concurrency を使用した**スレッドセーフな非同期 API**

## 要件

- Swift 5.9+
- macOS 13.0+ または iOS 16.0+
- Xcode 15+

## 依存関係

- [swift-crypto](https://github.com/apple/swift-crypto) - 暗号化プリミティブ（Ed25519、P-256 ECDH、AES-GCM、HKDF、SHA-256）

## アーキテクチャ

### コアコンポーネント

#### プロトコルレイヤー
- **MeshPacket**: コアパケット構造（UUID、タイプ、ソース/デスティネーション UHID、TTL、優先度、ペイロード、署名）
- **PacketType**: 26 のパケットタイプの列挙（RouteRequest、Data、SosBroadcast、DtnBundle など）
- **PacketSerializer**: リトルエンディアンのワイヤー形式を使用したバイナリシリアライザー/デシリアライザー

#### セキュリティレイヤー
- **Ed25519Service**: Curve25519 を使用した鍵の生成、署名、および検証
- **SignalProtocolService**: 暗号化セッション用の X3DH 鍵合意 + 対称ラチェット
- **PacketSigningService**: ノンスの重複排除とリプレイ防止を伴うパケットレベルの署名

#### トランスポートレイヤー
- **TransportService**: トランスポートコントラクトを定義するプロトコル
- **InProcessTransport**: テストおよびローカル通信用のメモリ内トランスポート

#### モデル
- **AetherNode**: UHID とアイデンティティ鍵を持つノード表現
- **PreKeyBundle**: 非同期セッション確立用のバンドル
- **EncryptedPayload**: 暗号化されたメッセージラッパー
- **DtnBundle**: 遅延耐性ネットワーキングバンドル
- **PeerInfo**: ルーティングテーブルのピア情報

### 定数
すべてのプロトコル定数（TTL、タイムアウト、容量制限）は `ProtocolConstants` で定義されています。

## インストール

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

Package.swift で:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherProtocol", package: "aether-protocol-swift")
    ]
)
```

## クイックスタート

### 1. パケットシリアライゼーション

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

### 2. Ed25519 署名

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Signal Protocol セッション

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

### 4. パケット署名

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. プロセス内トランスポート（テスト）

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

## ワイヤー形式

すべてのパケットはリトルエンディアンのワイヤー形式に準拠します:

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

空の UHID とペイロードを持つ最小パケットサイズ: **43 バイト**。

## セキュリティモデル

### 暗号化
- **アルゴリズム**: AES-256-GCM
- **鍵導出**: X3DH 共有シークレットからの HKDF-SHA256
- **セッションラチェット**: メッセージごとにチェーン鍵を前進させる対称ラチェット

### 署名
- **アルゴリズム**: Ed25519（Curve25519）
- **ペイロード保護**: 署名対象データに含まれる SHA256 ハッシュ
- **リプレイ防止**: 8 バイトのノンス + ミリ秒タイムスタンプ + 重複排除キャッシュ

### 鍵交換
- **プロトコル**: ECDH P-256 を使用した X3DH バリアント
- **プリキーバインディング**: Ed25519 で検証された署名済みプリキー
- **非同期**: 受信者がオンラインでなくてもセッションを確立

### 制限
- **MaxSkippedKeys**: 1,000（セッションごとの順不同メッセージ）
- **MaxPacketAge**: 300 秒（5 分）

## プロトコル定数

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5,000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 バイト
- **AesGcmTagSize**: 16 バイト

完全なリストは `ProtocolConstants` を参照してください。

## スレッドセーフ

すべてのサービスはスレッドセーフな並行アクセスのために `actor` 分離されています:

- `SignalProtocolService` - セッション管理と暗号化
- `PacketSigningService` - パケットの署名と検証
- `InProcessTransport` - メッセージ配信

Swift Concurrency での使用例:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## テスト

付属のデモを実行します:

```bash
cd swift
swift run aether-demo
```

期待される出力:

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

## 相互運用性

ワイヤー形式は以下と互換性があります:
- **Aether.Core**（C#）- リファレンス実装
- **aether-protocol-go** - Go 実装
- **aether-protocol-rust** - Rust 実装

すべての実装が使用するもの:
- リトルエンディアン整数
- UTF-8 文字列エンコーディング
- Ed25519 署名（64 バイト）
- AES-256-GCM 暗号化（12 バイトのノンス、16 バイトのタグ）

## パフォーマンス

Apple Silicon（M1 Pro）でのベンチマーク:

| 操作 | 時間 |
|-----------|------|
| パケットシリアライゼーション | ~0.5 μs |
| パケットデシリアライゼーション | ~0.7 μs |
| Ed25519 署名 | ~3.5 ms |
| Ed25519 検証 | ~4.2 ms |
| AES-256-GCM 暗号化 | ~0.8 μs |
| AES-256-GCM 復号化 | ~0.9 μs |
| X3DH 鍵合意 | ~8.5 ms |
| 対称ラチェット | ~0.3 μs |

## 今後の作業

- **BLE トランスポート**: Bluetooth Low Energy 実装
- **Wi-Fi Direct トランスポート**: 直接ピアツーピア Wi-Fi
- **Double Ratchet**: メッセージラチェット処理による完全な前方秘匿性
- **AODV ルーティング**: ルート探索とメンテナンス
- **DTN サービス**: ストアアンドフォワードバンドル配信
- **プレゼンスとプロキシミティ**: ロケーション対応のピア探索
- **音声とストリーミング**: リアルタイムメディアプロトコル

## ライセンス

MIT - LICENSE ファイルを参照してください

## リファレンス

1. [Aether Protocol Specification](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Double Ratchet Algorithm](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Ed25519 Signatures](https://en.wikipedia.org/wiki/Curve25519)
6. [AES-GCM Mode](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## コントリビューション

これはリファレンス実装です。バグ報告および機能リクエストについては、GitHub で Issue を開いてください。
