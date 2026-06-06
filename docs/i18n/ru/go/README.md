# Aether Protocol - реализация на Go

[English](../../../../go/README.md) · [Français](../../fr/go/README.md) · [Español](../../es/go/README.md) · [العربية](../../ar/go/README.md) · [中文简体](../../zh-CN/go/README.md) · [日本語](../../ja/go/README.md) · [Deutsch](../../de/go/README.md) · [Português (BR)](../../pt-BR/go/README.md) · [Русский](README.md) · [فارسی](../../fa/go/README.md) · [한국어](../../ko/go/README.md)

Полная реализация протокола Aether для ячеистых сетей на языке Go, совместимая по проводному формату с эталонной реализацией на C#.

## Обзор

Данный модуль реализует децентрализованный протокол ячеистых сетей Aether для сред с нестабильным или отсутствующим подключением к интернету. Он предоставляет:

- **Сериализацию пакетов**: Бинарный проводной формат, совместимый с эталонной реализацией на C# (кодирование с прямым порядком байт)
- **Подпись Ed25519**: Криптографическая аутентификация пакетов
- **Signal Protocol**: Согласование ключей X3DH + симметричный трещоточный механизм для сквозного шифрования
- **Сервис подписи пакетов**: Дедупликация nonce с TTL 5 минут для защиты от воспроизведения
- **Внутрипроцессный транспорт**: Транспорт на основе памяти для тестирования и межпроцессного взаимодействия
- **Модели**: Структуры AetherMeshNode, PeerInfo, RouteEntry, DtnBundle, SosAlert
- **Константы протокола**: Все константы маршрутизации, обнаружения, безопасности и транспорта

## Структура модуля

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

## Ключевые возможности

### 1. Сериализация пакетов (прямой порядок байт)

Проводной формат точно совпадает с C#, используя кодирование с прямым порядком байт для всех многобайтовых целых чисел:

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

**Пример:**
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

### 2. Подпись и верификация Ed25519

- **Формат ключа**: 32-байтовый seed (приватный), 32-байтовый публичный ключ, 64-байтовая подпись
- **Стандартная библиотека**: Использует `crypto/ed25519` (без внешних зависимостей)

**Пример:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. Signal Protocol (X3DH + симметричный трещоточный механизм)

Реализует Signal Protocol для сквозного шифрования:

- **Согласование ключей**: ECDH P-256 через `crypto/ecdh`
- **Деривация ключей**: HKDF-SHA256 через `golang.org/x/crypto/hkdf`
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **Шифрование**: AES-256-GCM с 12-байтовым nonce и 16-байтовым тегом
- **Трещоточный механизм**: Продвижение цепочки HMAC-SHA256
- **Внепорядковая доставка**: Кеш пропущенных ключей сообщений (максимум 1000)

**Пример:**
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

### 4. Подпись пакетов и дедупликация nonce

Защита от атак воспроизведения с TTL 5 минут для кеша nonce:

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

### 5. Внутрипроцессный транспорт

Транспорт на основе памяти для тестирования и локального межузлового взаимодействия:

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

### 6. Доменные модели

Полные структуры для ячеистых сетей:

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

## Константы протокола

Все константы из спецификации протокола (Приложение A):

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

## Запуск демонстрации

Демонстрационная программа иллюстрирует все основные возможности:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**Вывод демонстрации:**
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

## Совместимость проводного формата

Вся сериализация использует **кодирование с прямым порядком байт** для соответствия эталонной реализации на C#:

- **Целые числа**: `encoding/binary.LittleEndian`
- **UUID**: Стандартный 16-байтовый формат UUID
- **Строки**: Кодировка UTF-8 с 2-байтовым (uint16) или 4-байтовым (uint32) префиксом длины
- **Байты**: С префиксом длины (2 или 4 байта) с последующими необработанными данными

Это обеспечивает побайтовую совместимость при обмене пакетами между реализациями на Go и C#.

## Зависимости

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

Все криптографические примитивы используют стандартную библиотеку Go (`crypto/*`) плюс `golang.org/x/crypto` для HKDF и ECDH P-256.

## Функции безопасности

1. **Обнуление ключей**: Все промежуточные ключи безопасно обнуляются с помощью `ZeroMemory()`
2. **Отсутствие резервного шифрования**: Сообщения требуют установленных сессий; резервный вариант на основе UHID не используется
3. **Защита от воспроизведения**: 8-байтовый nonce + временная метка + кеш дедупликации на 5 минут
4. **Пробелы в счётчике**: Внепорядковые сообщения поддерживаются до MaxSkippedKeys (1000)
5. **Верификация подписи**: Все ответы на маршруты и пакеты с предварительными ключами верифицируются через Ed25519

## Примечания по производительности

- **Сериализация пакетов**: ~1–2 мкс на пакет (протестировано с полезной нагрузкой 100 байт)
- **Подпись Ed25519**: ~50 мкс на подпись
- **Шифрование Signal Protocol**: ~100 мкс на сообщение
- **Очистка дедупликации nonce**: Фоновая горутина запускается каждые 60 секунд

## Тестирование

Демонстрационная программа показывает:
- ✓ Цикличную сериализацию пакетов
- ✓ Верификацию подписи Ed25519
- ✓ Установку сессии Signal Protocol
- ✓ Сквозное шифрование/расшифрование
- ✓ Взаимодействие через внутрипроцессный транспорт
- ✓ Дедупликацию nonce

Все операции являются горутинобезопасными благодаря использованию `sync.RWMutex` и `sync.Map` там, где это необходимо.

## Примечания по реализации

1. **Формат UUID**: Использует `github.com/google/uuid` для соответствия RFC 4122
2. **Управление ключами**: Внешнее хранилище ключей не используется; ключи хранятся в памяти для демонстрации. В продакшн-среде следует использовать защищённое хранилище.
3. **Интерфейс транспорта**: Расширяем для BLE, Wi-Fi Direct и других физических уровней
4. **Сессии Signal**: Хранятся для каждого пира, без поддержки базы данных в данной реализации
5. **Обработка ошибок**: Все криптографические операции возвращают ошибки; вызывающий обязан их обрабатывать

## Планируемые улучшения

- [ ] Хранение маршрутов и сессий в SQLite
- [ ] Реализация транспорта BLE
- [ ] Реализация транспорта Wi-Fi Direct
- [ ] Реализация протокола маршрутизации AODV
- [ ] Эпидемическая маршрутизация DTN
- [ ] Сервис маяков присутствия и обнаружения
- [ ] Поддержка голоса и потоковой передачи
- [ ] Алгоритм двойного трещоточного механизма для повышенной гарантии прямой секретности

## Лицензия

SPDX-License-Identifier: MIT
