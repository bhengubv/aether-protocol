# Быстрый старт — подключите Aether к вашему .NET-приложению за 5 минут

Это руководство проведёт вас от пустого `Program.cs` до двух узлов — Alice и Bob —
обменивающихся сообщением со сквозным шифрованием. Всё компилируется против HEAD
(`b8b3d22`) репозитория [`bhengubv/aether-protocol`](../) на .NET 10.

> Ищете полную архитектуру? См. [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> Ищете информацию о том, что защищает и не защищает криптография? См.
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Известные ограничения отслеживаются в
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. Установка

Библиотеки Aether ещё не опубликованы на NuGet. На данный момент используйте
`<ProjectReference>` к локальному репозиторию:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` транзитивно включает `AetherNet.Core`,
`AetherNet.Security`, `AetherNet.Messaging`, `AetherNet.Transport`, `AetherNet.Streaming`,
`AetherNet.Voice` и `AetherNet.Content` — всё необходимое для стека обмена сообщениями. `AetherNet.Storage` — отдельная зависимость только если вам нужна
персистентность на диске (см. раздел 6).

После публикации пакета на NuGet это примет вид:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # optional, for persistence
```

API пакетов не изменится между вариантом с project-reference и вариантом с NuGet.

---

## 2. Регистрация — каноническая регистрация полного стека

DI-расширение `AddAetherNetProtocol(...)` возвращает fluent-строитель. Каждая
возможность подключается явно: хост, которому нужна только маршрутизация, цепляет `.AddRouting()`
и останавливается. Ниже показан полный стек, который обычно нужен разработчику.

```csharp
using AetherNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // host-side prerequisite for AddHealthChecks() below
builder.Services
    .AddAetherNetProtocol(opts => opts.LocalUhid = LocalUhid)
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

`AddAetherNetProtocol` и каждый цепочечный метод идемпотентны на одном
`IServiceCollection` — двойной вызов не приводит к двойной регистрации. Порядок
важен в одном месте: `AddMessaging()` выбрасывает `InvalidOperationException`, если
ни `AddSignalProtocol()`, ни `AddRouting()` не были вызваны ранее.

`InProcessTransport` предназначен для тестов и демонстраций. В продакшне вы реализуете
`AetherNet.Transport.Abstractions.ITransportService` для вашего физического уровня (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) и регистрируете `IMeshSender`, который
передаёт пакеты на него. Сервисы Routing/DTN/Messaging затем работают неизменно
поверх.

---

## 3. Установка сеанса

X3DH асимметричен. **Инициатор** обрабатывает опубликованный пакет от
**ответчика**; сеанс ответчика автоустанавливается при получении первого
зашифрованного сообщения инициатора (сообщение «PreKey»).

```csharp
using AetherNet.Security.Services;
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

`PreKeyBundle` — это простой DTO. Хосты публикуют его как угодно — напрямую
peer-to-peer через mesh (типы пакетов `PreKeyRequest` / `PreKeyResponse`,
см. PROTOCOL_SPEC §2.5), через директорию бэкенда или вручную. Протокол не
предписывает транспорт для пакетов.

---

## 4. Отправка и получение

Кратчайший сквозной путь (без DI, без маршрутизации, только шифр):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

В продакшне вы оборачиваете зашифрованный текст в `MeshPacket`, подписываете его с помощью
`PacketSigningService.SignPacketAsync` и позволяете `MessagingService.SendAsync`
обрабатывать маршрутизацию, повторные попытки и DTN-запасной вариант:

```csharp
using AetherNet.Messaging;
using AetherNet.Messaging.Models;

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

`MessagingService` ставит сообщения в очередь — никогда не отправляет их в открытом виде — когда
с получателем ещё нет сеанса Signal. Подпишитесь на `SessionRequired`,
чтобы знать, когда нужно запросить предварительный ключ пира и вызвать
`alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. Круговой обход двух узлов в 50 строках

Это запускаемый скрипт. Скопируйте в `Program.cs`, добавьте `<ProjectReference>`
к `AetherNet.Security.csproj` (который включает `AetherNet.Core` и BCL-криптографию),
и выполните `dotnet run`.

```csharp
using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
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

Ожидаемый вывод:

```
Bob got: "hello bob"
Alice got: "ack"
```

Для более богатой сквозной демонстрации — включая подпись пакетов, многохоповую ретрансляцию
через Charlie, MessagingService и DTN-запасной вариант с передачей хранения — запустите встроенную консоль:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Шаг DTN-хранения (шаг 9 демонстрации) — канонический паттерн продакшн-подключения:
`MessagingService` + `RoutingService` + `DtnService`
в связке с адаптером `IMeshSender` поверх реального транспорта.

---

## 6. Персистентность (хранилище ключей-значений)

По умолчанию `SignalProtocolService` хранит каждый сеанс, ключ идентификации, подписанный
предварительный ключ и одноразовый предварительный ключ в памяти процесса. Сбой означает: потерю идентификатора
(невозможность расшифровать любой предыдущий сеанс), потерю пула OPK (X3DH ответчика начинает
давать сбои для новых инициаторов), потерю состояния Double Ratchet (прямая секретность
сохраняется, но нарушается порядок сообщений).

`AetherNet.Storage.FileSystemKeyValueStore` — минимальное дисковое
`IKeyValueStore` (один файл на запись, атомарное переименование через временный файл). Подключите его
через адаптеры `KeyValue*Store`:

```csharp
using AetherNet.Storage;
using AetherNet.Security.Services;

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

`FileSystemKeyValueStore` намеренно прост: без уплотнения, без межключевых транзакций,
без шифрования в состоянии покоя. Для шифрования в состоянии покоя наложите
`EncryptedKeyValueStore` поверх файловой системы (или вашего собственного KV) и предоставьте
`IDataAtRestKeyProvider` — хост владеет обёрткой ключа, не протокол.

Вы также можете зарегистрировать нестандартные `IRouteStore`, `IDtnBundleStore` и
`IMessageStore` в DI-контейнере перед цепочкой
`.AddRouting()` / `.AddDtn()` / `.AddMessaging()` — строитель использует
`TryAdd*` и уважает всё, что вы поместили в контейнер первым. Адаптеры
`KeyValueRouteStore`, `KeyValueDtnBundleStore` и `KeyValueMessageStore`
в `AetherNet.Storage` покрывают эти слоты для любого `IKeyValueStore`.

---

## 7. Наблюдаемость

Aether поставляется с первоклассной инструментацией OpenTelemetry. Подпишитесь на один
метр и один источник активностей — оба являются стабильными строками, и библиотеки
не зависят ни от какого конкретного SDK OTel:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

Что вы получите:

- **Счётчики**: `aethernet.messages.encrypted`, `aethernet.messages.decrypted`,
  `aethernet.signatures.validated`, `aethernet.signatures.rejected`,
  `aethernet.nonces.replayed`, `aethernet.timestamps.stale`,
  `aethernet.sessions.established`, `aethernet.ratchet.dh_steps`,
  `aethernet.route.requests_emitted`, `aethernet.route.replies_received`,
  `aethernet.route.cache_hits`, `aethernet.dtn.bundles_accepted`,
  `aethernet.dtn.bundles_delivered`, `aethernet.dtn.bundles_expired`,
  `aethernet.sos.broadcasts`, `aethernet.sos.rebroadcasts_suppressed`,
  `aethernet.messaging.messages_sent`, `aethernet.messaging.messages_queued`,
  `aethernet.messaging.dtn_fallback`.
- **Гистограммы** (мс): `aethernet.encrypt.latency`, `aethernet.decrypt.latency`,
  `aethernet.route.lookup_latency`, `aethernet.sign.verify_latency`.
- **Активности** с тегами UHID, очищенными от PII:
  `AetherNet.Encrypt`, `AetherNet.Decrypt`, `AetherNet.DhRatchet.Step`,
  `AetherNet.Sign.Packet`, `AetherNet.Verify.Packet`, плюс спаны маршрутизации и DTN.

Когда слушатель не подключён, горячие пути не выделяют память — `counter.Add`
деградирует до чтения volatile-переменной, а `StartActivity` возвращает `null`.

Полный инвентарь инструментов и контракт PII находятся в
`src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs`.

---

## 8. Проверки работоспособности

`AddHealthChecks()` (метод строителя Aether) регистрирует четыре проверки уровня протокола
в хостовом `HealthCheckService`. Каждая записывает структурированные данные (`data`),
полезные для дашбордов.

| Имя проверки | Что отслеживает | Healthy → Degraded → Unhealthy |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10 000 → ≥ 10 000 → ≥ 50 000 (умолчания; настраивается) |
| `aether-dtn`                | активные пакеты в хранении                                 | < 80% ёмкости → ≥ 80% → ≥ `DtnMaxBundlesPerNode` |
| `aether-signal`             | доступные OPK + количество активных сеансов                | порог OPK → unhealthy ниже `MinAvailableOpks` (умолч. 10); потолок сеансов → degraded выше 1 000 |
| `aether-messaging-outbox`   | глубина очереди + рост между измерениями                   | < 100 → ≥ 100 → ≥ 100 И растёт |

Настройка через пакеты `AetherNetOptions.Routing`, `Dtn`, `Signal` и `Messaging`. Хост должен
вызвать `services.AddHealthChecks()` перед `.AddHealthChecks()` строителя Aether для видимости
регистраций в `MapHealthChecks(...)`.

---

## 9. Что дальше

- **`docs/PROTOCOL_SPEC.md`** — формат проводного протокола, маршрутизация, обмен ключами, DTN, полная
  таблица типов пакетов и канонический алгоритм `BuildSignableData`.
- **`docs/THREAT_MODEL.md`** — от чего защищает криптография, что явно выходит за рамки,
  и допущения, на которых строятся заявления о безопасности.
- **`OPEN_ISSUES.md`** — известные ограничения, отслеживаемые элементы дорожной карты и
  разрыв в механизме сеансов языка C.
- **`SECURITY.md`** — политика ответственного раскрытия.
- **`samples/AetherNet.Demo.Console/Program.cs`** — запускаемое сквозное руководство из 9 шагов.
  Шаг 9 (MessagingService + DTN) — паттерн продакшн-подключения.
- **`fixtures/signal/`** — кросс-языковые тестовые векторы. Если вы портируете
  Aether на другой язык, это побайтово закреплённые результаты, которые ваша
  реализация должна воспроизвести.

Нашли баг? Создайте issue на GitHub. Нашли уязвимость? См. `SECURITY.md`.
