# Aether Protocol — реализация на Rust

[English](../../../../rust/README.md) · [Français](../../fr/rust/README.md) · [Español](../../es/rust/README.md) · [العربية](../../ar/rust/README.md) · [中文简体](../../zh-CN/rust/README.md) · [日本語](../../ja/rust/README.md) · [Deutsch](../../de/rust/README.md) · [Português (BR)](../../pt-BR/rust/README.md) · [Русский](README.md) · [فارسی](../../fa/rust/README.md) · [한국어](../../ko/rust/README.md)

Полная реализация протокола mesh-сети Aether на Rust, обеспечивающая совместимость проводного формата с эталонной реализацией на C#.

## Обзор

Этот крейт предоставляет:

- **Сериализация/десериализация MeshPacket** — бинарный проводной формат, в точности совпадающий с C# PacketSerializer
- **Подпись Ed25519** — генерация идентификационных ключей, подпись и верификация
- **Signal Protocol** — соглашение о ключах на основе X3DH с симметричным трещоточным механизмом для прямой секретности
- **Сервис подписи пакетов** — дедупликация nonce и проверки актуальности
- **Внутрипроцессный транспорт** — симулированная mesh-сеть для тестирования и демонстраций

## Структура проекта

```
rust/
├── Cargo.toml                          # Crate manifest
├── src/
│   ├── lib.rs                          # Module declarations
│   ├── main.rs                         # Demo application
│   ├── constants.rs                    # Protocol constants
│   ├── models.rs                       # Core data structures
│   ├── protocol/
│   │   ├── mod.rs                      # MeshPacket, PacketType enum
│   │   └── serializer.rs               # Binary serialization (wire-compatible)
│   ├── security/
│   │   ├── mod.rs                      # Module declarations
│   │   ├── ed25519.rs                  # Ed25519 signing service
│   │   ├── signal_protocol.rs          # Signal Protocol implementation
│   │   └── packet_signing.rs           # Packet signing + nonce dedup
│   └── transport/
│       ├── mod.rs                      # TransportService trait
│       └── in_process.rs               # In-memory transport implementation
```

## Ключевые возможности

### 1. Совместимость проводного формата

`PacketSerializer` производит побайтово идентичный вывод с реализацией на C#:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (GUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (u16, LE)
[N bytes] SourceUhid (UTF-8)
[2 bytes] DestinationUhid length (u16, LE)
[N bytes] DestinationUhid (UTF-8)
[2 bytes] PacketNonce length (u16, LE)
[N bytes] PacketNonce
[4 bytes] Payload length (i32, LE)
[N bytes] Payload
[2 bytes] Signature length (u16, LE)
[N bytes] Signature
```

Все многобайтовые целые числа используют порядок байтов little-endian. Длины строк предваряются u16 (SourceUhid, DestinationUhid) или i32 (Payload, Signature), как указано в спецификации протокола.

### 2. Типы пакетов

Определены все 26 типов пакетов из спецификации протокола:

- RouteRequest (1), RouteReply (2), Data (3), Ack (4)
- SosBroadcast (5), SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8), ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11), StreamSegment (12), StreamSubscribe (13), StreamUnsubscribe (14)
- VoicePtt (15), VoiceCall (16), VoiceSignaling (17)
- DtnBundle (18), DtnCustodyAck (19), DtnDeliveryReceipt (20)
- PresenceBeacon (21), PresenceQuery (22), ProfileSync (23)
- TipPacket (24), PreKeyRequest (25), PreKeyResponse (26)

### 3. Подпись Ed25519

- 32-байтовые закрытые ключи (seed), 32-байтовые открытые ключи, 64-байтовые подписи
- Использует `ed25519-dalek` для криптографических операций
- Безопасное обнуление ключей после использования

### 4. Signal Protocol

Соглашение о ключах на основе X3DH с симметричным трещоточным механизмом:

- **Соглашение о ключах:** ECDH P-256 с использованием эфемерных и подписанных предварительных ключей
- **Деривация ключей:** HKDF-SHA256 с уникальными info-строками
  - `aether-root-v1` — корневой ключ
  - `aether-chain-send-v1` — ключ цепочки отправки
  - `aether-chain-recv-v1` — ключ цепочки приёма
- **Шифрование:** AES-256-GCM (12-байтовый nonce, 16-байтовый тег)
- **Трещоточный механизм:** продвижение симметричного цепочечного ключа с ключами сообщений на основе счётчика
- **Обработка не по порядку:** кэшируется до 1000 пропущенных ключей сообщений

### 5. Сервис подписи пакетов

- Генерация случайного 8-байтового nonce
- Метки времени с точностью до миллисекунд
- Проверка актуальности (окно 5 минут)
- Дедупликация nonce по отправителю (предотвращает повторные воспроизведения)
- Автоматическая очистка просроченных записей

### 6. Внутрипроцессный транспорт

Симулированная mesh-сеть для тестирования:

- Статический реестр узлов с использованием конкурентной HashMap
- Доставка сообщений по принципу «выстрелил и забыл»
- Проверки двунаправленного подключения между узлами
- Подходит для демонстраций и модульных тестов

## Использование

### Базовая генерация ключей и подпись

```rust
use aethermesh_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Сеанс Signal Protocol

```rust
use aethermesh_protocol::security::SignalProtocolService;

let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

// Боб публикует набор предварительных ключей
let bob_bundle = bob.generate_pre_key_bundle("bob-node")?;

// Алиса обрабатывает набор и устанавливает сеанс
alice.process_pre_key_bundle(&bob_bundle)?;

// Алиса шифрует сообщение
let plaintext = b"Hello!";
let encrypted = alice.encrypt("bob-node", plaintext)?;

// Боб расшифровывает
let alice_bundle = alice.generate_pre_key_bundle("alice-node")?;
bob.process_pre_key_bundle(&alice_bundle)?;
let decrypted = bob.decrypt("alice-node", &encrypted)?;

assert_eq!(decrypted, plaintext);
```

### Сериализация пакетов

```rust
use aethermesh_protocol::protocol::{MeshPacket, PacketType};
use aethermesh_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### Подпись пакетов

```rust
use aethermesh_protocol::security::PacketSigningService;
use aethermesh_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### Внутрипроцессный транспорт

```rust
use aethermesh_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Запуск демонстрации

```bash
cargo run --release
```

Демонстрация выполняет следующие шаги:

1. Генерирует идентификационные ключи для Алисы и Боба
2. Инициализирует сервисы Signal Protocol
3. Генерирует и обменивается наборами предварительных ключей
4. Устанавливает зашифрованные сеансы
5. Обменивается зашифрованными сообщениями
6. Создаёт и подписывает mesh-пакеты
7. Верифицирует подписи пакетов
8. Сериализует и десериализует пакеты
9. Демонстрирует внутрипроцессный транспорт

## Константы

Все константы протокола определены в `src/constants.rs` в соответствии со спецификацией C#:

- Маршрутизация: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Безопасность: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Транспорт: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- Голос/Потоки: различные конфигурации битрейта и буфера

## Зависимости

- `ed25519-dalek` — подпись Ed25519
- `x25519-dalek` — соглашение о ключах X25519
- `aes-gcm` — шифрование AES-256-GCM
- `hkdf` — деривация ключей HKDF
- `sha2` — хеширование SHA-256
- `hmac` — операции HMAC
- `rand` — генерация случайных чисел
- `uuid` — генерация и сериализация GUID
- `serde` + `serde_json` — сериализация
- `tokio` — асинхронная среда выполнения
- `async-trait` — асинхронные методы трейтов

## Тестирование

Запуск всех тестов:

```bash
cargo test
```

Тесты охватывают:

- Создание пакетов и управление TTL
- Преобразование типов пакетов
- Циклы сериализации/десериализации
- Генерацию ключей Ed25519 и верификацию подписей
- Установление сеанса Signal Protocol и шифрование
- Подпись пакетов и проверку актуальности
- Подключение через внутрипроцессный транспорт

## Соответствие протоколу

Данная реализация следует спецификации протокола Aether (версия 2.0) с поддержкой:

- ✅ Бинарного проводного формата (little-endian, с префиксами длин)
- ✅ Всех 26 типов пакетов
- ✅ Подписи Ed25519 с дедупликацией nonce
- ✅ Соглашения о ключах X3DH с HKDF-SHA256
- ✅ Шифрования AES-256-GCM с 12-байтовым nonce
- ✅ Симметричного трещоточного механизма с обработкой не по порядку
- ✅ Генерации и обработки наборов предварительных ключей
- ✅ Построения подписываемых данных пакета (хеш SHA-256 полезной нагрузки)
- ✅ Абстракции трейта транспорта

## Примечания

- Проводной формат повсеместно использует порядок байтов little-endian (соответствует C# BinaryPrimitives.WriteInt32LittleEndian)
- Префиксы длин строк используют u16 для UHID и i32 для payload/signature (соответствует C# WriteUInt16/WriteInt32)
- Все криптографические ключевые материалы обнуляются после использования через эквивалент `CryptographicOperations`
- Реализация Signal Protocol использует HKDF с байтами соли [0x01] и [0x02] для трещоточного продвижения цепочки (соответствует использованию C# HKDF)
- Дедупликация nonce использует VecDeque на отправителя с автоматической очисткой записей старше 5 минут
