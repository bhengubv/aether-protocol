# Aether Mesh Networking Protocol - реализация на C

[English](../../../../c/README.md) · [Français](../../fr/c/README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Высокопроизводительная реализация протокола Aether для ячеистых сетей на языке C, ориентированная на встраиваемые устройства. Разработана для устройств с ограниченными ресурсами, таких как ESP32 и nRF52; поддерживает подпись Ed25519, шифрование AES-256-GCM и маршрутизацию на основе AODV.

## Обзор

Aether — децентрализованный протокол ячеистых сетей для сред с нестабильным или отсутствующим подключением к интернету. Данная реализация на C обеспечивает:

- **Сериализацию/десериализацию протокола** — проводной формат с прямым порядком байт, совместимый с эталонной реализацией на C#
- **Криптографические операции** — подписи Ed25519, шифрование AES-256-GCM, HMAC-SHA256, HKDF-SHA256 (через libsodium)
- **Подпись пакетов** — детерминированное построение подписываемых данных согласно спецификации протокола
- **Абстракцию транспорта** — паттерн vtable для произвольных реализаций транспорта
- **Внутрипроцессный транспорт** — встроенный тестовый транспорт для многоузловых сценариев
- **Дизайн, ориентированный на встраиваемые системы** — буферы фиксированного размера там, где возможно, минимальное выделение памяти, операции с постоянным временем выполнения

## Требования к сборке

- **CMake** ≥ 3.16
- **Компилятор C11** (gcc, clang и др.)
- **libsodium** — для криптографических операций
- **POSIX threads** (pthread)

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

Библиотека предназначена для использования в качестве компонента ESP-IDF:

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

## Структура

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

## Быстрый старт

### Сборка и запуск демонстрации

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

Ожидаемый вывод демонстрирует:
1. Генерацию ключей Ed25519
2. Создание и подпись пакета
3. Сериализацию в проводной формат
4. Десериализацию
5. Шифрование/расшифрование AES-256-GCM
6. Аутентификацию HMAC-SHA256
7. Деривацию ключей HKDF

### Запуск модульных тестов

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Использование в вашем коде

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

## Справочник по API

### Протокол

#### Управление пакетами
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — Создать новый пакет
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — Освободить пакет
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — Клонировать пакет

#### Сериализация
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — Сериализовать в проводной формат
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — Десериализовать из проводного формата
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — Оценить размер в проводном формате

#### Поля пакета
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Установить источник
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Установить назначение
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Установить полезную нагрузку
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Установить подпись

#### Валидация
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — Проверить, истёк ли срок действия
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — Проверить, что TTL > 0

#### Данные для подписи
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — Получить детерминированные байты для подписи (вызывающий обязан освободить память)

### Безопасность

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — Сгенерировать ключи 32+32 байта
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Подписать (результат — 64 байта)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Проверить подпись

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Зашифровать (nonce генерируется автоматически, если NULL)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Расшифровать

#### HMAC и хеш
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 байта)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 байта)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Утилиты
- `void aethernet_zeroize(void *mem, size_t len)` — Очистка памяти за постоянное время
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — Криптографически случайные байты

### Транспорт

#### Общие функции
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Отправить данные
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — Проверить соединение
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — Зарегистрировать обратный вызов
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — Освободить ресурсы

#### Внутрипроцессный транспорт
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — Создать общий внутрипроцессный транспорт
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — Зарегистрировать узел
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — Отменить регистрацию узла

## Соответствие проводному формату

Данная реализация строго следует спецификации протокола с **прямым порядком байт** для многобайтовых целых чисел:

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

Пакеты, сериализованные этой реализацией на C, на 100% совместимы с эталонной реализацией на C#.

## Соображения безопасности

### Криптографические библиотеки
- **libsodium** (libsodium.org) для всех криптографических операций
- Подписи и верификация Ed25519
- Аутентифицированное шифрование AES-256-GCM
- HMAC-SHA256 и SHA-256
- Деривация ключей HKDF-SHA256
- Криптографически стойкая генерация случайных чисел

### Обнуление ключей
Все чувствительные данные (ключи, открытый текст, промежуточные значения) обнуляются в памяти с помощью `sodium_memzero()` сразу после использования. Это предотвращает случайную утечку ключей.

### Валидация пакетов
- Дедупликация на основе временных меток: пакеты старше 300 секунд отклоняются
- Уникальность nonce: случайный nonce длиной 8 байт в каждом пакете
- Валидация TTL: пакеты с TTL=0 отбрасываются
- Проверка подписи: подписи Ed25519 обязательны в протоколе версии 2

## Примечания для встраиваемых устройств

### ESP32
- Требует порт libsodium для ESP-IDF (доступен через компоненты ESP-IDF)
- Оценка фиксированного размера пакета упрощает выделение памяти
- Использует потоки POSIX для операций с мьютексом
- По возможности заранее выделяйте буферы в стеке

### nRF52
- Аналогично ESP32
- Транспортный уровень BLE GATT может быть реализован через vtable транспорта
- Рассмотрите использование RTOS, например FreeRTOS, для обработки нескольких пакетов

### Использование памяти
- Минимальный пакет: ~52 байта
- Максимальный пакет: 65 КБ (настраивается через `AETHERNET_MAX_PAYLOAD_LEN`)
- Таблица одноранговых узлов на 256 записей: ~32 КБ
- Один ячеистый пакет в памяти: ~8 КБ (наихудший случай с максимальными полями)

## Производительность

На современном процессоре x86-64 (Intel Core i9):
- **Сериализация**: ~1–2 мкс на пакет
- **Десериализация**: ~1–2 мкс на пакет
- **Ed25519 sign**: ~100 мкс
- **Ed25519 verify**: ~300 мкс
- **AES-256-GCM encrypt**: ~1 мкс на КБ
- **SHA-256**: ~0.5 мкс на КБ

## Тестирование

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Тесты охватывают:
- Создание и клонирование пакетов
- Циклы сериализации/десериализации
- Подпись и верификацию Ed25519
- Шифрование/расшифрование AES-GCM
- Вычисление HMAC-SHA256
- Деривацию ключей HKDF
- Валидацию TTL и срока действия
- Детерминизм подписываемых данных

## Интеграция с экосистемой Aether

Данная библиотека на C предназначена для интеграции с:
- **AetherNetAPI** (C#) — серверный ретранслятор ячеистой сети и аналитика
- **AetherNet.Core** (C#) — эталонная реализация (совместимый проводной формат)
- **Meshtastic** — прошивка для ячеистого радио с открытым исходным кодом
- **esp-idf** — фреймворк Espressif для разработки IoT
- Пользовательские встраиваемые приложения

## Лицензия

SPDX-License-Identifier: MIT

Полный текст см. в файле LICENSE.

## Участие в разработке

Вклад приветствуется! Пожалуйста, убедитесь, что:
- Все тесты проходят (`ctest --output-on-failure`)
- Код соответствует стандарту C11
- Проводной формат точно совпадает с эталонной реализацией на C#
- Все чувствительные данные обнуляются
- Документация обновлена

## Ссылки

- Protocol Spec: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# Reference: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
