# Aether Protocol — реализация на Swift

[English](../../../../swift/README.md) · [Français](../../fr/swift/README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Комплексная реализация протокола mesh-сети Aether на Swift, обеспечивающая сквозное шифрование, маршрутизацию и одноранговую связь для iOS и macOS.

## Обзор

Aether — это децентрализованный протокол mesh-сети, разработанный для сред с нестабильным или отсутствующим подключением к интернету. Данная реализация на Swift предоставляет:

- **Совместимую сериализацию** с эталонной реализацией на C# на уровне проводного формата
- **Подпись Ed25519** для аутентификации пакетов
- **Signal Protocol** (X3DH + симметричный трещоточный механизм) для сквозного шифрования
- **Абстракцию транспорта**, поддерживающую несколько физических уровней (BLE, Wi-Fi Direct, NearLink)
- **Потокобезопасные асинхронные API** с использованием Swift Concurrency

## Требования

- Swift 5.9+
- macOS 13.0+ или iOS 16.0+
- Xcode 15+

## Зависимости

- [swift-crypto](https://github.com/apple/swift-crypto) — криптографические примитивы (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## Архитектура

### Основные компоненты

#### Уровень протокола
- **MeshPacket**: основная структура пакета (UUID, тип, source/destination UHID, TTL, приоритет, полезная нагрузка, подпись)
- **PacketType**: перечисление 26 типов пакетов (RouteRequest, Data, SosBroadcast, DtnBundle и др.)
- **PacketSerializer**: бинарный сериализатор/десериализатор с проводным форматом little-endian

#### Уровень безопасности
- **Ed25519Service**: генерация ключей, подпись и верификация с использованием Curve25519
- **SignalProtocolService**: соглашение о ключах X3DH + симметричный трещоточный механизм для зашифрованных сеансов
- **PacketSigningService**: подпись на уровне пакетов с дедупликацией nonce и защитой от повторных воспроизведений

#### Уровень транспорта
- **TransportService**: протокол, определяющий контракт транспорта
- **InProcessTransport**: транспорт в памяти для тестирования и локальной коммуникации

#### Модели
- **AetherNetNode**: представление узла с UHID и идентификационным ключом
- **PreKeyBundle**: набор для асинхронного установления сеанса
- **EncryptedPayload**: обёртка зашифрованного сообщения
- **DtnBundle**: пакет для сетей с задержками (DTN)
- **PeerInfo**: информация об узлах в таблице маршрутизации

### Константы
Все константы протокола (TTL, тайм-ауты, ограничения ёмкости) определены в `ProtocolConstants`.

## Установка

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

В вашем Package.swift:

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherNetProtocol", package: "aether-protocol-swift")
    ]
)
```

## Быстрый старт

### 1. Сериализация пакетов

```swift
import AetherNetProtocol

// Создание пакета
var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice-node",
    destinationUhid: "bob-node",
    payload: "Hello, Aether!".data(using: .utf8)!
)

// Сериализация в байты
let serialized = PacketSerializer.serialize(packet)

// Десериализация
let deserialized = try PacketSerializer.deserialize(serialized)
```

### 2. Подпись Ed25519

```swift
// Генерация пары ключей
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Подпись данных
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Верификация подписи
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Сеанс Signal Protocol

```swift
let alice = SignalProtocolService()
let bob = SignalProtocolService()

// Обмен ключами: Боб публикует набор предварительных ключей
let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob-node")

// Алиса обрабатывает набор Боба и устанавливает сеанс
try await alice.processPreKeyBundle(bobBundle)

// Алиса шифрует сообщение
let encrypted = try await alice.encrypt(
    peerUhid: "bob-node",
    plaintext: "Secret message".data(using: .utf8)!
)

// Чтобы Боб мог расшифровать, ему также нужен набор Алисы
let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice-node")
try await bob.processPreKeyBundle(aliceBundle)

// Боб расшифровывает
let decrypted = try await bob.decrypt(peerUhid: "alice-node", payload: encrypted)
```

### 4. Подпись пакетов

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Подпись пакета
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Верификация полученного пакета
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. Внутрипроцессный транспорт (тестирование)

```swift
let alice = InProcessTransport(uhid: "alice")
let bob = InProcessTransport(uhid: "bob")

// Настройка обратного вызова для получения данных
await bob.onDataReceived { senderUhid, data in
    print("Received \(data.count) bytes from \(senderUhid)")
}

// Отправка сообщения
let success = await alice.sendAsync(
    peerUhid: "bob",
    data: "Hello".data(using: .utf8)!,
    cancellationToken: nil
)
```

## Проводной формат

Все пакеты соответствуют проводному формату little-endian:

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

Минимальный размер пакета с пустыми UHID и полезной нагрузкой: **43 байта**.

## Модель безопасности

### Шифрование
- **Алгоритм**: AES-256-GCM
- **Деривация ключей**: HKDF-SHA256 из общего секрета X3DH
- **Трещоточный механизм сеанса**: симметричный трещоточный механизм продвигает цепочечный ключ с каждым сообщением

### Подпись
- **Алгоритм**: Ed25519 (Curve25519)
- **Защита полезной нагрузки**: хеш SHA256 включён в подписываемые данные
- **Защита от повторных воспроизведений**: 8-байтовый nonce + метка времени в миллисекундах + кэш дедупликации

### Обмен ключами
- **Протокол**: вариант X3DH с ECDH P-256
- **Привязка предварительных ключей**: подписанный предварительный ключ верифицируется с помощью Ed25519
- **Асинхронность**: сеансы устанавливаются без необходимости присутствия получателя в сети

### Ограничения
- **MaxSkippedKeys**: 1000 (сообщения вне порядка на уровне сеанса)
- **MaxPacketAge**: 300 секунд (5 минут)

## Константы протокола

- **DefaultTtl**: 7
- **SosTtl**: 15
- **RouteTimeoutMs**: 5000
- **RouteExpirySeconds**: 300
- **DtnBundleTtlHours**: 72
- **DtnMaxCopies**: 3
- **AesGcmNonceSize**: 12 байт
- **AesGcmTagSize**: 16 байт

Полный список см. в `ProtocolConstants`.

## Потокобезопасность

Все сервисы изолированы через `actor` для потокобезопасного конкурентного доступа:

- `SignalProtocolService` — управление сеансами и шифрование
- `PacketSigningService` — подпись и верификация пакетов
- `InProcessTransport` — доставка сообщений

Использование с Swift Concurrency:

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## Тестирование

Запустите включённую демонстрацию:

```bash
cd swift
swift run aether-demo
```

Ожидаемый вывод:

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

## Совместимость

Проводной формат совместим с:
- **AetherNet.Core** (C#) — эталонная реализация
- **aether-protocol-go** — реализация на Go
- **aether-protocol-rust** — реализация на Rust

Все реализации используют:
- Целые числа в порядке байтов little-endian
- Кодировку строк UTF-8
- Подписи Ed25519 (64 байта)
- Шифрование AES-256-GCM (12-байтовый nonce, 16-байтовый тег)

## Производительность

Результаты бенчмарков на Apple Silicon (M1 Pro):

| Операция | Время |
|-----------|------|
| Сериализация пакета | ~0.5 мкс |
| Десериализация пакета | ~0.7 мкс |
| Подпись Ed25519 | ~3.5 мс |
| Верификация Ed25519 | ~4.2 мс |
| Шифрование AES-256-GCM | ~0.8 мкс |
| Расшифровка AES-256-GCM | ~0.9 мкс |
| Соглашение о ключах X3DH | ~8.5 мс |
| Симметричный трещоточный механизм | ~0.3 мкс |

## Планы развития

- **Транспорт BLE**: реализация Bluetooth Low Energy
- **Транспорт Wi-Fi Direct**: прямой одноранговый Wi-Fi
- **Double Ratchet**: полная прямая секретность с трещоточным механизмом сообщений
- **Маршрутизация AODV**: обнаружение и поддержание маршрутов
- **Сервис DTN**: доставка пакетов с накоплением и пересылкой
- **Присутствие и близость**: обнаружение узлов с учётом местоположения
- **Голос и потоки**: протоколы медиа реального времени

## Лицензия

MIT — см. файл LICENSE

## Ссылки

1. [Спецификация протокола Aether](../docs/PROTOCOL_SPEC.md)
2. [Extended Triple Diffie-Hellman (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Double Ratchet Algorithm](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869: HKDF](https://tools.ietf.org/html/rfc5869)
5. [Ed25519 Signatures](https://en.wikipedia.org/wiki/Curve25519)
6. [AES-GCM Mode](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## Участие в разработке

Это эталонная реализация. Для сообщений об ошибках и запросов новых возможностей, пожалуйста, откройте issue на GitHub.
