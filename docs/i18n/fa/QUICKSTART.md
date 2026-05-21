<div dir="rtl">

# شروع سریع — اتصال Aether به برنامه .NET در ۵ دقیقه

این راهنما شما را از یک `Program.cs` خالی به دو گره — Alice و Bob —
می‌رساند که یک پیام رمزگذاری‌شده انتها-به-انتها مبادله می‌کنند. همه چیز
بر اساس HEAD (`b8b3d22`) از
[`bhengubv/aether-protocol`](../) روی .NET 10 کامپایل می‌شود.

> به دنبال معماری کامل هستید؟ [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) را ببینید.
> می‌خواهید بدانید چه چیزی را رمزنگاری محافظت می‌کند و چه چیزی نه؟ [`THREAT_MODEL.md`](THREAT_MODEL.md) را ببینید. محدودیت‌های شناخته‌شده در [`OPEN_ISSUES.md`](../OPEN_ISSUES.md) ردیابی می‌شوند.

---

## ۱. نصب

کتابخانه‌های Aether هنوز روی NuGet منتشر نشده‌اند. فعلاً یک
`<ProjectReference>` به مخزن محلی بگیرید:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/Aether.DependencyInjection/Aether.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/Aether.Storage/Aether.Storage.csproj" />
</ItemGroup>
```

`Aether.DependencyInjection` به‌صورت transitively `Aether.Core`،
`Aether.Security`، `Aether.Messaging`، `Aether.Transport`، `Aether.Streaming`،
`Aether.Voice` و `Aether.Content` را وارد می‌کند — همه چیز برای پشته پیام‌رسانی. `Aether.Storage` یک وابستگی جداگانه است فقط اگر پایداری مبتنی بر دیسک می‌خواهید (بخش ۶ را ببینید).

وقتی پکیج روی NuGet منتشر شود، این تبدیل می‌شود به:

```bash
dotnet add package Aether.DependencyInjection
dotnet add package Aether.Storage   # اختیاری، برای پایداری
```

API پکیج‌ها بین جریان project-reference و NuGet تغییر نمی‌کنند.

---

## ۲. اتصال — ثبت کامل پشته استاندارد

پسوند DI `AddAetherProtocol(...)` یک builder fluent برمی‌گرداند. هر
قابلیت opt-in است: یک host که فقط به مسیریابی نیاز دارد `.AddRouting()`
را زنجیر می‌کند و همانجا می‌ایستد. در زیر پشته کاملی است که یک پذیرنده معمولی می‌خواهد.

```csharp
using Aether.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // پیش‌نیاز سمت host برای AddHealthChecks() زیر
builder.Services
    .AddAetherProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (ISignalProtocolService، IPacketSigningService را ثبت می‌کند)
    .AddRouting()                            // RREQ/RREP به‌سبک AODV + InMemoryRouteStore
    .AddDtn()                                // حضانت store-and-forward 72 ساعته + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // flood اضطراری
    .AddMessaging()                          // پیام‌های رمزگذاری‌شده ۱-به-۱، نیاز به AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // شبیه‌ساز درون‌حافظه (در تولید با BLE / Wi-Fi Direct جایگزین کنید)
    .AddHealthChecks();                      // چهار ثبت IHealthCheck در سطح پروتکل

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherProtocol` و هر متد زنجیرشده روی همان
`IServiceCollection` idempotent هستند — فراخوانی دو بار آن‌ها را دو بار ثبت نمی‌کند. ترتیب در یک جا مهم است: `AddMessaging()` در صورتی که `AddSignalProtocol()` یا `AddRouting()` قبلاً فراخوانی نشده باشند `InvalidOperationException` می‌اندازد.

`InProcessTransport` برای آزمون‌ها و دموهاست. در تولید
`Aether.Transport.Abstractions.ITransportService` را برای لایه فیزیکی خود (BLE
GATT، Wi-Fi Direct، NearLink، LoRa، …) پیاده‌سازی می‌کنید و یک `IMeshSender` ثبت می‌کنید که بسته‌ها را به آن پل می‌زند. سرویس‌های Routing/DTN/Messaging سپس بدون تغییر بر روی آن اجرا می‌شوند.

---

## ۳. برقراری جلسه

X3DH نامتقارن است. **آغازگر** یک بسته منتشرشده از **پاسخگو** را پردازش می‌کند؛ جلسه پاسخگو به‌طور خودکار وقتی اولین پیام رمزگذاری‌شده آغازگر را دریافت می‌کند (یک "پیام PreKey") برقرار می‌شود.

```csharp
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob یک بسته منتشر می‌کند: کلید هویتی + کلید پیش‌امضاشده + یک کلید پیش‌پرداخت یک‌بار مصرف.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice بسته را پردازش می‌کند. چهار عملیات DH با X25519 اجرا می‌شوند؛ کلید ریشه حاصل
// زنجیره ارسال Double Ratchet او را seed می‌کند.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — با اولین پیام دریافتی به‌طور خودکار برقرار می‌شود
```

`PreKeyBundle` یک DTO ساده است. Host‌ها آن را هر طور که بخواهند منتشر می‌کنند — مستقیماً peer-to-peer از طریق mesh (`PreKeyRequest` / `PreKeyResponse` انواع بسته، PROTOCOL_SPEC §2.5 را ببینید)، از طریق یک دایرکتوری بک‌اند، یا دست به دست. پروتکل یک انتقال برای بسته‌ها الزامی نمی‌کند.

---

## ۴. ارسال و دریافت

کوتاه‌ترین مسیر انتها-به-انتها (بدون DI، بدون مسیریابی، فقط رمزنگاری):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// ciphertext را از طریق انتقال خود ارسال کنید. در طرف Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

در تولید ciphertext را در یک `MeshPacket` می‌پیچید، با
`PacketSigningService.SignPacketAsync` امضا می‌کنید، و `MessagingService.SendAsync`
را برای مدیریت مسیریابی، تلاش مجدد و fallback DTN می‌گذارید:

```csharp
using Aether.Messaging;
using Aether.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent قبلاً توسط لایه پیام‌رسانی رمزگشایی شده است.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> ciphertext از طریق mesh، DTN یا رله بک‌اند خارج شد
// handed == false -> در صندوق خروجی صف شد؛ ProcessOutboxAsync دوباره تلاش خواهد کرد
```

`MessagingService` پیام‌ها را صف می‌کند — هرگز در cleartext ارسال نمی‌کند — وقتی هنوز جلسه Signal با گیرنده وجود ندارد. برای `SessionRequired` مشترک شوید تا بدانید چه زمانی باید بسته پیش‌کلید همتا را دریافت و `alice.ProcessPreKeyBundleAsync(...)` را فراخوانی کنید.

---

## ۵. رفت‌وبرگشت دو-گره در ۵۰ خط

این یک اسکریپت قابل اجراست. در `Program.cs` کپی کنید، یک `<ProjectReference>`
به `Aether.Security.csproj` اضافه کنید (که `Aether.Core` و رمزنگاری BCL را وارد می‌کند)، و `dotnet run` بزنید.

```csharp
using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob یک بسته منتشر می‌کند؛ Alice آن را پردازش می‌کند. پس از این، Alice می‌تواند
// به Bob رمزگذاری کند؛ جلسه Bob وقتی اولین پیام Alice را رمزگشایی می‌کند به‌طور خودکار برقرار می‌شود
// (که X3DH metadata را به‌عنوان یک "پیام PreKey" حمل می‌کند).
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// در تولید: `outbound` را سریال‌سازی کنید (یا در یک MeshPacket بپیچید و
// PacketSigningService.SignPacketAsync را فراخوانی کنید) و بایت‌ها را از طریق
// انتقال خود ارسال کنید. گیرنده EncryptedPayload را بازسازی و DecryptAsync را فراخوانی می‌کند.
// اینجا هر دو گره یک پروسه را به اشتراک می‌گذارند پس رکورد را مستقیم پاس می‌دهیم.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (جلسه اکنون در هر دو جهت زنده است) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

خروجی مورد انتظار:

```
Bob got: "hello bob"
Alice got: "ack"
```

برای یک دمو غنی‌تر انتها-به-انتها — شامل امضای بسته، رله چند-هاپی از طریق Charlie، MessagingService و DTN custody fallback — کنسول همراه را اجرا کنید:

```bash
dotnet run --project samples/Aether.Demo.Console
```

مرحله حضانت DTN (مرحله ۹ دمو) الگوی استاندارد برای اتصال تولید است: `MessagingService` + `RoutingService` + `DtnService` ترکیب‌شده در برابر یک آداپتور `IMeshSender` روی انتقال واقعی.

---

## ۶. پایداری (ذخیره کلید-مقدار)

به‌طور پیش‌فرض `SignalProtocolService` هر جلسه، کلید هویتی، کلید پیش‌امضاشده و کلید پیش‌پرداخت یک‌بار مصرف را در حافظه پروسه نگه می‌دارد. یک crash به معنای: هویت گمشده (نمی‌توان هیچ جلسه قبلی را رمزگشایی کرد)، مخزن OPK گمشده (X3DH پاسخگو برای آغازگرهای جدید شروع به شکست خوردن می‌کند)، وضعیت Double Ratchet گمشده (رازداری رو به جلو سالم است اما ترتیب پیام شکسته می‌شود).

`Aether.Storage.FileSystemKeyValueStore` یک `IKeyValueStore` مبتنی بر دیسک حداقلی است (یک فایل per entry، تغییر نام فایل موقت اتمیک). آن را از طریق آداپتورهای `KeyValue*Store` وصل کنید:

```csharp
using Aether.Storage;
using Aether.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// همان KV store را به هر دو آداپتور وصل کنید تا هویت، جلسات و
// پیش‌کلیدها پس از راه‌اندازی مجدد باقی بمانند.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore داخلی است — KeyValueSignalSessionStore هم داخلی است.
// در یک host Wave-3+، سازنده SignalProtocolService آگاه از وضعیت پایدار را
// از طریق ریشه ترکیب خود ثبت کنید (یا ثبت پیش‌فرض AddSignalProtocol() را با کارخانه خودتان جایگزین کنید).
```

`FileSystemKeyValueStore` عمداً ساده است: بدون فشرده‌سازی، بدون تراکنش‌های cross-key، بدون رمزگذاری در حالت استراحت. برای رمزگذاری در حالت استراحت `EncryptedKeyValueStore` را روی فایل سیستم (یا KV خودتان) لایه کنید و یک `IDataAtRestKeyProvider` تأمین کنید — host مالک wrapper کلید است، نه پروتکل.

همچنین می‌توانید `IRouteStore`، `IDtnBundleStore` و `IMessageStore` غیرپیش‌فرض را در برابر container DI قبل از زنجیرکردن `.AddRouting()` / `.AddDtn()` / `.AddMessaging()` ثبت کنید — builder از `TryAdd*` استفاده می‌کند و هر چیزی که اول در container گذاشتید را رعایت می‌کند. آداپتورهای `KeyValueRouteStore`، `KeyValueDtnBundleStore` و `KeyValueMessageStore` در `Aether.Storage` آن slot‌ها را در برابر هر `IKeyValueStore` پوشش می‌دهند.

---

## ۷. قابلیت مشاهده

Aether از OpenTelemetry با ابزارگذاری درجه اول پشتیبانی می‌کند. به یک meter و یک منبع activity مشترک شوید — هر دو رشته‌های پایدار هستند و کتابخانه‌ها به هیچ OTel SDK خاصی وابسته نیستند:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Aether.Protocol"))
    .WithTracing(t => t.AddSource("Aether.Protocol"));
```

آنچه به دست می‌آورید:

- **شمارنده‌ها**: `aether.messages.encrypted`، `aether.messages.decrypted`،
  `aether.signatures.validated`، `aether.signatures.rejected`،
  `aether.nonces.replayed`، `aether.timestamps.stale`،
  `aether.sessions.established`، `aether.ratchet.dh_steps`،
  `aether.route.requests_emitted`، `aether.route.replies_received`،
  `aether.route.cache_hits`، `aether.dtn.bundles_accepted`،
  `aether.dtn.bundles_delivered`، `aether.dtn.bundles_expired`،
  `aether.sos.broadcasts`، `aether.sos.rebroadcasts_suppressed`،
  `aether.messaging.messages_sent`، `aether.messaging.messages_queued`،
  `aether.messaging.dtn_fallback`.
- **هیستوگرام‌ها** (میلی‌ثانیه): `aether.encrypt.latency`، `aether.decrypt.latency`،
  `aether.route.lookup_latency`، `aether.sign.verify_latency`.
- **فعالیت‌ها** با برچسب‌های UHID پاک‌شده از PII:
  `Aether.Encrypt`، `Aether.Decrypt`، `Aether.DhRatchet.Step`،
  `Aether.Sign.Packet`، `Aether.Verify.Packet`، به‌علاوه spans مسیریابی و DTN.

وقتی هیچ listener‌ای وصل نیست مسیرهای گرم هیچ تخصیصی انجام نمی‌دهند — `Add` شمارنده به یک خواندن volatile تنزل می‌یابد و `StartActivity` مقدار `null` برمی‌گرداند.

موجودی کامل ابزار و قرارداد PII در
`src/Aether.Core/Diagnostics/AetherTelemetry.cs` زندگی می‌کنند.

---

## ۸. بررسی سلامت

`AddHealthChecks()` (متد builder Aether) چهار بررسی در سطح پروتکل را در برابر `HealthCheckService` host ثبت می‌کند. هر کدام `data` ساختاریافته‌ای مفید برای داشبورد می‌نویسند.

| نام بررسی | چه چیزی نظارت می‌کند | سالم → تخریب‌شده → ناسالم |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < ۱۰,۰۰۰ → ≥ ۱۰,۰۰۰ → ≥ ۵۰,۰۰۰ (پیش‌فرض؛ قابل تنظیم)             |
| `aether-dtn`                | بسته‌های فعال در حضانت                                  | < ۸۰٪ ظرفیت → ≥ ۸۰٪ → ≥ `DtnMaxBundlesPerNode`              |
| `aether-signal`             | OPKهای موجود + تعداد جلسات فعال                      | کف OPK → ناسالم زیر `MinAvailableOpks` (پیش‌فرض ۱۰)؛ سقف جلسه → تخریب‌شده بالای ۱,۰۰۰ |
| `aether-messaging-outbox`   | عمق صندوق خروجی معلق + رشد بین نمونه‌ها              | < ۱۰۰ → ≥ ۱۰۰ → ≥ ۱۰۰ و در حال رشد                              |

از طریق کیف‌های `AetherOptions.Routing`، `Dtn`، `Signal` و `Messaging` تنظیم کنید. Host باید قبل از `.AddHealthChecks()` builder Aether `services.AddHealthChecks()` را فراخوانی کند تا ثبت‌ها برای `MapHealthChecks(...)` قابل مشاهده باشند.

---

## ۹. مرحله بعد

- **`docs/PROTOCOL_SPEC.md`** — فرمت سیم، مسیریابی، تبادل کلید، DTN، جدول کامل انواع بسته، و الگوریتم استاندارد `BuildSignableData`.
- **`docs/THREAT_MODEL.md`** — آنچه رمزنگاری در برابرش محافظت می‌کند، آنچه صریحاً خارج از حوزه است، و فرض‌هایی که ادعاهای امنیتی به آن‌ها متکی هستند.
- **`OPEN_ISSUES.md`** — محدودیت‌های شناخته‌شده، آیتم‌های نقشه راه ردیابی‌شده، و شکاف ماشین‌آلات جلسه زبان C.
- **`SECURITY.md`** — سیاست افشای مسئولانه.
- **`samples/Aether.Demo.Console/Program.cs`** — پیاده‌روی انتها-به-انتها ۹-مرحله‌ای قابل اجرا. مرحله ۹ (MessagingService + DTN) الگوی اتصال تولید است.
- **`fixtures/signal/`** — بردارهای آزمون بین‌زبانی. اگر در حال پورت کردن Aether به زبان دیگری هستید، اینها خروجی‌های byte-pinned هستند که پیاده‌سازی شما باید با آن‌ها مطابقت داشته باشد.

باگ پیدا کردید؟ روی GitHub ثبت کنید. آسیب‌پذیری پیدا کردید؟ `SECURITY.md` را ببینید.

</div>
