```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

<div dir="rtl">

فایل‌ها، پیام‌ها و جریان‌ها را با افراد نزدیک به اشتراک بگذارید. بدون WiFi. بدون داده موبایل. بدون ثبت‌نام. شبیه AirDrop، با این تفاوت که با همه، روی هر پلتفرمی کار می‌کند.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## با آن چه می‌توان کرد؟

**اشتراک‌گذاری یادداشت‌های درسی بدون مصرف داده.**

در یک گروه مطالعاتی هستید. یکی سوالات قبلی امتحان روی گوشیش دارد. Aether آن‌ها را مستقیماً از طریق Bluetooth به دستگاه شما ارسال می‌کند — بدون نقطه اتصال، بدون گروه واتساپ، بدون محدودیت حجم فایل. اگر کسی در گروه خارج از محدوده باشد، فایل از طریق دستگاه‌های دیگر هاپ می‌کند تا به او برسد. پیام‌ها تا ۷۲ ساعت منتظر یک مسیر می‌مانند.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**ببینید اطرافتان چه خبر است.**

در یک رویداد دانشگاهی یا جشنواره هستید. Aether دستگاه‌های نزدیک را از طریق Bluetooth و WiFi Direct کشف می‌کند — بدون فید برنامه، بدون الگوریتم. آنچه واقعاً اطرافتان است می‌بینید، نه آنچه تبلیغ شده.

**ارسال SOS وقتی سیگنال ندارید.**

گوشی شما آنتن ندارد. Aether یک پیام اضطراری به تمام دستگاه‌های در محدوده پخش می‌کند و آن دستگاه‌ها آن را به دیگران منتقل می‌کنند. نیازی به دکل مخابراتی نیست.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**ایجاد کانال‌های گروهی خصوصی.**

کانالی برای طبقه خوابگاه، انجمن یا تیم پروژه‌تان. فقط اعضای تأییدشده می‌توانند پیام‌ها را بخوانند یا ارسال کنند. هیچ سروری مکالمه را ذخیره نمی‌کند.

**فروش اجناس به افراد نزدیک.**

یک کتاب درسی برای فروش بگذارید. افرادی که در محدوده mesh قدم می‌زنند آن را می‌بینند. بدون حساب کاربری مارکتپلیس، بدون کارمزد آگهی — فقط مجاورت.

**تماشای فیلم با هم، از طریق mesh.**

گروهتان شب فیلم دارد. یکی فایل را دارد. Aether پخش را در تمام دستگاه‌ها همزمان می‌کند — پخش، توقف، جستجو — همه هماهنگ. اگر بعضی‌ها فایل ندارند، mesh آن را در زمان واقعی به‌عنوان یک جریان P2P توزیع می‌کند. همه از طریق SDPKT برای خرید آن مشارکت می‌کنند اگر کسی آن را نداشته باشد.

## چطور کار می‌کند

دستگاه‌ها مستقیماً از طریق Bluetooth، WiFi Direct یا NearLink با یکدیگر صحبت می‌کنند. بدون اتصال اینترنت، بدون سرور، بدون زیرساخت مرکزی.

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

وقتی یک پیام نمی‌تواند مستقیماً به مقصد برسد، از طریق دستگاه‌های دیگر هاپ می‌کند. آن دستگاه‌های رله نمی‌توانند محتوای حمل‌شده را بخوانند — هر پیام با AES-256-GCM رمزگذاری شده است. هر بسته با کلیدهای هویتی Ed25519 امضا می‌شود و بسته‌های جعلی توسط شبکه دور انداخته می‌شوند.

> **یادداشت بلوغ امنیتی (قبل از راه‌اندازی بخوانید):** X3DH واقعی (۴ عملیات DH با X25519)، Double Ratchet کامل Signal (مرحله چرخش DH در دریافت، KDF_RK، زنجیره رچت 0x01/0x02) و مخزن کلید پیش‌پرداخت یک‌بار مصرف (پیش‌فرض ۱۰۰ OPK، FIFO، محافظت‌شده با قفل) در **تمام ۸ زبان** پیاده‌سازی شده‌اند و به یک مجموعه fixture مشترک بین‌زبانی در `fixtures/signal/` پین شده‌اند. تنها آیتم باز باقی‌مانده، راه‌اندازی فیزیکی RF روی سخت‌افزار واقعی BLE است (در `OPEN_ISSUES.md` ردیابی شده).

بدون حساب کاربری، بدون شماره تلفن، بدون ایمیل. یک جفت کلید تولید کنید و در شبکه حضور داشته باشید.

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

**مسیریابی** — AODV با پاسخ‌های مسیر امضاشده. هر پاسخ مسیر با کلید Ed25519 مقصد امضا می‌شود، بنابراین هیچ دستگاهی نمی‌تواند وانمود کند مقصدی است که نیست.

**ذخیره و ارسال** — وقتی مسیر زنده‌ای وجود ندارد، بسته‌ها تا ۷۲ ساعت نگه داشته می‌شوند تا مسیری باز شود.

**انتخاب انتقال** — پروتکل برای هر بسته انتقال مناسب را انتخاب می‌کند. پیام‌های کنترلی کوچک از BLE عبور می‌کنند. انتقال‌های حجیم از WiFi Direct استفاده می‌کنند. NearLink وقتی موجود باشد.

**صدا، تصویر و استریمینگ** — تماس تصویری با مذاکره کدک (H.264/H.265/VP8)، انتخاب کیفیت آگاه از انتقال، تماس تصویری گروهی با رله SFU خودکار، تماشای همزمان با جبران RTT، و استریمینگ با bitrate تطبیقی.

**محافظت در برابر پخش مجدد** — حذف تکراری nonce با پنجره تازگی timestamp پنج‌دقیقه‌ای.

## انتقال‌ها

هر انتقال یک نام رنگ دارد که در سراسر کد استفاده می‌شود. `IsAvailable` مسیرهای بلوکه‌شده توسط سخت‌افزار را کنترل می‌کند — `TransportManager` آن‌ها را به‌طور خودکار رد کرده و به انتقال بعدی موجود برمی‌گردد.

| رنگ | نام | برد | پهنای باند | وضعیت |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~۱۰۰ متر | ۱ مگابیت/ثانیه | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~۲۰۰ متر | ۲۵۰ مگابیت/ثانیه | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | رله سلولی HTTP | نامحدود | ~۱۰ مگابیت/ثانیه | ✅ Windows — سرور رله در `samples/AetherMesh.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~۵ سانتی‌متر | ۸۴۸ کیلوبیت/ثانیه | ⚠️ Android HCE (`android/white/`)؛ Windows: NDEF-over-BLE-GATT + ACR122U PC/SC تقریبی (`Windows.Networking.Proximity` در Win 11 حذف شده) |
| 🩵 Aether Teal | NearLink | ~۶۰۰ متر | ۱۲ مگابیت/ثانیه | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`؛ Windows + Android: تقریب SSAP-over-BLE (معادل API، نه سازگار با سیم) |
| 🔴 Aether Red | LoRa / CircleLink | ~۱۵ کیلومتر | ۳۷.۵ کیلوبیت/ثانیه | ⚠️ فرمت سیم Meshtastic روی BLE LR (~۱.۳ کیلومتر)؛ تعویض رادیو به SX1276/SX1278 وقتی ماژول LoRa موجود است |

ترتیب اولویت در `TransportManager`: NearLink → BLE (≤ 1 KB) → Wi-Fi Direct → NFC → LoRa → HTTP Relay (آخرین چاره، `PowerCostRelative = 100`).

## لایه‌های استقرار

Aether روی هر پلتفرمی که از Bluetooth یا Wi-Fi پشتیبانی می‌کند کار می‌کند. لایه‌ای که در آن هستید به سیستم‌عامل هدف شما بستگی دارد.

---

### لایه استاندارد — هر پلتفرم

Android · Windows · Linux · macOS · iOS

Aether به‌طور کامل روی هر دستگاهی با سخت‌افزار Bluetooth یا Wi-Fi اجرا می‌شود. جایی که یک رادیو از نظر فیزیکی وجود ندارد، هر انتقال مسدودشده با استفاده از آنچه موجود است تقریب زده می‌شود:

- **NearLink (Aether Teal)** — از طریق BLE GATT با استفاده از UUID سرویس Aether SLE استاندارد (`61657468-6572-0003-0000-000000000000`) تقریب زده می‌شود. لایه پروتکل برنامه SSAP از نظر API یکسان با GATT است. لایه رادیو (BPSK/QPSK/8PSK، کدهای Polar، کانال‌های ۱–۴ مگاهرتز) یکسان نیست — گره‌هایی که لایه استاندارد را اجرا می‌کنند نمی‌توانند بایت‌های خام را با سخت‌افزار NearLink واقعی مبادله کنند؛ با گره‌های Aether لایه استاندارد تعامل دارند.
- **LoRa (Aether Red)** — با استفاده از فرمت کامل سیم Meshtastic روی BLE 5.0 Coded PHY (S=8، ~۱.۳ کیلومتر در فضای باز) تقریب زده می‌شود. فدراسیون bridge-node با سخت‌افزار LoRa واقعی به‌طور خودکار کار می‌کند — همان فرمت بسته Meshtastic تمام هاپ‌ها را با هیچ ترجمه‌ای طی می‌کند.
- **NFC (Aether White)** — از طریق NDEF-over-BLE-GATT با یک دروازه مجاورت RSSI (≥ −40 دسی‌بل ≈ ۵–۱۰ سانتی‌متر) تقریب زده می‌شود که معنای tap-to-connect را بازتولید می‌کند. مسیر PC/SC از طریق خواننده USB NFC نیز در Windows پشتیبانی می‌شود.

تمام قابلیت‌های دیگر — BLE، Wi-Fi Direct، رله HTTP، امنیت Signal Protocol (X3DH + Double Ratchet)، مسیریابی AODV، DTN store-and-forward، پخش SOS، صدا، استریمینگ — بومی و یکسان با لایه بومی هستند.

**این یک استقرار کاملاً قادر و آماده تولید است.** اکثر برنامه‌ها از اینجا شروع می‌کنند.

---

### لایه بومی — CircleOS / OpenHarmony

CircleOS · HarmonyOS · هر سیستم‌عامل مبتنی بر OpenHarmony

CircleOS بر پایه OpenHarmony ساخته شده که چیپ NearLink (SLE) و SDK `@kit.NearLinkKit` را به‌عنوان قابلیت درجه اول سیستم‌عامل دارد. در دستگاه‌های CircleOS و HarmonyOS با سخت‌افزار NearLink، هیچ تقریبی لازم نیست — `harmonyos/teal/` مستقیماً از رادیو SLE واقعی استفاده می‌کند:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

این صرفاً نسخه بهتری از لایه استاندارد نیست. در لایه NearLink یک شبکه کاملاً متفاوت است:

| قابلیت | لایه استاندارد (BLE تقریبی) | لایه بومی (CircleOS / OpenHarmony) |
|---|---|---|
| **برد NearLink** | ~۱۰۰ متر (BLE) | **۶۰۰ متر** |
| **پهنای باند NearLink** | ~۱ مگابیت/ثانیه (BLE) | **۱۲ مگابیت/ثانیه** |
| **تأخیر NearLink** | ~۱۰ میلی‌ثانیه (BLE) | **۲۰ میکروثانیه** |
| **مصرف انرژی NearLink** | پایه BLE | **۶۰٪ کمتر از BLE 5.0** |
| **همتایان همزمان NearLink** | ~۷ (محدودیت اتصال BLE) | **۵۰۰+** |
| **منبع NearLink** | SSAP-over-BLE (`android/teal/`، `WinNearLinkStubTransportService`) | رادیو SLE واقعی (`harmonyos/teal/`، `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / رله HTTP** | بومی | بومی (یکسان) |
| **امنیت Signal Protocol** | کامل | کامل (یکسان) |
| **مسیریابی / DTN / SOS** | کامل | کامل (یکسان) |
| **هویت Aether Tag** | پشتیبانی می‌شود | پشتیبانی می‌شود (یکسان) |

---

### جابجایی بین لایه‌ها

هیچ تغییر کدی لازم نیست. لایه در زمان اجرا توسط `IsAvailable` در هر سرویس انتقال تعیین می‌شود:

۱. در یک دستگاه CircleOS یا HarmonyOS با چیپ NearLink، `IsAvailable` در انتقال NearLink `true` برمی‌گرداند (از طریق بررسی مجوز + تلاش اسکن غیرفعال بررسی سخت‌افزار می‌شود).
۲. `TransportManager` به‌طور خودکار NearLink را به موقعیت اولویت ارتقا می‌دهد — کمترین هزینه انرژی، بیشترین پهنای باند.
۳. کد برنامه، فرمت بسته، الگوریتم مسیریابی، لایه امنیتی و Aether Tags در هر دو لایه یکسان هستند.

یک گره در لایه استاندارد و یک گره در لایه بومی می‌توانند آزادانه ارتباط برقرار کنند — آن‌ها فرمت سیم یکسان، جلسات Signal Protocol یکسان و Aether Tags یکسانی دارند. تفاوت لایه فقط بر رادیو استفاده‌شده برای بسته‌های NearLink تأثیر می‌گذارد، نه پروتکل بالاتر از آن.

---

> **این لایه‌ها به‌صورت داخلی به نوع Asterix (استاندارد) و نوع Obelix (بومی) اشاره می‌شوند.** Asterix با آنچه موجود است به‌خوبی کار می‌کند. Obelix — که روی CircleOS با NearLink بومی اجرا می‌شود — با قابلیت دائماً بالاتری عمل می‌کند، به همان شکلی که Obelix قدرت اکسیر جادویی را بدون نیاز به نوشیدن مجدد با خود حمل می‌کند.

---

## پیاده‌سازی‌ها

Aether در ۸ زبان ساخته شده تا روی تلفن‌ها، لپ‌تاپ‌ها، تبلت‌ها و میکروکنترلرها اجرا شود. تمام پیاده‌سازی‌ها بسته‌های سازگار با سیم تولید می‌کنند — پیامی که توسط گره Rust رمزگذاری شده می‌تواند توسط گره Python رله شود و توسط گره Swift رمزگشایی شود.

| زبان | پوشه | فرمت سیم | مسیریابی/DTN/SOS | X3DH | Double Ratchet | مخزن OPK | صدا/گروه | استریمینگ/تصویر/تماشا |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

تمام ۸ زبان بسته‌های سیم بایت‌به‌بایت یکسانی تولید می‌کنند که توسط ۱۴ fixture استاندارد فرمت سیم و ۴ بردار آزمون Signal در CI تأیید می‌شوند (`fixtures/expected/*.bin`، `fixtures/signal/expected/*.json`). مسیریابی (RREQ/RREP به‌سبک AODV)، DTN store-and-forward، پخش SOS، صدا، استریمینگ و سرویس‌های سخت‌گیری امنیتی در هر زبان با **~۳,۰۰۰ آزمون** در تمام ۸ پیاده‌سازی اجرا می‌شوند:

| زبان | آزمون‌ها | پلتفرم CI |
|----------|------:|-------------|
| C# (.NET 10) | ۵۳۰ | ubuntu-latest |
| TypeScript / Node 20 | ۴۵۹ | ubuntu-latest |
| Kotlin / JVM 21 | ۴۵۷ | ubuntu-latest |
| Go 1.22 | ۴۲۳ | ubuntu-latest |
| Python 3.12 | ۳۸۷ | ubuntu-latest |
| Swift 6 | ۲۹۵ | macos-14 |
| C (GCC) | ۲۵۳ | ubuntu-latest |
| Rust (stable) | ~۱۹۵ | ubuntu-latest |
| **مجموع** | **~۳,۰۰۰** | |

تعامل‌پذیری Signal بین‌زبانی به `fixtures/signal/` با بردارهای آزمون مشترک برای X3DH (`x3dh_basic`)، رچت متقارن (`ratchet_step_basic`، `ratchet_step_three_iterations`) و KDF_RK (`kdf_rk_basic`) لنگر انداخته است. هر پیاده‌سازی باید خروجی‌های بایت‌به‌بایت یکسانی در برابر آن fixture‌ها تولید کند. هر ۸ زبان اکنون یک جلسه Signal کامل (`generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt`) دارند.

## شروع سریع

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherMesh.Demo.Console
```

دمو ۸ مرحله را طی می‌کند: تولید کلیدهای هویتی Ed25519 برای سه گره (Alice، Bob، Charlie)، برقراری جلسات Signal Protocol، ارسال پیام‌های رمزگذاری‌شده، رله یک پیام از طریق Charlie (که نمی‌تواند آن را بخواند)، نمایش فرمت سیم باینری، و نمایش رازداری رو به جلو در ۵ پیام متوالی. خروجی رنگ‌بندی‌شده است و بین مراحل مکث می‌کند.

**ارسال پیام در C#:**

```csharp
// Establish a Signal Protocol session
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Encrypt and send
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Create a signed packet
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

دمو کلیدهای هویتی را برای دو گره تولید می‌کند، بسته‌های پیش‌کلید را مبادله می‌کند، جلسات رمزگذاری‌شده برقرار می‌کند، پیام‌های رمزگذاری‌شده را در هر دو جهت ارسال می‌کند، بسته‌های mesh ایجاد و امضا می‌کند، امضاها را تأیید می‌کند و بسته‌ها را به فرمت سیم باینری سریال‌سازی می‌کند.

**ارسال پیام در Rust:**

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

دمو دو گره را در یک شبکه شبیه‌سازی‌شده ایجاد می‌کند، کلیدهای Ed25519 تولید می‌کند، جلسات Signal Protocol برقرار می‌کند، یک بسته ایجاد و امضا می‌کند، آن را به فرمت باینری سازگار با C# سریال‌سازی می‌کند، یک پیام مخفی رمزگذاری می‌کند، آن را در گره دیگر رمزگشایی می‌کند، از طریق انتقال ارسال می‌کند و سفر رفت‌وبرگشت را تأیید می‌کند.

**ارسال پیام در TypeScript:**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Exchange bundle with peer
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

دمو ۸ نمایش را اجرا می‌کند: تولید کلید Ed25519 و شناسایی دستکاری، ایجاد گره با قابلیت‌ها، تبادل کلید X3DH در Signal Protocol، رمزگذاری و رمزگشایی AES-256-GCM، سریال‌سازی بسته، امضای بسته با تشخیص پخش مجدد، انتقال درون‌پروسه‌ای، و یک جریان کامل انتها-به-انتها با ترکیب تمام لایه‌ها.

**ارسال پیام در Python:**

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

دمو ۵ نمایش را اجرا می‌کند: رفت‌وبرگشت سریال‌سازی بسته، امضای Ed25519 با تشخیص دستکاری، برقراری جلسه Signal Protocol با پیام‌رسانی رمزگذاری‌شده در هر دو جهت، انتقال درون‌پروسه‌ای بین دو همتا، و حذف تکراری nonce برای محافظت در برابر پخش مجدد.

**ارسال پیام در Go:**

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

دمو ۱۱ مرحله را طی می‌کند: تولید کلید، ایجاد گره با قابلیت‌ها، مقداردهی اولیه Signal Protocol، تبادل بسته پیش‌کلید، برقراری جلسه، ایجاد و امضای بسته، سریال‌سازی، deserialize با تأیید امضا، رمزگذاری انتها-به-انتها با ratcheting کلید، تشخیص حمله پخش مجدد، و انتقال درون‌پروسه‌ای.

**ارسال پیام در Kotlin:**

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

دمو ۵ آزمون را اجرا می‌کند: رفت‌وبرگشت سریال‌سازی بسته، امضای Ed25519 با رد دستکاری، برقراری جلسه Signal Protocol با رمزگذاری AES-256-GCM، تحویل پیام انتقال درون‌پروسه‌ای، و یک جریان کامل انتها-به-انتها که در آن Alice یک بسته را امضا می‌کند و Bob آن را پس از انتقال تأیید می‌کند.

**ارسال پیام در Swift:**

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

دمو ۷ نمایش را اجرا می‌کند: تولید کلید Ed25519، ایجاد و امضای بسته، سریال‌سازی به فرمت سیم باینری، deserialize با بررسی یکپارچگی، رمزگذاری و رمزگشایی AES-256-GCM، احراز هویت پیام HMAC-SHA256، و مشتق‌سازی کلید HKDF-SHA256.

**ارسال پیام در C:**

```c
aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
packet->type = AETHERMESH_PACKET_TYPE_DATA;
packet->ttl = 7;

aethermesh_packet_set_source_uhid(packet, "alice");
aethermesh_packet_set_destination_uhid(packet, "bob");
aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
aethermesh_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethermesh_packet_free(packet);
```

## نقشه راه

آنچه ساخته شده و آنچه در پیش است.

**انجام‌شده (تأییدشده بین‌زبانی، تمام ۸ پیاده‌سازی):**
- فرمت سیم: بایت‌به‌بایت یکسان در ۸ زبان، لنگرانداخته‌شده با ۱۴ fixture استاندارد و ادعاهای بین‌زبانی در CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — ماتریس ۹-شغلی (C#/.NET 10، Go 1.22، TypeScript/Node 20، Python 3.12، Kotlin/JVM 21، Swift/macOS-14، Rust stable، C/GCC، به‌علاوه شغل یکپارچگی fixture) در `.github/workflows/ci.yml`.
- امضا و تأیید بسته Ed25519
- رمزگذاری AES-256-GCM
- اولیه‌های مشتق‌سازی کلید HKDF / HMAC
- سریال‌سازی بسته + طرح‌بندی امضا (LE + فیلدهای ۴ بایتی int32)
- شبیه‌ساز انتقال درون‌پروسه‌ای (برای توسعه و آزمون‌ها)
- سرویس مسیریابی الهام‌گرفته از AODV با RREQ/RREP، پاسخ‌های مسیر امضاشده، حذف تکراری، ارسال TTL
- سرویس DTN store-and-forward با انتقال حضانت، تکثیر آگاه از geohash، TTL 72 ساعته
- سرویس پخش SOS با flood، حذف تکراری، محافظ خودمبدأ، محدودیت نرخ (۳/ساعت)
- نقاط توسعه‌پذیری: `IncentiveProvider`، `BackendClient`، `FeatureFlagProvider` (پیش‌فرض‌های Noop)
- **~۳,۰۰۰ آزمون** در تمام ۸ زبان (C# 530، TypeScript 459، Kotlin 457، Go 423، Python 387، Swift 295، C 253، Rust ~195) — همه سبز در CI
- ✅ **کلید مؤقت X3DH واقعی (۸ زبان)** — ۴ عملیات DH با X25519 با مشتق‌سازی ریشه HKDF-SHA256. لنگرانداخته‌شده با `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **هم‌راستایی Double Ratchet در سراسر خانواده** — Signal §5 کامل با HMAC-SHA256 + جداسازی دامنه 0x01/0x02 در رچت متقارن، HKDF-SHA256 KDF_RK در مرحله DH-ratchet، چرخش DH در دریافت. تأییدشده با fixture‌های `ratchet_step_basic`، `ratchet_step_three_iterations`، `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 با HEAD آشتی داده شده** — به `docs/PROTOCOL_SPEC.md` مراجعه کنید.

**انجام‌شده (تمام ۸ زبان):**
- ✅ **تماس‌های صوتی (یک‌به‌یک)** — ماشین حالت سیگنالینگ (Offer/Answer/Hangup/Cancel/Timeout) + انتقال فریم باینری (16B callId · 4B seq · 8B timestamp · 1B isSilence · N بایت). تحویل آگاه از مسیر از طریق `IRoutingService`.
- ✅ **صدای گروهی** — عضویت مبتنی بر میزبان (دعوت/اخراج/خروج)، فیلد تولید کلید per-frame، fan-out یک‌کاسته به تمام اعضای فعلی، چرخش کلید کنترل‌شده توسط میزبان در تغییر عضویت.
- ✅ **استریمینگ زنده** — ناشر `StreamAnnounce` پخش می‌کند؛ مشترکین `StreamSubscribe` ارسال می‌کنند؛ فریم‌های باینری `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N بایت) unicast به هر مشترک.
- ✅ **تماس‌های تصویری (یک‌به‌یک)** — مذاکره کدک/رزولوشن/fps/bitrate در سیگنالینگ، سیگنال‌های درخواست keyframe و تغییر کیفیت، فرمت `VideoFrame` باینری مطابق با طرح‌بندی صوتی.
- ✅ **تماشا با هم** — میزبان دستورات `WatchSync` معتبر (پخش/توقف/جستجو/سرعت) صادر می‌کند؛ پیروان با جبران RTT اعمال می‌کنند (`position = positionMs + elapsed × playbackSpeed`)؛ `WatchReaction` fire-and-forget.
- ✅ **مخزن کلید پیش‌پرداخت یک‌بار مصرف (OPK)** — پیش‌فرض ۱۰۰، صدور FIFO، شارژ تنبل، مصرف محافظت‌شده با قفل در تمام ۸ زبان. خطر همزمانی single-OPK را بسته می‌کند.
- ✅ **C: جلسه Signal کامل** — `aethermesh_signal_service_init`، `generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt` در `c/src/signal_protocol.c`؛ ۶ آزمون E2E دو-گره در `c/tests/test_signal_session.c`. هر ۸ زبان اکنون Signal Protocol کامل با قابلیت جلسه دارند.

**انجام‌شده (فقط مرجع C#):**
- ✅ **مرحله ۹ دمو — MessagingService + DTN fallback انتها-به-انتها**
- ✅ **پل `AetherMesh.Messaging` ↔ `AetherMesh.Security`** — `SignalMessageEnvelopeCipher` لایه پیام‌رسانی را به‌طور پیش‌فرض انتها-به-انتها رمزگذاری می‌کند.
- ✅ **استریمینگ با bitrate تطبیقی** — `AdaptiveBitrateController` با نردبان‌های bitrate مشخص‌شده در spec برای Profile A (زمان واقعی)، B (پخش زنده) و C (VOD).
- ✅ **تماشا با هم: ورودی BitTorrent + تأمین مالی گروهی ChipIn**
- ✅ **تماس‌های تصویری گروهی با رله SFU خودکار** — `GroupVideoService` / `IGroupVideoService`.
- ✅ **شبیه‌سازی انتقال BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`).
- ✅ **شبیه‌سازی انتقال Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`).
- ✅ **شبیه‌سازی انتقال NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`).
- ✅ **آزمون‌های شبیه‌سازی راه‌اندازی RF** — آزمون‌های تعامل‌پذیری دو-گره (`SimulatedTransportTests`).

**انجام‌شده (لایه انتقال C# — همه fail-fast):**
- ✅ **انتقال BLE GATT واقعی** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server).
- ✅ **انتقال Wi-Fi Direct واقعی** — `WinWifiDirectTransportService` (WinRT) + `android/green/` (`WifiP2pManager`).
- ✅ **انتقال رله HTTP (Aether Purple)** — `HttpRelayTransportService` با long-poll ۱۰ ثانیه‌ای.
- ✅ **NFC (Aether White)** — `android/white/` `HostApduService` را با AID `F061657468657200` پیاده‌سازی می‌کند.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — پیاده‌سازی کامل HarmonyOS 5.0.1 (API 13) ArkTS.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` تقریب Meshtastic-over-BLE-LR را مستند می‌کند.

**باز — ردیابی‌شده در `OPEN_ISSUES.md`:**
- راه‌اندازی RF روی سخت‌افزار واقعی: آزمون تعامل‌پذیری انتها-به-انتها دو-گره روی دستگاه‌های فیزیکی BLE / Wi-Fi Direct (آزمون‌های شبیه‌سازی پاس می‌شوند؛ جلسه آزمایشگاه سخت‌افزار مورد نیاز است)
- NearLink: `harmonyos/teal/` کامل است؛ نیاز به سخت‌افزار Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 دارد. Windows + Android به‌طور خودکار به تقریب SSAP-over-BLE برمی‌گردند.
- LoRa / CircleLink: ماژول رادیویی برای برد واقعی LoRa مورد نیاز است.

**هنوز برای مشارکت خارجی باز نشده:**
- پروتکل هنوز در حال توسعه فعال است. در این زمان مشارکت‌های خارجی پذیرفته نمی‌شوند.

## ساختار پروژه

```
aether-protocol/
  src/
    AetherMesh.Core/          مدل‌های پروتکل، ثابت‌ها، سریال‌سازی بسته
    AetherMesh.Security/      Signal Protocol، Ed25519، امضای بسته
    AetherMesh.Transport/     انتزاع‌های انتقال، NearLink، شبیه‌ساز درون‌پروسه‌ای
    AetherMesh.Messaging/     مدیریت پیام و رله
    AetherMesh.Storage/       پایداری DTN store-and-forward
    AetherMesh.Streaming/     استریمینگ bitrate تطبیقی، مدل‌ها و رابط‌های تصویری
    AetherMesh.Voice/         تماس‌های صوتی و صدای گروهی
    AetherMesh.Content/       تأیید محتوا و انتقال تقسیم‌شده
  samples/
    AetherMesh.Demo.Console/  دمو تعاملی
  tests/
    AetherMesh.Security.Tests/
    AetherMesh.Protocol.Tests/
  rust/                   پیاده‌سازی Rust
  typescript/             پیاده‌سازی TypeScript
  python/                 پیاده‌سازی Python
  go/                     پیاده‌سازی Go
  kotlin/                 پیاده‌سازی Kotlin/JVM
  swift/                  پیاده‌سازی Swift
  c/                      پیاده‌سازی C
  docs/
    PROTOCOL_SPEC.md      مشخصات پروتکل به سبک RFC
```

## افزودن یک انتقال جدید

`ITransportService` را پیاده‌سازی کنید:

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

آن را در DI ثبت کنید و `TransportManager` به‌طور خودکار آن را در انتخاب انتقال، مرتب‌شده بر اساس هزینه انرژی، لحاظ خواهد کرد.

## مقایسه با دیگران

| پروتکل | محدودیت | مزیت Aether |
|----------|-----------|-----------------|
| **Briar** | فقط Android، وابسته به Tor | چندپلتفرمی، mesh خالص |
| **Meshtastic** | فقط LoRa (حداکثر ۳۰ کیلوبیت/ثانیه) | چندانتقاله (BLE + WiFi + NearLink)، قادر به صدا و استریمینگ |
| **Reticulum** | Python، جامعه کوچک | ۸ زبان، سازگار با سیم در همه آن‌ها |
| **libp2p** | فرض می‌کند ستون فقرات اینترنت دارد | offline-first، با صفر زیرساخت کار می‌کند |
| **Yggdrasil** | شبکه overlay، نیاز به اینترنت دارد | mesh لایه فیزیکی، بدون اینترنت کار می‌کند |
| **Signal** | بدون mesh، نیاز به اینترنت دارد | آفلاین کار می‌کند، P2P، رله mesh، همان رمزگذاری E2E |

## نقاط توسعه

پروتکل به‌تنهایی کار می‌کند. این رابط‌ها به شما اجازه می‌دهند بک‌اند خودتان را اگر خواستید وصل کنید:

- `IAetherMeshIncentiveProvider` — پاداش به گره‌هایی که ترافیک را رله می‌کنند (پیش‌فرض no-op: رله نوع‌دوستانه)
- `IAetherMeshBackendClient` — همگام‌سازی با سرور وقتی اینترنت موجود است (پیش‌فرض no-op: کاملاً آفلاین)
- `IAetherMeshFeatureFlagProvider` — تغییر ویژگی‌های پروتکل در زمان اجرا (پیش‌فرض no-op: همه چیز فعال)

هر سه با پیاده‌سازی‌های no-op ارائه می‌شوند. آن‌ها را بردارید و چیزی خراب نمی‌شود.

## مشارکت

مشارکت‌های خارجی هنوز باز نشده‌اند. پروژه هنوز در حال توسعه فعال است. وقتی پنجره مشارکت عمومی را اعلام کردیم برگردید.

## امنیت

به [SECURITY.md](SECURITY.md) برای سیاست افشای مسئولانه مراجعه کنید.

## مجوز

مجوز MIT. به [LICENSE](LICENSE) مراجعه کنید.

</div>
