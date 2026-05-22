<div dir="rtl">

# پروتکل شبکه مِش Aether - پیاده‌سازی Python

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](README.md) · [한국어](../../ko/python/README.md)

یک پیاده‌سازی Python از پروتکل شبکه مِش Aether، که عملیات رمزنگاری سازگار با پیاده‌سازی مرجع C# را فراهم می‌کند.

## مرور کلی

Aether یک پروتکل شبکه مِش غیرمتمرکز است که برای محیط‌هایی با اتصال اینترنتی ناپایدار یا بدون اتصال طراحی شده است. این بسته Python موارد زیر را فراهم می‌کند:

- **امضای Ed25519**: تولید کلید، امضا و تأیید با استفاده از PyNaCl
- **پروتکل Signal X3DH**: تبادل کلید ناهمزمان با ECDH P-256
- **رمزنگاری AES-256-GCM**: رمزنگاری متقارن به‌ازای هر پیام با nonce های ۱۲ بایتی
- **اشتقاق کلید HKDF-SHA256**: اشتقاق کلید مطابق با RFC 5869 با رشته‌های اطلاعاتی مختص بافت
- **رچت متقارن**: اشتقاق کلید پیام مبتنی بر HMAC-SHA256 با محرمانگی رو به جلو
- **سریال‌سازی بسته**: فرمت سیمی باینری با ترتیب بایت little-endian که با پیاده‌سازی C# مطابقت دارد
- **پیشگیری از حملات بازپخش**: حذف تکراری مبتنی بر nonce با TTL پنج دقیقه‌ای
- **انتقال درون‌فرآیندی**: انتقال ساختگی برای آزمایش ارتباطات مِش

## نصب

### از PyPI (پس از انتشار)
```bash
pip install aether-protocol
```

### از سورس
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### وابستگی‌های توسعه
```bash
pip install -e ".[dev]"
```

## شروع سریع

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

## معماری

### ساختار بسته

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

## ویژگی‌های کلیدی

### ۱. سرویس امضای Ed25519

از PyNaCl (libsodium) برای عملیات رمزنگاری استفاده می‌کند:

```python
from aether.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**اندازه کلیدها:**
- کلید خصوصی: ۳۲ بایت (seed Ed25519)
- کلید عمومی: ۳۲ بایت (نقطه Ed25519)
- امضا: ۶۴ بایت

### ۲. پروتکل Signal

تبادل کلید X3DH را با رچت متقارن برای محرمانگی رو به جلو پیاده‌سازی می‌کند:

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

**اشتقاق کلید:**
- از HKDF-SHA256 با نمک: `"AetherSignal"` استفاده می‌کند
- اطلاعات کلید ریشه: `"aether-root-v1"`
- اطلاعات زنجیره ارسال: `"aether-chain-send-v1"`
- اطلاعات زنجیره دریافت: `"aether-chain-recv-v1"`

**رچت متقارن:**
- از HMAC-SHA256 با کلید زنجیره استفاده می‌کند
- با هر پیام کلیدهای پیام جدید استخراج کرده و زنجیره را پیش می‌برد
- از حداکثر ۱۰۰۰ کلید رد شده برای تحویل خارج از نوبت پشتیبانی می‌کند
- رمزنگاری به‌ازای هر پیام: AES-256-GCM با nonce تصادفی ۱۲ بایتی

### ۳. سریال‌سازی بسته

فرمت باینری سازگار با سیم که با پیاده‌سازی C# مطابقت دارد:

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

**فرمت سیمی (Little-Endian):**
- نسخه پروتکل: ۱ بایت
- نوع بسته: ۱ بایت
- شناسه بسته: ۱۶ بایت (UUID)
- اولویت: ۱ بایت
- TTL: ۴ بایت (int32)
- TimestampMs: ۸ بایت (int64)
- طول SourceUhid: ۲ بایت + داده UTF-8
- طول DestinationUhid: ۲ بایت + داده UTF-8
- طول PacketNonce: ۲ بایت + داده
- طول Payload: ۴ بایت + داده
- طول امضا: ۲ بایت + داده

### ۴. امضای بسته

بسته‌ها را با Ed25519 امضا کرده و حملات بازپخش را شناسایی می‌کند:

```python
from aether.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**داده‌های قابل امضا:**
طبق بخش ۲.۳ مشخصات پروتکل، امضا موارد زیر را پوشش می‌دهد:
- PacketNonce (۸ بایت)
- TimestampMs (۸ بایت، little-endian int64)
- Type (۴ بایت، little-endian int32)
- SourceUhid (طول + UTF-8)
- DestinationUhid (طول + UTF-8)
- SHA-256(Payload) (۳۲ بایت)
- Ttl (۴ بایت، little-endian int32)
- Priority (۴ بایت، little-endian int32)

**پیشگیری از بازپخش:**
- حافظه پنهانی از جفت‌های (sender_uhid, nonce) دیده‌شده نگه می‌دارد
- TTL پنج دقیقه‌ای برای هر ورودی حافظه پنهان
- پاکسازی خودکار هر ۶۰ ثانیه

### ۵. سرویس‌های انتقال

کلاس پایه انتزاعی برای انتقال‌های فیزیکی (BLE، Wi-Fi Direct و غیره):

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

**ویژگی‌های InProcessTransport:**
- رجیستری سراسری گره‌ها در سطح کلاس
- ایمن برای thread با threading.Lock
- مناسب برای آزمایش و شبیه‌سازی مِش محلی
- ویژگی‌ها: name، is_available، max_bandwidth_bps، max_range_meters، power_cost_relative، max_concurrent_peers

## مرجع ثابت‌ها

تمام ثابت‌های پروتکل در `aether/constants.py` تعریف شده‌اند:

### رمزنگاری
- `ED25519_PRIVATE_KEY_SIZE`: ۳۲ بایت
- `ED25519_PUBLIC_KEY_SIZE`: ۳۲ بایت
- `ED25519_SIGNATURE_SIZE`: ۶۴ بایت
- `AES_GCM_NONCE_SIZE`: ۱۲ بایت
- `AES_GCM_TAG_SIZE`: ۱۶ بایت
- `MAX_SKIPPED_KEYS`: ۱۰۰۰

### مسیریابی
- `DEFAULT_TTL`: ۷
- `SOS_TTL`: ۱۵
- `ROUTE_TIMEOUT_MS`: ۵۰۰۰
- `ROUTE_EXPIRY_SECONDS`: ۳۰۰

### DTN ذخیره و ارسال
- `DTN_BUNDLE_TTL_HOURS`: ۷۲
- `DTN_MAX_COPIES`: ۳
- `DTN_MAX_BUNDLES_PER_NODE`: ۵۰
- `DTN_SCAN_INTERVAL_SECONDS`: ۶۰

(برای فهرست کامل به `constants.py` مراجعه کنید)

## اجرای نمایش

تمام ویژگی‌های اصلی را با خروجی رنگارنگ نمایش می‌دهد:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

نمایش موارد زیر را پوشش می‌دهد:
۱. تولید کلید Ed25519 و امضا
۲. ایجاد گره با AetherNode
۳. تبادل کلید X3DH پروتکل Signal
۴. رمزنگاری و رمزگشایی پیام
۵. سریال‌سازی/حذف سریال‌سازی بسته
۶. امضای بسته و شناسایی حمله بازپخش
۷. ارتباط از طریق انتقال درون‌فرآیندی
۸. گردش کار کامل رمزنگاری سرتاسر

## وابستگی‌ها

### زمان اجرا
- `pynacl>=1.5.0` - امضای Ed25519 از طریق libsodium
- `cryptography>=41.0.0` - ECDH P-256، HKDF-SHA256، AES-256-GCM، HMAC-SHA256

### توسعه
- `pytest>=7.4.0` - چارچوب آزمایش
- `pytest-asyncio>=0.21.0` - پشتیبانی از آزمون ناهمزمان
- `black>=23.0.0` - قالب‌بندی کد
- `mypy>=1.5.0` - بررسی نوع استاتیک
- `ruff>=0.1.0` - بررسی lint

## سازگاری

**نسخه Python:** ۳.۱۰+

**پلتفرم:** چندپلتفرمی (Windows، macOS، Linux)

**پشتیبان رمزنگاری:** از پشتیبان‌های libsodium سیستمی و کتابخانه cryptography استفاده می‌کند و رفتار یکسانی را در همه پلتفرم‌ها تضمین می‌نماید.

## مراجع پروتکل

- **مسیریابی AODV:** RFC 3561
- **توافق کلید X3DH:** بنیاد Signal، نوامبر ۲۰۱۶
- **رچت دوگانه:** بنیاد Signal، نوامبر ۲۰۱۶
- **HKDF:** RFC 5869 (استخراج و توسعه مبتنی بر HMAC)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB و همکاران، ۲۰۱۲

## ملاحظات امنیتی

### صفر کردن کلید
مواد رمزنگاری میانی پس از استفاده صفر می‌شوند:
- اسرار مشترک از ECDH
- کلیدهای پیام از رچت متقارن
- مواد کلید مشتق‌شده در زمینه برقراری

در Python، صفر کردن حافظه درجای واقعی محدود است، اما داده‌های حساس بلافاصله پس از استفاده از حوزه متغیر پاک می‌شوند.

### مدل تهدید
Aether فرض می‌کند:
- شنود غیرفعال روی BLE/Wi-Fi
- تزریق بسته فعال و بازپخش
- حملات Sybil از طریق ایجاد گره جعلی
- انکار سرویس انتخابی

محافظت‌ها شامل:
- **محرمانگی:** کلیدهای AES-256-GCM به‌ازای هر پیام
- **یکپارچگی:** امضاهای بسته Ed25519
- **پیشگیری از بازپخش:** حذف تکراری مبتنی بر nonce
- **محرمانگی رو به جلو:** رچت متقارن با کلیدهای به‌ازای هر پیام
- **احراز هویت مسیر:** پاسخ‌های مسیر امضاشده

### محدودیت‌ها
- تحویل پیام خارج از نوبت تا ۱۰۰۰ پیام پشتیبانی می‌شود
- پیام‌های فراتر از شکاف رد می‌شوند
- آدرس‌های BLE هر ۱۵ دقیقه چرخش می‌کنند (در Python پیاده‌سازی نشده)
- پنجره مهاجرت از P-256 به Ed25519 سی روز است (بازگشت هنوز پیاده‌سازی نشده)

## آزمایش

اجرای مجموعه آزمون:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## مجوز

مجوز MIT - برای جزئیات به فایل LICENSE مراجعه کنید

## مشارکت

برای مشارکت در بهبودها:

۱. اطمینان حاصل کنید که کد از سبک PEP 8 پیروی می‌کند (از `black` برای قالب‌بندی استفاده کنید)
۲. اضافه کردن نشانه‌گذاری نوع به تمام توابع
۳. درج docstring برای API های عمومی
۴. اجرای `mypy` برای بررسی نوع
۵. اضافه کردن آزمون برای ویژگی‌های جدید

## مراجع

- مشخصات پروتکل Aether: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- پیاده‌سازی مرجع C#: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev

</div>
