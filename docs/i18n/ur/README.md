```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

<div dir="rtl">

اپنے آس پاس کے لوگوں کے ساتھ فائلیں، پیغامات اور اسٹریمز شیئر کریں۔ نہ WiFi۔ نہ موبائل ڈیٹا۔ نہ سائن اپ۔ AirDrop کی طرح، سوائے اس کے کہ یہ ہر پلیٹ فارم پر، ہر کسی کے ساتھ کام کرتا ہے۔

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](README.md) · [Français](docs/i18n/fr/README.md) · [Español](docs/i18n/es/README.md) · [العربية](docs/i18n/ar/README.md) · [中文简体](docs/i18n/zh-CN/README.md) · [日本語](docs/i18n/ja/README.md) · [Deutsch](docs/i18n/de/README.md) · [Português (BR)](docs/i18n/pt-BR/README.md) · [Русский](docs/i18n/ru/README.md) · [فارسی](docs/i18n/fa/README.md) · [한국어](docs/i18n/ko/README.md) · [isiZulu](docs/i18n/zu/README.md) · [Afrikaans](docs/i18n/af/README.md) · [Sesotho](docs/i18n/st/README.md) · [Kiswahili](docs/i18n/sw/README.md) · [Hausa](docs/i18n/ha/README.md) · [አማርኛ](docs/i18n/am/README.md) · [हिन्दी](docs/i18n/hi/README.md) · [Bahasa Indonesia](docs/i18n/id/README.md) · [বাংলা](docs/i18n/bn/README.md) · [اردو](docs/i18n/ur/README.md)

> **ایک پروٹوکول، آٹھ زبانیں، وائر پر ایک جیسا۔** Aether کو **C#، Rust، TypeScript، Python، Go، Kotlin، Swift، اور C** میں نافذ کیا گیا ہے — اور ہر پیکٹ ان سب میں بائٹ در بائٹ ایک جیسا ہے، جسے CI میں ایک مشترکہ کراس-لینگویج فکسچر کارپس کے ذریعے نافذ کیا جاتا ہے۔ اپنا نوڈ آٹھ میں سے کسی بھی زبان میں بنائیں؛ یہ باقی سب کے ساتھ باہمی طور پر کام کرے گا۔ یہ README گیارہ انسانی زبانوں میں بھی دستیاب ہے (لنکس اوپر)۔

## آپ اس سے کیا کر سکتے ہیں؟

**ڈیٹا خرچ کیے بغیر لیکچر نوٹس شیئر کریں۔**

آپ ایک اسٹڈی گروپ میں ہیں۔ کسی کے فون پر پرانے پیپرز ہیں۔ Aether انہیں براہِ راست بلوٹوتھ کے ذریعے آپ کے آلے پر بھیج دیتا ہے — نہ ہاٹ اسپاٹ، نہ WhatsApp گروپ، نہ فائل سائز کی کوئی حد۔ اگر گروپ میں کوئی رینج سے باہر ہو، تو فائل دوسرے آلات سے ہوتی ہوئی اُس تک پہنچتی ہے۔ ضرورت پڑنے پر پیغامات راستے کے انتظار میں 72 گھنٹے تک رُکے رہتے ہیں۔

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**جانیں کہ آپ کے آس پاس کیا ہو رہا ہے۔**

آپ کسی کیمپس ایونٹ یا فیسٹیول میں ہیں۔ Aether بلوٹوتھ اور WiFi Direct کے ذریعے آس پاس کے دوسرے آلات کو دریافت کرتا ہے — نہ کوئی ایپ فیڈ، نہ کوئی الگورتھم۔ آپ وہ دیکھتے ہیں جو واقعی آپ کے آس پاس ہے، نہ کہ جو پروموٹ کیا جا رہا ہے۔

**جب سگنل نہ ہو تو SOS بھیجیں۔**

آپ کے فون میں کوئی سگنل نہیں۔ Aether رینج میں موجود ہر آلے کو ایک ہنگامی پیغام نشر کرتا ہے، اور وہ آلات اسے آگے پہنچاتے ہیں۔ کسی سیل ٹاور کی ضرورت نہیں۔

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**نجی گروپ چینلز بنائیں۔**

اپنی رہائشی منزل، اپنی سوسائٹی، اپنی پروجیکٹ ٹیم کے لیے ایک چینل۔ صرف تصدیق شدہ اراکین ہی پیغامات پڑھ یا بھیج سکتے ہیں۔ کوئی سرور گفتگو کو محفوظ نہیں کرتا۔

**آس پاس کے لوگوں کو چیزیں بیچیں۔**

فروخت کے لیے کوئی نصابی کتاب درج کریں۔ میش کی رینج میں چلنے والے لوگ اسے دیکھتے ہیں۔ نہ کوئی مارکیٹ پلیس اکاؤنٹ، نہ لسٹنگ فیس — بس قربت۔

**میش کے آر پار، مل کر فلم دیکھیں۔**

آپ کے گروپ کی مووی نائٹ ہے۔ کسی کے پاس فائل ہے۔ Aether ہر آلے پر پلے بیک ہم آہنگ کرتا ہے — پلے، پاز، سیک — سب مکمل ہم آہنگی میں۔ اگر فائل صرف کچھ لوگوں کے پاس ہو، تو میش اسے حقیقی وقت میں ایک P2P اسٹریم کے طور پر تقسیم کرتا ہے۔ اگر کسی کے پاس نہ ہو تو سب اسے خریدنے کے لیے SDPKT کے ذریعے حصہ ڈالتے ہیں۔

## یہ کیسے کام کرتا ہے

آلات ایک دوسرے سے براہِ راست بلوٹوتھ، WiFi Direct، یا NearLink استعمال کرتے ہوئے بات کرتے ہیں۔ نہ کوئی انٹرنیٹ کنکشن، نہ سرور، نہ کوئی مرکزی ڈھانچہ۔

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

جب کوئی پیغام اپنی منزل تک براہِ راست نہیں پہنچ سکتا، تو یہ دوسرے آلات سے ہوتا ہوا آگے بڑھتا ہے۔ وہ ریلے کرنے والے آلات نہیں پڑھ سکتے کہ وہ کیا لے کر جا رہے ہیں — ہر پیغام AES-256-GCM سے خفیہ کیا گیا ہے۔ ہر پیکٹ Ed25519 شناختی کیز سے دستخط شدہ ہے، اور جعلی پیکٹس کو نیٹ ورک گرا دیتا ہے۔

> **سیکیورٹی پختگی کا نوٹ (شپ کرنے سے پہلے پڑھیں):** اصل X3DH (4 X25519 DHs)، مکمل Signal Double Ratchet (وصولی پر DH-روٹیشن مرحلہ، KDF_RK، 0x01/0x02 چین ریچٹ)، اور ون-ٹائم پری-کی پول (بذریعۂ طے شدہ 100 OPKs، FIFO، لاک-محفوظ) **تمام 8 زبانوں** میں نافذ ہیں اور `fixtures/signal/` کے تحت ایک مشترکہ کراس-لینگویج فکسچر کارپس سے منسلک ہیں۔ واحد باقی رہ جانے والا کھلا مسئلہ اصل BLE ہارڈویئر پر فزیکل RF برنگ-اپ ہے (جس کا سراغ `OPEN_ISSUES.md` میں رکھا گیا ہے)۔

نہ اکاؤنٹس، نہ فون نمبر، نہ ای میل۔ آپ ایک کی-پیئر بناتے ہیں اور آپ نیٹ ورک پر ہیں۔

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

**روٹنگ** — دستخط شدہ روٹ رپلائیز کے ساتھ AODV۔ ہر روٹ رپلائی منزل کی Ed25519 کی سے دستخط شدہ ہے، لہٰذا کوئی بھی آلہ ایسی منزل ہونے کا ڈھونگ نہیں کر سکتا جو وہ نہیں ہے۔

**اسٹور-اینڈ-فارورڈ** — جب کوئی لائیو روٹ نہ ہو، تو پیکٹس 72 گھنٹے تک اُس وقت تک روکے رکھے جاتے ہیں جب تک کوئی راستہ کھل نہ جائے۔

**ٹرانسپورٹ کا انتخاب** — پروٹوکول ہر پیکٹ کے لیے درست ٹرانسپورٹ کا انتخاب کرتا ہے۔ چھوٹے کنٹرول پیغامات BLE پر جاتے ہیں۔ بھاری منتقلیاں WiFi Direct استعمال کرتی ہیں۔ NearLink جب دستیاب ہو۔

**آواز، ویڈیو، اور اسٹریمنگ** — کوڈیک نیگوشی ایشن (H.264/H.265/VP8) کے ساتھ ویڈیو کالز، ٹرانسپورٹ سے آگاہ کوالٹی کا انتخاب، خودکار SFU ریلے کے ساتھ گروپ ویڈیو، RTT معاوضے کے ساتھ ہم آہنگ واچ-ٹوگیدر، اور اڈاپٹو بٹ ریٹ اسٹریمنگ۔

**ری پلے تحفظ** — 5 منٹ کی ٹائم اسٹیمپ تازگی ونڈو کے ساتھ nonce ڈی ڈپلی کیشن۔

## آپ کو کیا ملتا ہے — ہر سروس، ہر زبان میں

Aether محض ایک ٹرانسپورٹ نہیں ہے۔ پروٹوکول کے مختص کردہ ہر پیکٹ ٹائپ اب **تمام 8 زبانوں میں ایک اصل، کارآمد سروس ہے**، اور ہر ایک **بائٹ-یکساں وائر پیکٹس** میں سیریلائز ہوتی ہے — Go نوڈ کا بنایا ہوا ایک پیکٹ Swift، Rust، C، Python، TypeScript، Kotlin، یا C# نوڈ بغیر کسی تبدیلی کے ڈی کوڈ کر لیتا ہے۔ ہر سروس `fixtures/<service>/` کے تحت ایک مشترکہ کراس-لینگویج فکسچر سے منسلک ہے اور فی-زبان یونٹ ٹیسٹس کے ذریعے آزمائی جاتی ہے، جبکہ Swift اور C کی مزید تصدیق macOS بلڈ سرور پر ہوتی ہے۔

| صلاحیت | یہ کیا کرتی ہے | پیکٹ ٹائپ(س) | فکسچر | 8/8 |
|---|---|:-:|---|:-:|
| **پریزنس بیکن اور کوئری** | "میں یہاں ہوں" کا اعلان اور "آس پاس کون ہے؟" کا سوال — ایک **گھومتی، کی-مشتق عارضی ID** (آپ کی اصل شناخت نہیں) اور ایک موٹے geohash کے ذریعے | 21, 22 | `fixtures/presence/` | ✅ |
| **ہارٹ بیٹ** | منسلک ہم مرتبہ نوڈز کے درمیان ہلکا پھلکا لائیونیس کیپ-الائیو | 10 | `fixtures/heartbeat/` | ✅ |
| **پروفائل سنک** | میش کے ذریعے کسی ہم مرتبہ کے ساتھ ایک دستخط شدہ پروفائل کارڈ کا تبادلہ | 23 | `fixtures/profiles/` | ✅ |
| **عارضی-ID اعلان** | کسی دوست کو نجی طور پر اپنی موجودہ گھومتی روٹنگ ID بتانا تاکہ وہ گھومنے کے بعد بھی آپ تک پہنچ سکے | 56 | `fixtures/erid/` | ✅ |
| **پری-کی تبادلہ** | میش کے ذریعے ایک Signal پری-کی بنڈل کی درخواست اور فراہمی، تاکہ کسی ایسے شخص کے ساتھ اینڈ-ٹو-اینڈ سیشن شروع کیا جا سکے جس سے آپ کبھی نہیں ملے | 25, 26 | `fixtures/prekey/` | ✅ |
| **چینلز** | ایک نجی، صرف-اراکین والے گروپ چینل کو دستخط شدہ پیغامات | 7 | `fixtures/channels/` | ✅ |
| **پش-ٹو-ٹاک** | واکی-ٹاکی وائس فریمز (مبہم انکوڈ شدہ آڈیو پے لوڈ) | 15 | `fixtures/media/` | ✅ |
| **اسکرین شیئر** | اسکرین-شیئر ویڈیو فریمز (مبہم انکوڈ شدہ ویڈیو پے لوڈ) | 32 | `fixtures/media/` | ✅ |
| **کال کنٹرول** | وائس اور ویڈیو کالز کے لیے رنگ / قبول / رد / ہینگ-اپ سگنلنگ | 27 | `fixtures/videocall/` | ✅ |
| **SOS اعترافِ وصول** | بھیجنے والے کو تصدیق کہ اُس کا ہنگامی نشریہ موصول ہو گیا | 6 | `fixtures/sos/` | ✅ |
| **اسپیس بریڈکرمز** | "میرے آس پاس کیا ہے" پرت کے لیے مقام-ٹیگ شدہ دریافتی نشانات | 40 | `fixtures/space/` | ✅ |
| **فورج اعلان** | میش کو ایک مشتق/فورج شدہ مواد کے نمونے کی تشہیر | 41 | `fixtures/forge/` | ✅ |
| **والٹ شارڈ درخواست** | ایک اریژر-کوڈڈ اسٹوریج شارڈ حاصل کرنا (N میں سے کوئی بھی K شارڈز فائل کو دوبارہ بنا لیتے ہیں) | 42 | `fixtures/vaultshard/` | ✅ |
| **بینڈوتھ پیمائش** | لنک تھرو پٹ کی پروب / ایک / گاسپ کرنا تاکہ میش سب سے چوڑی پائپ (ABMF) کے ذریعے روٹ کرے | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

یہ پہلے سے مکمل **میسجنگ، 1-ٹو-1 اور گروپ وائس، ویڈیو کالز، لائیو اسٹریمنگ، واچ-ٹوگیدر، AODV روٹنگ، DTN اسٹور-اینڈ-فارورڈ، اور SOS فلڈ** سروسز کے اوپر بیٹھتی ہیں — جو تمام 8 زبانوں میں بھی نافذ ہیں۔

> **یہاں "بنایا گیا" کا دقیق مطلب کیا ہے۔** ہر سروس اپنا وائر پیکٹ تیار اور ہینڈل کرتی ہے، درست ایونٹس اٹھاتی ہے، اور ایک بائٹ-سطحی فکسچر سے منسلک ہے جس سے پوری زبانی خاندان کو مطابقت رکھنی ہوتی ہے۔ آپ کی ایپلی کیشن سروس کو اُس کے Signal سیشن، روٹنگ ٹیبل، اور مقامی حالت سے جوڑتی ہے۔ یہ پروٹوکول کی پرت ہے — کوڈ، ٹیسٹس، اور کراس-لینگویج بائٹ-فکسچرز میں ثابت شدہ — باقی ہر چیز کی طرح اُسی ایماندارانہ RF بنیاد پر: کوئی بھی راستہ جو بالآخر کسی ریڈیو پر سوار ہوتا ہے، وہ اُس ہارڈویئر برنگ-اپ تک میدانی طور پر غیر تصدیق شدہ رہتا ہے جس کا سراغ `OPEN_ISSUES.md` میں رکھا گیا ہے۔

## ٹرانسپورٹس

ہر ٹرانسپورٹ کا ایک رنگین نام ہے جو پورے کوڈ بیس میں استعمال ہوتا ہے۔ `IsAvailable` ہارڈویئر-مسدود راستوں کو گیٹ کرتا ہے — `TransportManager` انہیں چھوڑ دیتا ہے اور اگلے دستیاب ٹرانسپورٹ پر واپس آ جاتا ہے۔

**اسٹیٹس کلید:** ✅ اصل، بنایا اور تصدیق شدہ · ⏳ اصل، تصدیق جاری ہے · ⚠️ کچھ پلیٹ فارمز پر اصل، دوسروں پر اسٹب · ❌ اسٹب (ابھی کوئی ٹرانسپورٹ کوڈ نہیں)۔

| رنگ | نام | رینج | بینڈوتھ | اسٹیٹس |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 میٹر | 1 Mbps | ✅ اصل — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 میٹر | 250 Mbps | ✅ اصل — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC ریلے | لامحدود | ~10 Mbps | ✅ اصل — Windows؛ ریلے سرور `samples/AetherNet.RelayServer/` میں |
| 🟪 WebRTC P2P | انٹرنیٹ ڈیٹا چینل | لامحدود | ~100 Mbps | ✅ تمام 8 زبانوں میں اصل — **آٹھوں میں لوپ بیک-تصدیق شدہ** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust ہر ایک میں دو ہم مرتبہ ایک اصل ICE ڈیٹا چینل پر بائٹس کا تبادلہ کرتے ہیں) |
| ⚪ Aether White | NFC HCE | ~5 سینٹی میٹر | 848 kbps | ⚠️ Android پر اصل (`android/white/`)؛ Windows = اصل BLE-GATT + RSSI −40 dBm قربت کا تخمینہ (`WinNfcBleTransportService`، net9/10 پر کمپائل ہوتا ہے، رن ٹائم-غیر تصدیق شدہ) — `Windows.Networking.Proximity` کو Win 11 میں ہٹا دیا گیا |
| 🩵 Aether Teal | NearLink | ~600 میٹر | 12 Mbps | ⚠️ HarmonyOS پر اصل (`harmonyos/teal/`، `@kit.NearLinkKit` — آن-ڈیوائس تصدیق زیرِ التوا)؛ Android + Windows = اصل SSAP-over-BLE تخمینہ (`android/teal/AetherNetSleService`، `WinNearLinkBleTransportService`؛ کمپائل + یونٹ-ٹیسٹ تصدیق شدہ، رن ٹائم-غیر تصدیق شدہ) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 کلومیٹر | 37.5 kbps | ⚠️ اصل RYLR SX127x/SX126x سیریل ڈرائیور (`LoRaSerialTransport` C#/Go/Rust/C میں؛ کمپائل ہوتا ہے، رن ٹائم-غیر تصدیق شدہ — ایک فزیکل ماڈیول درکار ہے)؛ BLE Coded-PHY برج ابھی بھی ایک دستاویزی ڈیزائن ہے |

ریڈیو ٹرانسپورٹس صرف وہیں اصل ہیں جہاں پلیٹ فارم کوڈ موجود ہو (C#/Windows، Kotlin/Android، HarmonyOS)۔ آٹھوں زبانی لائبریریاں بصورتِ دیگر جانچ کے لیے ایک **اِن-پروسیس سمولیشن** ٹرانسپورٹ کے ساتھ آتی ہیں — **WebRTC ان سب کے لیے مشترکہ پہلا اصل ٹرانسپورٹ ہے** (مکمل؛ زبانوں کے آر پار لوپ بیک-تصدیق شدہ)۔

ترجیح پاور لاگت کے مطابق ہے: ریڈیو میش کو ترجیح دی جاتی ہے، پھر WebRTC ایک براہِ راست انٹرنیٹ راستے کے طور پر، جبکہ HTTP / QUIC ریلے آخری چارہ کے طور پر۔

## تعیناتی درجے

Aether کسی بھی ایسے پلیٹ فارم پر کام کرتا ہے جو بلوٹوتھ یا Wi-Fi کو سپورٹ کرتا ہو۔ آپ جس درجے پر ہیں اس کا انحصار اُس OS پر ہے جسے آپ ہدف بنا رہے ہیں۔

---

### معیاری درجہ — کوئی بھی پلیٹ فارم

Android · Windows · Linux · macOS · iOS

Aether کسی بھی ایسے آلے پر چلتا ہے جس میں بلوٹوتھ یا Wi-Fi ہارڈویئر ہو۔ جہاں کوئی ریڈیو فزیکل طور پر غائب ہو، ہر مسدود ٹرانسپورٹ کا تخمینہ اُس چیز سے لگایا جاتا ہے جو دستیاب ہو۔ یہ تخمینے اب **اصل کوڈ** ہیں (کمپائل-تصدیق شدہ؛ **رن ٹائم-غیر تصدیق شدہ**، ایک 2-آلہ / ہارڈویئر RF ٹیسٹ زیرِ التوا):

- **NearLink (Aether Teal)** — Android (`android/teal/AetherNetSleService`) اور Windows (`WinNearLinkBleTransportService`) پر اصل SSAP-over-BLE-GATT تخمینہ (Aether SLE UUID `61657468-6572-0003-…`)؛ کمپائل + یونٹ-ٹیسٹ تصدیق شدہ، رن ٹائم-غیر تصدیق شدہ۔ اصل NearLink ریڈیو صرف HarmonyOS پر موجود ہے (`harmonyos/teal/`، آن-ڈیوائس تصدیق زیرِ التوا)۔
- **LoRa (Aether Red)** — اصل RYLR SX127x/SX126x سیریل ڈرائیور (`LoRaSerialTransport` **تمام 8 زبانوں** میں — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin؛ ہر پورٹ کمپائل-تصدیق شدہ، بشمول Mac بلڈ سرور پر Swift + C؛ رن ٹائم-غیر تصدیق شدہ — ایک فزیکل ماڈیول درکار ہے)۔ Meshtastic-over-BLE-Coded-PHY برج (~1.3 کلومیٹر) ایک دستاویزی ڈیزائن رہتا ہے؛ اصل طویل-فاصلاتی LoRa کے لیے ایک LoRa-قابل نوڈ درکار ہے (گیٹ وے، SBC، یا LoRa ماڈیول والا مضبوط ہینڈسیٹ)۔
- **NFC (Aether White)** — Android پر اصل (HCE)۔ Windows میں اب ایک اصل BLE-GATT + RSSI −40 dBm قربت کا تخمینہ ہے (`WinNfcBleTransportService`، net9/10 پر کمپائل ہوتا ہے؛ رن ٹائم-غیر تصدیق شدہ)؛ جب کوئی ریڈر موجود ہو تو ACR122U PC/SC۔

جو ہر جگہ اصل اور ایک جیسا ہے: **BLE، Wi-Fi Direct، HTTP / QUIC ریلے، اور WebRTC P2P ٹرانسپورٹ (تمام 8 زبانوں میں لوپ بیک-تصدیق شدہ)**، نیز Signal پروٹوکول سیکیورٹی (X3DH + Double Ratchet)، AODV روٹنگ، DTN اسٹور-اینڈ-فارورڈ، SOS نشریہ، آواز، اور اسٹریمنگ۔

**ایماندار اسٹیٹس:** BLE + Wi-Fi Direct + ریلے پروڈکشن-اصل ہیں؛ **WebRTC P2P اصل اور تمام 8 زبانوں میں لوپ بیک-تصدیق شدہ ہے** (دو ہم مرتبہ ایک اصل ICE ڈیٹا چینل پر بائٹس کا تبادلہ کرتے ہیں — Rust کی `.201` Linux باکس پر کام کرتے UDP ICE کے ساتھ تصدیق ہوئی)؛ NearLink / LoRa / NFC-آن-Windows کے تخمینے اب اصل کوڈ ہیں جو کمپائل ہوتا ہے (LoRa آٹھوں میں کمپائل-تصدیق شدہ، بشمول Mac بلڈ سرور پر Swift + C؛ NearLink-Android کا یونٹ-ٹیسٹ بھی ہوا) مگر **رن ٹائم-غیر تصدیق شدہ** ہے — ابھی تک کوئی ہارڈویئر / 2-آلہ RF ٹیسٹ نہیں۔ وہ کوڈ میں میش میں شریک ہوتے ہیں؛ ان تینوں کو میدانی طور پر ثابت شدہ RF کی توقع کے ساتھ تعینات نہ کریں۔

---

### مقامی درجہ — CircleOS / OpenHarmony

CircleOS · HarmonyOS · کوئی بھی OpenHarmony-مبنی OS

CircleOS، OpenHarmony پر بنایا گیا ہے، جو NearLink (SLE) سلیکون اور `@kit.NearLinkKit` SDK کو ایک اول-درجہ OS صلاحیت کے طور پر شپ کرتا ہے۔ NearLink ہارڈویئر والے CircleOS اور HarmonyOS آلات پر، کسی تخمینے کی ضرورت نہیں — `harmonyos/teal/` اصل SLE ریڈیو کو براہِ راست استعمال کرتا ہے:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

یہ محض معیاری درجے کا ایک بہتر ورژن نہیں ہے۔ NearLink پرت پر یہ ایک بنیادی طور پر مختلف نیٹ ورک ہے:

| صلاحیت | معیاری درجہ (BLE تخمینہ) | مقامی درجہ (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink رینج** | ~100 میٹر (BLE) | **600 میٹر** |
| **NearLink بینڈوتھ** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink لیٹنسی** | ~10 ms (BLE) | **20 µs** |
| **NearLink پاور** | BLE بیس لائن | **BLE 5.0 سے 60% کم** |
| **بیک وقت NearLink ہم مرتبہ** | ~7 (BLE کنکشن حد) | **500+** |
| **NearLink ماخذ** | SSAP-over-BLE (`android/teal/`، `WinNearLinkStubTransportService`) | اصل SLE ریڈیو (`harmonyos/teal/`، `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP ریلے** | مقامی | مقامی (ایک جیسا) |
| **Signal پروٹوکول سیکیورٹی** | مکمل | مکمل (ایک جیسا) |
| **روٹنگ / DTN / SOS** | مکمل | مکمل (ایک جیسا) |
| **Aether Tag شناخت** | معاون | معاون (ایک جیسا) |

---

### درجوں کے درمیان منتقلی

کسی کوڈ تبدیلی کی ضرورت نہیں۔ درجہ رن ٹائم پر ہر ٹرانسپورٹ سروس پر `IsAvailable` کے ذریعے متعین ہوتا ہے:

1. NearLink سلیکون والے CircleOS یا HarmonyOS آلے پر، NearLink ٹرانسپورٹ پر `IsAvailable` `true` لوٹاتا ہے (اجازت-جانچ + غیر فعال اسکین کوشش کے ذریعے ہارڈویئر-پروب شدہ)۔
2. `TransportManager` خودکار طور پر NearLink کو ترجیحی مقام پر ترقی دیتا ہے — کم ترین پاور لاگت، بلند ترین بینڈوتھ۔
3. ایپ کوڈ، پیکٹ فارمیٹ، روٹنگ الگورتھم، سیکیورٹی پرت، اور Aether Tags دونوں درجوں میں ایک جیسے ہیں۔

معیاری درجے کا ایک نوڈ اور مقامی درجے کا ایک نوڈ آزادانہ طور پر بات چیت کر سکتے ہیں — وہ ایک ہی وائر فارمیٹ، ایک ہی Signal پروٹوکول سیشنز، اور ایک ہی Aether Tags شیئر کرتے ہیں۔ درجے کا فرق صرف NearLink پیکٹس کے لیے استعمال ہونے والے ریڈیو کو متاثر کرتا ہے، اس کے اوپر کے پروٹوکول کو نہیں۔

---

> **اندرونی طور پر ان درجوں کو Asterix ویریئنٹ (معیاری) اور Obelix ویریئنٹ (مقامی) کہا جاتا ہے۔** Asterix جو دستیاب ہو اس کے ساتھ اچھا کام کرتا ہے۔ Obelix — جو مقامی NearLink کے ساتھ CircleOS پر چلتا ہے — مستقل طور پر بلند صلاحیت پر کام کرتا ہے، جیسے Obelix دوبارہ پیے بغیر جادوئی معجون کی طاقت اپنے ساتھ لیے پھرتا ہے۔

---

## نفاذ

Aether کو 8 زبانوں میں بنایا گیا ہے تاکہ یہ فونز، لیپ ٹاپس، ٹیبلٹس، اور مائیکرو کنٹرولرز پر چلے۔ تمام نفاذ وائر-مطابق پیکٹس تیار کرتے ہیں — Rust نوڈ سے خفیہ کردہ ایک پیغام Python نوڈ سے ریلے کیا جا سکتا ہے اور Swift نوڈ سے ڈی کرپٹ کیا جا سکتا ہے۔

| زبان | ڈائریکٹری | وائر فارمیٹ | روٹنگ/DTN/SOS | X3DH | Double Ratchet | OPK پول | آواز/گروپ | اسٹریمنگ/ویڈیو/واچ |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

تمام 8 زبانیں بائٹ-یکساں وائر پیکٹس تیار کرتی ہیں، جن کی تصدیق CI میں چلنے والے 14 کینونیکل وائر-فارمیٹ فکسچرز اور 4 Signal ٹیسٹ ویکٹرز سے ہوتی ہے (`fixtures/expected/*.bin`، `fixtures/signal/expected/*.json`)۔ روٹنگ (AODV-طرز RREQ/RREP)، DTN اسٹور-اینڈ-فارورڈ، SOS نشریہ، آواز، اسٹریمنگ، اور سیکیورٹی-سختی سروسز ہر زبان میں **~3,000 ٹیسٹس** کے ساتھ تمام 8 نفاذ میں نافذ ہیں:

| زبان | ٹیسٹس | CI پلیٹ فارم |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **کل** | **~3,000** | |

کراس-لینگویج Signal انٹرآپ `fixtures/signal/` سے منسلک ہے، جس میں X3DH (`x3dh_basic`)، سمیٹرک ریچٹ (`ratchet_step_basic`، `ratchet_step_three_iterations`)، اور KDF_RK (`kdf_rk_basic`) کے لیے مشترکہ ٹیسٹ ویکٹرز ہیں۔ ہر نفاذ کو اُن فکسچرز کے مقابلے میں بائٹ-یکساں آؤٹ پٹ تیار کرنا ضروری ہے۔ تمام 8 زبانیں اب ایک مکمل Signal سیشن شپ کرتی ہیں (`generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt`)۔

وائر فارمیٹ اور Signal سے آگے، **پوری وائر-سروس سویٹ** — پریزنس، ہارٹ بیٹ، پروفائل سنک، عارضی-ID اعلان، پری-کی تبادلہ، چینلز، پش-ٹو-ٹاک، اسکرین شیئر، کال کنٹرول، SOS اعترافِ وصول، اسپیس بریڈکرمز، فورج اعلان، والٹ شارڈ درخواست، اور بینڈوتھ پیمائش (دیکھیں **آپ کو کیا ملتا ہے — ہر سروس، ہر زبان میں**) — اسی طرح تمام 8 زبانوں میں نافذ ہے اور اپنے اپنے فکسچرز (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/`، اور ان کے ہم پلہ) سے منسلک ہے۔ پروٹوکول کی پرت پر کوئی فیچر صرف-C# نہیں ہے۔

## فوری آغاز

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

ڈیمو آپ کو 8 مراحل سے گزارتا ہے: تین نوڈز (Alice، Bob، Charlie) کے لیے Ed25519 شناختی کیز تیار کرنا، Signal پروٹوکول سیشنز قائم کرنا، خفیہ پیغامات بھیجنا، Charlie کے ذریعے ایک پیغام ریلے کرنا (جو اسے پڑھ نہیں سکتا)، بائنری وائر فارمیٹ دکھانا، اور 5 لگاتار پیغامات پر فارورڈ سیکریسی کا مظاہرہ کرنا۔ آؤٹ پٹ رنگ-کوڈڈ ہے اور مراحل کے درمیان رُک جاتا ہے۔

**C# میں ایک پیغام بھیجیں:**

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

ڈیمو دو نوڈز کے لیے شناختی کیز تیار کرتا ہے، پری-کی بنڈلز کا تبادلہ کرتا ہے، خفیہ سیشنز قائم کرتا ہے، دونوں سمتوں میں خفیہ پیغامات بھیجتا ہے، میش پیکٹس بناتا اور دستخط کرتا ہے، دستخطوں کی توثیق کرتا ہے، اور پیکٹس کو بائنری وائر فارمیٹ میں سیریلائز کرتا ہے۔ یہ اِن-پروسیس ٹرانسپورٹ پرت کا بھی مظاہرہ کرتا ہے۔

**Rust میں ایک پیغام بھیجیں:**

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

ڈیمو ایک محاکاتی نیٹ ورک میں دو نوڈز بناتا ہے، Ed25519 کیز تیار کرتا ہے، Signal پروٹوکول سیشنز قائم کرتا ہے، ایک پیکٹ بناتا اور دستخط کرتا ہے، اسے C#-مطابق بائنری فارمیٹ میں سیریلائز کرتا ہے، ایک خفیہ پیغام کو خفیہ کرتا ہے، اسے دوسرے نوڈ پر ڈی کرپٹ کرتا ہے، اسے ٹرانسپورٹ کے ذریعے بھیجتا ہے، اور راؤنڈ-ٹرپ کی توثیق کرتا ہے۔

**TypeScript میں ایک پیغام بھیجیں:**

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

ڈیمو 8 مظاہرے چلاتا ہے: Ed25519 کی-جنریشن اور چھیڑ چھاڑ کا پتہ لگانا، صلاحیتوں کے ساتھ نوڈ بنانا، Signal پروٹوکول X3DH کی-تبادلہ، AES-256-GCM خفیہ کاری اور ڈی کرپشن، پیکٹ سیریلائزیشن، ری پلے کے پتے کے ساتھ پیکٹ دستخط، اِن-پروسیس ٹرانسپورٹ، اور تمام پرتوں کو یکجا کرتا ایک مکمل اینڈ-ٹو-اینڈ فلو۔

**Python میں ایک پیغام بھیجیں:**

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

ڈیمو 5 مظاہرے چلاتا ہے: پیکٹ سیریلائزیشن راؤنڈ-ٹرپس، چھیڑ چھاڑ کے پتے کے ساتھ Ed25519 دستخط، دونوں سمتوں میں خفیہ میسجنگ کے ساتھ Signal پروٹوکول سیشن قائم کرنا، دو ہم مرتبہ کے درمیان اِن-پروسیس ٹرانسپورٹ، اور ری پلے تحفظ کے لیے nonce ڈی ڈپلی کیشن۔

**Go میں ایک پیغام بھیجیں:**

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

ڈیمو 11 مراحل سے گزرتا ہے: کی-جنریشن، صلاحیتوں کے ساتھ نوڈ بنانا، Signal پروٹوکول ابتدائیہ، پری-کی بنڈل تبادلہ، سیشن قائم کرنا، پیکٹ بنانا اور دستخط، سیریلائزیشن، دستخط کی توثیق کے ساتھ ڈی سیریلائزیشن، کی-ریچٹنگ کے ساتھ اینڈ-ٹو-اینڈ خفیہ کاری، ری پلے حملے کا پتہ لگانا، اور اِن-پروسیس ٹرانسپورٹ۔

**Kotlin میں ایک پیغام بھیجیں:**

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

ڈیمو 5 ٹیسٹ چلاتا ہے: پیکٹ سیریلائزیشن راؤنڈ-ٹرپس، چھیڑ چھاڑ کے رد کے ساتھ Ed25519 دستخط، AES-256-GCM خفیہ کاری کے ساتھ Signal پروٹوکول سیشن قائم کرنا، اِن-پروسیس ٹرانسپورٹ پیغام کی ترسیل، اور ایک مکمل اینڈ-ٹو-اینڈ فلو جہاں Alice ایک پیکٹ دستخط کرتی ہے اور Bob ٹرانسپورٹ کے بعد اس کی توثیق کرتا ہے۔

**Swift میں ایک پیغام بھیجیں:**

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

ڈیمو 7 مظاہرے چلاتا ہے: Ed25519 کی-جنریشن، پیکٹ بنانا اور دستخط، بائنری وائر فارمیٹ میں سیریلائزیشن، سالمیت جانچ کے ساتھ ڈی سیریلائزیشن، AES-256-GCM خفیہ کاری اور ڈی کرپشن، HMAC-SHA256 پیغام تصدیق، اور HKDF-SHA256 کی-اخذ۔

**C میں ایک پیغام بھیجیں:**

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

## روڈ میپ

جو بنایا جا چکا ہے اور جو آگے آنا ہے۔

**مکمل (کراس-لینگویج تصدیق شدہ، تمام 8 نفاذ):**
- وائر فارمیٹ: 8 زبانوں کے آر پار بائٹ-یکساں، 14 کینونیکل فکسچرز اور CI میں کراس-لینگویج تصدیقات سے منسلک (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9-جاب میٹرکس (C#/.NET 10، Go 1.22، TypeScript/Node 20، Python 3.12، Kotlin/JVM 21، Swift/macOS-14، Rust stable، C/GCC، نیز فکسچر سالمیت جاب) `.github/workflows/ci.yml` میں۔
- Ed25519 پیکٹ دستخط اور توثیق
- AES-256-GCM خفیہ کاری
- HKDF / HMAC کی-اخذ کے بنیادی اجزا
- پیکٹ سیریلائزیشن + دستخط لے آؤٹ (LE + 4-بائٹ int32 فیلڈز)
- اِن-پروسیس ٹرانسپورٹ سمولیٹر (ترقی اور جانچ کے لیے)
- RREQ/RREP، دستخط شدہ روٹ رپلائیز، ڈی ڈپ، TTL فارورڈنگ کے ساتھ AODV-متاثر روٹنگ سروس
- کسٹڈی ٹرانسفر، geohash-آگاہ ری پلی کیشن، 72 گھنٹے TTL کے ساتھ DTN اسٹور-اینڈ-فارورڈ سروس
- فلڈ، ڈی ڈپ، سیلف-اوریجن گارڈ، ریٹ-لمٹ (3/گھنٹہ) کے ساتھ SOS نشریہ سروس
- توسیع پذیری کے جوڑ: `IncentiveProvider`، `BackendClient`، `FeatureFlagProvider` (Noop طے شدہ)
- **~3,000 ٹیسٹس** تمام 8 زبانوں کے آر پار (C# 530، TypeScript 459، Kotlin 457، Go 423، Python 387، Swift 295، C 253، Rust ~195) — سب CI میں ہرے
- ✅ **اصل X3DH عارضی کی (8 زبانیں)** — 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) HKDF-SHA256 روٹ-اخذ کے ساتھ۔ `fixtures/signal/expected/x3dh_basic.json` سے منسلک۔
- ✅ **Double Ratchet ہم آہنگی خاندان بھر میں** — مکمل Signal §5، سمیٹرک ریچٹ میں HMAC-SHA256 + 0x01/0x02 ڈومین علیحدگی کے ساتھ، DH-ریچٹ مرحلے میں HKDF-SHA256 KDF_RK، وصولی پر DH-روٹیشن۔ `ratchet_step_basic`، `ratchet_step_three_iterations`، `kdf_rk_basic` فکسچرز سے تصدیق شدہ۔
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 HEAD کے ساتھ ہم آہنگ** — دیکھیں `docs/PROTOCOL_SPEC.md`۔

**مکمل (تمام 8 زبانیں):**
- ✅ **وائس کالز (1-to-1)** — سگنلنگ اسٹیٹ مشین (Offer/Answer/Hangup/Cancel/Timeout) + بائنری فریم ٹرانسپورٹ (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes)۔ `IRoutingService` کے ذریعے روٹ-آگاہ ترسیل۔
- ✅ **گروپ وائس** — میزبان-چلائی گئی رکنیت (invite/kick/leave)، فی-فریم کی-جنریشن فیلڈ، تمام موجودہ اراکین کو یونی کاسٹ فین-آؤٹ، رکنیت تبدیلی پر میزبان-کنٹرول شدہ کی-روٹیشن۔
- ✅ **لائیو اسٹریمنگ** — پبلشر `StreamAnnounce` نشر کرتا ہے؛ سبسکرائبرز `StreamSubscribe` بھیجتے ہیں؛ بائنری `StreamSegment` فریمز (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) ہر سبسکرائبر کو یونی کاسٹ۔
- ✅ **ویڈیو کالز (1-to-1)** — سگنلنگ میں کوڈیک/ریزولوشن/fps/بٹ ریٹ نیگوشی ایشن، کی فریم-درخواست اور کوالٹی-تبدیلی سگنلز، وائس لے آؤٹ سے مطابق بائنری `VideoFrame` فارمیٹ۔
- ✅ **واچ ٹوگیدر** — میزبان مستند `WatchSync` (play/pause/seek/speed) کمانڈز جاری کرتا ہے؛ فالوورز RTT معاوضے کے ساتھ لاگو کرتے ہیں (`position = positionMs + elapsed × playbackSpeed`)؛ فائر-اینڈ-فارگیٹ `WatchReaction`۔
- ✅ **ون-ٹائم پری-کی (OPK) پول** — طے شدہ 100، FIFO اجرا، سست ٹاپ-اپ، تمام 8 زبانوں میں لاک-محفوظ کھپت۔ سنگل-OPK کنکرنسی خطرے کو بند کرتا ہے۔
- ✅ **C: مکمل Signal سیشن** — `c/src/signal_protocol.c` میں `aethernet_signal_service_init`، `generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt`؛ `c/tests/test_signal_session.c` میں 6 دو-نوڈ E2E ٹیسٹس۔ تمام 8 زبانوں میں اب مکمل سیشن-قابل Signal پروٹوکول ہے۔

**مکمل (تمام 8 زبانیں — مکمل وائر-سروس سویٹ):**
- ✅ **ہر مختص پیکٹ ٹائپ اب تمام 8 زبانوں میں ایک اصل، بائٹ-یکساں سروس ہے۔** پریزنس بیکن/کوئری (21/22)، ہارٹ بیٹ (10)، پروفائل سنک (23)، عارضی-روٹنگ-ID اعلان (56)، پری-کی تبادلہ (25/26)، چینلز (7)، پش-ٹو-ٹاک (15)، اسکرین شیئر (32)، کال کنٹرول (27)، SOS اعترافِ وصول (6)، اسپیس بریڈکرمز (40)، فورج اعلان (41)، والٹ شارڈ درخواست (42)، اور بینڈوتھ پیمائش / ABMF (53/54/55)۔ ہر ایک ایک پتلی سروس ہے (تیار + ہینڈل + ایونٹ) جسے میزبان اپنے Signal سیشن اور روٹنگ ٹیبل سے جوڑتا ہے؛ ہر ایک ایک مشترکہ کراس-لینگویج فکسچر (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/`، `fixtures/channels/`، `fixtures/profiles/`، `fixtures/heartbeat/`، `fixtures/erid/`، `fixtures/space/`، `fixtures/forge/`، `fixtures/sos/`) سے منسلک ہے اور فی-زبان یونٹ ٹیسٹس سے آزمائی جاتی ہے، جبکہ Swift اور C کی تصدیق macOS بلڈ سرور پر ہوتی ہے۔ دیکھیں **آپ کو کیا ملتا ہے — ہر سروس، ہر زبان میں**۔

**مکمل (صرف C# ریفرنس):**
- ✅ **ڈیمو مرحلہ 9 — MessagingService + DTN فال بیک اینڈ-ٹو-اینڈ** — `samples/AetherNet.Demo.Console` اصل-Signal-خفیہ میسجنگ کے ساتھ اُس وقت DTN اسٹور-اینڈ-فارورڈ سے گزرتا ہے جب وصول کنندہ آف لائن ہو۔
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` برج** — `SignalMessageEnvelopeCipher` میسجنگ پرت کو طے شدہ طور پر اینڈ-ٹو-اینڈ خفیہ بناتا ہے؛ بغیر Signal سیشن کے پیغامات قطار میں لگا دیے جاتے ہیں، کبھی غیر محفوظ طریقے سے نہیں بھیجے جاتے۔
- ✅ **اڈاپٹو بٹ ریٹ اسٹریمنگ** — `AdaptiveBitrateController` پروفائل A (حقیقی وقت)، B (لائیو نشریہ)، اور C (VOD) کے لیے اسپیک-لازمی بٹ ریٹ سیڑھیوں کے ساتھ۔ پبلشر بلند ترین قابلِ برداشت پایہ (20% ہیڈ روم) منتخب کرتا ہے اور فرش سے نیچے ہونے پر ایک شارڈ کے بجائے `StreamAbandon` (`PacketType.StreamAbandon`) جاری کرتا ہے۔ `IStreamingService`، `UpdateBandwidthEstimate` اور `GetCurrentBitrateRung` کو ظاہر کرتا ہے۔
- ✅ **واچ ٹوگیدر: BitTorrent انگیسٹ + ChipIn گروپ فنڈنگ** — `TorrentInfo` / `TorrentFile` ماڈلز؛ `WatchTogetherService`، `PacketType.TorrentMetadata` کو ہینڈل کرتا ہے اور `TorrentReceived` جاری کرتا ہے۔ `ChipInPool` / `ChipInContribution` اسٹیٹ مشین (Collecting → Funded → Purchasing → Acquired / Failed / Refunded)؛ `IWatchTogetherService` پر `StartChipInAsync` / `ContributeAsync` / `GetChipIn`۔
- ✅ **خودکار SFU ریلے کے ساتھ گروپ ویڈیو کالز** — `GroupVideoService` / `IGroupVideoService`۔ ≤ 3 شرکا کے لیے FullMesh ٹوپولوجی؛ `SfuThresholdParticipants` (4) پر خودکار طور پر SFU میں تبدیلی، `GroupVideoSignaling(SfuAssigned)` کے ذریعے ریلے دوبارہ-تفویض کے ساتھ۔ FullMesh میں فین-آؤٹ، SFU موڈ میں صرف-ریلے بھیجنا۔ سگنلنگ پیکٹ ٹائپ `GroupVideoSignaling = 35`۔
- ✅ **BLE GATT ٹرانسپورٹ سمولیشن** — `SimulatedBleGattTransportService` (`IBleTransportService`)۔ `BleGattFramer` کے ذریعے GATT MTU فریمنگ (1024 B/فریم، `[2B count][2B index][payload]`)، اِن-پروسیس جامد ہم مرتبہ رجسٹری، اشتہار نشریہ۔ تمام `BleMaxPayloadBytes` پابندیاں نافذ۔
- ✅ **Wi-Fi Direct ٹرانسپورٹ سمولیشن** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`)۔ واضح `ConnectAsync`/`DisconnectAsync` لائف سائیکل، براہِ راست بڑی-پے لوڈ ترسیل (بغیر فریمنگ)، دو طرفہ `PeerConnected`/`PeerDisconnected` ایونٹس۔
- ✅ **NearLink ٹرانسپورٹ سمولیشن** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`)۔ 4096 B فریم MTU، 500-ہم مرتبہ رجسٹری، `ConnectedPeerCount`، `IsAvailable` رن ٹائم پر قابلِ ترتیب۔
- ✅ **RF برنگ-اپ سمولیشن ٹیسٹس** — دو-نوڈ انٹرآپ ٹیسٹس (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` راؤنڈ-ٹرپ، WiFi Direct 64 KB پے لوڈ ٹرانسفر۔ سافٹ ویئر پرت مکمل طور پر تصدیق شدہ؛ آن-ہارڈویئر توثیق کے لیے فزیکل ڈیوائس لیب سیشن درکار۔

**مکمل (C# ٹرانسپورٹ پرت — سب fail-fast):**
- ✅ **BLE GATT اصل ٹرانسپورٹ** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server)۔ `samples/AetherNet.BleRfTest/` میں مکمل RF برنگ-اپ ٹیسٹ۔
- ✅ **Wi-Fi Direct اصل ٹرانسپورٹ** — `WinWifiDirectTransportService` (WinRT، `WiFiDirectAdvertisementPublisher` + TCP StreamSocket پورٹ 8888) + `android/green/` (`WifiP2pManager`)۔ `samples/AetherNet.WifiDirectRfTest/` میں RF ٹیسٹ۔
- ✅ **HTTP ریلے ٹرانسپورٹ (Aether Purple)** — `HttpRelayTransportService` 10-سیکنڈ لانگ-پول کے ساتھ، `PowerCostRelative = 100`، ہمیشہ آخری چارہ۔ ریلے سرور `samples/AetherNet.RelayServer/` میں (ASP.NET Core minimal API، پورٹ 5200)۔ `samples/AetherNet.RelayRfTest/` میں RF ٹیسٹ۔
- ✅ **NFC (Aether White)** — `android/white/`، AID `F061657468657200` کے ساتھ `HostApduService` نافذ کرتا ہے۔ `WinNfcStubTransportService` دو Windows تخمینہ راستے دستاویز کرتا ہے: (1) RSSI گیٹ ≥ −40 dBm کے ساتھ NDEF-over-BLE-GATT (NFC سلیکون کے بغیر ٹیپ-ٹو-کنیکٹ کی محاکات کرتا ہے، `IsAvailable = Bluetooth present`)؛ (2) `Windows.Devices.SmartCards` PC/SC کے ذریعے ACR122U USB ریڈر (`IsAvailable = contactless reader enumerated`)۔ اپ گریڈ راستہ: جب Microsoft ایک اول-فریق P2P NFC API شپ کرے تو `ITransportService` نافذ کریں۔
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`) استعمال کرتے ہوئے مکمل HarmonyOS 5.0.1 (API 13) ArkTS نفاذ؛ `isAvailable` رن ٹائم پر پروب شدہ۔ `WinNearLinkStubTransportService` + `android/teal/`، SSAP-over-BLE تخمینہ دستاویز کرتے ہیں: Aether SLE سروس UUID `61657468-6572-0003-0000-000000000000` کے ساتھ BLE GATT — SSAP سے API-مماثل، اصل NearLink ہارڈویئر کے ساتھ وائر-مطابق نہیں۔ اپ گریڈ راستہ: BLE GATT کالز کو `ssapc_*`/`ssaps_*` SDK کالز سے بدلیں؛ UUIDs اور `TransportManager` سلاٹ بلا تبدیلی۔
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/`، Meshtastic-over-BLE-LR تخمینہ دستاویز کرتے ہیں: BLE 5.0 Coded PHY S=8 (~1.3 کلومیٹر بیرونی) پر مکمل Meshtastic وائر فارمیٹ (16-بائٹ ہیڈر + AES-256-CTR protobuf)، منظم-فلڈ روٹنگ اور RSSI-وزنی تنازع ونڈو کے ساتھ۔ اصل LoRa ہارڈویئر کے ساتھ برج-نوڈ فیڈریشن خودکار طور پر کام کرتا ہے (وہی Meshtastic پیکٹ فارمیٹ، بغیر ترجمہ)۔ اپ گریڈ راستہ: BLE LR ریڈیو کو SX1276/SX1278 AT-کمانڈ یا SPI ڈرائیور سے بدلیں؛ پیکٹ فارمیٹ اور روٹنگ بلا تبدیلی۔

**کھلا — `OPEN_ISSUES.md` میں سراغ شدہ:**
- اصل ہارڈویئر پر RF برنگ-اپ: فزیکل BLE / Wi-Fi Direct آلات پر اینڈ-ٹو-اینڈ دو-نوڈ انٹرآپ ٹیسٹ (سمولیشن ٹیسٹس پاس؛ ہارڈویئر لیب سیشن درکار)
- NearLink: `harmonyos/teal/` مکمل؛ Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 ہارڈویئر درکار ہے (NearLink سلیکون غیر-Huawei آلات پر موجود نہیں)۔ Windows + Android خودکار طور پر SSAP-over-BLE تخمینے پر واپس آ جاتے ہیں۔
- LoRa / CircleLink: حقیقی LoRa رینج کے لیے ایک ریڈیو ماڈیول درکار ہے۔ اس کے بغیر، Meshtastic وائر فارمیٹ BLE LR (~1.3 کلومیٹر) پر لے جایا جاتا ہے اور اصل LoRa ہارڈویئر کے ساتھ برج-نوڈ فیڈریشن دستیاب ہے۔
- ✅ **(حل شدہ v1.2.0)** صارف پروٹوکول سطح (ویو 16/17) — آنے والے بنڈلز کے لیے `IDtnService.BundleReceived` ایونٹ ([#59](https://github.com/bhengubv/aether-protocol/issues/59))، ایپلی کیشن-پرت نامگذاری/دریافت ڈائریکٹری ([#60](https://github.com/bhengubv/aether-protocol/issues/60))، مصنف-ٹپنگ انٹرفیس ([#61](https://github.com/bhengubv/aether-protocol/issues/61))۔ تینوں بائٹ-برابر کراس-لینگویج فکسچرز کے ساتھ 8 زبانوں کے آر پار اضافی طور پر شپ ہوئے۔ دیکھیں CHANGELOG۔

**ابھی بیرونی شراکت کے لیے کھلا نہیں:**
- پروٹوکول ابھی فعال ترقی کے تحت ہے۔ اس وقت بیرونی شراکتیں قبول نہیں کی جا رہیں۔
- NearLink ٹرانسپورٹ نفاذ، Android/iOS انضمام کی مثالیں، اضافی ٹرانسپورٹ بیک اینڈز، کارکردگی بینچ مارکس، اور پروٹوکول فزنگ اندرونی طور پر سراغ شدہ ہیں اور جب پروجیکٹ ایک مستحکم عوامی شراکت نقطے پر پہنچے گا تو کھول دیے جائیں گے۔

## پروجیکٹ کا ڈھانچہ

```
aether-protocol/
  src/
    AetherNet.Core/          Protocol models, constants, packet serialization
    AetherNet.Security/      Signal Protocol, Ed25519, packet signing
    AetherNet.Transport/     Transport abstractions, NearLink, in-process simulator
    AetherNet.Messaging/     Message handling and relay
    AetherNet.Storage/       DTN store-and-forward persistence
    AetherNet.Streaming/     Adaptive bitrate streaming, video models and interfaces
    AetherNet.Voice/         Voice calls and group voice
    AetherNet.Content/       Content verification and chunked transfer
  samples/
    AetherNet.Demo.Console/  Interactive demo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust implementation
  typescript/             TypeScript implementation
  python/                 Python implementation
  go/                     Go implementation
  kotlin/                 Kotlin/JVM implementation
  swift/                  Swift implementation
  c/                      C implementation
  docs/
    PROTOCOL_SPEC.md      RFC-style protocol specification
```

## نیا ٹرانسپورٹ شامل کرنا

`ITransportService` نافذ کریں:

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

اسے DI میں رجسٹر کریں اور `TransportManager` خودکار طور پر اسے ٹرانسپورٹ کے انتخاب میں شامل کر لے گا، پاور لاگت کے مطابق ترتیب دے کر۔

## یہ کیسے موازنہ کرتا ہے

| پروٹوکول | حد | Aether کی برتری |
|----------|-----------|-----------------|
| **Briar** | صرف-Android، Tor پر منحصر | کراس-پلیٹ فارم، خالص میش |
| **Meshtastic** | صرف LoRa (زیادہ سے زیادہ 30 kbps) | ملٹی-ٹرانسپورٹ (BLE + WiFi + NearLink)، آواز اور اسٹریمنگ کے قابل |
| **Reticulum** | Python، چھوٹی کمیونٹی | 8 زبانیں، ان سب کے آر پار وائر-مطابق |
| **libp2p** | انٹرنیٹ بیک بون فرض کرتا ہے | آف لائن-فرسٹ، صفر ڈھانچے کے ساتھ کام کرتا ہے |
| **Yggdrasil** | اوورلے نیٹ ورک، انٹرنیٹ درکار | فزیکل-پرت میش، بغیر انٹرنیٹ کام کرتا ہے |
| **Signal** | کوئی میش نہیں، انٹرنیٹ درکار | آف لائن کام کرتا ہے، P2P، میش ریلے، وہی E2E خفیہ کاری |

## توسیعی نقاط

پروٹوکول تنہا کام کرتا ہے۔ یہ انٹرفیسز آپ کو اپنا بیک اینڈ لگانے دیتے ہیں اگر آپ کو کسی کی ضرورت ہو:

- `IAetherNetIncentiveProvider` — ٹریفک ریلے کرنے والے نوڈز کو انعام دیں (no-op طے شدہ: ایثاری ریلے)
- `IAetherNetBackendClient` — جب انٹرنیٹ دستیاب ہو تو کسی سرور کے ساتھ سنک کریں (no-op طے شدہ: مکمل طور پر آف لائن)
- `IAetherNetFeatureFlagProvider` — رن ٹائم پر پروٹوکول فیچرز کو ٹوگل کریں (no-op طے شدہ: سب کچھ فعال)

تینوں no-op نفاذ کے ساتھ آتے ہیں۔ انہیں ہٹا دیں اور کچھ نہیں ٹوٹے گا۔

## شراکت

بیرونی شراکتیں ابھی کھلی نہیں ہیں۔ پروجیکٹ ابھی فعال ترقی کے تحت ہے۔ جب ہم ایک عوامی شراکت ونڈو کا اعلان کریں تو دوبارہ چیک کریں۔

## سیکیورٹی

ذمہ دار افشا پالیسی کے لیے [SECURITY.md](SECURITY.md) دیکھیں۔

## لائسنس

MIT لائسنس۔ دیکھیں [LICENSE](LICENSE)۔

## ترجمے

یہ README اس فائل کے اوپر موجود لینگویج بار میں درج دیگر زبانوں میں بھی [`docs/i18n/`](docs/i18n/) کے تحت برقرار رکھا جاتا ہے — جو یورپی، مشرقی ایشیائی، مشرق وسطیٰ، جنوبی ایشیائی، جنوب مشرقی ایشیائی، اور افریقی زبانوں پر محیط ہے، کیونکہ ایسے لوگوں کے لیے بنائے گئے نیٹ ورک کا، جن کے پاس ڈیٹا نہیں، ایسا دروازہ نہیں ہونا چاہیے جسے صرف اچھی طرح جڑے ہوئے لوگ ہی پڑھ سکیں۔ **انگریزی ورژن ہی مصدرِ حقیقت ہے**: جہاں کوئی ترجمہ اور انگریزی متن اختلاف کریں، وہاں انگریزی متن مستند ہے، اور ترجمے اس سے ایک یا دو ریلیز پیچھے رہ سکتے ہیں۔ بیان کردہ پروٹوکول، کوڈ، فکسچرز، اور رویہ ایک جیسے ہیں چاہے آپ کوئی بھی زبان پڑھیں۔

</div>
