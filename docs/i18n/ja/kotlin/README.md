# Aether プロトコル - Kotlin 実装

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Aether メッシュネットワーキングプロトコルの完全なプロダクション対応 Kotlin 実装で、C# リファレンス実装との完全なクロス言語ワイヤーフォーマット互換性を備えています。

## 概要

Aether は、断続的またはインターネット接続が存在しない環境向けの分散型メッシュネットワーキングプロトコルです。この Kotlin 実装は以下を提供します:

- C# との**ワイヤーフォーマット互換性**（バイナリパケットシリアライズが完全に一致）
- パケット認証と完全性のための **Ed25519 署名**
- エンドツーエンド暗号化のための **Signal プロトコル**（X3DH 鍵合意、対称ラチェット、AES-256-GCM）
- セッション確立のための **ECDH P-256** 鍵合意
- リトルエンディアンマルチバイト整数を使用した**パケットシリアライズ/デシリアライズ**
- ノンス重複排除を使用した**リプレイ防止**
- BLE、Wi-Fi Direct、インプロセスメッセージングのための**トランスポート抽象化**

## プロジェクト構成

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherNetNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## ビルド方法

### 前提条件

- JDK 17 以上
- Gradle 8.0 以上

### コンパイル

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### デモの実行

```bash
./gradlew run
```

デモが示す内容:
1. Ed25519 鍵ペアの生成
2. プリキーバンドルの作成と交換
3. Signal プロトコルセッションの確立
4. Ed25519 によるパケット署名
5. パケットのシリアライズ/デシリアライズ
6. メッセージの暗号化と復号化
7. リプレイ防止
8. インプロセストランスポートメッセージング

## 主要コンポーネント

### 1. パケットシリアライズ (`PacketSerializer`)

ワイヤーフォーマット（リトルエンディアン）:
- プロトコルバージョン（1 バイト）
- パケットタイプ（1 バイト）
- パケット ID / UUID（16 バイト）
- プライオリティ（1 バイト）
- TTL（4 バイト、int32）
- TimestampMs（8 バイト、int64）
- SourceUhid（2 バイト長プレフィックス + UTF-8 バイト）
- DestinationUhid（2 バイト長プレフィックス + UTF-8 バイト）
- PacketNonce（2 バイト長プレフィックス + バイト）
- ペイロード（4 バイト長プレフィックス + バイト）
- 署名（2 バイト長プレフィックス + バイト）

C# の `PacketSerializer` と完全に互換性があります。

### 2. Ed25519 署名 (`Ed25519Service`、`PacketSigning`)

- **鍵生成**: 32 バイトの秘密鍵シード、32 バイトの公開鍵
- **署名**: 決定的な署名可能データに対する 64 バイト署名
- **検証**: 移行期間中に P-256 ECDSA を置き換え
- **署名可能データフォーマット**: C# 仕様と完全に一致（パケットノンス、タイムスタンプ、タイプ、UHID、ペイロードハッシュ、TTL、プライオリティ）
- **リプレイ防止**: 5 分間 TTL 付きのノンス重複排除

### 3. Signal プロトコル (`SignalProtocol`)

対称ラチェットを備えた X3DH 鍵合意を実装:

**セッション確立:**
- ピアのプリキーバンドルを取得
- Ed25519 でバンドル署名を検証
- X3DH を実行: DH(ローカルアイデンティティ、リモート署名済みプリキー) + DH(ローカルアイデンティティ、リモートプリキー)
- HKDF-SHA256 を使用してルートキーとチェーンキーを導出

**暗号化/復号化:**
- HMAC-SHA256 による対称ラチェット
- 12 バイトランダムノンス付き AES-256-GCM
- 前方秘匿性を持つメッセージごとの鍵
- 順序外メッセージ処理（スキップ鍵キャッシュ、最大 1000 鍵）

**パラメータ:**
- ルートキー導出情報: `"aether-root-v1"`
- 送信チェーン導出情報: `"aether-chain-send-v1"`
- 受信チェーン導出情報: `"aether-chain-recv-v1"`
- メッセージキーソルト: `0x01`、チェーンキーソルト: `0x02`

### 4. トランスポート抽象化 (`TransportService`)

物理トランスポート（BLE、Wi-Fi Direct など）向けインターフェース:

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport:** テスト/デモ用にグローバルな `ConcurrentHashMap` を使用したリファレンス実装。

### 5. ドメインモデル (`Models.kt`)

- **AetherNetNode**: UHID、公開鍵、ケイパビリティ、ジオハッシュを持つノードアイデンティティ
- **PeerInfo**: 信頼性スコアと最終確認タイムスタンプを持つ既知ピア
- **RouteEntry**: ホップカウントとクオリティスコアを持つルーティングテーブルエントリ
- **NodeCapabilities**: ビットフィールド（BLE、Wi-Fi Direct、ゲートウェイ、リレー、SOS、ストリーミング、音声、DTN）
- **DtnBundle**: 有効期限とコピー数を持つストア＆フォワードバンドル

## プロトコル定数

主要定数（`Constants.kt` より）:

| カテゴリ | 定数 | 値 |
|----------|------|-----|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## パケットタイプ

すべての 23 パケットタイプは C# の列挙値と一致（1〜23）:

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## 依存関係

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519、ECDH P-256、AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — 鍵フォーマットサポート
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — 非同期/await、Flow
- **org.slf4j:slf4j-api:2.0.9** — ロギング
- **kotlin-stdlib** — Kotlin 標準ライブラリ

## 使用例

### 鍵生成

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### パケット署名

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### パケットシリアライズ

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Signal プロトコル暗号化

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## クロス言語互換性

この実装は C# リファレンス実装との**完全なワイヤーフォーマット互換性**を維持しています:

- バイナリパケットフォーマット: 同一のリトルエンディアンレイアウト
- パケットタイプ列挙: C# の列挙値と完全に一致（1〜23）
- Ed25519 署名: NSec/libsodium と互換性あり
- ECDH P-256: 標準曲線、言語間で互換性あり
- HKDF-SHA256: RFC 5869 標準実装
- AES-256-GCM: 12 バイトノンス、16 バイトタグの NIST 標準

Kotlin でシリアライズされたパケットは C# でデシリアライズ可能であり、その逆も同様です。

## テスト

この実装には、以下を実行する包括的なデモ（`Demo.kt`）が含まれています:

1. 鍵生成と公開鍵のエクスポート
2. プリキーバンドルの生成と交換
3. Signal プロトコルによるセッション確立
4. パケットの作成、署名、シリアライズ
5. パケットのデシリアライズと署名検証
6. メッセージの暗号化と復号化
7. リプレイ攻撃防止
8. インプロセストランスポートメッセージング

実行方法:
```bash
./gradlew run
```

## セキュリティに関する考慮事項

- **鍵のゼロ化**: すべての中間暗号マテリアルは使用後に `CryptographicOperations.ZeroMemory`（Kotlin 相当: `fill(0)`）でゼロ化
- **リプレイ防止**: 5 分間 TTL 付きのノンス重複排除でリプレイ攻撃を防止
- **前方秘匿性**: チェーンラチェットから導出されたメッセージごとの鍵
- **順序外処理**: 最大 1000 鍵のスキップ鍵キャッシュでメモリ枯渇を防止
- **RREP 認証**: ルート応答パケットは宛先ノードが署名
- **パケット機密性**: メッセージコンテンツは AES-256-GCM で暗号化

## 今後の拡張予定

この実装は以下のフックを提供しています:

- **BLE トランスポート**（`TransportService` インターフェース）
- **Wi-Fi Direct トランスポート**（同一インターフェース）
- **DTN エピデミックルーティング**（`DtnBundle` モデル準備済み）
- **SOS ブロードキャスト**（パケットタイプ定義済み）
- **プレゼンスビーコン**（パケットタイプ定義済み）
- **音声とストリーミング**（パケットタイプ定義済み）
- **Double Ratchet**（常時接続トランスポートが利用可能な場合）

## プロトコルドキュメント

完全なプロトコル仕様: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## ライセンス

SPDX-License-Identifier: MIT
