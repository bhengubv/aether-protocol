<div dir="rtl">

# بروتوكول Aether - تطبيق Swift

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

تطبيق Swift شامل لبروتوكول شبكات الميش Aether، يوفر تشفيراً شاملاً من النهاية إلى النهاية، والتوجيه، والاتصال بين الأقران لنظامَي iOS وmacOS.

## نظرة عامة

Aether هو بروتوكول شبكات ميش لامركزي مصمم للبيئات التي تتقطع فيها الاتصال بالإنترنت أو تنعدم. يوفر تطبيق Swift هذا:

- **تسلسل متوافق مع السلك** مع التطبيق المرجعي C#
- **توقيع Ed25519** لمصادقة الحزم
- **بروتوكول Signal** (X3DH + الترقيع المتماثل) للتشفير من النهاية إلى النهاية
- **تجريد النقل** يدعم طبقات فيزيائية متعددة (BLE، Wi-Fi Direct، NearLink)
- **واجهات برمجية غير متزامنة آمنة للخيوط** باستخدام Swift Concurrency

## المتطلبات

- Swift 5.9+
- macOS 13.0+ أو iOS 16.0+
- Xcode 15+

## التبعيات

- [swift-crypto](https://github.com/apple/swift-crypto) - العناصر الأولية التشفيرية (Ed25519، P-256 ECDH، AES-GCM، HKDF، SHA-256)

## البنية المعمارية

### المكونات الأساسية

#### طبقة البروتوكول
- **MeshPacket**: هيكل الحزمة الأساسي (UUID، النوع، UHIDs المصدر/الوجهة، TTL، الأولوية، الحمولة، التوقيع)
- **PacketType**: تعداد لـ 26 نوعاً من أنواع الحزم (RouteRequest، Data، SosBroadcast، DtnBundle، إلخ)
- **PacketSerializer**: مُسلسِل/مُلغي تسلسل ثنائي بتنسيق سلك little-endian

#### طبقة الأمان
- **Ed25519Service**: توليد المفاتيح والتوقيع والتحقق باستخدام Curve25519
- **SignalProtocolService**: اتفاقية مفاتيح X3DH + الترقيع المتماثل للجلسات المشفرة
- **PacketSigningService**: التوقيع على مستوى الحزمة مع إزالة تكرار nonce ومنع الإعادة

#### طبقة النقل
- **TransportService**: بروتوكول يحدد عقد النقل
- **InProcessTransport**: نقل داخل الذاكرة للاختبار والاتصال المحلي

#### النماذج
- **AetherMeshNode**: تمثيل العقدة مع UHID ومفتاح الهوية
- **PreKeyBundle**: حزمة لتأسيس الجلسة غير المتزامن
- **EncryptedPayload**: غلاف الرسائل المشفرة
- **DtnBundle**: حزمة شبكة التحمل المتأخر
- **PeerInfo**: معلومات الأقران في جدول التوجيه

### الثوابت
جميع ثوابت البروتوكول (TTLs، المهلات، حدود السعة) محددة في `ProtocolConstants`.

## التثبيت

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

في ملف Package.swift الخاص بك:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherMeshProtocol", package: "aether-protocol-swift")
    ]
)
```

## البداية السريعة

### 1. تسلسل الحزم

```swift
import AetherMeshProtocol

// Create a packet
var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice-node",
    destinationUhid: "bob-node",
    payload: "Hello, Aether!".data(using: .utf8)!
)

// Serialize to bytes
let serialized = PacketSerializer.serialize(packet)

// Deserialize
let deserialized = try PacketSerializer.deserialize(serialized)
```

### 2. توقيع Ed25519

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. جلسة بروتوكول Signal

```swift
let alice = SignalProtocolService()
let bob = SignalProtocolService()

// Key exchange: Bob publishes pre-key bundle
let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob-node")

// Alice processes Bob's bundle and establishes session
try await alice.processPreKeyBundle(bobBundle)

// Alice encrypts message
let encrypted = try await alice.encrypt(
    peerUhid: "bob-node",
    plaintext: "Secret message".data(using: .utf8)!
)

// For Bob to decrypt, he also needs Alice's bundle
let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice-node")
try await bob.processPreKeyBundle(aliceBundle)

// Bob decrypts
let decrypted = try await bob.decrypt(peerUhid: "alice-node", payload: encrypted)
```

### 4. توقيع الحزم

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. النقل داخل العملية (الاختبار)

```swift
let alice = InProcessTransport(uhid: "alice")
let bob = InProcessTransport(uhid: "bob")

// Set up data received callback
await bob.onDataReceived { senderUhid, data in
    print("Received \(data.count) bytes from \(senderUhid)")
}

// Send message
let success = await alice.sendAsync(
    peerUhid: "bob",
    data: "Hello".data(using: .utf8)!,
    cancellationToken: nil
)
```

## تنسيق السلك

تتوافق جميع الحزم مع تنسيق السلك little-endian:

```
[1 byte]   Protocol version (2 = signed)
[1 byte]   Packet type
[16 bytes] Packet ID (UUID)
[1 byte]   Priority
[4 bytes]  TTL (Int32)
[8 bytes]  TimestampMs (Int64)
[2 bytes]  SourceUhid length (UInt16)
[N bytes]  SourceUhid (UTF-8)
[2 bytes]  DestinationUhid length (UInt16)
[N bytes]  DestinationUhid (UTF-8)
[2 bytes]  PacketNonce length (UInt16)
[N bytes]  PacketNonce (8 bytes)
[4 bytes]  Payload length (Int32)
[N bytes]  Payload
[2 bytes]  Signature length (UInt16)
[N bytes]  Signature (64 bytes Ed25519)
```

الحد الأدنى لحجم الحزمة مع UHIDs وحمولة فارغة: **43 بايتاً**.

## نموذج الأمان

### التشفير
- **الخوارزمية**: AES-256-GCM
- **اشتقاق المفاتيح**: HKDF-SHA256 من السر المشترك لـ X3DH
- **ترقيع الجلسة**: يتقدم الترقيع المتماثل في مفتاح السلسلة مع كل رسالة

### التوقيع
- **الخوارزمية**: Ed25519 (Curve25519)
- **حماية الحمولة**: تجزئة SHA256 مدرجة في البيانات القابلة للتوقيع
- **منع الإعادة**: nonce بحجم 8 بايتات + طابع زمني بالمللي ثانية + ذاكرة مؤقتة لإزالة التكرار

### تبادل المفاتيح
- **البروتوكول**: متغير X3DH مع ECDH P-256
- **ربط المفتاح المسبق**: تحقق من المفتاح المسبق الموقّع باستخدام Ed25519
- **غير متزامن**: تأسيس الجلسات دون اتصال المستلم

### الحدود
- **MaxSkippedKeys**: 1000 (رسائل خارج الترتيب لكل جلسة)
- **MaxPacketAge**: 300 ثانية (5 دقائق)

## ثوابت البروتوكول

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5,000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 بايت
- **AesGcmTagSize**: 16 بايت

راجع `ProtocolConstants` للقائمة الكاملة.

## أمان الخيوط

جميع الخدمات معزولة في `actor` للوصول المتزامن الآمن للخيوط:

- `SignalProtocolService` - إدارة الجلسات والتشفير
- `PacketSigningService` - توقيع الحزم والتحقق منها
- `InProcessTransport` - تسليم الرسائل

الاستخدام مع Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## الاختبار

تشغيل العرض التوضيحي المدرج:

```bash
cd swift
swift run aether-demo
```

المخرجات المتوقعة:

```
=== Aether Protocol Demo ===

Test 1: Packet Serialization
---
Original packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
Serialized size: XX bytes
Deserialized packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
✓ Serialization/Deserialization successful

Test 2: Ed25519 Signing
...

Test 5: End-to-End Messaging (Full Stack)
...
✓ End-to-end messaging test successful

=== All Tests Completed ===
```

## التشغيل البيني

تنسيق السلك متوافق مع:
- **AetherMesh.Core** (C#) - التطبيق المرجعي
- **aether-protocol-go** - تطبيق Go
- **aether-protocol-rust** - تطبيق Rust

تستخدم جميع التطبيقات:
- أعداد صحيحة little-endian
- ترميز سلاسل UTF-8
- توقيعات Ed25519 (64 بايت)
- تشفير AES-256-GCM (nonce بحجم 12 بايت، علامة بحجم 16 بايت)

## الأداء

معايير الأداء على Apple Silicon (M1 Pro):

| العملية | الوقت |
|---------|-------|
| تسلسل الحزمة | ~0.5 μs |
| إلغاء تسلسل الحزمة | ~0.7 μs |
| توقيع Ed25519 | ~3.5 ms |
| التحقق من Ed25519 | ~4.2 ms |
| تشفير AES-256-GCM | ~0.8 μs |
| فك تشفير AES-256-GCM | ~0.9 μs |
| اتفاقية مفاتيح X3DH | ~8.5 ms |
| الترقيع المتماثل | ~0.3 μs |

## الأعمال المستقبلية

- **نقل BLE**: تطبيق Bluetooth Low Energy
- **نقل Wi-Fi Direct**: Wi-Fi مباشر بين الأقران
- **الترقيع المزدوج**: سرية كاملة للأمام مع ترقيع الرسائل
- **توجيه AODV**: اكتشاف المسارات وصيانتها
- **خدمة DTN**: تسليم الحزم بالتخزين والإعادة
- **الحضور والقرب**: اكتشاف الأقران المدرك للموقع
- **الصوت والبث**: بروتوكولات الوسائط في الوقت الحقيقي

## الرخصة

MIT - راجع ملف LICENSE

## المراجع

1. [مواصفات بروتوكول Aether](../docs/PROTOCOL_SPEC.md)
2. [Diffie-Hellman الثلاثي الموسّع (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [خوارزمية الترقيع المزدوج](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [توقيعات Ed25519](https://en.wikipedia.org/wiki/Curve25519)
6. [وضع AES-GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## المساهمة

هذا تطبيق مرجعي. لتقارير الأخطاء وطلبات الميزات، يرجى فتح مشكلة على GitHub.

</div>
