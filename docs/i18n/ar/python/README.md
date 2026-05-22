<div dir="rtl">

# بروتوكول Aether لشبكات الميش - تطبيق Python

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

تطبيق Python لبروتوكول شبكات الميش Aether، يوفر عمليات تشفير متوافقة مع التطبيق المرجعي C# على مستوى البروتوكول السلكي.

## نظرة عامة

Aether هو بروتوكول شبكات ميش لامركزي مصمم للبيئات التي تتقطع فيها الاتصال بالإنترنت أو تنعدم. تقدم حزمة Python هذه:

- **توقيع Ed25519**: توليد المفاتيح والتوقيع والتحقق باستخدام PyNaCl
- **بروتوكول Signal X3DH**: تبادل المفاتيح غير المتزامن مع ECDH P-256
- **تشفير AES-256-GCM**: تشفير متماثل لكل رسالة مع nonces بحجم 12 بايت
- **اشتقاق المفاتيح HKDF-SHA256**: اشتقاق المفاتيح المتوافق مع RFC 5869 مع سلاسل معلومات خاصة بالسياق
- **الترقيع المتماثل (Symmetric Ratchet)**: اشتقاق مفاتيح الرسائل المستند إلى HMAC-SHA256 مع السرية للأمام
- **تسلسل الحزم**: تنسيق ثنائي سلكي little-endian متوافق مع تطبيق C#
- **منع هجمات الإعادة**: إزالة التكرار المستندة إلى nonce مع TTL مدته 5 دقائق
- **النقل داخل العملية**: نقل وهمي للاختبار في اتصالات الميش

## التثبيت

### من PyPI (عند النشر)
```bash
pip install aether-protocol
```

### من المصدر
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### تبعيات التطوير
```bash
pip install -e ".[dev]"
```

## البداية السريعة

```python
import asyncio
from aether.security.ed25519_service import Ed25519SigningService
from aether.security.signal_protocol import SignalProtocolService
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## البنية المعمارية

### هيكل الحزمة

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## الميزات الرئيسية

### 1. خدمة توقيع Ed25519

تستخدم PyNaCl (libsodium) للعمليات التشفيرية:

```python
from aether.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**أحجام المفاتيح:**
- المفتاح الخاص: 32 بايت (بذرة Ed25519)
- المفتاح العام: 32 بايت (نقطة Ed25519)
- التوقيع: 64 بايت

### 2. بروتوكول Signal

ينفذ تبادل مفاتيح X3DH مع الترقيع المتماثل للسرية للأمام:

```python
from aether.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**اشتقاق المفاتيح:**
- يستخدم HKDF-SHA256 مع الملح: `"AetherSignal"`
- معلومات المفتاح الجذر: `"aether-root-v1"`
- معلومات سلسلة الإرسال: `"aether-chain-send-v1"`
- معلومات سلسلة الاستقبال: `"aether-chain-recv-v1"`

**الترقيع المتماثل:**
- يستخدم HMAC-SHA256 مع مفتاح السلسلة
- يشتق مفاتيح رسائل جديدة ويتقدم في السلسلة مع كل رسالة
- يدعم حتى 1000 مفتاح متخطَّى للتسليم خارج الترتيب
- تشفير لكل رسالة: AES-256-GCM مع nonce عشوائي بحجم 12 بايت

### 3. تسلسل الحزم

تنسيق ثنائي سلكي متوافق مع تطبيق C#:

```python
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**تنسيق السلك (Little-Endian):**
- إصدار البروتوكول: 1 بايت
- نوع الحزمة: 1 بايت
- معرف الحزمة: 16 بايت (UUID)
- الأولوية: 1 بايت
- TTL: 4 بايتات (int32)
- TimestampMs: 8 بايتات (int64)
- طول SourceUhid: 2 بايت + بيانات UTF-8
- طول DestinationUhid: 2 بايت + بيانات UTF-8
- طول PacketNonce: 2 بايت + بيانات
- طول الحمولة: 4 بايتات + بيانات
- طول التوقيع: 2 بايت + بيانات

### 4. توقيع الحزم

يوقّع الحزم باستخدام Ed25519 ويكتشف هجمات الإعادة:

```python
from aether.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**البيانات القابلة للتوقيع:**
وفقاً للقسم 2.3 من مواصفات البروتوكول، يشمل التوقيع:
- PacketNonce (8 بايتات)
- TimestampMs (8 بايتات، little-endian int64)
- Type (4 بايتات، little-endian int32)
- SourceUhid (الطول + UTF-8)
- DestinationUhid (الطول + UTF-8)
- SHA-256(Payload) (32 بايتاً)
- Ttl (4 بايتات، little-endian int32)
- Priority (4 بايتات، little-endian int32)

**منع الإعادة:**
- يحتفظ بذاكرة تخزين مؤقت لأزواج (sender_uhid, nonce) المُشاهَدة
- TTL مدته 5 دقائق لكل إدخال في الذاكرة المؤقتة
- تنظيف تلقائي كل 60 ثانية

### 5. خدمات النقل

فئة أساسية مجردة للنقل المادي (BLE، Wi-Fi Direct، إلخ):

```python
from aether.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**ميزات InProcessTransport:**
- سجل عالمي على مستوى الفئة للعقد
- آمن للخيوط مع threading.Lock
- مثالي للاختبار ومحاكاة الميش المحلي
- الخصائص: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## مرجع الثوابت

جميع ثوابت البروتوكول معرّفة في `aether/constants.py`:

### التشفير
- `ED25519_PRIVATE_KEY_SIZE`: 32 بايت
- `ED25519_PUBLIC_KEY_SIZE`: 32 بايت
- `ED25519_SIGNATURE_SIZE`: 64 بايت
- `AES_GCM_NONCE_SIZE`: 12 بايت
- `AES_GCM_TAG_SIZE`: 16 بايت
- `MAX_SKIPPED_KEYS`: 1000

### التوجيه
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN التخزين والإعادة
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(راجع `constants.py` للقائمة الكاملة)

## تشغيل العرض التوضيحي

يوضح جميع الميزات الرئيسية بمخرجات ملوّنة:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

يشمل العرض التوضيحي:
1. توليد مفاتيح Ed25519 والتوقيع
2. إنشاء العقد مع AetherNode
3. تبادل مفاتيح X3DH لبروتوكول Signal
4. تشفير الرسائل وفكّ تشفيرها
5. تسلسل الحزم وإلغاء تسلسلها
6. توقيع الحزم والكشف عن هجمات الإعادة
7. الاتصال عبر النقل داخل العملية
8. سير عمل التشفير الشامل من النهاية إلى النهاية

## التبعيات

### وقت التشغيل
- `pynacl>=1.5.0` - توقيع Ed25519 عبر libsodium
- `cryptography>=41.0.0` - ECDH P-256، HKDF-SHA256، AES-256-GCM، HMAC-SHA256

### التطوير
- `pytest>=7.4.0` - إطار الاختبار
- `pytest-asyncio>=0.21.0` - دعم الاختبار غير المتزامن
- `black>=23.0.0` - تنسيق الكود
- `mypy>=1.5.0` - فحص الأنواع الثابت
- `ruff>=0.1.0` - الفحص اللغوي

## التوافق

**إصدار Python:** 3.10+

**المنصة:** متعددة المنصات (Windows، macOS، Linux)

**الواجهة الخلفية للتشفير:** يستخدم libsodium للنظام والواجهات الخلفية لمكتبة cryptography، مما يضمن سلوكاً متسقاً عبر المنصات.

## مراجع البروتوكول

- **توجيه AODV:** RFC 3561
- **اتفاقية مفاتيح X3DH:** مؤسسة Signal، نوفمبر 2016
- **الترقيع المزدوج:** مؤسسة Signal، نوفمبر 2016
- **HKDF:** RFC 5869 (الاستخراج والتوسيع المستند إلى HMAC)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB وآخرون، 2012

## اعتبارات الأمان

### إلغاء المفاتيح
تُصفَّر المواد التشفيرية الوسيطة بعد الاستخدام:
- الأسرار المشتركة من ECDH
- مفاتيح الرسائل من الترقيع المتماثل
- مواد المفاتيح المشتقة في سياق التأسيس

في Python، يكون التصفير الفعلي للذاكرة محدوداً، لكن يتم مسح البيانات الحساسة من نطاق المتغير فوراً بعد الاستخدام.

### نموذج التهديد
يفترض Aether:
- التنصت السلبي على BLE/Wi-Fi
- حقن الحزم النشط وهجمات الإعادة
- هجمات Sybil عبر إنشاء عقد مزيفة
- الحرمان الانتقائي من الخدمة

تشمل الحمايات:
- **السرية:** مفاتيح لكل رسالة AES-256-GCM
- **التكامل:** توقيعات حزم Ed25519
- **منع الإعادة:** إزالة التكرار المستندة إلى nonce
- **السرية للأمام:** الترقيع المتماثل مع مفاتيح لكل رسالة
- **مصادقة المسار:** ردود المسار الموقّعة

### القيود
- تسليم الرسائل خارج الترتيب مدعوم حتى 1000 رسالة
- يتم رفض الرسائل التي تتجاوز الفجوة
- تدوير عناوين BLE كل 15 دقيقة (غير منفَّذ في Python)
- نافذة الترحيل من P-256 إلى Ed25519 هي 30 يوماً (الرجوع غير منفَّذ بعد)

## الاختبار

تشغيل مجموعة الاختبارات:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## الرخصة

رخصة MIT - راجع ملف LICENSE للتفاصيل

## المساهمة

للمساهمة في التحسينات:

1. تأكد من أن الكود يتبع أسلوب PEP 8 (استخدم `black` للتنسيق)
2. أضف تلميحات الأنواع لجميع الدوال
3. أدرج docstrings للواجهات البرمجية العامة
4. شغّل `mypy` لفحص الأنواع
5. أضف اختبارات للميزات الجديدة

## المراجع

- مواصفات بروتوكول Aether: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- التطبيق المرجعي C#: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev

</div>
