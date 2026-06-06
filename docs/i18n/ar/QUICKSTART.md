<div dir="rtl">

# دليل البدء السريع — ربط Aether بتطبيق .NET في 5 دقائق

يأخذك هذا الدليل من ملف `Program.cs` فارغ إلى عقدتين — Alice وBob —
تتبادلان رسالة مشفرة من طرف إلى طرف. كل شيء يُصرَّف بناءً على HEAD
(`b8b3d22`) من [`bhengubv/aether-protocol`](../) على .NET 10.

> هل تبحث عن البنية الكاملة؟ انظر [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> هل تبحث عن ما يحمي التشفير وما لا يحمي؟ انظر
> [`THREAT_MODEL.md`](THREAT_MODEL.md). القيود المعروفة متتبعة في
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. التثبيت

مكتبات Aether لم تُنشر بعد على NuGet. في الوقت الحالي، استخدم
`<ProjectReference>` للمستودع المحلي:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` يسحب `AetherNet.Core` و`AetherNet.Security` و`AetherNet.Messaging` و`AetherNet.Transport` و`AetherNet.Streaming` و`AetherNet.Voice` و`AetherNet.Content` بشكل متعدي — كل ما تحتاجه لمكدس المراسلة. `AetherNet.Storage` تبعية منفصلة فقط إذا أردت استمرارية مدعومة بالقرص (انظر القسم 6).

بمجرد نشر الحزمة على NuGet، يصبح هذا:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # optional, for persistence
```

واجهات برمجة الحزمة لن تتغير بين تدفق مرجع المشروع وتدفق NuGet.

---

## 2. الربط — تسجيل المكدس الكامل القانوني

امتداد DI `AddAetherNetProtocol(...)` يُرجع منشئاً fluent. كل قدرة اختيارية: مضيف يحتاج فقط التوجيه يُسلسل `.AddRouting()` ويتوقف هناك. فيما يلي المكدس الكامل الذي يريده المستخدم النموذجي.

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

`AddAetherNetProtocol` وكل طريقة مُسلسلة بعده هي idempotent على نفس `IServiceCollection` — استدعاؤها مرتين لا يُسجّل مرتين. الترتيب مهم في مكان واحد: `AddMessaging()` يُلقي `InvalidOperationException` إذا لم يُستدعَ `AddSignalProtocol()` أو `AddRouting()` أولاً.

`InProcessTransport` للاختبارات والعروض التوضيحية. في الإنتاج تُطبّق `AetherNet.Transport.Abstractions.ITransportService` للطبقة الفيزيائية (BLE GATT، Wi-Fi Direct، NearLink، LoRa، …) وتُسجّل `IMeshSender` يجسر الحزم عليها. خدمات التوجيه/DTN/المراسلة تعمل بعد ذلك دون تغيير فوقها.

---

## 3. إنشاء جلسة

X3DH غير متماثل. **المُبادر** يعالج حزمة منشورة من **المستجيب**؛ جلسة المستجيب تُنشأ تلقائياً عندما يستلم أول رسالة مشفرة من المُبادر (رسالة "PreKey").

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

`PreKeyBundle` هو DTO بسيط. يُنشره المضيفون بأي طريقة يريدون — مباشرةً من نظير لنظير عبر الشبكة (أنواع حزم `PreKeyRequest` / `PreKeyResponse`، انظر PROTOCOL_SPEC §2.5)، أو عبر دليل خلفي، أو تسليماً يدوياً. البروتوكول لا يُلزم بوسيلة نقل للحزم.

---

## 4. الإرسال والاستلام

أقصر مسار من طرف إلى طرف (بدون DI، بدون توجيه، مجرد التشفير):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

في الإنتاج تُغلّف النص المشفر في `MeshPacket`، وتوقّعه بـ`PacketSigningService.SignPacketAsync`، وتدع `MessagingService.SendAsync` يتولى التوجيه وإعادة المحاولات وDTN كبديل:

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

`MessagingService` يُضيف الرسائل إلى قائمة انتظار — ولا يُرسلها أبداً كنص عادي — عندما لا توجد جلسة Signal بعد مع المستلم. اشترك في `SessionRequired` لمعرفة متى تجلب حزمة المفاتيح الأولية لنظير وتستدعي `alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. رحلة ذهاباً وإياباً بين عقدتين في 50 سطراً

هذا سكريبت قابل للتشغيل. انسخه في `Program.cs`، أضف `<ProjectReference>` إلى `AetherNet.Security.csproj` (الذي يسحب `AetherNet.Core` وتشفير BCL)، وشغّل `dotnet run`.

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

المخرجات المتوقعة:

```
Bob got: "hello bob"
Alice got: "ack"
```

للحصول على عرض توضيحي أغنى من طرف إلى طرف — يشمل توقيع الحزم، وترحيل متعدد القفزات عبر Charlie، وMessagingService، وDTN custody كبديل — شغّل وحدة التحكم المُرفقة:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

خطوة حضانة DTN (الخطوة 9 من العرض التوضيحي) هي النمط القانوني للربط الإنتاجي: `MessagingService` + `RoutingService` + `DtnService` مُجمَّعة مقابل محول `IMeshSender` فوق وسيلة النقل الحقيقية.

---

## 6. الاستمرارية (مخزن مفتاح-قيمة)

افتراضياً `SignalProtocolService` يحفظ كل جلسة، ومفتاح هوية، ومفتاح أولي موقَّع، ومفتاح أحادي الاستخدام في ذاكرة العملية. تعني الأعطال: فقدان الهوية (لا يمكن فك تشفير أي جلسة سابقة)، وفقدان مجموعة OPK (X3DH للمستجيب يبدأ في الفشل للمُبادرين الجدد)، وفقدان حالة Double Ratchet (السرية الأمامية سليمة لكن ترتيب الرسائل يتعطل).

`AetherNet.Storage.FileSystemKeyValueStore` هو `IKeyValueStore` بسيط مدعوم بالقرص (ملف واحد لكل مدخلة، إعادة تسمية ذرية عبر ملف مؤقت). ربطه عبر محولات `KeyValue*Store`:

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

`FileSystemKeyValueStore` بسيط عمداً: لا ضغط، ولا معاملات متعددة المفاتيح، ولا تشفير في حالة الراحة. لتشفير في حالة الراحة، طبّق طبقة `EncryptedKeyValueStore` فوق نظام الملفات (أو مخزن KV الخاص بك) وزوّد `IDataAtRestKeyProvider` — المضيف يملك مُغلّف المفتاح، وليس البروتوكول.

يمكنك أيضاً تسجيل `IRouteStore` و`IDtnBundleStore` و`IMessageStore` غير الافتراضية مقابل حاوية DI قبل تسلسل `.AddRouting()` / `.AddDtn()` / `.AddMessaging()` — يستخدم المنشئ `TryAdd*` ويحترم ما وضعته في الحاوية أولاً.

---

## 7. قابلية الرصد

يشحن Aether بأدوات OpenTelemetry من الدرجة الأولى. اشترك في عدّاد واحد ومصدر نشاط واحد — كلاهما سلاسل ثابتة والمكتبات لا تعتمد على أي SDK محدد لـOTel:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

ما تحصل عليه:

- **العدادات**: `aethernet.messages.encrypted`، `aethernet.messages.decrypted`،
  `aethernet.signatures.validated`، `aethernet.signatures.rejected`،
  `aethernet.nonces.replayed`، `aethernet.timestamps.stale`،
  `aethernet.sessions.established`، `aethernet.ratchet.dh_steps`،
  `aethernet.route.requests_emitted`، `aethernet.route.replies_received`،
  `aethernet.route.cache_hits`، `aethernet.dtn.bundles_accepted`،
  `aethernet.dtn.bundles_delivered`، `aethernet.dtn.bundles_expired`،
  `aethernet.sos.broadcasts`، `aethernet.sos.rebroadcasts_suppressed`،
  `aethernet.messaging.messages_sent`، `aethernet.messaging.messages_queued`،
  `aethernet.messaging.dtn_fallback`.
- **المدرجات التكرارية** (بالملي ثانية): `aethernet.encrypt.latency`، `aethernet.decrypt.latency`،
  `aethernet.route.lookup_latency`، `aethernet.sign.verify_latency`.
- **الأنشطة** مع وسوم UHID معقّمة من PII:
  `AetherNet.Encrypt`، `AetherNet.Decrypt`، `AetherNet.DhRatchet.Step`،
  `AetherNet.Sign.Packet`، `AetherNet.Verify.Packet`، بالإضافة إلى نطاقات التوجيه وDTN.

عندما لا يكون هناك مستمع مُرتبط، لا تُخصص المسارات الساخنة شيئاً — `counter Add` يُخفَّض إلى قراءة volatile و`StartActivity` يُرجع `null`.

مخزون الأدوات الكامل وعقد PII موجودان في
`src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs`.

---

## 8. فحوصات الصحة

`AddHealthChecks()` (طريقة منشئ Aether) تُسجّل أربعة فحوصات على مستوى البروتوكول مقابل `HealthCheckService` الخاص بالمضيف. كل منها يكتب `data` منظمة مفيدة للوحات التحكم.

| اسم الفحص | ما يراقبه | صحي → متدهور → غير صحي |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing` | `IRoutingService.GetAllRoutes().Count` | < 10,000 → ≥ 10,000 → ≥ 50,000 (افتراضيات؛ قابلة للضبط) |
| `aether-dtn` | الحزم النشطة في الحضانة | < 80% من الطاقة → ≥ 80% → ≥ `DtnMaxBundlesPerNode` |
| `aether-signal` | OPKs المتاحة + عدد الجلسات النشطة | حد OPK الأدنى → غير صحي دون `MinAvailableOpks` (افتراضي 10)؛ حد الجلسة الأقصى → متدهور فوق 1,000 |
| `aether-messaging-outbox` | عمق صندوق الصادر المعلق + النمو بين العينات | < 100 → ≥ 100 → ≥ 100 وينمو |

اضبط عبر أكياس `AetherNetOptions.Routing` و`Dtn` و`Signal` و`Messaging`. يجب على المضيف استدعاء `services.AddHealthChecks()` قبل `.AddHealthChecks()` الخاص بمنشئ Aether لتكون التسجيلات مرئية لـ`MapHealthChecks(...)`.

---

## 9. ما التالي

- **`docs/PROTOCOL_SPEC.md`** — تنسيق الأسلاك والتوجيه وتبادل المفاتيح وDTN وجدول أنواع الحزم الكامل وخوارزمية `BuildSignableData` القانونية.
- **`docs/THREAT_MODEL.md`** — ما يدافع عنه التشفير، وما هو خارج النطاق صراحةً، والافتراضات التي تعتمد عليها ادعاءات الأمان.
- **`OPEN_ISSUES.md`** — القيود المعروفة، وعناصر خارطة الطريق المتتبعة، وفجوة آلية جلسة لغة C.
- **`SECURITY.md`** — سياسة الإفصاح المسؤول.
- **`samples/AetherNet.Demo.Console/Program.cs`** — جولة كاملة قابلة للتشغيل بـ9 خطوات من طرف إلى طرف. الخطوة 9 (MessagingService + DTN) هي نمط الربط الإنتاجي.
- **`fixtures/signal/`** — متجهات اختبار متعددة اللغات. إذا كنت تنقل Aether إلى لغة أخرى، هذه هي المخرجات المثبتة بالبايت التي يجب أن يُطابقها تطبيقك.

وجدت خطأ؟ سجّله على GitHub. وجدت ثغرة أمنية؟ انظر `SECURITY.md`.

</div>
