<div dir="rtl">

# بروتوكول Aether - تطبيق Go

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/go/README.md) · [日本語](../../ja/go/README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](../../ru/go/README.md) · [فارسی](../../fa/go/README.md) · [한국어](../../ko/go/README.md)

تطبيق Go كامل لبروتوكول شبكات الميش Aether، متوافق سلكياً مع تطبيق C# المرجعي.

## نظرة عامة

تُطبّق هذه الوحدة بروتوكول شبكات الميش اللامركزي Aether للبيئات ذات الاتصال المتقطع أو المنعدم بالإنترنت. توفر:

- **تسلسل الحزم**: تنسيق سلكي ثنائي متوافق مع تطبيق C# المرجعي (ترميز little-endian)
- **توقيع Ed25519**: مصادقة تشفيرية للحزم
- **بروتوكول Signal**: اتفاقية مفتاح X3DH + ضامة تماثلية للتشفير الكامل من طرف إلى طرف
- **خدمة توقيع الحزم**: إلغاء تكرار nonce مع TTL مدته 5 دقائق لمنع إعادة التشغيل
- **النقل داخل العملية**: نقل مستند إلى الذاكرة للاختبار والاتصال بين العمليات
- **النماذج**: هياكل AetherMeshNode وPeerInfo وRouteEntry وDtnBundle وSosAlert
- **ثوابت البروتوكول**: جميع ثوابت التوجيه والاكتشاف والأمان والنقل

## هيكل الوحدة

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

## الميزات الرئيسية

### 1. تسلسل الحزم (Little-Endian)

يطابق التنسيق السلكي C# تماماً باستخدام ترميز little-endian لجميع الأعداد الصحيحة متعددة البايت:

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

### 2. التوقيع والتحقق Ed25519

- **تنسيق المفتاح**: بذرة 32 بايت (خاص)، مفتاح عام 32 بايت، توقيع 64 بايت
- **المكتبة القياسية**: يستخدم `crypto/ed25519` (بدون تبعيات خارجية)

**مثال:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. بروتوكول Signal (X3DH + ضامة تماثلية)

يُطبّق بروتوكول Signal للتشفير الكامل من طرف إلى طرف:

- **اتفاقية المفتاح**: ECDH P-256 باستخدام `crypto/ecdh`
- **اشتقاق المفتاح**: HKDF-SHA256 باستخدام `golang.org/x/crypto/hkdf`
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **التشفير**: AES-256-GCM مع nonce بحجم 12 بايت وعلامة 16 بايت
- **الضامة**: تقدّم سلسلة HMAC-SHA256
- **خارج الترتيب**: مفاتيح الرسائل المُتجاوزة (بحد أقصى 1000)

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

### 4. توقيع الحزم وإلغاء تكرار Nonce

يمنع هجمات إعادة التشغيل مع TTL مدته 5 دقائق على ذاكرة التخزين المؤقت لـ nonce:

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

### 5. النقل داخل العملية

نقل مستند إلى الذاكرة للاختبار والاتصال المحلي بين العقد:

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

### 6. نماذج المجال

هياكل كاملة لشبكات الميش:

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

## ثوابت البروتوكول

جميع الثوابت من مواصفات البروتوكول (القسم ملحق أ):

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

## تشغيل العرض التوضيحي

يوضح برنامج العرض التوضيحي جميع الميزات الرئيسية:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**خرج العرض التوضيحي:**
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

## توافق التنسيق السلكي

يستخدم جميع التسلسل **ترميز little-endian** ليتطابق مع تطبيق C# المرجعي:

- **الأعداد الصحيحة**: `encoding/binary.LittleEndian`
- **المعرفات UUID**: تنسيق UUID القياسي بحجم 16 بايت
- **السلاسل النصية**: مرمّزة بـ UTF-8 مع بادئة طول 2 بايت (uint16) أو 4 بايت (uint32)
- **البايتات**: مسبوقة بالطول (2 بايت أو 4 بايت) تليها البيانات الخام

يضمن ذلك التوافق البايت-بالبايت عند تبادل الحزم بين تطبيقَي Go وC#.

## التبعيات

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

تستخدم جميع البدائيات التشفيرية المكتبة القياسية لـ Go (`crypto/*`) بالإضافة إلى `golang.org/x/crypto` لـ HKDF وECDH P-256.

## ميزات الأمان

1. **إلغاء تصفير المفاتيح**: تُمسَح جميع المفاتيح الوسيطة بأمان باستخدام `ZeroMemory()`
2. **لا تشفير احتياطي**: تتطلب الرسائل جلسات مُنشأة؛ لا يوجد احتياطي مشتق من UHID
3. **منع إعادة التشغيل**: nonce مكون من 8 بايت + طابع زمني + ذاكرة تخزين مؤقت لإلغاء التكرار مدتها 5 دقائق
4. **فجوات العدّاد**: الرسائل خارج الترتيب مدعومة حتى MaxSkippedKeys (1000)
5. **التحقق من التوقيع**: جميع ردود المسار وحزم المفاتيح المسبقة مُوثَّقة بـ Ed25519

## ملاحظات الأداء

- **تسلسل الحزم**: ~1-2µs لكل حزمة (اختُبر بحمولات 100 بايت)
- **توقيع Ed25519**: ~50µs لكل توقيع
- **تشفير بروتوكول Signal**: ~100µs لكل رسالة
- **تنظيف إلغاء تكرار nonce**: goroutine في الخلفية يعمل كل 60 ثانية

## الاختبار

يوضح برنامج العرض التوضيحي:
- ✓ تسلسل الحزم ذهاباً وإياباً
- ✓ التحقق من توقيع Ed25519
- ✓ إنشاء جلسة بروتوكول Signal
- ✓ التشفير/فك التشفير من طرف إلى طرف
- ✓ الاتصال عبر النقل داخل العملية
- ✓ إلغاء تكرار Nonce

جميع العمليات آمنة للـ goroutine باستخدام `sync.RWMutex` و`sync.Map` حيثما اقتضى الأمر.

## ملاحظات التطبيق

1. **تنسيق UUID**: يستخدم `github.com/google/uuid` للتوافق مع RFC 4122
2. **إدارة المفاتيح**: لا تخزين خارجي للمفاتيح؛ تُحفظ المفاتيح في الذاكرة للعرض التوضيحي. يجب أن يستخدم الإنتاج تخزيناً آمناً.
3. **واجهة النقل**: قابلة للتوسع لـ BLE وWi-Fi Direct وغيرها من الطبقات الفيزيائية
4. **جلسات Signal**: مستمرة لكل نظير بدون قاعدة بيانات داعمة في هذا التطبيق
5. **معالجة الأخطاء**: جميع العمليات التشفيرية تُرجع أخطاء؛ يجب على المستدعي معالجة الإخفاقات

## التحسينات المستقبلية

- [ ] استمرارية SQLite للمسارات والجلسات
- [ ] تطبيق نقل BLE
- [ ] تطبيق نقل Wi-Fi Direct
- [ ] تطبيق بروتوكول توجيه AODV
- [ ] توجيه وبائي DTN
- [ ] خدمة منارة الحضور والاكتشاف
- [ ] دعم الصوت والبث
- [ ] خوارزمية Double Ratchet لسرية تقدمية أعلى ضماناً

## الرخصة

SPDX-License-Identifier: MIT

</div>
