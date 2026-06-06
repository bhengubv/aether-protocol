<div dir="rtl">

# بروتوكول Aether Mesh - تطبيق TypeScript

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

تطبيق TypeScript/Node.js كامل لبروتوكول شبكات الميش Aether، متوافق تماماً مع تنسيق السلك الخاص بالتطبيق المرجعي C#.

## الميزات

- **تسلسل MeshPacket**: تنسيق ثنائي سلكي يطابق C# تماماً (أعداد صحيحة little-endian، سلاسل/مصفوفات مسبوقة بالطول)
- **توقيع Ed25519**: باستخدام TweetNaCl لتوليد التوقيعات والتحقق منها
- **بروتوكول Signal**: تبادل مفاتيح X3DH مع اشتقاق مفاتيح HKDF-SHA256 وتشفير AES-256-GCM
- **توقيع الحزم**: بناء كامل للبيانات القابلة للتوقيع وفقاً لمواصفات البروتوكول (القسم 2.3)
- **النقل داخل العملية**: شبكة محاكاة للاختبار والعروض التوضيحية
- **الترقيع المتماثل**: تقدم مفتاح سلسلة HMAC-SHA256 مع دعم رسائل خارج الترتيب
- **ثوابت البروتوكول**: جميع الثوابت 60+ من القسم A من PROTOCOL_SPEC

## التثبيت

```bash
npm install
```

## الاستخدام

### البناء

```bash
npm run build
```

### تشغيل العرض التوضيحي

```bash
npm run dev
```

يقوم العرض التوضيحي بما يلي:
1. إنشاء عقدتين في شبكة محاكاة داخل العملية
2. توليد أزواج مفاتيح Ed25519
3. تأسيس جلسات بروتوكول Signal
4. إنشاء حزمة وتوقيعها والتحقق منها
5. تسلسل الحزم وإلغاء تسلسلها
6. تشفير الرسائل وفك تشفيرها
7. إرسال الحزم عبر طبقة النقل

### أمثلة الواجهة البرمجية

#### إنشاء الحزمة وتوقيعها

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### تشفير بروتوكول Signal

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### تسلسل الحزم

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### النقل داخل العملية

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## الامتثال للبروتوكول

### تنسيق السلك

جميع الأعداد الصحيحة متعددة البايتات بترتيب **little-endian**:
- معرف الحزمة: UUID بحجم 16 بايت
- TTL، TimestampMs: int32/int64 LE
- أطوال السلاسل: uint16 LE (وليس uint32)
- طول الحمولة: int32 LE

### توقيع الحزمة (القسم 2.3)

تنسيق البيانات القابلة للتوقيع:
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### بروتوكول Signal (القسم 4)

- **تبادل المفاتيح**: X3DH مع ECDH P-256
- **HKDF**: SHA256 مع ملح="AetherMeshSignal"
- **سلاسل المعلومات**: "aether-root-v1"، "aether-chain-send-v1"، "aether-chain-recv-v1"
- **التشفير**: AES-256-GCM مع nonce بحجم 12 بايت، علامة بحجم 16 بايت
- **ترقيع السلسلة**: HMAC-SHA256 مع تقدم العداد

## أنواع الحزم

جميع أنواع الحزم الـ 23 محددة:
- RouteRequest (1) - طلب مسار AODV
- RouteReply (2) - رد مسار AODV
- Data (3) - بيانات التطبيق
- Ack (4) - إقرار التسليم
- SosBroadcast (5) - بث الطوارئ
- ... و18 نوعاً آخر (راجع مواصفات البروتوكول)

## ميزات الأمان

- **توقيعات Ed25519**: توقيع جميع الحزم وفق البروتوكول v2
- **AES-256-GCM**: مفاتيح لكل رسالة مع nonces فريدة
- **منع الإعادة**: nonce عشوائي بحجم 8 بايتات + التحقق من الطابع الزمني
- **السرية للأمام**: تقدم الترقيع المتماثل في مفاتيح السلسلة
- **فك التشفير خارج الترتيب**: تخزين مؤقت لمفاتيح الرسائل المتخطَّاة (حتى 1000)

## هيكل المشروع

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## الاختبار

يختبر العرض التوضيحي (`npm run dev`) جميع الميزات الرئيسية:
- إنشاء الحزم وتسلسلها (رحلة ذهاباً وإياباً)
- توليد مفاتيح Ed25519 والتحقق من التوقيع
- تأسيس جلسة بروتوكول Signal
- تشفير الرسائل وفك تشفيرها
- تسليم النقل داخل العملية

للاختبارات الوحدوية، قم بالتوسيع باستخدام Jest أو مشغّل اختبارات مماثل.

## ملاحظات التوافق

- **تنسيق سلك C#**: متوافق 100% مع C# PacketSerializer
- **الحزم الموقّعة**: إصدار البروتوكول 2 مع توقيعات Ed25519
- **اشتقاق HKDF**: باستخدام @noble/hashes (تطبيق JavaScript خالص)
- **ECDH**: وحدة تشفير Node.js المدمجة (منحنى P-256)

## التبعيات

- **tweetnacl**: توقيعات Ed25519 عبر TweetNaCl
- **@noble/hashes**: اشتقاق مفاتيح HKDF-SHA256
- **uuid**: توليد UUID وتحليله
- **node crypto**: AES-256-GCM، HMAC-SHA256، ECDH

## الرخصة

MIT - راجع ملف LICENSE

## المراجع

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [تطبيق C#](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)

</div>
