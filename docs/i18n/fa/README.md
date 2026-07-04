# AetherNet — پروتکل شبکه‌سازی mesh با اولویت آفلاین

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

<div dir="rtl">

**AetherNet یک پروتکل شبکه‌سازی mesh متن‌باز و با مجوز MIT است** برای ارسال پیام، فایل، صدا و تصویر به افراد نزدیک — **بدون اینترنت، بدون سرور و بدون ثبت‌نام**. دستگاه‌ها مستقیماً از طریق Bluetooth، Wi-Fi Direct، NearLink و LoRa به یکدیگر متصل می‌شوند؛ وقتی گیرنده خارج از محدوده است، پیام‌ها از طریق دستگاه‌های دیگر هاپ می‌کنند و تا ۷۲ ساعت منتظر یک مسیر می‌مانند. این پروتکل **پیاده‌سازی‌های بایت‌به‌بایت یکسان در هشت زبان برنامه‌نویسی** ارائه می‌دهد — C#، Rust، TypeScript، Python، Go، Kotlin، Swift و C.

فایل‌ها، پیام‌ها و جریان‌ها را با افراد نزدیک به اشتراک بگذارید. بدون WiFi. بدون داده موبایل. بدون ثبت‌نام. شبیه AirDrop، با این تفاوت که با همه، روی هر پلتفرمی کار می‌کند.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **یک پروتکل، هشت زبان، یکسان روی سیم.** Aether در **C#، Rust، TypeScript، Python، Go، Kotlin، Swift و C** پیاده‌سازی شده است — و هر بسته در تمام آن‌ها بایت‌به‌بایت یکسان است، که توسط یک مجموعه fixture مشترک بین‌زبانی در CI اعمال می‌شود. گره خود را در هر یک از این هشت زبان بسازید؛ با تمام بقیه تعامل‌پذیر است. این README در ۱۱ زبان انسانی نیز موجود است (پیوندهای بالا).

## با آن چه می‌توان کرد؟

**اشتراک‌گذاری یادداشت‌های درسی بدون مصرف داده.**

در یک گروه مطالعاتی هستید. یکی سوالات قبلی امتحان روی گوشیش دارد. Aether آن‌ها را مستقیماً از طریق Bluetooth به دستگاه شما ارسال می‌کند — بدون نقطه اتصال، بدون گروه واتساپ، بدون محدودیت حجم فایل. اگر کسی در گروه خارج از محدوده باشد، فایل از طریق دستگاه‌های دیگر هاپ می‌کند تا به او برسد. پیام‌ها در صورت نیاز تا ۷۲ ساعت منتظر یک مسیر می‌مانند.

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

گروهتان شب فیلم دارد. یکی فایل را دارد. Aether پخش را در تمام دستگاه‌ها همزمان می‌کند — پخش، توقف، جستجو — همه هماهنگ. اگر بعضی‌ها فایل ندارند، mesh آن را در زمان واقعی به‌عنوان یک جریان P2P توزیع می‌کند. اگر کسی آن را نداشته باشد، همه از طریق SDPKT برای خرید آن مشارکت می‌کنند.

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

## آنچه دریافت می‌کنید — هر سرویس، در هر زبان

Aether صرفاً یک انتقال نیست. هر نوع بسته‌ای که پروتکل رزرو کرده اکنون یک **سرویس واقعی و کارآمد در تمام ۸ زبان** است، و هر یک به **بسته‌های سیم بایت‌به‌بایت یکسان** سریال‌سازی می‌شود — بسته‌ای که توسط گره Go ساخته شده، بدون تغییر، توسط گره Swift، Rust، C، Python، TypeScript، Kotlin یا C# رمزگشایی می‌شود. هر سرویس به یک fixture مشترک بین‌زبانی در `fixtures/<service>/` پین شده و توسط آزمون‌های واحد هر زبان اجرا می‌شود، به‌علاوه Swift و C روی سرور بیلد macOS نیز تأیید شده‌اند.

| قابلیت | چه کاری انجام می‌دهد | نوع(های) بسته | Fixture | ۸/۸ |
|---|---|:-:|---|:-:|
| **بیکن و پرس‌وجوی حضور** | اعلام «من اینجا هستم» و پرسیدن «چه کسی اطراف است؟» — روی یک **شناسه موقت چرخشی و مشتق‌شده از کلید** (نه هویت واقعی شما) به‌علاوه یک geohash درشت | 21, 22 | `fixtures/presence/` | ✅ |
| **ضربان قلب** | نگهداری اتصال سبک برای اثبات زنده‌بودن بین همتایان مرتبط | 10 | `fixtures/heartbeat/` | ✅ |
| **همگام‌سازی پروفایل** | تبادل یک کارت پروفایل امضاشده با یک همتا از طریق mesh | 23 | `fixtures/profiles/` | ✅ |
| **اعلام شناسه موقت** | به‌طور خصوصی به یک دوست شناسه مسیریابی چرخشی فعلی خود را می‌گویید تا حتی پس از چرخش آن بتوانند به شما برسند | 56 | `fixtures/erid/` | ✅ |
| **تبادل پیش‌کلید** | درخواست و تحویل یک بسته پیش‌کلید Signal از طریق mesh، برای راه‌اندازی یک جلسه انتها-به-انتها با کسی که هرگز ملاقات نکرده‌اید | 25, 26 | `fixtures/prekey/` | ✅ |
| **کانال‌ها** | پیام‌های امضاشده به یک کانال گروهی خصوصی و فقط برای اعضا | 7 | `fixtures/channels/` | ✅ |
| **فشار برای صحبت** | فریم‌های صوتی واکی‌تاکی (بار صوتی کدشده مبهم) | 15 | `fixtures/media/` | ✅ |
| **اشتراک صفحه** | فریم‌های تصویری اشتراک صفحه (بار تصویری کدشده مبهم) | 32 | `fixtures/media/` | ✅ |
| **کنترل تماس** | سیگنالینگ زنگ / پذیرش / رد / قطع برای تماس‌های صوتی و تصویری | 27 | `fixtures/videocall/` | ✅ |
| **تأیید SOS** | تأیید به فرستنده که پخش اضطراری او دریافت شد | 6 | `fixtures/sos/` | ✅ |
| **رد پای فضا** | خرده‌های کشف برچسب‌گذاری‌شده با موقعیت برای لایه «اطراف من چه خبر است» | 40 | `fixtures/space/` | ✅ |
| **اعلام Forge** | تبلیغ یک اثر محتوایی مشتق‌شده/ساخته‌شده به mesh | 41 | `fixtures/forge/` | ✅ |
| **درخواست شارد Vault** | واکشی یک شارد ذخیره‌سازی با کد پاک‌کننده (هر K از N شارد فایل را بازسازی می‌کند) | 42 | `fixtures/vaultshard/` | ✅ |
| **اندازه‌گیری پهنای باند** | پروب / تأیید / شایعه توان عبوری پیوند تا mesh روی ضخیم‌ترین لوله مسیریابی کند (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

این‌ها روی سرویس‌های از قبل کامل‌شده **پیام‌رسانی، صدای یک‌به‌یک و گروهی، تماس‌های تصویری، استریمینگ زنده، تماشا با هم، مسیریابی AODV، DTN store-and-forward، و پخش سیل‌آسای SOS** قرار می‌گیرند — که آن‌ها نیز در تمام ۸ زبان پیاده‌سازی شده‌اند.

> **«ساخته‌شده» اینجا دقیقاً به چه معناست.** هر سرویس بسته سیم خود را تولید و مدیریت می‌کند، رویدادهای درست را برمی‌انگیزد، و به یک fixture در سطح بایت پین شده است که کل خانواده زبانی باید با آن مطابقت داشته باشد. برنامه شما سرویس را به جلسه Signal، جدول مسیریابی و حالت محلی آن سیم‌کشی می‌کند. این لایه پروتکل است — اثبات‌شده در کد، آزمون‌ها و fixture‌های بایتی بین‌زبانی — روی همان بنیان صادقانه RF مانند بقیه چیزها: هر مسیری که در نهایت روی یک رادیو سوار می‌شود تا زمان راه‌اندازی سخت‌افزاری که در `OPEN_ISSUES.md` ردیابی شده، در میدان تأییدنشده است.

## امنیت و حریم خصوصی

فراتر از مجموعه سرویس‌های سیم، Aether یک **لایه امنیت و حریم خصوصی** کوچک را ارائه می‌دهد — مدیریت کلید هویت و ضد-ردیابی در لایه پیوند. مانند هر چیز دیگر، هر یک در **تمام ۸ زبان** پیاده‌سازی شده و به یک fixture مشترک بین‌زبانی در `fixtures/<feature>/` پین شده است (Swift و C به‌علاوه روی سرور بیلد macOS تأیید شده‌اند). این‌ها *نه* چهار سرویس سیم دیگر از ۱۸ سرویس هستند: سه‌تای آن‌ها اصلاً **هیچ نوع بسته سیم جدیدی** تعریف نمی‌کنند، و چهارمی مظروف‌های خود را **درون مسیر DTN/mesh موجود** حمل می‌کند نه به‌عنوان یک بسته رزرو‌شده جدید.

| قابلیت | چه کاری انجام می‌دهد | لایه | Fixture | ۸/۸ |
|---|---|---|---|:-:|
| **پشتیبان‌گیری با عبارت بازیابی** | پشتیبان‌گیری از یک هویت به‌صورت یک عبارت **BIP-39 با 24 کلمه** و بازیابی آن روی هر دستگاه. BIP-39 استاندارد (تأییدشده در برابر بردارهای رسمی Trezor)، با چک‌سام SHA-256 به‌گونه‌ای که یک کلمه اشتباه‌تایپ‌شده *رد می‌شود*، هرگز بی‌سروصدا نادرست نمی‌ماند. بدون سرور، بدون امین — عبارت **همان** هویت است. | محلی | `fixtures/bip39/` | ✅ |
| **محافظت در برابر ردیابی Bluetooth** | یک **Service UUID** بلوتوث چرخشی و مشتق‌شده از کلید (HMAC-SHA256، پنجره 15 دقیقه) و **آدرس‌های خصوصی قابل‌حل** (IRK + تابع RFC به نام `ah`، AES-128) مشتق می‌کند — ماده ضد-ردیابی که یک تبلیغ‌کننده BLE نیاز دارد تا یک اسکنر منفعل نتواند آن را در گذر زمان یا مکان به هم پیوند دهد. | لایه پیوند | `fixtures/bleprivacy/` | ✅ |
| **پاک‌سازی اضطراری** | یک **PIN اجبار** (SHA-256، مقایسه‌شده در زمان ثابت) که تحت اجبار، هر کلید هویت را به‌طور امن پاک می‌کند — بازنویسی با داده تصادفی و سپس صفر کردن — به‌گونه‌ای که چیزی برای بازیابی باقی نمی‌ماند. | محلی | `fixtures/panicwipe/` | ✅ |
| **همگام‌سازی چنددستگاهی** | همگام‌سازی **غیرمتمرکز و بدون سرور** میان دستگاه‌های *خودتان*: یک **DeviceLink** امضاشده با Ed25519 آن‌ها را جفت می‌کند، و مظروف‌های **SyncRecord** با اصل «آخرین‌نوشته‌برنده» وضعیت را آشتی می‌دهند — که به‌صورت رمزگذاری انتها-به-انتها روی DTN/mesh موجود حمل می‌شوند، بدون حساب ابری و بدون سرور همگام‌سازی. | روی DTN | `fixtures/sync/` | ✅ |

**یک عدم تقارن صادقانه.** `DeviceLink` چنددستگاهی با Ed25519 امضا می‌شود، و آن امضا **بایت‌به‌بایت در ۷ زبان از ۸ زبان یکسان** است. CryptoKit اپل عمداً امضاهای Ed25519 را *تصادفی‌سازی* می‌کند، بنابراین روی Swift آن 64 بایت امضا هر بار متفاوت است — اما **بدنه امضاشده بایت‌به‌بایت یکسان** است و هر پیوند همچنان روی هر ۸ SDK تأیید می‌شود، پس Swift به برابری **تأیید** می‌رسد نه برابری بایت امضا. این یک ویژگی رمزنگاری پلتفرم است، نه یک نقص، و تنها جایی در میان این چهار قابلیت است که «بایت‌به‌بایت یکسان» یک ستاره به همراه دارد. فرمت‌های کامل سیم در [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12 هستند؛ مدل تهدید در [`THREAT_MODEL.md`](THREAT_MODEL.md) است.

## انتقال‌ها

هر انتقال یک نام رنگ دارد که در سراسر کد استفاده می‌شود. `IsAvailable` مسیرهای بلوکه‌شده توسط سخت‌افزار را کنترل می‌کند — `TransportManager` آن‌ها را رد کرده و به انتقال بعدی موجود برمی‌گردد.

**کلید وضعیت:** ✅ واقعی، ساخته و تأییدشده · ⏳ واقعی، تأیید در حال انجام · ⚠️ واقعی روی برخی پلتفرم‌ها، استاب روی بقیه · ❌ استاب (هنوز کد انتقالی نیست).

| رنگ | نام | برد | پهنای باند | وضعیت |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~۱۰۰ متر | ۱ مگابیت/ثانیه | ✅ واقعی — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~۲۰۰ متر | ۲۵۰ مگابیت/ثانیه | ✅ واقعی — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | رله HTTP / QUIC | نامحدود | ~۱۰ مگابیت/ثانیه | ✅ واقعی — Windows؛ سرور رله در `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | کانال داده اینترنتی | نامحدود | ~۱۰۰ مگابیت/ثانیه | ✅ واقعی در تمام ۸ زبان — **در تمام ۸ زبان با loopback تأییدشده** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust هر کدام دو همتا دارند که بایت‌ها را روی یک کانال داده ICE واقعی مبادله می‌کنند) |
| ⚪ Aether White | NFC HCE | ~۵ سانتی‌متر | ۸۴۸ کیلوبیت/ثانیه | ⚠️ واقعی روی Android (`android/white/`)؛ Windows = تقریب مجاورت BLE-GATT واقعی + RSSI −40 دسی‌بل (`WinNfcBleTransportService`، net9/10 کامپایل می‌شود، در زمان اجرا تأییدنشده) — `Windows.Networking.Proximity` در Win 11 حذف شده |
| 🩵 Aether Teal | NearLink | ~۶۰۰ متر | ۱۲ مگابیت/ثانیه | ⚠️ واقعی روی HarmonyOS (`harmonyos/teal/`، `@kit.NearLinkKit` — در انتظار تأیید روی دستگاه)؛ Android + Windows = تقریب SSAP-over-BLE واقعی (`android/teal/AetherNetSleService`، `WinNearLinkBleTransportService`؛ کامپایل + آزمون واحد تأییدشده، در زمان اجرا تأییدنشده) |
| 🔴 Aether Red | LoRa / CircleLink | ~۱۵ کیلومتر | ۳۷.۵ کیلوبیت/ثانیه | ⚠️ درایور سریال RYLR SX127x/SX126x واقعی (`LoRaSerialTransport` در C#/Go/Rust/C؛ کامپایل می‌شود، در زمان اجرا تأییدنشده — به یک ماژول فیزیکی نیاز دارد)؛ پل BLE Coded-PHY هنوز یک طرح مستند است |

انتقال‌های رادیویی فقط جایی واقعی هستند که کد پلتفرم وجود دارد (C#/Windows، Kotlin/Android، HarmonyOS). هشت کتابخانه زبانی در غیر این صورت یک انتقال **شبیه‌سازی درون‌پروسه‌ای** برای آزمایش ارائه می‌کنند — **WebRTC نخستین انتقال واقعی مشترک بین همه آن‌هاست** (کامل؛ در بین زبان‌ها با loopback تأییدشده).

اولویت بر اساس هزینه انرژی است: mesh رادیویی ترجیح داده می‌شود، سپس WebRTC به‌عنوان یک مسیر مستقیم اینترنتی، با رله HTTP/QUIC به‌عنوان آخرین چاره.

## لایه‌های استقرار

Aether روی هر پلتفرمی که از Bluetooth یا Wi-Fi پشتیبانی می‌کند کار می‌کند. لایه‌ای که در آن هستید به سیستم‌عامل هدف شما بستگی دارد.

---

### لایه استاندارد — هر پلتفرم

Android · Windows · Linux · macOS · iOS

Aether روی هر دستگاهی با سخت‌افزار Bluetooth یا Wi-Fi اجرا می‌شود. جایی که یک رادیو از نظر فیزیکی وجود ندارد، هر انتقال مسدودشده با استفاده از آنچه موجود است تقریب زده می‌شود. این تقریب‌ها اکنون **کد واقعی** هستند (کامپایل تأییدشده؛ **در زمان اجرا تأییدنشده** در انتظار یک آزمون RF ۲-دستگاهی / سخت‌افزاری):

- **NearLink (Aether Teal)** — تقریب SSAP-over-BLE-GATT واقعی (Aether SLE UUID `61657468-6572-0003-…`) روی Android (`android/teal/AetherNetSleService`) و Windows (`WinNearLinkBleTransportService`)؛ کامپایل + آزمون واحد تأییدشده، در زمان اجرا تأییدنشده. رادیو NearLink واقعی فقط روی HarmonyOS وجود دارد (`harmonyos/teal/`، در انتظار تأیید روی دستگاه).
- **LoRa (Aether Red)** — درایور سریال RYLR SX127x/SX126x واقعی (`LoRaSerialTransport` در **تمام ۸ زبان** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin؛ هر پورت کامپایل تأییدشده، شامل Swift + C روی سرور بیلد Mac؛ در زمان اجرا تأییدنشده — به یک ماژول فیزیکی نیاز دارد). پل Meshtastic-over-BLE-Coded-PHY (~۱.۳ کیلومتر) یک طرح مستند باقی می‌ماند؛ LoRa واقعی برد بلند به یک گره با قابلیت LoRa نیاز دارد (دروازه، SBC یا دستگاه دستی مقاوم با یک ماژول LoRa).
- **NFC (Aether White)** — واقعی روی Android (HCE). Windows اکنون یک تقریب مجاورت BLE-GATT + RSSI −40 دسی‌بل واقعی دارد (`WinNfcBleTransportService`، net9/10 کامپایل می‌شود؛ در زمان اجرا تأییدنشده)؛ ACR122U PC/SC وقتی یک خواننده حاضر باشد.

آنچه واقعی و همه‌جا یکسان است: **BLE، Wi-Fi Direct، رله HTTP/QUIC، و انتقال WebRTC P2P (در تمام ۸ زبان با loopback تأییدشده)**، به‌علاوه امنیت Signal Protocol (X3DH + Double Ratchet)، مسیریابی AODV، DTN store-and-forward، پخش SOS، صدا و استریمینگ.

**وضعیت صادقانه:** BLE + Wi-Fi Direct + رله در تولید واقعی هستند؛ **WebRTC P2P در تمام ۸ زبان واقعی و با loopback تأییدشده است** (دو همتا بایت‌ها را روی یک کانال داده ICE واقعی مبادله می‌کنند — Rust روی جعبه لینوکس `.201` با ICE UDP کارآمد تأیید شد)؛ تقریب‌های NearLink / LoRa / NFC-روی-Windows اکنون کد واقعی هستند که کامپایل می‌شود (LoRa در تمام ۸ زبان کامپایل تأییدشده، شامل Swift + C روی سرور بیلد Mac؛ NearLink-Android نیز آزمون واحد شده) اما **در زمان اجرا تأییدنشده** است — هنوز آزمون سخت‌افزاری / RF ۲-دستگاهی ندارد. آن‌ها در کد در mesh مشارکت می‌کنند؛ آن سه را با انتظار RF اثبات‌شده در میدان مستقر نکنید.

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

تمام ۸ زبان بسته‌های سیم بایت‌به‌بایت یکسانی تولید می‌کنند که توسط ۱۷ fixture استاندارد فرمت سیم و ۶ بردار آزمون Signal در CI تأیید می‌شوند (`fixtures/expected/*.bin`، `fixtures/signal/expected/*.json`). مسیریابی (RREQ/RREP به‌سبک AODV)، DTN store-and-forward، پخش SOS، صدا، استریمینگ و سرویس‌های سخت‌گیری امنیتی در هر زبان با **~۳,۰۰۰ آزمون** در تمام ۸ پیاده‌سازی اجرا می‌شوند:

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

تعامل‌پذیری Signal بین‌زبانی به `fixtures/signal/` با بردارهای آزمون مشترک برای X3DH (`x3dh_basic`)، رچت متقارن (`ratchet_step_basic`، `ratchet_step_three_iterations`)، KDF_RK (`kdf_rk_basic`) و رفت‌وبرگشت کامل جلسه X3DH (`x3dh_session_msg1`، `x3dh_session_reply`) لنگر انداخته است. هر پیاده‌سازی باید خروجی‌های بایت‌به‌بایت یکسانی در برابر آن fixture‌ها تولید کند. هر ۸ زبان اکنون یک جلسه Signal کامل (`generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt`) دارند.

فراتر از فرمت سیم و Signal، **کل مجموعه سرویس‌های سیم** — حضور، ضربان قلب، همگام‌سازی پروفایل، اعلام شناسه موقت، تبادل پیش‌کلید، کانال‌ها، فشار برای صحبت، اشتراک صفحه، کنترل تماس، تأیید SOS، رد پای فضا، اعلام forge، درخواست شارد vault، و اندازه‌گیری پهنای باند (به **آنچه دریافت می‌کنید** مراجعه کنید) — به همین ترتیب در تمام ۸ زبان پیاده‌سازی شده و به fixture‌های خودش پین شده است (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/` و خواهرها). هیچ ویژگی‌ای در لایه پروتکل فقط-C# نیست.

## شروع سریع

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
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

دمو کلیدهای هویتی را برای دو گره تولید می‌کند، بسته‌های پیش‌کلید را مبادله می‌کند، جلسات رمزگذاری‌شده برقرار می‌کند، پیام‌های رمزگذاری‌شده را در هر دو جهت ارسال می‌کند، بسته‌های mesh ایجاد و امضا می‌کند، امضاها را تأیید می‌کند و بسته‌ها را به فرمت سیم باینری سریال‌سازی می‌کند. همچنین لایه انتقال درون‌پروسه‌ای را نمایش می‌دهد.

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
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethernet_packet_free(packet);
```

## نقشه راه

آنچه ساخته شده و آنچه در پیش است.

**انجام‌شده (تأییدشده بین‌زبانی، تمام ۸ پیاده‌سازی):**
- فرمت سیم: بایت‌به‌بایت یکسان در ۸ زبان، لنگرانداخته‌شده با ۱۷ fixture استاندارد و ادعاهای بین‌زبانی در CI (`fixtures/expected/*.bin`)
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
- ✅ **کلید موقت X3DH واقعی (۸ زبان)** — ۴ عملیات DH با X25519 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) با مشتق‌سازی ریشه HKDF-SHA256. پین‌شده با `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **هم‌راستایی Double Ratchet در سراسر خانواده** — Signal §5 کامل با HMAC-SHA256 + جداسازی دامنه 0x01/0x02 در رچت متقارن، HKDF-SHA256 KDF_RK در مرحله DH-ratchet، چرخش DH در دریافت. تأییدشده با fixture‌های `ratchet_step_basic`، `ratchet_step_three_iterations`، `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 با HEAD آشتی داده شده** — به `docs/PROTOCOL_SPEC.md` مراجعه کنید.

**انجام‌شده (تمام ۸ زبان):**
- ✅ **تماس‌های صوتی (یک‌به‌یک)** — ماشین حالت سیگنالینگ (Offer/Answer/Hangup/Cancel/Timeout) + انتقال فریم باینری (16B callId · 4B seq · 8B timestamp · 1B isSilence · N بایت). تحویل آگاه از مسیر از طریق `IRoutingService`.
- ✅ **صدای گروهی** — عضویت مبتنی بر میزبان (دعوت/اخراج/خروج)، فیلد تولید کلید per-frame، fan-out یک‌کاسته به تمام اعضای فعلی، چرخش کلید کنترل‌شده توسط میزبان در تغییر عضویت.
- ✅ **استریمینگ زنده** — ناشر `StreamAnnounce` پخش می‌کند؛ مشترکین `StreamSubscribe` ارسال می‌کنند؛ فریم‌های باینری `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N بایت) unicast به هر مشترک.
- ✅ **تماس‌های تصویری (یک‌به‌یک)** — مذاکره کدک/رزولوشن/fps/bitrate در سیگنالینگ، سیگنال‌های درخواست keyframe و تغییر کیفیت، فرمت `VideoFrame` باینری مطابق با طرح‌بندی صوتی.
- ✅ **تماشا با هم** — میزبان دستورات `WatchSync` معتبر (پخش/توقف/جستجو/سرعت) صادر می‌کند؛ پیروان با جبران RTT اعمال می‌کنند (`position = positionMs + elapsed × playbackSpeed`)؛ `WatchReaction` fire-and-forget.
- ✅ **مخزن کلید پیش‌پرداخت یک‌بار مصرف (OPK)** — پیش‌فرض ۱۰۰، صدور FIFO، شارژ تنبل، مصرف محافظت‌شده با قفل در تمام ۸ زبان. خطر همزمانی single-OPK را بسته می‌کند.
- ✅ **C: جلسه Signal کامل** — `aethernet_signal_service_init`، `generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt` در `c/src/signal_protocol.c`؛ ۶ آزمون E2E دو-گره در `c/tests/test_signal_session.c`. هر ۸ زبان اکنون Signal Protocol کامل با قابلیت جلسه دارند.

**انجام‌شده (تمام ۸ زبان — کل مجموعه سرویس‌های سیم):**
- ✅ **هر نوع بسته رزروشده اکنون یک سرویس واقعی و بایت‌به‌بایت یکسان در تمام ۸ زبان است.** بیکن/پرس‌وجوی حضور (21/22)، ضربان قلب (10)، همگام‌سازی پروفایل (23)، اعلام شناسه مسیریابی موقت (56)، تبادل پیش‌کلید (25/26)، کانال‌ها (7)، فشار برای صحبت (15)، اشتراک صفحه (32)، کنترل تماس (27)، تأیید SOS (6)، رد پای فضا (40)، اعلام forge (41)، درخواست شارد vault (42)، و اندازه‌گیری پهنای باند / ABMF (53/54/55). هر یک یک سرویس نازک است (تولید + مدیریت + رویداد) که میزبان به جلسه Signal و جدول مسیریابی خود سیم‌کشی می‌کند؛ هر یک به یک fixture مشترک بین‌زبانی پین شده است (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/`، `fixtures/channels/`، `fixtures/profiles/`، `fixtures/heartbeat/`، `fixtures/erid/`، `fixtures/space/`، `fixtures/forge/`، `fixtures/sos/`) و توسط آزمون‌های واحد هر زبان اجرا می‌شود، با Swift و C که روی سرور بیلد macOS تأیید شده‌اند. به **آنچه دریافت می‌کنید** مراجعه کنید.

**انجام‌شده (فقط مرجع C#):**
- ✅ **مرحله ۹ دمو — MessagingService + DTN fallback انتها-به-انتها** — `samples/AetherNet.Demo.Console` پیام‌رسانی رمزگذاری‌شده با Signal واقعی را با DTN store-and-forward وقتی گیرنده آفلاین است طی می‌کند.
- ✅ **پل `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` لایه پیام‌رسانی را به‌طور پیش‌فرض انتها-به-انتها رمزگذاری می‌کند؛ پیام‌های بدون جلسه Signal صف می‌شوند، هرگز ناامن ارسال نمی‌شوند.
- ✅ **استریمینگ با bitrate تطبیقی** — `AdaptiveBitrateController` با نردبان‌های bitrate مشخص‌شده در spec برای Profile A (زمان واقعی)، B (پخش زنده) و C (VOD). ناشر بالاترین پله پایدار را انتخاب می‌کند (۲۰٪ سرفضا) و به‌جای یک سگمنت وقتی زیر کف است `StreamAbandon` (`PacketType.StreamAbandon`) صادر می‌کند. `IStreamingService` متدهای `UpdateBandwidthEstimate` و `GetCurrentBitrateRung` را در معرض دید قرار می‌دهد.
- ✅ **تماشا با هم: ورودی BitTorrent + تأمین مالی گروهی ChipIn** — مدل‌های `TorrentInfo` / `TorrentFile`؛ `WatchTogetherService`، `PacketType.TorrentMetadata` را مدیریت می‌کند و `TorrentReceived` را برمی‌انگیزد. ماشین حالت `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded)؛ `StartChipInAsync` / `ContributeAsync` / `GetChipIn` روی `IWatchTogetherService`.
- ✅ **تماس‌های تصویری گروهی با رله SFU خودکار** — `GroupVideoService` / `IGroupVideoService`. توپولوژی FullMesh برای ≤ ۳ شرکت‌کننده؛ تعویض خودکار به SFU در `SfuThresholdParticipants` (۴) با تخصیص مجدد رله از طریق `GroupVideoSignaling(SfuAssigned)`. Fan-out در FullMesh، ارسال فقط-رله در حالت SFU. نوع بسته سیگنالینگ `GroupVideoSignaling = 35`.
- ✅ **شبیه‌سازی انتقال BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). فریم‌بندی GATT MTU از طریق `BleGattFramer` (۱۰۲۴ بایت/فریم، `[2B count][2B index][payload]`)، رجیستری همتای ثابت درون‌پروسه‌ای، پخش تبلیغ. تمام محدودیت‌های `BleMaxPayloadBytes` اعمال می‌شوند.
- ✅ **شبیه‌سازی انتقال Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). چرخه حیات صریح `ConnectAsync`/`DisconnectAsync`، تحویل مستقیم بار بزرگ (بدون فریم‌بندی)، رویدادهای دوطرفه `PeerConnected`/`PeerDisconnected`.
- ✅ **شبیه‌سازی انتقال NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU فریم ۴۰۹۶ بایت، رجیستری ۵۰۰-همتایی، `ConnectedPeerCount`، `IsAvailable` قابل تنظیم در زمان اجرا.
- ✅ **آزمون‌های شبیه‌سازی راه‌اندازی RF** — آزمون‌های تعامل‌پذیری دو-گره (`SimulatedTransportTests`): رفت‌وبرگشت `MeshPacket` روی BLE + NearLink، انتقال بار ۶۴ کیلوبایتی Wi-Fi Direct. لایه نرم‌افزاری کاملاً تأییدشده؛ جلسه آزمایشگاه دستگاه فیزیکی برای اعتبارسنجی روی سخت‌افزار مورد نیاز است.

**انجام‌شده (لایه انتقال C# — همه fail-fast):**
- ✅ **انتقال BLE GATT واقعی** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server). آزمون کامل راه‌اندازی RF در `samples/AetherNet.BleRfTest/`.
- ✅ **انتقال Wi-Fi Direct واقعی** — `WinWifiDirectTransportService` (WinRT، `WiFiDirectAdvertisementPublisher` + TCP StreamSocket پورت 8888) + `android/green/` (`WifiP2pManager`). آزمون RF در `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **انتقال رله HTTP (Aether Purple)** — `HttpRelayTransportService` با long-poll ۱۰ ثانیه‌ای، `PowerCostRelative = 100`، همیشه آخرین چاره. سرور رله در `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API، پورت 5200). آزمون RF در `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/`، `HostApduService` را با AID `F061657468657200` پیاده‌سازی می‌کند. `WinNfcStubTransportService` دو مسیر تقریب Windows را مستند می‌کند: (۱) NDEF-over-BLE-GATT با دروازه RSSI ≥ −40 دسی‌بل (بدون چیپ NFC، tap-to-connect را شبیه‌سازی می‌کند، `IsAvailable = Bluetooth حاضر`)؛ (۲) خواننده USB ACR122U از طریق `Windows.Devices.SmartCards` PC/SC (`IsAvailable = خواننده بدون تماس شمارش‌شده`). مسیر ارتقا: `ITransportService` را وقتی مایکروسافت یک API درجه اول P2P NFC ارائه داد پیاده‌سازی کنید.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — پیاده‌سازی کامل HarmonyOS 5.0.1 (API 13) ArkTS با استفاده از `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`)؛ `isAvailable` در زمان اجرا بررسی می‌شود. `WinNearLinkStubTransportService` + `android/teal/` تقریب SSAP-over-BLE را مستند می‌کنند: BLE GATT با UUID سرویس Aether SLE `61657468-6572-0003-0000-000000000000` — از نظر API مشابه SSAP، نه سازگار با سیم با سخت‌افزار NearLink واقعی. مسیر ارتقا: فراخوانی‌های BLE GATT را با فراخوانی‌های SDK `ssapc_*`/`ssaps_*` جایگزین کنید؛ UUIDها و اسلات `TransportManager` بدون تغییر.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` تقریب Meshtastic-over-BLE-LR را مستند می‌کنند: فرمت کامل سیم Meshtastic (هدر ۱۶ بایتی + protobuf AES-256-CTR) روی BLE 5.0 Coded PHY S=8 (~۱.۳ کیلومتر در فضای باز)، با مسیریابی managed-flood و پنجره رقابت وزن‌دار با RSSI. فدراسیون گره پل با سخت‌افزار LoRa واقعی به‌طور خودکار کار می‌کند (همان فرمت بسته Meshtastic، بدون ترجمه). مسیر ارتقا: رادیو BLE LR را با درایور AT-command یا SPI SX1276/SX1278 جایگزین کنید؛ فرمت بسته و مسیریابی بدون تغییر.

**باز — ردیابی‌شده در `OPEN_ISSUES.md`:**
- راه‌اندازی RF روی سخت‌افزار واقعی: آزمون تعامل‌پذیری انتها-به-انتها دو-گره روی دستگاه‌های فیزیکی BLE / Wi-Fi Direct (آزمون‌های شبیه‌سازی پاس می‌شوند؛ جلسه آزمایشگاه سخت‌افزار مورد نیاز است)
- NearLink: `harmonyos/teal/` کامل است؛ نیاز به سخت‌افزار Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 دارد (چیپ NearLink روی دستگاه‌های غیر Huawei وجود ندارد). Windows + Android به‌طور خودکار به تقریب SSAP-over-BLE برمی‌گردند.
- LoRa / CircleLink: ماژول رادیویی برای برد واقعی LoRa مورد نیاز است. بدون آن، فرمت سیم Meshtastic روی BLE LR (~۱.۳ کیلومتر) حمل می‌شود و فدراسیون گره پل با سخت‌افزار LoRa واقعی در دسترس است.
- ✅ **(حل‌شده در v1.2.0)** سطح پروتکل مصرف‌کننده (Wave 16/17) — رویداد `IDtnService.BundleReceived` برای بسته‌های ورودی ([#59](https://github.com/bhengubv/aether-protocol/issues/59))، دایرکتوری نام‌گذاری/کشف لایه برنامه ([#60](https://github.com/bhengubv/aether-protocol/issues/60))، رابط تیپینگ نویسنده ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). هر ۳ به‌صورت افزایشی در ۸ زبان با fixture‌های بین‌زبانی بایت‌برابر ارائه شدند. به CHANGELOG مراجعه کنید.

**هنوز برای مشارکت خارجی باز نشده:**
- پروتکل هنوز در حال توسعه فعال است. در این زمان مشارکت‌های خارجی پذیرفته نمی‌شوند.
- پیاده‌سازی انتقال NearLink، نمونه‌های یکپارچه‌سازی Android/iOS، بک‌اندهای انتقال اضافی، محک‌های عملکرد و فازینگ پروتکل به‌صورت داخلی ردیابی می‌شوند و وقتی پروژه به یک نقطه پایدار مشارکت عمومی رسید باز خواهند شد.

## ساختار پروژه

```
aether-protocol/
  src/
    AetherNet.Core/          مدل‌های پروتکل، ثابت‌ها، سریال‌سازی بسته
    AetherNet.Security/      Signal Protocol، Ed25519، امضای بسته
    AetherNet.Transport/     انتزاع‌های انتقال، NearLink، شبیه‌ساز درون‌پروسه‌ای
    AetherNet.Messaging/     مدیریت پیام و رله
    AetherNet.Storage/       پایداری DTN store-and-forward
    AetherNet.Streaming/     استریمینگ bitrate تطبیقی، مدل‌ها و رابط‌های تصویری
    AetherNet.Voice/         تماس‌های صوتی و صدای گروهی
    AetherNet.Content/       تأیید محتوا و انتقال تقسیم‌شده
  samples/
    AetherNet.Demo.Console/  دمو تعاملی
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
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

## پرسش‌های متداول

**آیا AetherNet بدون اینترنت کار می‌کند؟**
بله — با اولویت آفلاین است. دستگاه‌ها مستقیماً از طریق Bluetooth، Wi-Fi Direct، NearLink یا LoRa صحبت می‌کنند و پیام‌ها را هاپ‌به‌هاپ از طریق دستگاه‌های دیگر رله می‌کنند، بدون نیاز به اتصال اینترنت، دکل مخابراتی یا سرور. وقتی مسیر زنده‌ای وجود ندارد، پیام‌ها (ذخیره‌و‌ارسال تحمل‌کننده تأخیر) تا ۷۲ ساعت نگه داشته می‌شوند تا مسیری باز شود.

**آیا رمزگذاری انتها-به-انتها دارد؟**
بله. AetherNet از Signal Protocol (توافق کلید X3DH به‌علاوه Double Ratchet روی X25519) برای رمزگذاری انتها-به-انتها، AES-256-GCM برای بارهای پیام و امضاهای Ed25519 روی هر بسته استفاده می‌کند. دستگاه‌هایی که یک پیام را رله می‌کنند نمی‌توانند آن را بخوانند.

**از چه انتقال‌هایی استفاده می‌کند؟**
Bluetooth LE، Wi-Fi Direct، NearLink (SLE)، یک رادیو سریال LoRa/CircleLink، یک رله HTTP/QUIC و WebRTC برای اتصال مستقیم اینترنتی نقطه‌به‌نقطه. پروتکل به‌طور خودکار کم‌مصرف‌ترین انتقال موجود را برای هر بسته انتخاب می‌کند و به انتقال بعدی برمی‌گردد.

**در چه زبان‌های برنامه‌نویسی موجود است؟**
هشت — C#، Rust، TypeScript، Python، Go، Kotlin، Swift و C. هر پیاده‌سازی بسته‌های سیم بایت‌به‌بایت یکسانی تولید می‌کند که توسط یک مجموعه fixture مشترک بین‌زبانی در CI اعمال می‌شود، بنابراین بسته‌ای که توسط یک زبان ساخته شده بدون تغییر توسط هر زبان دیگری رمزگشایی می‌شود.

**چه تفاوتی با Meshtastic، Briar یا Bridgefy دارد؟**
Meshtastic فقط-LoRa است؛ AetherNet چندانتقاله (Bluetooth + Wi-Fi + NearLink + LoRa) است و علاوه بر پیام، صدا، تصویر و استریمینگ را نیز حمل می‌کند. Briar فقط-Android است و روی Tor مسیریابی می‌کند؛ AetherNet چندپلتفرمی و mesh خالص است. برخلاف SDKهای بسته، AetherNet با مجوز MIT و به‌صورت باز در هشت زبان پیاده‌سازی شده است. جدول مقایسه بالا جزئیات را دارد.

**آیا آماده تولید است؟**
لایه پروتکل — فرمت سیم، امنیت Signal، مسیریابی، DTN store-and-forward و کل مجموعه سرویس‌ها — در تمام هشت زبان پیاده‌سازی و آزمون شده است. انتقال‌های رادیویی جایی که کد پلتفرم وجود دارد واقعی هستند (Bluetooth و Wi-Fi روی Windows و Android، WebRTC همه‌جا) و در جاهای دیگر در انتظار راه‌اندازی سخت‌افزار، در میدان تأییدنشده‌اند، که صادقانه در `OPEN_ISSUES.md` ردیابی می‌شود. قبل از استقرار، یادداشت‌های وضعیت در هر بخش را بخوانید.

**تحت چه مجوزی است؟**
MIT — رایگان برای استفاده تجاری و متن‌باز. به [LICENSE](LICENSE) مراجعه کنید.

**چه کسی AetherNet را می‌سازد؟**
به‌عنوان پروتکل باز پشت اکوسیستم mesh شرکت The Geek Network توسعه یافته است، ساخته‌شده در آفریقای جنوبی برای ارتباطی که با یا بدون داده موبایل کار می‌کند.

## نقاط توسعه

پروتکل به‌تنهایی کار می‌کند. این رابط‌ها به شما اجازه می‌دهند بک‌اند خودتان را اگر خواستید وصل کنید:

- `IAetherNetIncentiveProvider` — پاداش به گره‌هایی که ترافیک را رله می‌کنند (پیش‌فرض no-op: رله نوع‌دوستانه)
- `IAetherNetBackendClient` — همگام‌سازی با سرور وقتی اینترنت موجود است (پیش‌فرض no-op: کاملاً آفلاین)
- `IAetherNetFeatureFlagProvider` — تغییر ویژگی‌های پروتکل در زمان اجرا (پیش‌فرض no-op: همه چیز فعال)

هر سه با پیاده‌سازی‌های no-op ارائه می‌شوند. آن‌ها را بردارید و چیزی خراب نمی‌شود.

## مشارکت

مشارکت‌های خارجی هنوز باز نشده‌اند. پروژه هنوز در حال توسعه فعال است. وقتی پنجره مشارکت عمومی را اعلام کردیم برگردید.

## امنیت

به [SECURITY.md](SECURITY.md) برای سیاست افشای مسئولانه مراجعه کنید.

## مجوز

مجوز MIT. به [LICENSE](LICENSE) مراجعه کنید.

## ترجمه‌ها

این README به زبان انگلیسی نگهداری می‌شود و به ۱۰ زبان دیگر در [`docs/i18n/`](docs/i18n/) ترجمه شده است: Français، Español، العربية، 中文简体، 日本語، Deutsch، Português (BR)، Русский، فارسی و 한국어. **نسخه انگلیسی منبع حقیقت است** — جایی که یک ترجمه و متن انگلیسی با هم اختلاف دارند، متن انگلیسی معتبر است و ممکن است ترجمه‌ها یک یا دو نسخه از آن عقب باشند. پروتکل، کد، fixture‌ها و رفتار توصیف‌شده، مهم نیست کدام زبان را می‌خوانید، یکسان هستند.

</div>
