# Aether Mesh Networking Protocol Specification
**著者：** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **読者への注意。** このドキュメントの初期草稿は、8 言語ワイヤーフォーマット整合と、X25519 + Signal Double Ratchet へのファミリー全体の移行よりも前のものです。2026-05-05 時点で、§2（パケットフォーマット）、§3（ルーティング）、§4（鍵交換）、§9（DTN）は実装されたプロトコルを説明しています。§10（ビデオストリーミング）と §11（Watch Together）はターゲットプロトコルを説明しています — ワイヤー定義され、フィクスチャーテスト済みですが、コーデック / BitTorrent / ChipIn パイプラインはまだスキャフォールディングに接続されていません。このドキュメントと実装が食い違う場合は C# リファレンスが権威を持ちます。
>
> - 正規ワイヤーバイト：`fixtures/expected/*.bin`（10 個の名前付きケース）
> - リファレンスシリアライザー：`src/Aether.Core/Protocol/PacketSerializer.cs`
> - リファレンス Signal スタック：`src/Aether.Security/Services/SignalProtocolService.cs`
> - リファレンスルーティング：`src/Aether.Core/Routing/RoutingService.cs`
> - リファレンス DTN：`src/Aether.Core/Dtn/DtnService.cs`
> - クロス言語ワイヤー相互運用証明：`fixtures/README.md`
> - クロス言語 Signal 相互運用証明：`fixtures/signal/README.md`

---

## 目次

1. [概要](#1-abstract)
2. [パケットフォーマット](#2-packet-format)
3. [ルーティングアルゴリズム](#3-routing-algorithm)
4. [鍵交換](#4-key-exchange)
5. [トランスポート層の要件](#5-transport-layer-requirements)
6. [ディスカバリープロトコル](#6-discovery-protocol)
7. [セキュリティモデル](#7-security-model)
8. [SOS ブロードキャスト](#8-sos-broadcast)
9. [DTN ストアアンドフォワード](#9-dtn-store-and-forward)
10. [ビデオストリーミング](#10-video-streaming)
11. [Watch Together](#11-watch-together)

---

## 1. 概要

Aether は、インターネット接続が断続的または存在しない環境向けに設計された分散型メッシュネットワーキングプロトコルです。異種の近距離通信トランスポート（Bluetooth Low Energy、Wi-Fi Direct、NearLink）を介したマルチホップパケットルーティング、シンメトリックラチェットを伴う X3DH 派生鍵合意を使用したエンドツーエンド暗号化、遅延耐性のあるストアアンドフォワード配信、および緊急 SOS フラッドメカニズムを提供します。プロトコルはトランスポート非依存です。ピア間でバイト配列を送受信できる物理層はすべて有効な Aether トランスポートです。ノードはユニバーサルハードウェア識別子（UHID）によって識別され、Ed25519 ID キーによって認証されます。Aether はユニバーサルネットワーク層として意図されています — エコシステム内のすべてのアプリケーションが Aether サービスを登録し、インターネット接続のないノードは、メッシュトラフィックをインターネットにブリッジするゲートウェイピアを通じてより広いネットワークに接続します。

---

## 2. パケットフォーマット

> 2026-05-05 に `src/Aether.Core/Protocol/PacketSerializer.cs` および `fixtures/expected/` 以下の 10 個のフィクスチャーケースと照合済み。

### 2.1. MeshPacket ワイヤーレイアウト

すべての Aether メッセージは `MeshPacket` にカプセル化されます。フィールドはワイヤー上で**正確に**この順序で現れます：

| オフ | フィールド            | 型                            | サイズ       | 注記 |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = 未署名（レガシー）、`2` = 署名済み（現在） |
| 1   | Type             | uint8                           | 1          | パケット型の列挙（§2.4 を参照） |
| 2   | Id               | UUID、RFC 4122 ビッグエンディアン       | 16         | 重複排除のためのパケット識別子。.NET のミックスエンディアン Guid デフォルトではなく、**ビッグエンディアン**バイト順。 |
| 18  | Priority         | uint8                           | 1          | 優先度レベル（0 = 通常、255 = SOS）。**ワイヤーフィールドは 1 バイト；255 を超える値はクランプされなければなりません。** |
| 19  | Ttl              | int32、リトルエンディアン            | 4          | 各ホップで減少する有効期限（TTL）。**4 バイト int32**、1 バイト uint8 ではありません — 最大 ~2³¹-1 の値が有効です。 |
| 23  | TimestampMs      | int64、リトルエンディアン            | 8          | Unix エポックミリ秒（UTC）。 |
| 31  | SourceUhid Len   | uint16、リトルエンディアン           | 2          | `SourceUhid` の UTF-8 バイト長。最大 65535。 |
| 33  | SourceUhid       | UTF-8 バイト                     | N          | 送信者の UHID；空は許可されるが通常は使用されない。 |
| 33+N | DestinationUhid Len | uint16、リトルエンディアン        | 2          | `DestinationUhid` の UTF-8 バイト長。 |
| ... | DestinationUhid  | UTF-8 バイト                     | M          | 受信者の UHID；ブロードキャストの場合は空文字列。 |
| ... | PacketNonce Len  | uint16、リトルエンディアン           | 2          | `PacketNonce` のバイト長。標準値：8。 |
| ... | PacketNonce      | バイト                           | P          | リプレイ防止のための暗号学的ランダムノンス。 |
| ... | Payload Len      | int32、リトルエンディアン            | 4          | `Payload` のバイト長。負の値はエラー。 |
| ... | Payload          | バイト                           | Q          | アプリケーションデータ。解釈は `Type` に依存。 |
| ... | Signature Len    | uint16、リトルエンディアン           | 2          | `Signature` のバイト長。0（未署名）または 64（Ed25519）。 |
| ... | Signature        | バイト                           | R          | 署名可能データに対する Ed25519 署名（§2.3 を参照）。 |

**長さプレフィックスの幅**はフィールドによって異なります — `SourceUhid`、`DestinationUhid`、`PacketNonce`、`Signature` は **2 バイト（uint16）**の長さプレフィックスを使用します；`Payload` はペイロードが 64 KiB を超える可能性があるため **4 バイト（int32）**の長さプレフィックスを使用します。

### 2.2. 最小パケットサイズ

すべての可変長フィールドが空（長さゼロの UHID、長さゼロのノンス、長さゼロのペイロード、長さゼロのシグネチャ）の場合、ワイヤーサイズは：

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

この仕様の初期草稿にあった 50 バイト / 52 バイトの数値は誤りでした。

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

具体的な例については `fixtures/expected/basic_data.bin`（83 バイト、`fixtures/inputs.json` 内の正規入力）を参照してください。実装はフルフィクスチャーコーパスに対して検証されます — 相違点があるとクロス言語フィクスチャーベリファイアーテストに失敗します。

### 2.4. 署名可能データの構築

署名（ワイヤー上の `Signature` フィールド）は、ワイヤーバイト自体ではなく、別の正規バイトシーケンスに対して計算されます。これにより、ワイヤーレイアウトが署名を壊さずに進化でき、中継ノードが平文ペイロードを見ることなく整合性を検証できます（ペイロードの SHA-256 ハッシュのみが署名されます）。

署名可能なバイトシーケンスは以下の連結です：

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

> §2.1 のワイヤーレイアウトからの意図的な乖離に注意してください。署名可能データは `Type`、`Length`、`Ttl`、`Priority` に **4 バイト int32** を使用しますが、ワイヤーはそれぞれ 1 バイト / 2 バイト / 4 バイト / 1 バイトを使用します。これは意図的です — 署名可能な形式は言語をまたいで移植可能であり、固定幅フィールドを使用します；ワイヤー形式は BLE PDU の効率性のためにコンパクトです。実装は `Priority` を署名可能バイトにエンコードする前に `[0,255]` にクランプしなければなりません。そうしないと、受信側（ワイヤーバイト 0..255 を参照する）が異なる署名可能バッファを導出し、検証が失敗します。

リファレンス実装は `src/Aether.Security/Services/PacketSigningService.cs::BuildSignableData` にあり、ポーティングの必読資料です。

### 2.5. パケット型

| 値 | 名称              | 方向     | 説明 |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | ブロードキャスト     | AODV ルートリクエスト |
| 2     | RouteReply        | ユニキャスト       | AODV ルートリプライ（宛先による署名が必須） |
| 3     | Data              | ユニキャスト       | アプリケーションデータ |
| 4     | Ack               | ユニキャスト       | 配信確認応答 |
| 5     | SosBroadcast      | フラッド         | 緊急ブロードキャスト（セクション 8 を参照） |
| 6     | SosAck            | ユニキャスト       | SOS 確認応答 |
| 7     | ChannelMessage    | マルチキャスト     | グループチャンネルメッセージ |
| 8     | ChunkRequest      | ユニキャスト       | P2P コンテンツチャンクリクエスト |
| 9     | ChunkData         | ユニキャスト       | P2P コンテンツチャンクレスポンス |
| 10    | Heartbeat         | ブロードキャスト     | 定期的な生存確認シグナル |
| 11    | StreamAnnounce    | ブロードキャスト     | ライブストリーム広告 |
| 12    | StreamSegment     | ユニキャスト / ツリー  | ライブストリームメディアセグメント |
| 13    | StreamSubscribe   | ユニキャスト       | ストリームリレーツリーへの参加リクエスト |
| 14    | StreamUnsubscribe | ユニキャスト       | ストリームリレーツリーからの離脱 |
| 15    | VoicePtt          | ユニキャスト       | プッシュツートーク音声フレーム |
| 16    | VoiceCall         | ユニキャスト       | リアルタイム音声通話フレーム |
| 17    | VoiceSignaling    | ユニキャスト       | 音声通話のセットアップ / ティアダウン |
| 18    | DtnBundle         | ユニキャスト       | DTN ストアアンドフォワードバンドル（セクション 9 を参照） |
| 19    | DtnCustodyAck     | ユニキャスト       | DTN カストディ転送確認応答 |
| 20    | DtnDeliveryReceipt| ユニキャスト       | DTN エンドツーエンド配信確認 |
| 21    | PresenceBeacon    | ブロードキャスト     | プレゼンスと利用可能性のアナウンス |
| 22    | PresenceQuery     | ユニキャスト       | プレゼンスステータスリクエスト |
| 23    | ProfileSync       | ユニキャスト       | プロファイルメタデータの同期 |
| 24    | TipPacket         | ユニキャスト       | ノードチップ（LedgerAPI 経由で決済） |
| 25    | PreKeyRequest     | ユニキャスト       | ピアのプリキーバンドルリクエスト |
| 26    | PreKeyResponse    | ユニキャスト       | プリキーバンドル配信 |
| 27    | VideoCall         | ユニキャスト       | 暗号化ビデオフレーム（H.264/H.265/VP8 NAL ユニット） |
| 28    | VideoSignaling    | ユニキャスト       | ビデオ通話セットアップ：オファー、アンサー、リジェクト、バイ、コーデックネゴシエーション |
| 29    | WatchSync         | ユニキャスト       | 同期再生コマンド：再生、一時停止、シーク、速度 |
| 30    | WatchReaction     | マルチキャスト     | Watch Together 中のタイムスタンプ付き絵文字または音声リアクション |
| 31    | VideoFrame        | ユニキャスト / SFU   | グループビデオフレーム（SFU リレーが参加者に配布） |
| 32    | ScreenShare       | ユニキャスト       | スクリーンシェアフレーム（ビデオと同じパイプライン、別フラグ） |
| 33    | WatchChunkRequest | ユニキャスト       | 再生位置にバイアスされた優先チャンクリクエスト |
| 34    | TorrentMetadata   | マルチキャスト     | BitTorrent .torrent ファイルまたはマグネットリンクのメタデータ交換 |

### 2.6. ノードケイパビリティ

ノードはケイパビリティをビットフィールドとして広告します：

| ビット | 値 | ケイパビリティ  | 説明 |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth Low Energy トランスポート利用可能 |
| 1   | 2     | WifiDirect  | Wi-Fi Direct トランスポート利用可能 |
| 2   | 4     | Gateway     | インターネットゲートウェイ（メッシュを IP ネットワークにブリッジ） |
| 3   | 8     | Relay       | 他者のためにパケットをリレーする意思あり |
| 4   | 16    | Sos         | SOS ブロードキャスト対応 |
| 5   | 32    | Streaming   | ライブストリーミングリレー対応 |
| 6   | 64    | Voice       | 音声通話リレー対応 |
| 7   | 128   | DtnCarrier  | DTN ストアアンドフォワードキャリア |
| 8   | 256   | NearLink    | NearLink トランスポート利用可能 |
| 9   | 512   | Video       | ビデオエンコード / デコード対応 |

---

## 3. ルーティングアルゴリズム

Aether は、暗号ルート認証と QoS 重み付けルート選択で拡張された、アドホックオンデマンド距離ベクター（AODV）ルーティングに基づくリアクティブルーティングプロトコルを使用します。

### 3.1. ルートリクエスト（RREQ）

ノードがルートを持たない宛先にパケットを送信する必要がある場合、ルートリクエストを開始します：

1. 発信元は `Type = RouteRequest` で `MeshPacket` を作成し、`SourceUhid` を自身に、`DestinationUhid` をターゲットに設定し、`TTL = 7`（デフォルト）に設定します。
2. パケットは直接接続されているすべてのピアにブロードキャストされます。
3. RREQ を受信した各中間ノードは：
   a. パケット `Id` によってこの RREQ を既に見ているか確認します。見ている場合はパケットをサイレントに破棄します（重複排除）。重複排除キャッシュは最大 `DeduplicationCacheSize` エントリ（デフォルト 10,000）を保持し、上限に達するとすべてクリアされます。
   b. RREQ の発信元への**リバースルート**をインストールします。リバースルートは RREQ を受信したピアの UHID をネクストホップとして記録します。ホップカウントは `DefaultTtl - packet.Ttl + 1` から導出されます。
   c. 宛先である場合は RREP を生成します（セクション 3.2 を参照）。
   d. 宛先への有効なルートが既に存在する場合は、宛先に代わって RREP を生成できます（MAY）。
   e. それ以外の場合は TTL を減少させ、RREQ を再ブロードキャストします。
4. 発信元は **5,000 ms**（`RouteTimeoutMs`）のタイムアウトで RREP を待ちます。RREP が到着しない場合、ルート発見は失敗します。

### 3.2. ルートリプライ（RREP）

宛先（または有効なルートを持つ中間ノード）がルートリプライを生成する場合：

1. `Type = RouteReply` の `MeshPacket` が作成され、`SourceUhid` が宛先ノードに、`DestinationUhid` が RREQ 発信元に設定されます。
2. **セキュリティ要件：** RREP は宛先ノードの Ed25519 ID キーで署名されなければなりません（MUST）。署名は標準の署名可能データ（セクション 2.3）をカバーします。これにより、悪意のある中間ノードによるルートポイズニングを防ぎます。
3. RREP は RREQ 伝播中にインストールされたリバースルートに沿ってユニキャストで返送されます。
4. RREP を転送する各中間ノードは：
   a. RREP の署名を主張されたソースの公開鍵に対して検証します（既知の場合）。検証が失敗した場合、RREP は破棄され警告がログに記録されます。
   b. RREP ソース（宛先ノード）への**フォワードルート**を RREP の送信者をネクストホップとしてインストールします。
   c. TTL を減少させ、RREQ 発信元に向けて転送します。
5. RREP が発信元に到達すると、保留中のルートリクエスト（`TaskCompletionSource` で追跡）がインストールされたルートで解決されます。

### 3.3. ルートメンテナンス

- **TTL ベースの有効期限：** すべてのルートエントリは `now + 300 秒`（`RouteExpirySeconds`）に設定された `ExpiresAt` タイムスタンプを持ちます。ルートは暗黙的にリフレッシュされません。有効期限後は新しい RREQ / RREP サイクルで再確立しなければなりません。
- **定期的なプルーニング：** プロトコルサービスは定期的なハートビート（デフォルトで 300 秒ごと）を実行します。各サイクルで、インメモリの `ConcurrentDictionary` と SQLite バッキングストアの両方から期限切れのルートを削除します。
- **RREQ 重複排除のプルーニング：** 見られた RREQ ID のセットは、`DeduplicationCacheSize`（デフォルト 10,000）エントリを超えるとクリアされます。

### 3.4. ルート品質と QoS

各 `RouteEntry` は [0, 100] の範囲の `QualityScore` を持ち、新しく発見されたルートは 50 で初期化されます。スコアは以下を考慮します：

- **ホップカウント：** ホップが少ないほど一般的に高速なルートを示します。
- **レイテンシー：** 利用可能な場合の測定されたラウンドトリップタイム。
- **ピアの信頼性：** ネクストホップピアの信頼性スコア（セクション 3.5 を参照）。

チップインセンティブシステムに参加しているノードは、ルート品質スコアに QoS ブーストを受けます。これはソフトな優先設定です。チップを行わないノードも常にサービスを受けますが、継続的にチップを行うノードはわずかに優れたルート選択を経験する場合があります。ブーストティアは以下の通りです：

| ティア    | 一貫性しきい値 | QoS ブースト |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. ピア信頼性スコアリング

すべての既知のピアには [0, 100] の範囲の信頼性スコアが割り当てられ、50（`DefaultReliabilityScore`）で初期化されます。スコアは観察された動作に基づいて調整されます：

| イベント                | デルタ |
|----------------------|-------|
| 成功したリレー     | +2    |
| 失敗したリレー         | -5    |
| SOS リレー            | +5    |
| チャンク提供         | +1    |
| チャンク提供失敗  | -10   |

信頼性スコアは SQLite に永続化され、起動時にメモリに読み込まれます。スコアはルート選択に影響します。信頼性の高いピアを通じるルートが優先されます。

---

## 4. 鍵交換

> 2026-05-05 に `src/Aether.Security/Services/SignalProtocolService.cs` の C# リファレンス実装および `fixtures/signal/` 以下のクロス言語フィクスチャーコーパスと照合済み。C# リファレンスは X25519 上で完全な X3DH + Double Ratchet（Signal §3 + §5）を搭載しています。Go、Python、TypeScript、Rust、Swift、Kotlin は同じエンベロープに移植され、X3DH および KDF_RK フィクスチャーレベルでバイト等価です。C は X25519 + KDF_RK + シンメトリックラチェットプリミティブのみを搭載しています — フィクスチャーベリファイアには十分ですが、完全なセッション機構はまだありません。このセクションとコードが食い違う場合は、コードが権威を持ちます。`OPEN_ISSUES.md` に issue を提出してください。

Aether は非同期セッション確立のために **X3DH**（Extended Triple Diffie-Hellman、Signal §3）を実装し、継続的な前方秘匿性と侵害後のセキュリティのために直ちに **Signal Double Ratchet**（Signal §5）を続けます。すべてのセッション暗号は Curve25519 上で実行されます：ECDH には **X25519**（RFC 7748）、署名には **Ed25519**（RFC 8032）。

### 4.1. ID キー

各ノードは最初の起動時に **2 つの**長期キーペアを生成します（XEdDSA なし；よりシンプルなデュアルキー配置がすべての実装でシップされます）：

- **Ed25519 キーペア** — 32 バイトシード（秘密鍵）、32 バイト公開鍵。パケット署名（§2.4）、`SignedPreKeySignature`（§4.3）、RREP 認証（§3.2）、およびチップ署名に使用されます。
- **X25519 キーペア** — 32 バイトのローパイベートキーとパブリックキー。4 つの X3DH DH 操作に使用されます（§4.4）。

リファレンス：`SignalProtocolService.InitializeIdentityKeys`。秘密鍵はデバイス上にのみ存在します；公開鍵は `PreKeyBundle` に公開されます。

署名**検証**のみに対して、受信パケットに対して 30 日間の P-256 → Ed25519 移行ウィンドウが適用されます — §7.5 を参照してください。プリキーバンドル自体はワイヤー上では X25519 のみです。

### 4.2. 曲線の選択

X3DH と Double Ratchet は **X25519** を排他的に使用します。P-256 は現在のいかなる実装でもセッション確立に使用されていません。この仕様の初期草稿では P-256 ECDH が説明されていましたが、そのテキストは 2026-05-05 のファミリー全体の X25519 への移行より前のものであり、もはや正確ではありません。

### 4.3. プリキーバンドル

イニシエーターがレスポンダーがオンラインでなくてもセッションを確立できるように、プリキーバンドルが公開されます（Signal §3.4）：

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

リファレンス：`Aether.Security.Models.PreKeyBundle`。ワイヤー形状のコントラクトはすべての 8 言語で同一です。

**ワンタイムプリキー（OPK）プール。** 各レスポンダーは `OpkPoolSize`（デフォルト 100、Signal の公開ガイダンスを反映）の X25519 OPK プールを維持します。バンドル生成は FIFO キューから次の未使用 id をポップし、プールをターゲットサイズまで補充します。各 OPK はちょうど 1 回消費されます。レスポンダーは、その id を参照する最初の PreKey メッセージで秘密の半分を削除してゼロ化します。同じ OPK id を競い合う並行イニシエーターは、`EstablishResponderSession` の 1 つが `_preKeyLock` 下で成功します；失敗した方は `CryptographicException` を発生させます。

リファレンス：`SignalProtocolService.TopUpOpkPoolNoLock`（494–518 行）、`SignalProtocolService.EstablishResponderSession`（636–718 行）。プールのセマンティクスは `tests/Aether.Core.Tests/PreKeyPoolTests.cs` によって検証されています。

**署名済みプリキー（SPK）のローテーション。** SPK は最初のバンドル呼び出し時に遅延生成され、後続の呼び出しにわたって再使用されます。これにより、X3DH が実行される前にバンドルをフェッチする並行イニシエーターが互いのバンドルを無効化しません。定期的な SPK ローテーション（Signal §3.3 では週次を推奨）は明示的な操作であり、バンドル生成の副作用ではありません。

プリキー id は `RandomNumberGenerator.GetInt32(1, int.MaxValue)` から、明示的な衝突リトライ（発生前に最大 64 回の試行）で取得されます。

### 4.4. セッション確立（X3DH）

完全な X3DH（Signal §3.3）はイニシエーター側で実行されます。4 つの DH 操作が X25519 上で計算されます：

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

ここで `IK_A` / `IK_B` は X25519 ID キー、`EK_A` はこのセッションのためだけに生成される新鮮な X25519 エフェメラル、`SPK_B` はレスポンダーの署名済みプリキー、`OPK_B` はレスポンダーのワンタイムプリキーです。初期ルートキーは：

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

`info` 定数 `aether-x3dh-root-v1` はすべての実装で同一であり、`fixtures/signal/expected/x3dh_basic.json`（フィールド `root_key_hex`）によってピン留めされています。

リファレンス：`SignalProtocolService.ProcessPreKeyBundleAsync`（554–626 行）。検証パス：`fixtures/signal/inputs.json` ケース `x3dh_basic` → `fixtures/signal/expected/x3dh_basic.json`。

**バンドル検証。** DH が実行される前に、イニシエーターは Ed25519 を使用して `IdentityKey` に対して `SignedPreKeySignature` を検証します。検証が失敗した場合は `CryptographicException` が発生し、バンドルが破棄されます。公開鍵サイズは `X25519Service.PublicKeySize`（32）に対して検証され、不正なバンドルは拒否されます。

**セッションプライミング。** `ProcessPreKeyBundleAsync` の終わりに `SignalSession` が以下で作成されます：

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal 正規の X3DH ↔ Double-Ratchet 統合：イニシエーターの X3DH エフェメラルが最初の DH ラチェットキーペア（`DHs`）になります。
- `RemoteEphemeralPub = SPK_B` — レスポンダーの署名済みプリキーが初期ピアラチェットキー（`DHr`）として扱われます。
- `SendChainKey = null`、`RecvChainKey = null` — 両チェーンキーは最初の送信 / 最初の DH ラチェット受信時に遅延導出されます。
- `PendingPreKeyMessage = true` — 次のアウトバウンドの `EncryptAsync` 呼び出しが PreKey メッセージ（`MessageType=1`）を発行しなければならないことをフラグします。

すべての DH 出力と連結された共有シークレットは `finally` ブロックで `CryptographicOperations.ZeroMemory` によってゼロ化されます。

**安全でない送信の拒否。** セッションなしのピアに対して `EncryptAsync` が呼び出された場合、その呼び出しは `InvalidOperationException` をスローします。UHID 派生のフォールバックパスはありません。ホストはメッセージをキューに入れ（`MessagingService` + `SignalMessageEnvelopeCipher` を参照）、セッション確立が完了したらリトライすることが期待されます。

### 4.5. Double Ratchet（Signal §5）

各サイドは回転する X25519 ラチェットキーペア（`DHs`）と、ピアの最後に見られたラチェット公開鍵のコピー（`DHr`）を維持します。すべてのメッセージで送信者は現在の `DHs` 公開鍵を公開します；受信者が新しい `DHr` を観察するたびに、`KDF_RK(RK, DH(myDHs, newDHr))` を介してチェーンを再鍵化する **DH ラチェットステップ**を実行し、ルートキーと新鮮なチェーンキーの両方を再導出します。

#### 4.5.1. KDF_RK

`KDF_RK` は 64 バイトブロックに対する HKDF-SHA256 であり、新しいルートキーとチェーンキーに 32+32 に分割されます：

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

リファレンス：`SignalProtocolService.KdfRk`（857–868 行）。`fixtures/signal/inputs.json` ケース `kdf_rk_basic` → `fixtures/signal/expected/kdf_rk_basic.json` によってピン留めされています。

#### 4.5.2. シンメトリックラチェット

Signal §5.1 に従い、メッセージキーとチェーンキーは、シングルバイトドメイン分離を使用した HMAC-SHA256 でチェーンキーから導出されます：

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

リファレンス：`SignalProtocolService.RatchetChainKey`（876–881 行）。`fixtures/signal/inputs.json` ケース `ratchet_step_basic` および `ratchet_step_three_iterations` によってピン留めされています。

この仕様の初期草稿では `messageKey = HMAC-SHA256(chain_key, counter_bytes)` と、`HMAC(chain_key, 0x01)` を介した別個の `chain_key` 進行が説明されていました。それは非 Signal であり、実装されたことはありませんでした。正規の 0x01/0x02 分割に置き換えられています。

#### 4.5.3. 受信時の DH ラチェットステップ

受信メッセージの `SenderEphemeralKeyX25519` がキャッシュされた `RemoteEphemeralPub` と（定数時間比較で）異なる場合にトリガーされます。

1. アウトバウンドカウンターを `PreviousChainCount`（Signal §5：PN）として保存し、ピアが境界をまたいでスキップされたキーを計算できるようにします。
2. `SendCounter` と `RecvCounter` を 0 にリセット；新しい `RemoteEphemeralPub` をインストールします。
3. 新しい受信チェーンを導出：`(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`。
4. 古い `myDHs` の秘密鍵をゼロ化；新鮮な X25519 キーペアを生成します。
5. 新しい送信チェーンを導出：`(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`。

リファレンス：`SignalProtocolService.DhRatchetReceive`（726–772 行）。

#### 4.5.4. 遅延送信チェーン導出

イニシエーターの最初の送信は、完全な DH ラチェットではなく**ハーフステップ**を実行します — X3DH がすでに `DHs` と `DHr` を配置しているため、送信チェーンのみを導出する必要があります：

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` はここでは回転**されません**。真の受信側 DH ラチェットステップでのみ回転されます。

リファレンス：`SignalProtocolService.DhRatchetSendOnly`（780–796 行）。

#### 4.5.5. スキップされたメッセージキー

メッセージが順序どおりに到着しない場合、各スキップされたカウンターのメッセージキーは `SkippedMessageKeys` にキャッシュされ、`(Hex(remoteEphPub):counter)` でキーが付けられます。リモート公開鍵のバインディングが不可欠です — DH ラチェットステップ後に、以前のチェーン（異なる `DHr`）からの順序不整合メッセージが到着した場合でも、独自のチェーンごとのキーセットが必要です。

制限：

- 単一のギャップで `MaxSkippedKeys`（1000）エントリを超えてスキップすると `CryptographicException` が発生し、セッションの再確立が強制されます。
- DH ラチェット境界を越える場合、受信側はまず*古い*チェーン上で `PreviousChainCount` キーまでスキップし、次に新しいチェーンでキーを導出する前に DH ラチェットステップを実行します。

リファレンス：`SignalProtocolService.SkipMessageKeys`（804–830 行）および復号内のスキップループ（366–388 行）。

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

リファレンス：`Aether.Security.Models.EncryptedPayload`（`SecurityModels.cs` の 55–66 行）。`InitiatorEphemeralKeyX25519` フィールドは、Double Ratchet 以前のワイヤーエンベロープとの後方互換エイリアスであり、PreKey メッセージでは `SenderEphemeralKeyX25519` と等しくなります；新しいコンシューマーはこれを無視すべきです。

AES-GCM パラメーター：256 ビットキー、96 ビットノンス（`AesNonceSize = 12`）、128 ビットタグ（`AesTagSize = 16`）、タグは暗号文に連結されます。メッセージキーは AES-GCM 暗号化 / 復号直後に `finally` ブロックでゼロ化されます。

### 4.7. 言語別ステータス

| 言語    | X3DH（4 DH） | Double Ratchet | OPK プール       | フィクスチャー検証済み |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | 完全         | 完全（§5）      | プール、デフォルト 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | プリミティブのみ — `aether_x25519_*`, `aether_signal_kdf_rk` | 未実装 | — | kdf_rk_basic のみ |

セッション対応のすべての 7 言語（C# + Go + TypeScript + Python + Kotlin + Swift + Rust）は、C# リファレンスコントラクトに合わせた遅延補充とロック保護された消費を持つ 100 キー FIFO OPK プールを搭載しています。C はプリミティブのみです；完全なセッション機構は `OPEN_ISSUES.md` アイテム 11 で追跡されています。

---

## 5. トランスポート層の要件

Aether はトランスポート非依存です。`ITransportService` コントラクトを満たす物理通信チャネルはすべてメッシュに参加できます。

### 5.1. ITransportService インターフェースコントラクト

すべてのトランスポート実装は以下を公開しなければなりません（MUST）：

**プロパティ：**

| プロパティ           | 型   | 説明 |
|--------------------|--------|-------------|
| `Name`             | string | 人間が読める識別子（例："BLE"、"Wi-Fi Direct"、"NearLink"） |
| `IsAvailable`      | bool   | このデバイスでトランスポートが現在使用可能かどうか |
| `MaxBandwidthBps`  | int64  | 1 秒あたりの最大スループット（バイト） |
| `MaxRangeMeters`   | int32  | 最大通信範囲（メートル） |
| `PowerCostRelative`| int32  | 相対的な消費電力（1 = 低、10 = 高） |
| `MaxConcurrentPeers` | int32 | 最大同時ピア接続数 |

**メソッド：**

| メソッド         | シグネチャ | 説明 |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | バイト配列を特定のピアに送信します。成功した場合は true を返します。 |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | ストリームをピアに送信します（大容量転送、音声、ビデオ向け）。 |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | ピアへの接続がアクティブかどうかを確認します。 |

**イベント：**

| イベント          | シグネチャ | 説明 |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | ピアからデータが到着したときに発火します。 |

### 5.2. トランスポート選択アルゴリズム

`TransportManager` は以下に基づいて各パケットに最適なトランスポートを選択します：

1. **可用性：** `IsAvailable == true` のトランスポートのみが考慮されます。
2. **ペイロードサイズ：** ペイロードサイズが `BleMaxPayloadBytes`（1,024 バイト）以下の場合、消費電力効率のために BLE が優先されます。より大きなペイロードは Wi-Fi Direct を優先します。
3. **消費電力の重み付け：** 利用可能なトランスポートの中で、低い `PowerCostRelative` 値が通常トラフィックに優先されます。高優先度パケット（SOS、音声）はこの優先設定をオーバーライドできます。
4. **ピア接続性：** トランスポートがターゲットピアへのアクティブな接続をすでに持っている場合（`IsConnected` が true を返す）、接続セットアップのオーバーヘッドを避けるために優先されます。
5. **フォールバック：** ローカルトランスポートがターゲットに到達できない場合、パケットは AetherAPI 経由のサーバーリレーにキューイングされます。

### 5.3. リファレンストランスポート

| トランスポート    | 最大帯域幅   | 最大レンジ | 消費電力コスト | 最大ピア | 注記 |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | 主要なディスカバリー + 小パケット |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | 大容量転送、ストリーミング、音声 |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon、高スループット |

**BLE ペイロード制限：** 1,024 バイト（`BleMaxPayloadBytes`）を超えるパケットは Wi-Fi Direct または NearLink に自動的にルーティングされます。BLE はディスカバリー広告、小型制御パケット（RREQ/RREP、プレゼンスビーコン）、および低帯域幅メッセージングに使用されます。

**Wi-Fi Direct** の接続タイムアウトは 10,000 ms（`WifiDirectTimeoutMs`）で、最大 8 つの同時ピア（`MaxWifiDirectPeers`）が許可されます。

---

## 6. ディスカバリープロトコル

### 6.1. BLE アドバタイジング

Aether ノードは主に BLE アドバタイジングを通じてお互いを発見します。静的識別子による永続的な追跡を防ぐため、プロトコルは 2 つのプライバシーメカニズムを採用しています：ローテーティングサービス UUID と Identity Resolving Key。

**アドバタイジングサイクル：** 2 秒スキャンオン、8 秒オフ（`BleScanOnMs`/`BleScanOffMs`）。アドバタイズ間隔は 1,000 ms（`BleAdvertiseIntervalMs`）。タイミングパターン検出を防ぐため、スキャン間隔に 0-2,000 ms のランダムジッター（`BleScanJitterMaxMs`）が追加されます。

**ピアタイムアウト：** 30 秒以内に再発見されないピアは失われたと見なされます（`PeerLost` イベント）。

### 6.2. ローテーティングサービス UUID

長期的な BLE フィンガープリンティングを防ぐため、アドバタイジングに使用されるサービス UUID は 15 分ごとにローテーションされます（`BleUuidRotationSeconds = 900`）：

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key` はノードごとに一度生成され、セキュアストレージに保存される 32 バイトのキーです。同じローテーションキーを共有するすべての Aether ノードは、所定の時間ウィンドウに対して同じ UUID を導出し、永続識別子を明かさずに相互ディスカバリーを可能にします。

非ローテーションスキームからの移行のために、90 日間の静的フォールバック UUID（`A3E7-1001-0001-0000-000000000000`）が維持されます。

### 6.3. Identity Resolving Key（IRK）

各ノードはセキュアストレージに保存された 128 ビットの Identity Resolving Key（IRK）を生成します。IRK は鍵交換中に信頼できるピアと共有されます。

**Resolvable Private Address（RPA）の生成：**

1. `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` を計算します（3 バイト）。
2. `prand[0]` の最上位 2 ビットを `01` に設定します（BLE 仕様に従った RPA フラグ）。
3. `hash = AES-128-ECB(IRK, pad(prand))` を計算します。ここで `prand` は 16 バイトのゼロパディング入力のバイト 13-15 を占めます。
4. RPA を構築します：`hash[0..2] || prand[0..2]`（合計 6 バイト）。

**RPA 解決：** ピアの IRK を持つノードは、RPA の `prand` コンポーネントからハッシュを再計算することで、観察された RPA がそのピアに属するかどうかを確認できます。解決時間は N を既知の IRK の数として約 O(N) です。100 ピアで約 0.1ms のベンチマーク。

RPA はサービス UUID と同じ 15 分サイクルでローテーションされます。

### 6.4. ジオハッシュベースの近接性

ノードはオプションで位置情報をジオハッシュとしてエンコードします。プライバシーのため、ジオハッシュは 4 文字に切り詰められ、約 39km x 20km の解像度を提供します。この粒度は以下に十分です：

- 近接ベースのチャンネルディスカバリー
- DTN エピデミックルーティング（受信者の最後に知られたジオハッシュエリアに向けて複製）
- SOS アラートの地理的コンテキスト

完全精度のジオハッシュはメッシュ上で送信されません。切り詰められた形式のみが共有され、ノードのプライバシーレベルが許可する場合にのみ共有されます（`PrivacyLevel.Full` または `PrivacyLevel.Partial`）。

---

## 7. セキュリティモデル

### 7.1. 脅威モデル

Aether は以下の敵対者の能力を仮定します：

- **受動的な盗聴：** 敵対者は電波範囲内のすべての BLE 広告とメッシュトラフィックを観察できます。
- **アクティブな注入：** 敵対者はパケットを注入、修正、またはリプレイできます。
- **Sybil 攻撃：** 敵対者は複数の偽のノード ID を作成できます。
- **選択的なサービス拒否：** 敵対者はリレーノードとしてパケットを選択的にドロップできます。

### 7.2. 保護されているもの

| プロパティ | 保護レベル | メカニズム |
|----------|-----------------|-----------|
| メッセージコンテンツ | 完全な機密性 | メッセージごとのキーを使用した AES-256-GCM（セクション 4.5） |
| 送信者 ID | 部分的 | パケットヘッダーに UHID が見える；BLE アドレスがローテーション（セクション 6） |
| 受信者 ID | 部分的 | ルーティングされたパケットに宛先 UHID が見える；ブロードキャストパケットは宛先が空 |
| ルーティングメタデータ | 最小限 | 中間ノードがソース / 宛先 UHID と TTL を見える |
| メッセージの順序 | 保護済み | シンメトリックラチェットのカウンターが並び替えを防止 |
| メッセージの整合性 | 完全 | すべてのパケット（v2）に Ed25519 署名 |

### 7.3. 攻撃耐性

**リプレイ攻撃：**
各パケットには 8 バイトの暗号学的ランダムノンスとミリ秒精度のタイムスタンプが含まれます。リレーノードは 5 分間の TTL を持つ `(SenderUhid, NonceValue)` ペアの重複排除キャッシュを維持します（`MaxPacketAgeSeconds = 300`）。同じ送信者からの重複ノンスを持つパケットはドロップされます。タイムスタンプが 5 分以上古いパケットはノンスに関わらず拒否されます。

ノンス重複排除キャッシュは 60 秒ごとにクリーンアップされます。期限切れのエントリ（5 分以上古い）は削除されます。

**中間者攻撃（MITM）：**
- Route Reply パケットは、主張された宛先ノードからの有効な Ed25519 署名を持たなければなりません。中間ノードは宛先の秘密鍵を持っていないため、RREP を偽造できません。
- プリキーバンドルには `SignedPreKey` に対する `SignedPreKeySignature`（Ed25519）が含まれ、エフェメラル ECDH キーを長期 ID にバインドします。
- セッション確立（セクション 4.4）はプリキー検証ステップを通じて、セッションを両者の ID に暗号学的にバインドします。

**Sybil 攻撃：**
- 各ノードの信頼性スコアは 50 から始まり、観察された動作に基づいて調整されます（セクション 3.5）。新しく作成された Sybil ノードには蓄積された信頼性がありません。
- 信頼性スコアが低い（0 に近づく）ノードはルート選択で優先度が下げられます。
- DTN エピデミックルーティングアルゴリズムは、ジオハッシュの近接性とリレー成功履歴を使用して複製ターゲットを選択し、真のリレー貢献なしに Sybil ノードがトラフィックを引き付けることを困難にします。

**フラッディング攻撃：**
- TTL は各ホップで減少し、TTL = 0 のパケットはドロップされます。デフォルト TTL 7 は任意のブロードキャストのブラストラジウスを制限します。
- パケット ID による RREQ の重複排除により、ブロードキャストストームによる増幅を防ぎます。重複排除キャッシュは `DeduplicationCacheSize`（デフォルト 10,000）エントリを超えるとフラッシュされます。
- SOS ブロードキャストはノードごとに 1 時間あたり 3 回に制限されています（セクション 8）。

### 7.4. キーのゼロ化

すべての中間的な暗号素材は使用直後にゼロ化されます：

- ECDH 鍵合意からの `sharedSecret`：HKDF 導出後にゼロ化。
- チェーンラチェットからの `messageKey`：AES-GCM 暗号化 / 復号後にゼロ化。
- 順序不整合復号からの `skippedKey`：使用後にゼロ化され、マップから削除。
- 導出された `RootKey`、`SendChainKey`、`RecvChainKey`：確立コンテキストからゼロ化（セッションは独自のコピーを保持）。

ゼロ化にはコンパイラーによって最適化されないことが保証された `CryptographicOperations.ZeroMemory` を使用します。

### 7.5. P-256 から Ed25519 への移行

プロトコルは ECDSA P-256 ID キー（プロトコルバージョン 1）から Ed25519（プロトコルバージョン 2）への 30 日間の移行ウィンドウをサポートします：

1. 移行期間中はプロトコルバージョン 1 パケット（未署名）が受け入れられます。
2. 署名検証は最初に Ed25519 を試みます。公開鍵が 32 バイトより長い（DER エンコードの P-256 キーを示す）場合、P-256 ECDSA 検証にフォールバックします。
3. 30 日ウィンドウ後、プロトコルバージョン 1 パケットは拒否されます。
4. 移行していないノードは新しい Ed25519 ID で再初期化しなければなりません。

### 7.6. 管轄意識

プロトコルは暗号化とメッシュネットワーキングに関するさまざまな法的要件を処理するための管轄ティアを定義します：

| ティア | 動作 | 管轄の例 |
|------|----------|-----------------------|
| 1    | 自由に運用 | 南アフリカ、ケニア、ガーナ |
| 2    | 変更された運用 | ナイジェリア、インド、EU、米国、英国 |
| 3    | メッシュのみ（高リスク） | 中国、ロシア、イラン、UAE、ミャンマー |
| 4    | 不明（デフォルトのメッシュのみ） | その他すべて |

ティア選択は機能の可用性に影響します（例：チップ / 金融機能はティア 3 で無効になる場合があります）が、暗号化を弱めません。エンドツーエンド暗号化は管轄に関わらず常に適用されます。

---

## 8. SOS ブロードキャスト

SOS メカニズムは、ユーザーが危険な状況にあり近くのメッシュピアやインターネットに同時に到達する必要がある状況のために設計されたデュアルパス緊急フラッドです。

### 8.1. ブロードキャストパラメーター

| パラメーター | 値 | 説明 |
|-----------|-------|-------------|
| TTL       | 15    | 通常のデフォルト（7）の 2 倍、より広い伝播を保証 |
| Priority  | 999   | 最高優先度；リレーキュー内の他のすべてのトラフィックを先取り |
| レート制限| 3 / 時| 乱用防止のためのノードごとの制限 |
| 宛先| 空 | すべてのピアにブロードキャスト（特定の宛先なし） |

### 8.2. フラッドアルゴリズム

1. 発信元は `Type = SosBroadcast`、`TTL = 15`、`Priority = 999`、空の `DestinationUhid` で SOS パケットを構築します。
2. ペイロードは JSON エンコードされ、以下を含みます：
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
3. **デュアルパスディスパッチ：** SOS は同時に以下を介して送信されます：
   - **メッシュフラッド：** 利用可能なすべてのトランスポートを介して接続されているすべてのピアにブロードキャスト。
   - **API 呼び出し：** サーバー側の配信と PanikAPI（SMS / メールディスパッチ）へのブリッジのために AetherAPI に送信。
4. 両方のパスはお互いに対してファイアアンドフォーゲットです。API 呼び出しが失敗した場合、メッシュフラッドは独立して続行します。

### 8.3. リレー動作

ノードが SOS パケットを受信した場合：

1. パケット `Id` による重複排除チェック。既に見ている場合はサイレントにドロップ。
2. ペイロードをデシリアライズし、ローカル UI のために `SosReceived` イベントを発生させます。
3. アクティブなアラートリストにアラートを追加します。
4. `TTL > 1` の場合、TTL を減少させ、ルーティングテーブルの状態に関わらず**すべてのピアに再ブロードキャスト**します。SOS パケットは通常のルーティングをバイパスします — 無条件にフラッドします。

### 8.4. レート制限

各ノードは最近のブロードキャストタイムスタンプのスライディングウィンドウを維持します。新しい SOS を開始する前に：

1. キューから 1 時間以上前のエントリを削除します。
2. キューに 3 つ以上のエントリ（`MaxSosBroadcastsPerHour`）が含まれている場合、ブロードキャストは拒否されます。
3. 正常なディスパッチ時に、現在のタイムスタンプをエンキューします。

レート制限は SOS ブロードキャストの発信にのみ適用され、リレーには適用されません。

### 8.5. SOS-PanikAPI ブリッジ

メッシュ経由で受信した SOS ブロードキャストは、従来の緊急対応（連絡先への SMS、メールアラート）のために PanikAPI に転送できます。逆に、PanikAPI の緊急セッションはコミュニティ意識のためにメッシュにブロードキャストできます。ループ防止は、ソースのマーキング（`direct` vs `mesh_forward`）とメッシュブロードキャスト上の `internet_forwarded` フラグによって達成されます。

---

## 9. DTN ストアアンドフォワード

Delay-Tolerant Networking（DTN）サブシステムは、送信者と受信者の間にエンドツーエンドのパスが存在しない場合にメッセージを配信します。バンドルは中間ノードに保存され、接続が変化するにつれて日和見的に転送されます。

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

### 9.2. バンドルのライフサイクル

1. **作成：** 送信者は暗号化されたペイロード（受信者との Signal セッションで暗号化）でバンドルを作成します。`Status = Pending`、`CopyCount = 1`。
2. **即時配信の試み：** 送信者はまず直接メッシュルーティング（RREQ / RREP）を試みます。ルートが存在する場合、バンドルは即座に配信され、`Status` は `Delivered` に遷移します。
3. **サーバーリレーの試み：** メッシュルーティングが失敗した場合、送信者は AetherAPI を通じたリレーを試みます。サーバーが受信者に到達できる（またはメッセージをキューに入れられる）場合、配信は成功します。
4. **ストアアンドフォワード：** メッシュとサーバーリレーの両方が失敗した場合、バンドルは次の配信スキャンを待って（`Pending` ステータスで）ローカルストレージに残ります。

### 9.3. 配信スキャン

定期的なスキャンが 60 秒ごとに実行されます（`DtnScanIntervalSeconds`）：

1. SQLite（真実のソース）からすべての保留中のバンドルを読み込みます。
2. 各保留中のバンドルに対して：
   a. 受信者へのメッシュルートを試みます。
   b. サーバーリレーを試みます。
   c. 両方が失敗し、`CopyCount < MaxCopies` の場合、エピデミック複製を試みます（セクション 9.4）。
3. 期限切れのバンドルを削除します（`ExpiresAt <= now`）。

### 9.4. エピデミックルーティング

直接配信とサーバーリレーの両方が失敗した場合、バンドルはエピデミックルーティングを使用して近くのピアに複製されます：

1. `EpidemicRoutingService` は現在のピアリストから複製ターゲットを選択します。
2. ターゲット選択は以下を考慮します：
   - **ジオハッシュの近接性：** ジオハッシュが受信者の最後に知られたジオハッシュに近いピアが優先されます。
   - **リレー履歴：** 信頼性スコアが高いピアが優先されます。
   - **コピーバジェット：** `CopyCount >= MaxCopies`（デフォルト：3）になると複製は停止します。
3. 各複製は選択されたピアに `DtnBundle` パケットを送信します。
4. 受信時に、ピアの DTN サービスは `AcceptCustodyAsync` を呼び出します。

### 9.5. カストディ転送

別のノード向けの DTN バンドルをノードが受信した場合：

1. **容量確認：** ノードは現在のバンドルカウントを `DtnMaxBundlesPerNode`（50）と照合します。容量に達している場合、カストディは拒否されます。
2. **受諾：** バンドルのステータスは `InCustody` に設定され、ホップカウントが増加し、バンドルは SQLite に永続化されます。
3. **カストディ記録：** 転送を文書化する `CustodyRecord` が作成されます（送信元、宛先、タイムスタンプ）。
4. **コピーカウントの増加：** バンドルの `CopyCount` は永続ストレージで増加します。
5. **確認応答：** `Accepted = true` の `DtnCustodyAck` パケットが転送ノードに返送されます。
6. 受諾ノードは後続のスキャンで配信を試みる責任を持ちます。

### 9.6. 配信レシート

意図した受信者が DTN バンドルを受信した場合：

1. バンドルのステータスは `Delivered` に更新されます。
2. `DtnDeliveryReceipt` がメッシュルーティング（サーバーリレーフォールバックを伴う）を介して元の送信者に返送されます：
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. レシートを受信すると、送信者はバンドルをストアから削除し `BundleDelivered` イベントを発生させます。
4. レシートはアナリティクスのために AetherAPI にも同期されます。

### 9.7. バンドルの有効期限

- デフォルトのバンドル TTL は 72 時間（`DtnBundleTtlHours`）です。
- 期限切れのバンドルは定期的な配信スキャン中にクリーンアップされます。
- `Expired` または `Delivered` ステータスのバンドルはインメモリキャッシュと SQLite の両方から削除されます。

### 9.8. 容量制限

| パラメーター               | デフォルト | 説明 |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | バンドルの最大有効期間 |
| `DtnMaxCopies`          | 3       | ネットワーク全体のバンドルごとの最大コピー数 |
| `DtnMaxBundlesPerNode`  | 50      | 単一ノードが保持する最大バンドル数 |
| `DtnScanIntervalSeconds`| 60      | 配信スキャンの頻度 |

---

## 10. ビデオストリーミング

> **2026-05-05 時点のステータス — 設計 + C# スキャフォールディング、配送コーデックパイプラインなし。** パケット型 `StreamAnnounce`（11）、`StreamSegment`（12）、`StreamSubscribe`（13）、`StreamUnsubscribe`（14）、`VideoCall`（27）、`VideoSignaling`（28）、`VideoFrame`（31）、`ScreenShare`（32）はワイヤー定義され、クロス言語フィクスチャーコーパスでラウンドトリップします。C# `Aether.Streaming` モジュールはインターフェース、モデル、およびスケルトンサービス（`StreamingService`、`VideoCallService`、`WatchTogetherService`）を搭載し、ルーティング / DI シームとユニキャストセグメントファンアウトを配線しています — しかし実際のビデオエンコード / デコードはバインドされていません。他の 7 言語はワイヤー型のみです。`docs/adaptive-secure-streaming-spec.md` の前方設計ドキュメントがターゲットアーキテクチャーです。以下の散文をこれらのサービスが実装するものの仕様として扱ってください；本番対応のギャップについては `OPEN_ISSUES.md` を参照してください。

Aether は 3 つのビデオモードをサポートします：ピアツーピアビデオ通話、グループビデオ（動的トポロジーで無制限の参加者）、およびライブブロードキャスト。すべてのビデオフレームは Signal プロトコルで暗号化され、Ed25519 で署名されています。

### 10.1. トランスポートケイパビリティマトリクス

ビデオ通話を開始する前に、発信元はトランスポート層に問い合わせて、ピアへの最良の接続を確認します。トランスポートはどの品質のビデオが可能かを決定します：

| トランスポート | ビデオサポート | 最大解像度 | 推奨コーデック | 最大ビットレート | Watch Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | なし（音声のみ） | — | — | 64 Kbps | 同期パケットのみ |
| NearLink | ライト | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | フル | 1080p | H.264 | 3000 Kbps | すべてのモード |
| Internet | フル | 720p | H.264 | 1500 Kbps | すべてのモード |
| CircleLink | なし（音声のみ） | — | — | 64 Kbps | 同期パケットのみ |

利用可能な唯一のトランスポートが BLE または CircleLink の場合、ビデオ通話サービスは自動的に音声通話にダウングレードします。

### 10.2. ビデオコーデック

| 列挙値 | コーデック | ユースケース |
|------------|-------|----------|
| 0 | H.264 | デフォルト。広くサポートされ、良好な圧縮。 |
| 1 | H.265 | より優れた圧縮。NearLink（帯域幅制約）で使用。 |
| 2 | VP8 | ロイヤリティフリーの代替。 |

### 10.3. ビデオ解像度

| 列挙値 | 解像度 | 典型的なビットレート |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps（Opus） |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P ビデオ通話フロー

1. **ケイパビリティ確認**：発信元は `GetVideoCapabilityAsync(peerUhid)` に問い合わせ、最良のトランスポート、最大解像度、推奨コーデックを決定します。
2. **オファー**：発信元は `SignalType = Offer`、優先コーデック、最大解像度、最大ビットレートを含む `VideoSignaling` パケット（型 28）を送信します。
3. **アンサー / リジェクト**：着信側は `SignalType = Answer`（コーデックを最小公倍数にネゴシエート）または `SignalType = Reject` で応答します。
4. **アクティブ通話**：両ノードは H.264/H.265/VP8 NAL ユニットを含む `VideoCall` パケット（型 27）を交換します。各フレームにはジッターバッファー順序付けのためのシーケンス番号とキーフレームフラグが含まれます。
5. **スクリーンシェア**：どちらの当事者もスクリーン共有を切り替えられます。`SignalType = ScreenShareStart/Stop` の `VideoSignaling` はピアに通知します。スクリーンシェアフレームは `PacketType.ScreenShare`（型 32）を使用しますが、同じ処理パイプラインを使用します。
6. **通話終了**：どちらの当事者も `SignalType = Bye` の `VideoSignaling` を送信します。

すべてのシグナリングおよびフレームペイロードは Signal プロトコル（X3DH セッション）で暗号化されます。暗号化されたペイロードは `MeshPacket.Payload` フィールド内に JSON エンコードされた `EncryptedPayload` としてシリアライズされます。

### 10.5. ビデオ通話ステートマシン

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

ステート：`Initiating(0)`、`Ringing(1)`、`Active(2)`、`OnHold(3)`、`Ended(4)`、`Failed(5)`、`Rejected(6)`。

### 10.6. グループビデオ

グループビデオセッションは無制限の参加者をサポートします。トポロジーは参加者数に基づいて動的に選択されます：

- **FullMesh**（2-3 参加者）：各参加者が他のすべての参加者に 1 つのストリームを送信します。シンプルで低レイテンシー。
- **SFU**（4 人以上の参加者、しきい値：`SfuThresholdParticipants = 4`）：1 つのノードが SFU リレーとして選出されます。各参加者はリレーに 1 つのストリームを送信し、リレーはそれをすべての他者に配布します。リレーノードはインセンティブ層を通じてチップを獲得します。

トポロジーの切り替えは自動です：4 人目の参加者が参加すると、セッションは FullMesh から SFU に遷移します。参加者が離れて数が 4 未満になると、元に戻ります。

グループビデオフレームは `PacketType.VideoFrame`（型 31）を使用します。SFU モードでは、フレームはリレーノードの UHID に送信され、リレーノードがそれらを再ブロードキャストします。

### 10.7. ジッターバッファー

ビデオジッターバッファーは音声ジッターバッファー（20ms Opus フレームを処理）とは独立して動作します：

- **範囲**：最小 60ms、最大 500ms。
- **適応的な深さ**：指数移動平均（EMA）を通じてフレーム間ジッターを追跡します。バッファー深さ = ジッター推定値の 2 倍、[60, 500] ms にクランプ。
- **キーフレーム認識ドロッピング**：バッファーがオーバーフローした場合、非キーフレーム（P/B フレーム）が最初にドロップされます。I フレーム（キーフレーム）はドロップされません — デコーダーリカバリーに必要です。
- **ギャップ処理**：シーケンスギャップが検出された場合、バッファーは無期限に待機するのではなく、次の利用可能なキーフレームにスキップします。

### 10.8. ビデオシグナリング型

| 列挙値 | 型 | 説明 |
|------------|------|-------------|
| 0 | Offer | コーデック / 解像度優先設定を伴うビデオ通話開始 |
| 1 | Answer | ネゴシエートされたパラメーターを伴う通話受諾 |
| 2 | Reject | 通話拒否 |
| 3 | Bye | 通話終了 |
| 4 | Upgrade | より高い品質のリクエスト（例：トランスポートが改善された） |
| 5 | Downgrade | より低い品質のリクエスト（例：帯域幅低下） |
| 6 | ScreenShareStart | ピアがスクリーン共有を開始 |
| 7 | ScreenShareStop | ピアがスクリーン共有を停止 |

### 10.9. 暗号化モデル

| モード | 暗号化 | 鍵配布 |
|------|-----------|-----------------|
| P2P ビデオ通話 | フレームごとの Signal プロトコル | X3DH 鍵合意 |
| グループビデオ | グループチャンネルキー（AES-GCM） | セッション作成時に Signal プロトコルで配布 |
| スクリーンシェア | 親通話モードと同じ | ビデオ通話セッションから継承 |

---

## 11. Watch Together

> **2026-05-05 時点のステータス — 設計 + C# スキャフォールディング、§10 と同じ成熟度。** パケット型 `WatchSync`（29）、`WatchReaction`（30）、`WatchChunkRequest`（33）、`TorrentMetadata`（34）はワイヤー定義され、フィクスチャーテスト済みです。`Aether.Streaming.WatchTogetherService` はコーディネーションスケルトン（セッション状態、`IMeshSender` 経由の同期コマンド伝播、RTT 補正ヘルパー）を提供します；BitTorrent インジェスト、ChipIn SDPKT 決済、チャンクフェッチはどの言語でも実装されていません。以下の散文をターゲットプロトコルとして扱ってください；`docs/adaptive-secure-streaming-spec.md` の前方設計ドキュメントが同じ内容をより詳しく説明しています。

Watch Together は、メッシュピアのグループにわたって同期されたメディア再生を可能にします。ホストは再生（再生、一時停止、シーク、速度）の独占的な制御を持ちます。同期コマンドには RTT 補正のためのウォールクロックタイムスタンプが含まれます。

### 11.1. Watch モード

| 列挙値 | モード | データフロー | トランスポート要件 |
|------------|------|-----------|----------------------|
| 0 | SharedFile | 同期パケットのみ（各 100 バイト未満） | 任意（BLE で動作） |
| 1 | StreamFromHost | P2P チャンク転送（P2pContentService を再利用） | WiFi Direct またはインターネット |
| 2 | BitTorrent | ゲートウェイノード経由のメッシュ + 外部スウォーム | WiFi Direct またはインターネット |

### 11.2. SharedFile モード

両方の参加者が同じファイルを持っています（SHA-256 コンテンツハッシュでマッチング）。`WatchSync` パケットのみが交換されます。これが最も帯域幅効率の高いモードで、BLE で動作します。

1. ホストは `contentHash`（ファイルの SHA-256）でウォッチセッションを作成します。
2. 参加者が参加し、プレーヤーがロードされると `IsReady = true` を報告します。
3. すべての参加者がレディを報告するとセッションが開始されます。
4. ホストは `WatchSync` パケット（型 29）として再生 / 一時停止 / シーク / 速度コマンドを送信します。
5. 受信者は RTT 補正を適用します：`adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`。

### 11.3. StreamFromHost モード

ホストのみがファイルを持っています。ホストは `ContentManifest`（P2P コンテンツシステムを再利用）を生成し、参加者はメッシュ経由でチャンクをダウンロードします。

- チャンク選択は `SequentialFromPosition` ストラテジーを使用します（`RarestFirst` ではない）：現在の再生位置より先のチャンクを優先し、次にシードのためにバックフィルします。
- バッファーターゲット：30 秒先（`WatchTogetherBufferAheadSeconds`）。
- 自動一時停止：任意の参加者のバッファーが 10 秒（`WatchTogetherMinBufferSeconds`）を下回ると、セッションはすべての参加者を `BufferUnderrun` 同期コマンドで自動一時停止します。すべての参加者が十分なバッファーを持つと（`BufferReady`）再生が再開されます。
- 視聴者がチャンクをダウンロードするにつれて、他の視聴者のシーダーになります（メッシュ内の BitTorrent スタイルのスウォーミング）。

### 11.4. BitTorrent モード

参加者がグループチャットに `.torrent` ファイルまたはマグネットリンクを共有します。`TorrentMetadata` パケット（型 34）はすべてのセッション参加者にトレント情報を配布します。

**メッシュ-スウォームブリッジ：**
- ゲートウェイノード（インターネットを持つノード）は外部 BitTorrent スウォームからピースをダウンロードします。
- ゲートウェイノードはダウンロードしたピースをメッシュ配布のために再暗号化し、メッシュピアにシードします。
- インターネットのないメッシュピアはゲートウェイノードとお互いからピースを受け取ります。
- P2P コンテンツエンジンは BitTorrent のピースモデルと Aether のチャンクモデルの間で変換します。

十分なコンテンツがバッファリングされると、SharedFile モードと同じ同期プロトコルを使用して Watch Together 再生が始まります。

### 11.5. Watch セッションステートマシン

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

ステート：`WaitingForReady(0)`、`Buffering(1)`、`Playing(2)`、`Paused(3)`、`Ended(4)`。

### 11.6. 同期コマンド型

| 列挙値 | 型 | 説明 |
|------------|------|-------------|
| 0 | Play | 指定された位置で再生を再開 |
| 1 | Pause | 指定された位置で一時停止 |
| 2 | Seek | 指定された位置にジャンプ |
| 3 | Speed | 再生速度を変更 |
| 4 | BufferUnderrun | 自動一時停止 — 参加者のバッファーが危機的に低い |
| 5 | BufferReady | 再開 — すべての参加者が十分なバッファーを持つ |

### 11.7. RTT 補正

同期コマンドには `WallClockMs` フィールド（Unix エポックミリ秒）が含まれます。受信者が同期コマンドを処理する際：

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Play および BufferReady コマンドの場合：`adjustedPosition = commandPosition + networkDelay`
4. Pause および Seek コマンドの場合：位置は正確に適用されます（再生が停止 / ジャンプするため調整は不要）。

これにより、すべての参加者がネットワーク RTT の半分以内で同期されます。

### 11.8. リアクション

参加者は再生中にコンテンツにリアクションできます：

- **絵文字リアクション**：`Type = Emoji`、絵文字文字列、リアクション時のメディア位置を持つ `WatchReaction` パケット（型 30）。
- **音声コメント**：Opus エンコードの音声データ（最大 10 秒）を持つ `Type = VoiceComment` の `WatchReaction` パケット。音声データはリアクションの `VoiceData` フィールドに含まれます。

リアクションはすべてのセッション参加者にブロードキャストされます。メディア位置にタイムスタンプが付けられ、再生同期表示が可能です。

### 11.9. ChipIn — グループコンテンツ取得

ChipIn はグループメンバーが資金（ZAR、LedgerAPI を通じた SDPKT ウォレットで決済）を共同出資してグループ視聴のためにコンテンツを集合的に取得できるようにします。

**ステートマシン：**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

ステート：`Collecting(0)`、`Funded(1)`、`Purchasing(2)`、`Acquired(3)`、`Failed(4)`、`Refunded(5)`。

**フロー：**
1. 発起人はターゲット金額とコンテンツ説明で `ChipInPool` を作成します。
2. 参加者は SDPKT ウォレットトランザクションで金額を拠出します。
3. `CollectedAmount >= TargetAmount` になると、ステートは `Funded` に遷移します。
4. システムはコンテンツを取得します（例：BitTorrent ダウンロードを開始）。
5. コンテンツが利用可能になると、ステートは `Acquired` に遷移し、Watch Together が始められます。

各拠出はオーディットトレイルのために SDPKT トランザクション ID とともに記録されます。

### 11.10. 暗号化モデル

| モード | 暗号化 | 鍵配布 |
|------|-----------|-----------------|
| Watch 同期コマンド | チャンネル / 会話キー | 既存の Signal プロトコルセッション |
| コンテンツチャンク（StreamFromHost） | マニフェストごとのコンテンツキー | Signal プロトコルで配布 |
| BitTorrent ピース | インジェスト時に再暗号化 | ゲートウェイがスウォームから平文をダウンロードし、メッシュ用に暗号化 |
| Watch リアクション | セッションキー | 会話キーから導出 |

### 11.11. フィーチャーフラグ

すべてのビデオおよび Watch Together 機能はフィーチャーフラグの背後にゲートされています（すべてデフォルトで無効）：

| フラグ | 親 | 説明 |
|------|--------|-------------|
| AETHER_VIDEO_CALL | AETHER_VOICE | P2P およびグループビデオ通話 |
| AETHER_VIDEO_GROUP | AETHER_VIDEO_CALL | マルチパーティビデオセッション |
| AETHER_SCREEN_SHARE | AETHER_VIDEO_CALL | ビデオ通話でのスクリーン共有 |
| AETHER_WATCH_TOGETHER | AETHER_CONTENT_P2P | 同期メディア再生 |
| AETHER_WATCH_REACTIONS | AETHER_WATCH_TOGETHER | 絵文字および音声リアクション |
| AETHER_TORRENT_INGEST | AETHER_CONTENT_P2P | メッシュ配布のための BitTorrent ファイル受け入れ |

フィーチャーフラグには親の依存関係があります：子フラグは親も有効になっている場合にのみ有効にできます。これにより段階的なロールアウトが可能です。

---

## 付録 A：定数リファレンス

すべてのプロトコル定数は `ProtocolConstants` に定義されており、参照のためにここに再掲します：

### ルーティング
| 定数              | 値  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE ディスカバリー
| 定数                  | 値  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### セキュリティ
| 定数                  | 値  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| 定数                   | 値 |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| 定数                  | 値  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### トランスポート
| 定数                  | 値   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### ハートビート
| 定数                      | 値 |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### プレゼンス
| 定数                          | 値 |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### 音声
| 定数                  | 値 |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### ストリーミング
| 定数                    | 値 |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### ビデオ
| 定数                       | 値 |
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

## 付録 B：用語集

| 用語 | 定義 |
|------|------------|
| **UHID** | ユニバーサルハードウェア識別子。デバイスの ID と暗号キーから導出される、メッシュノードを識別する一意の文字列。 |
| **RREQ** | ルートリクエスト。宛先ノードへのパスを発見するために使用されるブロードキャストパケット。 |
| **RREP** | ルートリプライ。RREQ によって確立されたリバースルートに沿ってユニキャストで返送されるパケット。 |
| **IRK** | Identity Resolving Key。BLE のリゾルバブルプライベートアドレスを生成および解決するために使用される 128 ビットキー。 |
| **RPA** | リゾルバブルプライベートアドレス。定期的にローテーションしますが、送信者の IRK を保持するピアが解決できる 6 バイトの BLE アドレス。 |
| **X3DH** | Extended Triple Diffie-Hellman。非同期セッション確立を可能にする鍵合意プロトコル。 |
| **DTN** | Delay-Tolerant Networking（遅延耐性ネットワーク）。断続的な接続を持つ環境向けのストアアンドフォワードパラダイム。 |
| **ゲートウェイ** | インターネット接続を持ち、メッシュトラフィックを IP ベースのサービスとの間でブリッジするメッシュノード。 |
| **HKDF** | HMAC ベースの鍵導出関数。単一の共有シークレットから複数のキーを導出するために使用されます。 |
| **プリキーバンドル** | 受信者がオンラインでなくても、送信者が暗号化セッションを確立できるように公開されたキーのセット。 |
| **SFU** | Selective Forwarding Unit（選択的転送ユニット）。各送信者から 1 つのビデオストリームを受け取り、他のすべての参加者に配布するリレーノード。ノードごとのアップロード帯域幅を削減します。 |
| **ChipIn** | 参加者が SDPKT 資金を共同出資してグループ視聴のためにコンテンツを集合的に取得するグループファンディングメカニズム。 |
| **NAL** | ネットワーク抽象化層。ビデオフレームをパケット化するために H.264 および H.265 コーデックが使用するカプセル化フォーマット。 |

---

## 付録 C：参考文献

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
