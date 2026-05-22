# Aether Protocol - реализация на Kotlin

[English](../../../../kotlin/README.md) · [Français](../../fr/kotlin/README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Полная, готовая к продакшн-использованию реализация протокола Aether для ячеистых сетей на языке Kotlin, с полной межъязыковой совместимостью проводного формата с эталонной реализацией на C#.

## Обзор

Aether — децентрализованный протокол ячеистых сетей для сред с нестабильным или отсутствующим подключением к интернету. Данная реализация на Kotlin обеспечивает:

- **Совместимость проводного формата** с C# (бинарная сериализация пакетов совпадает точно)
- **Подпись Ed25519** для аутентификации и целостности пакетов
- **Signal Protocol** для сквозного шифрования (согласование ключей X3DH, симметричный трещоточный механизм, AES-256-GCM)
- **Согласование ключей ECDH P-256** для установки сессии
- **Сериализацию/десериализацию пакетов** с многобайтовыми целыми числами в прямом порядке байт
- **Защиту от воспроизведения** с использованием дедупликации nonce
- **Абстракцию транспорта** для BLE, Wi-Fi Direct и внутрипроцессного обмена сообщениями

## Структура проекта

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## Сборка

### Предварительные требования

- JDK 17 или выше
- Gradle 8.0 или выше

### Компиляция

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Запуск демонстрации

```bash
./gradlew run
```

Демонстрация показывает:
1. Генерацию пары ключей Ed25519
2. Создание и обмен пакетом предварительных ключей
3. Установку сессии через Signal Protocol
4. Подпись пакета с помощью Ed25519
5. Сериализацию/десериализацию пакета
6. Шифрование и расшифрование сообщений
7. Защиту от воспроизведения
8. Обмен сообщениями через внутрипроцессный транспорт

## Ключевые компоненты

### 1. Сериализация пакетов (`PacketSerializer`)

Проводной формат (прямой порядок байт):
- Версия протокола (1 байт)
- Тип пакета (1 байт)
- Идентификатор пакета / UUID (16 байт)
- Приоритет (1 байт)
- TTL (4 байта, int32)
- TimestampMs (8 байт, int64)
- SourceUhid (2-байтовый префикс длины + байты UTF-8)
- DestinationUhid (2-байтовый префикс длины + байты UTF-8)
- PacketNonce (2-байтовый префикс длины + байты)
- Payload (4-байтовый префикс длины + байты)
- Signature (2-байтовый префикс длины + байты)

Полностью совместимо с C# `PacketSerializer`.

### 2. Подпись Ed25519 (`Ed25519Service`, `PacketSigning`)

- **Генерация ключей**: 32-байтовый seed приватного ключа, 32-байтовый публичный ключ
- **Подпись**: 64-байтовые подписи над детерминированными подписываемыми данными
- **Верификация**: Заменяет P-256 ECDSA в период миграции
- **Формат подписываемых данных**: Точно совпадает со спецификацией C# (nonce пакета, временная метка, тип, UHID, хеш полезной нагрузки, TTL, приоритет)
- **Защита от воспроизведения**: Дедупликация nonce с TTL 5 минут

### 3. Signal Protocol (`SignalProtocol`)

Реализует согласование ключей X3DH с симметричным трещоточным механизмом:

**Установка сессии:**
- Получает пакет предварительных ключей пира
- Верифицирует подпись пакета с помощью Ed25519
- Выполняет X3DH: DH(local identity, remote signed pre-key) + DH(local identity, remote pre-key)
- Выводит корневой ключ и ключи цепочки с помощью HKDF-SHA256

**Шифрование/расшифрование:**
- Симметричный трещоточный механизм с HMAC-SHA256
- AES-256-GCM с 12-байтовым случайным nonce
- Ключи для каждого сообщения с прямой секретностью
- Обработка внепорядковых сообщений (кеш пропущенных ключей, максимум 1000 ключей)

**Параметры:**
- Информация для деривации корневого ключа: `"aether-root-v1"`
- Информация для деривации цепочки отправки: `"aether-chain-send-v1"`
- Информация для деривации цепочки приёма: `"aether-chain-recv-v1"`
- Соль ключа сообщения: `0x01`, соль ключа цепочки: `0x02`

### 4. Абстракция транспорта (`TransportService`)

Интерфейс для физических транспортов (BLE, Wi-Fi Direct и др.):

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport:** Эталонная реализация, использующая глобальный `ConcurrentHashMap` для тестирования/демонстрации.

### 5. Доменные модели (`Models.kt`)

- **AetherNode**: Идентификация узла с UHID, публичным ключом, возможностями, геохешем
- **PeerInfo**: Известный пир с оценкой надёжности и временной меткой последнего появления
- **RouteEntry**: Запись таблицы маршрутизации с количеством переходов и оценкой качества
- **NodeCapabilities**: Битовое поле (BLE, Wi-Fi Direct, Gateway, Relay, SOS, Streaming, Voice, DTN)
- **DtnBundle**: Пакет отложенной доставки с истечением срока действия и подсчётом копий

## Константы протокола

Ключевые константы (из `Constants.kt`):

| Категория | Константа | Значение |
|----------|----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Типы пакетов

Все 23 типа пакетов совпадают со значениями перечисления C# (1–23):

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## Зависимости

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — Поддержка формата ключей
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await, Flow
- **org.slf4j:slf4j-api:2.0.9** — Логирование
- **kotlin-stdlib** — Стандартная библиотека Kotlin

## Примеры использования

### Генерация ключей

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Подпись пакета

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### Сериализация пакета

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Шифрование через Signal Protocol

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## Межъязыковая совместимость

Данная реализация поддерживает **точную совместимость проводного формата** с эталонной реализацией на C#:

- Бинарный формат пакета: идентичное расположение в прямом порядке байт
- Перечисление типов пакетов: значения точно совпадают с перечислением C# (1–23)
- Подписи Ed25519: совместимы с NSec/libsodium
- ECDH P-256: стандартная кривая, совместима между языками
- HKDF-SHA256: стандартная реализация RFC 5869
- AES-256-GCM: стандарт NIST с 12-байтовым nonce и 16-байтовым тегом

Пакеты, сериализованные в Kotlin, могут быть десериализованы в C# и наоборот.

## Тестирование

Реализация включает комплексную демонстрацию (`Demo.kt`), которая проверяет:

1. Генерацию ключей и экспорт публичного ключа
2. Генерацию и обмен пакетами предварительных ключей
3. Установку сессии через Signal Protocol
4. Создание, подпись и сериализацию пакета
5. Десериализацию пакета и верификацию подписи
6. Шифрование и расшифрование сообщений
7. Защиту от атак воспроизведения
8. Обмен сообщениями через внутрипроцессный транспорт

Запуск:
```bash
./gradlew run
```

## Соображения безопасности

- **Обнуление ключей**: Все промежуточные криптографические данные обнуляются после использования с помощью `CryptographicOperations.ZeroMemory` (эквивалент в Kotlin: `fill(0)`)
- **Защита от воспроизведения**: Дедупликация nonce с TTL 5 минут предотвращает атаки воспроизведения
- **Прямая секретность**: Ключи для каждого сообщения выводятся из цепочки трещоточного механизма
- **Обработка внепорядковых сообщений**: Кеш пропущенных ключей с максимум 1000 ключей для предотвращения исчерпания памяти
- **Аутентификация RREP**: Пакеты Route Reply подписываются узлом назначения
- **Конфиденциальность пакетов**: Содержимое сообщений зашифровано с помощью AES-256-GCM

## Планируемые расширения

Реализация предоставляет хуки для:

- **Транспорта BLE** (интерфейс `TransportService`)
- **Транспорта Wi-Fi Direct** (тот же интерфейс)
- **Эпидемической маршрутизации DTN** (модель `DtnBundle` готова)
- **SOS-трансляции** (тип пакета определён)
- **Маяков присутствия** (тип пакета определён)
- **Голоса и потоковой передачи** (типы пакетов определены)
- **Двойного трещоточного механизма** (при наличии всегда включённых транспортов)

## Документация протокола

Полная спецификация протокола: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## Лицензия

SPDX-License-Identifier: MIT
