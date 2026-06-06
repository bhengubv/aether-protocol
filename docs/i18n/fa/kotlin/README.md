<div dir="rtl">

# پروتکل Aether - پیاده‌سازی Kotlin

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](README.md) · [한국어](../../ko/kotlin/README.md)

یک پیاده‌سازی کامل و آماده برای محصول از پروتکل شبکه‌سازی مِش Aether به زبان Kotlin، با سازگاری کامل قالب سیمی بین‌زبانی با پیاده‌سازی مرجع C#.

## مرور کلی

Aether یک پروتکل شبکه‌سازی مِش غیرمتمرکز برای محیط‌هایی با اتصال اینترنتی متناوب یا فاقد اتصال است. این پیاده‌سازی Kotlin موارد زیر را فراهم می‌کند:

- **سازگاری قالب سیمی** با C# (سریال‌سازی بسته باینری دقیقاً مطابقت دارد)
- **امضای Ed25519** برای احراز هویت و یکپارچگی بسته
- **پروتکل سیگنال** برای رمزنگاری سرتاسری (توافق کلید X3DH، چرخ لنگر متقارن، AES-256-GCM)
- **توافق کلید ECDH P-256** برای برقراری جلسه
- **سریال‌سازی/سریال‌زدایی بسته** با اعداد صحیح چندبایتی little-endian
- **محافظت از بازپخش** با استفاده از حذف تکراری nonce
- **انتزاع حمل‌ونقل** برای BLE، Wi-Fi Direct و پیام‌رسانی درون‌فرایندی

## ساختار پروژه

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherMeshNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## ساخت

### پیش‌نیازها

- JDK 17 یا بالاتر
- Gradle 8.0 یا بالاتر

### کامپایل

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### اجرای نسخه نمایشی

```bash
./gradlew run
```

نسخه نمایشی موارد زیر را نشان می‌دهد:
1. تولید جفت کلید Ed25519
2. ایجاد و تبادل بسته کلید پیش‌درآمد
3. برقراری جلسه پروتکل سیگنال
4. امضای بسته با Ed25519
5. سریال‌سازی/سریال‌زدایی بسته
6. رمزنگاری و رمزگشایی پیام
7. محافظت از بازپخش
8. پیام‌رسانی حمل‌ونقل درون‌فرایندی

## اجزای کلیدی

### ۱. سریال‌سازی بسته (`PacketSerializer`)

قالب سیمی (little-endian):
- نسخه پروتکل (1 بایت)
- نوع بسته (1 بایت)
- شناسه بسته / UUID (16 بایت)
- اولویت (1 بایت)
- TTL (4 بایت، int32)
- TimestampMs (8 بایت، int64)
- SourceUhid (پیشوند طول 2 بایتی + بایت‌های UTF-8)
- DestinationUhid (پیشوند طول 2 بایتی + بایت‌های UTF-8)
- PacketNonce (پیشوند طول 2 بایتی + بایت‌ها)
- Payload (پیشوند طول 4 بایتی + بایت‌ها)
- Signature (پیشوند طول 2 بایتی + بایت‌ها)

کاملاً سازگار با `PacketSerializer` در C#.

### ۲. امضای Ed25519 (`Ed25519Service`، `PacketSigning`)

- **تولید کلید**: دانه کلید خصوصی 32 بایتی، کلید عمومی 32 بایتی
- **امضا**: امضاهای 64 بایتی روی داده‌های قابل امضای قطعی
- **تأیید**: جایگزین P-256 ECDSA در دوره انتقال
- **قالب داده قابل امضا**: دقیقاً با مشخصات C# مطابقت دارد (nonce بسته، مهر زمانی، نوع، UHIDها، هش محموله، TTL، اولویت)
- **محافظت از بازپخش**: حذف تکراری nonce با TTL 5 دقیقه‌ای

### ۳. پروتکل سیگنال (`SignalProtocol`)

توافق کلید X3DH با چرخ لنگر متقارن را پیاده‌سازی می‌کند:

**برقراری جلسه:**
- بسته کلید پیش‌درآمد همتا را دریافت می‌کند
- امضای بسته را با Ed25519 تأیید می‌کند
- X3DH را اجرا می‌کند: DH(هویت محلی، کلید پیش‌امضاشده از راه دور) + DH(هویت محلی، کلید پیش‌درآمد از راه دور)
- کلید ریشه و کلیدهای زنجیره را با HKDF-SHA256 مشتق می‌کند

**رمزنگاری/رمزگشایی:**
- چرخ لنگر متقارن با HMAC-SHA256
- AES-256-GCM با nonce تصادفی 12 بایتی
- کلیدهای هر پیام با محرمانگی رو به جلو
- مدیریت پیام‌های خارج از ترتیب (کش کلید رد شده، حداکثر 1000 کلید)

**پارامترها:**
- اطلاعات مشتق‌سازی کلید ریشه: `"aether-root-v1"`
- اطلاعات مشتق‌سازی زنجیره ارسال: `"aether-chain-send-v1"`
- اطلاعات مشتق‌سازی زنجیره دریافت: `"aether-chain-recv-v1"`
- نمک کلید پیام: `0x01`، نمک کلید زنجیره: `0x02`

### ۴. انتزاع حمل‌ونقل (`TransportService`)

رابطی برای حمل‌ونقل‌های فیزیکی (BLE، Wi-Fi Direct و غیره):

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport:** پیاده‌سازی مرجع با استفاده از `ConcurrentHashMap` سراسری برای آزمون/نسخه نمایشی.

### ۵. مدل‌های دامنه (`Models.kt`)

- **AetherMeshNode**: هویت گره با UHID، کلید عمومی، قابلیت‌ها، geohash
- **PeerInfo**: همتای شناخته‌شده با امتیاز قابلیت اطمینان و مهر زمانی آخرین مشاهده
- **RouteEntry**: ورودی جدول مسیریابی با تعداد hop و امتیاز کیفیت
- **NodeCapabilities**: فیلد بیت (BLE، Wi-Fi Direct، Gateway، Relay، SOS، Streaming، Voice، DTN)
- **DtnBundle**: بسته ذخیره‌و‌ارسال با انقضا و شمارش کپی

## ثوابت پروتکل

ثوابت کلیدی (از `Constants.kt`):

| دسته‌بندی | ثابت | مقدار |
|----------|----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## انواع بسته

همه 23 نوع بسته با مقادیر enum در C# (1-23) مطابقت دارند:

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## وابستگی‌ها

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519، ECDH P-256، AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — پشتیبانی از قالب کلید
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await، Flow
- **org.slf4j:slf4j-api:2.0.9** — ثبت وقایع
- **kotlin-stdlib** — کتابخانه استاندارد Kotlin

## مثال‌های استفاده

### تولید کلید

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### امضای بسته

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### سریال‌سازی بسته

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### رمزنگاری پروتکل سیگنال

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## سازگاری بین‌زبانی

این پیاده‌سازی **سازگاری دقیق قالب سیمی** با پیاده‌سازی مرجع C# را حفظ می‌کند:

- قالب بسته باینری: چینش little-endian یکسان
- enum نوع بسته: مقادیر دقیقاً با enum در C# (1-23) مطابقت دارند
- امضاهای Ed25519: سازگار با NSec/libsodium
- ECDH P-256: منحنی استاندارد، سازگار در تمام زبان‌ها
- HKDF-SHA256: پیاده‌سازی استاندارد RFC 5869
- AES-256-GCM: استاندارد NIST با nonce 12 بایتی، برچسب 16 بایتی

بسته‌های سریال‌شده در Kotlin می‌توانند در C# سریال‌زدایی شوند و بالعکس.

## آزمون

پیاده‌سازی شامل یک نسخه نمایشی جامع (`Demo.kt`) است که موارد زیر را آزمایش می‌کند:

1. تولید کلید و خروجی کلید عمومی
2. تولید و تبادل بسته کلید پیش‌درآمد
3. برقراری جلسه از طریق پروتکل سیگنال
4. ایجاد، امضا و سریال‌سازی بسته
5. سریال‌زدایی و تأیید امضای بسته
6. رمزنگاری و رمزگشایی پیام
7. جلوگیری از حمله بازپخش
8. پیام‌رسانی حمل‌ونقل درون‌فرایندی

اجرا با:
```bash
./gradlew run
```

## ملاحظات امنیتی

- **پاک‌سازی کلید**: تمام مواد رمزنگاری میانی پس از استفاده با `CryptographicOperations.ZeroMemory` پاک می‌شوند (معادل Kotlin: `fill(0)`)
- **محافظت از بازپخش**: حذف تکراری nonce با TTL 5 دقیقه‌ای از حملات بازپخش جلوگیری می‌کند
- **محرمانگی رو به جلو**: کلیدهای هر پیام از چرخ لنگر زنجیره مشتق می‌شوند
- **مدیریت خارج از ترتیب**: کش کلید رد شده با حداکثر 1000 کلید برای جلوگیری از اتمام حافظه
- **احراز هویت RREP**: بسته‌های پاسخ مسیر توسط گره مقصد امضا می‌شوند
- **محرمانگی بسته**: محتوای پیام با AES-256-GCM رمزنگاری می‌شود

## توسعه‌های آینده

پیاده‌سازی قلاب‌هایی برای موارد زیر فراهم می‌کند:

- **حمل‌ونقل BLE** (رابط `TransportService`)
- **حمل‌ونقل Wi-Fi Direct** (همان رابط)
- **مسیریابی همه‌گیر DTN** (مدل `DtnBundle` آماده است)
- **پخش SOS** (نوع بسته تعریف شده است)
- **بیکن‌های حضور** (نوع بسته تعریف شده است)
- **صدا و استریم** (انواع بسته تعریف شده‌اند)
- **Double Ratchet** (هنگامی که حمل‌ونقل‌های همیشه‌روشن موجود باشند)

## مستندات پروتکل

مشخصات کامل پروتکل: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## مجوز

SPDX-License-Identifier: MIT

</div>
