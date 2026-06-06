<div dir="rtl">

# پروتکل Aether - پیاده‌سازی Go

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](../../ar/go/README.md) · [中文简体](../../zh-CN/go/README.md) · [日本語](../../ja/go/README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](../../ru/go/README.md) · [فارسی](README.md) · [한국어](../../ko/go/README.md)

یک پیاده‌سازی کامل Go از پروتکل شبکه‌سازی مِش Aether، سازگار با قالب سیمی پیاده‌سازی مرجع C#.

## مرور کلی

این ماژول پروتکل شبکه‌سازی مِش غیرمتمرکز Aether را برای محیط‌هایی با اتصال اینترنتی متناوب یا فاقد اتصال پیاده‌سازی می‌کند. این ماژول موارد زیر را فراهم می‌کند:

- **سریال‌سازی بسته**: قالب سیمی باینری سازگار با پیاده‌سازی مرجع C# (کدگذاری little-endian)
- **امضای Ed25519**: احراز هویت رمزنگاری‌شده بسته
- **پروتکل سیگنال**: توافق کلید X3DH + چرخ لنگر متقارن برای رمزنگاری سرتاسری
- **سرویس امضای بسته**: حذف تکراری nonce با TTL 5 دقیقه‌ای برای جلوگیری از حملات بازپخش
- **حمل‌ونقل درون‌فرایندی**: حمل‌ونقل مبتنی بر حافظه برای آزمون و ارتباط بین‌فرایندی
- **مدل‌ها**: ساختارهای AetherMeshNode، PeerInfo، RouteEntry، DtnBundle، SosAlert
- **ثوابت پروتکل**: تمام ثوابت مسیریابی، کشف، امنیت و حمل‌ونقل

## ساختار ماژول

```
aether-protocol/go/
├── go.mod                          # Module definition
├── go.sum                           # Dependency checksums
├── README.md                        # This file
│
├── protocol/
│   ├── packet.go                   # MeshPacket struct, PacketType constants
│   └── serializer.go               # Binary serialization (little-endian)
│
├── security/
│   ├── ed25519.go                  # Ed25519 signing/verification
│   ├── signal_protocol.go          # Signal Protocol (X3DH + ratchet)
│   ├── packet_signing.go           # Nonce deduplication service
│   └── models.go                   # PreKeyBundle, EncryptedPayload, SignalSession
│
├── transport/
│   ├── transport.go                # TransportService interface
│   └── in_process.go               # In-memory transport implementation
│
├── models/
│   └── models.go                   # Domain models (Node, Route, DtnBundle, etc.)
│
├── constants/
│   └── constants.go                # Protocol constants
│
└── cmd/demo/
    └── main.go                      # Comprehensive demo program
```

## ویژگی‌های کلیدی

### ۱. سریال‌سازی بسته (Little-Endian)

قالب سیمی دقیقاً با C# مطابقت دارد و از کدگذاری little-endian برای تمام اعداد صحیح چندبایتی استفاده می‌کند:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (UUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (uint16, LE)
[N bytes] SourceUhid (UTF-8)
... (destination, nonce, payload, signature)
```

**مثال:**
```go
serializer := &protocol.PacketSerializer{}
packet := protocol.NewMeshPacket()
packet.Type = protocol.Data
packet.SourceUhid = "node-alice"
packet.DestinationUhid = "node-bob"
packet.Payload = []byte("Hello!")

data, err := serializer.Serialize(packet)      // Binary format
recovered, err := serializer.Deserialize(data) // Round-trip
```

### ۲. امضا و تأیید Ed25519

- **قالب کلید**: دانه 32 بایتی (خصوصی)، کلید عمومی 32 بایتی، امضای 64 بایتی
- **کتابخانه استاندارد**: از `crypto/ed25519` استفاده می‌کند (بدون وابستگی‌های خارجی)

**مثال:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### ۳. پروتکل سیگنال (X3DH + چرخ لنگر متقارن)

پروتکل سیگنال را برای رمزنگاری سرتاسری پیاده‌سازی می‌کند:

- **توافق کلید**: ECDH P-256 با استفاده از `crypto/ecdh`
- **مشتق‌سازی کلید**: HKDF-SHA256 با استفاده از `golang.org/x/crypto/hkdf`
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **رمزنگاری**: AES-256-GCM با nonce 12 بایتی، برچسب 16 بایتی
- **چرخ لنگر**: پیشرفت زنجیره HMAC-SHA256
- **خارج از ترتیب**: کلیدهای پیام رد شده (حداکثر 1000)

**مثال:**
```go
aliceService, _ := security.NewSignalProtocolService()
bobService, _ := security.NewSignalProtocolService()

// Alice generates pre-key bundle
aliceBundle, _ := aliceService.GeneratePreKeyBundle("alice")

// Bob establishes session with Alice
bobService.ProcessPreKeyBundle(aliceBundle)

// Alice establishes session with Bob
bobBundle, _ := bobService.GeneratePreKeyBundle("bob")
aliceService.ProcessPreKeyBundle(bobBundle)

// End-to-end encrypted messaging
plaintext := []byte("Secret message")
encrypted, _ := aliceService.Encrypt("bob", plaintext)
decrypted, _ := bobService.Decrypt("alice", encrypted)
```

### ۴. امضای بسته و حذف تکراری Nonce

از حملات بازپخش با TTL 5 دقیقه‌ای روی کش nonce جلوگیری می‌کند:

```go
signer := security.NewPacketSigningService(300) // 300 seconds TTL
defer signer.Close()

// Compute signable data (SHA256 of payload + header fields)
signableData := signer.ComputeSignableData(
    nonce, timestamp, packetType, sourceUhid, destUhid, payload, ttl, priority)

// Track nonces for deduplication
signer.RecordNonce(sourceUhid, nonce)
isDuplicate := signer.IsNonceSeen(sourceUhid, nonce)
```

### ۵. حمل‌ونقل درون‌فرایندی

حمل‌ونقل مبتنی بر حافظه برای آزمون و ارتباط گره محلی:

```go
inProcTransport := transport.NewInProcessTransport()

// Register peers
aliceRx, _ := inProcTransport.RegisterPeer("alice", 10) // buffered channel
bobRx, _ := inProcTransport.RegisterPeer("bob", 10)

// Send and receive
ctx := context.Background()
inProcTransport.SendAsync(ctx, "bob", []byte("Hello!"))
message := <-bobRx

// Properties
fmt.Println(inProcTransport.Name())                // "InProcess"
fmt.Println(inProcTransport.IsAvailable())         // true
fmt.Println(inProcTransport.MaxBandwidthBps())     // 1000000
fmt.Println(inProcTransport.IsConnected("bob"))    // true
```

### ۶. مدل‌های دامنه

ساختارهای کامل برای شبکه‌سازی مِش:

```go
// Node in the mesh
node := &models.AetherMeshNode{
    UHID: "node-alice-001",
    IdentityKey: publicKey,
    Capabilities: models.CapabilityBLE | models.CapabilityRelay,
    IsLocal: true,
}

// Route to destination
route := &models.RouteEntry{
    DestinationUhid: "node-bob",
    NextHop: "node-bob",
    HopCount: 1,
    ExpiresAt: time.Now().Add(5 * time.Minute),
    QualityScore: 85,
}

// DTN bundle for store-and-forward
bundle := &models.DtnBundle{
    ID: uuid.New().String(),
    SenderUhid: "alice",
    RecipientUhid: "bob",
    Priority: models.DtnPriorityHigh,
    Status: models.DtnStatusPending,
}

// Emergency alert
alert := &models.SosAlert{
    SenderUhid: "alice",
    Message: "Emergency! Need help!",
    Latitude: -33.9249,
    Longitude: 18.4241,
}
```

## ثوابت پروتکل

تمام ثوابت از مشخصات پروتکل (بخش ضمیمه A):

```go
// Routing
DefaultTtl = 7
SosTtl = 15
RouteTimeoutMs = 5000

// BLE Discovery
BleScanOnMs = 2000
BleScanOffMs = 8000
BleUuidRotationSeconds = 900

// Security
MaxPacketAgeSeconds = 300
MaxSkippedKeys = 1000
AesGcmNonceSize = 12
AesGcmTagSize = 16

// DTN
DtnBundleTtlHours = 72
DtnMaxCopies = 3
DtnMaxBundlesPerNode = 50

// Voice, Streaming, Presence constants...
```

## اجرای نسخه نمایشی

برنامه نمایشی تمام ویژگی‌های اصلی را نشان می‌دهد:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**خروجی نسخه نمایشی:**
```
========================================
Aether Protocol - Go Implementation Demo
========================================

[ DEMO 1: Packet Serialization ]
  Original Packet: [Data] ... src=node-alice-001 dst=node-bob-001
  Payload: Hello, Aether!
  Serialized size: 95 bytes
  Deserialized Packet: [Data] ...
  Payload: Hello, Aether!
  ✓ Round-trip serialization successful!

[ DEMO 2: Ed25519 Signing ]
  Generated Ed25519 Key Pair:
    Private Key (seed): 32 bytes
    Public Key: 32 bytes
  Signed message: Important mesh packet signature
  Signature: 64 bytes
  Signature verification: true
  Verification with tampered data: false (should be false)
  ✓ Ed25519 signing verification successful!

[ DEMO 3: Signal Protocol - Session Establishment ]
  Creating Signal Protocol services for Alice and Bob...
  ✓ Alice generated pre-key bundle
  ✓ Bob established session with Alice
  ✓ Bob generated pre-key bundle
  ✓ Alice established session with Bob
  ✓ Alice encrypted message: Hello Bob, this is Alice!
    Ciphertext: 41 bytes
  ✓ Bob decrypted message: Hello Bob, this is Alice!
  ✓ Bob encrypted message: Hi Alice, I received your message!
  ✓ Alice decrypted message: Hi Alice, I received your message!
  ✓ Signal Protocol end-to-end encryption successful!

[ DEMO 4: In-Process Transport ]
  Transport: InProcess
  Available: true
  Max Bandwidth: 1000000 bps
  Max Range: 100 meters
  ✓ Registered peer: alice
  ✓ Registered peer: bob
  ✓ Alice sent: Hello Bob! (success: true)
  ✓ Bob received: Hello Bob!
  ✓ Bob sent: Hi Alice! (success: true)
  ✓ Alice received: Hi Alice!
  Alice connected to bob: true
  Bob connected to alice: true
  ✓ In-process transport successful!

[ DEMO 5: Packet Signing & Nonce Deduplication ]
  Computed signable data: 152 bytes
  ✓ Recorded nonce for replay prevention
  Nonce seen (should be true): true
  Different nonce seen (should be false): false
  ✓ Nonce deduplication working correctly!

========================================
All demos completed successfully!
========================================
```

## سازگاری قالب سیمی

تمام سریال‌سازی از **کدگذاری little-endian** استفاده می‌کند تا با پیاده‌سازی مرجع C# مطابقت داشته باشد:

- **اعداد صحیح**: `encoding/binary.LittleEndian`
- **UUIDها**: قالب UUID استاندارد 16 بایتی
- **رشته‌ها**: کدگذاری UTF-8 با پیشوند طول 2 بایتی (uint16) یا 4 بایتی (uint32)
- **بایت‌ها**: پیشوند طول (2 بایت یا 4 بایت) به دنبال داده خام

این امر سازگاری بایت‌به‌بایت هنگام تبادل بسته‌ها بین پیاده‌سازی‌های Go و C# را تضمین می‌کند.

## وابستگی‌ها

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

تمام اوابع اولیه رمزنگاری از کتابخانه استاندارد Go (`crypto/*`) به علاوه `golang.org/x/crypto` برای HKDF و ECDH P-256 استفاده می‌کنند.

## ویژگی‌های امنیتی

1. **پاک‌سازی کلید**: تمام کلیدهای میانی با `ZeroMemory()` به‌صورت امن پاک می‌شوند
2. **بدون رمزنگاری پشتیبان**: پیام‌ها نیازمند جلسات برقرارشده هستند؛ بدون پشتیبان مشتق‌شده از UHID
3. **جلوگیری از بازپخش**: nonce 8 بایتی + مهر زمانی + کش حذف تکراری 5 دقیقه‌ای
4. **شکاف‌های شمارنده**: پیام‌های خارج از ترتیب تا MaxSkippedKeys (1000) پشتیبانی می‌شوند
5. **تأیید امضا**: تمام پاسخ‌های مسیر و بسته‌های کلید پیش‌درآمد با Ed25519 تأیید می‌شوند

## یادداشت‌های کارایی

- **سریال‌سازی بسته**: ~1-2µs در هر بسته (آزمون با محموله‌های 100 بایتی)
- **امضای Ed25519**: ~50µs در هر امضا
- **رمزنگاری پروتکل سیگنال**: ~100µs در هر پیام
- **پاک‌سازی nonce**: goroutine پس‌زمینه هر 60 ثانیه اجرا می‌شود

## آزمون

برنامه نمایشی موارد زیر را نشان می‌دهد:
- ✓ سریال‌سازی رفت‌وبرگشت بسته
- ✓ تأیید امضای Ed25519
- ✓ برقراری جلسه پروتکل سیگنال
- ✓ رمزنگاری/رمزگشایی سرتاسری
- ✓ ارتباط حمل‌ونقل درون‌فرایندی
- ✓ حذف تکراری nonce

تمام عملیات با استفاده از `sync.RWMutex` و `sync.Map` در صورت مناسب، ایمن برای goroutine هستند.

## یادداشت‌های پیاده‌سازی

1. **قالب UUID**: از `github.com/google/uuid` برای سازگاری RFC 4122 استفاده می‌کند
2. **مدیریت کلید**: بدون ذخیره‌سازی کلید خارجی؛ کلیدها برای نسخه نمایشی در حافظه نگهداری می‌شوند. محصول نهایی باید از ذخیره‌سازی امن استفاده کند.
3. **رابط حمل‌ونقل**: قابل گسترش برای BLE، Wi-Fi Direct و سایر لایه‌های فیزیکی
4. **جلسات سیگنال**: به ازای هر همتا بدون پشتیبان پایگاه داده در این پیاده‌سازی ذخیره می‌شود
5. **مدیریت خطا**: تمام عملیات رمزنگاری خطا برمی‌گردانند؛ فراخواننده باید خرابی‌ها را مدیریت کند

## بهبودهای آینده

- [ ] ذخیره‌سازی SQLite برای مسیرها و جلسات
- [ ] پیاده‌سازی حمل‌ونقل BLE
- [ ] پیاده‌سازی حمل‌ونقل Wi-Fi Direct
- [ ] پیاده‌سازی پروتکل مسیریابی AODV
- [ ] مسیریابی همه‌گیر DTN
- [ ] سرویس بیکن حضور و کشف
- [ ] پشتیبانی از صدا و استریم
- [ ] الگوریتم Double Ratchet برای محرمانگی رو به جلو با اطمینان بالاتر

## مجوز

SPDX-License-Identifier: MIT

</div>
