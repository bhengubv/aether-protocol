<div dir="rtl">

# پروتکل مِش Aether - پیاده‌سازی TypeScript

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](README.md) · [한국어](../../ko/typescript/README.md)

یک پیاده‌سازی کامل TypeScript/Node.js از پروتکل شبکه مِش Aether، که به طور کامل با فرمت سیمی پیاده‌سازی مرجع C# سازگار است.

## ویژگی‌ها

- **سریال‌سازی MeshPacket**: فرمت سیمی باینری که دقیقاً با C# مطابقت دارد (اعداد صحیح little-endian، رشته‌ها/آرایه‌های دارای پیشوند طول)
- **امضای Ed25519**: با استفاده از TweetNaCl برای تولید و تأیید امضا
- **پروتکل Signal**: تبادل کلید X3DH با اشتقاق کلید HKDF-SHA256 و رمزنگاری AES-256-GCM
- **امضای بسته**: ساخت کامل داده‌های قابل امضا طبق مشخصات پروتکل (بخش ۲.۳)
- **انتقال درون‌فرآیندی**: شبکه شبیه‌سازی‌شده برای آزمایش و نمایش
- **رچت متقارن**: پیشبرد کلید زنجیره HMAC-SHA256 با پشتیبانی از پیام‌های خارج از نوبت
- **ثابت‌های پروتکل**: تمام ۶۰+ ثابت از بخش A مشخصات پروتکل

## نصب

```bash
npm install
```

## استفاده

### ساخت

```bash
npm run build
```

### اجرای نمایش

```bash
npm run dev
```

نمایش:
۱. دو گره در یک شبکه شبیه‌سازی‌شده درون‌فرآیندی ایجاد می‌کند
۲. جفت کلیدهای Ed25519 تولید می‌کند
۳. نشست‌های پروتکل Signal برقرار می‌کند
۴. یک بسته ایجاد، امضا و تأیید می‌کند
۵. بسته‌ها را سریال‌سازی و حذف سریال‌سازی می‌کند
۶. پیام‌ها را رمزنگاری و رمزگشایی می‌کند
۷. بسته‌ها را از طریق لایه انتقال ارسال می‌کند

### مثال‌های API

#### ایجاد و امضای بسته

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

#### رمزنگاری پروتکل Signal

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

#### سریال‌سازی بسته

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### انتقال درون‌فرآیندی

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

## انطباق با پروتکل

### فرمت سیمی

تمام اعداد صحیح چندبایتی **little-endian** هستند:
- شناسه بسته: UUID 16 بایتی
- TTL، TimestampMs: int32/int64 LE
- طول رشته: uint16 LE (نه uint32)
- طول payload: int32 LE

### امضای بسته (بخش ۲.۳)

فرمت داده‌های قابل امضا:
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

### پروتکل Signal (بخش ۴)

- **تبادل کلید**: X3DH با ECDH P-256
- **HKDF**: SHA256 با salt="AetherNetSignal"
- **رشته‌های Info**: "aether-root-v1"، "aether-chain-send-v1"، "aether-chain-recv-v1"
- **رمزنگاری**: AES-256-GCM با nonce 12 بایتی، tag 16 بایتی
- **رچت زنجیره**: HMAC-SHA256 با پیشبرد شمارنده

## انواع بسته

تمام ۲۳ نوع بسته تعریف شده‌اند:
- RouteRequest (1) - درخواست مسیر AODV
- RouteReply (2) - پاسخ مسیر AODV
- Data (3) - داده برنامه
- Ack (4) - تأییدیه تحویل
- SosBroadcast (5) - پخش اضطراری
- ... و ۱۸ مورد دیگر (به مشخصات پروتکل مراجعه کنید)

## ویژگی‌های امنیتی

- **امضاهای Ed25519**: تمام بسته‌ها طبق پروتکل v2 امضا می‌شوند
- **AES-256-GCM**: کلیدهای به‌ازای هر پیام با nonce های منحصربه‌فرد
- **پیشگیری از بازپخش**: nonce تصادفی ۸ بایتی + اعتبارسنجی مهرزمانی
- **محرمانگی رو به جلو**: رچت متقارن کلیدهای زنجیره را پیش می‌برد
- **رمزگشایی خارج از نوبت**: حافظه پنهان کلید پیام رد شده (تا ۱۰۰۰)

## ساختار پروژه

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

## آزمایش

نمایش (`npm run dev`) تمام ویژگی‌های اصلی را آزمایش می‌کند:
- ایجاد و سریال‌سازی بسته (دور کامل)
- تولید کلید Ed25519 و تأیید امضا
- برقراری نشست پروتکل Signal
- رمزنگاری و رمزگشایی پیام
- تحویل از طریق انتقال درون‌فرآیندی

برای آزمون‌های واحد، با Jest یا runner آزمون مشابه گسترش دهید.

## یادداشت‌های سازگاری

- **فرمت سیمی C#**: ۱۰۰٪ سازگار با PacketSerializer در C#
- **بسته‌های امضاشده**: نسخه پروتکل ۲ با امضاهای Ed25519
- **اشتقاق HKDF**: با استفاده از @noble/hashes (پیاده‌سازی خالص JavaScript)
- **ECDH**: ماژول رمزنگاری داخلی Node.js (منحنی P-256)

## وابستگی‌ها

- **tweetnacl**: امضاهای Ed25519 از طریق TweetNaCl
- **@noble/hashes**: اشتقاق کلید HKDF-SHA256
- **uuid**: تولید و تجزیه UUID
- **node crypto**: AES-256-GCM، HMAC-SHA256، ECDH

## مجوز

MIT - برای جزئیات به فایل LICENSE مراجعه کنید

## مراجع

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [پیاده‌سازی C#](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)

</div>
