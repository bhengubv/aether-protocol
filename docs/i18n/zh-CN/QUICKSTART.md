# 快速入门 — 5 分钟内将 Aether 接入你的 .NET 应用

本指南带你从空白的 `Program.cs` 出发，实现两个节点——Alice 和 Bob——交换端到端加密消息。所有内容均针对 [`bhengubv/aether-protocol`](../) 的 HEAD（`b8b3d22`）在 .NET 10 上编译。

> 想了解完整架构？请参见 [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md)。
> 想了解加密保护和不保护的内容？请参见
> [`THREAT_MODEL.md`](THREAT_MODEL.md)。已知限制追踪于
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md)。

---

## 1. 安装

Aether 库尚未在 NuGet 上发布。目前，请使用 `<ProjectReference>` 指向本地仓库：

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/Aether.DependencyInjection/Aether.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/Aether.Storage/Aether.Storage.csproj" />
</ItemGroup>
```

`Aether.DependencyInjection` 会传递引入 `Aether.Core`、
`Aether.Security`、`Aether.Messaging`、`Aether.Transport`、`Aether.Streaming`、
`Aether.Voice` 和 `Aether.Content`——消息栈所需的一切。`Aether.Storage` 是一个单独的依赖项，仅当你需要磁盘持久化时才需要（参见第 6 节）。

软件包发布到 NuGet 后，安装将变为：

```bash
dotnet add package Aether.DependencyInjection
dotnet add package Aether.Storage   # optional, for persistence
```

项目引用方式与 NuGet 方式之间的包 API 不会发生变化。

---

## 2. 接线——规范全栈注册

DI 扩展方法 `AddAetherProtocol(...)` 返回一个流式构建器。每个能力都是可选的：只需要路由的主机调用 `.AddRouting()` 后即可停止。以下是典型采用者所需的完整栈。

```csharp
using Aether.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // host-side prerequisite for AddHealthChecks() below
builder.Services
    .AddAetherProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (registers ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // AODV-style RREQ/RREP + InMemoryRouteStore
    .AddDtn()                                // 72h store-and-forward custody + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // emergency flood
    .AddMessaging()                          // 1-to-1 encrypted messages, requires AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // in-memory simulator (replace with BLE / Wi-Fi Direct in production)
    .AddHealthChecks();                      // four protocol-level IHealthCheck registrations

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherProtocol` 以及每个链式方法在同一 `IServiceCollection` 上是幂等的——调用两次不会重复注册。顺序在一处有影响：如果未先调用 `AddSignalProtocol()` 或 `AddRouting()`，`AddMessaging()` 会抛出 `InvalidOperationException`。

`InProcessTransport` 用于测试和演示。在生产环境中，你需要为物理层（BLE GATT、Wi-Fi Direct、NearLink、LoRa……）实现 `Aether.Transport.Abstractions.ITransportService`，并注册一个 `IMeshSender` 将数据包桥接到它上面。路由/DTN/消息服务随后在其之上不变地运行。

---

## 3. 建立会话

X3DH 是非对称的。**发起方**处理**响应方**发布的包；响应方的会话在收到发起方的第一条加密消息（"PreKey 消息"）时自动建立。

```csharp
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle: identity key + signed pre-key + one one-time pre-key.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice processes the bundle. Four X25519 DHs run; the resulting root key
// seeds her Double Ratchet sending chain.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — auto-establishes on first received message
```

`PreKeyBundle` 是一个普通 DTO。主机可以通过任何方式发布它——直接通过网格点对点（`PreKeyRequest` / `PreKeyResponse` 数据包类型，参见 PROTOCOL_SPEC §2.5）、通过后端目录，或手动传递。协议不强制规定包的传输方式。

---

## 4. 发送和接收

最短的端到端路径（无 DI，无路由，仅加密器）：

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

在生产环境中，你需要将密文封装在 `MeshPacket` 中，用 `PacketSigningService.SignPacketAsync` 签名，并让 `MessagingService.SendAsync` 处理路由、重试和 DTN 回退：

```csharp
using Aether.Messaging;
using Aether.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent has already been decrypted by the messaging layer.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> ciphertext exited via the mesh, DTN, or backend relay
// handed == false -> queued in the outbox; ProcessOutboxAsync will retry
```

当与接收方尚不存在 Signal 会话时，`MessagingService` 会将消息加入队列——永不以明文发送。订阅 `SessionRequired` 以了解何时获取对等方的预密钥包并调用 `alice.ProcessPreKeyBundleAsync(...)`。

---

## 5. 50 行内完成双节点往返

这是一个可运行的脚本。将其复制到 `Program.cs`，添加对 `Aether.Security.csproj` 的 `<ProjectReference>`（它会引入 `Aether.Core` 和 BCL 加密），然后 `dotnet run`。

```csharp
using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle; Alice processes it. After this, Alice can encrypt
// to Bob; Bob's session auto-establishes when he decrypts Alice's first
// message (which carries X3DH metadata as a "PreKey message").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Production: serialize `outbound` (or wrap in a MeshPacket and call
// PacketSigningService.SignPacketAsync) and ship the bytes over your
// transport. The receiver reconstructs the EncryptedPayload and calls
// DecryptAsync. Here both nodes share a process so we just hand the
// record across.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (session is now live in both directions) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

预期输出：

```
Bob got: "hello bob"
Alice got: "ack"
```

如需更丰富的端到端演示——包括数据包签名、通过 Charlie 的多跳中继、MessagingService 和 DTN 保管回退——请运行捆绑的控制台：

```bash
dotnet run --project samples/Aether.Demo.Console
```

DTN 保管步骤（演示的第 9 步）是生产接线的规范模式：`MessagingService` + `RoutingService` + `DtnService` 通过 `IMeshSender` 适配器对真实传输进行组合。

---

## 6. 持久化（键值存储）

默认情况下，`SignalProtocolService` 将每个会话、身份密钥、签名预密钥和一次性预密钥保存在进程内存中。崩溃意味着：身份丢失（无法解密任何先前会话）、OPK 池丢失（新发起方的响应方 X3DH 开始失败）、双棘轮状态丢失（前向保密性完好，但消息排序会中断）。

`Aether.Storage.FileSystemKeyValueStore` 是一个最小的磁盘支持的 `IKeyValueStore`（每个条目一个文件，原子临时文件重命名）。通过 `KeyValue*Store` 适配器接入：

```csharp
using Aether.Storage;
using Aether.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Plug the same KV store into BOTH adapters so identity, sessions, and
// pre-keys all survive a restart.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore is internal — KeyValueSignalSessionStore is also internal.
// In a Wave-3+ host, register the persistent-state-aware SignalProtocolService
// constructor through your composition root (or replace the default
// AddSignalProtocol() registration with your own factory).
```

`FileSystemKeyValueStore` 设计上保持简单：无压缩，无跨键事务，无静态加密。对于静态加密，请在文件系统（或你自己的 KV）之上层叠 `EncryptedKeyValueStore`，并提供 `IDataAtRestKeyProvider`——主机拥有密钥包装器，而非协议。

你也可以在链式调用 `.AddRouting()` / `.AddDtn()` / `.AddMessaging()` 之前，向 DI 容器注册非默认的 `IRouteStore`、`IDtnBundleStore` 和 `IMessageStore`——构建器使用 `TryAdd*` 并尊重你首先放入容器的内容。`Aether.Storage` 中的 `KeyValueRouteStore`、`KeyValueDtnBundleStore` 和 `KeyValueMessageStore` 适配器可针对任意 `IKeyValueStore` 覆盖这些插槽。

---

## 7. 可观测性

Aether 内置一流的 OpenTelemetry 检测。订阅一个计量器和一个活动源——两者都是稳定字符串，这些库不依赖任何特定的 OTel SDK：

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Aether.Protocol"))
    .WithTracing(t => t.AddSource("Aether.Protocol"));
```

你将获得：

- **计数器**：`aether.messages.encrypted`、`aether.messages.decrypted`、
  `aether.signatures.validated`、`aether.signatures.rejected`、
  `aether.nonces.replayed`、`aether.timestamps.stale`、
  `aether.sessions.established`、`aether.ratchet.dh_steps`、
  `aether.route.requests_emitted`、`aether.route.replies_received`、
  `aether.route.cache_hits`、`aether.dtn.bundles_accepted`、
  `aether.dtn.bundles_delivered`、`aether.dtn.bundles_expired`、
  `aether.sos.broadcasts`、`aether.sos.rebroadcasts_suppressed`、
  `aether.messaging.messages_sent`、`aether.messaging.messages_queued`、
  `aether.messaging.dtn_fallback`。
- **直方图**（毫秒）：`aether.encrypt.latency`、`aether.decrypt.latency`、
  `aether.route.lookup_latency`、`aether.sign.verify_latency`。
- **活动**（带 PII 脱敏 UHID 标签）：
  `Aether.Encrypt`、`Aether.Decrypt`、`Aether.DhRatchet.Step`、
  `Aether.Sign.Packet`、`Aether.Verify.Packet`，以及路由和 DTN 跨度。

当没有监听器附加时，热路径不分配内存——计数器 `Add` 退化为 volatile 读取，`StartActivity` 返回 `null`。

完整的检测清单和 PII 契约位于 `src/Aether.Core/Diagnostics/AetherTelemetry.cs`。

---

## 8. 健康检查

`AddHealthChecks()`（Aether 构建器方法）向主机的 `HealthCheckService` 注册四个协议级检查。每个检查都写入对仪表板有用的结构化 `data`。

| 检查名称 | 监控内容 | 健康 → 降级 → 不健康 |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing` | `IRoutingService.GetAllRoutes().Count` | < 10 000 → ≥ 10 000 → ≥ 50 000（默认值；可调整） |
| `aether-dtn` | 保管中的活跃包数 | < 80% 容量 → ≥ 80% → ≥ `DtnMaxBundlesPerNode` |
| `aether-signal` | 可用 OPK 数 + 活跃会话数 | OPK 下限 → 低于 `MinAvailableOpks`（默认 10）时不健康；会话上限 → 超过 1,000 时降级 |
| `aether-messaging-outbox` | 待处理发件箱深度 + 样本间增长 | < 100 → ≥ 100 → ≥ 100 且持续增长 |

通过 `AetherOptions.Routing`、`Dtn`、`Signal` 和 `Messaging` 配置包进行调整。主机必须在 Aether 构建器的 `.AddHealthChecks()` 之前调用 `services.AddHealthChecks()`，才能使注册对 `MapHealthChecks(...)` 可见。

---

## 9. 下一步

- **`docs/PROTOCOL_SPEC.md`** — 线路格式、路由、密钥交换、DTN、完整数据包类型表，以及规范 `BuildSignableData` 算法。
- **`docs/THREAT_MODEL.md`** — 加密防御的内容、明确超出范围的内容，以及安全声明所依赖的假设。
- **`OPEN_ISSUES.md`** — 已知限制、追踪中的路线图项目，以及 C 语言会话机制缺口。
- **`SECURITY.md`** — 负责任披露政策。
- **`samples/Aether.Demo.Console/Program.cs`** — 可运行的 9 步端到端演练。步骤 9（MessagingService + DTN）是生产接线模式。
- **`fixtures/signal/`** — 跨语言测试向量。如果你正在将 Aether 移植到另一种语言，这些是你的实现必须匹配的字节固定输出。

发现了 bug？请在 GitHub 上提交。发现了漏洞？请参见 `SECURITY.md`。
