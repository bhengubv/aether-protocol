<div dir="rtl">

# مشخصات پروتکل شبکه مشبک Aether

**نسخه:** 2.0
**وضعیت:** با HEAD تطبیق یافته (2026-05-05)
**تاریخ:** 2026-03-15 (پیش‌نویس اولیه)؛ 2026-05-05 (§2، §4، §10، §11 تطبیق یافت؛ §3/§9 تأیید شد)
**نویسندگان:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **اطلاعیه خواننده.** پیش‌نویس‌های قبلی این سند مقدم بر تراز فرمت سیم ۸ زبانه و انتقال خانواده‌گستر به X25519 + Signal Double Ratchet هستند. از 2026-05-05، §2 (فرمت بسته)، §3 (مسیریابی)، §4 (تبادل کلید)، §9 (DTN) پروتکل پیاده‌سازی شده را توصیف می‌کنند؛ §10 (پخش ویدیو) و §11 (تماشا با هم) پروتکل هدف را توصیف می‌کنند — آن‌ها از نظر سیم تعریف شده و با fixture آزمایش شده‌اند اما pipeline‌های کدک / BitTorrent / ChipIn هنوز به اسکلت‌بندی متصل نشده‌اند. مرجع C# در هر جایی که این سند و پیاده‌سازی اختلاف دارند، معتبر است.
>
> - بایت‌های سیم قانونی: `fixtures/expected/*.bin` (۱۰ مورد نامگذاری شده)
> - Serializer مرجع: `src/Aether.Core/Protocol/PacketSerializer.cs`
> - پشته Signal مرجع: `src/Aether.Security/Services/SignalProtocolService.cs`
> - مسیریابی مرجع: `src/Aether.Core/Routing/RoutingService.cs`
> - DTN مرجع: `src/Aether.Core/Dtn/DtnService.cs`
> - اثبات تعامل‌پذیری سیم چند زبانه: `fixtures/README.md`
> - اثبات تعامل‌پذیری Signal چند زبانه: `fixtures/signal/README.md`

---

## فهرست مطالب

1. [چکیده](#1-abstract)
2. [فرمت بسته](#2-packet-format)
3. [الگوریتم مسیریابی](#3-routing-algorithm)
4. [تبادل کلید](#4-key-exchange)
5. [الزامات لایه انتقال](#5-transport-layer-requirements)
6. [پروتکل کشف](#6-discovery-protocol)
7. [مدل امنیتی](#7-security-model)
8. [پخش SOS](#8-sos-broadcast)
9. [ذخیره و ارسال DTN](#9-dtn-store-and-forward)
10. [پخش ویدیو](#10-video-streaming)
11. [تماشا با هم](#11-watch-together)

---

## ۱. چکیده

Aether یک پروتکل شبکه مشبک غیرمتمرکز است که برای محیط‌هایی با اتصال اینترنتی متناوب یا غایب طراحی شده است. مسیریابی چندگام بسته را روی انتقال‌های کوتاه‌برد ناهمگون (Bluetooth Low Energy، Wi-Fi Direct، NearLink)، رمزنگاری سرتاسر با استفاده از توافق کلید مشتق از X3DH با چرخ متقارن، تحویل ذخیره و ارسال تحمل‌پذیر تأخیر، و یک مکانیسم سیل اضطراری SOS ارائه می‌دهد. پروتکل مستقل از انتقال است: هر لایه فیزیکی که بتواند آرایه‌های بایت بین همتایان ارسال و دریافت کند یک انتقال Aether معتبر است. گره‌ها توسط شناسه‌های سخت‌افزار جهانی (UHID) شناسایی می‌شوند و از طریق کلیدهای هویت Ed25519 احراز هویت می‌کنند. Aether به عنوان یک لایه شبکه جهانی در نظر گرفته شده است — هر برنامه در اکوسیستم سرویس‌های Aether را ثبت می‌کند، و گره‌های بدون اتصال اینترنت از طریق همتایان دروازه‌ای که ترافیک مشبک را به اینترنت پل می‌زنند به شبکه گسترده‌تر دسترسی پیدا می‌کنند.

---

## ۲. فرمت بسته

> در 2026-05-05 با `src/Aether.Core/Protocol/PacketSerializer.cs` و ۱۰ مورد fixture در `fixtures/expected/` تطبیق یافت.

### ۲.۱. طرح‌بندی سیم MeshPacket

هر پیام Aether در یک `MeshPacket` کپسوله می‌شود. فیلدها **دقیقاً** به این ترتیب روی سیم ظاهر می‌شوند:

| آفست | فیلد | نوع | اندازه | یادداشت‌ها |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = بدون امضا (قدیمی)، `2` = امضادار (فعلی) |
| 1   | Type             | uint8                           | 1          | شمارش نوع بسته (§2.4 را ببینید) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | شناسه بسته برای حذف تکراری. ترتیب بایت **Big-endian**، نه Guid mixed-endian پیش‌فرض .NET. |
| 18  | Priority         | uint8                           | 1          | سطح اولویت (0 = عادی، 255 = SOS). **فیلد سیم ۱ بایت است؛ مقادیر >255 باید محدود شوند.** |
| 19  | Ttl              | int32, little-endian            | 4          | زمان زندگی، در هر گام کاهش می‌یابد. **int32 4 بایتی**، نه uint8 1 بایتی — مقادیر تا ~۲³¹-1 معتبرند. |
| 23  | TimestampMs      | int64, little-endian            | 8          | میلی‌ثانیه‌های epoch یونیکس (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | طول `SourceUhid` به بایت‌های UTF-8. حداکثر 65535. |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | UHID فرستنده؛ خالی مجاز است اما غیرمعمول. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | طول `DestinationUhid` به بایت‌های UTF-8. |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | UHID گیرنده؛ رشته خالی برای broadcast. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | طول `PacketNonce` به بایت. مقدار استاندارد: 8. |
| ... | PacketNonce      | bytes                           | P          | nonce تصادفی رمزنگاری برای جلوگیری از replay. |
| ... | Payload Len      | int32, little-endian            | 4          | طول `Payload` به بایت. مقادیر منفی خطا هستند. |
| ... | Payload          | bytes                           | Q          | داده برنامه. تفسیر به `Type` بستگی دارد. |
| ... | Signature Len    | uint16, little-endian           | 2          | طول `Signature` به بایت. 0 (بدون امضا) یا 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | امضای Ed25519 روی داده قابل امضا (§2.3 را ببینید). |

**پهنای پیشوند طول** بر اساس فیلد متفاوت است — `SourceUhid`، `DestinationUhid`، `PacketNonce`، و `Signature` از پیشوند طول **2 بایتی (uint16)** استفاده می‌کنند؛ `Payload` از پیشوند طول **4 بایتی (int32)** استفاده می‌کند چون payload ها می‌توانند از 64 KiB تجاوز کنند.

### ۲.۲. حداقل اندازه بسته

با تمام فیلدهای متغیر-طول خالی (UHID صفر طول، nonce صفر طول، payload صفر طول، امضا صفر طول)، اندازه سیم است:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

ارقام ۵۰ بایت / ۵۲ بایت در پیش‌نویس‌های قبلی این مشخصه نادرست بودند.

### ۲.۳. نمودار فرمت سیم

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

برای یک مثال کارشده، `fixtures/expected/basic_data.bin` را ببینید (83 بایت، ورودی قانونی در `fixtures/inputs.json`). پیاده‌سازی‌ها در برابر مجموعه fixture کامل تأیید می‌شوند — هر انحرافی آزمایش تأیید fixture چند زبانه را شکست می‌دهد.

### ۲.۴. ساختار داده قابل امضا

امضا (فیلد `Signature` روی سیم) روی یک دنباله بایت قانونی جداگانه محاسبه می‌شود — **نه** روی بایت‌های سیم خودشان. این اجازه می‌دهد طرح‌بندی سیم بدون شکستن امضاها تکامل یابد، و به گره‌های میانی اجازه می‌دهد یکپارچگی را بدون دیدن payload متن ساده تأیید کنند (فقط هش SHA-256 آن امضا می‌شود).

دنباله بایت قابل امضا الحاق است:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> توجه به تفاوت عمدی از طرح‌بندی سیم در §2.1: داده قابل امضا از **int32 4 بایتی** برای `Type`، `Length`، `Ttl`، و `Priority` استفاده می‌کند، در حالی که سیم به ترتیب از 1 بایت / 2 بایت / 4 بایت / 1 بایت استفاده می‌کند. این عمدی است — فرم قابل امضا در بین زبان‌ها قابل حمل است و از فیلدهای عرض ثابت استفاده می‌کند؛ فرم سیم برای اقتصاد BLE PDU فشرده است. پیاده‌سازی‌ها باید `Priority` را به `[0,255]` محدود کنند قبل از کدگذاری در بایت‌های قابل امضا، وگرنه گیرنده (که بایت سیم 0..255 را می‌بیند) یک بافر قابل امضای متفاوت می‌گیرد و تأیید شکست می‌خورد.

پیاده‌سازی مرجع در `src/Aether.Security/Services/PacketSigningService.cs::BuildSignableData` قرار دارد و خواندنی الزامی برای پورت کردن است.

### ۲.۵. انواع بسته

| مقدار | نام | جهت | توضیح |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | درخواست مسیر AODV |
| 2     | RouteReply        | Unicast       | پاسخ مسیر AODV (باید توسط مقصد امضا شود) |
| 3     | Data              | Unicast       | داده برنامه |
| 4     | Ack               | Unicast       | تأییدیه تحویل |
| 5     | SosBroadcast      | Flood         | پخش اضطراری (بخش ۸ را ببینید) |
| 6     | SosAck            | Unicast       | تأییدیه SOS |
| 7     | ChannelMessage    | Multicast     | پیام کانال گروهی |
| 8     | ChunkRequest      | Unicast       | درخواست تکه محتوای P2P |
| 9     | ChunkData         | Unicast       | پاسخ تکه محتوای P2P |
| 10    | Heartbeat         | Broadcast     | سیگنال زنده بودن دوره‌ای |
| 11    | StreamAnnounce    | Broadcast     | آگهی پخش زنده |
| 12    | StreamSegment     | Unicast/Tree  | بخش رسانه پخش زنده |
| 13    | StreamSubscribe   | Unicast       | درخواست پیوستن به درخت relay پخش |
| 14    | StreamUnsubscribe | Unicast       | خروج از درخت relay پخش |
| 15    | VoicePtt          | Unicast       | فریم صوتی push-to-talk |
| 16    | VoiceCall         | Unicast       | فریم تماس صوتی بلادرنگ |
| 17    | VoiceSignaling    | Unicast       | راه‌اندازی/خاتمه تماس صوتی |
| 18    | DtnBundle         | Unicast       | بسته DTN ذخیره و ارسال (بخش ۹ را ببینید) |
| 19    | DtnCustodyAck     | Unicast       | تأییدیه انتقال نگهداری DTN |
| 20    | DtnDeliveryReceipt| Unicast       | تأییدیه تحویل سرتاسر DTN |
| 21    | PresenceBeacon    | Broadcast     | اعلان حضور و در دسترس بودن |
| 22    | PresenceQuery     | Unicast       | درخواست وضعیت حضور |
| 23    | ProfileSync       | Unicast       | همگام‌سازی متادیتای پروفایل |
| 24    | TipPacket         | Unicast       | انعام دادن به گره (تسویه از طریق LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | درخواست بسته pre-key همتا |
| 26    | PreKeyResponse    | Unicast       | تحویل بسته pre-key |
| 27    | VideoCall         | Unicast       | فریم ویدیو رمزنگاری شده (NAL unit H.264/H.265/VP8) |
| 28    | VideoSignaling    | Unicast       | راه‌اندازی تماس ویدیو: پیشنهاد، پاسخ، رد، خداحافظی، مذاکره کدک |
| 29    | WatchSync         | Unicast       | دستور پخش همگام‌شده: پخش، توقف، جستجو، سرعت |
| 30    | WatchReaction     | Multicast     | واکنش emoji یا صوتی دارای timestamp در طول تماشا با هم |
| 31    | VideoFrame        | Unicast/SFU   | فریم ویدیو گروهی (SFU relay به شرکت‌کنندگان توزیع می‌کند) |
| 32    | ScreenShare       | Unicast       | فریم اشتراک‌گذاری صفحه (همان pipeline ویدیو، به طور جداگانه علامت‌گذاری شده) |
| 33    | WatchChunkRequest | Unicast       | درخواست تکه اولویت‌دار با تعصب به موقعیت پخش |
| 34    | TorrentMetadata   | Multicast     | تبادل متادیتای فایل .torrent یا لینک magnet BitTorrent |

### ۲.۶. قابلیت‌های گره

گره‌ها قابلیت‌های خود را به عنوان یک bitfield آگهی می‌دهند:

| بیت | مقدار | قابلیت | توضیح |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | انتقال Bluetooth Low Energy در دسترس |
| 1   | 2     | WifiDirect  | انتقال Wi-Fi Direct در دسترس |
| 2   | 4     | Gateway     | دروازه اینترنت (مشبک را به شبکه IP پل می‌زند) |
| 3   | 8     | Relay       | آماده relay بسته برای دیگران |
| 4   | 16    | Sos         | قادر به پخش SOS |
| 5   | 32    | Streaming   | قادر به relay پخش زنده |
| 6   | 64    | Voice       | قادر به relay تماس صوتی |
| 7   | 128   | DtnCarrier  | حامل ذخیره و ارسال DTN |
| 8   | 256   | NearLink    | انتقال NearLink در دسترس |
| 9   | 512   | Video       | قادر به رمزنگاری/رمزگشایی ویدیو |

---

## ۳. الگوریتم مسیریابی

Aether از یک پروتکل مسیریابی واکنشی بر اساس مسیریابی Ad-hoc On-demand Distance Vector (AODV) استفاده می‌کند که با احراز هویت مسیر رمزنگاری شده و انتخاب مسیر وزن‌دهی شده QoS گسترش یافته است.

### ۳.۱. درخواست مسیر (RREQ)

وقتی یک گره نیاز به ارسال بسته به مقصدی دارد که برای آن مسیری ندارد، یک درخواست مسیر را آغاز می‌کند:

1. گره مبدا یک `MeshPacket` با `Type = RouteRequest` ایجاد می‌کند، `SourceUhid` را روی خودش و `DestinationUhid` را روی هدف تنظیم می‌کند، و `TTL = 7` (پیش‌فرض) را تنظیم می‌کند.
2. بسته به تمام همتایان متصل مستقیم broadcast می‌شود.
3. هر گره میانی که یک RREQ دریافت می‌کند:
   a. بررسی می‌کند آیا این RREQ را قبلاً دیده توسط `Id` بسته. اگر چنین است، بسته را بی‌صدا رها می‌کند (حذف تکراری). حافظه پنهان حذف تکراری تا `DeduplicationCacheSize` (پیش‌فرض 10,000) ورودی نگه می‌دارد و وقتی ظرفیت رسید کاملاً پاک می‌شود.
   b. یک **مسیر معکوس** به مبدا RREQ نصب می‌کند. مسیر معکوس UHID همتایی را که از آن RREQ دریافت شده به عنوان گام بعدی ثبت می‌کند. تعداد گام‌ها از `DefaultTtl - packet.Ttl + 1` محاسبه می‌شود.
   c. اگر خودش مقصد است، یک RREP تولید می‌کند (§3.2 را ببینید).
   d. اگر یک مسیر معتبر موجود به مقصد دارد، ممکن است از طرف مقصد یک RREP تولید کند.
   e. در غیر این صورت، TTL را کاهش می‌دهد و RREQ را دوباره broadcast می‌کند.
4. مبدا با یک تایم‌اوت **5,000 ms** (`RouteTimeoutMs`) منتظر RREP می‌ماند. اگر RREP نرسید، کشف مسیر شکست می‌خورد.

### ۳.۲. پاسخ مسیر (RREP)

وقتی مقصد (یا یک گره میانی با یک مسیر معتبر) یک پاسخ مسیر تولید می‌کند:

1. یک `MeshPacket` با `Type = RouteReply` ایجاد می‌شود، با `SourceUhid` تنظیم شده روی گره مقصد و `DestinationUhid` تنظیم شده روی مبدا RREQ.
2. **الزام امنیتی:** RREP باید توسط کلید هویت Ed25519 گره مقصد امضا شود. امضا داده قابل امضای استاندارد را پوشش می‌دهد (§2.3). این از مسموم کردن مسیر توسط گره‌های میانی مخرب جلوگیری می‌کند.
3. RREP از طریق مسیر معکوس نصب شده در طول انتشار RREQ به صورت unicast برمی‌گردد.
4. هر گره میانی که RREP را ارسال می‌کند:
   a. امضای RREP را در برابر کلید عمومی منبع ادعا شده تأیید می‌کند (اگر شناخته شده باشد). اگر تأیید شکست بخورد، RREP رها شده و یک هشدار لاگ می‌شود.
   b. یک **مسیر رو به جلو** به منبع RREP (گره مقصد) با فرستنده RREP به عنوان گام بعدی نصب می‌کند.
   c. TTL را کاهش می‌دهد و به سمت مبدا RREQ ارسال می‌کند.
5. وقتی RREP به مبدا می‌رسد، درخواست مسیر در انتظار (از طریق `TaskCompletionSource` پیگیری شده) با مسیر نصب شده حل می‌شود.

### ۳.۳. نگهداری مسیر

- **انقضا بر اساس TTL:** هر ورودی مسیر یک timestamp `ExpiresAt` تنظیم شده روی `now + 300 seconds` (`RouteExpirySeconds`) حمل می‌کند. مسیرها به طور ضمنی تجدید نمی‌شوند؛ پس از انقضا باید از طریق یک چرخه RREQ/RREP جدید برقرار شوند.
- **هرس دوره‌ای:** سرویس پروتکل یک heartbeat دوره‌ای (پیش‌فرض هر 300 ثانیه) اجرا می‌کند. در طول هر چرخه، مسیرهای منقضی شده را از هر دو `ConcurrentDictionary` در حافظه و فروشگاه پشتیبان SQLite حذف می‌کند.
- **هرس dedup RREQ:** مجموعه IDهای RREQ دیده شده وقتی از `DeduplicationCacheSize` (پیش‌فرض 10,000) ورودی تجاوز کرد، پاک می‌شود.

### ۳.۴. کیفیت مسیر و QoS

هر `RouteEntry` یک `QualityScore` در محدوده [0, 100] حمل می‌کند که برای مسیرهای تازه کشف شده روی 50 مقداردهی می‌شود. امتیاز موارد زیر را در نظر می‌گیرد:

- **تعداد گام:** تعداد گام کمتر عموماً نشان‌دهنده مسیر سریع‌تر است.
- **تأخیر:** زمان رفت و برگشت اندازه‌گیری شده وقتی در دسترس باشد.
- **قابلیت اطمینان همتا:** امتیاز قابلیت اطمینان همتای گام بعدی (§3.5 را ببینید).

گره‌هایی که در سیستم انگیزه انعام شرکت می‌کنند تقویت QoS در امتیاز کیفیت مسیر خود دریافت می‌کنند. این یک ترجیح نرم است: کسانی که انعام نمی‌دهند همیشه سرویس دریافت می‌کنند، اما کسانی که انعام می‌دهند ممکن است انتخاب مسیر کمی بهتری تجربه کنند. سطوح تقویت عبارتند از:

| سطح | آستانه ثبات | تقویت QoS |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### ۳.۵. امتیاز قابلیت اطمینان همتا

هر همتای شناخته شده یک امتیاز قابلیت اطمینان در محدوده [0, 100] دارد که روی 50 (`DefaultReliabilityScore`) مقداردهی می‌شود. امتیاز بر اساس رفتار مشاهده شده تنظیم می‌شود:

| رویداد | تغییر |
|----------------------|-------|
| relay موفق     | +2    |
| relay ناموفق         | -5    |
| relay SOS            | +5    |
| تکه ارائه شده       | +1    |
| خرابی ارائه تکه  | -10   |

امتیازهای قابلیت اطمینان در SQLite ذخیره می‌شوند و در هنگام راه‌اندازی به حافظه بارگذاری می‌شوند. امتیاز بر انتخاب مسیر تأثیر می‌گذارد: مسیرهایی از طریق همتایان قابل اطمینان‌تر ترجیح داده می‌شوند.

---

## ۴. تبادل کلید

> در 2026-05-05 با پیاده‌سازی مرجع C# در `src/Aether.Security/Services/SignalProtocolService.cs` و مجموعه fixture چند زبانه در `fixtures/signal/` تطبیق یافت. مرجع C# X3DH کامل + Double Ratchet (Signal §3 + §5) روی X25519 را ارسال می‌کند. Go، Python، TypeScript، Rust، Swift، و Kotlin به همان envelope پورت شده‌اند و در سطح fixture X3DH و KDF_RK هم‌ارز بایت هستند. C فقط اولیه‌های X25519 + KDF_RK + symmetric-ratchet را ارسال می‌کند — برای تأییدکننده fixture کافی است، هنوز بدون ماشین‌آلات کامل session. در هر جایی که این بخش با کد اختلاف دارد، کد معتبر است؛ یک issue در `OPEN_ISSUES.md` ثبت کنید.

Aether **X3DH** (Extended Triple Diffie-Hellman، Signal §3) را برای برقراری session ناهمزمان پیاده‌سازی می‌کند، که بلافاصله توسط **Signal Double Ratchet** (Signal §5) برای محرمانگی رو به جلو و امنیت پس از به خطر افتادن دنبال می‌شود. تمام رمزنگاری session روی Curve25519 اجرا می‌شود: **X25519** (RFC 7748) برای ECDH و **Ed25519** (RFC 8032) برای امضا.

### ۴.۱. کلیدهای هویت

هر گره در اولین راه‌اندازی **دو** جفت کلید بلندمدت تولید می‌کند (بدون XEdDSA؛ ترتیب dual-key ساده‌تر آن چیزی است که هر پیاده‌سازی ارسال می‌کند):

- **جفت کلید Ed25519** — seed 32 بایتی (خصوصی)، کلید عمومی 32 بایتی. برای امضای بسته (§2.4)، `SignedPreKeySignature` (§4.3)، احراز هویت RREP (§3.2)، و امضای انعام استفاده می‌شود.
- **جفت کلید X25519** — کلیدهای خصوصی و عمومی خام 32 بایتی. برای چهار عملیات DH در X3DH (§4.4) استفاده می‌شود.

مرجع: `SignalProtocolService.InitializeIdentityKeys`. کلیدهای خصوصی فقط روی دستگاه هستند؛ کلیدهای عمومی در `PreKeyBundle` منتشر می‌شوند.

یک پنجره مهاجرت ۳۰ روزه P-256 → Ed25519 فقط برای *تأیید امضا* روی بسته‌های ورودی رعایت می‌شود — §7.5 را ببینید. خود بسته‌های pre-key در سیم فقط X25519 هستند.

### ۴.۲. انتخاب Curve

X3DH و Double Ratchet به طور انحصاری از **X25519** استفاده می‌کنند. P-256 در برقراری session توسط هیچ پیاده‌سازی فعلی استفاده نمی‌شود. پیش‌نویس قبلی این مشخصه P-256 ECDH را توصیف می‌کرد؛ آن متن مقدم بر انتقال خانواده‌گستر 2026-05-05 به X25519 است و دیگر دقیق نیست.

### ۴.۳. بسته Pre-Key

یک بسته pre-key منتشر می‌شود تا یک initiator بتواند session را بدون آنلاین بودن responder برقرار کند (Signal §3.4):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

مرجع: `Aether.Security.Models.PreKeyBundle`. قرارداد wire-shape در همه ۸ زبان یکسان است.

**استخر کلید یک‌بار مصرف (OPK).** هر responder یک استخر از `OpkPoolSize` (پیش‌فرض 100، منعکس‌کننده راهنمایی منتشر شده Signal) X25519 OPK نگه می‌دارد. تولید bundle id بعدی-استفاده نشده را از یک صف FIFO بیرون می‌کشد، سپس استخر را به اندازه هدفش بالا می‌آورد. هر OPK دقیقاً یک بار مصرف می‌شود: responder نیمه خصوصی را در اولین پیام PreKey که به id آن اشاره می‌کند، حذف و صفر می‌کند. initiatorهای همزمان که برای یک id OPK مشابه رقابت می‌کنند دقیقاً یک `EstablishResponderSession` موفق را تحت `_preKeyLock` خواهند دید؛ بازنده `CryptographicException` ایجاد می‌کند.

مرجع: `SignalProtocolService.TopUpOpkPoolNoLock` (خطوط 494–518)، `SignalProtocolService.EstablishResponderSession` (خطوط 636–718). معناشناسی استخر توسط `tests/Aether.Core.Tests/PreKeyPoolTests.cs` آزمایش می‌شود.

**چرخش کلید pre-key امضاشده (SPK).** SPK در اولین فراخوانی bundle به صورت تنبل تولید می‌شود و در فراخوانی‌های بعدی دوباره استفاده می‌شود تا initiatorهای همزمان که bundle‌ها را قبل از اجرای X3DH دریافت می‌کنند bundle‌های یکدیگر را باطل نکنند. چرخش دوره‌ای SPK (Signal §3.3 چرخش هفتگی را توصیه می‌کند) یک عملیات صریح است، نه یک اثر جانبی تولید bundle.

id های pre-key از `RandomNumberGenerator.GetInt32(1, int.MaxValue)` با تلاش مجدد صریح برای برخورد کشیده می‌شوند (تا ۶۴ تلاش قبل از ایجاد خطا).

### ۴.۴. برقراری Session (X3DH)

X3DH کامل (Signal §3.3) روی طرف initiator اجرا می‌شود. چهار عملیات DH روی X25519 محاسبه می‌شوند:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

که در آن `IK_A` / `IK_B` کلیدهای هویت X25519 هستند، `EK_A` یک ephemeral X25519 تازه فقط برای این session است، `SPK_B` کلید pre-key امضاشده responder است، و `OPK_B` کلید pre-key یک‌بار مصرف responder است. کلید root اولیه است:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

ثابت `info` یعنی `aether-x3dh-root-v1` در همه پیاده‌سازی‌ها یکسان است و توسط `fixtures/signal/expected/x3dh_basic.json` (فیلد `root_key_hex`) پین شده است.

مرجع: `SignalProtocolService.ProcessPreKeyBundleAsync` (خطوط 554–626). مسیر تأیید: مورد `x3dh_basic` در `fixtures/signal/inputs.json` → `fixtures/signal/expected/x3dh_basic.json`.

**تأیید bundle.** قبل از اجرای هر DH، initiator `SignedPreKeySignature` را در برابر `IdentityKey` با استفاده از Ed25519 تأیید می‌کند. یک تأیید ناموفق `CryptographicException` ایجاد می‌کند و bundle رها می‌شود. اندازه‌های کلید عمومی در برابر `X25519Service.PublicKeySize` (32) تأیید می‌شوند؛ bundle‌های بدشکل رد می‌شوند.

**آماده‌سازی session.** در پایان `ProcessPreKeyBundleAsync` یک `SignalSession` با موارد زیر ایجاد می‌شود:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — یکپارچه‌سازی قانونی Signal X3DH ↔ Double-Ratchet: ephemeral X3DH initiator اولین جفت کلید DH-ratchet (`DHs`) می‌شود.
- `RemoteEphemeralPub = SPK_B` — کلید pre-key امضاشده responder به عنوان کلید ratchet همتای اولیه (`DHr`) در نظر گرفته می‌شود.
- `SendChainKey = null`، `RecvChainKey = null` — هر دو کلید زنجیر به صورت تنبل در اولین ارسال / اولین دریافت DH-ratchet مشتق می‌شوند.
- `PendingPreKeyMessage = true` — نشان می‌دهد که فراخوانی بعدی `EncryptAsync` خروجی باید یک پیام PreKey (`MessageType=1`) منتشر کند.

تمام خروجی‌های DH و secret مشترک الحاق شده در بلوک `finally` از طریق `CryptographicOperations.ZeroMemory` صفر می‌شوند.

**رد ارسال ناامن.** اگر `EncryptAsync` برای یک همتا بدون session فراخوانی شود، `InvalidOperationException` پرتاب می‌شود. هیچ مسیر fallback مشتق شده از UHID وجود ندارد. انتظار می‌رود میزبان‌ها پیام را صف‌بندی کنند (به `MessagingService` + `SignalMessageEnvelopeCipher` مراجعه کنید) و پس از تکمیل برقراری session دوباره تلاش کنند.

### ۴.۵. Double Ratchet (Signal §5)

هر طرف یک جفت کلید X25519 ratchet در حال چرخش (`DHs`) و یک کپی از آخرین کلید عمومی ratchet آخرین بار دیده شده همتا (`DHr`) نگه می‌دارد. در هر پیام فرستنده `DHs` عمومی فعلی خود را منتشر می‌کند؛ هر بار که گیرنده یک `DHr` جدید می‌بیند، یک **گام DH-ratchet** اجرا می‌کند که زنجیر را از طریق `KDF_RK(RK, DH(myDHs, newDHr))` مجدداً کلیدگذاری می‌کند — هم کلید root و هم یک کلید زنجیر تازه را مجدداً مشتق می‌کند.

#### ۴.۵.۱. KDF_RK

`KDF_RK` یک HKDF-SHA256 روی یک بلوک ۶۴ بایتی است که به صورت 32+32 به کلید root جدید و کلید زنجیر جدید تقسیم می‌شود:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

مرجع: `SignalProtocolService.KdfRk` (خطوط 857–868). پین شده توسط مورد `kdf_rk_basic` در `fixtures/signal/inputs.json` → `fixtures/signal/expected/kdf_rk_basic.json`.

#### ۴.۵.۲. Ratchet متقارن

مطابق Signal §5.1، کلیدهای پیام و کلیدهای زنجیر از یک کلید زنجیر با استفاده از HMAC-SHA256 با جداسازی دامنه تک‌بایتی مشتق می‌شوند:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

مرجع: `SignalProtocolService.RatchetChainKey` (خطوط 876–881). پین شده توسط موارد `ratchet_step_basic` و `ratchet_step_three_iterations` در `fixtures/signal/inputs.json`.

پیش‌نویس قبلی این مشخصه `messageKey = HMAC-SHA256(chain_key, counter_bytes)` و یک `chain_key advance via HMAC(chain_key, 0x01)` جداگانه توصیف می‌کرد. این غیر-Signal بود و هرگز پیاده‌سازی نشد؛ با تقسیم قانونی 0x01/0x02 جایگزین شده است.

#### ۴.۵.۳. گام DH-Ratchet هنگام دریافت

وقتی `SenderEphemeralKeyX25519` پیام ورودی با `RemoteEphemeralPub` ذخیره شده (مقایسه زمان ثابت) متفاوت باشد، فعال می‌شود.

1. شمارنده خروجی را به عنوان `PreviousChainCount` ذخیره کنید (Signal §5: PN) تا همتا بتواند کلیدهای رد شده را در مرز محاسبه کند.
2. `SendCounter` و `RecvCounter` را به 0 بازنشانی کنید؛ `RemoteEphemeralPub` جدید را نصب کنید.
3. زنجیر دریافتی جدید را مشتق کنید: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. خصوصی قدیمی `myDHs` را صفر کنید؛ یک جفت کلید X25519 تازه تولید کنید.
5. زنجیر ارسالی جدید را مشتق کنید: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

مرجع: `SignalProtocolService.DhRatchetReceive` (خطوط 726–772).

#### ۴.۵.۴. مشتق‌سازی تنبل زنجیر ارسال

اولین ارسال initiator یک **نیم‌گام** به جای یک DH-ratchet کامل اجرا می‌کند — X3DH قبلاً `DHs` و `DHr` را قرار داده، بنابراین فقط زنجیر ارسال نیاز به مشتق‌سازی دارد:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` *اینجا چرخش نمی‌کند*. فقط در یک گام DH-ratchet طرف دریافت واقعی چرخش می‌کند.

مرجع: `SignalProtocolService.DhRatchetSendOnly` (خطوط 780–796).

#### ۴.۵.۵. کلیدهای پیام رد شده

وقتی پیام‌ها خارج از ترتیب می‌رسند، کلید پیام هر شمارنده رد شده در `SkippedMessageKeys` ذخیره می‌شود، کلیدگذاری شده توسط `(Hex(remoteEphPub):counter)`. پیوند کلید عمومی از راه دور ضروری است — پیام‌های خارج از ترتیب از یک زنجیر قبلی (متفاوت `DHr`) هنوز می‌توانند بعد از یک گام DH-ratchet برسند و به مجموعه کلید per-chain خودشان نیاز دارند.

محدودیت‌ها:

- رد کردن بیش از `MaxSkippedKeys` (1000) ورودی در یک شکاف `CryptographicException` ایجاد می‌کند و برقراری مجدد session را مجبور می‌کند.
- عبور از مرز DH-ratchet، گیرنده ابتدا تا `PreviousChainCount` کلید را روی زنجیر *قدیمی* رد می‌کند، سپس گام DH-ratchet را قبل از مشتق‌سازی کلیدها روی زنجیر جدید اجرا می‌کند.

مرجع: `SignalProtocolService.SkipMessageKeys` (خطوط 804–830) و حلقه skip در رمزگشایی (خطوط 366–388).

### ۴.۶. فرمت Payload رمزنگاری شده

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

مرجع: `Aether.Security.Models.EncryptedPayload` (خطوط 55–66 از `SecurityModels.cs`). فیلد `InitiatorEphemeralKeyX25519` یک alias سازگاری به عقب برای envelope سیم قبل از Double-Ratchet است و با `SenderEphemeralKeyX25519` در پیام‌های PreKey برابر است؛ مصرف‌کنندگان جدید باید آن را نادیده بگیرند.

پارامترهای AES-GCM: کلید ۲۵۶ بیتی، nonce 96 بیتی (`AesNonceSize = 12`)، tag 128 بیتی (`AesTagSize = 16`)، tag به ciphertext الحاق می‌شود. کلیدهای پیام بلافاصله پس از رمزنگاری/رمزگشایی AES-GCM در بلوک‌های `finally` صفر می‌شوند.

### ۴.۷. وضعیت هر زبان

| زبان | X3DH (4 DH) | Double Ratchet | استخر OPK | تأیید fixture |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | کامل         | کامل (§5)      | استخر، پیش‌فرض 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | فقط اولیه‌ها — `aether_x25519_*`, `aether_signal_kdf_rk` | پیاده‌سازی نشده | — | فقط kdf_rk_basic |

همه ۷ زبان قادر به session (C# + Go + TypeScript + Python + Kotlin + Swift + Rust) استخر OPK FIFO با 100 کلید با بالاآوردن تنبل و مصرف محافظت‌شده با قفل را ارسال می‌کنند، که قرارداد مرجع C# را مطابقت می‌دهند. C فقط اولیه‌ها را ارسال می‌کند؛ ماشین‌آلات کامل session در آیتم 11 `OPEN_ISSUES.md` پیگیری می‌شود.

---

## ۵. الزامات لایه انتقال

Aether مستقل از انتقال است. هر کانال ارتباط فیزیکی که قرارداد `ITransportService` را رعایت کند می‌تواند در مشبک شرکت کند.

### ۵.۱. قرارداد رابط ITransportService

هر پیاده‌سازی انتقال باید موارد زیر را نمایان کند:

**ویژگی‌ها:**

| ویژگی | نوع | توضیح |
|--------------------|--------|-------------|
| `Name`             | string | شناسه خوانا توسط انسان (مثلاً "BLE"، "Wi-Fi Direct"، "NearLink") |
| `IsAvailable`      | bool   | آیا انتقال در حال حاضر روی این دستگاه قابل استفاده است |
| `MaxBandwidthBps`  | int64  | حداکثر throughput به بایت در ثانیه |
| `MaxRangeMeters`   | int32  | حداکثر برد ارتباطی به متر |
| `PowerCostRelative`| int32  | مصرف برق نسبی (1 = کم، 10 = زیاد) |
| `MaxConcurrentPeers` | int32 | حداکثر اتصالات همزمان همتا |

**متدها:**

| متد | امضا | توضیح |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | یک آرایه بایت به یک همتای مشخص ارسال کنید. در صورت موفقیت true برمی‌گرداند. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | یک stream به یک همتا ارسال کنید (برای انتقال‌های بزرگ، صوت، ویدیو). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | بررسی کنید آیا یک اتصال فعال به یک همتا وجود دارد. |

**رویدادها:**

| رویداد | امضا | توضیح |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | وقتی داده از یک همتا می‌رسد فعال می‌شود. |

### ۵.۲. الگوریتم انتخاب انتقال

`TransportManager` بهترین انتقال را برای هر بسته بر اساس موارد زیر انتخاب می‌کند:

1. **در دسترس بودن:** فقط انتقال‌هایی که `IsAvailable == true` است در نظر گرفته می‌شوند.
2. **اندازه payload:** اگر اندازه payload در یا زیر `BleMaxPayloadBytes` (1,024 بایت) باشد، BLE برای کارایی برق ترجیح داده می‌شود. payload های بزرگ‌تر Wi-Fi Direct را ترجیح می‌دهند.
3. **وزن‌دهی هزینه برق:** در بین انتقال‌های در دسترس، مقادیر `PowerCostRelative` کمتر برای ترافیک روتین ترجیح داده می‌شوند. بسته‌های با اولویت بالا (SOS، صوت) ممکن است این ترجیح را لغو کنند.
4. **اتصال همتا:** اگر یک انتقال قبلاً یک اتصال فعال به همتای هدف داشته باشد (`IsConnected` true برمی‌گرداند)، برای جلوگیری از هزینه‌های راه‌اندازی اتصال ترجیح داده می‌شود.
5. **Fallback:** اگر هیچ انتقال محلی نتواند به هدف برسد، بسته برای relay سرور از طریق AetherAPI صف‌بندی می‌شود.

### ۵.۳. انتقال‌های مرجع

| انتقال | حداکثر پهنای باند | حداکثر برد | هزینه برق | حداکثر همتا | یادداشت‌ها |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | کشف اصلی + بسته‌های کوچک |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | انتقال‌های بزرگ، پخش، صوت |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon، throughput بالا |

**محدودیت payload BLE:** بسته‌هایی که از 1,024 بایت (`BleMaxPayloadBytes`) تجاوز می‌کنند به طور خودکار به Wi-Fi Direct یا NearLink هدایت می‌شوند. BLE برای آگهی‌های کشف، بسته‌های کنترل کوچک (RREQ/RREP، beacon حضور)، و پیام‌رسانی با پهنای باند کم استفاده می‌شود.

تایم‌اوت اتصال **Wi-Fi Direct** 10,000 ms (`WifiDirectTimeoutMs`) است با حداکثر ۸ همتای همزمان (`MaxWifiDirectPeers`).

---

## ۶. پروتکل کشف

### ۶.۱. تبلیغ BLE

گره‌های Aether عمدتاً از طریق تبلیغ BLE یکدیگر را کشف می‌کنند. برای جلوگیری از ردیابی پایدار از طریق شناسه‌های ایستا، پروتکل از دو مکانیسم حریم خصوصی استفاده می‌کند: UUID سرویس در حال چرخش و کلیدهای حل‌کننده هویت.

**چرخه تبلیغ:** 2 ثانیه اسکن روشن، 8 ثانیه خاموش (`BleScanOnMs`/`BleScanOffMs`). فاصله تبلیغ 1,000 ms (`BleAdvertiseIntervalMs`) است. یک jitter تصادفی 0-2,000 ms (`BleScanJitterMaxMs`) به فاصله اسکن اضافه می‌شود تا تشخیص الگوی زمانی را جلوگیری کند.

**تایم‌اوت همتا:** یک همتا که در عرض 30 ثانیه مجدداً کشف نشود از دست رفته تلقی می‌شود (رویداد `PeerLost`).

### ۶.۲. UUID سرویس در حال چرخش

برای جلوگیری از اثرانگشت‌گذاری بلندمدت BLE، UUID سرویس مورد استفاده در تبلیغ‌ها هر 15 دقیقه چرخش می‌کند (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

`rotation_key` یک کلید ۳۲ بایتی است که یک بار در هر گره تولید می‌شود و در ذخیره‌سازی امن نگه داشته می‌شود. تمام گره‌های Aether با همان کلید چرخش برای یک پنجره زمانی مشخص UUID یکسانی مشتق می‌کنند، کشف متقابل را بدون آشکار کردن یک شناسه دائمی ممکن می‌سازد.

یک UUID fallback ایستا (`A3E7-1001-0001-0000-000000000000`) برای ۹۰ روز در طول انتقال از طرح غیر-چرخشی حفظ می‌شود.

### ۶.۳. کلید حل‌کننده هویت (IRK)

هر گره یک کلید حل‌کننده هویت (IRK) 128 بیتی ذخیره شده در ذخیره‌سازی امن تولید می‌کند. IRK در طول تبادل کلید با همتایان مورد اعتماد به اشتراک گذاشته می‌شود.

**تولید آدرس خصوصی قابل حل (RPA):**

1. محاسبه `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 بایت).
2. تنظیم دو بیت پراهمیت‌ترین `prand[0]` به `01` (پرچم RPA مطابق BLE spec).
3. محاسبه `hash = AES-128-ECB(IRK, pad(prand))` که `prand` بایت‌های 13-15 یک ورودی صفر-پد شده 16 بایتی را اشغال می‌کند.
4. ساختن RPA: `hash[0..2] || prand[0..2]` (مجموعاً 6 بایت).

**حل RPA:** یک گره که IRK یک همتا را دارد می‌تواند تأیید کند آیا یک RPA مشاهده شده متعلق به آن همتا است با محاسبه مجدد هش از مؤلفه `prand` RPA. زمان حل تقریباً O(N) است که N تعداد IRK های شناخته شده است، که برای 100 همتا ~0.1ms معیار است.

RPA در همان چرخه 15 دقیقه‌ای مانند UUID سرویس چرخش می‌کند.

### ۶.۴. مجاورت بر اساس Geohash

گره‌ها اختیاری موقعیت خود را به عنوان یک geohash کدگذاری می‌کنند. برای حریم خصوصی، geohash به 4 کاراکتر قطع می‌شود، که وضوح تقریبی ۳۹km x ۲۰km را فراهم می‌کند. این دقت برای موارد زیر کافی است:

- کشف کانال بر اساس مجاورت
- مسیریابی اپیدمیک DTN (تکرار به سمت ناحیه geohash آخرین شناخته شده گیرنده)
- زمینه جغرافیایی هشدار SOS

geohash با دقت کامل هرگز روی مشبک ارسال نمی‌شود. فقط فرم قطع شده به اشتراک گذاشته می‌شود، و فقط زمانی که سطح حریم خصوصی گره اجازه می‌دهد (`PrivacyLevel.Full` یا `PrivacyLevel.Partial`).

---

## ۷. مدل امنیتی

### ۷.۱. مدل تهدید

Aether قابلیت‌های دشمن زیر را فرض می‌کند:

- **استراق سمع غیرفعال:** دشمن می‌تواند تمام تبلیغ‌های BLE و ترافیک مشبک در محدوده رادیو را مشاهده کند.
- **تزریق فعال:** دشمن می‌تواند بسته‌ها را تزریق، تغییر، یا replay کند.
- **حمله Sybil:** دشمن می‌تواند چندین هویت جعلی گره ایجاد کند.
- **انکار سرویس انتخابی:** دشمن می‌تواند به طور انتخابی بسته‌ها را به عنوان یک گره relay رها کند.

### ۷.۲. آنچه محافظت می‌شود

| ویژگی | سطح محافظت | مکانیسم |
|----------|-----------------|-----------|
| محتوای پیام | محرمانگی کامل | AES-256-GCM با کلیدهای per-message (بخش ۴.۵) |
| هویت فرستنده | جزئی | UHID در هدرهای بسته قابل مشاهده است؛ آدرس BLE چرخش می‌کند (بخش ۶) |
| هویت گیرنده | جزئی | UHID مقصد در بسته‌های هدایت شده قابل مشاهده است؛ بسته‌های broadcast مقصد خالی دارند |
| متادیتای مسیریابی | حداقل | گره‌های میانی UHID منبع/مقصد و TTL را می‌بینند |
| ترتیب پیام | محافظت شده | شمارنده‌ها در ratchet متقارن از بازترتیب‌دهی جلوگیری می‌کنند |
| یکپارچگی پیام | کامل | امضای Ed25519 روی هر بسته (v2) |

### ۷.۳. مقاومت در برابر حمله

**حملات replay:**
هر بسته یک nonce تصادفی رمزنگاری ۸ بایتی و یک timestamp با دقت میلی‌ثانیه حمل می‌کند. گره‌های relay یک حافظه پنهان حذف تکراری از جفت‌های `(SenderUhid, NonceValue)` با TTL 5 دقیقه‌ای (`MaxPacketAgeSeconds = 300`) نگه می‌دارند. یک بسته با یک nonce تکراری از یک فرستنده مشابه رها می‌شود. بسته‌هایی با timestamp قدیمی‌تر از 5 دقیقه بدون توجه به nonce رد می‌شوند.

حافظه پنهان dedup nonce هر 60 ثانیه تمیز می‌شود. ورودی‌های منقضی شده (قدیمی‌تر از 5 دقیقه) حذف می‌شوند.

**حمله مرد میانی (MITM):**
- بسته‌های Route Reply باید یک امضای Ed25519 معتبر از گره مقصد ادعاشده حمل کنند. گره‌های میانی نمی‌توانند RREP جعل کنند چون کلید خصوصی مقصد را ندارند.
- بسته‌های pre-key شامل یک `SignedPreKeySignature` (Ed25519) روی `SignedPreKey` هستند که کلید ECDH موقت را به هویت بلندمدت گره می‌دهند.
- برقراری session (§4.4) session را از طریق مرحله تأیید pre-key به هویت هر دو طرف از نظر رمزنگاری گره می‌زند.

**حملات Sybil:**
- امتیاز قابلیت اطمینان هر گره در 50 شروع می‌شود و بر اساس رفتار مشاهده شده تنظیم می‌شود (§3.5). گره‌های Sybil تازه ایجاد شده هیچ اعتبار انباشته‌ای ندارند.
- گره‌های با امتیاز قابلیت اطمینان پایین (نزدیک به 0) در انتخاب مسیر اولویت‌بندی کمتری دارند.
- الگوریتم مسیریابی اپیدمیک DTN از مجاورت geohash و تاریخچه موفقیت relay برای انتخاب هدف‌های تکرار استفاده می‌کند، و جذب ترافیک توسط گره‌های Sybil بدون مشارکت relay واقعی را دشوارتر می‌کند.

**حملات سیل:**
- TTL در هر گام کاهش می‌یابد و بسته‌هایی با TTL = 0 رها می‌شوند. TTL پیش‌فرض ۷ شعاع انفجار هر broadcast را محدود می‌کند.
- حذف تکراری RREQ توسط ID بسته از تقویت از طریق طوفان broadcast جلوگیری می‌کند. حافظه پنهان dedup وقتی از `DeduplicationCacheSize` (پیش‌فرض 10,000) ورودی تجاوز کرد خالی می‌شود.
- پخش‌های SOS به ۳ در ساعت در هر گره محدود می‌شوند (بخش ۸).

### ۷.۴. صفر کردن کلید

تمام مواد رمزنگاری میانی بلافاصله پس از استفاده صفر می‌شوند:

- `sharedSecret` از توافق کلید ECDH: بعد از مشتق‌سازی HKDF صفر می‌شود.
- `messageKey` از ratchet زنجیر: بعد از رمزنگاری/رمزگشایی AES-GCM صفر می‌شود.
- `skippedKey` از رمزگشایی خارج از ترتیب: بعد از استفاده صفر شده و از نقشه حذف می‌شود.
- `RootKey`، `SendChainKey`، `RecvChainKey` مشتق شده: از زمینه برقراری صفر می‌شوند (session کپی‌های خود را نگه می‌دارد).

صفر کردن از `CryptographicOperations.ZeroMemory` استفاده می‌کند که تضمین می‌شود توسط کامپایلر بهینه‌سازی نمی‌شود.

### ۷.۵. مهاجرت P-256 به Ed25519

پروتکل از یک پنجره انتقال ۳۰ روزه از کلیدهای هویت ECDSA P-256 (نسخه پروتکل ۱) به Ed25519 (نسخه پروتکل ۲) پشتیبانی می‌کند:

1. بسته‌های نسخه پروتکل ۱ (بدون امضا) در طول دوره انتقال پذیرفته می‌شوند.
2. تأیید امضا ابتدا Ed25519 را امتحان می‌کند. اگر کلید عمومی بلندتر از ۳۲ بایت باشد (نشان‌دهنده یک کلید P-256 DER-رمزگذاری شده)، به تأیید P-256 ECDSA fallback می‌کند.
3. پس از پنجره ۳۰ روزه، بسته‌های نسخه پروتکل ۱ رد می‌شوند.
4. گره‌هایی که مهاجرت نکرده‌اند باید با یک هویت Ed25519 جدید مجدداً مقداردهی اولیه کنند.

### ۷.۶. آگاهی از قضایی

پروتکل سطوح قضایی را برای مدیریت الزامات قانونی متنوع در مورد رمزنگاری و شبکه مشبک تعریف می‌کند:

| سطح | رفتار | کشورهای نمونه |
|------|----------|-----------------------|
| 1    | آزادانه عمل کنید | آفریقای جنوبی، کنیا، غنا |
| 2    | عملکرد اصلاح شده | نیجریه، هند، اتحادیه اروپا، ایالات متحده، بریتانیا |
| 3    | فقط مشبک (خطر بالا) | چین، روسیه، ایران، امارات متحده عربی، میانمار |
| 4    | ناشناخته (پیش‌فرض فقط مشبک) | همه بقیه |

انتخاب سطح بر دسترسی ویژگی تأثیر می‌گذارد (مثلاً ویژگی‌های انعام/مالی ممکن است در سطح ۳ غیرفعال شوند) اما رمزنگاری را ضعیف نمی‌کند. رمزنگاری سرتاسر همیشه صرف نظر از قضای قانونی اعمال می‌شود.

---

## ۸. پخش SOS

مکانیسم SOS یک سیل اضطراری دو-مسیره است که برای موقعیت‌هایی طراحی شده که کاربر در خطر است و نیاز دارد به صورت همزمان به همتایان مشبک نزدیک و/یا اینترنت دسترسی داشته باشد.

### ۸.۱. پارامترهای پخش

| پارامتر | مقدار | توضیح |
|-----------|-------|-------------|
| TTL       | 15    | دو برابر پیش‌فرض عادی (7)، انتشار گسترده‌تر را تضمین می‌کند |
| Priority  | 999   | حداکثر اولویت؛ تمام ترافیک دیگر را در صف‌های relay پیش می‌اندازد |
| محدودیت نرخ | 3/ساعت | محدودیت per-node برای جلوگیری از سوءاستفاده |
| مقصد | خالی | broadcast به تمام همتایان (مقصد مشخص نیست) |

### ۸.۲. الگوریتم سیل

1. مبدا یک بسته SOS با `Type = SosBroadcast`، `TTL = 15`، `Priority = 999`، و یک `DestinationUhid` خالی ساختار می‌دهد.
2. payload به صورت JSON رمزگذاری شده است و شامل:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **ارسال دو-مسیره:** SOS به طور همزمان از طریق موارد زیر ارسال می‌شود:
   - **سیل مشبک:** از طریق تمام انتقال‌های در دسترس به تمام همتایان متصل broadcast می‌شود.
   - **فراخوانی API:** برای توزیع سمت سرور و پل‌زدن به PanikAPI (ارسال SMS/ایمیل) به AetherAPI ارسال می‌شود.
4. هر دو مسیر نسبت به یکدیگر fire-and-forget هستند. اگر فراخوانی API شکست بخورد، سیل مشبک به طور مستقل ادامه می‌دهد.

### ۸.۳. رفتار relay

وقتی یک گره یک بسته SOS دریافت می‌کند:

1. حذف تکراری را با `Id` بسته بررسی کنید. اگر قبلاً دیده شده، بی‌صدا رها کنید.
2. payload را deserialize کرده و رویداد `SosReceived` را برای UI محلی ایجاد کنید.
3. هشدار را به لیست هشدارهای فعال اضافه کنید.
4. اگر `TTL > 1`، TTL را کاهش دهید و **بدون توجه به وضعیت جدول مسیریابی به تمام همتایان re-broadcast کنید**. بسته‌های SOS مسیریابی عادی را دور می‌زنند — آن‌ها بدون قید و شرط سیل می‌شوند.

### ۸.۴. محدودیت نرخ

هر گره یک پنجره لغزان از timestamp‌های broadcast اخیر نگه می‌دارد. قبل از شروع یک SOS جدید:

1. ورودی‌های قدیمی‌تر از ۱ ساعت را از صف پاک کنید.
2. اگر صف ۳ یا بیشتر ورودی (`MaxSosBroadcastsPerHour`) داشت، broadcast رد می‌شود.
3. در صورت ارسال موفق، timestamp فعلی صف‌بندی می‌شود.

محدودیت نرخ فقط برای پخش‌های SOS مبدا اعمال می‌شود، نه برای relay کردن.

### ۸.۵. پل SOS-PanikAPI

پخش‌های SOS دریافت شده از طریق مشبک می‌توانند برای پاسخ اضطراری سنتی به PanikAPI ارسال شوند (SMS به مخاطبین، هشدارهای ایمیل). برعکس، جلسات اضطراری PanikAPI می‌توانند برای آگاهی جمعی به مشبک broadcast شوند. جلوگیری از حلقه با علامت‌گذاری منبع (`direct` در برابر `mesh_forward`) و یک پرچم `internet_forwarded` روی پخش‌های مشبک به دست می‌آید.

---

## ۹. ذخیره و ارسال DTN

زیرسیستم شبکه تحمل‌پذیر تأخیر (DTN) تحویل پیام را زمانی که هیچ مسیر سرتاسری بین فرستنده و گیرنده وجود ندارد ممکن می‌سازد. بسته‌ها روی گره‌های میانی ذخیره می‌شوند و با تغییر اتصال به طور فرصت‌طلبانه ارسال می‌شوند.

### ۹.۱. فرمت بسته

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### ۹.۲. چرخه عمر بسته

1. **ایجاد:** فرستنده یک بسته با یک payload رمزنگاری شده (رمزنگاری شده از طریق session Signal با گیرنده) ایجاد می‌کند. `Status = Pending`، `CopyCount = 1`.
2. **تلاش تحویل فوری:** فرستنده ابتدا مسیریابی مشبک مستقیم (RREQ/RREP) را امتحان می‌کند. اگر یک مسیر وجود داشت، بسته فوراً تحویل می‌شود و `Status` به `Delivered` انتقال می‌یابد.
3. **تلاش relay سرور:** اگر مسیریابی مشبک شکست بخورد، فرستنده سعی می‌کند از طریق AetherAPI relay کند. اگر سرور بتواند به گیرنده برسد (یا پیام را صف‌بندی کند)، تحویل موفق می‌شود.
4. **ذخیره و ارسال:** اگر هر دو مشبک و relay سرور شکست بخورند، بسته در ذخیره‌سازی محلی (`Pending` status) منتظر اسکن تحویل بعدی می‌ماند.

### ۹.۳. اسکن تحویل

یک اسکن دوره‌ای هر 60 ثانیه (`DtnScanIntervalSeconds`) اجرا می‌شود:

1. تمام بسته‌های در انتظار را از SQLite (منبع حقیقت) بارگذاری کنید.
2. برای هر بسته در انتظار:
   a. تلاش برای مسیر مشبک به گیرنده.
   b. تلاش برای relay سرور.
   c. اگر هر دو شکست خوردند و `CopyCount < MaxCopies`، تلاش برای تکرار اپیدمیک (§9.4).
3. بسته‌های منقضی شده (`ExpiresAt <= now`) را حذف کنید.

### ۹.۴. مسیریابی اپیدمیک

وقتی تحویل مستقیم و relay سرور هر دو شکست می‌خورند، بسته‌ها با استفاده از مسیریابی اپیدمیک به همتایان نزدیک تکرار می‌شوند:

1. `EpidemicRoutingService` هدف‌های تکرار را از لیست همتای فعلی انتخاب می‌کند.
2. انتخاب هدف در نظر می‌گیرد:
   - **مجاورت geohash:** همتایانی که geohash آن‌ها به آخرین geohash شناخته شده گیرنده نزدیک‌تر است ترجیح داده می‌شوند.
   - **تاریخچه relay:** همتایان با امتیاز قابلیت اطمینان بالاتر ترجیح داده می‌شوند.
   - **بودجه کپی:** تکرار متوقف می‌شود وقتی `CopyCount >= MaxCopies` (پیش‌فرض: 3).
3. هر تکرار یک بسته `DtnBundle` به همتای انتخاب شده می‌فرستد.
4. هنگام دریافت، سرویس DTN همتا `AcceptCustodyAsync` را فراخوانی می‌کند.

### ۹.۵. انتقال نگهداری

وقتی یک گره یک بسته DTN در نظر گرفته شده برای گره دیگر دریافت می‌کند:

1. **بررسی ظرفیت:** گره تعداد بسته فعلی خود را در برابر `DtnMaxBundlesPerNode` (50) بررسی می‌کند. اگر در ظرفیت باشد، نگهداری رد می‌شود.
2. **پذیرش:** وضعیت بسته روی `InCustody` تنظیم می‌شود، تعداد گام افزایش می‌یابد، و بسته در SQLite ذخیره می‌شود.
3. **ثبت نگهداری:** یک `CustodyRecord` که انتقال را مستند می‌کند (از، به، timestamp) ایجاد می‌شود.
4. **افزایش تعداد کپی:** `CopyCount` بسته در ذخیره‌سازی مستمر افزایش می‌یابد.
5. **تأیید:** یک بسته `DtnCustodyAck` با `Accepted = true` به گره انتقال‌دهنده ارسال می‌شود.
6. گره پذیرنده مسئولیت تلاش برای تحویل در اسکن‌های بعدی را بر عهده می‌گیرد.

### ۹.۶. رسید تحویل

وقتی گیرنده مقصد یک بسته DTN دریافت می‌کند:

1. وضعیت بسته به `Delivered` به‌روزرسانی می‌شود.
2. یک `DtnDeliveryReceipt` از طریق مسیریابی مشبک (با fallback relay سرور) به فرستنده اصلی ارسال می‌شود:
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. هنگام دریافت رسید، فرستنده بسته را از فروشگاه خود حذف کرده و رویداد `BundleDelivered` را فعال می‌کند.
4. رسید همچنین برای تحلیل با AetherAPI همگام‌سازی می‌شود.

### ۹.۷. انقضای بسته

- TTL پیش‌فرض بسته ۷۲ ساعت (`DtnBundleTtlHours`) است.
- بسته‌های منقضی شده در طول اسکن تحویل دوره‌ای تمیز می‌شوند.
- بسته‌های در وضعیت `Expired` یا `Delivered` از هم حافظه پنهان در حافظه و SQLite حذف می‌شوند.

### ۹.۸. محدودیت‌های ظرفیت

| پارامتر | پیش‌فرض | توضیح |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | حداکثر طول عمر بسته |
| `DtnMaxCopies`          | 3       | حداکثر کپی در هر بسته در سراسر شبکه |
| `DtnMaxBundlesPerNode`  | 50      | حداکثر بسته‌هایی که یک گره واحد حمل می‌کند |
| `DtnScanIntervalSeconds`| 60      | فرکانس اسکن تحویل |

---

## ۱۰. پخش ویدیو

> **وضعیت از 2026-05-05 — طراحی + اسکلت‌بندی C#، بدون pipeline کدک در حال ارسال.** انواع بسته `StreamAnnounce` (11)، `StreamSegment` (12)، `StreamSubscribe` (13)، `StreamUnsubscribe` (14)، `VideoCall` (27)، `VideoSignaling` (28)، `VideoFrame` (31)، `ScreenShare` (32) از نظر سیم تعریف شده‌اند و از طریق مجموعه fixture چند زبانه رفت و برگشت می‌کنند. ماژول C# `Aether.Streaming` رابط‌ها، مدل‌ها، و سرویس‌های اسکلتی (`StreamingService`، `VideoCallService`، `WatchTogetherService`) را ارسال می‌کند که درزهای مسیریابی/DI را سیم‌کشی می‌کنند و fan-out بخش unicast را می‌کنند — اما هیچ رمزنگاری/رمزگشایی ویدیوی واقعی به آن‌ها متصل نیست. ۷ زبان دیگر فقط انواع سیم دارند. سند طراحی رو به جلو در `docs/adaptive-secure-streaming-spec.md` معماری هدف است. نثر زیر را به عنوان مشخصه آنچه آن سرویس‌ها پیاده‌سازی خواهند کرد تلقی کنید؛ برای شکاف‌های آمادگی تولید به `OPEN_ISSUES.md` مراجعه کنید.

Aether از سه حالت ویدیو پشتیبانی می‌کند: تماس‌های ویدیویی peer-to-peer، ویدیوی گروهی (شرکت‌کنندگان نامحدود با توپولوژی پویا)، و پخش زنده. تمام فریم‌های ویدیو با Signal Protocol رمزنگاری شده و با Ed25519 امضا می‌شوند.

### ۱۰.۱. ماتریس قابلیت انتقال

قبل از شروع یک تماس ویدیویی، مبدا لایه انتقال را برای تعیین بهترین اتصال در دسترس به همتا query می‌کند. انتقال تعیین می‌کند چه کیفیتی از ویدیو امکان‌پذیر است:

| انتقال | پشتیبانی ویدیو | حداکثر وضوح | کدک توصیه شده | حداکثر bitrate | تماشا با هم |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | خیر (فقط صوت) | — | — | 64 Kbps | فقط بسته‌های sync |
| NearLink | سبک | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | کامل | 1080p | H.264 | 3000 Kbps | همه حالت‌ها |
| اینترنت | کامل | 720p | H.264 | 1500 Kbps | همه حالت‌ها |
| CircleLink | خیر (فقط صوت) | — | — | 64 Kbps | فقط بسته‌های sync |

اگر تنها انتقال در دسترس BLE یا CircleLink باشد، سرویس تماس ویدیو به طور خودکار به یک تماس صوتی تنزل می‌دهد.

### ۱۰.۲. کدک‌های ویدیو

| مقدار Enum | کدک | کاربرد |
|------------|-------|----------|
| 0 | H.264 | پیش‌فرض. پشتیبانی گسترده، فشرده‌سازی خوب. |
| 1 | H.265 | فشرده‌سازی بهتر. روی NearLink استفاده می‌شود (پهنای باند محدود). |
| 2 | VP8 | جایگزین بدون حق امتیاز. |

### ۱۰.۳. وضوح‌های ویدیو

| مقدار Enum | وضوح | Bitrate معمول |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### ۱۰.۴. جریان تماس ویدیو P2P

1. **بررسی قابلیت:** مبدا `GetVideoCapabilityAsync(peerUhid)` را برای تعیین بهترین انتقال، حداکثر وضوح، و کدک توصیه شده query می‌کند.
2. **پیشنهاد:** مبدا یک بسته `VideoSignaling` (نوع 28) با `SignalType = Offer`، شامل کدک ترجیحی، حداکثر وضوح، و حداکثر bitrate ارسال می‌کند.
3. **پاسخ/رد:** گیرنده با `SignalType = Answer` (کدک را به پایین‌ترین مخرج مشترک مذاکره می‌کند) یا `SignalType = Reject` پاسخ می‌دهد.
4. **تماس فعال:** هر دو گره بسته‌های `VideoCall` (نوع 27) حاوی NAL unit های H.264/H.265/VP8 را تبادل می‌کنند. هر فریم شامل یک شماره دنباله برای ترتیب‌دهی jitter buffer و یک پرچم keyframe است.
5. **اشتراک‌گذاری صفحه:** هر یک از طرفین می‌توانند اشتراک‌گذاری صفحه را تغییر دهند. `VideoSignaling` با `SignalType = ScreenShareStart/Stop` همتا را آگاه می‌کند. فریم‌های اشتراک صفحه از `PacketType.ScreenShare` (نوع 32) استفاده می‌کنند اما با همان pipeline پردازش.
6. **پایان تماس:** یکی از طرفین `VideoSignaling` با `SignalType = Bye` ارسال می‌کند.

تمام payloadهای سیگنالینگ و فریم با Signal Protocol (session X3DH) رمزنگاری می‌شوند. payload رمزنگاری شده به عنوان `EncryptedPayload` JSON-رمزگذاری شده درون فیلد `MeshPacket.Payload` سریال‌سازی می‌شود.

### ۱۰.۵. ماشین حالت تماس ویدیو

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

حالت‌ها: `Initiating(0)`، `Ringing(1)`، `Active(2)`، `OnHold(3)`، `Ended(4)`، `Failed(5)`، `Rejected(6)`.

### ۱۰.۶. ویدیوی گروهی

جلسات ویدیوی گروهی از شرکت‌کنندگان نامحدود پشتیبانی می‌کنند. توپولوژی بر اساس تعداد شرکت‌کننده به طور پویا انتخاب می‌شود:

- **FullMesh** (۲-۳ شرکت‌کننده): هر شرکت‌کننده یک stream به هر شرکت‌کننده دیگر ارسال می‌کند. ساده، تأخیر کم.
- **SFU** (۴+ شرکت‌کننده، آستانه: `SfuThresholdParticipants = 4`): یک گره به عنوان SFU relay انتخاب می‌شود. هر شرکت‌کننده یک stream به relay ارسال می‌کند که آن را به همه دیگران توزیع می‌کند. گره relay از طریق لایه انگیزه انعام دریافت می‌کند.

تغییر توپولوژی خودکار است: وقتی چهارمین شرکت‌کننده می‌پیوندد، session از FullMesh به SFU انتقال می‌یابد. وقتی شرکت‌کنندگان خارج می‌شوند و تعداد زیر ۴ کاهش می‌یابد، به حالت قبل برمی‌گردد.

فریم‌های ویدیوی گروهی از `PacketType.VideoFrame` (نوع 31) استفاده می‌کنند. در حالت SFU، فریم‌ها به UHID گره relay ارسال می‌شوند که آن‌ها را دوباره broadcast می‌کند.

### ۱۰.۷. Jitter Buffer

jitter buffer ویدیو به طور مستقل از jitter buffer صوتی (که فریم‌های Opus 20ms را مدیریت می‌کند) عمل می‌کند:

- **محدوده:** حداقل 60ms، حداکثر 500ms.
- **عمق تطبیقی:** jitter بین فریمی را از طریق میانگین متحرک نمایی (EMA) پیگیری می‌کند. عمق buffer = ۲× تخمین jitter، محدود به [60, 500] ms.
- **رها کردن آگاه از keyframe:** وقتی buffer سرریز می‌کند، فریم‌های غیر-keyframe (P/B) ابتدا رها می‌شوند. فریم‌های I (keyframe) هرگز رها نمی‌شوند — برای بازیابی decoder مورد نیاز هستند.
- **مدیریت شکاف:** وقتی یک شکاف دنباله تشخیص داده می‌شود، buffer به جای انتظار نامحدود به keyframe بعدی در دسترس می‌پرد.

### ۱۰.۸. انواع سیگنالینگ ویدیو

| مقدار Enum | نوع | توضیح |
|------------|------|-------------|
| 0 | Offer | شروع تماس ویدیو با ترجیح کدک/وضوح |
| 1 | Answer | پذیرش تماس با پارامترهای مذاکره شده |
| 2 | Reject | رد تماس |
| 3 | Bye | پایان تماس |
| 4 | Upgrade | درخواست کیفیت بالاتر (مثلاً انتقال بهبود یافته) |
| 5 | Downgrade | درخواست کیفیت پایین‌تر (مثلاً کاهش پهنای باند) |
| 6 | ScreenShareStart | همتا اشتراک‌گذاری صفحه را شروع کرد |
| 7 | ScreenShareStop | همتا اشتراک‌گذاری صفحه را متوقف کرد |

### ۱۰.۹. مدل رمزنگاری

| حالت | رمزنگاری | توزیع کلید |
|------|-----------|-----------------|
| تماس ویدیوی P2P | Signal Protocol در هر فریم | توافق کلید X3DH |
| ویدیوی گروهی | کلید کانال گروهی (AES-GCM) | توزیع شده از طریق Signal Protocol در زمان ایجاد session |
| اشتراک‌گذاری صفحه | مشابه حالت تماس والد | به ارث رسیده از session تماس ویدیو |

---

## ۱۱. تماشا با هم

> **وضعیت از 2026-05-05 — طراحی + اسکلت‌بندی C#، بلوغ یکسان با §10.** انواع بسته `WatchSync` (29)، `WatchReaction` (30)، `WatchChunkRequest` (33)، `TorrentMetadata` (34) از نظر سیم تعریف شده و با fixture آزمایش شده‌اند. `Aether.Streaming.WatchTogetherService` اسکلت هماهنگی (حالت session، انتشار دستور sync از طریق `IMeshSender`، کمک‌کننده‌های RTT-compensation) را فراهم می‌کند؛ دریافت BitTorrent، تسویه SDPKT ChipIn، و دریافت تکه از همتایان در هیچ زبانی پیاده‌سازی نشده. نثر زیر را به عنوان پروتکل هدف تلقی کنید؛ سند طراحی رو به جلو در `docs/adaptive-secure-streaming-spec.md` همین زمینه را با جزئیات بیشتر پوشش می‌دهد.

تماشا با هم پخش رسانه همگام‌شده را در یک گروه از همتایان مشبک ممکن می‌سازد. میزبان کنترل انحصاری پخش (پخش، توقف، جستجو، سرعت) دارد. دستورات sync شامل timestamp ساعت دیواری برای RTT compensation هستند.

### ۱۱.۱. حالت‌های تماشا

| مقدار Enum | حالت | جریان داده | الزام انتقال |
|------------|------|-----------|----------------------|
| 0 | SharedFile | فقط بسته‌های sync (هر کدام < 100 بایت) | هر (روی BLE کار می‌کند) |
| 1 | StreamFromHost | انتقال تکه P2P (از P2pContentService استفاده می‌کند) | WiFi Direct یا اینترنت |
| 2 | BitTorrent | مشبک + swarm خارجی از طریق گره‌های دروازه | WiFi Direct یا اینترنت |

### ۱۱.۲. حالت SharedFile

هر دو شرکت‌کننده فایل یکسانی دارند (مطابقت توسط هش محتوای SHA-256). فقط بسته‌های `WatchSync` تبادل می‌شوند. این کارآمدترین حالت از نظر پهنای باند است و روی BLE کار می‌کند.

1. میزبان یک watch session با `contentHash` (SHA-256 فایل) ایجاد می‌کند.
2. شرکت‌کنندگان می‌پیوندند و وقتی پخش‌کننده آن‌ها بارگذاری شد `IsReady = true` گزارش می‌دهند.
3. Session زمانی شروع می‌شود که همه شرکت‌کنندگان آماده گزارش دهند.
4. میزبان دستورات پخش/توقف/جستجو/سرعت را به عنوان بسته‌های `WatchSync` (نوع 29) ارسال می‌کند.
5. گیرندگان RTT compensation اعمال می‌کنند: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### ۱۱.۳. حالت StreamFromHost

فقط میزبان فایل را دارد. میزبان یک `ContentManifest` (با استفاده مجدد از سیستم محتوای P2P) تولید می‌کند و شرکت‌کنندگان تکه‌ها را از طریق مشبک دانلود می‌کنند.

- انتخاب تکه از استراتژی `SequentialFromPosition` استفاده می‌کند (نه `RarestFirst`): تکه‌های جلوتر از موقعیت پخش فعلی را اولویت‌بندی می‌کند، سپس برای seeding backfill می‌کند.
- هدف buffer: ۳۰ ثانیه جلوتر (`WatchTogetherBufferAheadSeconds`).
- توقف خودکار: اگر buffer هر شرکت‌کننده زیر ۱۰ ثانیه (`WatchTogetherMinBufferSeconds`) کاهش یابد، session به طور خودکار همه شرکت‌کنندگان را با یک دستور sync `BufferUnderrun` متوقف می‌کند. پخش زمانی از سر گرفته می‌شود که همه شرکت‌کنندگان buffer کافی داشته باشند (`BufferReady`).
- با دانلود تکه‌ها توسط بینندگان، آن‌ها به seeders برای بینندگان دیگر تبدیل می‌شوند (swarming به سبک BitTorrent در مشبک).

### ۱۱.۴. حالت BitTorrent

یک شرکت‌کننده یک فایل `.torrent` یا لینک magnet در چت گروهی به اشتراک می‌گذارد. بسته `TorrentMetadata` (نوع 34) اطلاعات torrent را به تمام شرکت‌کنندگان session توزیع می‌کند.

**پل مشبک به Swarm:**
- گره‌های دروازه (گره‌هایی با اینترنت) قطعات را از swarm BitTorrent خارجی دانلود می‌کنند.
- گره‌های دروازه قطعات دانلود شده را برای توزیع مشبک مجدداً رمزنگاری می‌کنند و به همتایان مشبک seed می‌کنند.
- همتایان مشبک بدون اینترنت قطعات را از گره‌های دروازه و از یکدیگر دریافت می‌کنند.
- موتور محتوای P2P بین مدل قطعه BitTorrent و مدل تکه Aether ترجمه می‌کند.

وقتی محتوای کافی buffer شد، پخش watch-together با استفاده از همان پروتکل sync مانند حالت SharedFile شروع می‌شود.

### ۱۱.۵. ماشین حالت Watch Session

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

حالت‌ها: `WaitingForReady(0)`، `Buffering(1)`، `Playing(2)`، `Paused(3)`، `Ended(4)`.

### ۱۱.۶. انواع دستور Sync

| مقدار Enum | نوع | توضیح |
|------------|------|-------------|
| 0 | Play | ازسرگیری پخش در موقعیت مشخص |
| 1 | Pause | توقف در موقعیت مشخص |
| 2 | Seek | پریدن به موقعیت مشخص |
| 3 | Speed | تغییر سرعت پخش |
| 4 | BufferUnderrun | توقف خودکار — buffer یک شرکت‌کننده به طور بحرانی کم است |
| 5 | BufferReady | ازسرگیری — همه شرکت‌کنندگان buffer کافی دارند |

### ۱۱.۷. RTT Compensation

دستورات Sync شامل یک فیلد `WallClockMs` (میلی‌ثانیه epoch یونیکس) هستند. وقتی یک گیرنده یک دستور sync پردازش می‌کند:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. برای دستورات Play و BufferReady: `adjustedPosition = commandPosition + networkDelay`
4. برای دستورات Pause و Seek: موقعیت دقیقاً اعمال می‌شود (نیاز به تنظیم نیست چون پخش متوقف/پرید).

این تضمین می‌کند همه شرکت‌کنندگان در نصف RTT شبکه همگام‌سازی شوند.

### ۱۱.۸. واکنش‌ها

شرکت‌کنندگان می‌توانند در طول پخش به محتوا واکنش نشان دهند:

- **واکنش‌های emoji:** بسته `WatchReaction` (نوع 30) با `Type = Emoji`، حامل رشته emoji و موقعیت رسانه در زمان واکنش.
- **نظرات صوتی:** بسته `WatchReaction` با `Type = VoiceComment`، حامل داده صوتی Opus-رمزگذاری شده (حداکثر ۱۰ ثانیه). داده صوتی در فیلد `VoiceData` واکنش گنجانده شده است.

واکنش‌ها به تمام شرکت‌کنندگان session broadcast می‌شوند. آن‌ها به موقعیت رسانه مهر زمان می‌خورند، و نمایش همگام‌شده با replay را ممکن می‌سازند.

### ۱۱.۹. ChipIn — تهیه محتوای گروهی

ChipIn اعضای گروه را قادر می‌سازد تا وجوه را (به ZAR، تسویه از طریق کیف پول‌های SDPKT از طریق LedgerAPI) برای تهیه دسته‌جمعی محتوا برای تماشای گروهی جمع‌آوری کنند.

**ماشین حالت:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

حالت‌ها: `Collecting(0)`، `Funded(1)`، `Purchasing(2)`، `Acquired(3)`، `Failed(4)`، `Refunded(5)`.

**جریان:**
1. آغازکننده یک `ChipInPool` با مبلغ هدف و توضیح محتوا ایجاد می‌کند.
2. شرکت‌کنندگان از طریق تراکنش‌های کیف پول SDPKT مبالغی مشارکت می‌کنند.
3. وقتی `CollectedAmount >= TargetAmount`، حالت به `Funded` انتقال می‌یابد.
4. سیستم محتوا را تهیه می‌کند (مثلاً دانلود BitTorrent را آغاز می‌کند).
5. وقتی محتوا در دسترس شد، حالت به `Acquired` انتقال می‌یابد و watch-together می‌تواند شروع شود.

هر مشارکت با یک ID تراکنش SDPKT برای مسیر حسابرسی ثبت می‌شود.

### ۱۱.۱۰. مدل رمزنگاری

| حالت | رمزنگاری | توزیع کلید |
|------|-----------|-----------------|
| دستورات sync تماشا | کلید کانال/مکالمه | session Signal Protocol موجود |
| تکه‌های محتوا (StreamFromHost) | کلید محتوا در هر manifest | توزیع شده از طریق Signal Protocol |
| قطعات BitTorrent | مجدداً رمزنگاری شده در زمان دریافت | دروازه cleartext را از swarm دانلود می‌کند، برای مشبک رمزنگاری می‌کند |
| واکنش‌های تماشا | کلید session | مشتق شده از کلید مکالمه |

### ۱۱.۱۱. پرچم‌های ویژگی

تمام ویژگی‌های ویدیو و تماشا با هم پشت پرچم‌های ویژگی قرار دارند (همه به طور پیش‌فرض غیرفعال):

| پرچم | والد | توضیح |
|------|--------|-------------|
| AETHER_VIDEO_CALL | AETHER_VOICE | تماس ویدیوی P2P و گروهی |
| AETHER_VIDEO_GROUP | AETHER_VIDEO_CALL | جلسات ویدیوی چند طرفه |
| AETHER_SCREEN_SHARE | AETHER_VIDEO_CALL | اشتراک‌گذاری صفحه در تماس‌های ویدیویی |
| AETHER_WATCH_TOGETHER | AETHER_CONTENT_P2P | پخش رسانه همگام‌شده |
| AETHER_WATCH_REACTIONS | AETHER_WATCH_TOGETHER | واکنش‌های emoji و صوتی |
| AETHER_TORRENT_INGEST | AETHER_CONTENT_P2P | پذیرش فایل BitTorrent برای توزیع مشبک |

پرچم‌های ویژگی وابستگی‌های والد دارند: یک پرچم فرزند فقط می‌تواند فعال شود اگر والدش نیز فعال باشد. این امکان rollout تدریجی را فراهم می‌کند.

---

## پیوست الف: مرجع ثوابت

تمام ثوابت پروتکل در `ProtocolConstants` تعریف شده‌اند و اینجا برای مرجع بازتولید شده‌اند:

### مسیریابی
| ثابت | مقدار |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### کشف BLE
| ثابت | مقدار |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### امنیت
| ثابت | مقدار |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| ثابت | مقدار |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| ثابت | مقدار |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### انتقال
| ثابت | مقدار |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Heartbeat
| ثابت | مقدار |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### حضور
| ثابت | مقدار |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### صوت
| ثابت | مقدار |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### پخش
| ثابت | مقدار |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### ویدیو
| ثابت | مقدار |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

---

## پیوست ب: واژه‌نامه

| اصطلاح | تعریف |
|------|------------|
| **UHID** | شناسه سخت‌افزار جهانی. یک رشته منحصر به فرد که یک گره مشبک را شناسایی می‌کند، مشتق شده از هویت دستگاه و کلیدهای رمزنگاری. |
| **RREQ** | درخواست مسیر. یک بسته broadcast که برای کشف یک مسیر به یک گره مقصد استفاده می‌شود. |
| **RREP** | پاسخ مسیر. یک بسته unicast که از طریق مسیر معکوس تأسیس شده توسط یک RREQ ارسال می‌شود. |
| **IRK** | کلید حل‌کننده هویت. یک کلید 128 بیتی که برای تولید و حل آدرس‌های خصوصی قابل حل BLE استفاده می‌شود. |
| **RPA** | آدرس خصوصی قابل حل. یک آدرس BLE 6 بایتی که به طور دوره‌ای چرخش می‌کند اما توسط همتایانی که IRK فرستنده را نگه می‌دارند قابل حل است. |
| **X3DH** | Diffie-Hellman سه‌گانه گسترش یافته. یک پروتکل توافق کلید که برقراری session ناهمزمان را ممکن می‌سازد. |
| **DTN** | شبکه تحمل‌پذیر تأخیر. یک پارادایم ذخیره و ارسال برای محیط‌هایی با اتصال متناوب. |
| **Gateway** | یک گره مشبک که اتصال اینترنتی دارد و ترافیک مشبک را به/از سرویس‌های مبتنی بر IP پل می‌زند. |
| **HKDF** | تابع مشتق‌سازی کلید مبتنی بر HMAC. برای مشتق‌سازی چندین کلید از یک secret مشترک واحد استفاده می‌شود. |
| **Pre-key bundle** | یک مجموعه کلید منتشر شده که به یک فرستنده اجازه می‌دهد یک session رمزنگاری شده را بدون آنلاین بودن گیرنده برقرار کند. |
| **SFU** | واحد ارسال انتخابی. یک گره relay که یک stream ویدیو از هر فرستنده دریافت می‌کند و آن را به تمام شرکت‌کنندگان دیگر توزیع می‌کند، و پهنای باند آپلود هر گره را کاهش می‌دهد. |
| **ChipIn** | مکانیسم تأمین مالی گروهی که شرکت‌کنندگان وجوه SDPKT را برای تهیه دسته‌جمعی محتوا برای تماشای گروهی جمع می‌کنند. |
| **NAL** | لایه انتزاع شبکه. فرمت کپسوله‌سازی که توسط کدک‌های H.264 و H.265 برای تقسیم‌بندی فریم‌های ویدیو استفاده می‌شود. |

---

## پیوست ج: منابع

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).

</div>
