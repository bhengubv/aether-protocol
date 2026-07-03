# Aether Mesh Networking Protocol Specification

**Version:** 2.0
**Status:** Reconciled with HEAD (2026-05-05)
**Date:** 2026-03-15 (初版); 2026-05-05 (§2、§4、§10、§11 調整済み、§3/§9 検証済み)
**Authors:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **読者へのご注意。** 本ドキュメントの旧版は、8言語対応のワイヤーフォーマット統一および
> ファミリー全体の X25519 + Signal Double Ratchet への移行以前に作成されたものです。
> 2026-05-05 時点において、§2（パケットフォーマット）、§3（ルーティング）、§4（鍵交換）、
> §9（DTN）は実装済みプロトコルを記述しています。§10（ビデオストリーミング）および
> §11（Watch Together）はターゲットプロトコルを記述しており、ワイヤー定義済みかつ
> フィクスチャテスト済みですが、コーデック / BitTorrent / ChipIn パイプラインはまだ
> スキャフォールディングに結合されていません。本ドキュメントと実装が乖離している箇所では、
> C# リファレンスが正式なものとして扱われます。
>
> - 正規ワイヤーバイト列: `fixtures/expected/*.bin`（10の名前付きケース）
> - リファレンスシリアライザ: `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - リファレンスシグナルスタック: `src/AetherNet.Security/Services/SignalProtocolService.cs`
> - リファレンスルーティング: `src/AetherNet.Core/Routing/RoutingService.cs`
> - リファレンス DTN: `src/AetherNet.Core/Dtn/DtnService.cs`
> - クロス言語ワイヤー相互運用証明: `fixtures/README.md`
> - クロス言語シグナル相互運用証明: `fixtures/signal/README.md`

---

## 目次

1. [概要](#1-概要)
2. [パケットフォーマット](#2-パケットフォーマット)
3. [ルーティングアルゴリズム](#3-ルーティングアルゴリズム)
4. [鍵交換](#4-鍵交換)
5. [トランスポートレイヤー要件](#5-トランスポートレイヤー要件)
6. [ディスカバリプロトコル](#6-ディスカバリプロトコル)
7. [セキュリティモデル](#7-セキュリティモデル)
8. [SOS ブロードキャスト](#8-sos-ブロードキャスト)
9. [DTN ストアアンドフォワード](#9-dtn-ストアアンドフォワード)
10. [ビデオストリーミング](#10-ビデオストリーミング)
11. [Watch Together](#11-watch-together)
12. [セキュリティ・プライバシーレイヤー](#12-security--privacy-layer)

---

## 1. 概要

Aether は、インターネット接続が断続的または存在しない環境向けに設計された分散型メッシュネットワーキングプロトコルです。異種の短距離トランスポート（Bluetooth Low Energy、Wi-Fi Direct、NearLink）を経由したマルチホップパケットルーティング、X3DH 派生の鍵共有と対称ラチェットを用いたエンドツーエンド暗号化、遅延耐性のあるストアアンドフォワード配信、そして緊急 SOS フラッドメカニズムを提供します。このプロトコルはトランスポート非依存です。ピア間でバイト配列を送受信できる物理レイヤーであれば、いずれも有効な Aether トランスポートとなります。ノードはユニバーサルハードウェア識別子（UHID）によって識別され、Ed25519 アイデンティティキーによって認証されます。Aether はユニバーサルネットワークレイヤーとして機能することを意図しており、エコシステム内のすべてのアプリケーションが Aether サービスを登録し、インターネット接続のないノードは、メッシュトラフィックをインターネットにブリッジするゲートウェイピアを通じて広域ネットワークに到達します。

---

## 2. パケットフォーマット

> 2026-05-05 に `src/AetherNet.Core/Protocol/PacketSerializer.cs` および
> `fixtures/expected/` 配下の 10 フィクスチャケースと照合済み。

### 2.1. MeshPacket ワイヤーレイアウト

すべての Aether メッセージは `MeshPacket` にカプセル化されます。フィールドはワイヤー上で**正確に**この順序で現れます:

| Off | Field            | Type                            | Size       | Notes |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = 未署名（レガシー）、`2` = 署名済み（現行） |
| 1   | Type             | uint8                           | 1          | パケットタイプ列挙（§2.4 参照） |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | 重複排除用パケット識別子。.NET のデフォルトである混合エンディアン Guid ではなく、**ビッグエンディアン**バイト順。 |
| 18  | Priority         | uint8                           | 1          | 優先度レベル（0 = 通常、255 = SOS）。**ワイヤーフィールドは 1 バイト。255 を超える値はクランプする。** |
| 19  | Ttl              | int32, little-endian            | 4          | 各ホップで減算される生存時間。1バイト uint8 ではなく **4バイト int32** — 最大約 2³¹-1 の値が有効。 |
| 23  | TimestampMs      | int64, little-endian            | 8          | Unix エポックミリ秒（UTC）。 |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | `SourceUhid` の UTF-8 バイト長。最大 65535。 |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | 送信者の UHID。空も許可されるが通常は使用しない。 |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | `DestinationUhid` の UTF-8 バイト長。 |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | 受信者の UHID。ブロードキャストの場合は空文字列。 |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | `PacketNonce` のバイト長。標準値: 8。 |
| ... | PacketNonce      | bytes                           | P          | リプレイ防止用の暗号学的乱数ノンス。 |
| ... | Payload Len      | int32, little-endian            | 4          | `Payload` のバイト長。負値はエラー。 |
| ... | Payload          | bytes                           | Q          | アプリケーションデータ。解釈は `Type` に依存する。 |
| ... | Signature Len    | uint16, little-endian           | 2          | `Signature` のバイト長。0（未署名）または 64（Ed25519）。 |
| ... | Signature        | bytes                           | R          | 署名対象データに対する Ed25519 署名（§2.3 参照）。 |

**長さプレフィックスの幅**はフィールドによって異なります。`SourceUhid`、`DestinationUhid`、
`PacketNonce`、`Signature` は **2バイト（uint16）** 長さプレフィックスを使用します。
`Payload` はペイロードが 64 KiB を超える場合があるため、**4バイト（int32）** 長さプレフィックスを使用します。

### 2.2. 最小パケットサイズ

すべての可変長フィールドが空（ゼロ長 UHID、ゼロ長ノンス、ゼロ長ペイロード、ゼロ長署名）の場合、ワイヤーサイズは:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

旧版の仕様に記載されていた 50バイト / 52バイトという数値は誤りでした。

### 2.3. ワイヤーフォーマット図

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

具体的な例については `fixtures/expected/basic_data.bin`（83バイト、
正規入力は `fixtures/inputs.json`）を参照してください。実装はフルフィクスチャコーパスに
対して検証されます — いずれかの乖離があるとクロス言語フィクスチャ検証テストが失敗します。

### 2.4. 署名対象データの構築

署名（ワイヤー上の `Signature` フィールド）は、ワイヤーバイト列自体ではなく、別個の
正規バイト列に対して計算されます。これにより、ワイヤーレイアウトが署名を破ることなく
進化できるようになり、中間ノードが平文ペイロードを見ることなく完全性を検証できます
（署名されるのはその SHA-256 ハッシュのみです）。

署名対象バイト列は以下の連結です:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> §2.1 のワイヤーレイアウトとの意図的な相違点に注意してください: 署名対象データでは
> `Type`、`Length`、`Ttl`、`Priority` に **4バイト int32** を使用しますが、
> ワイヤーではそれぞれ 1バイト / 2バイト / 4バイト / 1バイトを使用します。
> これは意図的な設計です — 署名対象形式は言語をまたいで移植可能であり固定幅フィールドを使用します。
> ワイヤー形式は BLE PDU の節約のためにコンパクトになっています。
> 実装では `Priority` を署名対象バイトにエンコードする前に `[0,255]` にクランプする必要があります。
> そうしないと受信者（ワイヤーバイト 0..255 を参照する）が異なる署名対象バッファを導出し、
> 検証が失敗します。

リファレンス実装は `src/AetherNet.Security/Services/
PacketSigningService.cs::BuildSignableData` にあり、移植作業において必読です。

### 2.5. パケットタイプ

| Value | Name              | Direction     | Description |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV ルートリクエスト |
| 2     | RouteReply        | Unicast       | AODV ルートリプライ（宛先ノードによる署名が必須） |
| 3     | Data              | Unicast       | アプリケーションデータ |
| 4     | Ack               | Unicast       | 配信確認応答 |
| 5     | SosBroadcast      | Flood         | 緊急ブロードキャスト（セクション 8 参照） |
| 6     | SosAck            | Unicast       | SOS 確認応答 |
| 7     | ChannelMessage    | Multicast     | グループチャンネルメッセージ |
| 8     | ChunkRequest      | Unicast       | P2P コンテンツチャンクリクエスト |
| 9     | ChunkData         | Unicast       | P2P コンテンツチャンクレスポンス |
| 10    | Heartbeat         | Broadcast     | 定期的な生存確認シグナル |
| 11    | StreamAnnounce    | Broadcast     | ライブストリームのアドバタイズ |
| 12    | StreamSegment     | Unicast/Tree  | ライブストリームのメディアセグメント |
| 13    | StreamSubscribe   | Unicast       | ストリームリレーツリーへの参加リクエスト |
| 14    | StreamUnsubscribe | Unicast       | ストリームリレーツリーからの離脱 |
| 15    | VoicePtt          | Unicast       | プッシュトゥトーク音声フレーム |
| 16    | VoiceCall         | Unicast       | リアルタイム音声通話フレーム |
| 17    | VoiceSignaling    | Unicast       | 音声通話のセットアップ/ティアダウン |
| 18    | DtnBundle         | Unicast       | DTN ストアアンドフォワードバンドル（セクション 9 参照） |
| 19    | DtnCustodyAck     | Unicast       | DTN カストディ転送確認応答 |
| 20    | DtnDeliveryReceipt| Unicast       | DTN エンドツーエンド配信確認 |
| 21    | PresenceBeacon    | Broadcast     | プレゼンスおよび可用性アナウンス |
| 22    | PresenceQuery     | Unicast       | プレゼンスステータスリクエスト |
| 23    | ProfileSync       | Unicast       | プロファイルメタデータの同期 |
| 24    | TipPacket         | Unicast       | ノードチップ（LedgerAPI 経由で決済） |
| 25    | PreKeyRequest     | Unicast       | ピアのプレキーバンドルリクエスト |
| 26    | PreKeyResponse    | Unicast       | プレキーバンドルの配信 |
| 27    | VideoCall         | Unicast       | 暗号化ビデオフレーム（H.264/H.265/VP8 NAL ユニット） |
| 28    | VideoSignaling    | Unicast       | ビデオ通話セットアップ: オファー、アンサー、リジェクト、バイ、コーデックネゴシエーション |
| 29    | WatchSync         | Unicast       | 同期再生コマンド: プレイ、ポーズ、シーク、スピード |
| 30    | WatchReaction     | Multicast     | Watch Together 中のタイムスタンプ付き絵文字または音声リアクション |
| 31    | VideoFrame        | Unicast/SFU   | グループビデオフレーム（SFU リレーが参加者に配信） |
| 32    | ScreenShare       | Unicast       | 画面共有フレーム（ビデオと同じパイプライン、別フラグ） |
| 33    | WatchChunkRequest | Unicast       | 再生位置に偏重した優先チャンクリクエスト |
| 34    | TorrentMetadata   | Multicast     | BitTorrent の .torrent ファイルまたはマグネットリンクのメタデータ交換 |

### 2.6. ノードケイパビリティ

ノードはケイパビリティをビットフィールドとしてアドバタイズします:

| Bit | Value | Capability  | Description |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth Low Energy トランスポートが利用可能 |
| 1   | 2     | WifiDirect  | Wi-Fi Direct トランスポートが利用可能 |
| 2   | 4     | Gateway     | インターネットゲートウェイ（メッシュを IP ネットワークにブリッジ） |
| 3   | 8     | Relay       | 他ノードのパケットをリレーする意思あり |
| 4   | 16    | Sos         | SOS ブロードキャスト対応 |
| 5   | 32    | Streaming   | ライブストリーミングリレー対応 |
| 6   | 64    | Voice       | 音声通話リレー対応 |
| 7   | 128   | DtnCarrier  | DTN ストアアンドフォワードキャリア |
| 8   | 256   | NearLink    | NearLink トランスポートが利用可能 |
| 9   | 512   | Video       | ビデオのエンコード/デコード対応 |

---

## 3. ルーティングアルゴリズム

Aether は、暗号学的なルート認証と QoS 重み付きルート選択を拡張した、Ad-hoc On-demand Distance Vector（AODV）ルーティングに基づくリアクティブルーティングプロトコルを使用します。

### 3.1. ルートリクエスト（RREQ）

ノードがルートを持たない宛先にパケットを送信する必要がある場合、ルートリクエストを開始します:

1. 発信者は `Type = RouteRequest` の `MeshPacket` を作成し、`SourceUhid` を自身に、`DestinationUhid` をターゲットに設定し、`TTL = 7`（デフォルト値）とします。
2. パケットは直接接続されたすべてのピアにブロードキャストされます。
3. RREQ を受信した各中間ノードは:
   a. パケット `Id` によってすでにこの RREQ を受信済みかどうかを確認します。受信済みの場合、パケットはサイレントに破棄されます（重複排除）。重複排除キャッシュは最大 `DeduplicationCacheSize` エントリ（デフォルト 10,000）を保持し、上限に達すると完全にクリアされます。
   b. RREQ 発信者への**逆方向ルート**を設定します。逆方向ルートは RREQ を受信したピアの UHID をネクストホップとして記録します。ホップカウントは `DefaultTtl - packet.Ttl + 1` から導出されます。
   c. 自身が宛先ノードである場合、RREP を生成します（セクション 3.2 参照）。
   d. 宛先への有効なルートが存在する場合、宛先に代わって RREP を生成してもよいです（MAY）。
   e. そうでない場合、TTL を減算して RREQ を再ブロードキャストします。
4. 発信者は **5,000 ms**（`RouteTimeoutMs`）のタイムアウトで RREP を待ちます。RREP が到達しない場合、ルートディスカバリは失敗します。

### 3.2. ルートリプライ（RREP）

宛先（または有効なルートを持つ中間ノード）がルートリプライを生成する場合:

1. `Type = RouteReply` の `MeshPacket` が作成され、`SourceUhid` を宛先ノードに、`DestinationUhid` を RREQ 発信者に設定します。
2. **セキュリティ要件:** RREP は宛先ノードの Ed25519 アイデンティティキーで署名されなければなりません（MUST）。署名は標準の署名対象データ（セクション 2.3）をカバーします。これにより、悪意のある中間ノードによるルートポイズニングを防ぎます。
3. RREP は RREQ 伝播中に設定された逆方向ルートに沿ってユニキャストで返送されます。
4. RREP を転送する各中間ノードは:
   a. 既知であれば、主張されたソースの公開鍵に対して RREP 署名を検証します。検証が失敗した場合、RREP は破棄されて警告がログに記録されます。
   b. RREP ソース（宛先ノード）への**前方ルート**を、RREP の送信者をネクストホップとして設定します。
   c. TTL を減算して RREQ 発信者に向けて転送します。
5. RREP が発信者に到達すると、保留中のルートリクエスト（`TaskCompletionSource` で追跡）が設定済みルートとともに解決されます。

### 3.3. ルートメンテナンス

- **TTL ベースの有効期限:** すべてのルートエントリは `ExpiresAt` タイムスタンプを持ち、`now + 300 秒`（`RouteExpirySeconds`）に設定されます。ルートは暗黙的にリフレッシュされません。有効期限後は新しい RREQ/RREP サイクルで再確立する必要があります。
- **定期的なプルーニング:** プロトコルサービスは定期的なハートビートを実行します（デフォルトは 300 秒ごと）。各サイクルで、期限切れのルートをインメモリの `ConcurrentDictionary` と SQLite バッキングストアの両方から削除します。
- **RREQ 重複排除プルーニング:** 確認済み RREQ ID のセットは `DeduplicationCacheSize`（デフォルト 10,000）エントリを超えるとクリアされます。

### 3.4. ルート品質と QoS

各 `RouteEntry` は [0, 100] の範囲の `QualityScore` を持ち、新しく発見されたルートでは 50 に初期化されます。スコアは以下を考慮します:

- **ホップカウント:** ホップ数が少ないほど、一般的に高速なルートを示します。
- **レイテンシ:** 利用可能な場合の測定往復時間。
- **ピア信頼性:** ネクストホップピアの信頼性スコア（セクション 3.5 参照）。

チップインセンティブシステムに参加しているノードは、ルート品質スコアに QoS ブーストを受けます。これはソフトな優先設定であり、チップしていないノードは常にサービスを受けますが、継続的なチッパーはわずかに優れたルート選択を経験する場合があります。ブーストティアは以下の通りです:

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. ピア信頼性スコアリング

すべての既知ピアには [0, 100] の範囲の信頼性スコアが割り当てられ、50（`DefaultReliabilityScore`）に初期化されます。スコアは観察された動作に基づいて調整されます:

| Event                | Delta |
|----------------------|-------|
| Successful relay     | +2    |
| Failed relay         | -5    |
| SOS relay            | +5    |
| Chunk served         | +1    |
| Chunk serve failure  | -10   |

信頼性スコアは SQLite に永続化され、起動時にメモリにロードされます。スコアはルート選択に影響します: より信頼性の高いピアを経由するルートが優先されます。

---

## 4. 鍵交換

> 2026-05-05 に `src/AetherNet.Security/Services/SignalProtocolService.cs` の C# リファレンス実装
> および `fixtures/signal/` 配下のクロス言語フィクスチャコーパスと照合済み。C# リファレンスは
> X25519 上で完全な X3DH + Double Ratchet（Signal §3 + §5）を実装しています。Go、Python、
> TypeScript、Rust、Swift、Kotlin は同じエンベロープに移植済みで、X3DH および KDF_RK
> フィクスチャレベルでバイト等価です。C は X25519 + KDF_RK + 対称ラチェットのプリミティブのみ
> 実装しており、フィクスチャ検証には十分ですが、完全なセッション機能はまだありません。
> このセクションとコードが一致しない場合はコードが正式です。`OPEN_ISSUES.md` に
> イシューを起票してください。

Aether は、非同期セッション確立のために **X3DH**（Extended Triple Diffie-Hellman、Signal §3）を実装し、
その後すぐに継続的な前方秘匿性とポストコンプロマイズセキュリティのために
**Signal Double Ratchet**（Signal §5）を実装します。すべてのセッション暗号は Curve25519 で動作します:
ECDH には **X25519**（RFC 7748）、署名には **Ed25519**（RFC 8032）を使用します。

### 4.1. アイデンティティキー

各ノードは最初の起動時に**2つ**の長期キーペアを生成します（XEdDSA は使用せず、
よりシンプルなデュアルキー構成がすべての実装で採用されています）:

- **Ed25519 キーペア** — 32バイトのシード（秘密鍵）、32バイトの公開鍵。
  パケット署名（§2.4）、`SignedPreKeySignature`（§4.3）、
  RREP 認証（§3.2）、チップ署名に使用されます。
- **X25519 キーペア** — 32バイトの生の秘密鍵と公開鍵。
  4つの X3DH DH 演算（§4.4）に使用されます。

リファレンス: `SignalProtocolService.InitializeIdentityKeys`。秘密鍵はデバイス上にのみ存在し、
公開鍵は `PreKeyBundle` で公開されます。

受信パケットの*署名検証*のみに対して、30日間の P-256 → Ed25519 移行ウィンドウが
設けられています（§7.5 参照）。プレキーバンドル自体はワイヤー上では X25519 のみです。

### 4.2. 曲線の選択

X3DH と Double Ratchet は **X25519** のみを使用します。P-256 は現在の実装では
セッション確立に使用されて*いません*。この仕様の旧版には P-256 ECDH について
記述がありましたが、そのテキストは 2026-05-05 のファミリー全体の X25519 移行以前のものであり、
現在は正確ではありません。

### 4.3. プレキーバンドル

イニシエータがレスポンダがオンラインでなくてもセッションを確立できるよう、
プレキーバンドルが公開されます（Signal §3.4）:

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

リファレンス: `AetherNet.Security.Models.PreKeyBundle`。ワイヤーシェイプのコントラクトは
8つの言語すべてで同一です。

**ワンタイムプレキー（OPK）プール。** 各レスポンダは `OpkPoolSize`（デフォルト 100、
Signal の公開ガイダンスを踏襲）の X25519 OPK プールを維持します。バンドル生成では
FIFO キューから未使用の次の id をポップし、プールをターゲットサイズまで補充します。
各 OPK は厳密に1回だけ消費されます: レスポンダはその id を参照する最初の PreKey
メッセージで秘密鍵の半分を削除してゼロ化します。同じ OPK id を競合する並列イニシエータは、
`_preKeyLock` 配下で厳密に1つの `EstablishResponderSession` が成功するのを確認します。
失敗した方は `CryptographicException` を発生させます。

リファレンス: `SignalProtocolService.TopUpOpkPoolNoLock`（494–518行）、
`SignalProtocolService.EstablishResponderSession`（636–718行）。プールのセマンティクスは
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs` でテストされています。

**署名済みプレキー（SPK）のローテーション。** SPK は最初のバンドル呼び出し時に遅延生成され、
後続の呼び出し間で再利用されます。これにより、X3DH 実行前にバンドルをフェッチする
並列イニシエータが互いのバンドルを無効化しないようにします。
定期的な SPK ローテーション（Signal §3.3 では週次を推奨）はバンドル生成の副作用ではなく、
明示的な操作です。

プレキー id は `RandomNumberGenerator.GetInt32(1, int.MaxValue)` から取得され、
明示的な衝突リトライ（発生前に最大 64 回試行）が行われます。

### 4.4. セッション確立（X3DH）

完全な X3DH（Signal §3.3）はイニシエータ側で実行されます。X25519 上で4つの DH 演算が計算されます:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

ここで `IK_A` / `IK_B` は X25519 アイデンティティキー、`EK_A` はこのセッション専用に
生成された新規 X25519 エフェメラルキー、`SPK_B` はレスポンダの署名済みプレキー、
`OPK_B` はレスポンダのワンタイムプレキーです。初期ルートキーは:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

`info` 定数 `aether-x3dh-root-v1` はすべての実装で同一であり、
`fixtures/signal/expected/x3dh_basic.json`（フィールド `root_key_hex`）でピン固定されています。

リファレンス: `SignalProtocolService.ProcessPreKeyBundleAsync`（554–626行）。
検証パス: `fixtures/signal/inputs.json` のケース `x3dh_basic` →
`fixtures/signal/expected/x3dh_basic.json`。

**バンドル検証。** DH 演算の前に、イニシエータは Ed25519 を使用して
`IdentityKey` に対して `SignedPreKeySignature` を検証します。
検証が失敗した場合、`CryptographicException` が発生してバンドルは破棄されます。
公開鍵サイズは `X25519Service.PublicKeySize`（32）に対して検証されます。
不正な形式のバンドルは拒否されます。

**セッションプライミング。** `ProcessPreKeyBundleAsync` の最後に `SignalSession` が
以下のように作成されます:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal 正規の X3DH ↔ Double-Ratchet 統合:
  イニシエータの X3DH エフェメラルが最初の DH ラチェットキーペア（`DHs`）になります。
- `RemoteEphemeralPub = SPK_B` — レスポンダの署名済みプレキーが
  初期ピアラチェットキー（`DHr`）として扱われます。
- `SendChainKey = null`、`RecvChainKey = null` — 両方のチェーンキーは
  最初の送信 / 最初の DH ラチェット受信時に遅延導出されます。
- `PendingPreKeyMessage = true` — 次の送信 `EncryptAsync` 呼び出しが
  PreKey メッセージ（`MessageType=1`）を送出しなければならないことを示すフラグ。

すべての DH 出力と連結共有シークレットは `finally` ブロックで
`CryptographicOperations.ZeroMemory` によってゼロ化されます。

**安全でない送信の拒否。** セッションのないピアに対して `EncryptAsync` が呼び出された場合、
呼び出しは `InvalidOperationException` をスローします。UHID 派生のフォールバックパスはありません。
ホストはメッセージをキューに入れ（`MessagingService` + `SignalMessageEnvelopeCipher` 参照）、
セッション確立が完了したら再試行することが期待されます。

### 4.5. Double Ratchet（Signal §5）

各サイドは回転する X25519 ラチェットキーペア（`DHs`）と、ピアの最後に確認された
ラチェット公開鍵のコピー（`DHr`）を維持します。すべてのメッセージで送信者は
現在の `DHs` 公開鍵を公開し、受信者が新しい `DHr` を観察するたびに、
`KDF_RK(RK, DH(myDHs, newDHr))` によってチェーンを再キーイングする **DH ラチェットステップ**を実行します。
これによりルートキーと新しいチェーンキーの両方が再導出されます。

#### 4.5.1. KDF_RK

`KDF_RK` は 64バイトのブロックに対する HKDF-SHA256 で、32+32 に分割して
新しいルートキーと新しいチェーンキーになります:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

リファレンス: `SignalProtocolService.KdfRk`（857–868行）。
`fixtures/signal/inputs.json` のケース `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json` でピン固定。

#### 4.5.2. 対称ラチェット

Signal §5.1 に従い、メッセージキーとチェーンキーは
1バイトのドメイン分離を使用した HMAC-SHA256 によってチェーンキーから導出されます:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

リファレンス: `SignalProtocolService.RatchetChainKey`（876–881行）。
`fixtures/signal/inputs.json` のケース `ratchet_step_basic` および
`ratchet_step_three_iterations` でピン固定。

この仕様の旧版では `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` および `chain_key` の
`HMAC(chain_key, 0x01)` による別途アドバンスが記述されていました。
それは Signal 非準拠であり実装されたことがありませんでした。
正規の 0x01/0x02 分割に置き換えられています。

#### 4.5.3. 受信時の DH ラチェットステップ

受信メッセージの `SenderEphemeralKeyX25519` がキャッシュされた
`RemoteEphemeralPub` と異なる場合（定数時間比較）にトリガーされます。

1. 送信カウンタを `PreviousChainCount` として保存（Signal §5: PN）し、
   ピアが境界を越えたスキップキーを計算できるようにします。
2. `SendCounter` と `RecvCounter` を 0 にリセットし、新しい `RemoteEphemeralPub` をインストールします。
3. 新しい受信チェーンを導出: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`。
4. 古い `myDHs` 秘密鍵をゼロ化し、新しい X25519 キーペアを生成します。
5. 新しい送信チェーンを導出: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`。

リファレンス: `SignalProtocolService.DhRatchetReceive`（726–772行）。

#### 4.5.4. 送信チェーンの遅延導出

イニシエータの最初の送信は、完全な DH ラチェットではなく**ハーフステップ**を実行します。
X3DH がすでに `DHs` と `DHr` を配置しているため、送信チェーンのみを導出する必要があります:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` はここでは*ローテーションされません*。
真の受信側 DH ラチェットステップでのみローテーションされます。

リファレンス: `SignalProtocolService.DhRatchetSendOnly`（780–796行）。

#### 4.5.5. スキップされたメッセージキー

メッセージが順序外れで到着した場合、スキップされた各カウンタのメッセージキーは
`SkippedMessageKeys` にキャッシュされ、`(Hex(remoteEphPub):counter)` をキーとします。
リモート公開鍵のバインディングは不可欠です — DH ラチェットステップ後でも
以前のチェーン（異なる `DHr`）からの順序外れのメッセージが到着する可能性があり、
それぞれのチェーン固有のキーセットが必要です。

制限:

- 単一のギャップで `MaxSkippedKeys`（1000）エントリを超えてスキップすると
  `CryptographicException` が発生し、セッションの再確立が強制されます。
- DH ラチェット境界を越える場合、受信者はまず*古い*チェーンで最大
  `PreviousChainCount` キーをスキップし、その後 DH ラチェットステップを実行してから
  新しいチェーンのキーを導出します。

リファレンス: `SignalProtocolService.SkipMessageKeys`（804–830行）および
復号内のスキップループ（366–388行）。

### 4.6. 暗号化ペイロードフォーマット

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

リファレンス: `AetherNet.Security.Models.EncryptedPayload`（`SecurityModels.cs` の 55–66行）。
`InitiatorEphemeralKeyX25519` フィールドは Pre-Double-Ratchet ワイヤーエンベロープの
後方互換エイリアスであり、PreKey メッセージでは `SenderEphemeralKeyX25519` と等しいです。
新しいコンシューマはこれを無視するべきです。

AES-GCM パラメータ: 256ビットキー、96ビットノンス（`AesNonceSize = 12`）、
128ビットタグ（`AesTagSize = 16`）、タグは暗号文に連結されます。
メッセージキーは AES-GCM の暗号化/復号の直後に `finally` ブロックでゼロ化されます。

### 4.7. 言語別ステータス

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | primitives only — `aethernet_x25519_*`, `aethernet_signal_kdf_rk` | not implemented | — | kdf_rk_basic only |

セッション対応の 7 言語すべて（C# + Go + TypeScript + Python + Kotlin + Swift + Rust）は、
C# リファレンスコントラクトに合わせた遅延補充とロック保護消費を備えた 100 キー FIFO OPK プールを実装しています。
C はプリミティブのみ実装しており、完全なセッション機能は `OPEN_ISSUES.md` のアイテム 11 で追跡されています。

---

## 5. トランスポートレイヤー要件

Aether はトランスポート非依存です。`ITransportService` コントラクトを満たすあらゆる物理通信チャネルがメッシュに参加できます。

### 5.1. ITransportService インターフェースコントラクト

すべてのトランスポート実装は以下を公開しなければなりません（MUST）:

**プロパティ:**

| Property           | Type   | Description |
|--------------------|--------|-------------|
| `Name`             | string | 人間が読めるための識別子（例: "BLE"、"Wi-Fi Direct"、"NearLink"） |
| `IsAvailable`      | bool   | トランスポートがこのデバイスで現在使用可能かどうか |
| `MaxBandwidthBps`  | int64  | バイト毎秒での最大スループット |
| `MaxRangeMeters`   | int32  | メートル単位の最大通信範囲 |
| `PowerCostRelative`| int32  | 相対的な消費電力（1 = 低、10 = 高） |
| `MaxConcurrentPeers` | int32 | 同時ピア接続の最大数 |

**メソッド:**

| Method         | Signature | Description |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | 特定のピアにバイト配列を送信。成功時に true を返す。 |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | ピアにストリームを送信（大規模転送、音声、ビデオ用）。 |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | ピアへのアクティブな接続があるかどうかを確認。 |

**イベント:**

| Event          | Signature | Description |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | ピアからデータが到着したときに発火。 |

### 5.2. トランスポート選択アルゴリズム

`TransportManager` は以下に基づいて各パケットの最適なトランスポートを選択します:

1. **可用性:** `IsAvailable == true` のトランスポートのみが考慮されます。
2. **ペイロードサイズ:** ペイロードサイズが `BleMaxPayloadBytes`（1,024バイト）以下の場合、電力効率のために BLE が優先されます。それより大きいペイロードは Wi-Fi Direct を優先します。
3. **消費電力の重み付け:** 利用可能なトランスポートの中で、通常トラフィックには低い `PowerCostRelative` 値が優先されます。高優先度パケット（SOS、音声）はこの優先設定を上書きする場合があります。
4. **ピア接続性:** ターゲットピアへのアクティブな接続がすでにあるトランスポート（`IsConnected` が true を返す）は、接続セットアップのオーバーヘッドを避けるために優先されます。
5. **フォールバック:** どのローカルトランスポートもターゲットに到達できない場合、パケットは AetherNetAPI 経由のサーバーリレーのためにキューに入れられます。

### 5.3. リファレンストランスポート

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | Notes |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | 主要なディスカバリ + 小パケット |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | 大規模転送、ストリーミング、音声 |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon、高スループット |

**BLE ペイロード制限:** 1,024バイト（`BleMaxPayloadBytes`）を超えるパケットは
自動的に Wi-Fi Direct または NearLink にルーティングされます。BLE はディスカバリアドバタイズ、
小さな制御パケット（RREQ/RREP、プレゼンスビーコン）、および低帯域幅メッセージングに使用されます。

**Wi-Fi Direct** の接続タイムアウトは 10,000 ms（`WifiDirectTimeoutMs`）で、
同時ピア数の最大値は 8（`MaxWifiDirectPeers`）です。

---

## 6. ディスカバリプロトコル

### 6.1. BLE アドバタイジング

Aether ノードは主に BLE アドバタイジングを通じて互いを発見します。静的識別子による
永続的なトラッキングを防ぐため、プロトコルは2つのプライバシーメカニズムを採用しています:
ローテーティングサービス UUID とアイデンティティ解決キー。

**アドバタイジングサイクル:** 2秒スキャンオン、8秒オフ（`BleScanOnMs`/`BleScanOffMs`）。
アドバタイズ間隔は 1,000 ms（`BleAdvertiseIntervalMs`）です。
タイミングパターンの検出を防ぐため、スキャン間隔に 0〜2,000 ms のランダムジッター
（`BleScanJitterMaxMs`）が追加されます。

**ピアタイムアウト:** 30秒以内に再発見されないピアは切断されたとみなされます
（`PeerLost` イベント）。

### 6.2. ローテーティングサービス UUID

長期的な BLE フィンガープリンティングを防ぐため、アドバタイズで使用されるサービス UUID は
15分ごとにローテーションされます（`BleUuidRotationSeconds = 900`）:

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key` はノードごとに1回生成された 32バイトのキーで、セキュアストレージに保存されます。
同じローテーションキーを共有するすべての Aether ノードは、指定された時間ウィンドウに対して
同じ UUID を導出し、永続的な識別子を公開せずに相互発見が可能になります。

非ローテーションスキームからの移行期間中 90 日間、静的フォールバック UUID
（`A3E7-1001-0001-0000-000000000000`）が維持されます。

### 6.3. アイデンティティ解決キー（IRK）

各ノードはセキュアストレージに保存された 128ビットのアイデンティティ解決キー（IRK）を生成します。
IRK は鍵交換中に信頼済みピアと共有されます。

**解決可能なプライベートアドレス（RPA）の生成:**

1. `prand = HMAC-SHA256(IRK, window_bytes)[0..2]`（3バイト）を計算します。
2. `prand[0]` の上位2ビットを `01`（BLE 仕様に従った RPA フラグ）に設定します。
3. `prand` が 16バイトのゼロパディング入力の 13〜15バイトを占める形で
   `hash = AES-128-ECB(IRK, pad(prand))` を計算します。
4. RPA を構築: `hash[0..2] || prand[0..2]`（合計 6バイト）。

**RPA の解決:** ピアの IRK を持つノードは、RPA の `prand` コンポーネントからハッシュを
再計算することで、観察された RPA がそのピアのものかどうかを検証できます。
解決時間は N が既知の IRK 数である場合、約 O(N) で、100ピアで約 0.1ms です。

RPA はサービス UUID と同じ 15分サイクルでローテーションされます。

### 6.4. ジオハッシュベースの近接性

ノードはオプションで位置をジオハッシュとしてエンコードします。プライバシーのため、
ジオハッシュは4文字に切り詰められ、約 39km × 20km の解像度が得られます。
この粒度は以下に十分です:

- 近接ベースのチャンネルディスカバリ
- DTN エピデミックルーティング（受信者の最後に既知のジオハッシュエリアに向けてレプリケート）
- SOS アラートの地理的コンテキスト

完全精度のジオハッシュはメッシュ上では送信されません。切り詰められた形式のみが共有され、
それもノードのプライバシーレベルが許可する場合のみです（`PrivacyLevel.Full` または `PrivacyLevel.Partial`）。

---

## 7. セキュリティモデル

### 7.1. 脅威モデル

Aether は以下の攻撃者ケイパビリティを想定しています:

- **受動的盗聴:** 攻撃者は無線範囲内のすべての BLE アドバタイズとメッシュトラフィックを観察できます。
- **能動的注入:** 攻撃者はパケットを注入、変更、またはリプレイできます。
- **シビル攻撃:** 攻撃者は複数の偽ノードアイデンティティを作成できます。
- **選択的サービス拒否:** 攻撃者はリレーノードとしてパケットを選択的にドロップできます。

### 7.2. 保護されているもの

| Property | Protection Level | Mechanism |
|----------|-----------------|-----------|
| メッセージ内容 | 完全な機密性 | AES-256-GCM とメッセージごとのキー（セクション 4.5） |
| 送信者アイデンティティ | 部分的 | UHID はパケットヘッダに表示される。BLE アドレスはローテーションされる（セクション 6） |
| 受信者アイデンティティ | 部分的 | 宛先 UHID はルーティングされたパケットに表示される。ブロードキャストパケットは宛先が空 |
| ルーティングメタデータ | 最小限 | 中間ノードはソース/宛先 UHID と TTL を参照できる |
| メッセージ順序 | 保護済み | 対称ラチェットのカウンタが並び替えを防止 |
| メッセージ完全性 | 完全 | すべてのパケット（v2）に Ed25519 署名 |

### 7.3. 攻撃耐性

**リプレイ攻撃:**
各パケットは 8バイトの暗号学的乱数ノンスとミリ秒精度のタイムスタンプを持ちます。
リレーノードは 5分間の TTL（`MaxPacketAgeSeconds = 300`）で
`(SenderUhid, NonceValue)` ペアの重複排除キャッシュを維持します。
同一送信者からの重複ノンスを持つパケットはドロップされます。
タイムスタンプが 5分以上古いパケットはノンスに関わらず拒否されます。

ノンス重複排除キャッシュは 60秒ごとにクリーニングされます。期限切れのエントリ
（5分以上古いもの）は削除されます。

**中間者攻撃（MITM）:**
- ルートリプライパケットは、主張された宛先ノードからの有効な Ed25519 署名を持たなければなりません。
  中間ノードは宛先の秘密鍵を持っていないため、RREP を偽造できません。
- プレキーバンドルには長期アイデンティティに対して `SignedPreKey` をバインドする
  `SignedPreKeySignature`（Ed25519）が含まれています。
- セッション確立（セクション 4.4）はプレキー検証ステップを通じて両者のアイデンティティに
  暗号学的にセッションをバインドします。

**シビル攻撃:**
- 各ノードの信頼性スコアは 50 から始まり、観察された動作に基づいて調整されます（セクション 3.5）。
  新規作成されたシビルノードは蓄積されたレピュテーションを持ちません。
- 信頼性スコアが低い（0 に近い）ノードはルート選択で優先度が下げられます。
- DTN エピデミックルーティングアルゴリズムはジオハッシュの近接性とリレー成功履歴を使用して
  レプリケーションターゲットを選択するため、シビルノードが真のリレー貢献なしにトラフィックを
  引き寄せることが難しくなります。

**フラッディング攻撃:**
- TTL は各ホップで減算され、TTL = 0 のパケットはドロップされます。デフォルト TTL の 7 は
  あらゆるブロードキャストの影響範囲を制限します。
- パケット ID による RREQ 重複排除はブロードキャストストームによる増幅を防止します。
  重複排除キャッシュは `DeduplicationCacheSize`（デフォルト 10,000）エントリを超えるとフラッシュされます。
- SOS ブロードキャストはノードごとに 1時間に 3回に制限されます（セクション 8）。

### 7.4. 鍵のゼロ化

すべての中間暗号マテリアルは使用直後にゼロ化されます:

- ECDH 鍵共有からの `sharedSecret`: HKDF 導出後にゼロ化。
- チェーンラチェットからの `messageKey`: AES-GCM の暗号化/復号後にゼロ化。
- 順序外れの復号からの `skippedKey`: 使用後にゼロ化されてマップから削除。
- 導出された `RootKey`、`SendChainKey`、`RecvChainKey`: 確立コンテキストからゼロ化（セッションは自身のコピーを保持）。

ゼロ化にはコンパイラによる最適化が保証されない `CryptographicOperations.ZeroMemory` を使用します。

### 7.5. P-256 から Ed25519 への移行

プロトコルは ECDSA P-256 アイデンティティキー（プロトコルバージョン 1）から
Ed25519（プロトコルバージョン 2）への 30日間の移行ウィンドウをサポートします:

1. プロトコルバージョン 1 パケット（未署名）は移行期間中に受け入れられます。
2. 署名検証はまず Ed25519 を試みます。公開鍵が 32バイトより長い場合
（DER エンコードされた P-256 キーを示す）、P-256 ECDSA 検証にフォールバックします。
3. 30日間のウィンドウ後、プロトコルバージョン 1 パケットは拒否されます。
4. 移行していないノードは新しい Ed25519 アイデンティティで再初期化する必要があります。

### 7.6. 管轄地域の認識

プロトコルは、暗号化とメッシュネットワーキングに関する異なる法的要件に対応するために
管轄地域ティアを定義します:

| Tier | Behavior | Example Jurisdictions |
|------|----------|-----------------------|
| 1    | 自由に動作 | South Africa, Kenya, Ghana |
| 2    | 変更された動作 | Nigeria, India, EU, US, UK |
| 3    | メッシュのみ（高リスク） | China, Russia, Iran, UAE, Myanmar |
| 4    | 不明（デフォルトはメッシュのみ） | All others |

ティアの選択は機能の可用性に影響します（例: チップ/金融機能はティア 3 で無効になる場合があります）が、
暗号化を弱めません。エンドツーエンド暗号化は管轄地域に関わらず常に適用されます。

---

## 8. SOS ブロードキャスト

SOS メカニズムは、ユーザーが危険な状況にあり、近くのメッシュピアやインターネットに
同時にアクセスする必要がある状況向けに設計されたデュアルパス緊急フラッドです。

### 8.1. ブロードキャストパラメータ

| Parameter | Value | Description |
|-----------|-------|-------------|
| TTL       | 15    | 通常のデフォルト（7）の2倍で、より広い伝播を確保 |
| Priority  | 999   | 最大優先度。リレーキュー内の他のすべてのトラフィックをプリエンプト |
| Rate limit| 3/hour| 悪用防止のためのノードごとの制限 |
| Destination| empty | すべてのピアへのブロードキャスト（特定の宛先なし） |

### 8.2. フラッドアルゴリズム

1. 発信者は `Type = SosBroadcast`、`TTL = 15`、`Priority = 999`、
   空の `DestinationUhid` で SOS パケットを構築します。
2. ペイロードは JSON エンコードされ、以下を含みます:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **デュアルパスディスパッチ:** SOS は同時に以下を介して送信されます:
   - **メッシュフラッド:** 利用可能なすべてのトランスポートを通じて接続されたすべてのピアにブロードキャスト。
   - **API コール:** サーバーサイドの配信と PanikAPI へのブリッジ（SMS/メールディスパッチ）のために AetherNetAPI に送信。
4. 両方のパスは互いに対してファイアアンドフォゲットです。
   API コールが失敗しても、メッシュフラッドは独立して継続されます。

### 8.3. リレー動作

ノードが SOS パケットを受信した場合:

1. パケット `Id` による重複排除を確認します。すでに確認済みの場合はサイレントにドロップします。
2. ペイロードをデシリアライズし、ローカル UI のために `SosReceived` イベントを発火します。
3. アラートをアクティブアラートリストに追加します。
4. `TTL > 1` の場合、TTL を減算して、ルーティングテーブルの状態に関わらず
   **すべてのピアに再ブロードキャスト**します。SOS パケットは通常のルーティングをバイパスして
   無条件にフラッドします。

### 8.4. レート制限

各ノードは最近のブロードキャストタイムスタンプのスライディングウィンドウを維持します。
新しい SOS を開始する前に:

1. キューから 1時間以上古いエントリを削除します。
2. キューに 3つ以上のエントリ（`MaxSosBroadcastsPerHour`）がある場合、
   ブロードキャストは拒否されます。
3. ディスパッチが成功すると、現在のタイムスタンプがエンキューされます。

レート制限は発信 SOS ブロードキャストのみに適用され、リレーには適用されません。

### 8.5. SOS-PanikAPI ブリッジ

メッシュ経由で受信した SOS ブロードキャストは、従来の緊急対応（連絡先への SMS、
メールアラート）のために PanikAPI に転送できます。逆に、PanikAPI の緊急セッションは
コミュニティの認識のためにメッシュにブロードキャストできます。
ループ防止はソース（`direct` vs `mesh_forward`）のマーキングと
メッシュブロードキャストの `internet_forwarded` フラグによって実現されます。

---

## 9. DTN ストアアンドフォワード

遅延耐性ネットワーキング（DTN）サブシステムは、送信者と受信者間にエンドツーエンドの
パスが存在しない場合のメッセージ配信を可能にします。バンドルは中間ノードに保存され、
接続状況の変化に応じて日和見的に転送されます。

### 9.1. バンドルフォーマット

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. バンドルライフサイクル

1. **作成:** 送信者は暗号化されたペイロードを含むバンドルを作成します（受信者との Signal セッションで暗号化）。`Status = Pending`、`CopyCount = 1`。
2. **即時配信の試行:** 送信者はまず直接メッシュルーティング（RREQ/RREP）を試みます。ルートが存在する場合、バンドルは即座に配信され、`Status` が `Delivered` に遷移します。
3. **サーバーリレーの試行:** メッシュルーティングが失敗した場合、送信者は AetherNetAPI を介したリレーを試みます。サーバーが受信者に到達できる（またはメッセージをキューに入れられる）場合、配信は成功します。
4. **ストアアンドフォワード:** メッシュとサーバーリレーの両方が失敗した場合、バンドルはローカルストレージに残ります（`Pending` ステータス）。次の配信スキャンを待ちます。

### 9.3. 配信スキャン

定期的なスキャンが 60秒ごとに実行されます（`DtnScanIntervalSeconds`）:

1. SQLite から（信頼の源として）すべての保留中バンドルを読み込みます。
2. 各保留中バンドルに対して:
   a. 受信者へのメッシュルートを試みます。
   b. サーバーリレーを試みます。
   c. 両方が失敗し `CopyCount < MaxCopies` の場合、エピデミックレプリケーションを試みます（セクション 9.4）。
3. 期限切れのバンドルを削除します（`ExpiresAt <= now`）。

### 9.4. エピデミックルーティング

直接配信とサーバーリレーの両方が失敗した場合、バンドルはエピデミックルーティングを使用して
近くのピアにレプリケートされます:

1. `EpidemicRoutingService` が現在のピアリストからレプリケーションターゲットを選択します。
2. ターゲット選択は以下を考慮します:
   - **ジオハッシュ近接性:** ジオハッシュが受信者の最後に既知のジオハッシュに近いピアが優先されます。
   - **リレー履歴:** 信頼性スコアが高いピアが優先されます。
   - **コピー予算:** `CopyCount >= MaxCopies`（デフォルト: 3）になるとレプリケーションが停止します。
3. 各レプリケーションは選択されたピアに `DtnBundle` パケットを送信します。
4. 受信時、ピアの DTN サービスが `AcceptCustodyAsync` を呼び出します。

### 9.5. カストディ転送

ノードが別のノード宛の DTN バンドルを受信した場合:

1. **容量チェック:** ノードは現在のバンドル数を `DtnMaxBundlesPerNode`（50）と比較します。
   容量に達している場合、カストディは拒否されます。
2. **受け入れ:** バンドルのステータスが `InCustody` に設定され、ホップカウントがインクリメントされ、
   バンドルが SQLite に永続化されます。
3. **カストディレコード:** 転送を記録する `CustodyRecord` が作成されます（from、to、タイムスタンプ）。
4. **コピーカウントのインクリメント:** バンドルの `CopyCount` が永続ストレージでインクリメントされます。
5. **確認応答:** `Accepted = true` の `DtnCustodyAck` パケットが転送ノードに返送されます。
6. 受け入れノードは後続のスキャンでの配信試行を担当するようになります。

### 9.6. 配信レシート

意図された受信者が DTN バンドルを受信した場合:

1. バンドルのステータスが `Delivered` に更新されます。
2. `DtnDeliveryReceipt` がメッシュルーティング（サーバーリレーフォールバックあり）で
   元の送信者に返送されます:
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. レシートを受信すると、送信者はストアからバンドルを削除して `BundleDelivered` イベントを発火します。
4. レシートはアナリティクスのために AetherNetAPI にも同期されます。

### 9.7. バンドル有効期限

- デフォルトのバンドル TTL は 72時間（`DtnBundleTtlHours`）です。
- 期限切れのバンドルは定期的な配信スキャン中にクリーンアップされます。
- `Expired` または `Delivered` ステータスのバンドルはインメモリキャッシュと SQLite の両方から削除されます。

### 9.8. 容量制限

| Parameter               | Default | Description |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | バンドルの最大有効期間 |
| `DtnMaxCopies`          | 3       | ネットワーク全体でのバンドルごとの最大コピー数 |
| `DtnMaxBundlesPerNode`  | 50      | 単一ノードが保持するバンドルの最大数 |
| `DtnScanIntervalSeconds`| 60      | 配信スキャン頻度 |

---

## 10. ビデオストリーミング

> **2026-05-05 時点のステータス — 設計 + C# スキャフォールディング、配布可能なコーデックパイプラインなし。**
> パケットタイプ `StreamAnnounce`（11）、`StreamSegment`（12）、
> `StreamSubscribe`（13）、`StreamUnsubscribe`（14）、`VideoCall`（27）、
> `VideoSignaling`（28）、`VideoFrame`（31）、`ScreenShare`（32）は
> ワイヤー定義済みでクロス言語フィクスチャコーパスでのラウンドトリップが確認されています。
> C# の `AetherNet.Streaming` モジュールはインターフェース、モデル、スケルトンサービス
> （`StreamingService`、`VideoCallService`、`WatchTogetherService`）を実装しており、
> ルーティング/DI のシームとユニキャストセグメントファンアウトを接続していますが、
> 実際のビデオエンコード/デコードは結合されていません。他の 7 言語はワイヤータイプのみです。
> `docs/adaptive-secure-streaming-spec.md` の前方設計ドキュメントがターゲットアーキテクチャです。
> 以下の散文をそれらのサービスが**実装する**仕様として扱い、本番環境の準備ギャップについては
> `OPEN_ISSUES.md` を参照してください。

Aether は3つのビデオモードをサポートします: ピアツーピアビデオ通話、グループビデオ
（動的トポロジーの無制限参加者）、そしてライブブロードキャスト。すべてのビデオフレームは
Signal プロトコルで暗号化され、Ed25519 で署名されます。

### 10.1. トランスポートケイパビリティマトリクス

ビデオ通話を開始する前に、発信者はトランスポートレイヤーにクエリを送信して
ピアへの最良の利用可能接続を決定します。トランスポートはどの品質のビデオが
可能かを決定します:

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | No (audio-only) | — | — | 64 Kbps | Sync packets only |
| NearLink | Light | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Full | 1080p | H.264 | 3000 Kbps | All modes |
| Internet | Full | 720p | H.264 | 1500 Kbps | All modes |
| CircleLink | No (audio-only) | — | — | 64 Kbps | Sync packets only |

利用可能なトランスポートが BLE または CircleLink のみの場合、
ビデオ通話サービスは自動的に音声通話にダウングレードします。

### 10.2. ビデオコーデック

| Enum Value | Codec | Use Case |
|------------|-------|----------|
| 0 | H.264 | デフォルト。広くサポートされ、優れた圧縮。 |
| 1 | H.265 | より優れた圧縮。帯域幅制約のある NearLink で使用。 |
| 2 | VP8 | ロイヤリティフリーの代替。 |

### 10.3. ビデオ解像度

| Enum Value | Resolution | Typical Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P ビデオ通話フロー

1. **ケイパビリティチェック**: 発信者は `GetVideoCapabilityAsync(peerUhid)` にクエリして
   最適なトランスポート、最大解像度、推奨コーデックを決定します。
2. **オファー**: 発信者は優先コーデック、最大解像度、最大ビットレートを含む
   `VideoSignaling` パケット（タイプ 28）を `SignalType = Offer` で送信します。
3. **アンサー/リジェクト**: 着信側は `SignalType = Answer`（コーデックを最小公倍数に交渉）
   または `SignalType = Reject` で応答します。
4. **アクティブ通話**: 両ノードは H.264/H.265/VP8 NAL ユニットを含む `VideoCall`
   パケット（タイプ 27）を交換します。各フレームにはジッターバッファの順序付け用の
   シーケンス番号とキーフレームフラグが含まれます。
5. **画面共有**: いずれかのパーティが画面共有を切り替えられます。`SignalType = ScreenShareStart/Stop` の
   `VideoSignaling` がピアに通知します。画面共有フレームは `PacketType.ScreenShare`（タイプ 32）を
   使用しますが、同じ処理パイプラインです。
6. **通話終了**: いずれかのパーティが `SignalType = Bye` の `VideoSignaling` を送信します。

すべてのシグナリングとフレームペイロードは Signal プロトコル（X3DH セッション）で暗号化されます。
暗号化されたペイロードは `MeshPacket.Payload` フィールド内に JSON エンコードされた
`EncryptedPayload` としてシリアライズされます。

### 10.5. ビデオ通話ステートマシン

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

ステート: `Initiating(0)`、`Ringing(1)`、`Active(2)`、`OnHold(3)`、`Ended(4)`、`Failed(5)`、`Rejected(6)`。

### 10.6. グループビデオ

グループビデオセッションは無制限の参加者をサポートします。トポロジーは参加者数に基づいて
動的に選択されます:

- **FullMesh**（2〜3参加者）: 各参加者が他のすべての参加者に1つのストリームを送信します。
  シンプルで低レイテンシ。
- **SFU**（4人以上の参加者、閾値: `SfuThresholdParticipants = 4`）: 1つのノードが
  SFU リレーとして選出されます。各参加者はリレーに1つのストリームを送信し、
  リレーがそれを他のすべてに配信します。リレーノードはインセンティブレイヤー経由でチップを獲得します。

トポロジーの切り替えは自動です: 4人目の参加者が参加すると、セッションは FullMesh から SFU に移行します。
参加者が離れて数が 4 を下回ると、元に戻ります。

グループビデオフレームは `PacketType.VideoFrame`（タイプ 31）を使用します。
SFU モードでは、フレームはリレーノードの UHID に送信され、リレーが再ブロードキャストします。

### 10.7. ジッターバッファ

ビデオジッターバッファは音声ジッターバッファ（20ms の Opus フレームを処理）とは独立して動作します:

- **範囲**: 最小 60ms、最大 500ms。
- **適応的な深さ**: 指数移動平均（EMA）によるフレーム間ジッターの追跡。
  バッファ深さ = ジッター推定値の 2倍、[60, 500] ms にクランプ。
- **キーフレーム対応ドロッピング**: バッファがオーバーフローした場合、非キーフレーム（P/Bフレーム）が
  先にドロップされます。I フレーム（キーフレーム）は決してドロップされません — デコーダ回復に
  必要です。
- **ギャップ処理**: シーケンスギャップが検出された場合、バッファは無期限に待機するのではなく、
  次に利用可能なキーフレームにスキップします。

### 10.8. ビデオシグナリングタイプ

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Offer | コーデック/解像度の優先設定を含むビデオ通話開始 |
| 1 | Answer | 交渉されたパラメータでの通話受け入れ |
| 2 | Reject | 通話拒否 |
| 3 | Bye | 通話終了 |
| 4 | Upgrade | より高い品質のリクエスト（例: トランスポートが改善された） |
| 5 | Downgrade | より低い品質のリクエスト（例: 帯域幅低下） |
| 6 | ScreenShareStart | ピアが画面共有を開始した |
| 7 | ScreenShareStop | ピアが画面共有を停止した |

### 10.9. 暗号化モデル

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| P2P ビデオ通話 | フレームごとの Signal プロトコル | X3DH 鍵共有 |
| グループビデオ | グループチャンネルキー（AES-GCM） | セッション作成時に Signal プロトコルで配布 |
| 画面共有 | 親通話モードと同じ | ビデオ通話セッションから継承 |

---

## 11. Watch Together

> **2026-05-05 時点のステータス — 設計 + C# スキャフォールディング、§10 と同じ成熟度。**
> パケットタイプ `WatchSync`（29）、`WatchReaction`（30）、
> `WatchChunkRequest`（33）、`TorrentMetadata`（34）はワイヤー定義済みで
> フィクスチャテスト済みです。`AetherNet.Streaming.WatchTogetherService` は
> コーディネーションスケルトン（セッション状態、`IMeshSender` 経由の同期コマンド伝播、
> RTT 補償ヘルパー）を提供していますが、BitTorrent インジェスト、
> ChipIn SDPKT 決済、ピアからのチャンクフェッチはいずれの言語にも実装されていません。
> 以下の散文をターゲットプロトコルとして扱ってください。`docs/adaptive-secure-streaming-spec.md`
> の前方設計ドキュメントが同じ内容をより詳細にカバーしています。

Watch Together は、メッシュピアのグループ全体で同期されたメディア再生を可能にします。
ホストは再生（プレイ、ポーズ、シーク、スピード）の独占的な制御権を持ちます。
同期コマンドには RTT 補償のためのウォールクロックタイムスタンプが含まれます。

### 11.1. ウォッチモード

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | 同期パケットのみ（それぞれ 100バイト未満） | Any (works over BLE) |
| 1 | StreamFromHost | P2P チャンク転送（P2pContentService を再利用） | WiFi Direct or Internet |
| 2 | BitTorrent | ゲートウェイノード経由のメッシュ + 外部スウォーム | WiFi Direct or Internet |

### 11.2. SharedFile モード

両方の参加者が同じファイルを持っています（SHA-256 コンテンツハッシュで照合）。
`WatchSync` パケットのみが交換されます。これは最も帯域幅効率の高いモードで、BLE 上で動作します。

1. ホストがファイルの SHA-256 のコンテンツハッシュ `contentHash` でウォッチセッションを作成します。
2. 参加者が参加し、プレイヤーがロードされると `IsReady = true` を報告します。
3. セッションはすべての参加者が準備完了を報告したときに開始されます。
4. ホストは `WatchSync` パケット（タイプ 29）としてプレイ/ポーズ/シーク/スピードコマンドを送信します。
5. 受信者は RTT 補償を適用します: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`。

### 11.3. StreamFromHost モード

ホストのみがファイルを持っています。ホストは `ContentManifest`（P2P コンテンツシステムを再利用）を
生成し、参加者はメッシュを介してチャンクをダウンロードします。

- チャンク選択は `SequentialFromPosition` 戦略（`RarestFirst` ではなく）を使用します。
  現在の再生位置より前のチャンクを優先し、その後バックフィルしてシーディングを行います。
- バッファターゲット: 30秒先（`WatchTogetherBufferAheadSeconds`）。
- 自動ポーズ: いずれかの参加者のバッファが 10秒（`WatchTogetherMinBufferSeconds`）を
  下回ると、セッションはすべての参加者を `BufferUnderrun` 同期コマンドで自動ポーズします。
  すべての参加者が十分なバッファを持つと再生が再開されます（`BufferReady`）。
- 視聴者がチャンクをダウンロードするにつれて、他の視聴者のシーダーになります
  （メッシュ内での BitTorrent スタイルのスウォーミング）。

### 11.4. BitTorrent モード

参加者がグループチャットで `.torrent` ファイルまたはマグネットリンクを共有します。
`TorrentMetadata` パケット（タイプ 34）がすべてのセッション参加者にトレント情報を配信します。

**メッシュからスウォームへのブリッジ:**
- ゲートウェイノード（インターネット接続のあるノード）が外部の BitTorrent スウォームからピースをダウンロードします。
- ゲートウェイノードはダウンロードされたピースをメッシュ配信のために再暗号化し、メッシュピアにシードします。
- インターネットなしのメッシュピアはゲートウェイノードと互いからピースを受信します。
- P2P コンテンツエンジンが BitTorrent のピースモデルと Aether のチャンクモデルの間で変換を行います。

十分なコンテンツがバッファリングされると、SharedFile モードと同じ同期プロトコルを使用して
Watch Together 再生が開始されます。

### 11.5. ウォッチセッションステートマシン

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

ステート: `WaitingForReady(0)`、`Buffering(1)`、`Playing(2)`、`Paused(3)`、`Ended(4)`。

### 11.6. 同期コマンドタイプ

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Play | 指定された位置から再生を再開 |
| 1 | Pause | 指定された位置で一時停止 |
| 2 | Seek | 指定された位置にジャンプ |
| 3 | Speed | 再生速度を変更 |
| 4 | BufferUnderrun | 自動ポーズ — 参加者のバッファが致命的に低い |
| 5 | BufferReady | 再開 — すべての参加者が十分なバッファを持っている |

### 11.7. RTT 補償

同期コマンドには `WallClockMs` フィールド（Unix エポックミリ秒）が含まれます。
受信者が同期コマンドを処理する際:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Play および BufferReady コマンドの場合: `adjustedPosition = commandPosition + networkDelay`
4. Pause および Seek コマンドの場合: 再生が停止/ジャンプするため、位置は正確に適用されます（調整不要）。

これにより、すべての参加者がネットワーク RTT の半分以内で同期されます。

### 11.8. リアクション

参加者は再生中にコンテンツに対してリアクションできます:

- **絵文字リアクション**: `Type = Emoji` の `WatchReaction` パケット（タイプ 30）で、
  絵文字文字列とリアクション時のメディア位置を含みます。
- **音声コメント**: `Type = VoiceComment` の `WatchReaction` パケットで、
  Opus エンコードされた音声データ（最大 10秒）を含みます。
  音声データはリアクションの `VoiceData` フィールドに含まれます。

リアクションはすべてのセッション参加者にブロードキャストされます。メディア位置にタイムスタンプが付けられ、
再生同期ディスプレイが可能です。

### 11.9. ChipIn — グループコンテンツ取得

ChipIn により、グループメンバーが資金をプール（ZAR 建て、LedgerAPI を介した SDPKT
ウォレットで決済）して、グループウォッチング用のコンテンツを共同取得できます。

**ステートマシン:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

ステート: `Collecting(0)`、`Funded(1)`、`Purchasing(2)`、`Acquired(3)`、`Failed(4)`、`Refunded(5)`。

**フロー:**
1. イニシエータが目標金額とコンテンツの説明で `ChipInPool` を作成します。
2. 参加者が SDPKT ウォレットトランザクションで金額を拠出します。
3. `CollectedAmount >= TargetAmount` になると、ステートが `Funded` に遷移します。
4. システムがコンテンツを取得します（例: BitTorrent ダウンロードを開始する）。
5. コンテンツが利用可能になると、ステートが `Acquired` に遷移し、Watch Together が開始できます。

各拠出は監査証跡のために SDPKT トランザクション ID とともに記録されます。

### 11.10. 暗号化モデル

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| ウォッチ同期コマンド | チャンネル/会話キー | 既存の Signal プロトコルセッション |
| コンテンツチャンク（StreamFromHost） | マニフェストごとのコンテンツキー | Signal プロトコルで配布 |
| BitTorrent ピース | インジェスト時に再暗号化 | ゲートウェイがスウォームからクリアテキストをダウンロードし、メッシュ用に暗号化 |
| ウォッチリアクション | セッションキー | 会話キーから導出 |

### 11.11. 機能フラグ

すべてのビデオおよび Watch Together 機能は機能フラグによって制御されます（デフォルトはすべて無効）:

| Flag | Parent | Description |
|------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | P2P およびグループビデオ通話 |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | マルチパーティビデオセッション |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | ビデオ通話での画面共有 |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | 同期メディア再生 |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | 絵文字および音声リアクション |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | メッシュ配信用の BitTorrent ファイル受け入れ |

機能フラグには親の依存関係があります: 子フラグは親も有効な場合にのみ有効にできます。
これにより段階的なロールアウトが可能になります。

---

## 12. セキュリティ・プライバシーレイヤー

> 2.3.0 で追加。リファレンス実装: `src/AetherNet.Security/Backup/`（リカバリーフレーズ）、`src/AetherNet.Security/Privacy/`（BLE トラッキング保護、パニックワイプ）、および `src/AetherNet.Security/Sync/`（マルチデバイス同期）。言語間バイトベクトル: `fixtures/bip39/`、`fixtures/bleprivacy/`、`fixtures/panicwipe/`、`fixtures/sync/`。

このレイヤーは付加的なものであり、§2 のパケットスイートから独立しています。**マルチデバイス同期**（§12.1–12.2）と **BLE トラッキング保護アドレス方式**（§12.3）のみがバイト / オンエア形式を持ちます。**リカバリーフレーズバックアップ**（§12.4）と **パニックワイプ**（§12.5）はローカル専用であり、完全性のためにここで規定します。これらはすべて 8 言語すべてでバイト単位で同一に実装されており、唯一の例外は §12.1 に記載した Ed25519 署名です。

### 12.1. DeviceLink（デバイスペアリング）

`DeviceLink` は、あるデバイスの公開鍵があるアイデンティティに属することを示す Ed25519 署名付きのアサーションであり、マルチデバイス同期のためにユーザー自身のデバイスをペアリングするのに使用されます。**署名対象の本体**は次のとおりです:

| Off | Field | Type | Size | Notes |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01`。読み取り時にそれ以外の値は拒否する |
| 1 | device_id_len | uint16, little-endian | 2 | `device_id` の UTF-8 バイト長 |
| 3 | device_id | UTF-8 bytes | N | リンクされるデバイスの識別子 |
| 3+N | device_public_key | bytes | 32 | リンクされるデバイスの Ed25519 公開鍵 |
| 35+N | issued_at_ms | int64, little-endian | 8 | Unix エポックミリ秒 |

シリアライズされた `DeviceLink` は、署名対象の本体に続いてその本体に対する **64 バイトの Ed25519 署名**であり、*アイデンティティ*秘密鍵で計算されます。検証では本体を再計算し、アイデンティティ公開鍵に対して署名を確認します。

> **署名バイト一致の例外。** 署名対象の本体と検証結果は 8 言語すべてで同一であり、64 個の署名**バイト**はそのうち 7 言語でバイト単位で同一です。Apple の CryptoKit は Ed25519 署名をランダム化するため（RFC 8032 §8 のヘッジ署名）、Swift の署名は呼び出しごとに異なりますが、有効かつ相互検証可能なままです。相互運用は署名バイトの比較ではなく、必ず*検証*に依拠しなければなりません（MUST）。

### 12.2. SyncRecord（後書き優先の同期エンベロープ）

`SyncRecord` は、ユーザー自身のマルチデバイス状態への 1 つの複製された変更であり、後書き優先で調停されます。レコードは既存の DTN/メッシュ経路の内部を E2E 暗号化されて移動します（`encrypted_payload` は不透明な暗号文です）——これらは新しい `MeshPacket` タイプ**ではありません**。

| Off | Field | Type | Size | Notes |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01` |
| 1 | record_id | UUID, RFC 4122 big-endian | 16 | §2.1 と同じ big-endian 規約 |
| 17 | op | uint8 | 1 | `0`=Upsert、`1`=Delete、`2`=Read。2 より大きい値は拒否する |
| 18 | logical_clock | int64, little-endian | 8 | デバイスごとの単調カウンター |
| 26 | created_at_ms | int64, little-endian | 8 | Unix エポックミリ秒 |
| 34 | device_id_len | uint16, little-endian | 2 | UTF-8 バイト長 |
| 36 | device_id | UTF-8 bytes | N | 発信元デバイス |
| 36+N | item_id_len | uint16, little-endian | 2 | UTF-8 バイト長 |
| 38+N | item_id | UTF-8 bytes | M | 同期される論理キー |
| 38+N+M | payload_len | int32, little-endian | 4 | 暗号文の長さ。負値は拒否する |
| 42+N+M | encrypted_payload | bytes | payload_len | 不透明な E2E 暗号文 |

**調停（後書き優先）。** 同じ `item_id` の 2 つのレコードの間では、いずれかが異なるまで順に比較して勝者を選びます: `created_at_ms`、次に `logical_clock`、次に `device_id`（序数バイト比較）、次に `record_id`（big-endian バイト比較）。この順序は全順序かつ決定的であるため、到着順に関係なくすべてのデバイスが同じ勝者に収束します。

### 12.3. BLE トラッキング保護

2 つの導出により、デバイスはパッシブスキャナーに追跡されることなくアドバタイズできます。どちらも `fixtures/bleprivacy/` に固定された純粋関数であり、それらをオンエアで送出するのはホストの BLE スタックの役割です。

- **ローテーティングサービス UUID。** `window = floor(unix_time_seconds / 900)`（15 分エポック）。アドバタイズされる 128 ビットサービス UUID は `HMAC-SHA256(ble_rotation_key, LE_int64(window))` の先頭 16 バイトです。UUID を記録するスキャナーは、ローテーションキーなしでは 2 つのウィンドウをリンクできません。
- **解決可能プライベートアドレス（RPA）。** Bluetooth の `ah` 関数に従います: `hash = ah(IRK, prand)`。ここで `ah` は 24 ビットの `prand`（128 ビットにパディング）に対する AES-128 であり、下位 24 ビットが取られます。48 ビットアドレスは `hash(24) || prand(24)` で、`prand` の上位 2 ビットを `0b01` に設定して解決可能であることを示します。IRK を保持するピアは、`ah` を再計算してハッシュを比較することでアドレスを解決します。

### 12.4. リカバリーフレーズバックアップ（ローカル）

アイデンティティは Ed25519 鍵ペアであり、その 32 バイトの秘密シード（256 ビット）は、標準の SHA-256 チェックサム付きで公式の英語ワードリスト上の **24 語の BIP-39** ニーモニックとしてエンコードされます（タイプミスされた単語はチェックサムに失敗し、静かに異なるアイデンティティを生成するのではなく拒否されます）。これは標準の BIP-39 であり——公式の Trezor テストベクトルに対して検証され、8 言語すべてでバイト単位で再現されています——そのため、このフレーズはサーバーやカストディアンなしで任意のデバイス上でアイデンティティを復元します。ワイヤー形式はありません。フレーズがネットワークに触れることは決してありません。

### 12.5. パニックワイプ（ローカル）

強要下では、**強要 PIN**——保存された `SHA-256(pin)` に対して一定時間で比較されます——がすべてのアイデンティティ鍵素材のセキュアな消去をトリガーします: 各バッファはランダムバイトで上書きされてからゼロ化され、アイデンティティ鍵名の固定されたマニフェスト（アイデンティティ鍵ペア、デバイスソルト、DRK、および §12.3 の BLE ローテーションキー / IRK）にわたって行われます。ワイヤー形式はありません。この操作は完全にデバイスにローカルです。

---

## 付録 A: 定数リファレンス

すべてのプロトコル定数は `ProtocolConstants` で定義されており、参照のためにここに再掲します:

### ルーティング
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE ディスカバリ
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherNetBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### セキュリティ
| Constant                  | Value  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constant                   | Value |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constant                  | Value  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### トランスポート
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### ハートビート
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### プレゼンス
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### 音声
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### ストリーミング
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### ビデオ
| Constant                       | Value |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

---

## 付録 B: 用語集

| Term | Definition |
|------|------------|
| **UHID** | ユニバーサルハードウェア識別子。デバイスアイデンティティと暗号鍵から導出された、メッシュノードを識別する一意の文字列。 |
| **RREQ** | ルートリクエスト。宛先ノードへのパスを発見するために使用されるブロードキャストパケット。 |
| **RREP** | ルートリプライ。RREQ によって確立された逆方向ルートに沿って返送されるユニキャストパケット。 |
| **IRK** | アイデンティティ解決キー。BLE 解決可能なプライベートアドレスを生成および解決するために使用される 128ビットキー。 |
| **RPA** | 解決可能なプライベートアドレス。定期的にローテーションされるが、送信者の IRK を持つピアが解決できる 6バイトの BLE アドレス。 |
| **X3DH** | Extended Triple Diffie-Hellman。非同期セッション確立を可能にする鍵共有プロトコル。 |
| **DTN** | 遅延耐性ネットワーキング。断続的な接続環境向けのストアアンドフォワードパラダイム。 |
| **Gateway** | インターネット接続を持ち、メッシュトラフィックを IP ベースのサービスとの間でブリッジするメッシュノード。 |
| **HKDF** | HMAC ベースの鍵導出関数。単一の共有シークレットから複数の鍵を導出するために使用。 |
| **Pre-key bundle** | 受信者がオンラインでなくても送信者が暗号化されたセッションを確立できるように公開された鍵のセット。 |
| **SFU** | 選択的転送ユニット。各送信者から1つのビデオストリームを受信し、他のすべての参加者に配信するリレーノード。ノードごとのアップロード帯域幅を削減する。 |
| **ChipIn** | グループメンバーが SDPKT 資金をプールして、グループウォッチング用のコンテンツを共同取得するグループ資金調達メカニズム。 |
| **NAL** | ネットワーク抽象化レイヤー。H.264 および H.265 コーデックがビデオフレームをパケット化するために使用するカプセル化フォーマット。 |

---

## 付録 C: 参考文献

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
