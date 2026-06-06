<div dir="rtl">

# بروتوكول Aether - تطبيق Kotlin

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

تطبيق Kotlin كامل وجاهز للإنتاج لبروتوكول شبكات الميش Aether، مع توافق كامل لتنسيق السلك عبر اللغات مع تطبيق C# المرجعي.

## نظرة عامة

Aether هو بروتوكول شبكات ميش لامركزي للبيئات ذات الاتصال المتقطع أو المنعدم بالإنترنت. يوفر تطبيق Kotlin هذا:

- **توافق تنسيق السلك** مع C# (تسلسل حزم ثنائي مطابق تماماً)
- **توقيع Ed25519** لمصادقة الحزم والتحقق من سلامتها
- **بروتوكول Signal** للتشفير الكامل من طرف إلى طرف (اتفاقية مفتاح X3DH وضامة تماثلية وAES-256-GCM)
- **اتفاقية مفتاح ECDH P-256** لإنشاء الجلسة
- **تسلسل/إلغاء تسلسل الحزم** مع أعداد صحيحة متعددة البايت little-endian
- **حماية إعادة التشغيل** باستخدام إلغاء تكرار nonce
- **تجريد النقل** لـ BLE وWi-Fi Direct والمراسلة داخل العملية

## هيكل المشروع

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

## البناء

### المتطلبات الأساسية

- JDK 17 أو أعلى
- Gradle 8.0 أو أعلى

### التجميع

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### تشغيل العرض التوضيحي

```bash
./gradlew run
```

يوضح العرض التوضيحي:
1. توليد زوج مفاتيح Ed25519
2. إنشاء حزمة المفاتيح المسبقة وتبادلها
3. إنشاء جلسة بروتوكول Signal
4. توقيع الحزم بـ Ed25519
5. تسلسل/إلغاء تسلسل الحزم
6. تشفير وفك تشفير الرسائل
7. حماية إعادة التشغيل
8. مراسلة النقل داخل العملية

## المكونات الرئيسية

### 1. تسلسل الحزم (`PacketSerializer`)

التنسيق السلكي (little-endian):
- إصدار البروتوكول (1 بايت)
- نوع الحزمة (1 بايت)
- معرّف الحزمة / UUID (16 بايت)
- الأولوية (1 بايت)
- TTL (4 بايت، int32)
- TimestampMs (8 بايت، int64)
- SourceUhid (بادئة طول 2 بايت + بايتات UTF-8)
- DestinationUhid (بادئة طول 2 بايت + بايتات UTF-8)
- PacketNonce (بادئة طول 2 بايت + بايتات)
- الحمولة (بادئة طول 4 بايت + بايتات)
- التوقيع (بادئة طول 2 بايت + بايتات)

متوافق تماماً مع `PacketSerializer` الخاص بـ C#.

### 2. توقيع Ed25519 (`Ed25519Service` و`PacketSigning`)

- **توليد المفتاح**: بذرة مفتاح خاص 32 بايت، مفتاح عام 32 بايت
- **التوقيع**: توقيعات 64 بايت على بيانات قابلة للتوقيع حتمية
- **التحقق**: يحلّ محل P-256 ECDSA خلال فترة الهجرة
- **تنسيق بيانات التوقيع**: يطابق مواصفة C# تماماً (nonce الحزمة والطابع الزمني والنوع والUHIDs وتجزئة الحمولة وTTL والأولوية)
- **حماية إعادة التشغيل**: إلغاء تكرار nonce مع TTL مدته 5 دقائق

### 3. بروتوكول Signal (`SignalProtocol`)

يُطبّق اتفاقية مفتاح X3DH مع ضامة تماثلية:

**إنشاء الجلسة:**
- جلب حزمة المفاتيح المسبقة للنظير
- التحقق من توقيع الحزمة بـ Ed25519
- تنفيذ X3DH: DH(المفتاح المحلي للهوية، المفتاح المُوقَّع المسبق للبُعد) + DH(المفتاح المحلي للهوية، المفتاح المسبق للبُعد)
- اشتقاق مفتاح الجذر ومفاتيح السلسلة باستخدام HKDF-SHA256

**التشفير/فك التشفير:**
- ضامة تماثلية مع HMAC-SHA256
- AES-256-GCM مع nonce عشوائي مكون من 12 بايت
- مفاتيح لكل رسالة مع سرية تقدمية
- معالجة الرسائل خارج الترتيب (ذاكرة التخزين المؤقت للمفاتيح المُتجاوزة، بحد أقصى 1000 مفتاح)

**المعاملات:**
- معلومات اشتقاق مفتاح الجذر: `"aether-root-v1"`
- معلومات اشتقاق سلسلة الإرسال: `"aether-chain-send-v1"`
- معلومات اشتقاق سلسلة الاستقبال: `"aether-chain-recv-v1"`
- ملح مفتاح الرسالة: `0x01`، ملح مفتاح السلسلة: `0x02`

### 4. تجريد النقل (`TransportService`)

واجهة للنقل الفيزيائي (BLE وWi-Fi Direct وغيرهما):

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

**InProcessTransport:** تطبيق مرجعي يستخدم `ConcurrentHashMap` عاماً للاختبار/العرض التوضيحي.

### 5. نماذج المجال (`Models.kt`)

- **AetherMeshNode**: هوية العقدة مع UHID والمفتاح العام والإمكانيات والـ geohash
- **PeerInfo**: نظير معروف مع درجة موثوقية وطابع زمني لآخر ظهور
- **RouteEntry**: إدخال جدول التوجيه مع عدد القفزات ودرجة الجودة
- **NodeCapabilities**: حقل بت (BLE وWi-Fi Direct وBوابة وترحيل وSOS وبث وصوت وDTN)
- **DtnBundle**: حزمة التخزين والإعادة مع انتهاء الصلاحية وعدّ النسخ

## ثوابت البروتوكول

الثوابت الرئيسية (من `Constants.kt`):

| الفئة | الثابت | القيمة |
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

## أنواع الحزم

جميع أنواع الحزم الـ 23 تطابق قيم C# enum (1-23):

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

## التبعيات

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519 وECDH P-256 وAES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — دعم تنسيق المفتاح
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await وFlow
- **org.slf4j:slf4j-api:2.0.9** — التسجيل
- **kotlin-stdlib** — المكتبة القياسية لـ Kotlin

## أمثلة الاستخدام

### توليد المفتاح

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### توقيع الحزمة

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

### تسلسل الحزمة

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### تشفير بروتوكول Signal

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

## التوافق عبر اللغات

يحافظ هذا التطبيق على **توافق تنسيق السلك الدقيق** مع تطبيق C# المرجعي:

- تنسيق الحزمة الثنائية: تخطيط little-endian متطابق
- نوع الحزمة enum: تطابق قيم C# enum تماماً (1-23)
- توقيعات Ed25519: متوافقة مع NSec/libsodium
- ECDH P-256: منحنى قياسي، متوافق عبر اللغات
- HKDF-SHA256: تطبيق قياسي وفق RFC 5869
- AES-256-GCM: معيار NIST مع nonce بحجم 12 بايت وعلامة 16 بايت

يمكن إلغاء تسلسل الحزم المُسلسَلة بـ Kotlin في C# والعكس صحيح.

## الاختبار

يتضمن التطبيق عرضاً توضيحياً شاملاً (`Demo.kt`) يختبر:

1. توليد المفاتيح وتصدير المفتاح العام
2. توليد حزمة المفاتيح المسبقة وتبادلها
3. إنشاء الجلسة عبر بروتوكول Signal
4. إنشاء الحزمة وتوقيعها وتسلسلها
5. إلغاء تسلسل الحزمة والتحقق من التوقيع
6. تشفير وفك تشفير الرسالة
7. منع هجمات إعادة التشغيل
8. مراسلة النقل داخل العملية

التشغيل بـ:
```bash
./gradlew run
```

## اعتبارات الأمان

- **إلغاء تصفير المفاتيح**: تُمسَح جميع المواد التشفيرية الوسيطة بعد الاستخدام باستخدام `CryptographicOperations.ZeroMemory` (المكافئ في Kotlin: `fill(0)`)
- **حماية إعادة التشغيل**: إلغاء تكرار nonce مع TTL مدته 5 دقائق يمنع هجمات إعادة التشغيل
- **السرية التقدمية**: مفاتيح لكل رسالة مشتقة من ضامة السلسلة
- **معالجة الرسائل خارج الترتيب**: ذاكرة تخزين مؤقت للمفاتيح المُتجاوزة بحد أقصى 1000 مفتاح لمنع استنزاف الذاكرة
- **مصادقة RREP**: حزم Route Reply مُوقَّعة من عقدة الوجهة
- **سرية الحزمة**: محتوى الرسالة مُشفَّر بـ AES-256-GCM

## الامتدادات المستقبلية

يوفر التطبيق خطافات من أجل:

- **نقل BLE** (واجهة `TransportService`)
- **نقل Wi-Fi Direct** (نفس الواجهة)
- **توجيه وبائي DTN** (نموذج `DtnBundle` جاهز)
- **بث SOS** (نوع الحزمة مُعرَّف)
- **منارات الحضور** (نوع الحزمة مُعرَّف)
- **الصوت والبث** (أنواع الحزم مُعرَّفة)
- **Double Ratchet** (عند توفر نقل دائم الاتصال)

## توثيق البروتوكول

مواصفات البروتوكول الكاملة: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## الرخصة

SPDX-License-Identifier: MIT

</div>
