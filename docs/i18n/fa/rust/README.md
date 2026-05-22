<div dir="rtl">

# پروتکل Aether — پیاده‌سازی Rust

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](../../ru/rust/README.md) · [فارسی](README.md) · [한국어](../../ko/rust/README.md)

پیاده‌سازی کامل Rust از پروتکل شبکه مِش Aether، با سازگاری فرمت سیمی با پیاده‌سازی مرجع C#.

## مرور کلی

این crate موارد زیر را فراهم می‌کند:

- **سریال‌سازی/حذف سریال‌سازی MeshPacket** — فرمت سیمی باینری که دقیقاً با PacketSerializer در C# مطابقت دارد
- **امضای Ed25519** — تولید کلید هویتی، امضا و تأیید
- **پروتکل Signal** — توافق کلید مبتنی بر X3DH با رچت متقارن برای محرمانگی رو به جلو
- **سرویس امضای بسته** — حذف تکراری nonce و بررسی تازگی
- **انتقال درون‌فرآیندی** — شبکه مِش شبیه‌سازی‌شده برای آزمایش و نمایش

## ساختار پروژه

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

## ویژگی‌های کلیدی

### ۱. سازگاری فرمت سیمی

`PacketSerializer` خروجی یکسانی بایت به بایت با پیاده‌سازی C# تولید می‌کند:

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

تمام اعداد صحیح چندبایتی از ترتیب بایت little-endian استفاده می‌کنند. پیشوندهای طول رشته برای SourceUhid و DestinationUhid از u16 و برای Payload و Signature از i32 استفاده می‌کنند که طبق مشخصات پروتکل است.

### ۲. انواع بسته

تمام ۲۶ نوع بسته از مشخصات پروتکل تعریف شده‌اند:

- RouteRequest (1)، RouteReply (2)، Data (3)، Ack (4)
- SosBroadcast (5)، SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8)، ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11)، StreamSegment (12)، StreamSubscribe (13)، StreamUnsubscribe (14)
- VoicePtt (15)، VoiceCall (16)، VoiceSignaling (17)
- DtnBundle (18)، DtnCustodyAck (19)، DtnDeliveryReceipt (20)
- PresenceBeacon (21)، PresenceQuery (22)، ProfileSync (23)
- TipPacket (24)، PreKeyRequest (25)، PreKeyResponse (26)

### ۳. امضای Ed25519

- کلیدهای خصوصی ۳۲ بایتی (seed)، کلیدهای عمومی ۳۲ بایتی، امضاهای ۶۴ بایتی
- از `ed25519-dalek` برای عملیات رمزنگاری استفاده می‌کند
- صفر کردن ایمن کلید پس از استفاده

### ۴. پروتکل Signal

توافق کلید مبتنی بر X3DH با رچت متقارن:

- **توافق کلید:** ECDH P-256 با استفاده از کلیدهای پیش‌ساخته موقت + امضاشده
- **اشتقاق کلید:** HKDF-SHA256 با رشته‌های اطلاعاتی منحصربه‌فرد
  - `aether-root-v1` — کلید ریشه
  - `aether-chain-send-v1` — کلید زنجیره ارسال
  - `aether-chain-recv-v1` — کلید زنجیره دریافت
- **رمزنگاری:** AES-256-GCM (nonce 12 بایتی، tag 16 بایتی)
- **رچت:** پیشبرد کلید زنجیره متقارن با کلیدهای پیام مبتنی بر شمارنده
- **مدیریت خارج از نوبت:** حداکثر ۱۰۰۰ کلید پیام رد شده در حافظه پنهان

### ۵. سرویس امضای بسته

- تولید nonce تصادفی ۸ بایتی
- مهرزمانی با دقت میلی‌ثانیه
- اعتبارسنجی تازگی (پنجره ۵ دقیقه‌ای)
- حذف تکراری nonce به‌ازای فرستنده (جلوگیری از بازپخش)
- پاکسازی خودکار ورودی‌های منقضی‌شده

### ۶. انتقال درون‌فرآیندی

شبکه مِش شبیه‌سازی‌شده برای آزمایش:

- رجیستری استاتیک گره‌ها با استفاده از HashMap همزمان
- تحویل پیام «شلیک و فراموش»
- بررسی اتصال دوطرفه همتا
- مناسب برای نمایش و آزمون‌های واحد

## استفاده

### تولید کلید و امضای پایه

```rust
use aether_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### نشست پروتکل Signal

```rust
use aether_protocol::security::SignalProtocolService;

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

### سریال‌سازی بسته

```rust
use aether_protocol::protocol::{MeshPacket, PacketType};
use aether_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### امضای بسته

```rust
use aether_protocol::security::PacketSigningService;
use aether_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### انتقال درون‌فرآیندی

```rust
use aether_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## اجرای نمایش

```bash
cargo run --release
```

نمایش مراحل زیر را انجام می‌دهد:

۱. تولید کلیدهای هویتی برای Alice و Bob
۲. راه‌اندازی سرویس‌های پروتکل Signal
۳. تولید و تبادل bundle های کلید پیش‌ساخته
۴. برقراری نشست‌های رمزنگاری‌شده
۵. تبادل پیام‌های رمزنگاری‌شده
۶. ایجاد و امضای بسته‌های مِش
۷. تأیید امضاهای بسته
۸. سریال‌سازی و حذف سریال‌سازی بسته‌ها
۹. نمایش انتقال درون‌فرآیندی

## ثابت‌ها

تمام ثابت‌های پروتکل در `src/constants.rs` تعریف شده‌اند و با مشخصات C# مطابقت دارند:

- مسیریابی: DefaultTtl=7، SosTtl=15، RouteTimeoutMs=5000
- امنیت: MaxPacketAgeSeconds=300، MaxSkippedKeys=1000
- انتقال: BleMaxPayloadBytes=1024، WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72، DtnMaxCopies=3
- صدا/جریان: تنظیمات مختلف نرخ بیت و بافر

## وابستگی‌ها

- `ed25519-dalek` — امضای Ed25519
- `x25519-dalek` — توافق کلید X25519
- `aes-gcm` — رمزنگاری AES-256-GCM
- `hkdf` — اشتقاق کلید HKDF
- `sha2` — هش SHA-256
- `hmac` — عملیات HMAC
- `rand` — تولید اعداد تصادفی
- `uuid` — تولید و سریال‌سازی GUID
- `serde` + `serde_json` — سریال‌سازی
- `tokio` — رانتایم ناهمزمان
- `async-trait` — متدهای trait ناهمزمان

## آزمایش

اجرای تمام آزمون‌ها:

```bash
cargo test
```

آزمون‌ها موارد زیر را پوشش می‌دهند:

- ایجاد بسته و مدیریت TTL
- تبدیل نوع بسته
- دورهای سریال‌سازی/حذف سریال‌سازی
- تولید کلید Ed25519 و تأیید امضا
- برقراری نشست پروتکل Signal و رمزنگاری
- امضای بسته و اعتبارسنجی تازگی
- اتصال انتقال درون‌فرآیندی

## انطباق با پروتکل

این پیاده‌سازی از مشخصات پروتکل Aether (نسخه ۲.۰) پیروی می‌کند با:

- ✅ فرمت سیمی باینری (little-endian، دارای پیشوند طول)
- ✅ تمام ۲۶ نوع بسته
- ✅ امضای Ed25519 با حذف تکراری nonce
- ✅ توافق کلید X3DH با HKDF-SHA256
- ✅ رمزنگاری AES-256-GCM با nonce ۱۲ بایتی
- ✅ رچت متقارن با مدیریت خارج از نوبت
- ✅ تولید و پردازش bundle کلید پیش‌ساخته
- ✅ ساخت داده‌های قابل امضای بسته (هش SHA-256 payload)
- ✅ انتزاع trait انتقال

## یادداشت‌ها

- فرمت سیمی در سراسر از ترتیب بایت little-endian استفاده می‌کند (مطابق با BinaryPrimitives.WriteInt32LittleEndian در C#)
- پیشوندهای طول رشته برای UHID ها از u16 و برای payload/امضا از i32 استفاده می‌کنند (مطابق با WriteUInt16/WriteInt32 در C#)
- تمام مواد کلید رمزنگاری پس از استفاده از طریق معادل `CryptographicOperations` صفر می‌شوند
- پیاده‌سازی پروتکل Signal از HKDF با بایت‌های نمک [0x01] و [0x02] برای رچت زنجیره استفاده می‌کند (مطابق با استفاده HKDF در C#)
- حذف تکراری nonce از یک VecDeque به‌ازای فرستنده با پاکسازی خودکار ورودی‌های قدیمی‌تر از ۵ دقیقه استفاده می‌کند

</div>
