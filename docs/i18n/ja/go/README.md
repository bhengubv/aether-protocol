# Aether プロトコル - Go 実装

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](../../ar/go/README.md) · [中文简体](../../zh-CN/go/README.md) · [日本語](README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](../../ru/go/README.md) · [فارسی](../../fa/go/README.md) · [한국어](../../ko/go/README.md)

Aether メッシュネットワーキングプロトコルの完全な Go 実装で、C# リファレンス実装とワイヤー互換性があります。

## 概要

このモジュールは、断続的またはインターネット接続が存在しない環境向けの Aether 分散型メッシュネットワーキングプロトコルを実装しています。提供する機能:

- **パケットシリアライズ**: C# リファレンス実装と互換性のあるバイナリワイヤーフォーマット（リトルエンディアンエンコーディング）
- **Ed25519 署名**: 暗号学的なパケット認証
- **Signal プロトコル**: エンドツーエンド暗号化のための X3DH 鍵合意 + 対称ラチェット
- **パケット署名サービス**: リプレイ防止のための 5 分間 TTL 付きノンス重複排除
- **インプロセストランスポート**: テストとプロセス間通信のためのメモリベーストランスポート
- **モデル**: AetherMeshNode、PeerInfo、RouteEntry、DtnBundle、SosAlert 構造体
- **プロトコル定数**: すべてのルーティング、ディスカバリ、セキュリティ、トランスポート定数

## モジュール構成

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

## 主な機能

### 1. パケットシリアライズ（リトルエンディアン）

すべてのマルチバイト整数にリトルエンディアンエンコーディングを使用して C# とワイヤーフォーマットが完全に一致:

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

**例:**
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

### 2. Ed25519 署名と検証

- **鍵フォーマット**: 32 バイトシード（秘密鍵）、32 バイト公開鍵、64 バイト署名
- **標準ライブラリ**: `crypto/ed25519` を使用（外部依存なし）

**例:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. Signal プロトコル（X3DH + 対称ラチェット）

エンドツーエンド暗号化のための Signal プロトコルを実装:

- **鍵合意**: `crypto/ecdh` を使用した ECDH P-256
- **鍵導出**: `golang.org/x/crypto/hkdf` を使用した HKDF-SHA256
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **暗号化**: 12 バイトノンス、16 バイトタグ付き AES-256-GCM
- **ラチェット**: HMAC-SHA256 チェーン進行
- **順序外処理**: スキップされたメッセージ鍵（最大 1000）

**例:**
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

### 4. パケット署名とノンス重複排除

ノンスキャッシュに 5 分間 TTL を設定してリプレイ攻撃を防止:

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

### 5. インプロセストランスポート

テストとローカルノード通信のためのメモリベーストランスポート:

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

### 6. ドメインモデル

メッシュネットワーキング向けの完全な構造体:

```go
// Node in the mesh
node := &models.AetherMeshNode{
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

## プロトコル定数

プロトコル仕様（付録 A）のすべての定数:

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

## デモの実行

デモプログラムはすべての主要機能を説明します:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**デモ出力:**
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

## ワイヤーフォーマット互換性

すべてのシリアライズは C# リファレンス実装に合わせて**リトルエンディアンエンコーディング**を使用:

- **整数**: `encoding/binary.LittleEndian`
- **UUID**: 標準 16 バイト UUID フォーマット
- **文字列**: 2 バイト（uint16）または 4 バイト（uint32）長プレフィックス付き UTF-8 エンコード
- **バイト列**: 長プレフィックス（2 バイトまたは 4 バイト）に続く生データ

これにより、Go と C# の実装間でパケットを交換する際のバイト単位の互換性が保証されます。

## 依存関係

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

すべての暗号プリミティブは Go の標準ライブラリ（`crypto/*`）と、HKDF および ECDH P-256 向けの `golang.org/x/crypto` を使用しています。

## セキュリティ機能

1. **鍵のゼロ化**: すべての中間鍵は `ZeroMemory()` で安全にゼロ化
2. **フォールバック暗号化なし**: メッセージには確立済みセッションが必要。UHID 派生フォールバックなし
3. **リプレイ防止**: 8 バイトノンス + タイムスタンプ + 5 分間重複排除キャッシュ
4. **カウンターギャップ**: 順序外メッセージは MaxSkippedKeys（1000）まで対応
5. **署名検証**: すべてのルート応答とプリキーバンドルは Ed25519 で検証

## パフォーマンスノート

- **パケットシリアライズ**: パケットあたり約 1〜2µs（100 バイトペイロードでテスト）
- **Ed25519 署名**: 署名あたり約 50µs
- **Signal プロトコル暗号化**: メッセージあたり約 100µs
- **ノンス重複排除クリーンアップ**: バックグラウンドゴルーチンが 60 秒ごとに実行

## テスト

デモプログラムが示す内容:
- ✓ パケットのラウンドトリップシリアライズ
- ✓ Ed25519 署名検証
- ✓ Signal プロトコルセッション確立
- ✓ エンドツーエンド暗号化/復号化
- ✓ インプロセストランスポート通信
- ✓ ノンス重複排除

すべての操作は、適切な箇所で `sync.RWMutex` と `sync.Map` を使用してゴルーチンセーフです。

## 実装ノート

1. **UUID フォーマット**: RFC 4122 準拠のために `github.com/google/uuid` を使用
2. **鍵管理**: 外部鍵ストレージなし。デモではメモリ内に鍵を保持。本番環境ではセキュアストレージを使用すること
3. **トランスポートインターフェース**: BLE、Wi-Fi Direct、その他の物理レイヤーへの拡張が可能
4. **Signal セッション**: この実装ではデータベースバックなしでピアごとに保持
5. **エラーハンドリング**: すべての暗号演算はエラーを返す。呼び出し元は必ず失敗を処理すること

## 今後の拡張予定

- [ ] ルートとセッションの SQLite 永続化
- [ ] BLE トランスポート実装
- [ ] Wi-Fi Direct トランスポート実装
- [ ] AODV ルーティングプロトコル実装
- [ ] DTN エピデミックルーティング
- [ ] プレゼンスとディスカバリービーコンサービス
- [ ] 音声とストリーミングサポート
- [ ] より高い確実性の前方秘匿性のための Double Ratchet アルゴリズム

## ライセンス

SPDX-License-Identifier: MIT
