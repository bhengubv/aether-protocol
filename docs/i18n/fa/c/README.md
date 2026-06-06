<div dir="rtl">

# پیاده‌سازی پروتکل شبکه‌سازی مِش Aether به زبان C

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](README.md) · [한국어](../../ko/c/README.md)

یک پیاده‌سازی با کارایی بالا و مناسب برای سیستم‌های جاسازی‌شده از پروتکل شبکه‌سازی مِش Aether به زبان C. این پیاده‌سازی برای دستگاه‌هایی با منابع محدود مانند ESP32 و nRF52 طراحی شده و از امضای Ed25519، رمزنگاری AES-256-GCM و مسیریابی مبتنی بر AODV پشتیبانی کامل می‌کند.

## مرور کلی

Aether یک پروتکل شبکه‌سازی مِش غیرمتمرکز برای محیط‌هایی با اتصال اینترنتی متناوب یا فاقد اتصال است. این پیاده‌سازی C موارد زیر را فراهم می‌کند:

- **سریال‌سازی/سریال‌زدایی پروتکل** — قالب سیمی little-endian مطابق با پیاده‌سازی مرجع C#
- **عملیات رمزنگاری** — امضاهای Ed25519، رمزنگاری AES-256-GCM، HMAC-SHA256، HKDF-SHA256 (از طریق libsodium)
- **امضای بسته** — ساخت داده‌های قابل امضای قطعی مطابق مشخصات پروتکل
- **انتزاع حمل‌ونقل** — الگوی vtable برای پیاده‌سازی‌های حمل‌ونقل سفارشی
- **حمل‌ونقل درون‌فرایندی** — حمل‌ونقل آزمایشی داخلی برای سناریوهای چندگرهی
- **طراحی اول برای سیستم‌های جاسازی‌شده** — بافرهای با اندازه ثابت در صورت امکان، تخصیص حافظه حداقلی، عملیات با زمان ثابت

## پیش‌نیازهای ساخت

- **CMake** ≥ 3.16
- **کامپایلر C11** (gcc، clang و غیره)
- **libsodium** — برای عملیات رمزنگاری
- **رشته‌های POSIX** (pthread)

### macOS

```bash
# Install libsodium using Homebrew
brew install libsodium

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### Linux (Ubuntu/Debian)

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF (ESP32)

این کتابخانه برای استفاده به‌عنوان یک کامپوننت ESP-IDF طراحی شده است:

```bash
# In your ESP-IDF project components directory
cp -r /Users/admin/Code/Dev/aether-protocol/c/include aether
cp -r /Users/admin/Code/Dev/aether-protocol/c/src aether/

# Create idf_component.yml
cat > aether/idf_component.yml << 'EOF'
version: "1.0.0"
description: "Aether Mesh Networking Protocol"
dependencies:
  libsodium: "*"
EOF

# In your project's CMakeLists.txt
idf_component_register(
    INCLUDE_DIRS "aether/include"
    SRCS "aether/src/protocol.c" "aether/src/security.c" "aether/src/transport_inprocess.c"
    REQUIRES libsodium pthread
)
```

## ساختار

```
c/
├── include/aether/
│   ├── constants.h       # Protocol constants and limits
│   ├── protocol.h        # Packet structure and serialization
│   ├── security.h        # Cryptographic operations
│   └── transport.h       # Transport abstraction
├── src/
│   ├── protocol.c        # Serialization implementation
│   ├── security.c        # Cryptography using libsodium
│   ├── transport_inprocess.c  # In-process test transport
│   └── demo.c            # Example usage
├── tests/
│   ├── CMakeLists.txt
│   └── test_protocol.c   # Unit tests
├── CMakeLists.txt
└── README.md
```

## شروع سریع

### ساخت و اجرای نسخه نمایشی

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

خروجی مورد انتظار موارد زیر را نشان می‌دهد:
1. تولید کلید Ed25519
2. ایجاد و امضای بسته
3. سریال‌سازی به قالب سیمی
4. سریال‌زدایی
5. رمزنگاری/رمزگشایی AES-256-GCM
6. احراز هویت HMAC-SHA256
7. مشتق‌سازی کلید HKDF

### اجرای آزمون‌های واحد

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### استفاده در کد خود

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    if (!packet) return 1;

    // Set fields
    aethernet_packet_set_source_uhid(packet, "node-alice");
    aethernet_packet_set_destination_uhid(packet, "node-bob");
    aethernet_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    aethernet_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];
        aethernet_ed25519_sign(private_key, signable, signable_len, signature);
        aethernet_packet_set_signature(packet, signature, AETHERNET_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethernet_mesh_packet_t *received = aethernet_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethernet_packet_free(received);
    }

    aethernet_packet_free(packet);
    return 0;
}
```

## مرجع API

### پروتکل

#### مدیریت بسته
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — ایجاد یک بسته جدید
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — آزادسازی یک بسته
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — کلون‌سازی یک بسته

#### سریال‌سازی
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — سریال‌سازی به قالب سیمی
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — سریال‌زدایی از قالب سیمی
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — تخمین اندازه سیمی

#### فیلدهای بسته
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — تنظیم منبع
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — تنظیم مقصد
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — تنظیم محموله
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — تنظیم امضا

#### اعتبارسنجی
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — بررسی انقضا
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — بررسی اینکه TTL > 0 باشد

#### داده‌های امضا
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — دریافت بایت‌های قابل امضای قطعی (فراخواننده باید آزاد کند)

### امنیت

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — تولید کلیدهای 32+32 بایتی
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — امضا (تولید 64 بایت)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — تأیید

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — رمزنگاری (nonce در صورت NULL به‌صورت خودکار تولید می‌شود)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — رمزگشایی

#### HMAC و Hash
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 بایت)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 بایت)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### ابزارها
- `void aethernet_zeroize(void *mem, size_t len)` — پاک‌سازی حافظه با زمان ثابت
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — بایت‌های تصادفی رمزنگاری‌شده

### حمل‌ونقل

#### توابع عمومی
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — ارسال داده
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — بررسی اتصال
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — ثبت callback
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — پاک‌سازی

#### حمل‌ونقل درون‌فرایندی
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — ایجاد حمل‌ونقل درون‌فرایندی مشترک
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — ثبت یک گره
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — لغو ثبت یک گره

## سازگاری قالب سیمی

این پیاده‌سازی به‌طور دقیق از مشخصات پروتکل با اعداد صحیح چندبایتی **little-endian** پیروی می‌کند:

```
[1] protocol_version
[1] type
[16] packet_id (UUID bytes)
[1] priority
[4] ttl (little-endian int32)
[8] timestamp_ms (little-endian int64)
[2] source_uhid_len (little-endian uint16)
[N] source_uhid (UTF-8)
[2] destination_uhid_len (little-endian uint16)
[N] destination_uhid (UTF-8)
[2] nonce_len (little-endian uint16)
[N] packet_nonce
[4] payload_len (little-endian int32)
[N] payload
[2] signature_len (little-endian uint16)
[N] signature (Ed25519, 64 bytes)
```

بسته‌های سریال‌شده توسط این پیاده‌سازی C صددرصد با پیاده‌سازی مرجع C# سازگار هستند.

## ملاحظات امنیتی

### کتابخانه‌های رمزنگاری
- **libsodium** (libsodium.org) برای تمام عملیات رمزنگاری
- امضا و تأیید Ed25519
- رمزنگاری احراز هویت‌شده AES-256-GCM
- HMAC-SHA256 و SHA-256
- مشتق‌سازی کلید HKDF-SHA256
- تولید اعداد تصادفی امن رمزنگاری‌شده

### پاک‌سازی کلید
تمام مواد حساس (کلیدها، متن ساده، مقادیر میانی) بلافاصله پس از استفاده با `sodium_memzero()` از حافظه پاک می‌شوند. این امر از نشت تصادفی کلید جلوگیری می‌کند.

### اعتبارسنجی بسته
- حذف تکراری مبتنی بر زمان: بسته‌های قدیمی‌تر از 300 ثانیه رد می‌شوند
- یکتایی nonce: 8 بایت nonce تصادفی در هر بسته
- اعتبارسنجی TTL: بسته‌های با TTL=0 حذف می‌شوند
- تأیید امضا: امضاهای Ed25519 در پروتکل نسخه 2 اجباری هستند

## یادداشت‌های دستگاه‌های جاسازی‌شده

### ESP32
- نیازمند پورت libsodium برای ESP-IDF (از طریق کامپوننت‌های ESP-IDF موجود است)
- تخمین اندازه ثابت بسته، تخصیص حافظه را ساده می‌کند
- از رشته‌های POSIX برای عملیات mutex استفاده می‌کند
- در صورت امکان، بافرها را از پیش روی پشته تخصیص دهید

### nRF52
- مشابه ESP32
- لایه حمل‌ونقل BLE GATT می‌تواند از طریق vtable حمل‌ونقل پیاده‌سازی شود
- استفاده از RTOS مانند FreeRTOS برای مدیریت چندبسته‌ای توصیه می‌شود

### مصرف حافظه
- حداقل بسته: ~52 بایت
- حداکثر بسته: 65KB (قابل تنظیم از طریق `AETHERNET_MAX_PAYLOAD_LEN`)
- جدول همتا با 256 گره: ~32KB
- یک بسته مِش در حافظه: ~8KB (بدترین حالت با حداکثر فیلدها)

## کارایی

روی یک دستگاه مدرن x86-64 (Intel Core i9):
- **سریال‌سازی**: ~1-2 µs در هر بسته
- **سریال‌زدایی**: ~1-2 µs در هر بسته
- **امضای Ed25519**: ~100 µs
- **تأیید Ed25519**: ~300 µs
- **رمزنگاری AES-256-GCM**: ~1 µs در هر KB
- **SHA-256**: ~0.5 µs در هر KB

## آزمون

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

آزمون‌ها موارد زیر را پوشش می‌دهند:
- ایجاد و کلون‌سازی بسته
- رفت‌وبرگشت سریال‌سازی
- امضا و تأیید Ed25519
- رمزنگاری/رمزگشایی AES-GCM
- محاسبه HMAC-SHA256
- مشتق‌سازی کلید HKDF
- اعتبارسنجی TTL و انقضا
- قطعی‌بودن داده‌های قابل امضا

## یکپارچه‌سازی با اکوسیستم Aether

این کتابخانه C برای یکپارچه‌سازی با موارد زیر طراحی شده است:
- **AetherNetAPI** (C#) — رله مِش سمت سرور و تحلیلگر
- **AetherNet.Core** (C#) — پیاده‌سازی مرجع (قالب سیمی قابل همکاری)
- **Meshtastic** — فریمور رادیو مِش متن‌باز
- **esp-idf** — چارچوب توسعه IoT اسپرسیف
- برنامه‌های جاسازی‌شده سفارشی

## مجوز

SPDX-License-Identifier: MIT

برای متن کامل، فایل LICENSE را ببینید.

## مشارکت

از مشارکت استقبال می‌شود! لطفاً اطمینان حاصل کنید که:
- تمام آزمون‌ها قبول شوند (`ctest --output-on-failure`)
- کد با C11 سازگار باشد
- قالب سیمی دقیقاً با مرجع C# مطابقت داشته باشد
- تمام داده‌های حساس پاک‌سازی شوند
- مستندات به‌روزرسانی شوند

## منابع

- مشخصات پروتکل: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- مرجع C#: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561

</div>
