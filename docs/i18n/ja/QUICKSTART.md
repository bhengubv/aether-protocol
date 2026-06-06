# クイックスタート — .NETアプリにAetherを5分で組み込む

このガイドでは空の `Program.cs` から始めて、エンドツーエンド暗号化されたメッセージを交換する2つのノード — AliceとBob — を作成するまでを説明します。すべて [`bhengubv/aether-protocol`](../) のHEAD（`b8b3d22`）を .NET 10 でコンパイルします。

> 完全なアーキテクチャをお探しですか？ [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) を参照してください。
> 暗号が何を保護し、何を保護しないかをお探しですか？ [`THREAT_MODEL.md`](THREAT_MODEL.md) を参照してください。既知の制限は [`OPEN_ISSUES.md`](../OPEN_ISSUES.md) で追跡されています。

---

## 1. インストール

Aetherライブラリはまだ NuGet で公開されていません。現時点では、ローカルリポジトリへの `<ProjectReference>` を使用してください:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` は `AetherNet.Core`、`AetherNet.Security`、`AetherNet.Messaging`、`AetherNet.Transport`、`AetherNet.Streaming`、`AetherNet.Voice`、`AetherNet.Content` を推移的に取り込みます — メッセージングスタックに必要なすべてが揃います。`AetherNet.Storage` はディスクバックドの永続化が必要な場合のみ別途依存します（セクション6参照）。

パッケージが NuGet で公開されると、次のようになります:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # オプション、永続化用
```

プロジェクト参照フローと NuGet フローでパッケージのAPIは変わりません。

---

## 2. 接続 — 標準的なフルスタック登録

DI拡張 `AddAetherNetProtocol(...)` はフルエントビルダーを返します。各機能はオプトイン: ルーティングだけ必要なホストは `.AddRouting()` をチェーンしてそこで止めます。以下は典型的な採用者が求めるフルスタックです。

```csharp
using AetherNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // 以下のAddHealthChecks()のホスト側前提条件
builder.Services
    .AddAetherNetProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (ISignalProtocolService, IPacketSigningServiceを登録)
    .AddRouting()                            // AODVスタイルのRREQ/RREP + InMemoryRouteStore
    .AddDtn()                                // 72時間ストアアンドフォワード保管 + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // 緊急フラッド
    .AddMessaging()                          // 1対1暗号化メッセージ、AddSignalProtocol + AddRoutingが必要
    .AddInProcessTransport(LocalUhid)        // インメモリシミュレーター（本番ではBLE / Wi-Fi Directに置き換え）
    .AddHealthChecks();                      // 4つのプロトコルレベルのIHealthCheck登録

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherNetProtocol` と連鎖したすべてのメソッドは同じ `IServiceCollection` 上でべき等です — 2回呼び出しても二重登録されません。順序が重要な箇所が1つあります: `AddSignalProtocol()` または `AddRouting()` が先に呼ばれていない場合、`AddMessaging()` は `InvalidOperationException` をスローします。

`InProcessTransport` はテストとデモ用です。本番では物理層（BLE GATT、Wi-Fi Direct、NearLink、LoRaなど）向けに `AetherNet.Transport.Abstractions.ITransportService` を実装し、パケットをそこにブリッジする `IMeshSender` を登録します。Routing/DTN/Messagingサービスはその上で変更なく動作します。

---

## 3. セッションの確立

X3DHは非対称です。**イニシエーター**は**レスポンダー**から公開されたバンドルを処理します; レスポンダーのセッションはイニシエーターの最初の暗号化メッセージ（「PreKeyメッセージ」）を受信したときに自動的に確立されます。

```csharp
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bobがバンドルを公開: アイデンティティキー + 署名済みプレキー + 1つのワンタイムプレキー。
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Aliceがバンドルを処理。4つのX25519 DHが実行され、結果のルートキーが
// 彼女のDouble Ratchet送信チェーンをシードします。
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — 最初に受信したメッセージで自動確立
```

`PreKeyBundle` はプレーンなDTOです。ホストは好きな方法で公開できます — メッシュ経由の直接ピアツーピア（`PreKeyRequest` / `PreKeyResponse` パケットタイプ、PROTOCOL_SPEC §2.5参照）、バックエンドディレクトリ経由、または手渡し。プロトコルはバンドルのトランスポートを強制しません。

---

## 4. 送受信

最短のエンドツーエンドパス（DIなし、ルーティングなし、暗号のみ）:

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// 暗号文をトランスポート経由でワイヤリングします。Bob側:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

本番では暗号文を `MeshPacket` でラップし、`PacketSigningService.SignPacketAsync` で署名し、`MessagingService.SendAsync` にルーティング、リトライ、DTNフォールバックを処理させます:

```csharp
using AetherNet.Messaging;
using AetherNet.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContentはメッセージング層によって既に復号済みです。
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> 暗号文はメッシュ、DTN、またはバックエンドリレー経由で送信済み
// handed == false -> 送信ボックスにキュー; ProcessOutboxAsyncが再試行します
```

`MessagingService` は受信者との Signal セッションがまだ存在しない場合、メッセージをクリアテキストで送信せずにキューに入れます。`SessionRequired` をサブスクライブして、ピアのプレキーバンドルをフェッチして `alice.ProcessPreKeyBundleAsync(...)` を呼び出すタイミングを知ります。

---

## 5. 50行での2ノードラウンドトリップ

これは実行可能なスクリプトです。`Program.cs` にコピーし、`AetherNet.Security.csproj`（`AetherNet.Core`とBCL暗号を取り込む）への `<ProjectReference>` を追加し、`dotnet run` を実行します。

```csharp
using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bobがバンドルを公開; Aliceが処理します。この後、AliceはBobに暗号化できます;
// BobのセッションはAliceの最初のメッセージ（X3DHメタデータを「PreKeyメッセージ」として運ぶ）
// を復号するときに自動確立されます。
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// 本番: `outbound` をシリアライズ（またはMeshPacketでラップして
// PacketSigningService.SignPacketAsyncを呼び出し）し、トランスポート経由でバイトを送信します。
// 受信側はEncryptedPayloadを再構築してDecryptAsyncを呼び出します。
// ここでは両ノードがプロセスを共有するので直接レコードを渡します。
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (セッションは双方向でライブ) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

期待される出力:

```
Bob got: "hello bob"
Alice got: "ack"
```

パケット署名、Charlie経由のマルチホップリレー、MessagingService、DTN保管フォールバックを含む、より豊富なエンドツーエンドデモは付属のコンソールを実行してください:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

DTN保管ステップ（デモのステップ9）は本番接続のための標準パターンです: `MessagingService` + `RoutingService` + `DtnService` を実トランスポート上の `IMeshSender` アダプターに対して組み合わせます。

---

## 6. 永続化（キー値ストア）

デフォルトでは `SignalProtocolService` はすべてのセッション、アイデンティティキー、署名済みプレキー、ワンタイムプレキーをプロセスメモリに保持します。クラッシュが意味すること: アイデンティティの喪失（以前のセッションを復号できない）、OPKプールの喪失（新しいイニシエーターのレスポンダーX3DHが失敗し始める）、Double Ratchet状態の喪失（前方秘匿性は保たれますがメッセージ順序が崩れます）。

`AetherNet.Storage.FileSystemKeyValueStore` は最小限のディスクバックドの `IKeyValueStore`（エントリ1つにつき1ファイル、アトミックな一時ファイルリネーム）です。`KeyValue*Store` アダプター経由で接続します:

```csharp
using AetherNet.Storage;
using AetherNet.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// アイデンティティ、セッション、プレキーがすべて再起動後も保持されるように
// 同じKVストアを両方のアダプターに接続します。
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStoreは内部です — KeyValueSignalSessionStoreも内部です。
// Wave-3+ホストでは、コンポジションルート経由で永続状態対応の
// SignalProtocolServiceコンストラクターを登録します（またはデフォルトの
// AddSignalProtocol()登録を独自のファクトリーで置き換えます）。
```

`FileSystemKeyValueStore` は意図的にシンプルです: コンパクションなし、クロスキートランザクションなし、保存時暗号化なし。保存時暗号化の場合は `EncryptedKeyValueStore` をファイルシステム（または独自のKV）の上に重ねて、`IDataAtRestKeyProvider` を提供します — ホストがキーラッパーを所有し、プロトコルではありません。

DIコンテナに `.AddRouting()` / `.AddDtn()` / `.AddMessaging()` のチェーン前に非デフォルトの `IRouteStore`、`IDtnBundleStore`、`IMessageStore` を登録することもできます — ビルダーは `TryAdd*` を使用し、最初にコンテナに入れたものを尊重します。`AetherNet.Storage` の `KeyValueRouteStore`、`KeyValueDtnBundleStore`、`KeyValueMessageStore` アダプターは任意の `IKeyValueStore` に対してこれらのスロットをカバーします。

---

## 7. オブザーバビリティ

AetherはファーストクラスのOpenTelemetryインストゥルメンテーションを搭載しています。1つのメーターと1つのアクティビティソースをサブスクライブします — どちらも安定した文字列で、ライブラリは特定のOTel SDKに依存しません:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

取得できるもの:

- **カウンター**: `aethernet.messages.encrypted`、`aethernet.messages.decrypted`、
  `aethernet.signatures.validated`、`aethernet.signatures.rejected`、
  `aethernet.nonces.replayed`、`aethernet.timestamps.stale`、
  `aethernet.sessions.established`、`aethernet.ratchet.dh_steps`、
  `aethernet.route.requests_emitted`、`aethernet.route.replies_received`、
  `aethernet.route.cache_hits`、`aethernet.dtn.bundles_accepted`、
  `aethernet.dtn.bundles_delivered`、`aethernet.dtn.bundles_expired`、
  `aethernet.sos.broadcasts`、`aethernet.sos.rebroadcasts_suppressed`、
  `aethernet.messaging.messages_sent`、`aethernet.messaging.messages_queued`、
  `aethernet.messaging.dtn_fallback`。
- **ヒストグラム**（ms）: `aethernet.encrypt.latency`、`aethernet.decrypt.latency`、
  `aethernet.route.lookup_latency`、`aethernet.sign.verify_latency`。
- **PIIサニタイズされたUHIDタグ付きのアクティビティ**:
  `AetherNet.Encrypt`、`AetherNet.Decrypt`、`AetherNet.DhRatchet.Step`、
  `AetherNet.Sign.Packet`、`AetherNet.Verify.Packet`、ルーティングとDTNスパン。

リスナーが接続されていない場合、ホットパスは何も割り当てません — カウンターの `Add` はボラタイル読み取りに劣化し、`StartActivity` は `null` を返します。

完全なインストゥルメントインベントリとPII契約は `src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs` にあります。

---

## 8. ヘルスチェック

`AddHealthChecks()`（Aetherビルダーメソッド）はホストの `HealthCheckService` に4つのプロトコルレベルのチェックを登録します。各チェックはダッシュボードに役立つ構造化された `data` を書き込みます。

| チェック名 | 監視対象 | 正常 → 劣化 → 異常 |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing` | `IRoutingService.GetAllRoutes().Count` | < 10 000 → ≥ 10 000 → ≥ 50 000（デフォルト; 調整可能） |
| `aether-dtn` | 保管中のアクティブバンドル数 | < 80%容量 → ≥ 80% → ≥ `DtnMaxBundlesPerNode` |
| `aether-signal` | 利用可能なOPK数 + アクティブセッション数 | OPKフロア → `MinAvailableOpks`（デフォルト10）以下で異常; セッション上限 → 1,000超で劣化 |
| `aether-messaging-outbox` | 保留中の送信ボックスの深さ + サンプル間の増加 | < 100 → ≥ 100 → ≥ 100かつ増加中 |

`AetherNetOptions.Routing`、`Dtn`、`Signal`、`Messaging` バッグで調整します。Aetherビルダーの `.AddHealthChecks()` が `MapHealthChecks(...)` に表示されるためには、ホストが事前に `services.AddHealthChecks()` を呼び出す必要があります。

---

## 9. 次のステップ

- **`docs/PROTOCOL_SPEC.md`** — ワイヤーフォーマット、ルーティング、キー交換、DTN、完全なパケットタイプテーブル、標準的な `BuildSignableData` アルゴリズム。
- **`docs/THREAT_MODEL.md`** — 暗号が何を守るか、明示的にスコープ外のもの、セキュリティクレームが依存する前提条件。
- **`OPEN_ISSUES.md`** — 既知の制限、追跡中のロードマップ項目、C言語セッションメカニズムのギャップ。
- **`SECURITY.md`** — 責任ある開示ポリシー。
- **`samples/AetherNet.Demo.Console/Program.cs`** — 実行可能な9ステップのエンドツーエンドウォークスルー。ステップ9（MessagingService + DTN）は本番接続パターンです。
- **`fixtures/signal/`** — クロス言語テストベクター。Aetherを別の言語に移植する場合、これらが実装で一致させなければならないバイト固定の出力です。

バグを見つけたら？GitHubに報告してください。脆弱性を見つけたら？`SECURITY.md` を参照してください。
