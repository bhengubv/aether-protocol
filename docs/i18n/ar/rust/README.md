<div dir="rtl">

# بروتوكول Aether — تطبيق Rust

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

تطبيق Rust كامل لبروتوكول شبكات الميش Aether، يتميز بالتوافق مع تنسيق السلك الخاص بالتطبيق المرجعي C#.

## نظرة عامة

توفر هذه الـ crate:

- **تسلسل/إلغاء تسلسل MeshPacket** — تنسيق ثنائي سلكي يطابق C# PacketSerializer تماماً
- **توقيع Ed25519** — توليد مفاتيح الهوية والتوقيع والتحقق
- **بروتوكول Signal** — اتفاقية مفاتيح مستندة إلى X3DH مع الترقيع المتماثل للسرية للأمام
- **خدمة توقيع الحزم** — إزالة تكرار nonce وفحوصات النضارة
- **النقل داخل العملية** — شبكة ميش محاكاة للاختبار والعروض التوضيحية

## هيكل المشروع

```
rust/
├── Cargo.toml                          # Crate manifest
├── src/
│   ├── lib.rs                          # Module declarations
│   ├── main.rs                         # Demo application
│   ├── constants.rs                    # Protocol constants
│   ├── models.rs                       # Core data structures
│   ├── protocol/
│   │   ├── mod.rs                      # MeshPacket, PacketType enum
│   │   └── serializer.rs               # Binary serialization (wire-compatible)
│   ├── security/
│   │   ├── mod.rs                      # Module declarations
│   │   ├── ed25519.rs                  # Ed25519 signing service
│   │   ├── signal_protocol.rs          # Signal Protocol implementation
│   │   └── packet_signing.rs           # Packet signing + nonce dedup
│   └── transport/
│       ├── mod.rs                      # TransportService trait
│       └── in_process.rs               # In-memory transport implementation
```

## الميزات الرئيسية

### 1. التوافق مع تنسيق السلك

يُنتج `PacketSerializer` مخرجات متطابقة بايت-لبايت مع تطبيق C#:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (GUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (u16, LE)
[N bytes] SourceUhid (UTF-8)
[2 bytes] DestinationUhid length (u16, LE)
[N bytes] DestinationUhid (UTF-8)
[2 bytes] PacketNonce length (u16, LE)
[N bytes] PacketNonce
[4 bytes] Payload length (i32, LE)
[N bytes] Payload
[2 bytes] Signature length (u16, LE)
[N bytes] Signature
```

تستخدم جميع الأعداد الصحيحة متعددة البايتات ترتيب البايتات little-endian. تكون أطوال السلاسل مسبوقة بـ u16 (SourceUhid، DestinationUhid) أو i32 (Payload، Signature) كما هو محدد في مواصفات البروتوكول.

### 2. أنواع الحزم

جميع 26 نوعاً من أنواع الحزم من مواصفات البروتوكول محددة:

- RouteRequest (1), RouteReply (2), Data (3), Ack (4)
- SosBroadcast (5), SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8), ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11), StreamSegment (12), StreamSubscribe (13), StreamUnsubscribe (14)
- VoicePtt (15), VoiceCall (16), VoiceSignaling (17)
- DtnBundle (18), DtnCustodyAck (19), DtnDeliveryReceipt (20)
- PresenceBeacon (21), PresenceQuery (22), ProfileSync (23)
- TipPacket (24), PreKeyRequest (25), PreKeyResponse (26)

### 3. توقيع Ed25519

- مفاتيح خاصة بحجم 32 بايت (بذرة)، مفاتيح عامة بحجم 32 بايت، توقيعات بحجم 64 بايت
- يستخدم `ed25519-dalek` للعمليات التشفيرية
- تصفير آمن للمفاتيح بعد الاستخدام

### 4. بروتوكول Signal

اتفاقية مفاتيح مستندة إلى X3DH مع الترقيع المتماثل:

- **اتفاقية المفاتيح:** ECDH P-256 باستخدام مفاتيح مؤقتة وموقّعة مسبقاً
- **اشتقاق المفاتيح:** HKDF-SHA256 مع سلاسل معلومات فريدة
  - `aether-root-v1` — المفتاح الجذر
  - `aether-chain-send-v1` — مفتاح سلسلة الإرسال
  - `aether-chain-recv-v1` — مفتاح سلسلة الاستقبال
- **التشفير:** AES-256-GCM (nonce بحجم 12 بايت، علامة بحجم 16 بايت)
- **الترقيع:** تقدم مفتاح السلسلة المتماثلة مع مفاتيح رسائل مستندة إلى عداد
- **معالجة الرسائل خارج الترتيب:** تخزين مؤقت لحتى 1000 مفتاح رسالة متخطَّى

### 5. خدمة توقيع الحزم

- توليد nonce عشوائي بحجم 8 بايتات
- طوابع زمنية بدقة المللي ثانية
- التحقق من النضارة (نافذة 5 دقائق)
- إزالة تكرار nonce لكل مُرسِل (يمنع الإعادة)
- تنظيف تلقائي للإدخالات منتهية الصلاحية

### 6. النقل داخل العملية

شبكة ميش محاكاة للاختبار:

- سجل ثابت للعقد باستخدام HashMap متزامن
- تسليم الرسائل بأسلوب "أطلق وانسَ"
- فحوصات اتصال الأقران ثنائية الاتجاه
- مناسب للعروض التوضيحية واختبارات الوحدة

## الاستخدام

### توليد المفاتيح والتوقيع الأساسي

```rust
use aethermesh_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### جلسة بروتوكول Signal

```rust
use aethermesh_protocol::security::SignalProtocolService;

let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

// Bob publishes pre-key bundle
let bob_bundle = bob.generate_pre_key_bundle("bob-node")?;

// Alice processes bundle and establishes session
alice.process_pre_key_bundle(&bob_bundle)?;

// Alice encrypts message
let plaintext = b"Hello!";
let encrypted = alice.encrypt("bob-node", plaintext)?;

// Bob decrypts
let alice_bundle = alice.generate_pre_key_bundle("alice-node")?;
bob.process_pre_key_bundle(&alice_bundle)?;
let decrypted = bob.decrypt("alice-node", &encrypted)?;

assert_eq!(decrypted, plaintext);
```

### تسلسل الحزم

```rust
use aethermesh_protocol::protocol::{MeshPacket, PacketType};
use aethermesh_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### توقيع الحزم

```rust
use aethermesh_protocol::security::PacketSigningService;
use aethermesh_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### النقل داخل العملية

```rust
use aethermesh_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## تشغيل العرض التوضيحي

```bash
cargo run --release
```

ينفذ العرض التوضيحي الخطوات التالية:

1. يولد مفاتيح هوية لـ Alice وBob
2. يُهيئ خدمات بروتوكول Signal
3. يولد حزم المفاتيح المسبقة ويتبادلها
4. يؤسس جلسات مشفرة
5. يتبادل الرسائل المشفرة
6. ينشئ حزم الميش ويوقّعها
7. يتحقق من توقيعات الحزم
8. يُسلسل الحزم ويُلغي تسلسلها
9. يُظهر النقل داخل العملية

## الثوابت

جميع ثوابت البروتوكول محددة في `src/constants.rs`، تتطابق مع مواصفات C#:

- التوجيه: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- الأمان: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- النقل: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- الصوت/البث: إعدادات معدل البت والمخزن المؤقت المتنوعة

## التبعيات

- `ed25519-dalek` — توقيع Ed25519
- `x25519-dalek` — اتفاقية مفاتيح X25519
- `aes-gcm` — تشفير AES-256-GCM
- `hkdf` — اشتقاق مفاتيح HKDF
- `sha2` — تجزئة SHA-256
- `hmac` — عمليات HMAC
- `rand` — توليد الأرقام العشوائية
- `uuid` — توليد GUID وتسلسله
- `serde` + `serde_json` — التسلسل
- `tokio` — وقت تشغيل غير متزامن
- `async-trait` — أساليب الصفات غير المتزامنة

## الاختبار

تشغيل جميع الاختبارات:

```bash
cargo test
```

تشمل الاختبارات:

- إنشاء الحزم وإدارة TTL
- تحويل أنواع الحزم
- رحلات التسلسل/إلغاء التسلسل ذهاباً وإياباً
- توليد مفاتيح Ed25519 والتحقق من التوقيع
- تأسيس جلسة بروتوكول Signal والتشفير
- توقيع الحزم والتحقق من النضارة
- اتصال النقل داخل العملية

## الامتثال للبروتوكول

يتبع هذا التطبيق مواصفات بروتوكول Aether (الإصدار 2.0) مع:

- ✅ تنسيق السلك الثنائي (little-endian، مسبوق بالطول)
- ✅ جميع أنواع الحزم الـ 26
- ✅ توقيع Ed25519 مع إزالة تكرار nonce
- ✅ اتفاقية مفاتيح X3DH مع HKDF-SHA256
- ✅ تشفير AES-256-GCM مع nonce بحجم 12 بايت
- ✅ الترقيع المتماثل مع معالجة الرسائل خارج الترتيب
- ✅ توليد حزم المفاتيح المسبقة ومعالجتها
- ✅ بناء البيانات القابلة للتوقيع في الحزمة (تجزئة SHA-256 للحمولة)
- ✅ تجريد صفة النقل

## ملاحظات

- يستخدم تنسيق السلك ترتيب البايتات little-endian طوال الوقت (يطابق C# BinaryPrimitives.WriteInt32LittleEndian)
- تستخدم بادئات طول السلاسل u16 لـ UHIDs، وi32 للحمولة/التوقيع (يطابق C# WriteUInt16/WriteInt32)
- يتم تصفير جميع مواد المفاتيح التشفيرية بعد الاستخدام عبر ما يعادل `CryptographicOperations`
- يستخدم تطبيق بروتوكول Signal HKDF مع بايتات ملح [0x01] و[0x02] لترقيع السلسلة (يطابق استخدام C# لـ HKDF)
- تستخدم إزالة تكرار Nonce قائمة VecDeque لكل مُرسِل مع تنظيف تلقائي للإدخالات الأقدم من 5 دقائق

</div>
