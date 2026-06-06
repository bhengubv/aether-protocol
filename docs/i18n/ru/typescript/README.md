# Aether Mesh Protocol — реализация на TypeScript

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Полная реализация протокола mesh-сети Aether на TypeScript/Node.js, полностью совместимая с эталонной реализацией на C# на уровне проводного формата.

## Возможности

- **Сериализация MeshPacket**: бинарный проводной формат, в точности совпадающий с C# (целые числа little-endian, строки/массивы с префиксами длин)
- **Подпись Ed25519**: использует TweetNaCl для генерации и верификации подписей
- **Signal Protocol**: обмен ключами X3DH с деривацией ключей HKDF-SHA256 и шифрованием AES-256-GCM
- **Подпись пакетов**: полное построение подписываемых данных согласно спецификации протокола (Раздел 2.3)
- **Внутрипроцессный транспорт**: симулированная сеть для тестирования и демонстраций
- **Симметричный трещоточный механизм**: продвижение цепочечного ключа HMAC-SHA256 с поддержкой сообщений вне порядка
- **Константы протокола**: все 60+ констант из Раздела A PROTOCOL_SPEC

## Установка

```bash
npm install
```

## Использование

### Сборка

```bash
npm run build
```

### Запуск демонстрации

```bash
npm run dev
```

Демонстрация:
1. Создаёт 2 узла в симулированной внутрипроцессной сети
2. Генерирует пары ключей Ed25519
3. Устанавливает сеансы протокола Signal
4. Создаёт, подписывает и верифицирует пакет
5. Сериализует и десериализует пакеты
6. Шифрует и расшифровывает сообщения
7. Отправляет пакеты через транспортный уровень

### Примеры API

#### Создание пакетов и подпись

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Создание пакета
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Подпись
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Верификация
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Шифрование Signal Protocol

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Генерация набора предварительных ключей
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Обработка набора узла-партнёра для установления сеанса
await signal.processPreKeyBundle(peerBundle);

// Шифрование сообщения
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Расшифровка сообщения
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### Сериализация пакетов

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Сериализация в бинарный формат
const binary = PacketSerializer.serialize(packet);

// Десериализация из бинарного формата
const restored = PacketSerializer.deserialize(binary);
```

#### Внутрипроцессный транспорт

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Прослушивание входящих данных
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Отправка данных
await nodeA.sendAsync("uhid-b", payload);
```

## Соответствие протоколу

### Проводной формат

Все многобайтовые целые числа — **little-endian**:
- Идентификатор пакета: UUID 16 байт
- TTL, TimestampMs: int32/int64 LE
- Длины строк: uint16 LE (не uint32)
- Длина полезной нагрузки: int32 LE

### Подпись пакетов (Раздел 2.3)

Формат подписываемых данных:
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

### Signal Protocol (Раздел 4)

- **Обмен ключами**: X3DH с ECDH P-256
- **HKDF**: SHA256 с salt="AetherNetSignal"
- **Info-строки**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Шифрование**: AES-256-GCM с 12-байтовым nonce, 16-байтовым тегом
- **Трещоточный механизм цепочки**: HMAC-SHA256 с продвижением счётчика

## Типы пакетов

Определены все 23 типа пакетов:
- RouteRequest (1) — AODV Route Request
- RouteReply (2) — AODV Route Reply
- Data (3) — данные приложения
- Ack (4) — подтверждение доставки
- SosBroadcast (5) — экстренная рассылка
- ... и ещё 18 (см. спецификацию протокола)

## Функции безопасности

- **Подписи Ed25519**: все пакеты подписываются согласно протоколу v2
- **AES-256-GCM**: ключи на уровне сообщений с уникальными nonce
- **Защита от повторных воспроизведений**: 8-байтовый случайный nonce + проверка метки времени
- **Прямая секретность**: симметричный трещоточный механизм продвигает цепочечные ключи
- **Расшифровка вне порядка**: кэширование пропущенных ключей сообщений (до 1000)

## Структура проекта

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

## Тестирование

Демонстрация (`npm run dev`) охватывает все основные возможности:
- Создание пакетов и сериализацию (с обратным циклом)
- Генерацию ключей Ed25519 и верификацию подписей
- Установление сеанса Signal Protocol
- Шифрование и расшифровку сообщений
- Доставку через внутрипроцессный транспорт

Для модульных тестов расширьте с помощью Jest или аналогичного тестового фреймворка.

## Примечания о совместимости

- **Проводной формат C#**: 100% совместим с C# PacketSerializer
- **Подписанные пакеты**: версия протокола 2 с подписями Ed25519
- **Деривация HKDF**: использует @noble/hashes (реализация на чистом JavaScript)
- **ECDH**: встроенный криптомодуль Node.js (кривая P-256)

## Зависимости

- **tweetnacl**: подписи Ed25519 через TweetNaCl
- **@noble/hashes**: деривация ключей HKDF-SHA256
- **uuid**: генерация и разбор UUID
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## Лицензия

MIT — см. файл LICENSE

## Ссылки

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# Implementation](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
