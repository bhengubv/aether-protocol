# Aether メッシュプロトコル - TypeScript 実装

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

C# リファレンス実装と完全にワイヤー形式互換の、Aether メッシュネットワーキングプロトコルの完全な TypeScript/Node.js 実装です。

## 機能

- **MeshPacket シリアライゼーション**: C# と完全に一致するバイナリワイヤー形式（リトルエンディアン整数、長さプレフィックスの文字列/配列）
- **Ed25519 署名**: 署名の生成と検証に TweetNaCl を使用
- **Signal Protocol**: HKDF-SHA256 鍵導出と AES-256-GCM 暗号化を使用した X3DH 鍵交換
- **パケット署名**: プロトコル仕様（セクション 2.3）に従った完全な署名対象データの構築
- **プロセス内トランスポート**: テストとデモ用のシミュレートされたネットワーク
- **対称ラチェット**: 順不同メッセージのサポートを伴う HMAC-SHA256 チェーン鍵の前進
- **プロトコル定数**: PROTOCOL_SPEC セクション A からの 60 以上すべての定数

## インストール

```bash
npm install
```

## 使用方法

### ビルド

```bash
npm run build
```

### デモの実行

```bash
npm run dev
```

デモの内容:
1. プロセス内シミュレートネットワークに 2 つのノードを作成
2. Ed25519 鍵ペアを生成
3. Signal Protocol セッションを確立
4. パケットを作成、署名、および検証
5. パケットをシリアライズおよびデシリアライズ
6. メッセージを暗号化および復号化
7. トランスポートレイヤーを通じてパケットを送信

### API サンプル

#### パケットの作成と署名

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

#### Signal Protocol 暗号化

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

#### パケットシリアライゼーション

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### プロセス内トランスポート

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

## プロトコル準拠

### ワイヤー形式

マルチバイト整数はすべて**リトルエンディアン**です:
- パケット ID: 16 バイト UUID
- TTL、TimestampMs: int32/int64 LE
- 文字列の長さ: uint16 LE（uint32 ではない）
- ペイロードの長さ: int32 LE

### パケット署名（セクション 2.3）

署名対象データの形式:
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

### Signal Protocol（セクション 4）

- **鍵交換**: ECDH P-256 を使用した X3DH
- **HKDF**: ソルト="AetherNetSignal" を使用した SHA256
- **情報文字列**: "aether-root-v1"、"aether-chain-send-v1"、"aether-chain-recv-v1"
- **暗号化**: 12 バイトのノンス、16 バイトのタグを使用した AES-256-GCM
- **チェーンラチェット**: カウンター前進を伴う HMAC-SHA256

## パケットタイプ

23 のパケットタイプすべてが定義されています:
- RouteRequest (1) - AODV ルートリクエスト
- RouteReply (2) - AODV ルートリプライ
- Data (3) - アプリケーションデータ
- Ack (4) - 配信確認応答
- SosBroadcast (5) - 緊急ブロードキャスト
- ... その他 18 件（プロトコル仕様を参照）

## セキュリティ機能

- **Ed25519 署名**: v2 プロトコルに従いすべてのパケットに署名
- **AES-256-GCM**: 固有のノンスを使用したメッセージごとのキー
- **リプレイ防止**: 8 バイトのランダムノンス + タイムスタンプ検証
- **前方秘匿性**: チェーン鍵を前進させる対称ラチェット
- **順不同復号化**: スキップされたメッセージキーのキャッシュ（最大 1000）

## プロジェクト構造

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

## テスト

デモ（`npm run dev`）はすべての主要機能を実行します:
- パケットの作成とシリアライゼーション（ラウンドトリップ）
- Ed25519 鍵の生成と署名の検証
- Signal Protocol セッションの確立
- メッセージの暗号化と復号化
- プロセス内トランスポートの配信

ユニットテストには Jest または同様のテストランナーを使用して拡張してください。

## 互換性に関する注意事項

- **C# ワイヤー形式**: C# PacketSerializer と 100% 互換
- **署名付きパケット**: Ed25519 署名を使用したプロトコルバージョン 2
- **HKDF 導出**: @noble/hashes を使用（純粋な JavaScript 実装）
- **ECDH**: Node.js 組み込みの crypto モジュール（P-256 曲線）

## 依存関係

- **tweetnacl**: TweetNaCl による Ed25519 署名
- **@noble/hashes**: HKDF-SHA256 鍵導出
- **uuid**: UUID の生成と解析
- **node crypto**: AES-256-GCM、HMAC-SHA256、ECDH

## ライセンス

MIT - LICENSE ファイルを参照してください

## リファレンス

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# Implementation](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
