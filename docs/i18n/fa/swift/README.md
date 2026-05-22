<div dir="rtl">

# پروتکل Aether - پیاده‌سازی Swift

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](README.md) · [한국어](../../ko/swift/README.md)

یک پیاده‌سازی جامع Swift از پروتکل شبکه مِش Aether، که رمزنگاری سرتاسر، مسیریابی و ارتباطات همتا به همتا را برای iOS و macOS فراهم می‌کند.

## مرور کلی

Aether یک پروتکل شبکه مِش غیرمتمرکز است که برای محیط‌هایی با اتصال اینترنتی ناپایدار یا بدون اتصال طراحی شده است. این پیاده‌سازی Swift موارد زیر را فراهم می‌کند:

- **سریال‌سازی سازگار با سیم** با پیاده‌سازی مرجع C#
- **امضای Ed25519** برای احراز هویت بسته
- **پروتکل Signal** (X3DH + رچت متقارن) برای رمزنگاری سرتاسر
- **انتزاع انتقال** پشتیبانی از لایه‌های فیزیکی متعدد (BLE، Wi-Fi Direct، NearLink)
- **API های ناهمزمان ایمن برای thread** با استفاده از Swift Concurrency

## پیش‌نیازها

- Swift 5.9+
- macOS 13.0+ یا iOS 16.0+
- Xcode 15+

## وابستگی‌ها

- [swift-crypto](https://github.com/apple/swift-crypto) - الگوریتم‌های رمزنگاری بنیادین (Ed25519، P-256 ECDH، AES-GCM، HKDF، SHA-256)

## معماری

### اجزای اصلی

#### لایه پروتکل
- **MeshPacket**: ساختار اصلی بسته (UUID، نوع، UHID منبع/مقصد، TTL، اولویت، payload، امضا)
- **PacketType**: شمارش ۲۶ نوع بسته (RouteRequest، Data، SosBroadcast، DtnBundle و غیره)
- **PacketSerializer**: سریال‌ساز/حذف سریال‌ساز باینری با فرمت سیمی little-endian

#### لایه امنیت
- **Ed25519Service**: تولید کلید، امضا و تأیید با استفاده از Curve25519
- **SignalProtocolService**: توافق کلید X3DH + رچت متقارن برای نشست‌های رمزنگاری‌شده
- **PacketSigningService**: امضای سطح بسته با حذف تکراری nonce و پیشگیری از بازپخش

#### لایه انتقال
- **TransportService**: پروتکل تعریف‌کننده قرارداد انتقال
- **InProcessTransport**: انتقال درون‌حافظه برای آزمایش و ارتباط محلی

#### مدل‌ها
- **AetherNode**: نمایش گره با UHID و کلید هویتی
- **PreKeyBundle**: Bundle برای برقراری نشست ناهمزمان
- **EncryptedPayload**: پوشش پیام رمزنگاری‌شده
- **DtnBundle**: Bundle شبکه تحمل‌پذیر در برابر تأخیر
- **PeerInfo**: اطلاعات همتای جدول مسیریابی

### ثابت‌ها
تمام ثابت‌های پروتکل (TTL ها، timeout ها، محدودیت‌های ظرفیت) در `ProtocolConstants` تعریف شده‌اند.

## نصب

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

در Package.swift خود:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherProtocol", package: "aether-protocol-swift")
    ]
)
```

## شروع سریع

### ۱. سریال‌سازی بسته

```swift
import AetherProtocol

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

### ۲. امضای Ed25519

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### ۳. نشست پروتکل Signal

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

### ۴. امضای بسته

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### ۵. انتقال درون‌فرآیندی (آزمایش)

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

## فرمت سیمی

تمام بسته‌ها از فرمت سیمی little-endian پیروی می‌کنند:

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

حداقل اندازه بسته با UHID و payload خالی: **۴۳ بایت**.

## مدل امنیتی

### رمزنگاری
- **الگوریتم**: AES-256-GCM
- **اشتقاق کلید**: HKDF-SHA256 از اسرار مشترک X3DH
- **رچت نشست**: رچت متقارن کلید زنجیره را به‌ازای هر پیام پیش می‌برد

### امضا
- **الگوریتم**: Ed25519 (Curve25519)
- **حفاظت payload**: هش SHA256 در داده‌های قابل امضا گنجانده می‌شود
- **پیشگیری از بازپخش**: nonce 8 بایتی + مهرزمانی میلی‌ثانیه‌ای + حافظه پنهان حذف تکراری

### تبادل کلید
- **پروتکل**: نوع X3DH با ECDH P-256
- **اتصال کلید پیش‌ساخته**: کلید پیش‌ساخته امضاشده با Ed25519 تأیید می‌شود
- **ناهمزمان**: نشست‌ها بدون حضور آنلاین گیرنده برقرار می‌شوند

### محدودیت‌ها
- **MaxSkippedKeys**: ۱۰۰۰ (پیام‌های خارج از نوبت به‌ازای هر نشست)
- **MaxPacketAge**: ۳۰۰ ثانیه (۵ دقیقه)

## ثابت‌های پروتکل

- **DefaultTtl**: ۷
- **SosTtl**: ۱۵
- **RouteTimeoutMs**: ۵٬۰۰۰
- **RouteExpirySeconds**: ۳۰۰
- **DtnBundleTtlHours**: ۷۲
- **DtnMaxCopies**: ۳
- **AesGcmNonceSize**: ۱۲ بایت
- **AesGcmTagSize**: ۱۶ بایت

برای فهرست کامل به `ProtocolConstants` مراجعه کنید.

## ایمنی thread

تمام سرویس‌ها به صورت `actor`-isolated برای دسترسی همزمان ایمن برای thread هستند:

- `SignalProtocolService` - مدیریت نشست و رمزنگاری
- `PacketSigningService` - امضا و تأیید بسته
- `InProcessTransport` - تحویل پیام

استفاده با Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## آزمایش

اجرای نمایش همراه:

```bash
cd swift
swift run aether-demo
```

خروجی مورد انتظار:

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

## قابلیت همکاری

فرمت سیمی با موارد زیر سازگار است:
- **Aether.Core** (C#) - پیاده‌سازی مرجع
- **aether-protocol-go** - پیاده‌سازی Go
- **aether-protocol-rust** - پیاده‌سازی Rust

همه پیاده‌سازی‌ها از موارد زیر استفاده می‌کنند:
- اعداد صحیح little-endian
- رمزگذاری رشته UTF-8
- امضاهای Ed25519 (۶۴ بایت)
- رمزنگاری AES-256-GCM (nonce 12 بایتی، tag 16 بایتی)

## کارایی

بنچمارک روی Apple Silicon (M1 Pro):

| عملیات | زمان |
|-----------|------|
| سریال‌سازی بسته | ~0.5 μs |
| حذف سریال‌سازی بسته | ~0.7 μs |
| امضای Ed25519 | ~3.5 ms |
| تأیید Ed25519 | ~4.2 ms |
| رمزنگاری AES-256-GCM | ~0.8 μs |
| رمزگشایی AES-256-GCM | ~0.9 μs |
| توافق کلید X3DH | ~8.5 ms |
| رچت متقارن | ~0.3 μs |

## کارهای آینده

- **انتقال BLE**: پیاده‌سازی Bluetooth Low Energy
- **انتقال Wi-Fi Direct**: Wi-Fi مستقیم همتا به همتا
- **رچت دوگانه**: محرمانگی رو به جلو کامل با رچت پیام
- **مسیریابی AODV**: کشف و نگهداری مسیر
- **سرویس DTN**: تحویل bundle ذخیره و ارسال
- **حضور و مجاورت**: کشف همتای آگاه از مکان
- **صدا و جریان**: پروتکل‌های رسانه بلادرنگ

## مجوز

MIT - برای جزئیات به فایل LICENSE مراجعه کنید

## مراجع

1. [مشخصات پروتکل Aether](../docs/PROTOCOL_SPEC.md)
2. [توافق کلید سه‌گانه Diffie-Hellman توسعه‌یافته (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [الگوریتم رچت دوگانه](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [امضاهای Ed25519](https://en.wikipedia.org/wiki/Curve25519)
6. [حالت AES-GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## مشارکت

این یک پیاده‌سازی مرجع است. برای گزارش اشکال و درخواست‌های ویژگی، لطفاً یک issue در GitHub باز کنید.

</div>
