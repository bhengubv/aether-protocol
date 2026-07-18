# AetherNet — オフラインファーストのメッシュネットワーキングプロトコル

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNetは、オープンソースのMITライセンスのメッシュネットワーキングプロトコル**で、近くにいる人へメッセージ、ファイル、音声、ビデオを送信できます — **インターネットなし、サーバーなし、アカウント登録なし**で。デバイスはBluetooth、Wi-Fi Direct、NearLink、LoRaを介して直接接続し、受信者が範囲外にいる場合、メッセージは他のデバイスを経由してホップし、ルートが見つかるまで最大72時間待機します。**8つのプログラミング言語でバイト単位で完全に同一の実装**を同梱しています — C#、Rust、TypeScript、Python、Go、Kotlin、Swift、C。

近くにいる人とファイル、メッセージ、ストリームを共有できます。Wi-Fi不要。モバイルデータ不要。アカウント登録不要。AirDropに似ていますが、あらゆるプラットフォームのすべての人と使えます。

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **1つのプロトコル、8つの言語、ワイヤー上では同一。** Aetherは**C#、Rust、TypeScript、Python、Go、Kotlin、Swift、C**で実装されており、すべてにおいてどのパケットもバイト単位で完全に同一で、すべての実装がバイト単位で一致しなければならない共有クロス言語フィクスチャコーパスによって保証されています。8言語のどれでノードを構築しても、他のすべてと相互運用できます。このREADMEは20の人間の言語でも利用できます（上のリンク）。

## わかりやすく言うと

**AetherNetは、スマートフォンやノートパソコンが互いに直接通信することを可能にします — インターネットも、電話会社も、アカウントも不要で。** 周りの人がアプリを持っていれば、すべてのスマートフォンにすでに内蔵されている近距離無線（BluetoothとWi-Fi）だけを使って、メッセージを送ったり、写真や大きなファイルを送ったり、音声通話やビデオ通話をしたり、ライブ配信を共有したりできます。直接届かないほど遠くに誰かがいる場合、あなたのメッセージは届くまで静かに1台のスマートフォンから次のスマートフォンへとホップします — 必要であれば経路を最大3日間待ちます。世界の大規模な公開ファイル共有ネットワーク（Linuxやゲームアップデートのような合法的なダウンロードを支えるのと同じ技術）にまで手を伸ばし、ファイルを取得して、インターネットをまったく持たない友人へと内側に運ぶことさえできます。

すべてはエンドツーエンドでスクランブルされるため、あなたが話している相手だけが読むことができ、それを中継するスマートフォンは読めません。誰でも使ったり検査したりできるように**無料でオープン**であり、8つのプログラミング言語で8回にわたって書かれているため、ほぼあらゆるデバイスで動作します。

**どのくらい完成しているの？** ネットワークの「頭脳」— メッセージフォーマット、暗号化、ルーティング、ファイル共有 — は、全8言語にわたって構築され、機械的に検証されています。まだ実地でのテストが必要なのは、2台の物理的なスマートフォン間で実際の無線が電波越しに互いに通信する部分です; そのハードウェアのステップが残されており、私たちはそれを `OPEN_ISSUES.md` で公開して追跡しています。以下はすべて、同じ話をより詳しく述べたものです。

## 何ができるの？

**データ通信を使わずに講義ノートを共有する。**

勉強グループにいます。誰かが過去問をスマートフォンに持っています。Aetherはホットスポットなし、WhatsAppグループなし、ファイルサイズ制限なしで、Bluetooth経由で直接あなたのデバイスに送信します。グループ内の誰かが範囲外にいる場合、ファイルは他のデバイスを経由してホップし、届くまで転送されます。必要であれば、最大72時間メッセージはルートを待ちます。

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**周囲で何が起きているかを知る。**

キャンパスのイベントやフェスティバルにいます。AetherはBluetoothとWiFi Directを通じて近くのデバイスを発見します — アプリのフィードも、アルゴリズムも不要。宣伝されているものではなく、実際に周りにあるものが見えます。

**電波がないときにSOSを送る。**

スマートフォンに電波がありません。Aetherは緊急メッセージを範囲内のすべてのデバイスにブロードキャストし、そのデバイスがさらに転送します。携帯電話の基地局は不要です。

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**プライベートグループチャンネルを作る。**

寮のフロア、サークル、プロジェクトチーム向けのチャンネル。確認済みのメンバーだけがメッセージを読み書きできます。サーバーには会話が保存されません。

**近くの人に物を売る。**

教科書を出品する。メッシュの範囲内を歩いている人に表示されます。マーケットプレイスのアカウント不要、出品手数料不要 — ただ近くにいるだけ。

**メッシュを越えて一緒に映画を観る。**

グループで映画鑑賞します。誰かがファイルを持っています。Aetherはすべてのデバイスで再生を同期します — 再生、一時停止、シーク — すべて同時に。ファイルを持っていない人がいる場合、メッシュがP2Pストリームとしてリアルタイムで配布します。誰も持っていない場合は、SDPKTを通じてグループ全員で購入できます。

**インターネット全体がすでに共有しているやり方で大きなファイルを手に入れる。**

BitTorrentは、世界の合法的なファイル共有の大きな部分を支える技術です — Linuxのリリース、ゲームアップデート、Internet Archive。Aetherはいまやそれを*本物として*話します: Aetherノードは通常のBitTorrentスウォームに参加し、中央サーバーなしで群衆から直接ファイルを取得できます。そしてデータを持たない人々のためのひねりがここにあります — インターネットを*持っている*1つのAetherノードがトレントを取得し、**オフラインメッシュ越しに再共有**できるため、完全にオフラインの友人でも、BluetoothとWi-Fiを通じて、ホップごとにそのファイルを受け取れます。世界最大のファイル共有ネットワークが、インターネットの届かない人々に届きます。

## 仕組み

デバイスはBluetooth、WiFi Direct、またはNearLinkを使って直接通信します。インターネット接続不要、サーバー不要、中央インフラ不要。

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ End-to-End Encrypted (Signal Protocol) ======>|
       |                                                             |
       |  No internet. No servers. No ISP. Just devices talking.     |
```

メッセージが宛先に直接届かない場合、他のデバイスを経由してホップします。中継デバイスは運ぶ内容を読み取れません — すべてのメッセージはAES-256-GCMで暗号化されています。すべてのパケットはEd25519アイデンティティキーで署名され、偽造パケットはネットワークによって破棄されます。

> **セキュリティ成熟度に関する注意（リリース前に必読）:** 本物のX3DH（4つのX25519 DH）、完全なSignal Double Ratchet（受信時のDHローテーションステップ、KDF_RK、0x01/0x02チェーンラチェット）、およびワンタイムプレキープール（デフォルト100 OPK、FIFO、ロック保護）は**全8言語**で実装され、`fixtures/signal/` 配下の共有クロス言語フィクスチャコーパスに固定されています。残る未解決事項は実際のBLEハードウェアでの物理的なRF立ち上げのみです（`OPEN_ISSUES.md` で追跡中）。

アカウントなし、電話番号なし、メールアドレスなし。キーペアを生成するだけでネットワークに参加できます。

```
  ┌─────────────────────────────────┐
  │         Your Application        │
  ├─────────────────────────────────┤
  │ Messaging · Streaming · Voice   │
  │ Video · Watch Together          │
  ├─────────────────────────────────┤
  │  Security: AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
  ├─────────────────────────────────┤
  │  Routing: AODV + DTN            │
  ├─────────────────────────────────┤
  │  Transport: BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**ルーティング** — 署名されたルート応答を持つAODV。すべてのルート応答は宛先のEd25519キーで署名されるため、どのデバイスも存在しない宛先を偽ることができません。

**ストアアンドフォワード** — ライブルートがない場合、パケットはルートが開くまで最大72時間保持されます。

**トランスポート選択** — プロトコルはパケットごとに適切なトランスポートを選択します。小さな制御メッセージはBLEで送信されます。大量転送はWiFi Directを使用します。利用可能な場合はNearLinkを使用します。

**音声、ビデオ、ストリーミング** — コーデックネゴシエーション（H.264/H.265/VP8）付きのビデオ通話、トランスポート対応の品質選択、自動SFUリレーによるグループビデオ、RTT補正付きの同期ウォッチトゥゲザー、アダプティブビットレートストリーミング。

**リプレイ保護** — 5分タイムスタンプ鮮度ウィンドウを持つノンス重複排除。

## 手に入るもの — すべてのサービスを、すべての言語で

Aetherは単なるトランスポートではありません。プロトコルが予約するすべてのパケットタイプは、いまや**全8言語で実際に動作するサービス**であり、そのどれもが**バイト同一のワイヤーパケット**にシリアライズされます — Goノードが構築したパケットは、Swift、Rust、C、Python、TypeScript、Kotlin、C#のノードが変更なしに復号します。各サービスは `fixtures/<service>/` 配下の共有クロス言語フィクスチャに固定され、言語ごとのユニットテストで検証されており、SwiftとCはさらにmacOSビルドサーバー上で確認されています。

| 機能 | 内容 | パケットタイプ | フィクスチャ | 8/8 |
|---|---|:-:|---|:-:|
| **プレゼンスビーコン & クエリ** | 「ここにいます」を告知し「誰が周りにいる?」と尋ねる — **ローテーションするキー導出の一時ID**（あなたの本当のアイデンティティではない）と粗いジオハッシュで | 21, 22 | `fixtures/presence/` | ✅ |
| **ハートビート** | リンクされたピア間の軽量な生存キープアライブ | 10 | `fixtures/heartbeat/` | ✅ |
| **プロフィール同期** | 署名されたプロフィールカードをメッシュ越しにピアと交換 | 23 | `fixtures/profiles/` | ✅ |
| **一時ID告知** | 現在のローテーションするルーティングIDを友人にプライベートに伝え、ローテーション後も引き続き到達できるようにする | 56 | `fixtures/erid/` | ✅ |
| **プレキー交換** | Signalプレキーバンドルをメッシュ越しに要求・配信し、一度も会ったことのない相手とのエンドツーエンドセッションをブートストラップする | 25, 26 | `fixtures/prekey/` | ✅ |
| **チャンネル** | プライベートなメンバー限定グループチャンネルへの署名済みメッセージ | 7 | `fixtures/channels/` | ✅ |
| **プッシュトゥトーク** | トランシーバー方式の音声フレーム（不透明なエンコード済み音声ペイロード） | 15 | `fixtures/media/` | ✅ |
| **画面共有** | 画面共有ビデオフレーム（不透明なエンコード済みビデオペイロード） | 32 | `fixtures/media/` | ✅ |
| **通話制御** | 音声・ビデオ通話の呼び出し / 応答 / 拒否 / 切断シグナリング | 27 | `fixtures/videocall/` | ✅ |
| **SOS確認応答** | 緊急ブロードキャストが受信されたことを送信者に確認する | 6 | `fixtures/sos/` | ✅ |
| **スペースブレッドクラム** | 「周りに何があるか」レイヤー向けの位置タグ付き発見クラム | 40 | `fixtures/space/` | ✅ |
| **フォージ告知** | 派生/フォージされたコンテンツアーティファクトをメッシュに広告する | 41 | `fixtures/forge/` | ✅ |
| **ボールトシャード要求** | 消失訂正符号化されたストレージシャードを取得する（N個中どのK個のシャードでもファイルを再構築できる） | 42 | `fixtures/vaultshard/` | ✅ |
| **帯域幅測定** | リンクスループットをプローブ / ack / ゴシップし、メッシュが最も太いパイプ経由でルーティングするようにする（ABMF） | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

これらは、すでに完成している**メッセージング、1対1およびグループ音声、ビデオ通話、ライブストリーミング、ウォッチトゥゲザー、AODVルーティング、DTNストアアンドフォワード、SOSフラッド**サービスの上に載っています — こちらも全8言語で実装されています。

> **ここでの「構築済み」の正確な意味。** 各サービスはワイヤーパケットを生成・処理し、適切なイベントを発火し、言語ファミリー全体が一致しなければならないバイトレベルのフィクスチャに固定されています。アプリケーションは、このサービスをそのSignalセッション、ルーティングテーブル、ローカル状態に接続します。これはプロトコル層です — コード、テスト、クロス言語バイトフィクスチャで証明されており、他のすべてと同じ正直なRF基盤の上にあります: 最終的に無線に乗るあらゆるパスは、`OPEN_ISSUES.md` で追跡されているハードウェア立ち上げまでは実地未検証です。

## BitTorrent — 本物、そしてメッシュへの橋渡し

Aetherはいまや**本物の、相互運用可能なBitTorrent実装**を含んでいます — 本物のトレントクライアントが使う実際のプロトコルであり、見た目だけの模倣ではありません。そのためAetherノードは通常のスウォームに参加し、中間にサーバーを置くことなく、インターネット上の見知らぬ人々とファイルのピースを交換できます。

本物だと主張しただけではありません — それを証明しました。Aetherは、他の人々によって構築された成熟した独立のBitTorrentライブラリである**MonoTorrent**と照合されました: 同じファイルを与えると、両者は*同一の*フィンガープリントを生成するため、どの本物のトレントクライアントもAetherを自分の仲間として扱います。誰でも本物のBitTorrentクライアントをそこに向けて、自分の目で確かめられます。

それに加えて、Aetherは**ブリッジ**を追加します: インターネットを持つノードは、より広いウェブからトレントを取得し、そのピースをAether独自の暗号化されたメッシュチャンクとして再パッケージし、先へ共有できます — そのため**インターネットをまったく持たない**人でも、オフラインメッシュ越しにそのファイルを受け取れます。それが要点です: 世界最大のファイル共有ネットワークを、それが普段は届かない人々につなぐのです。

**正直なところ、現状は。** BitTorrentの*フォーマット* — トレントがどのように記述され、フィンガープリントされ、ワイヤー上でフレーム化されるか — は構築済みで、**全8言語でバイト単位で完全に同一**であり、`fixtures/bittorrent/` の共有フィクスチャコーパスに固定されています。完全に動作するクライアントとメッシュブリッジは、**C#リファレンス**で完成・検証済みです; 他の7言語は同一のプロトコルフォーマットを備えており、そのライブネットワーク層が次のステップです。

> **開発者向け。** カバー範囲: bencode + `.torrent`/magnet + SHA-1 インフォハッシュとBEP-3ピアワイヤー（レアレストファースト）、HTTP + UDPトラッカー（BEP-3/15/23）、Mainline DHT + PEX + ut_metadata（BEP-5/11/9/10）、µTP（BEP-29）、およびBitTorrent v2 SHA-256マークル（BEP-52）、加えてコンテンツサービスへのピース↔チャンク**ゲートウェイ**と、並行かつ再開可能なセグメント化ダウンローダー。C#リファレンス（`src/AetherNet.BitTorrent`、`src/AetherNet.BitTorrent.Gateway`）は、ライブTCP/µTPクライアント、DHTノード、トラッカー、ゲートウェイ、ダウンローダーを同梱し、MonoTorrent相互運用テストは `tests/AetherNet.BitTorrent.Interop.Tests` にあります。8言語バイト同一性コーパス（`fixtures/bittorrent/vectors.json`、7カテゴリ）は、bencode、インフォハッシュ、ピアワイヤー、µTP、マークル、コンパクトインフォ、KRPCをカバーします; 各SDKは対応するフィクスチャテストを同梱しています。

## セキュリティとプライバシー

ワイヤーサービス群に加えて、Aetherは小さな**セキュリティ・プライバシー層**を同梱します — アイデンティティキー管理とリンク層のトラッキング対策です。他のすべてと同様に、それぞれ**全8言語**で実装され、`fixtures/<feature>/` 配下の共有クロス言語フィクスチャに固定されています（SwiftとCはさらにmacOSビルドサーバー上で確認済み）。これらは18のワイヤーサービスにさらに4つ加わったもの*ではありません*: うち3つは**新しいワイヤーパケットタイプをまったく定義せず**、4つ目は新しい予約パケットとしてではなく**既存のDTN/メッシュ経路の内側に**自身のエンベロープを載せて運びます。

| 機能 | 内容 | 層 | フィクスチャ | 8/8 |
|---|---|---|---|:-:|
| **リカバリーフレーズバックアップ** | アイデンティティを**24語のBIP-39**フレーズとしてバックアップし、任意のデバイスで復元します。標準的なBIP-39（公式Trezorベクターと照合済み）で、SHA-256チェックサム付きのため、打ち間違えた単語は*拒否*され、静かに誤ることは決してありません。サーバーもカストディアンも不要 — フレーズが**そのまま**アイデンティティです。 | ローカル | `fixtures/bip39/` | ✅ |
| **Bluetoothトラッキング対策** | ローテーションするキー導出のBLE**サービスUUID**（HMAC-SHA256、15分ウィンドウ）と**解決可能なプライベートアドレス**（IRK + RFCの `ah` 関数、AES-128）を導出します — 受動的スキャナーが時間や場所をまたいで紐付けられないよう、BLEアドバタイザーが必要とするトラッキング対策素材です。 | リンク層 | `fixtures/bleprivacy/` | ✅ |
| **パニックワイプ** | **強要PIN**（SHA-256、定数時間で比較）で、強要下ですべてのアイデンティティキーを安全に消去します — 乱数で上書きしてからゼロ化 — 復元できるものを何も残しません。 | ローカル | `fixtures/panicwipe/` | ✅ |
| **マルチデバイス同期** | *自分自身の*デバイス間での**分散型・サーバーレス**な同期です: Ed25519署名の **DeviceLink** がそれらをペアリングし、last-write-winsの **SyncRecord** エンベロープが状態を調停します — 既存のDTN/メッシュ上をE2E暗号化で運ばれ、クラウドアカウントも同期サーバーも不要です。 | DTN上に載る | `fixtures/sync/` | ✅ |

**一つの正直な非対称性。** マルチデバイスの `DeviceLink` はEd25519署名され、その署名は**8言語中7言語でバイト同一**です。AppleのCryptoKitは意図的にEd25519署名を*ランダム化*するため、Swiftではその64署名バイトが毎回異なります — しかし**署名される本体はバイト同一**であり、各リンクは全8SDKで依然として検証できるため、Swiftは署名バイトのパリティではなく**検証**のパリティに達します。これはプラットフォーム暗号の性質であって欠陥ではなく、これら4機能の中で「バイト同一」にアスタリスクが付く唯一の箇所です。完全なワイヤーフォーマットは [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12に、脅威モデルは [`THREAT_MODEL.md`](THREAT_MODEL.md) にあります。

## トランスポート

各トランスポートにはコードベース全体で使用されるカラー名があります。`IsAvailable` はハードウェアがブロックされたパスをゲートします — `TransportManager` はそれらを自動的にスキップし、次に利用可能なトランスポートにフォールバックします。

**ステータス凡例:** ✅ 本物、構築・検証済み · ⏳ 本物、検証進行中 · ⚠️ 一部のプラットフォームで本物、他はスタブ · ❌ スタブ（トランスポートコードはまだなし）。

| カラー | 名称 | 範囲 | 帯域幅 | ステータス |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ 本物 — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ 本物 — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC リレー | 無制限 | ~10 Mbps | ✅ 本物 — Windows; リレーサーバーは `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | インターネットデータチャンネル | 無制限 | ~100 Mbps | ✅ 全8言語で本物 — **全8言語でループバック検証済み**（C#/Go/Kotlin/TypeScript/Python/C/Swift/Rustのそれぞれで2つのピアが本物のICEデータチャンネル経由でバイトを交換） |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Androidで本物 (`android/white/`); Windows = 本物のBLE-GATT + RSSI −40 dBm 近接近似 (`WinNfcBleTransportService`, net9/10でコンパイル、実行時未検証) — `Windows.Networking.Proximity` はWin 11で削除済み |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ HarmonyOSで本物 (`harmonyos/teal/`, `@kit.NearLinkKit` — オンデバイス検証待ち); Android + Windows = 本物のSSAP-over-BLE近似 (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; コンパイル + ユニットテスト検証済み、実行時未検証) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ 本物のRYLR SX127x/SX126xシリアルドライバ (`LoRaSerialTransport` in C#/Go/Rust/C; コンパイル可、実行時未検証 — 物理モジュールが必要); BLE Coded-PHYブリッジは依然として文書化された設計 |

ラジオトランスポートはプラットフォームコードが存在する場所（C#/Windows、Kotlin/Android、HarmonyOS）でのみ本物です。8つの言語ライブラリはそれ以外の場合、テスト用に**プロセス内シミュレーション**トランスポートを同梱しています — **WebRTCはそれらすべてに共通する最初の本物のトランスポート**です（完成; 各言語でループバック検証済み）。

優先順位は電力コストによります: ラジオメッシュが優先され、次に直接インターネットパスとしてのWebRTC、最終手段としてHTTP/QUICリレー。

## デプロイメント層

Aetherはブルートゥースまたは Wi-Fi をサポートするあらゆるプラットフォームで動作します。使用する層はターゲットにするOSによって異なります。

---

### スタンダード層 — あらゆるプラットフォーム

Android · Windows · Linux · macOS · iOS

Aetherはブルートゥースまたは Wi-Fi ハードウェアを持つあらゆるデバイスで動作します。ラジオが物理的にない場合、ブロックされた各トランスポートは利用可能なものを使って近似されます。これらの近似はいまや**本物のコード**です（コンパイル検証済み; 2デバイス / ハードウェアRFテストまでは**実行時未検証**）:

- **NearLink (Aether Teal)** — Android (`android/teal/AetherNetSleService`) と Windows (`WinNearLinkBleTransportService`) 上の本物のSSAP-over-BLE-GATT近似（Aether SLE UUID `61657468-6572-0003-…`）; コンパイル + ユニットテスト検証済み、実行時未検証。本物のNearLinkラジオはHarmonyOS (`harmonyos/teal/`, オンデバイス検証待ち) にのみ存在します。
- **LoRa (Aether Red)** — 本物のRYLR SX127x/SX126xシリアルドライバ（`LoRaSerialTransport` in **全8言語** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; すべてのポートがコンパイル検証済み、Mac ビルドサーバー上のSwift + Cを含む; 実行時未検証 — 物理モジュールが必要）。Meshtastic-over-BLE-Coded-PHYブリッジ（~1.3 km）は依然として文書化された設計です; 本物の長距離LoRaにはLoRa対応ノード（ゲートウェイ、SBC、またはLoRaモジュール付きの堅牢なハンドセット）が必要です。
- **NFC (Aether White)** — Androidで本物 (HCE)。Windowsはいまや本物のBLE-GATT + RSSI −40 dBm 近接近似 (`WinNfcBleTransportService`, net9/10でコンパイル; 実行時未検証) を持ちます; リーダーが存在する場合はACR122U PC/SC。

どこでも本物かつ同一なもの: **BLE、Wi-Fi Direct、HTTP/QUICリレー、WebRTC P2Pトランスポート（全8言語でループバック検証済み）**、加えてSignal Protocolセキュリティ（X3DH + Double Ratchet）、AODVルーティング、DTNストアアンドフォワード、SOSブロードキャスト、音声、ストリーミング。

**正直なステータス:** BLE + Wi-Fi Direct + リレーは本番グレードで本物; **WebRTC P2Pは本物で全8言語でループバック検証済み**（2つのピアが本物のICEデータチャンネル経由でバイトを交換 — Rustは動作するUDP ICEを持つ `.201` Linuxボックス上で確認済み）; NearLink / LoRa / NFC-on-Windows近似はいまやコンパイルされる本物のコード（LoRaは全8言語でコンパイル検証済み、Mac ビルドサーバー上のSwift + Cを含む; NearLink-Androidはユニットテストも実施済み）ですが、**実行時未検証**です — ハードウェア / 2デバイスRFテストはまだありません。これらはコード上メッシュに参加します; その3つを実地実証済みのRFを期待してデプロイしないでください。

---

### ネイティブ層 — CircleOS / OpenHarmony

CircleOS · HarmonyOS · あらゆるOpenHarmonyベースのOS

CircleOSはOpenHarmonyをベースに構築されており、NearLink（SLE）シリコンと`@kit.NearLinkKit` SDKをファーストクラスのOS機能として搭載しています。NearLinkハードウェアを持つCircleOSおよびHarmonyOSデバイスでは、近似は不要です — `harmonyos/teal/` は本物のSLEラジオを直接使用します:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

これはスタンダード層の単なる上位互換ではありません。NearLink層では本質的に異なるネットワークです:

| 機能 | スタンダード層（BLE近似） | ネイティブ層（CircleOS / OpenHarmony） |
|---|---|---|
| **NearLink範囲** | ~100 m (BLE) | **600 m** |
| **NearLink帯域幅** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLinkレイテンシ** | ~10 ms (BLE) | **20 µs** |
| **NearLink電力** | BLEベースライン | **BLE 5.0より60%少ない** |
| **NearLink同時ピア数** | ~7 (BLE接続制限) | **500以上** |
| **NearLinkソース** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | 本物のSLEラジオ (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTPリレー** | ネイティブ | ネイティブ（同一） |
| **Signal Protocolセキュリティ** | 完全 | 完全（同一） |
| **ルーティング / DTN / SOS** | 完全 | 完全（同一） |
| **Aether Tagアイデンティティ** | サポート済み | サポート済み（同一） |

---

### 層間の移行

コード変更は不要です。層は各トランスポートサービスの`IsAvailable`によって実行時に決定されます:

1. NearLinkシリコンを持つCircleOSまたはHarmonyOSデバイスでは、NearLinkトランスポートの`IsAvailable`が`true`を返します（パーミッションチェック + パッシブスキャン試行によるハードウェアプローブ）。
2. `TransportManager`は自動的にNearLinkを優先位置に昇格させます — 最低電力コスト、最高帯域幅。
3. アプリコード、パケットフォーマット、ルーティングアルゴリズム、セキュリティ層、Aether Tagは両層で同一です。

スタンダード層のノードとネイティブ層のノードは自由に通信できます — 同じワイヤーフォーマット、同じSignal Protocolセッション、同じAether Tagを共有します。層の違いはNearLinkパケットに使用されるラジオにのみ影響し、その上のプロトコルには影響しません。

---

> **内部的には、これらの層はAsterixバリアント（スタンダード）とObelixバリアント（ネイティブ）と呼ばれています。** Asterixは利用可能なものでうまく機能します。NearLinkネイティブのCircleOSで動作するObelixは、魔法の薬の力をもう一度飲まなくても持ち続けるように、永続的に向上した能力で動作します。

---

## 実装

Aetherはスマートフォン、ラップトップ、タブレット、マイクロコントローラで動作するように8言語で構築されています。すべての実装はワイヤー互換なパケットを生成します — Rustノードで暗号化されたメッセージはPythonノードで中継され、Swiftノードで復号できます。

| 言語 | ディレクトリ | ワイヤーフォーマット | Routing/DTN/SOS | X3DH | Double Ratchet | OPKプール | Voice/Group | Streaming/Video/Watch | BitTorrent |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |

**BitTorrent列:** ✅ = 完全に動作するクライアント + メッシュゲートウェイ（C#リファレンス）。◐ = ここではBitTorrentの**ワイヤーフォーマット**がバイト単位で完全に同一であり（`fixtures/bittorrent/` に固定）、ライブネットワーク層が次のステップです — [BitTorrent — 本物、そしてメッシュへの橋渡し](#bittorrent--real-and-bridged-into-the-mesh) を参照。他のすべての列は全8言語で本物かつ動作します。

全8言語はバイト同一のワイヤーパケットを生成し、17の標準ワイヤーフォーマットフィクスチャと6つのSignalテストベクターに対して検証されています（`fixtures/expected/*.bin`、`fixtures/signal/expected/*.json`）— **どの言語も同じバイト列に対して照合されます**。ルーティング（AODVスタイルのRREQ/RREP）、DTNストアアンドフォワード、SOSブロードキャスト、音声、ストリーミング、セキュリティ強化サービスはすべての言語で実装されており、全8実装で**約3,000テスト**あります:

| 言語 | テスト数 | テストプラットフォーム |
|----------|------:|-------------|
| C# (.NET 10) | 530 | Linux |
| TypeScript / Node 20 | 459 | Linux |
| Kotlin / JVM 21 | 457 | Linux |
| Go 1.22 | 423 | Linux |
| Python 3.12 | 387 | Linux |
| Swift 6 | 295 | macOS |
| C (GCC) | 253 | Linux |
| Rust (stable) | ~195 | Linux |
| **合計** | **~3,000** | |

クロス言語Signalの相互運用は `fixtures/signal/` に固定されており、X3DH（`x3dh_basic`）、対称ラチェット（`ratchet_step_basic`、`ratchet_step_three_iterations`）、KDF_RK（`kdf_rk_basic`）、および完全なX3DHセッションのラウンドトリップ（`x3dh_session_msg1`、`x3dh_session_reply`）の共有テストベクターがあります。すべての実装はこれらのフィクスチャに対してバイト同一の出力を生成しなければなりません。全8言語は完全なSignalセッション（`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`）を搭載しています。

ワイヤーフォーマットとSignalを超えて、**ワイヤーサービススイート全体** — プレゼンス、ハートビート、プロフィール同期、一時ID告知、プレキー交換、チャンネル、プッシュトゥトーク、画面共有、通話制御、SOS確認応答、スペースブレッドクラム、フォージ告知、ボールトシャード要求、帯域幅測定（**手に入るもの — すべてのサービスを、すべての言語で** を参照）— も同様に全8言語で実装され、それぞれ独自のフィクスチャ（`fixtures/presence/`、`fixtures/media/`、`fixtures/bandwidth/`、`fixtures/prekey/`、`fixtures/videocall/`、`fixtures/vaultshard/` およびその同類）に固定されています。プロトコル層でC#専用の機能は1つもありません。

## クイックスタート

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

このデモでは8つのステップを体験できます: 3つのノード（Alice、Bob、Charlie）のEd25519アイデンティティキー生成、Signal Protocolセッションの確立、暗号化されたメッセージの送信、Charlie経由のメッセージリレー（Charlieは内容を読めない）、バイナリワイヤーフォーマットの表示、5つの連続メッセージにわたる前方秘匿性のデモ。出力はカラーコード付きで、ステップ間に一時停止します。

**C#でメッセージを送信する:**

```csharp
// Signal Protocolセッションを確立する
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// 暗号化して送信
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// 署名済みパケットを作成
var packet = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = "alice",
    DestinationUhid = "bob",
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7
};
var wireBytes = PacketSerializer.Serialize(packet);
await transport.SendAsync("bob", wireBytes);
```

### Rust (1.70+)

```bash
cd rust && cargo run
```

このデモでは2つのノードのアイデンティティキー生成、プレキーバンドルの交換、暗号化されたセッションの確立、双方向の暗号化メッセージ送信、メッシュパケットの作成と署名、署名の検証、バイナリワイヤーフォーマットへのシリアライズが示されます。プロセス内トランスポート層もデモされます。

**Rustでメッセージを送信する:**

```rust
let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

let alice_bundle = alice.generate_pre_key_bundle("alice")?;
bob.process_pre_key_bundle(&alice_bundle)?;

let bob_bundle = bob.generate_pre_key_bundle("bob")?;
alice.process_pre_key_bundle(&bob_bundle)?;

let encrypted = alice.encrypt("bob", b"Hello Bob!")?;
let decrypted = bob.decrypt("alice", &encrypted)?;
```

### TypeScript (Node 18+, tsx)

```bash
cd typescript && npm install && npm run dev
```

このデモではシミュレートされたネットワーク上に2つのノードを作成し、Ed25519キーを生成し、Signal Protocolセッションを確立し、パケットを作成して署名し、C#互換のバイナリフォーマットにシリアライズし、秘密メッセージを暗号化し、他のノードで復号し、トランスポート経由で送信し、ラウンドトリップを検証します。

**TypeScriptでメッセージを送信する:**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// バンドルをピアと交換する
await signal.processPreKeyBundle(peerBundle);

const plaintext = new TextEncoder().encode("Hello!");
const encrypted = await signal.encrypt("peer-node", plaintext);

const packet = MeshPacket.create(PacketType.Data, "my-node");
packet.destinationUhid = "peer-node";
packet.payload = encrypted;

const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

const serialized = PacketSerializer.serialize(packet);
await transport.sendAsync("peer-node", serialized);
```

### Python (3.10+)

```bash
cd python && pip install -e . && python3 demo.py
```

このデモでは8つのデモンストレーションを実行します: Ed25519キー生成と改ざん検出、機能付きのノード作成、Signal Protocol X3DHキー交換、AES-256-GCM暗号化と復号、パケットシリアライズ、リプレイ検出付きパケット署名、プロセス内トランスポート、すべての層を組み合わせたエンドツーエンドフロー。

**Pythonでメッセージを送信する:**

```python
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

bob_bundle = await bob_signal.generate_pre_key_bundle("bob")
await alice_signal.process_pre_key_bundle(bob_bundle)

encrypted = await alice_signal.encrypt("bob", b"Hello Bob!")

packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="alice",
    destination_uhid="bob",
    payload=encrypted.ciphertext,
    ttl=7
)
signing_service.sign_packet(packet, alice_private_key)

serialized = PacketSerializer.serialize(packet)
await transport.send_async("bob", serialized)
```

### Go (1.22+)

```bash
cd go && go run ./cmd/demo/main.go
```

このデモでは5つのデモンストレーションを実行します: パケットシリアライズのラウンドトリップ、改ざん検出付きEd25519署名、双方向暗号化メッセージングによるSignal Protocolセッション確立、2つのピア間のプロセス内トランスポート、リプレイ保護のためのノンス重複排除。

**Goでメッセージを送信する:**

```go
alice, _ := security.NewSignalProtocolService()
bob, _ := security.NewSignalProtocolService()

aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
bob.ProcessPreKeyBundle(aliceBundle)

bobBundle, _ := bob.GeneratePreKeyBundle("bob")
alice.ProcessPreKeyBundle(bobBundle)

encrypted, _ := alice.Encrypt("bob", []byte("Hello Bob!"))
decrypted, _ := bob.Decrypt("alice", encrypted)
```

### Kotlin (JDK 17+, Gradle 8+)

```bash
cd kotlin && ./gradlew run
```

このデモでは11のステップを体験できます: キー生成、機能付きのノード作成、Signal Protocol初期化、プレキーバンドル交換、セッション確立、パケット作成と署名、シリアライズ、署名検証付きデシリアライズ、キーラチェットによるエンドツーエンド暗号化、リプレイ攻撃検出、プロセス内トランスポート。

**Kotlinでメッセージを送信する:**

```kotlin
val aliceSignal = SignalProtocol()
val bobSignal = SignalProtocol()

val bobBundle = bobSignal.generatePreKeyBundle("bob")
aliceSignal.processPreKeyBundle(bobBundle)

val aliceBundle = aliceSignal.generatePreKeyBundle("alice")
bobSignal.processPreKeyBundle(aliceBundle)

val encrypted = aliceSignal.encrypt("bob", "Hello Bob!".toByteArray())
val decrypted = bobSignal.decrypt("alice", encrypted)
```

### Swift (5.9+, macOS 13+ / iOS 16+)

```bash
cd swift && swift run aether-demo
```

このデモでは5つのテストを実行します: パケットシリアライズのラウンドトリップ、改ざん拒否付きEd25519署名、AES-256-GCM暗号化によるSignal Protocolセッション確立、プロセス内トランスポートメッセージ配信、AliceがパケットにサインしてBobがトランスポート後に検証する完全なエンドツーエンドフロー。

**Swiftでメッセージを送信する:**

```swift
let aliceSignal = SignalProtocolService()
let bobSignal = SignalProtocolService()

let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: "bob")
try await aliceSignal.processPreKeyBundle(bobBundle)

var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice",
    destinationUhid: "bob",
    ttl: 7,
    payload: "Hello Bob!".data(using: .utf8)!
)

let signer = await PacketSigningService(
    privateKey: alicePrivateKey, publicKey: alicePublicKey)
try await signer.signPacket(&packet)

let serialized = PacketSerializer.serialize(packet)
await transport.sendAsync(peerUhid: "bob", data: serialized)
```

### C (CMake 3.16+, C11, libsodium)

```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

このデモでは7つのデモンストレーションを実行します: Ed25519キー生成、パケット作成と署名、バイナリワイヤーフォーマットへのシリアライズ、整合性チェック付きデシリアライズ、AES-256-GCM暗号化と復号、HMAC-SHA256メッセージ認証、HKDF-SHA256キー導出。

**Cでメッセージを送信する:**

```c
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// 署名
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// シリアライズして送信
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// buffer[0..size-1] をトランスポート経由で送信

aethernet_packet_free(packet);
```

## ロードマップ

構築済みのものと次に来るもの。

**完了（クロス言語検証済み、全8実装）:**
- ワイヤーフォーマット: 8言語で完全なバイト同一性、17の標準フィクスチャとクロス言語アサーションで確認済み（`fixtures/expected/*.bin`）
- **GitHub Actionsワークフロー（定義済み、ただし現在のゲートではない）** — 9ジョブのマトリックス（C#/.NET 10、Go 1.22、TypeScript/Node 20、Python 3.12、Kotlin/JVM 21、Swift/macOS、Rust stable、C/GCC、加えてフィクスチャ整合性ジョブ）が `.github/workflows/ci.yml` に定義されています。コミットは現在 `[skip ci]` を付けてプッシュされているため、実際の強制は**言語ごとにローカルで**実行されるフィクスチャコーパスです（SwiftとCはmacOSビルドサーバー上）; CIはコード変更なしで再び有効にできます。
- Ed25519パケット署名と検証
- AES-256-GCM暗号化
- HKDF / HMACキー導出プリミティブ
- パケットシリアライズ + 署名レイアウト（LE + 4バイトint32フィールド）
- プロセス内トランスポートシミュレーター（開発とテスト用）
- RREQ/RREP、署名済みルート応答、重複排除、TTL転送を持つAODVインスパイアのルーティングサービス
- 保管移転、ジオハッシュ対応レプリケーション、72時間TTLを持つDTNストアアンドフォワードサービス
- フラッド、重複排除、自己起源ガード、レート制限（3/時間）を持つSOSブロードキャストサービス
- 拡張ポイント: `IncentiveProvider`、`BackendClient`、`FeatureFlagProvider`（Noopデフォルト）
- 全8言語で**約3,000テスト**（C# 530、TypeScript 459、Kotlin 457、Go 423、Python 387、Swift 295、C 253、Rust ~195） — すべてグリーン、言語ごとに実行（SwiftとCはmacOSビルドサーバー上）
- ✅ **本物のX3DH一時キー（8言語）** — 4つのX25519 DH（`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`）とHKDF-SHA256ルート導出。`fixtures/signal/expected/x3dh_basic.json` で固定。
- ✅ **Double Ratchetのファミリー全体への適用** — HMAC-SHA256 + 対称ラチェットでの0x01/0x02ドメイン分離、DHラチェットステップでのHKDF-SHA256 KDF_RK、受信時のDHローテーションを持つ完全なSignal §5。`ratchet_step_basic`、`ratchet_step_three_iterations`、`kdf_rk_basic` フィクスチャで検証済み。
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9とHEADの照合** — `docs/PROTOCOL_SPEC.md` を参照。

**完了（全8言語）:**
- ✅ **音声通話（1対1）** — シグナリングステートマシン（Offer/Answer/Hangup/Cancel/Timeout）+ バイナリフレームトランスポート（16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes）。`IRoutingService` 経由のルート対応配信。
- ✅ **グループ音声** — ホスト主導のメンバーシップ（招待/キック/退出）、フレームごとのキー生成フィールド、現在の全メンバーへのユニキャストファンアウト、メンバーシップ変更時のホスト制御キーローテーション。
- ✅ **ライブストリーミング** — パブリッシャーが`StreamAnnounce`をブロードキャスト; サブスクライバーが`StreamSubscribe`を送信; バイナリ`StreamSegment`フレーム（16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes）を各サブスクライバーにユニキャスト。
- ✅ **ビデオ通話（1対1）** — シグナリングでのコーデック/解像度/fps/ビットレートネゴシエーション、キーフレームリクエストと品質変更シグナル、音声レイアウトと一致するバイナリ`VideoFrame`フォーマット。
- ✅ **ウォッチトゥゲザー** — ホストが権威ある`WatchSync`（再生/一時停止/シーク/速度）コマンドを発行; フォロワーはRTT補正で適用（`position = positionMs + elapsed × playbackSpeed`）; ファイアアンドフォーゲットの`WatchReaction`。
- ✅ **ワンタイムプレキー（OPK）プール** — デフォルト100、FIFOイシュー、レイジートップアップ、全8言語でのロック保護消費。単一OPK並行性の危険性を排除。
- ✅ **C: 完全なSignalセッション** — `c/src/signal_protocol.c`の`aethernet_signal_service_init`、`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`; `c/tests/test_signal_session.c`の6つの2ノードE2Eテスト。全8言語で完全なセッション対応Signal Protocolが揃いました。

**完了（全8言語 — ワイヤーサービススイート全体）:**
- ✅ **すべての予約済みパケットタイプが、いまや全8言語でバイト同一の実際に動作するサービスです。** プレゼンスビーコン/クエリ (21/22)、ハートビート (10)、プロフィール同期 (23)、一時ルーティングID告知 (56)、プレキー交換 (25/26)、チャンネル (7)、プッシュトゥトーク (15)、画面共有 (32)、通話制御 (27)、SOS確認応答 (6)、スペースブレッドクラム (40)、フォージ告知 (41)、ボールトシャード要求 (42)、帯域幅測定 / ABMF (53/54/55)。それぞれはホストがそのSignalセッションとルーティングテーブルに接続する薄いサービス（生成 + 処理 + イベント）であり、それぞれが共有クロス言語フィクスチャ（`fixtures/presence/`、`fixtures/media/`、`fixtures/bandwidth/`、`fixtures/prekey/`、`fixtures/videocall/`、`fixtures/vaultshard/`、`fixtures/channels/`、`fixtures/profiles/`、`fixtures/heartbeat/`、`fixtures/erid/`、`fixtures/space/`、`fixtures/forge/`、`fixtures/sos/`）に固定され、言語ごとのユニットテストで検証されており、SwiftとCはmacOSビルドサーバー上で確認されています。**手に入るもの — すべてのサービスを、すべての言語で** を参照。

**完了（C#リファレンスのみ）:**
- ✅ **デモステップ9 — MessagingService + DTNフォールバックのエンドツーエンド** — `samples/AetherNet.Demo.Console`では、受信者がオフラインの場合のDTNストアアンドフォワードを使った実際のSignal暗号化メッセージングのウォークスルー。
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` ブリッジ** — `SignalMessageEnvelopeCipher`はメッセージング層をデフォルトでエンドツーエンド暗号化にします; Signalセッションのないメッセージはキューに入れられ、安全でない状態では送信されません。
- ✅ **アダプティブビットレートストリーミング** — プロファイルA（リアルタイム）、B（ライブブロードキャスト）、C（VOD）のスペック準拠ビットレートラダーを持つ`AdaptiveBitrateController`。パブリッシャーは最高持続可能なランクを選択し（20%ヘッドルーム）、フロア以下の場合はセグメントの代わりに`StreamAbandon`（`PacketType.StreamAbandon`）を発行。`IStreamingService`は`UpdateBandwidthEstimate`と`GetCurrentBitrateRung`を公開。
- ✅ **ウォッチトゥゲザー: BitTorrentインジェスト + ChipInグループファンディング** — `TorrentInfo` / `TorrentFile`モデル; `WatchTogetherService`は`PacketType.TorrentMetadata`を処理し`TorrentReceived`を発火。`ChipInPool` / `ChipInContribution`ステートマシン（Collecting → Funded → Purchasing → Acquired / Failed / Refunded）; `IWatchTogetherService`の`StartChipInAsync` / `ContributeAsync` / `GetChipIn`。
- ✅ **自動SFUリレーによるグループビデオ通話** — `GroupVideoService` / `IGroupVideoService`。参加者が3人以下の場合はFullMeshトポロジー; `SfuThresholdParticipants`（4）で自動的にSFUに切り替えと`GroupVideoSignaling(SfuAssigned)`によるリレー再割り当て。FullMeshではファンアウト、SFUモードではリレーのみ送信。シグナリングパケットタイプ`GroupVideoSignaling = 35`。
- ✅ **BLE GATTトランスポートシミュレーション** — `SimulatedBleGattTransportService`（`IBleTransportService`）。`BleGattFramer`によるGATT MTUフレーミング（1024 B/フレーム、`[2B count][2B index][payload]`）、プロセス内静的ピアレジストリ、アドバタイズメントブロードキャスト。すべての`BleMaxPayloadBytes`制約を適用。
- ✅ **Wi-Fi Directトランスポートシミュレーション** — `SimulatedWifiDirectTransportService`（`IWifiDirectService`）。明示的な`ConnectAsync`/`DisconnectAsync`ライフサイクル、ダイレクト大容量ペイロード配信（フレーミングなし）、双方向`PeerConnected`/`PeerDisconnected`イベント。
- ✅ **NearLinkトランスポートシミュレーション** — `SimulatedNearLinkTransportService`（`INearLinkTransportService`）。4096 Bフレームのデバイス、500ピアレジストリ、`ConnectedPeerCount`、実行時設定可能な`IsAvailable`。
- ✅ **RF立ち上げシミュレーションテスト** — 2ノード相互運用テスト（`SimulatedTransportTests`）: BLE + NearLink `MeshPacket`のラウンドトリップ、WiFi Direct 64 KBペイロード転送。ソフトウェア層は完全に検証済み; ハードウェア上での検証には実機デバイスラボセッションが必要。

**完了（C#トランスポート層 — すべてフェイルファスト）:**
- ✅ **BLE GATTリアルトランスポート** — `WinBleGattTransportService`（Windows WinRT）+ `android/blue/`（Android GATTサーバー）。`samples/AetherNet.BleRfTest/`の完全RF立ち上げテスト。
- ✅ **Wi-Fi Directリアルトランスポート** — `WinWifiDirectTransportService`（WinRT、`WiFiDirectAdvertisementPublisher` + TCP StreamSocketポート8888）+ `android/green/`（`WifiP2pManager`）。`samples/AetherNet.WifiDirectRfTest/`のRFテスト。
- ✅ **HTTPリレートランスポート（Aether Purple）** — 10秒ロングポール、`PowerCostRelative = 100`、常に最終手段の`HttpRelayTransportService`。`samples/AetherNet.RelayServer/`のリレーサーバー（ASP.NET Coreミニマルアプリ、ポート5200）。`samples/AetherNet.RelayRfTest/`のRFテスト。
- ✅ **NFC（Aether White）** — `android/white/`はAID `F061657468657200`で`HostApduService`を実装。`WinNfcStubTransportService`は2つのWindowsの近似パスを文書化: (1) RSSI ゲート ≥ −40 dBm（NFCシリコンなしのタップ接続をシミュレート、`IsAvailable = Bluetooth present`）を持つNDEF-over-BLE-GATT; (2) `Windows.Devices.SmartCards` PC/SC経由のACR122U USBリーダー（`IsAvailable = contactless reader enumerated`）。アップグレードパス: MicrosoftがファーストパーティのピアツーピアNFC APIを提供したら`ITransportService`を実装。
- ✅ **NearLink（Aether Teal）** — **`harmonyos/teal/`** — `@kit.NearLinkKit`（`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`）を使用したHarmonyOS 5.0.1（API 13）ArkTS完全実装; `isAvailable`は実行時にプローブ。`WinNearLinkStubTransportService` + `android/teal/`はSSAP-over-BLE近似を文書化: Aether SLEサービスUUID `61657468-6572-0003-0000-000000000000`を持つBLE GATT — SSAPとAPI同等、本物のNearLinkハードウェアとはワイヤー互換ではない。アップグレードパス: BLE GATTコールを`ssapc_*`/`ssaps_*` SDKコールに置き換え; UUIDと`TransportManager`スロットは変更なし。
- ✅ **LoRa / CircleLink（Aether Red）** — `LoRaCircleLinkStub` + `android/red/`は、BLE 5.0 Coded PHY S=8（~1.3 km屋外）上で完全なMeshtasticワイヤーフォーマット（16バイトヘッダー + AES-256-CTR protobuf）を使用したMeshtastic-over-BLE-LR近似を文書化: マネージドフラッドルーティングとRSSI重み付けコンテンションウィンドウ付き。本物のLoRaハードウェアとのブリッジノードフェデレーションは自動的に機能します（同じMeshtasticパケットフォーマット、翻訳なし）。アップグレードパス: BLE LRラジオをSX1276/SX1278のATコマンドまたはSPIドライバに置き換え; パケットフォーマットとルーティングは変更なし。

**未解決 — `OPEN_ISSUES.md` で追跡中:**
- 実機ハードウェアでのRF立ち上げ: 物理BLE / Wi-Fi Directデバイスでのエンドツーエンド2ノード相互運用テスト（シミュレーションテストはパス; ハードウェアラボセッションが必要）
- NearLink: `harmonyos/teal/`は完成; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6ハードウェアが必要（NearLinkシリコンはHuawei以外のデバイスには搭載されていない）。Windows + AndroidはSSAP-over-BLE近似に自動フォールバック。
- LoRa / CircleLink: 真のLoRa範囲にはラジオモジュールが必要。なければMeshtasticワイヤーフォーマットはBLE LR（~1.3 km）で運ばれ、本物のLoRaハードウェアとのブリッジノードフェデレーションは利用可能。
- ✅ **（v1.2.0で解決済み）** コンシューマープロトコルサーフェス（Wave 16/17） — インバウンドバンドル向けの `IDtnService.BundleReceived` イベント（[#59](https://github.com/bhengubv/aether-protocol/issues/59)）、アプリケーション層の命名/発見ディレクトリ（[#60](https://github.com/bhengubv/aether-protocol/issues/60)）、著者チップインターフェース（[#61](https://github.com/bhengubv/aether-protocol/issues/61)）。3つすべてがバイト同一のクロス言語フィクスチャとともに8言語にわたって加算的に出荷されました。CHANGELOGを参照。

**外部コントリビューションはまだ受け付けていません:**
- プロトコルはまだ活発に開発中です。現時点では外部コントリビューションは受け付けていません。
- NearLinkトランスポートの実装、Android/iOS統合例、追加のトランスポートバックエンド、パフォーマンスベンチマーク、プロトコルファジングは社内で追跡されており、プロジェクトが安定した公開コントリビューションポイントに達したときに公開されます。

## プロジェクト構成

```
aether-protocol/
  src/
    AetherNet.Core/          プロトコルモデル、定数、パケットシリアライズ
    AetherNet.Security/      Signal Protocol、Ed25519、パケット署名
    AetherNet.Transport/     トランスポート抽象化、NearLink、プロセス内シミュレーター
    AetherNet.Messaging/     メッセージ処理とリレー
    AetherNet.Storage/       DTNストアアンドフォワード永続化
    AetherNet.Streaming/     アダプティブビットレートストリーミング、ビデオモデルとインターフェース
    AetherNet.Voice/         音声通話とグループ音声
    AetherNet.Content/       コンテンツ検証とチャンク転送
  samples/
    AetherNet.Demo.Console/  インタラクティブデモ
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust実装
  typescript/             TypeScript実装
  python/                 Python実装
  go/                     Go実装
  kotlin/                 Kotlin/JVM実装
  swift/                  Swift実装
  c/                      C実装
  docs/
    PROTOCOL_SPEC.md      RFCスタイルのプロトコル仕様
```

## 新しいトランスポートの追加

`ITransportService`を実装します:

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implement SendAsync, IsConnected, DataReceived
}
```

DIに登録すると、`TransportManager`は自動的にトランスポート選択に含め、電力コスト順にソートします。

## 比較

| プロトコル | 制限 | Aetherの優位性 |
|----------|-----------|-----------------|
| **Briar** | Android専用、Tor依存 | クロスプラットフォーム、純粋なメッシュ |
| **Meshtastic** | LoRaのみ（最大30 kbps） | マルチトランスポート（BLE + WiFi + NearLink）、音声とストリーミング対応 |
| **Reticulum** | Python、小さなコミュニティ | 8言語、すべてでワイヤー互換 |
| **libp2p** | インターネットバックボーンを前提 | オフラインファースト、ゼロインフラで動作 |
| **Yggdrasil** | オーバーレイネットワーク、インターネット必要 | 物理層メッシュ、インターネットなしで動作 |
| **Signal** | メッシュなし、インターネット必要 | オフライン動作、P2P、メッシュリレー、同じE2E暗号化 |

## よくある質問

**AetherNetはインターネットなしで動作しますか?**
はい — オフラインファーストです。デバイスはBluetooth、Wi-Fi Direct、NearLink、LoRaを介して直接通信し、メッセージを他のデバイスを経由してホップバイホップで中継します。インターネット接続、携帯電話の基地局、サーバーは一切不要です。ライブルートが存在しない場合、メッセージはルートが開くまで最大72時間保持されます（遅延耐性のあるストアアンドフォワード）。

**エンドツーエンドで暗号化されていますか?**
はい。AetherNetはエンドツーエンド暗号化にSignal Protocol（X3DHキー合意とX25519上のDouble Ratchet）を使用し、メッセージペイロードにはAES-256-GCM、すべてのパケットにはEd25519署名を使用します。メッセージを中継するデバイスは、その内容を読み取れません。

**どのトランスポートを使用しますか?**
Bluetooth LE、Wi-Fi Direct、NearLink（SLE）、LoRa/CircleLinkシリアルラジオ、HTTP/QUICリレー、そして直接インターネットピアツーピア用のWebRTC。プロトコルはパケットごとに最も電力の低い利用可能なトランスポートを自動的に選択し、次のものにフォールバックします。

**どのプログラミング言語で利用できますか?**
8つ — C#、Rust、TypeScript、Python、Go、Kotlin、Swift、C。すべての実装はバイト単位で同一のワイヤーパケットを生成し、すべての実装が照合される共有クロス言語フィクスチャコーパスによって保証されているため、ある言語で構築されたパケットは、他のどの言語でも変更なしに復号されます。

**Meshtastic、Briar、Bridgefyとはどう違いますか?**
MeshtasticはLoRa専用です; AetherNetはマルチトランスポート（Bluetooth + Wi-Fi + NearLink + LoRa）で、メッセージに加えて音声、ビデオ、ストリーミングも運びます。BriarはAndroid専用でTor経由でルーティングします; AetherNetはクロスプラットフォームで純粋なメッシュです。クローズドなSDKとは異なり、AetherNetはMITライセンスで、8言語でオープンに実装されています。詳細は上の比較表にあります。

**本番環境で使える状態ですか?**
プロトコル層 — ワイヤーフォーマット、Signalセキュリティ、ルーティング、DTNストアアンドフォワード、そして完全なサービススイート — は全8言語で実装・テストされています。ラジオトランスポートはプラットフォームコードが存在する場所（WindowsとAndroid上のBluetoothとWi-Fi、どこでも動作するWebRTC）では本物で、それ以外の場所ではハードウェア立ち上げまで実地未検証であり、これは `OPEN_ISSUES.md` で正直に追跡されています。デプロイ前に各セクションのステータス注記をお読みください。

**どのライセンスですか?**
MIT — 商用およびオープンソース利用に無料。[LICENSE](LICENSE) を参照してください。

**AetherNetは誰が構築していますか?**
The Geek Networkのメッシュエコシステムを支えるオープンプロトコルとして開発されており、モバイルデータの有無にかかわらず機能する通信を目指して南アフリカで構築されています。

## 拡張ポイント

プロトコルはスタンドアローンで動作します。これらのインターフェースを使うと独自のバックエンドをプラグインできます:

- `IAetherNetIncentiveProvider` — トラフィックを中継するノードに報酬を与える（Noopデフォルト: 利他的中継）
- `IAetherNetBackendClient` — インターネット利用時にサーバーと同期する（Noopデフォルト: 完全オフライン）
- `IAetherNetFeatureFlagProvider` — 実行時にプロトコル機能を切り替える（Noopデフォルト: すべて有効）

3つすべてにNoopの実装が付属しています。削除しても何も壊れません。

## コントリビューション

外部コントリビューションはまだ受け付けていません。プロジェクトはまだ活発に開発中です。公開コントリビューションウィンドウを発表したときに確認してください。

## セキュリティ

責任ある開示ポリシーについては [SECURITY.md](SECURITY.md) を参照してください。

## ライセンス

MITライセンス。[LICENSE](LICENSE) を参照してください。

## 翻訳

このREADMEは、このファイルの先頭にある言語バーに列挙された他の言語でも、[`docs/i18n/`](docs/i18n/) 配下で管理されています — ヨーロッパ、東アジア、中東、南アジア、東南アジア、アフリカの言語にわたっており、それはデータを持たない人々のために作られたネットワークが、十分につながっている人だけが読める玄関口を持つべきではないからです。**英語版が信頼できる情報源（source of truth）** です: 翻訳と英語のテキストが食い違う場合、英語のテキストが正であり、翻訳は1〜2リリース遅れることがあります。記述されているプロトコル、コード、フィクスチャ、動作は、どの言語で読んでも同一です。
