<div dir="rtl">

# بروتوكول Aether لشبكات الميش - تطبيق C

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

تطبيق C عالي الأداء ومناسب للأنظمة المدمجة لبروتوكول شبكات الميش Aether. مصمم للأجهزة ذات الموارد المحدودة مثل ESP32 وnRF52، مع دعم كامل لتوقيع Ed25519 وتشفير AES-256-GCM والتوجيه المستند إلى AODV.

## نظرة عامة

Aether هو بروتوكول شبكات ميش لامركزي للبيئات ذات الاتصال المتقطع أو المنعدم بالإنترنت. يوفر تطبيق C هذا:

- **تسلسل/إلغاء تسلسل البروتوكول** — تنسيق سلكي little-endian يطابق تطبيق C# المرجعي
- **العمليات التشفيرية** — توقيعات Ed25519 وتشفير AES-256-GCM وHMAC-SHA256 وHKDF-SHA256 (عبر libsodium)
- **توقيع الحزم** — بناء حتمي للبيانات القابلة للتوقيع وفق مواصفات البروتوكول
- **تجريد النقل** — نمط vtable لتطبيقات النقل المخصصة
- **نقل داخل العملية** — نقل اختبار مدمج لسيناريوهات العقد المتعددة
- **تصميم أولوية الأنظمة المدمجة** — مخازن ذات حجم ثابت حيثما أمكن، وتخصيص ذاكرة أدنى، وعمليات ذات وقت ثابت

## متطلبات البناء

- **CMake** ≥ 3.16
- **مترجم C11** (gcc أو clang أو غيرهما)
- **libsodium** — للعمليات التشفيرية
- **خيوط POSIX** (pthread)

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

تم تصميم المكتبة لتُستخدم كمكوّن ESP-IDF:

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

## الهيكل

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

## البداية السريعة

### بناء وتشغيل العرض التوضيحي

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

يوضح الخرج المتوقع:
1. توليد مفتاح Ed25519
2. إنشاء الحزمة وتوقيعها
3. التسلسل إلى التنسيق السلكي
4. إلغاء التسلسل
5. التشفير/فك التشفير AES-256-GCM
6. مصادقة HMAC-SHA256
7. اشتقاق مفتاح HKDF

### تشغيل اختبارات الوحدة

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### الاستخدام في الكود الخاص بك

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

## مرجع واجهة برمجة التطبيقات

### البروتوكول

#### إدارة الحزم
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — إنشاء حزمة جديدة
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — تحرير حزمة
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — استنساخ حزمة

#### التسلسل
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — التسلسل إلى التنسيق السلكي
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — إلغاء التسلسل من التنسيق السلكي
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — تقدير الحجم السلكي

#### حقول الحزمة
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — تعيين المصدر
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — تعيين الوجهة
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — تعيين الحمولة
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — تعيين التوقيع

#### التحقق
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — التحقق من انتهاء الصلاحية
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — التحقق من أن TTL > 0

#### بيانات التوقيع
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — الحصول على البايتات الحتمية القابلة للتوقيع (يجب على المستدعي تحريرها)

### الأمان

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — توليد مفاتيح بحجم 32+32 بايت
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — التوقيع (ينتج 64 بايت)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — التحقق

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — التشفير (يُولَّد nonce تلقائياً إذا كانت NULL)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — فك التشفير

#### HMAC والتجزئة
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 بايت)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 بايت)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### الأدوات المساعدة
- `void aethernet_zeroize(void *mem, size_t len)` — مسح الذاكرة في وقت ثابت
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — بايتات عشوائية تشفيرياً

### النقل

#### الدوال العامة
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — إرسال البيانات
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — التحقق من الاتصال
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — تسجيل callback
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — التنظيف

#### النقل داخل العملية
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — إنشاء نقل داخل العملية مشترك
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — تسجيل عقدة
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — إلغاء تسجيل عقدة

## توافق التنسيق السلكي

يتبع هذا التطبيق بدقة مواصفات البروتوكول مع أعداد صحيحة متعددة البايت **little-endian**:

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

الحزم المُسلسَلة بتطبيق C هذا متوافقة 100% مع تطبيق C# المرجعي.

## اعتبارات الأمان

### المكتبات التشفيرية
- **libsodium** (libsodium.org) لجميع العمليات التشفيرية
- توقيعات والتحقق Ed25519
- التشفير المُوثَّق AES-256-GCM
- HMAC-SHA256 وSHA-256
- اشتقاق مفتاح HKDF-SHA256
- توليد أرقام عشوائية آمنة تشفيرياً

### إلغاء تصفير المفاتيح
يُزال جميع المواد الحساسة (المفاتيح والنص العادي والقيم الوسيطة) من الذاكرة باستخدام `sodium_memzero()` فور الانتهاء منها. هذا يمنع تسريب المفاتيح العرضي.

### التحقق من صحة الحزم
- إلغاء التكرار المستند إلى الطابع الزمني: تُرفض الحزم الأقدم من 300 ثانية
- تفرد nonce: nonce عشوائي مكون من 8 بايت في كل حزمة
- التحقق من TTL: تُسقط الحزم ذات TTL=0
- التحقق من التوقيع: توقيعات Ed25519 إلزامية في البروتوكول الإصدار v2

## ملاحظات الأجهزة المدمجة

### ESP32
- يتطلب منفذ libsodium لـ ESP-IDF (متاح عبر مكونات ESP-IDF)
- يبسّط تقدير حجم الحزمة الثابت تخصيص الذاكرة
- يستخدم خيوط POSIX لعمليات mutex
- خصّص المخازن المؤقتة مسبقاً على المكدس حيثما أمكن

### nRF52
- مشابه لـ ESP32
- يمكن تطبيق طبقة نقل BLE GATT عبر vtable النقل
- فكر في استخدام RTOS مثل FreeRTOS للتعامل مع حزم متعددة

### استخدام الذاكرة
- الحزمة الدنيا: ~52 بايت
- الحزمة القصوى: 65 كيلوبايت (قابل للتهيئة عبر `AETHERNET_MAX_PAYLOAD_LEN`)
- جدول نظير مكون من 256 عقدة: ~32 كيلوبايت
- حزمة ميش واحدة في الذاكرة: ~8 كيلوبايت (أسوأ الحالات مع الحقول القصوى)

## الأداء

على جهاز x86-64 حديث (Intel Core i9):
- **التسلسل**: ~1-2 µs لكل حزمة
- **إلغاء التسلسل**: ~1-2 µs لكل حزمة
- **توقيع Ed25519**: ~100 µs
- **التحقق Ed25519**: ~300 µs
- **تشفير AES-256-GCM**: ~1 µs لكل كيلوبايت
- **SHA-256**: ~0.5 µs لكل كيلوبايت

## الاختبار

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

تغطي الاختبارات:
- إنشاء الحزم واستنساخها
- جولات تسلسل كاملة
- توقيع والتحقق Ed25519
- التشفير/فك التشفير AES-GCM
- حساب HMAC-SHA256
- اشتقاق مفتاح HKDF
- التحقق من TTL وانتهاء الصلاحية
- حتمية البيانات القابلة للتوقيع

## التكامل مع نظام Aether البيئي

تم تصميم مكتبة C هذه للتكامل مع:
- **AetherNetAPI** (C#) — ترحيل الميش من جانب الخادم والتحليلات
- **AetherNet.Core** (C#) — التطبيق المرجعي (تنسيق سلكي قابل للتشغيل البيني)
- **Meshtastic** — برمجيات راديو الميش مفتوحة المصدر
- **esp-idf** — إطار تطوير إنترنت الأشياء من Espressif
- التطبيقات المدمجة المخصصة

## الرخصة

SPDX-License-Identifier: MIT

راجع ملف LICENSE للنص الكامل.

## المساهمة

المساهمات مرحب بها! يُرجى التأكد من:
- اجتياز جميع الاختبارات (`ctest --output-on-failure`)
- أن الكود متوافق مع C11
- مطابقة التنسيق السلكي للتطبيق المرجعي C# تماماً
- إلغاء تصفير جميع البيانات الحساسة
- تحديث التوثيق

## المراجع

- مواصفات البروتوكول: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- المرجع C#: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561

</div>
