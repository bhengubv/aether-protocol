```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

近くにいる人とファイル、メッセージ、ストリームを共有できます。Wi-Fi不要。モバイルデータ不要。アカウント登録不要。AirDropに似ていますが、あらゆるプラットフォームのすべての人と使えます。

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

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

## トランスポート

各トランスポートにはコードベース全体で使用されるカラー名があります。`IsAvailable` はハードウェアがブロックされたパスをゲートします — `TransportManager` はそれらを自動的にスキップし、次に利用可能なトランスポートにフォールバックします。

| カラー | 名称 | 範囲 | 帯域幅 | ステータス |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | セルラーHTTPリレー | 無制限 | ~10 Mbps | ✅ Windows — リレーサーバーは `samples/Aether.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE (`android/white/`); Windows: NDEF-over-BLE-GATT + ACR122U PC/SC 近似（`Windows.Networking.Proximity` はWin 11で削除済み） |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`; Windows + Android: SSAP-over-BLE 近似（API同等、ワイヤー互換ではない） |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ BLE LR経由Meshtasticワイヤーフォーマット（~1.3 km）; LoRaモジュール存在時はSX1276/SX1278へのラジオスワップ |

`TransportManager` での優先順位: NearLink → BLE (≤ 1 KB) → Wi-Fi Direct → NFC → LoRa → HTTPリレー（最終手段、`PowerCostRelative = 100`）。

## デプロイメント層

Aetherはブルートゥースまたは Wi-Fi をサポートするあらゆるプラットフォームで動作します。使用する層はターゲットにするOSによって異なります。

---

### スタンダード層 — あらゆるプラットフォーム

Android · Windows · Linux · macOS · iOS

Aetherはブルートゥースまたは Wi-Fi ハードウェアを持つあらゆるデバイスで完全に動作します。ラジオが物理的にない場合、ブロックされた各トランスポートは利用可能なものを使って近似されます:

- **NearLink (Aether Teal)** — 標準的なAether SLEサービスUUID（`61657468-6572-0003-0000-000000000000`）を使用したBLE GATT経由で近似。SSAPアプリケーションプロトコル層はGATTとAPI同一です。ラジオ層（BPSK/QPSK/8PSK、Polarコード、1〜4 MHzチャンネル）は異なります — スタンダード層で動作するノードは本物のNearLinkハードウェアとの生バイト交換はできませんが、他のスタンダード層Aetherノードとは相互運用できます。
- **LoRa (Aether Red)** — BLE 5.0 Coded PHY（S=8、~1.3 km屋外）上で完全なMeshtasticワイヤーフォーマットを使用して近似。本物のLoRaハードウェアとのブリッジノードフェデレーションは自動的に機能します — 同じMeshtasticパケットフォーマットが翻訳なしですべてのホップを渡ります。
- **NFC (Aether White)** — タップ接続のセマンティクスを再現するRSSI近接ゲート（≥ −40 dBm ≈ 5〜10 cm）を持つNDEF-over-BLE-GATT経由で近似。WindowsではUSB NFCリーダー経由のPC/SCパスもサポートされています。

その他のすべての機能 — BLE、Wi-Fi Direct、HTTPリレー、Signal Protocolセキュリティ（X3DH + Double Ratchet）、AODVルーティング、DTNストアアンドフォワード、SOSブロードキャスト、音声、ストリーミング — はネイティブかつネイティブ層と同一です。

**これは完全に機能する本番グレードのデプロイメントです。** ほとんどのアプリはここから始まります。

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

| 言語 | ディレクトリ | ワイヤーフォーマット | Routing/DTN/SOS | X3DH | Double Ratchet | OPKプール | Voice/Group | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

全8言語はバイト同一のワイヤーパケットを生成し、CIで実行される14の標準ワイヤーフォーマットフィクスチャと4つのSignalテストベクターによって検証されています（`fixtures/expected/*.bin`、`fixtures/signal/expected/*.json`）。ルーティング（AODVスタイルのRREQ/RREP）、DTNストアアンドフォワード、SOSブロードキャスト、音声、ストリーミング、セキュリティ強化サービスはすべての言語で実装されており、全8実装で**約3,000テスト**あります:

| 言語 | テスト数 | CIプラットフォーム |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **合計** | **~3,000** | |

クロス言語Signalの相互運用は `fixtures/signal/` に固定されており、X3DH（`x3dh_basic`）、対称ラチェット（`ratchet_step_basic`、`ratchet_step_three_iterations`）、KDF_RK（`kdf_rk_basic`）の共有テストベクターがあります。すべての実装はこれらのフィクスチャに対してバイト同一の出力を生成しなければなりません。全8言語は完全なSignalセッション（`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`）を搭載しています。

## クイックスタート

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/Aether.Demo.Console
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
aether_mesh_packet_t *packet = aether_packet_new();
packet->type = AETHER_PACKET_TYPE_DATA;
packet->ttl = 7;

aether_packet_set_source_uhid(packet, "alice");
aether_packet_set_destination_uhid(packet, "bob");
aether_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// 署名
size_t signable_len = 0;
uint8_t *signable = aether_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aether_ed25519_sign(private_key, signable, signable_len, signature);
aether_packet_set_signature(packet, signature, 64);
free(signable);

// シリアライズして送信
uint8_t buffer[2048];
int size = aether_packet_serialize(packet, buffer, sizeof(buffer));
// buffer[0..size-1] をトランスポート経由で送信

aether_packet_free(packet);
```

## ロードマップ

構築済みのものと次に来るもの。

**完了（クロス言語検証済み、全8実装）:**
- ワイヤーフォーマット: 8言語で完全なバイト同一性、14の標準フィクスチャとCIのクロス言語アサーションで確認済み（`fixtures/expected/*.bin`）
- ✅ **GitHub Actions CI** — 9ジョブのマトリックス（C#/.NET 10、Go 1.22、TypeScript/Node 20、Python 3.12、Kotlin/JVM 21、Swift/macOS-14、Rust stable、C/GCC、フィクスチャ整合性ジョブ）`.github/workflows/ci.yml`。
- Ed25519パケット署名と検証
- AES-256-GCM暗号化
- HKDF / HMACキー導出プリミティブ
- パケットシリアライズ + 署名レイアウト（LE + 4バイトint32フィールド）
- プロセス内トランスポートシミュレーター（開発とテスト用）
- RREQ/RREP、署名済みルート応答、重複排除、TTL転送を持つAODVインスパイアのルーティングサービス
- 保管移転、ジオハッシュ対応レプリケーション、72時間TTLを持つDTNストアアンドフォワードサービス
- フラッド、重複排除、自己起源ガード、レート制限（3/時間）を持つSOSブロードキャストサービス
- 拡張ポイント: `IncentiveProvider`、`BackendClient`、`FeatureFlagProvider`（Noopデフォルト）
- 全8言語で**約3,000テスト**（C# 530、TypeScript 459、Kotlin 457、Go 423、Python 387、Swift 295、C 253、Rust ~195） — CI全グリーン
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
- ✅ **C: 完全なSignalセッション** — `c/src/signal_protocol.c`の`aether_signal_service_init`、`generate_pre_key_bundle`、`process_pre_key_bundle`、`encrypt`、`decrypt`; `c/tests/test_signal_session.c`の6つの2ノードE2Eテスト。全8言語で完全なセッション対応Signal Protocolが揃いました。

**完了（C#リファレンスのみ）:**
- ✅ **デモステップ9 — MessagingService + DTNフォールバックのエンドツーエンド** — `samples/Aether.Demo.Console`では、受信者がオフラインの場合のDTNストアアンドフォワードを使った実際のSignal暗号化メッセージングのウォークスルー。
- ✅ **`Aether.Messaging` ↔ `Aether.Security` ブリッジ** — `SignalMessageEnvelopeCipher`はメッセージング層をデフォルトでエンドツーエンド暗号化にします; Signalセッションのないメッセージはキューに入れられ、安全でない状態では送信されません。
- ✅ **アダプティブビットレートストリーミング** — プロファイルA（リアルタイム）、B（ライブブロードキャスト）、C（VOD）のスペック準拠ビットレートラダーを持つ`AdaptiveBitrateController`。パブリッシャーは最高持続可能なランクを選択し（20%ヘッドルーム）、フロア以下の場合はセグメントの代わりに`StreamAbandon`（`PacketType.StreamAbandon`）を発行。`IStreamingService`は`UpdateBandwidthEstimate`と`GetCurrentBitrateRung`を公開。
- ✅ **ウォッチトゥゲザー: BitTorrentインジェスト + ChipInグループファンディング** — `TorrentInfo` / `TorrentFile`モデル; `WatchTogetherService`は`PacketType.TorrentMetadata`を処理し`TorrentReceived`を発火。`ChipInPool` / `ChipInContribution`ステートマシン（Collecting → Funded → Purchasing → Acquired / Failed / Refunded）; `IWatchTogetherService`の`StartChipInAsync` / `ContributeAsync` / `GetChipIn`。
- ✅ **自動SFUリレーによるグループビデオ通話** — `GroupVideoService` / `IGroupVideoService`。参加者が3人以下の場合はFullMeshトポロジー; `SfuThresholdParticipants`（4）で自動的にSFUに切り替えと`GroupVideoSignaling(SfuAssigned)`によるリレー再割り当て。FullMeshではファンアウト、SFUモードではリレーのみ送信。シグナリングパケットタイプ`GroupVideoSignaling = 35`。
- ✅ **BLE GATTトランスポートシミュレーション** — `SimulatedBleGattTransportService`（`IBleTransportService`）。`BleGattFramer`によるGATT MTUフレーミング（1024 B/フレーム、`[2B count][2B index][payload]`）、プロセス内静的ピアレジストリ、アドバタイズメントブロードキャスト。すべての`BleMaxPayloadBytes`制約を適用。
- ✅ **Wi-Fi Directトランスポートシミュレーション** — `SimulatedWifiDirectTransportService`（`IWifiDirectService`）。明示的な`ConnectAsync`/`DisconnectAsync`ライフサイクル、ダイレクト大容量ペイロード配信（フレーミングなし）、双方向`PeerConnected`/`PeerDisconnected`イベント。
- ✅ **NearLinkトランスポートシミュレーション** — `SimulatedNearLinkTransportService`（`INearLinkTransportService`）。4096 Bフレームのデバイス、500ピアレジストリ、`ConnectedPeerCount`、実行時設定可能な`IsAvailable`。
- ✅ **RF立ち上げシミュレーションテスト** — 2ノード相互運用テスト（`SimulatedTransportTests`）: BLE + NearLink `MeshPacket`のラウンドトリップ、WiFi Direct 64 KBペイロード転送。ソフトウェア層は完全に検証済み; ハードウェア上での検証には実機デバイスラボセッションが必要。

**完了（C#トランスポート層 — すべてフェイルファスト）:**
- ✅ **BLE GATTリアルトランスポート** — `WinBleGattTransportService`（Windows WinRT）+ `android/blue/`（Android GATTサーバー）。`samples/Aether.BleRfTest/`の完全RF立ち上げテスト。
- ✅ **Wi-Fi Directリアルトランスポート** — `WinWifiDirectTransportService`（WinRT、`WiFiDirectAdvertisementPublisher` + TCP StreamSocketポート8888）+ `android/green/`（`WifiP2pManager`）。`samples/Aether.WifiDirectRfTest/`のRFテスト。
- ✅ **HTTPリレートランスポート（Aether Purple）** — 10秒ロングポール、`PowerCostRelative = 100`、常に最終手段の`HttpRelayTransportService`。`samples/Aether.RelayServer/`のリレーサーバー（ASP.NET Coreミニマルアプリ、ポート5200）。`samples/Aether.RelayRfTest/`のRFテスト。
- ✅ **NFC（Aether White）** — `android/white/`はAID `F061657468657200`で`HostApduService`を実装。`WinNfcStubTransportService`は2つのWindowsの近似パスを文書化: (1) RSSI ゲート ≥ −40 dBm（NFCシリコンなしのタップ接続をシミュレート、`IsAvailable = Bluetooth present`）を持つNDEF-over-BLE-GATT; (2) `Windows.Devices.SmartCards` PC/SC経由のACR122U USBリーダー（`IsAvailable = contactless reader enumerated`）。アップグレードパス: MicrosoftがファーストパーティのピアツーピアNFC APIを提供したら`ITransportService`を実装。
- ✅ **NearLink（Aether Teal）** — **`harmonyos/teal/`** — `@kit.NearLinkKit`（`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`）を使用したHarmonyOS 5.0.1（API 13）ArkTS完全実装; `isAvailable`は実行時にプローブ。`WinNearLinkStubTransportService` + `android/teal/`はSSAP-over-BLE近似を文書化: Aether SLEサービスUUID `61657468-6572-0003-0000-000000000000`を持つBLE GATT — SSAPとAPI同等、本物のNearLinkハードウェアとはワイヤー互換ではない。アップグレードパス: BLE GATTコールを`ssapc_*`/`ssaps_*` SDKコールに置き換え; UUIDと`TransportManager`スロットは変更なし。
- ✅ **LoRa / CircleLink（Aether Red）** — `LoRaCircleLinkStub` + `android/red/`は、BLE 5.0 Coded PHY S=8（~1.3 km屋外）上で完全なMeshtasticワイヤーフォーマット（16バイトヘッダー + AES-256-CTR protobuf）を使用したMeshtastic-over-BLE-LR近似を文書化: マネージドフラッドルーティングとRSSI重み付けコンテンションウィンドウ付き。本物のLoRaハードウェアとのブリッジノードフェデレーションは自動的に機能します（同じMeshtasticパケットフォーマット、翻訳なし）。アップグレードパス: BLE LRラジオをSX1276/SX1278のATコマンドまたはSPIドライバに置き換え; パケットフォーマットとルーティングは変更なし。

**未解決 — `OPEN_ISSUES.md` で追跡中:**
- 実機ハードウェアでのRF立ち上げ: 物理BLE / Wi-Fi Directデバイスでのエンドツーエンド2ノード相互運用テスト（シミュレーションテストはパス; ハードウェアラボセッションが必要）
- NearLink: `harmonyos/teal/`は完成; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6ハードウェアが必要（NearLinkシリコンはHuawei以外のデバイスには搭載されていない）。Windows + AndroidはSSAP-over-BLE近似に自動フォールバック。
- LoRa / CircleLink: 真のLoRa範囲にはラジオモジュールが必要。なければMeshtasticワイヤーフォーマットはBLE LR（~1.3 km）で運ばれ、本物のLoRaハードウェアとのブリッジノードフェデレーションは利用可能。

**外部コントリビューションはまだ受け付けていません:**
- プロトコルはまだ活発に開発中です。現時点では外部コントリビューションは受け付けていません。
- NearLinkトランスポートの実装、Android/iOS統合例、追加のトランスポートバックエンド、パフォーマンスベンチマーク、プロトコルファジングは社内で追跡されており、プロジェクトが安定した公開コントリビューションポイントに達したときに公開されます。

## プロジェクト構成

```
aether-protocol/
  src/
    Aether.Core/          プロトコルモデル、定数、パケットシリアライズ
    Aether.Security/      Signal Protocol、Ed25519、パケット署名
    Aether.Transport/     トランスポート抽象化、NearLink、プロセス内シミュレーター
    Aether.Messaging/     メッセージ処理とリレー
    Aether.Storage/       DTNストアアンドフォワード永続化
    Aether.Streaming/     アダプティブビットレートストリーミング、ビデオモデルとインターフェース
    Aether.Voice/         音声通話とグループ音声
    Aether.Content/       コンテンツ検証とチャンク転送
  samples/
    Aether.Demo.Console/  インタラクティブデモ
  tests/
    Aether.Security.Tests/
    Aether.Protocol.Tests/
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

## 拡張ポイント

プロトコルはスタンドアローンで動作します。これらのインターフェースを使うと独自のバックエンドをプラグインできます:

- `IAetherIncentiveProvider` — トラフィックを中継するノードに報酬を与える（Noopデフォルト: 利他的中継）
- `IAetherBackendClient` — インターネット利用時にサーバーと同期する（Noopデフォルト: 完全オフライン）
- `IAetherFeatureFlagProvider` — 実行時にプロトコル機能を切り替える（Noopデフォルト: すべて有効）

3つすべてにNoopの実装が付属しています。削除しても何も壊れません。

## コントリビューション

外部コントリビューションはまだ受け付けていません。プロジェクトはまだ活発に開発中です。公開コントリビューションウィンドウを発表したときに確認してください。

## セキュリティ

責任ある開示ポリシーについては [SECURITY.md](SECURITY.md) を参照してください。

## ライセンス

MITライセンス。[LICENSE](LICENSE) を参照してください。
