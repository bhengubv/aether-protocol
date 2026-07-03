# AetherNet — بروتوكول الشبكات المتشابكة الذي يعمل دون اتصال أولاً

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

<div dir="rtl">

**AetherNet هو بروتوكول شبكات متشابكة مفتوح المصدر ومرخّص برخصة MIT** لإرسال الرسائل والملفات والصوت والفيديو إلى الأشخاص القريبين منك — **دون إنترنت، ودون خوادم، ودون تسجيل**. تتصل الأجهزة مباشرة عبر البلوتوث، وWi-Fi Direct، وNearLink، وLoRa؛ وعندما يكون المستلم خارج النطاق، تقفز الرسائل عبر أجهزة أخرى وتنتظر ما يصل إلى 72 ساعة للعثور على مسار. يُشحن بـ**تطبيقات متطابقة بايتاً ببايت في ثماني لغات برمجة** — C# وRust وTypeScript وPython وGo وKotlin وSwift وC.

شارك الملفات والرسائل والبثوث مع الأشخاص القريبين منك. دون WiFi. دون بيانات موبايل. دون تسجيل. مثل AirDrop، إلا أنه يعمل مع الجميع، على جميع المنصات.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **بروتوكول واحد، ثماني لغات، متطابق على الأسلاك.** Aether مُطبَّق بـ**C# وRust وTypeScript وPython وGo وKotlin وSwift وC** — وكل حزمة متطابقة بالبايت عبرها جميعاً، مفروضٌ ذلك بمستودع مشترك للاختبارات متعددة اللغات في CI. ابنِ عقدتك بأيٍّ من الثماني؛ فإنها تتشغّل بينياً مع جميع الأخريات. هذا الملف التعريفي متوفر أيضاً بـ11 لغة بشرية (الروابط أعلاه).

## ماذا يمكنك أن تفعل به؟

**شارك ملاحظات المحاضرات دون استهلاك بيانات.**

أنت في مجموعة دراسية. لدى أحدهم أوراق اختبارات سابقة على هاتفه. يرسلها Aether مباشرة إلى جهازك عبر البلوتوث — دون نقطة اتصال، ودون مجموعة واتساب، ودون حد لحجم الملفات. إذا كان أحد أفراد المجموعة خارج النطاق، ينتقل الملف عبر الأجهزة الأخرى حتى يصل إليه. تنتظر الرسائل ما يصل إلى 72 ساعة إذا لزم الأمر للعثور على مسار.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**اكتشف ما يجري من حولك.**

أنت في فعالية جامعية أو مهرجان. يكتشف Aether الأجهزة القريبة عبر البلوتوث وWiFi Direct — دون خلاصة تطبيق، ودون خوارزمية. ترى ما يحدث فعلاً من حولك، لا ما يُروَّج له.

**أرسل نداء استغاثة عندما لا يوجد إشارة.**

هاتفك بلا تغطية. يبث Aether رسالة طوارئ لكل جهاز في النطاق، وتلك الأجهزة تمررها. لا حاجة لبرج خلوي.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**أنشئ قنوات مجموعات خاصة.**

قناة لطابق مبيت، لجمعيتك، لفريق مشروعك. المشاركون المعتمدون فقط يمكنهم القراءة والإرسال. لا خادم يخزن المحادثة.

**بع أشياء للأشخاص القريبين منك.**

أدرج كتاباً مدرسياً للبيع. الأشخاص المارون في نطاق الشبكة يرونه. دون حساب سوق، دون رسوم إدراج — مجرد قرب.

**شاهد فيلماً معاً عبر الشبكة.**

مجموعتك تحيي ليلة سينمائية. يملك أحدهم الملف. يزامن Aether التشغيل عبر كل الأجهزة — تشغيل، إيقاف مؤقت، بحث — كل ذلك بتزامن تام. إذا لم يكن لدى البعض الملف، تُوزعه الشبكة في الوقت الفعلي كبث P2P. يساهم الجميع عبر SDPKT لشرائه إذا لم يكن لدى أحد.

## كيف يعمل

تتحدث الأجهزة مباشرة مع بعضها باستخدام البلوتوث أو WiFi Direct أو NearLink. لا اتصال بالإنترنت، لا خادم، لا بنية تحتية مركزية.

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

عندما لا تستطيع رسالة ما الوصول إلى وجهتها مباشرة، تقفز عبر أجهزة أخرى. تلك الأجهزة الوسيطة لا تستطيع قراءة ما تحمله — كل رسالة مشفرة بـAES-256-GCM. كل حزمة موقَّعة بمفاتيح هوية Ed25519، والحزم المزيفة يسقطها الاتصال الشبكي.

> **ملاحظة حول نضج الأمان (اقرأ قبل الشحن):** تم تطبيق X3DH الحقيقي (4 عمليات DH بـX25519)، وDouble Ratchet الكامل لبروتوكول Signal (خطوة تدوير DH عند الاستلام، KDF_RK، تشعبات السلسلة 0x01/0x02)، ومجموعة المفاتيح أحادية الاستخدام (100 OPK افتراضياً، FIFO، محمية بالقفل) في **جميع اللغات الثماني** ومثبتة في مستودع مشترك للاختبارات متعددة اللغات تحت `fixtures/signal/`. العنصر الوحيد المتبقي هو تشغيل RF المادي على أجهزة BLE حقيقية (متتبع في `OPEN_ISSUES.md`).

لا حسابات، لا أرقام هاتف، لا بريد إلكتروني. تنشئ زوج مفاتيح وتكون على الشبكة.

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

**التوجيه** — AODV مع ردود مسار موقَّعة. كل رد مسار موقَّع بمفتاح Ed25519 للوجهة، لذا لا يستطيع أي جهاز التظاهر بأنه وجهة لا يمثلها.

**التخزين والإعادة** — عندما لا يوجد مسار مباشر، تُحتفظ بالحزم لمدة تصل إلى 72 ساعة حتى يُفتح مسار.

**اختيار وسيلة النقل** — يختار البروتوكول وسيلة النقل المناسبة لكل حزمة. رسائل التحكم الصغيرة تسير عبر BLE. نقل البيانات الكبيرة يستخدم WiFi Direct. NearLink عند توفره.

**الصوت والفيديو والبث** — مكالمات فيديو مع التفاوض على الترميز (H.264/H.265/VP8)، واختيار جودة يراعي وسيلة النقل، وفيديو جماعي مع ترحيل SFU تلقائي، ومشاهدة مشتركة مع تعويض RTT، وبث تكيفي متغير معدل البت.

**حماية إعادة التشغيل** — إزالة تكرار الـnonce مع نافذة حداثة زمنية مدتها 5 دقائق.

## ما الذي تحصل عليه — كل خدمة، بكل لغة

Aether ليس مجرد وسيلة نقل. كل نوع حزمة يحجزه البروتوكول أصبح الآن **خدمة حقيقية وعاملة في جميع اللغات الثماني**، وكل واحدة تُسلسل إلى **حزم أسلاك متطابقة بالبايت** — حزمة تبنيها عقدة Go تفكها، دون تغيير، عقدة Swift أو Rust أو C أو Python أو TypeScript أو Kotlin أو C#. كل خدمة مثبتة في مستودع مشترك متعدد اللغات تحت `fixtures/<service>/` وتُختبر باختبارات وحدة لكل لغة، مع تحقق إضافي من Swift وC على خادم بناء macOS.

| القدرة | ما تفعله | نوع (أنواع) الحزمة | المستودع | 8/8 |
|---|---|:-:|---|:-:|
| **منارة الحضور والاستعلام** | إعلان "أنا هنا" والسؤال "من حولي؟" — عبر **معرف مؤقت متبدّل مشتق من مفتاح** (وليس هويتك الحقيقية) بالإضافة إلى geohash خشن | 21, 22 | `fixtures/presence/` | ✅ |
| **نبضة القلب** | إبقاء الحياة خفيف الوزن بين الأقران المرتبطين | 10 | `fixtures/heartbeat/` | ✅ |
| **مزامنة الملف الشخصي** | تبادل بطاقة ملف شخصي موقَّعة مع قرين عبر الشبكة | 23 | `fixtures/profiles/` | ✅ |
| **إعلان المعرف المؤقت** | إخبار صديق سراً بمعرف التوجيه المتبدّل الحالي كي يظل قادراً على الوصول إليك بعد تبدّله | 56 | `fixtures/erid/` | ✅ |
| **تبادل المفاتيح الأولية** | طلب وتسليم حزمة مفاتيح Signal الأولية عبر الشبكة، لبدء جلسة من طرف إلى طرف مع شخص لم تلتقِه قط | 25, 26 | `fixtures/prekey/` | ✅ |
| **القنوات** | رسائل موقَّعة إلى قناة مجموعة خاصة، للأعضاء فقط | 7 | `fixtures/channels/` | ✅ |
| **الضغط للتحدث** | إطارات صوت لاسلكي (حمولة صوت مُرمَّزة معتمة) | 15 | `fixtures/media/` | ✅ |
| **مشاركة الشاشة** | إطارات فيديو لمشاركة الشاشة (حمولة فيديو مُرمَّزة معتمة) | 32 | `fixtures/media/` | ✅ |
| **التحكم بالمكالمة** | إشارات رنين / قبول / رفض / إنهاء للمكالمات الصوتية والمرئية | 27 | `fixtures/videocall/` | ✅ |
| **إقرار نداء الاستغاثة** | تأكيد للمرسِل أن بث الطوارئ الخاص به قد استُلم | 6 | `fixtures/sos/` | ✅ |
| **فُتات المساحة** | فُتات اكتشاف موسومة بالموقع لطبقة "ما حولي" | 40 | `fixtures/space/` | ✅ |
| **إعلان المصهر** | الإعلان عن قطعة محتوى مشتقة/مصهورة للشبكة | 41 | `fixtures/forge/` | ✅ |
| **طلب شظية الخزنة** | جلب شظية تخزين مُرمَّزة بالمحو (أي K من N شظية تعيد بناء الملف) | 42 | `fixtures/vaultshard/` | ✅ |
| **قياس النطاق الترددي** | فحص / إقرار / إشاعة إنتاجية الرابط كي تُوجِّه الشبكة عبر أعرض أنبوب (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

تجلس هذه فوق خدمات **المراسلة، والصوت الفردي والجماعي، ومكالمات الفيديو، والبث المباشر، والمشاهدة المشتركة، وتوجيه AODV، وتخزين وإعادة DTN، وفيضان SOS** المكتملة سلفاً — والمطبَّقة أيضاً في جميع اللغات الثماني.

> **ما الذي يعنيه "مبنيّ" هنا، بدقة.** كل خدمة تُنتج حزمة أسلاكها وتعالجها، وتُطلق الأحداث الصحيحة، وتُثبَّت في مستودع على مستوى البايت يجب أن تطابقه عائلة اللغات بأكملها. تطبيقك يربط الخدمة بجلسة Signal الخاصة به، وجدول التوجيه، والحالة المحلية. هذه هي طبقة البروتوكول — مُثبتة في الكود والاختبارات ومستودعات البايت متعددة اللغات — على نفس الأساس الصادق للـRF مثل كل شيء آخر: أي مسار يركب راديو في نهاية المطاف يبقى غير متحقق منه ميدانياً حتى تشغيل العتاد المتتبع في `OPEN_ISSUES.md`.

## الأمان والخصوصية

إلى جانب مجموعة خدمات الأسلاك، يأتي Aether مع **طبقة أمان وخصوصية** صغيرة — إدارة مفاتيح الهوية ومكافحة التتبع على طبقة الوصلة. كما هو الحال مع كل شيء آخر، كل واحدة مطبَّقة في **جميع اللغات الثماني** ومثبتة في مستودع مشترك متعدد اللغات تحت `fixtures/<feature>/` (مع تحقق إضافي من Swift وC على خادم بناء macOS). هذه *ليست* أربع خدمات أسلاك إضافية من الـ18: ثلاث منها لا تُعرّف **أي نوع حزمة أسلاك جديد** على الإطلاق، والرابعة تحمل مظاريفها الخاصة **داخل مسار DTN/الشبكة القائم** بدلاً من كونها حزمة محجوزة جديدة.

| القدرة | ما تفعله | الطبقة | المستودع | 8/8 |
|---|---|---|---|:-:|
| **النسخ الاحتياطي بعبارة الاسترداد** | نسخ الهوية احتياطياً كعبارة **BIP-39 من 24 كلمة** واستعادتها على أي جهاز. BIP-39 قياسي (مُتحقَّق منه مقابل متجهات Trezor الرسمية)، بمجموع تحقق SHA-256 بحيث تُرفَض الكلمة المكتوبة خطأً *رفضاً*، ولا تكون خاطئة بصمت أبداً. لا خادم ولا وصيّ — العبارة **هي** الهوية. | محلية | `fixtures/bip39/` | ✅ |
| **الحماية من تتبع Bluetooth** | تشتق **UUID خدمة** BLE متبدّلاً مشتقاً من مفتاح (HMAC-SHA256، نافذة 15 دقيقة) و**عناوين خاصة قابلة للحل** (IRK + دالة RFC المسماة `ah`، AES-128) — مادة مكافحة التتبع التي يحتاجها مُعلِن BLE كي لا يتمكن ماسح سلبي من ربطه عبر الزمان أو المكان. | طبقة الوصلة | `fixtures/bleprivacy/` | ✅ |
| **مسح الذعر** | **رمز PIN للإكراه** (SHA-256، يُقارَن في زمن ثابت) يمحو بأمان تحت الإكراه كل مفتاح هوية — الكتابة فوقه بقيم عشوائية ثم التصفير — بحيث لا يبقى شيء للاسترداد. | محلية | `fixtures/panicwipe/` | ✅ |
| **مزامنة متعددة الأجهزة** | مزامنة **لامركزية وبلا خادم** عبر أجهزتك *أنت*: **DeviceLink** موقَّع بـEd25519 يُقرِن بينها، ومظاريف **SyncRecord** بمبدأ "آخر كتابة تفوز" توفّق الحالة — محمولة مشفّرة من طرف إلى طرف عبر DTN/الشبكة القائم، بلا حساب سحابي وبلا خادم مزامنة. | تركب DTN | `fixtures/sync/` | ✅ |

**عدم تماثل واحد صادق.** الـ`DeviceLink` متعدد الأجهزة موقَّع بـEd25519، وذلك التوقيع **متطابق بالبايت عبر 7 من اللغات الثماني**. تعمد CryptoKit من Apple إلى *عشوَنة* توقيعات Ed25519، لذا على Swift تختلف بايتات التوقيع الـ64 في كل مرة — لكن **الجسم الموقَّع متطابق بالبايت** وكل وصلة لا تزال تُتحقَّق على جميع الـ8 SDK، فيبلغ Swift تكافؤ **التحقق** بدلاً من تكافؤ بايتات التوقيع. هذه خاصية من خصائص تشفير المنصة، لا عيب، وهي الموضع الوحيد عبر هذه الميزات الأربع الذي يحمل فيه "متطابق بالبايت" علامة نجمية. صيغ الأسلاك الكاملة في [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12؛ ونموذج التهديد في [`THREAT_MODEL.md`](THREAT_MODEL.md).

## وسائل النقل

لكل وسيلة نقل اسم لوني يُستخدم في كافة أرجاء قاعدة الكود. `IsAvailable` يحجب المسارات التي يمنعها العتاد — يتجاوزها `TransportManager` تلقائياً ويرجع إلى وسيلة النقل التالية المتاحة.

**مفتاح الحالة:** ✅ حقيقي، مبنيّ ومتحقق منه · ⏳ حقيقي، التحقق قيد التقدم · ⚠️ حقيقي على بعض المنصات، بديل على غيرها · ❌ بديل (لا كود نقل بعد).

| اللون | الاسم | النطاق | النطاق الترددي | الحالة |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 م | 1 Mbps | ✅ حقيقي — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 م | 250 Mbps | ✅ حقيقي — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | ترحيل HTTP / QUIC | غير محدود | ~10 Mbps | ✅ حقيقي — Windows؛ خادم الترحيل في `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | قناة بيانات عبر الإنترنت | غير محدود | ~100 Mbps | ✅ حقيقي في جميع اللغات الثماني — **متحقق منه بالحلقة الراجعة في الثماني جميعاً** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust كلٌّ منها لديه قرينان يتبادلان البايتات عبر قناة بيانات ICE حقيقية) |
| ⚪ Aether White | NFC HCE | ~5 سم | 848 kbps | ⚠️ حقيقي على Android (`android/white/`)؛ Windows = BLE-GATT حقيقي + تقريب قرب RSSI −40 dBm (`WinNfcBleTransportService`، يُترجم على net9/10، غير متحقق منه في وقت التشغيل) — `Windows.Networking.Proximity` أُزيل في Win 11 |
| 🩵 Aether Teal | NearLink | ~600 م | 12 Mbps | ⚠️ حقيقي على HarmonyOS (`harmonyos/teal/`، `@kit.NearLinkKit` — بانتظار التحقق على الجهاز)؛ Android + Windows = تقريب SSAP-over-BLE حقيقي (`android/teal/AetherNetSleService`، `WinNearLinkBleTransportService`؛ متحقق منه بالترجمة واختبار الوحدة، غير متحقق منه في وقت التشغيل) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 كم | 37.5 kbps | ⚠️ محرك تسلسلي RYLR SX127x/SX126x حقيقي (`LoRaSerialTransport` في C#/Go/Rust/C؛ يُترجم، غير متحقق منه في وقت التشغيل — يحتاج وحدة مادية)؛ جسر BLE Coded-PHY لا يزال تصميماً موثقاً |

وسائل النقل الراديوية حقيقية فقط حيث يوجد كود المنصة (C#/Windows، Kotlin/Android، HarmonyOS). أما مكتبات اللغات الثماني فتشحن بخلاف ذلك بوسيلة نقل **محاكاة داخل العملية** للاختبار — **WebRTC هي أول وسيلة نقل حقيقية مشتركة بينها جميعاً** (مكتملة؛ متحقق منها بالحلقة الراجعة عبر اللغات).

الأولوية بحسب تكلفة الطاقة: الشبكة الراديوية مُفضَّلة، ثم WebRTC كمسار إنترنت مباشر، مع ترحيل HTTP / QUIC كملاذ أخير.

## مستويات النشر

يعمل Aether على أي منصة تدعم البلوتوث أو Wi-Fi. يعتمد المستوى الذي أنت فيه على نظام التشغيل المستهدف.

---

### المستوى القياسي — أي منصة

Android · Windows · Linux · macOS · iOS

يعمل Aether على أي جهاز يحتوي على عتاد بلوتوث أو Wi-Fi. عندما تكون إحدى الراديوات غائبة مادياً، يُقرَّب كل مسار نقل محجوب باستخدام ما هو متاح. هذه التقريبات أصبحت الآن **كوداً حقيقياً** (متحقق منه بالترجمة؛ **غير متحقق منه في وقت التشغيل** بانتظار اختبار RF على جهازين / عتاد):

- **NearLink (Aether Teal)** — تقريب SSAP-over-BLE-GATT حقيقي (معرف Aether SLE `61657468-6572-0003-…`) على Android (`android/teal/AetherNetSleService`) وWindows (`WinNearLinkBleTransportService`)؛ متحقق منه بالترجمة واختبار الوحدة، غير متحقق منه في وقت التشغيل. راديو NearLink الحقيقي موجود فقط على HarmonyOS (`harmonyos/teal/`، بانتظار التحقق على الجهاز).
- **LoRa (Aether Red)** — محرك تسلسلي RYLR SX127x/SX126x حقيقي (`LoRaSerialTransport` في **جميع اللغات الثماني** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin؛ كل منفذ متحقق منه بالترجمة، بما في ذلك Swift + C على خادم بناء Mac؛ غير متحقق منه في وقت التشغيل — يحتاج وحدة مادية). جسر Meshtastic-over-BLE-Coded-PHY (~1.3 كم) يبقى تصميماً موثقاً؛ LoRa الحقيقي بعيد المدى يحتاج عقدة قادرة على LoRa (بوابة، أو SBC، أو هاتف مُحصَّن بوحدة LoRa).
- **NFC (Aether White)** — حقيقي على Android (HCE). Windows لديه الآن تقريب قرب BLE-GATT + RSSI −40 dBm حقيقي (`WinNfcBleTransportService`، يُترجم على net9/10؛ غير متحقق منه في وقت التشغيل)؛ ACR122U PC/SC عند وجود قارئ.

ما هو حقيقي ومتطابق في كل مكان: **BLE، وWi-Fi Direct، وترحيل HTTP / QUIC، ووسيلة نقل WebRTC P2P (متحقق منها بالحلقة الراجعة في اللغات الثماني جميعاً)**، بالإضافة إلى أمان بروتوكول Signal (X3DH + Double Ratchet)، وتوجيه AODV، وتخزين وإعادة DTN، وبث SOS، والصوت، والبث المباشر.

**الحالة الصادقة:** BLE + Wi-Fi Direct + الترحيل حقيقية بجودة إنتاجية؛ **WebRTC P2P حقيقية ومتحقق منها بالحلقة الراجعة في اللغات الثماني جميعاً** (قرينان يتبادلان البايتات عبر قناة بيانات ICE حقيقية — تأكد Rust على صندوق Linux `.201` بـUDP ICE عامل)؛ تقريبات NearLink / LoRa / NFC-على-Windows أصبحت الآن كوداً حقيقياً يُترجم (LoRa متحقق منه بالترجمة في الثماني، بما فيها Swift + C على خادم بناء Mac؛ NearLink-Android مختبَر بالوحدة أيضاً) لكنه **غير متحقق منه في وقت التشغيل** — لا اختبار RF على عتاد / جهازين بعد. تشارك في الشبكة في الكود؛ لا تنشر تلك الثلاثة متوقعاً RF مُثبتاً ميدانياً.

---

### المستوى الأصلي — CircleOS / OpenHarmony

CircleOS · HarmonyOS · أي نظام تشغيل مبني على OpenHarmony

CircleOS مبني على OpenHarmony، الذي يشحن بشريحة NearLink (SLE) وحزمة SDK `@kit.NearLinkKit` كقدرة نظام تشغيل من الدرجة الأولى. على أجهزة CircleOS وHarmonyOS المزودة بعتاد NearLink، لا حاجة للتقريب — يستخدم `harmonyos/teal/` راديو SLE الحقيقي مباشرة:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

هذا ليس مجرد نسخة أفضل من المستوى القياسي. على مستوى NearLink، إنه شبكة مختلفة جوهرياً:

| القدرة | المستوى القياسي (تقريب BLE) | المستوى الأصلي (CircleOS / OpenHarmony) |
|---|---|---|
| **نطاق NearLink** | ~100 م (BLE) | **600 م** |
| **عرض نطاق NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **تأخير NearLink** | ~10 ms (BLE) | **20 µs** |
| **استهلاك طاقة NearLink** | خط أساسي BLE | **60% أقل من BLE 5.0** |
| **أقران NearLink المتزامنون** | ~7 (حد اتصال BLE) | **500+** |
| **مصدر NearLink** | SSAP-over-BLE (`android/teal/`، `WinNearLinkStubTransportService`) | راديو SLE حقيقي (`harmonyos/teal/`، `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / ترحيل HTTP** | أصلي | أصلي (مطابق) |
| **أمان بروتوكول Signal** | كامل | كامل (مطابق) |
| **التوجيه / DTN / SOS** | كامل | كامل (مطابق) |
| **هوية Aether Tag** | مدعومة | مدعومة (مطابق) |

---

### الانتقال بين المستويات

لا يلزم تغيير في الكود. يُحدَّد المستوى في وقت التشغيل من خلال `IsAvailable` على كل خدمة نقل:

1. على جهاز CircleOS أو HarmonyOS بشريحة NearLink، يُرجع `IsAvailable` على خدمة نقل NearLink `true` (يُستشار العتاد عبر فحص الإذن + محاولة مسح سلبي).
2. يُعلي `TransportManager` تلقائياً أولوية NearLink — أدنى تكلفة طاقة، وأعلى عرض نطاق.
3. كود التطبيق وتنسيق الحزمة وخوارزمية التوجيه وطبقة الأمان وعلامات Aether Tags متطابقة عبر كلا المستويين.

العقدة على المستوى القياسي والعقدة على المستوى الأصلي يمكنهما التواصل بحرية — تشتركان في نفس تنسيق الأسلاك، ونفس جلسات بروتوكول Signal، ونفس علامات Aether Tags. الفرق في المستوى يؤثر فقط على الراديو المستخدم لحزم NearLink، وليس على البروتوكول فوقه.

---

> **داخلياً، يُشار إلى هذين المستويين باسم متغير Asterix (القياسي) ومتغير Obelix (الأصلي).** Asterix يعمل جيداً بما هو متاح. أما Obelix — الذي يعمل على CircleOS مع NearLink الأصلي — فيعمل بقدرة مرتفعة بشكل دائم، تماماً كما يحمل Obelix قوة الجرعة السحرية دون الحاجة إلى شربها مجدداً.

---

## التطبيقات

Aether مبني بـ8 لغات ليعمل على الهواتف وأجهزة الكمبيوتر المحمولة والأجهزة اللوحية والمتحكمات الدقيقة. جميع التطبيقات تُنتج حزمًا متوافقة مع الأسلاك — رسالة مشفرة بعقدة Rust يمكن ترحيلها بعقدة Python وفك تشفيرها بعقدة Swift.

| اللغة | الدليل | تنسيق الأسلاك | التوجيه/DTN/SOS | X3DH | Double Ratchet | مجموعة OPK | الصوت/المجموعة | البث/الفيديو/المشاهدة |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

تُنتج جميع اللغات الثماني حزمًا متطابقة بالبايت على مستوى الأسلاك، مُتحقق منها بـ14 مخرج قانونياً لتنسيق الأسلاك و4 متجهات اختبار Signal تُشغَّل في CI (`fixtures/expected/*.bin`، `fixtures/signal/expected/*.json`). التوجيه (بأسلوب AODV RREQ/RREP)، وDTN للتخزين والإعادة، وبث SOS، والصوت، والبث، وخدمات تصليب الأمان مُطبَّقة في كل لغة مع **~3,000 اختبار** عبر جميع التطبيقات الثمانية:

| اللغة | الاختبارات | منصة CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **الإجمالي** | **~3,000** | |

التشغيل البيني لـSignal متعدد اللغات مرتبط بـ`fixtures/signal/` مع متجهات اختبار مشتركة لـX3DH (`x3dh_basic`)، والتشعبات المتماثلة (`ratchet_step_basic`، `ratchet_step_three_iterations`)، وKDF_RK (`kdf_rk_basic`). يجب على كل تطبيق إنتاج مخرجات متطابقة بالبايت مقابل تلك المخرجات المحددة. تشحن جميع اللغات الثماني الآن بجلسة Signal كاملة (`generate_pre_key_bundle`، `process_pre_key_bundle`، `encrypt`، `decrypt`).

فيما يتجاوز تنسيق الأسلاك وSignal، فإن **مجموعة خدمات الأسلاك بأكملها** — الحضور، ونبضة القلب، ومزامنة الملف الشخصي، وإعلان المعرف المؤقت، وتبادل المفاتيح الأولية، والقنوات، والضغط للتحدث، ومشاركة الشاشة، والتحكم بالمكالمة، وإقرار نداء الاستغاثة، وفُتات المساحة، وإعلان المصهر، وطلب شظية الخزنة، وقياس النطاق الترددي (انظر **ما الذي تحصل عليه — كل خدمة، بكل لغة**) — مطبَّقة كذلك في جميع اللغات الثماني ومثبتة في مستودعاتها الخاصة (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/`، وأخواتها). لا ميزة حكرٌ على C# في طبقة البروتوكول.

## البدء السريع

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

يأخذك العرض التوضيحي عبر 8 خطوات: توليد مفاتيح هوية Ed25519 لثلاث عقد (Alice وBob وCharlie)، وإنشاء جلسات بروتوكول Signal، وإرسال رسائل مشفرة، وترحيل رسالة عبر Charlie (الذي لا يستطيع قراءتها)، وعرض تنسيق الأسلاك الثنائي، وإظهار السرية الأمامية عبر 5 رسائل متتالية. المخرجات ملونة وتتوقف بين الخطوات.

**إرسال رسالة بـC#:**

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

يُولّد العرض التوضيحي مفاتيح هوية لعقدتين، ويتبادل حزم المفاتيح الأولية، ويُنشئ جلسات مشفرة، ويُرسل رسائل مشفرة في كلا الاتجاهين، ويُنشئ حزم شبكة ويوقعها، ويتحقق من التوقيعات، ويُسلسل الحزم إلى تنسيق الأسلاك الثنائي. كما يُوضح طبقة النقل داخل العملية.

**إرسال رسالة بـRust:**

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

يُنشئ العرض التوضيحي عقدتين في شبكة محاكاة، ويُولّد مفاتيح Ed25519، ويُنشئ جلسات بروتوكول Signal، ويُنشئ حزمة ويوقعها، ويُسلسلها إلى تنسيق ثنائي متوافق مع C#، ويُشفر رسالة سرية، ويفك تشفيرها على العقدة الأخرى، ويُرسلها عبر وسيلة النقل، ويتحقق من الرحلة ذهاباً وإياباً.

**إرسال رسالة بـTypeScript:**

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

يُشغّل العرض التوضيحي 8 مظاهرات: توليد مفاتيح Ed25519 وكشف التلاعب، وإنشاء عقد بقدرات محددة، وتبادل مفاتيح X3DH لبروتوكول Signal، وتشفير وفك تشفير AES-256-GCM، وتسلسل الحزم، وتوقيع الحزم مع كشف إعادة التشغيل، ووسيلة النقل داخل العملية، وتدفق كامل من طرف إلى طرف يجمع كل الطبقات.

**إرسال رسالة بـPython:**

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

يُشغّل العرض التوضيحي 5 مظاهرات: جولات تسلسل الحزم، وتوقيع Ed25519 مع كشف التلاعب، وإنشاء جلسة بروتوكول Signal مع رسائل مشفرة في كلا الاتجاهين، ووسيلة النقل داخل العملية بين عقدتين، وإزالة تكرار الـnonce لحماية إعادة التشغيل.

**إرسال رسالة بـGo:**

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

يمر العرض التوضيحي بـ11 خطوة: توليد المفاتيح، وإنشاء عقد بقدرات، وتهيئة بروتوكول Signal، وتبادل حزم المفاتيح الأولية، وإنشاء الجلسة، وإنشاء الحزمة وتوقيعها، والتسلسل، وإلغاء التسلسل مع التحقق من التوقيع، والتشفير الكامل من طرف إلى طرف مع تشعبات المفاتيح، وكشف هجمات إعادة التشغيل، ووسيلة النقل داخل العملية.

**إرسال رسالة بـKotlin:**

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

يُشغّل العرض التوضيحي 5 اختبارات: جولات تسلسل الحزم، وتوقيع Ed25519 مع رفض التلاعب، وإنشاء جلسة بروتوكول Signal مع تشفير AES-256-GCM، وتسليم رسائل وسيلة النقل داخل العملية، وتدفق كامل من طرف إلى طرف حيث توقّع Alice حزمة ويتحقق منها Bob بعد النقل.

**إرسال رسالة بـSwift:**

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

يُشغّل العرض التوضيحي 7 مظاهرات: توليد مفاتيح Ed25519، وإنشاء الحزمة وتوقيعها، والتسلسل إلى تنسيق الأسلاك الثنائي، وإلغاء التسلسل مع فحوصات السلامة، وتشفير وفك تشفير AES-256-GCM، ومصادقة رسائل HMAC-SHA256، واشتقاق مفاتيح HKDF-SHA256.

**إرسال رسالة بـC:**

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

## خارطة الطريق

ما تم بناؤه وما هو قادم.

**مكتمل (متحقق منه متعدد اللغات، جميع التطبيقات الثمانية):**
- تنسيق الأسلاك: متطابق بالبايت عبر 8 لغات، مرتبط بـ14 مخرج قانونياً وتأكيدات متعددة اللغات في CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — مصفوفة 9 وظائف (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, بالإضافة إلى وظيفة سلامة المخرجات) في `.github/workflows/ci.yml`.
- توقيع والتحقق من حزم Ed25519
- تشفير AES-256-GCM
- أساسيات اشتقاق مفاتيح HKDF / HMAC
- تسلسل الحزم + تخطيط التوقيع (LE + حقول int32 بـ4 بايتات)
- محاكي النقل داخل العملية (للتطوير والاختبارات)
- خدمة توجيه مستوحاة من AODV مع RREQ/RREP، وردود مسار موقَّعة، وإزالة التكرار، وإعادة توجيه TTL
- خدمة DTN للتخزين والإعادة مع نقل الحضانة، والنسخ المتماثل المدرك للـgeohash، ومدة حياة 72 ساعة
- خدمة بث SOS مع الفيضان، وإزالة التكرار، وحارس المصدر الذاتي، وتحديد المعدل (3/ساعة)
- نقاط التوسعة: `IncentiveProvider`، `BackendClient`، `FeatureFlagProvider` (افتراضيات Noop)
- **~3,000 اختبار** عبر جميع اللغات الثماني (C# 530، TypeScript 459، Kotlin 457، Go 423، Python 387، Swift 295، C 253، Rust ~195) — جميعها ناجحة في CI
- ✅ **مفتاح X3DH المؤقت الحقيقي (8 لغات)** — 4 عمليات DH بـX25519 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) مع اشتقاق جذر HKDF-SHA256. مثبت بـ`fixtures/signal/expected/x3dh_basic.json`.
- ✅ **محاذاة Double Ratchet على مستوى العائلة** — Signal §5 الكامل مع HMAC-SHA256 + فصل نطاق 0x01/0x02 في التشعبات المتماثلة، وHKDF-SHA256 KDF_RK في خطوة تشعب DH، وتدوير DH عند الاستلام. متحقق منه بمخرجات `ratchet_step_basic` و`ratchet_step_three_iterations` و`kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 متوافق مع HEAD** — انظر `docs/PROTOCOL_SPEC.md`.

**مكتمل (جميع اللغات الثماني):**
- ✅ **مكالمات صوتية (1-to-1)** — آلة حالة الإشارة (Offer/Answer/Hangup/Cancel/Timeout) + نقل إطارات ثنائية (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). تسليم مدرك للمسار عبر `IRoutingService`.
- ✅ **صوت جماعي** — عضوية يديرها المضيف (دعوة/طرد/مغادرة)، حقل توليد مفتاح لكل إطار، توزيع أحادي الاتجاه لجميع الأعضاء الحاليين، تدوير مفاتيح يتحكم فيه المضيف عند تغيير العضوية.
- ✅ **البث المباشر** — يبث الناشر `StreamAnnounce`؛ يرسل المشتركون `StreamSubscribe`؛ إطارات ثنائية `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) أحادية الاتجاه لكل مشترك.
- ✅ **مكالمات فيديو (1-to-1)** — التفاوض على الترميز/الدقة/معدل الإطارات/معدل البت في الإشارة، وإشارات طلب الإطار الرئيسي وتغيير الجودة، وتنسيق ثنائي `VideoFrame` مطابق لتخطيط الصوت.
- ✅ **المشاهدة الجماعية** — يصدر المضيف أوامر `WatchSync` موثوقة (تشغيل/إيقاف مؤقت/بحث/سرعة)؛ يطبقها المتابعون مع تعويض RTT (`position = positionMs + elapsed × playbackSpeed`)؛ `WatchReaction` بدون تأكيد.
- ✅ **مجموعة مفاتيح أحادية الاستخدام (OPK)** — 100 افتراضياً، إصدار FIFO، تعبئة كسولة، استهلاك محمي بالقفل عبر جميع اللغات الثماني. يُغلق خطر التزامن لمفتاح OPK الواحد.
- ✅ **C: جلسة Signal كاملة** — `aethernet_signal_service_init` و`generate_pre_key_bundle` و`process_pre_key_bundle` و`encrypt` و`decrypt` في `c/src/signal_protocol.c`؛ 6 اختبارات E2E بين عقدتين في `c/tests/test_signal_session.c`. جميع اللغات الثماني الآن لديها بروتوكول Signal كامل قادر على الجلسات.

**مكتمل (جميع اللغات الثماني — مجموعة خدمات الأسلاك الكاملة):**
- ✅ **كل نوع حزمة محجوز أصبح الآن خدمة حقيقية متطابقة بالبايت في جميع اللغات الثماني.** منارة/استعلام الحضور (21/22)، ونبضة القلب (10)، ومزامنة الملف الشخصي (23)، وإعلان معرف التوجيه المؤقت (56)، وتبادل المفاتيح الأولية (25/26)، والقنوات (7)، والضغط للتحدث (15)، ومشاركة الشاشة (32)، والتحكم بالمكالمة (27)، وإقرار نداء الاستغاثة (6)، وفُتات المساحة (40)، وإعلان المصهر (41)، وطلب شظية الخزنة (42)، وقياس النطاق الترددي / ABMF (53/54/55). كل واحدة خدمة رفيعة (إنتاج + معالجة + حدث) يربطها المضيف بجلسة Signal الخاصة به وجدول التوجيه؛ وكل واحدة مثبتة في مستودع مشترك متعدد اللغات (`fixtures/presence/`، `fixtures/media/`، `fixtures/bandwidth/`، `fixtures/prekey/`، `fixtures/videocall/`، `fixtures/vaultshard/`، `fixtures/channels/`، `fixtures/profiles/`، `fixtures/heartbeat/`، `fixtures/erid/`، `fixtures/space/`، `fixtures/forge/`، `fixtures/sos/`) وتُختبر باختبارات وحدة لكل لغة، مع تحقق من Swift وC على خادم بناء macOS. انظر **ما الذي تحصل عليه — كل خدمة، بكل لغة**.

**مكتمل (مرجع C# فقط):**
- ✅ **العرض التوضيحي الخطوة 9 — MessagingService + DTN fallback من طرف إلى طرف** — `samples/AetherNet.Demo.Console` يمر عبر مراسلة مشفرة بـSignal الحقيقي مع تخزين وإعادة DTN عندما يكون المستلم غير متصل.
- ✅ **جسر `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` يجعل طبقة المراسلة مشفرة من طرف إلى طرف افتراضياً؛ الرسائل بلا جلسة Signal تُصفّ في طابور، ولا تُرسل قط بشكل غير آمن.
- ✅ **البث التكيفي متغير معدل البت** — `AdaptiveBitrateController` مع سلالم معدل البت المحددة في المواصفات للملف الشخصي A (الوقت الفعلي)، وB (البث المباشر)، وC (VOD). يختار الناشر أعلى درجة مستدامة (هامش 20%) ويصدر `StreamAbandon` (`PacketType.StreamAbandon`) بدلاً من شظية عندما يكون تحت الحد الأدنى. `IStreamingService` يعرض `UpdateBandwidthEstimate` و`GetCurrentBitrateRung`.
- ✅ **المشاهدة الجماعية: استيعاب BitTorrent + تمويل ChipIn الجماعي** — نماذج `TorrentInfo` / `TorrentFile`؛ يعالج `WatchTogetherService` النوع `PacketType.TorrentMetadata` ويُطلق `TorrentReceived`. آلة حالة `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded)؛ `StartChipInAsync` / `ContributeAsync` / `GetChipIn` على `IWatchTogetherService`.
- ✅ **مكالمات فيديو جماعية مع ترحيل SFU تلقائي** — `GroupVideoService` / `IGroupVideoService`. طبولوجيا FullMesh لـ≤ 3 مشاركين؛ تبديل تلقائي إلى SFU عند `SfuThresholdParticipants` (4) مع إعادة تعيين الترحيل عبر `GroupVideoSignaling(SfuAssigned)`. توزيع في FullMesh، وإرسال عبر الترحيل فقط في وضع SFU. نوع حزمة الإشارة `GroupVideoSignaling = 35`.
- ✅ **محاكاة نقل BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). تأطير GATT MTU عبر `BleGattFramer` (1024 B/إطار، `[2B count][2B index][payload]`)، سجل أقران ثابت داخل العملية، بث الإعلان. جميع قيود `BleMaxPayloadBytes` مفروضة.
- ✅ **محاكاة نقل Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). دورة حياة `ConnectAsync`/`DisconnectAsync` صريحة، تسليم حمولة كبيرة مباشر (بلا تأطير)، أحداث `PeerConnected`/`PeerDisconnected` ثنائية الاتجاه.
- ✅ **محاكاة نقل NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU إطار 4096 B، سجل 500 قرين، `ConnectedPeerCount`، `IsAvailable` قابل للضبط في وقت التشغيل.
- ✅ **اختبارات محاكاة تشغيل RF** — اختبارات تشغيل بيني بين عقدتين (`SimulatedTransportTests`): جولة `MeshPacket` عبر BLE + NearLink، ونقل حمولة 64 KB عبر WiFi Direct. الطبقة البرمجية متحقق منها بالكامل؛ جلسة مختبر أجهزة مادية مطلوبة للتحقق على العتاد.

**مكتمل (طبقة النقل C# — جميعها fail-fast):**
- ✅ **نقل BLE GATT الحقيقي** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server). اختبار تشغيل RF كامل في `samples/AetherNet.BleRfTest/`.
- ✅ **نقل Wi-Fi Direct الحقيقي** — `WinWifiDirectTransportService` (WinRT، `WiFiDirectAdvertisementPublisher` + TCP StreamSocket المنفذ 8888) + `android/green/` (`WifiP2pManager`). اختبار RF في `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **نقل ترحيل HTTP (Aether Purple)** — `HttpRelayTransportService` مع استطلاع طويل مدته 10 ثوانٍ، `PowerCostRelative = 100`، دائماً الملاذ الأخير. خادم الترحيل في `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API، المنفذ 5200). اختبار RF في `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` يطبق `HostApduService` بـAID `F061657468657200`. `WinNfcStubTransportService` يوثق مسارَي تقريب على Windows: (1) NDEF-over-BLE-GATT ببوابة RSSI ≥ −40 dBm (يحاكي النقر للاتصال دون شريحة NFC، `IsAvailable = Bluetooth present`)؛ (2) قارئ ACR122U USB عبر `Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). مسار الترقية: نفّذ `ITransportService` عندما تشحن Microsoft واجهة برمجية P2P NFC أصلية.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — تطبيق ArkTS كامل لـHarmonyOS 5.0.1 (API 13) باستخدام `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`)؛ `isAvailable` يُستشار في وقت التشغيل. `WinNearLinkStubTransportService` + `android/teal/` يوثقان تقريب SSAP-over-BLE: GATT BLE بمعرف خدمة Aether SLE `61657468-6572-0003-0000-000000000000` — مماثل للواجهة البرمجية لـSSAP، غير متوافق مع الأسلاك مع عتاد NearLink الحقيقي. مسار الترقية: استبدل استدعاءات BLE GATT باستدعاءات SDK `ssapc_*`/`ssaps_*`؛ المعرفات وفتحة `TransportManager` دون تغيير.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` يوثقان تقريب Meshtastic-over-BLE-LR: تنسيق أسلاك Meshtastic الكامل (رأس 16 بايت + protobuf بـAES-256-CTR) عبر BLE 5.0 Coded PHY S=8 (~1.3 كم خارجياً)، مع توجيه فيضان مُدار ونافذة تنازع مرجّحة بـRSSI. اتحاد عقد الجسر مع عتاد LoRa الحقيقي يعمل تلقائياً (نفس تنسيق حزمة Meshtastic، بلا ترجمة). مسار الترقية: استبدل راديو BLE LR بمحرك SX1276/SX1278 بأوامر AT أو SPI؛ تنسيق الحزمة والتوجيه دون تغيير.

**مفتوح — متتبع في `OPEN_ISSUES.md`:**
- تشغيل RF على العتاد الحقيقي: اختبار تشغيل بيني بين عقدتين على أجهزة BLE / Wi-Fi Direct فعلية (اختبارات المحاكاة ناجحة؛ جلسة مختبر العتاد مطلوبة)
- NearLink: `harmonyos/teal/` مكتمل؛ يتطلب عتاد Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (شريحة NearLink غير موجودة على الأجهزة غير المصنوعة من Huawei). Windows + Android يرجعان تلقائياً إلى تقريب SSAP-over-BLE.
- LoRa / CircleLink: وحدة راديو مطلوبة لنطاق LoRa الحقيقي. بدونها، يُحمل تنسيق أسلاك Meshtastic عبر BLE LR (~1.3 كم) ويتوفر اتحاد عقد الجسر مع عتاد LoRa الحقيقي.
- ✅ **(حُلّ في v1.2.0)** سطح البروتوكول الاستهلاكي (الموجة 16/17) — حدث `IDtnService.BundleReceived` للحزم الواردة ([#59](https://github.com/bhengubv/aether-protocol/issues/59))، دليل تسمية/اكتشاف على مستوى التطبيق ([#60](https://github.com/bhengubv/aether-protocol/issues/60))، واجهة إكرامية المؤلف ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). شُحنت الثلاثة إضافياً عبر 8 لغات بمستودعات متعددة اللغات متطابقة بالبايت. انظر CHANGELOG.

**لم يُفتح بعد للمساهمات الخارجية:**
- البروتوكول لا يزال قيد التطوير النشط. المساهمات الخارجية غير مقبولة في الوقت الحالي.
- تطبيق نقل NearLink، وأمثلة تكامل Android/iOS، ووسائل نقل إضافية، ومقاييس الأداء، وتشويش البروتوكول متتبعة داخلياً وستُفتح عندما يصل المشروع إلى نقطة مساهمة عامة مستقرة.

## هيكل المشروع

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

## إضافة وسيلة نقل جديدة

نفّذ `ITransportService`:

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

سجّلها في DI وسيُضمنها `TransportManager` تلقائياً في اختيار وسيلة النقل، مرتبةً حسب تكلفة الطاقة.

## المقارنة

| البروتوكول | القيود | ميزة Aether |
|----------|-----------|-----------------|
| **Briar** | Android فقط، يعتمد على Tor | متعدد المنصات، شبكة كاملة |
| **Meshtastic** | LoRa فقط (30 kbps كحد أقصى) | متعدد وسائل النقل (BLE + WiFi + NearLink)، قادر على الصوت والبث |
| **Reticulum** | Python، مجتمع صغير | 8 لغات، متوافق في الأسلاك عبر جميعها |
| **libp2p** | يفترض وجود عمود فقري للإنترنت | يُقدّم الوضع غير المتصل أولاً، يعمل دون بنية تحتية |
| **Yggdrasil** | شبكة تراكب، تحتاج إنترنت | شبكة طبقة مادية، تعمل دون إنترنت |
| **Signal** | لا شبكة، يتطلب إنترنت | يعمل غير متصل، P2P، ترحيل شبكي، نفس تشفير E2E |

## الأسئلة الشائعة

**هل يعمل AetherNet دون إنترنت؟**
نعم — إنه يعمل دون اتصال أولاً. تتحدث الأجهزة مباشرة عبر البلوتوث، وWi-Fi Direct، وNearLink، أو LoRa، وتُرحّل الرسائل قفزة بقفزة عبر أجهزة أخرى، دون الحاجة إلى اتصال بالإنترنت، أو برج خلوي، أو خادم. وعندما لا يوجد مسار مباشر، تُحتفظ بالرسائل (تخزين وإعادة يتحمل التأخير) لمدة تصل إلى 72 ساعة حتى يُفتح مسار.

**هل هو مشفَّر من طرف إلى طرف؟**
نعم. يستخدم AetherNet بروتوكول Signal (اتفاق مفاتيح X3DH بالإضافة إلى Double Ratchet عبر X25519) للتشفير من طرف إلى طرف، وAES-256-GCM لحمولات الرسائل، وتوقيعات Ed25519 على كل حزمة. والأجهزة التي تُرحّل رسالة لا تستطيع قراءتها.

**ما وسائل النقل التي يستخدمها؟**
البلوتوث LE، وWi-Fi Direct، وNearLink (SLE)، وراديو تسلسلي LoRa/CircleLink، وترحيل HTTP/QUIC، وWebRTC للاتصال المباشر بين الأقران عبر الإنترنت. يختار البروتوكول تلقائياً أدنى وسيلة نقل متاحة استهلاكاً للطاقة لكل حزمة ويرجع إلى التالية.

**بأي لغات البرمجة يتوفر؟**
ثماني لغات — C# وRust وTypeScript وPython وGo وKotlin وSwift وC. كل تطبيق يُنتج حزمًا متطابقة بالبايت على مستوى الأسلاك، مفروضٌ ذلك بمستودع مشترك للاختبارات متعددة اللغات في CI، لذا فإن الحزمة التي تبنيها لغة واحدة تفكها أي لغة أخرى دون تغيير.

**كيف يختلف عن Meshtastic أو Briar أو Bridgefy؟**
Meshtastic يقتصر على LoRa؛ أما AetherNet فهو متعدد وسائل النقل (بلوتوث + Wi-Fi + NearLink + LoRa) ويحمل الصوت والفيديو والبث بالإضافة إلى الرسائل. وBriar يقتصر على Android ويوجّه عبر Tor؛ أما AetherNet فمتعدد المنصات وشبكة متشابكة خالصة. وبخلاف حزم SDK المغلقة، فإن AetherNet مرخّص برخصة MIT ومُطبَّق بشكل مفتوح في ثماني لغات. جدول المقارنة أعلاه فيه التفاصيل.

**هل هو جاهز للإنتاج؟**
طبقة البروتوكول — تنسيق الأسلاك، وأمان Signal، والتوجيه، وتخزين وإعادة DTN، ومجموعة الخدمات الكاملة — مُطبَّقة ومُختبَرة عبر اللغات الثماني جميعاً. وسائل النقل الراديوية حقيقية حيث يوجد كود المنصة (البلوتوث وWi-Fi على Windows وAndroid، وWebRTC في كل مكان) وغير متحقق منها ميدانياً في غير ذلك بانتظار تشغيل العتاد، وهو ما يُتتبع بصدق في `OPEN_ISSUES.md`. اقرأ ملاحظات الحالة في كل قسم قبل النشر.

**بأي رخصة هو؟**
MIT — مجاني للاستخدام التجاري ومفتوح المصدر. انظر [LICENSE](LICENSE).

**من يبني AetherNet؟**
يُطوَّر بوصفه البروتوكول المفتوح خلف منظومة الشبكات المتشابكة لـThe Geek Network، مبنيّاً في جنوب أفريقيا لأجل تواصل يعمل مع بيانات الموبايل أو دونها.

## نقاط التوسعة

يعمل البروتوكول بشكل مستقل. هذه الواجهات تتيح لك توصيل خلفيتك الخاصة إذا أردت:

- `IAetherNetIncentiveProvider` — مكافأة العقد التي تُرحّل حركة المرور (افتراضي no-op: ترحيل إيثاري)
- `IAetherNetBackendClient` — المزامنة مع خادم عند توفر الإنترنت (افتراضي no-op: غير متصل بالكامل)
- `IAetherNetFeatureFlagProvider` — تبديل ميزات البروتوكول في وقت التشغيل (افتراضي no-op: كل شيء مُفعَّل)

تشحن الثلاثة مع تطبيقات no-op. أزلها ولن يتعطل شيء.

## المساهمة

المساهمات الخارجية لم تُفتح بعد. المشروع لا يزال قيد التطوير النشط. تحقق مجدداً عندما نُعلن نافذة مساهمة عامة.

## الأمان

انظر [SECURITY.md](SECURITY.md) لسياسة الإفصاح المسؤول.

## الرخصة

رخصة MIT. انظر [LICENSE](LICENSE).

## الترجمات

هذا الملف التعريفي مُصان بالإنجليزية ومُترجَم إلى 10 لغات إضافية تحت [`docs/i18n/`](docs/i18n/): Français, Español, العربية, 中文简体, 日本語, Deutsch, Português (BR), Русский, فارسی, و한국어. **النسخة الإنجليزية هي مصدر الحقيقة** — حين تختلف ترجمة عن النص الإنجليزي، يكون النص الإنجليزي هو المرجع، وقد تتأخر الترجمات عنه بإصدار أو اثنين. البروتوكول والكود والمستودعات والسلوك الموصوف متطابقة مهما كانت اللغة التي تقرأ بها.

</div>
