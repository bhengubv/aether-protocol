```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

Обменивайтесь файлами, сообщениями и потоками с людьми рядом. Без Wi-Fi. Без мобильных данных. Без регистрации. Как AirDrop, только работает для всех, на любой платформе.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## Что можно делать?

**Делитесь конспектами лекций, не тратя трафик.**

Вы в учебной группе. У кого-то на телефоне есть старые экзаменационные материалы. Aether передаёт их прямо на ваше устройство по Bluetooth — без точки доступа, без группы в WhatsApp, без ограничений на размер файла. Если кто-то в группе вне зоны доступности, файл передаётся через другие устройства, пока не достигнет адресата. Сообщения ждут маршрута до 72 часов.

```
  [Вы] ──BLE──▶ [Друг] ──WiFi──▶ [Друг друга]
    notes.pdf           ретранслируется, зашифровано
```

**Узнайте, что происходит вокруг вас.**

Вы на кампусном мероприятии или фестивале. Aether обнаруживает соседние устройства по Bluetooth и Wi-Fi Direct — без ленты приложения, без алгоритмов. Вы видите то, что реально происходит рядом, а не то, что продвигается.

**Отправьте SOS при отсутствии сигнала.**

Ваш телефон без связи. Aether транслирует экстренное сообщение на все устройства в зоне доступности, а те передают его дальше. Сотовая вышка не нужна.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Создавайте приватные групповые каналы.**

Канал для вашего общежития, клуба, проектной команды. Только верифицированные участники могут читать сообщения и отправлять их. Ни один сервер не хранит переписку.

**Продавайте вещи людям рядом.**

Выставьте учебник на продажу. Люди, проходящие в зоне доступности сети, увидят объявление. Никакого аккаунта на маркетплейсе, никаких комиссий — только близость.

**Смотрите фильм вместе через сеть.**

У вашей группы кинопросмотр. У кого-то есть файл. Aether синхронизирует воспроизведение на всех устройствах — воспроизведение, пауза, перемотка — всё в унисон. Если только у некоторых есть файл, сеть распределяет его в реальном времени через P2P-поток. Все вносят вклад через SDPKT для покупки, если ни у кого нет файла.

## Как это работает

Устройства общаются напрямую друг с другом через Bluetooth, Wi-Fi Direct или NearLink. Без интернет-соединения, без сервера, без центральной инфраструктуры.

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ End-to-End Encrypted (Signal Protocol) ======>|
       |                                                             |
       |  No internet. No servers. No ISP. Just devices talking.     |
```

Когда сообщение не может достичь получателя напрямую, оно передаётся через другие устройства. Эти ретрансляционные устройства не могут прочитать то, что несут — каждое сообщение зашифровано алгоритмом AES-256-GCM. Каждый пакет подписан ключами идентификации Ed25519, а поддельные пакеты отклоняются сетью.

> **Замечание о зрелости системы безопасности (прочтите перед внедрением):** Настоящий X3DH (4 обмена X25519 DH), полный Signal Double Ratchet (шаг DH-ротации при получении, KDF_RK, цепочечный храповик 0x01/0x02) и пул одноразовых ключей (по умолчанию 100 OPK, FIFO, защита блокировками) реализованы на **всех 8 языках** и закреплены в совместном кросс-языковом корпусе фикстур в `fixtures/signal/`. Единственным открытым вопросом остаётся физическое включение RF на реальном BLE-оборудовании (отслеживается в `OPEN_ISSUES.md`).

Без аккаунтов, без телефонных номеров, без электронной почты. Сгенерируйте пару ключей — и вы в сети.

```
  ┌─────────────────────────────────┐
  │         Your Application        │
  ├─────────────────────────────────┤
  │ Messaging · Streaming · Voice   │
  │ Video · Watch Together          │
  ├─────────────────────────────────┤
  │  Security: AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
  ├─────────────────────────────────┤
  │  Routing: AODV + DTN            │
  ├─────────────────────────────────┤
  │  Transport: BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**Маршрутизация** — AODV с подписанными ответами на маршрутные запросы. Каждый ответ на маршрутный запрос подписан ключом Ed25519 адресата, поэтому ни одно устройство не может притвориться адресатом.

**Хранение и пересылка** — когда активного маршрута нет, пакеты удерживаются до 72 часов, пока не откроется путь.

**Выбор транспорта** — протокол выбирает подходящий транспорт для каждого пакета. Небольшие управляющие сообщения передаются по BLE. Массовые пересылки используют Wi-Fi Direct. NearLink при наличии.

**Голос, видео и потоковая передача** — видеозвонки с согласованием кодека (H.264/H.265/VP8), выбор качества с учётом транспорта, групповое видео с автоматической SFU-ретрансляцией, синхронный совместный просмотр с компенсацией RTT и адаптивная потоковая передача с изменяемым битрейтом.

**Защита от повторных атак** — дедупликация nonce с окном актуальности метки времени в 5 минут.

## Транспорты

Каждый транспорт имеет цветовое имя, используемое во всей кодовой базе. `IsAvailable` блокирует аппаратно-недоступные пути — `TransportManager` автоматически пропускает их и переключается на следующий доступный транспорт.

| Цвет | Название | Дальность | Пропускная способность | Статус |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 м | 1 Мбит/с | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 м | 250 Мбит/с | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | Сотовая HTTP-ретрансляция | Неограничено | ~10 Мбит/с | ✅ Windows — сервер ретрансляции в `samples/AetherNet.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 см | 848 кбит/с | ⚠️ Android HCE (`android/white/`); Windows: NDEF-over-BLE-GATT + приближение ACR122U PC/SC (функция `Windows.Networking.Proximity` удалена в Win 11) |
| 🩵 Aether Teal | NearLink | ~600 м | 12 Мбит/с | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`; Windows + Android: приближение SSAP-over-BLE (API-аналогично, не совместимо на уровне проводного протокола) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 км | 37,5 кбит/с | ⚠️ Формат Meshtastic по BLE LR (~1,3 км); замена радио на SX1276/SX1278 при наличии модуля LoRa |

Порядок приоритетов в `TransportManager`: NearLink → BLE (≤ 1 КБ) → Wi-Fi Direct → NFC → LoRa → HTTP Relay (последний вариант, `PowerCostRelative = 100`).

## Уровни развёртывания

Aether работает на любой платформе, поддерживающей Bluetooth или Wi-Fi. Доступный уровень зависит от целевой операционной системы.

---

### Стандартный уровень — любая платформа

Android · Windows · Linux · macOS · iOS

Aether полностью функционирует на любом устройстве с Bluetooth или Wi-Fi. Там, где физически отсутствует соответствующий радиомодуль, каждый заблокированный транспорт аппроксимируется с помощью доступных средств:

- **NearLink (Aether Teal)** — аппроксимируется через BLE GATT с использованием канонического UUID сервиса Aether SLE (`61657468-6572-0003-0000-000000000000`). Прикладной протокольный уровень SSAP идентичен GATT по API. Радиоуровень (BPSK/QPSK/8PSK, коды Polar, каналы 1–4 МГц) — нет: узлы стандартного уровня не могут обмениваться сырыми байтами с реальным оборудованием NearLink; они взаимодействуют с другими узлами стандартного уровня Aether.
- **LoRa (Aether Red)** — аппроксимируется с использованием полного формата Meshtastic по BLE 5.0 Coded PHY (S=8, ~1,3 км на открытом воздухе). Федерация мостовых узлов с реальным оборудованием LoRa работает автоматически — тот же формат пакета Meshtastic используется на всех хопах без трансляции.
- **NFC (Aether White)** — аппроксимируется через NDEF-over-BLE-GATT с порогом близости RSSI (≥ −40 дБм ≈ 5–10 см), воспроизводящим семантику подключения касанием. Путь PC/SC через USB NFC-ридер также поддерживается на Windows.

Все остальные возможности — BLE, Wi-Fi Direct, HTTP-ретрансляция, безопасность Signal Protocol (X3DH + Double Ratchet), маршрутизация AODV, DTN-хранение и пересылка, SOS-трансляция, голос, потоковая передача — являются нативными и идентичны нативному уровню.

**Это полноценное, готовое к производственному использованию развёртывание.** Большинство приложений начинают именно здесь.

---

### Нативный уровень — CircleOS / OpenHarmony

CircleOS · HarmonyOS · любая ОС на базе OpenHarmony

CircleOS построен на OpenHarmony, который поставляется с кремнием NearLink (SLE) и SDK `@kit.NearLinkKit` в качестве первоклассной возможности ОС. На устройствах CircleOS и HarmonyOS с аппаратным обеспечением NearLink аппроксимация не нужна — `harmonyos/teal/` использует реальное SLE-радио напрямую:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Это не просто улучшенная версия стандартного уровня. На уровне NearLink это принципиально иная сеть:

| Возможность | Стандартный уровень (приближение BLE) | Нативный уровень (CircleOS / OpenHarmony) |
|---|---|---|
| **Дальность NearLink** | ~100 м (BLE) | **600 м** |
| **Пропускная способность NearLink** | ~1 Мбит/с (BLE) | **12 Мбит/с** |
| **Задержка NearLink** | ~10 мс (BLE) | **20 мкс** |
| **Потребление NearLink** | Базовое BLE | **На 60% меньше, чем BLE 5.0** |
| **Одновременные NearLink-пиры** | ~7 (лимит подключений BLE) | **500+** |
| **Источник NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Настоящее SLE-радио (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP-ретрансляция** | Нативное | Нативное (идентично) |
| **Безопасность Signal Protocol** | Полная | Полная (идентично) |
| **Маршрутизация / DTN / SOS** | Полная | Полная (идентично) |
| **Идентификатор Aether Tag** | Поддерживается | Поддерживается (идентично) |

---

### Переход между уровнями

Никаких изменений в коде не требуется. Уровень определяется во время выполнения по `IsAvailable` каждого транспортного сервиса:

1. На устройстве CircleOS или HarmonyOS с кремнием NearLink `IsAvailable` транспорта NearLink возвращает `true` (аппаратный зондаж через проверку прав + попытку пассивного сканирования).
2. `TransportManager` автоматически повышает NearLink до приоритетной позиции — наименьшие энергозатраты, наибольшая пропускная способность.
3. Код приложения, формат пакета, алгоритм маршрутизации, уровень безопасности и Aether Tags идентичны на обоих уровнях.

Узел стандартного уровня и узел нативного уровня могут свободно общаться — они разделяют один формат проводного протокола, одни сеансы Signal Protocol и одни Aether Tags. Разница в уровнях влияет только на радио, используемое для пакетов NearLink, но не на протокол выше.

---

> **Внутри эти уровни называются вариантом Asterix (стандартный) и вариантом Obelix (нативный).** Asterix хорошо работает с тем, что есть. Obelix — работающий на CircleOS с нативным NearLink — функционирует на постоянно повышенных возможностях, как Обеликс несёт силу волшебного зелья, не нуждаясь в том, чтобы пить его снова.

---

## Реализации

Aether реализован на 8 языках, чтобы работать на телефонах, ноутбуках, планшетах и микроконтроллерах. Все реализации производят совместимые пакеты — сообщение, зашифрованное узлом Rust, может быть ретранслировано узлом Python и расшифровано узлом Swift.

| Язык | Каталог | Формат протокола | Маршрутизация/DTN/SOS | X3DH | Double Ratchet | Пул OPK | Голос/Группа | Потоковая/Видео/Совместный просмотр |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Все 8 языков производят побайтово идентичные пакеты проводного протокола, верифицированные 14 каноническими фикстурами формата и 4 тестовыми векторами Signal, запускаемыми в CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Маршрутизация (AODV-стиль RREQ/RREP), DTN-хранение и пересылка, SOS-трансляция, голос, потоковая передача и сервисы усиления безопасности реализованы на каждом языке с **~3000 тестами** по всем 8 реализациям:

| Язык | Тесты | CI-платформа |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Итого** | **~3000** | |

Кросс-языковая совместимость Signal привязана к `fixtures/signal/` с общими тестовыми векторами для X3DH (`x3dh_basic`), симметричного храповика (`ratchet_step_basic`, `ratchet_step_three_iterations`) и KDF_RK (`kdf_rk_basic`). Каждая реализация должна производить побайтово идентичные результаты с этими фикстурами. Все 8 языков теперь поставляются с полным сеансом Signal (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

## Быстрый старт

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Демонстрация проходит 8 шагов: генерация ключей идентификации Ed25519 для трёх узлов (Alice, Bob, Charlie), установка сеансов Signal Protocol, отправка зашифрованных сообщений, ретрансляция сообщения через Charlie (который не может его прочитать), отображение двоичного формата проводного протокола и демонстрация прямой секретности на 5 последовательных сообщениях. Вывод с цветовым кодированием и паузами между шагами.

**Отправка сообщения на C#:**

```csharp
// Establish a Signal Protocol session
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Encrypt and send
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Create a signed packet
var packet = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = "alice",
    DestinationUhid = "bob",
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7
};
var wireBytes = PacketSerializer.Serialize(packet);
await transport.SendAsync("bob", wireBytes);
```

### Rust (1.70+)

```bash
cd rust && cargo run
```

Демонстрация генерирует ключи идентификации для двух узлов, обменивается пакетами предварительных ключей, устанавливает зашифрованные сеансы, отправляет зашифрованные сообщения в обоих направлениях, создаёт и подписывает mesh-пакеты, проверяет подписи и сериализует пакеты в двоичный формат проводного протокола. Также демонстрирует транспортный уровень внутри процесса.

**Отправка сообщения на Rust:**

```rust
let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

let alice_bundle = alice.generate_pre_key_bundle("alice")?;
bob.process_pre_key_bundle(&alice_bundle)?;

let bob_bundle = bob.generate_pre_key_bundle("bob")?;
alice.process_pre_key_bundle(&bob_bundle)?;

let encrypted = alice.encrypt("bob", b"Hello Bob!")?;
let decrypted = bob.decrypt("alice", &encrypted)?;
```

### TypeScript (Node 18+, tsx)

```bash
cd typescript && npm install && npm run dev
```

Демонстрация создаёт два узла в симулированной сети, генерирует ключи Ed25519, устанавливает сеансы Signal Protocol, создаёт и подписывает пакет, сериализует его в бинарный формат, совместимый с C#, шифрует секретное сообщение, расшифровывает его на другом узле, отправляет через транспорт и проверяет круговой обход.

**Отправка сообщения на TypeScript:**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Exchange bundle with peer
await signal.processPreKeyBundle(peerBundle);

const plaintext = new TextEncoder().encode("Hello!");
const encrypted = await signal.encrypt("peer-node", plaintext);

const packet = MeshPacket.create(PacketType.Data, "my-node");
packet.destinationUhid = "peer-node";
packet.payload = encrypted;

const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

const serialized = PacketSerializer.serialize(packet);
await transport.sendAsync("peer-node", serialized);
```

### Python (3.10+)

```bash
cd python && pip install -e . && python3 demo.py
```

Демонстрация запускает 8 примеров: генерация ключей Ed25519 и обнаружение подделки, создание узлов с возможностями, обмен ключами X3DH Signal Protocol, шифрование и расшифровка AES-256-GCM, сериализация пакетов, подпись пакетов с обнаружением повторов, транспорт внутри процесса и полный сквозной поток, объединяющий все уровни.

**Отправка сообщения на Python:**

```python
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

bob_bundle = await bob_signal.generate_pre_key_bundle("bob")
await alice_signal.process_pre_key_bundle(bob_bundle)

encrypted = await alice_signal.encrypt("bob", b"Hello Bob!")

packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="alice",
    destination_uhid="bob",
    payload=encrypted.ciphertext,
    ttl=7
)
signing_service.sign_packet(packet, alice_private_key)

serialized = PacketSerializer.serialize(packet)
await transport.send_async("bob", serialized)
```

### Go (1.22+)

```bash
cd go && go run ./cmd/demo/main.go
```

Демонстрация запускает 5 примеров: круговые обходы сериализации пакетов, подпись Ed25519 с обнаружением подделки, установка сеанса Signal Protocol с зашифрованным обменом сообщениями в обоих направлениях, транспорт внутри процесса между двумя пирами и дедупликация nonce для защиты от повторных атак.

**Отправка сообщения на Go:**

```go
alice, _ := security.NewSignalProtocolService()
bob, _ := security.NewSignalProtocolService()

aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
bob.ProcessPreKeyBundle(aliceBundle)

bobBundle, _ := bob.GeneratePreKeyBundle("bob")
alice.ProcessPreKeyBundle(bobBundle)

encrypted, _ := alice.Encrypt("bob", []byte("Hello Bob!"))
decrypted, _ := bob.Decrypt("alice", encrypted)
```

### Kotlin (JDK 17+, Gradle 8+)

```bash
cd kotlin && ./gradlew run
```

Демонстрация проходит 11 шагов: генерация ключей, создание узлов с возможностями, инициализация Signal Protocol, обмен пакетами предварительных ключей, установка сеанса, создание и подпись пакета, сериализация, десериализация с проверкой подписи, сквозное шифрование с обновлением ключей, обнаружение атаки повтора и транспорт внутри процесса.

**Отправка сообщения на Kotlin:**

```kotlin
val aliceSignal = SignalProtocol()
val bobSignal = SignalProtocol()

val bobBundle = bobSignal.generatePreKeyBundle("bob")
aliceSignal.processPreKeyBundle(bobBundle)

val aliceBundle = aliceSignal.generatePreKeyBundle("alice")
bobSignal.processPreKeyBundle(aliceBundle)

val encrypted = aliceSignal.encrypt("bob", "Hello Bob!".toByteArray())
val decrypted = bobSignal.decrypt("alice", encrypted)
```

### Swift (5.9+, macOS 13+ / iOS 16+)

```bash
cd swift && swift run aether-demo
```

Демонстрация запускает 5 тестов: круговые обходы сериализации пакетов, подпись Ed25519 с отклонением подделок, установка сеанса Signal Protocol с шифрованием AES-256-GCM, доставка сообщений через транспорт внутри процесса и полный сквозной поток, где Alice подписывает пакет, а Bob проверяет его после транспортировки.

**Отправка сообщения на Swift:**

```swift
let aliceSignal = SignalProtocolService()
let bobSignal = SignalProtocolService()

let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: "bob")
try await aliceSignal.processPreKeyBundle(bobBundle)

var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice",
    destinationUhid: "bob",
    ttl: 7,
    payload: "Hello Bob!".data(using: .utf8)!
)

let signer = await PacketSigningService(
    privateKey: alicePrivateKey, publicKey: alicePublicKey)
try await signer.signPacket(&packet)

let serialized = PacketSerializer.serialize(packet)
await transport.sendAsync(peerUhid: "bob", data: serialized)
```

### C (CMake 3.16+, C11, libsodium)

```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

Демонстрация запускает 7 примеров: генерация ключей Ed25519, создание и подпись пакета, сериализация в двоичный формат проводного протокола, десериализация с проверкой целостности, шифрование и расшифровка AES-256-GCM, аутентификация сообщений HMAC-SHA256 и вывод ключей HKDF-SHA256.

**Отправка сообщения на C:**

```c
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethernet_packet_free(packet);
```

## Дорожная карта

Что сделано и что планируется.

**Готово (верифицировано кросс-языково, все 8 реализаций):**
- Формат проводного протокола: побайтово идентичен на 8 языках, закреплён 14 каноническими фикстурами и кросс-языковыми утверждениями в CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — матрица из 9 задач (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, плюс задача проверки целостности фикстур) в `.github/workflows/ci.yml`.
- Подпись и верификация пакетов Ed25519
- Шифрование AES-256-GCM
- Примитивы вывода ключей HKDF / HMAC
- Сериализация пакетов + разметка подписей (LE + поля int32 по 4 байта)
- Симулятор транспорта внутри процесса (для разработки и тестов)
- Сервис маршрутизации на основе AODV с RREQ/RREP, подписанными маршрутными ответами, дедупликацией и пересылкой TTL
- Сервис DTN-хранения и пересылки с передачей хранения, геохэш-осведомлённой репликацией, TTL 72 ч
- Сервис SOS-трансляции с флудом, дедупликацией, защитой от самоотправки, ограничением частоты (3/ч)
- Точки расширяемости: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (заглушки по умолчанию)
- **~3000 тестов** по всем 8 языкам (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — все зелёные в CI
- ✅ **Настоящий эфемерный ключ X3DH (8 языков)** — 4 обмена X25519 DH (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) с корневым выводом HKDF-SHA256. Закреплён фикстурой `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Выравнивание Double Ratchet по всему семейству** — полный §5 Signal с HMAC-SHA256 + разделением домена 0x01/0x02 в симметричном храповике, HKDF-SHA256 KDF_RK в шаге DH-храповика, DH-ротация при получении. Верифицировано фикстурами `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 согласован с HEAD** — см. `docs/PROTOCOL_SPEC.md`.

**Готово (все 8 языков):**
- ✅ **Голосовые звонки (1-на-1)** — конечный автомат сигнализации (Offer/Answer/Hangup/Cancel/Timeout) + двоичный транспорт фреймов (16Б callId · 4Б seq · 8Б timestamp · 1Б isSilence · N байт). Маршрутно-осведомлённая доставка через `IRoutingService`.
- ✅ **Групповой голос** — управляемое хостом членство (приглашение/исключение/выход), поле генерации ключа для каждого фрейма, однонаправленная рассылка всем текущим участникам, ротация ключа хостом при изменении состава.
- ✅ **Прямая трансляция** — издатель транслирует `StreamAnnounce`; подписчики отправляют `StreamSubscribe`; двоичные фреймы `StreamSegment` (16Б streamId · 4Б seq · 8Б ts · 1Б isKeyframe · N байт) однонаправленно каждому подписчику.
- ✅ **Видеозвонки (1-на-1)** — согласование кодека/разрешения/fps/битрейта в сигнализации, сигналы запроса ключевого кадра и изменения качества, двоичный формат `VideoFrame`, соответствующий голосовому формату.
- ✅ **Совместный просмотр** — хост передаёт авторитетные команды `WatchSync` (play/pause/seek/speed); последователи применяют с компенсацией RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` «выстрели и забудь».
- ✅ **Пул одноразовых предварительных ключей (OPK)** — по умолчанию 100, выдача FIFO, ленивое пополнение, защищённое блокировками потребление на всех 8 языках. Устраняет уязвимость конкурентного доступа при наличии одного OPK.
- ✅ **C: полный сеанс Signal** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` в `c/src/signal_protocol.c`; 6 сквозных двухузловых тестов в `c/tests/test_signal_session.c`. Все 8 языков теперь имеют полный Signal Protocol с поддержкой сеансов.

**Готово (только эталонная реализация C#):**
- ✅ **Шаг 9 демонстрации — MessagingService + сквозной DTN-запасной вариант** — `samples/AetherNet.Demo.Console` демонстрирует реальный обмен сообщениями с шифрованием Signal и DTN-хранением и пересылкой при офлайн-получателе.
- ✅ **Мост `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` обеспечивает сквозное шифрование уровня обмена сообщениями по умолчанию; сообщения без сеанса Signal ставятся в очередь, но никогда не отправляются незашифрованными.
- ✅ **Адаптивная потоковая передача с изменяемым битрейтом** — `AdaptiveBitrateController` с предписанными спецификацией лестницами битрейтов для профиля A (реальное время), B (прямая трансляция) и C (VOD). Издатель выбирает максимально устойчивую ступень (запас 20%) и передаёт `StreamAbandon` (`PacketType.StreamAbandon`) вместо сегмента при падении ниже минимума. `IStreamingService` предоставляет `UpdateBandwidthEstimate` и `GetCurrentBitrateRung`.
- ✅ **Совместный просмотр: прием BitTorrent + групповое финансирование ChipIn** — модели `TorrentInfo` / `TorrentFile`; `WatchTogetherService` обрабатывает `PacketType.TorrentMetadata` и инициирует `TorrentReceived`. Конечный автомат `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` на `IWatchTogetherService`.
- ✅ **Групповые видеозвонки с автоматической SFU-ретрансляцией** — `GroupVideoService` / `IGroupVideoService`. Топология FullMesh для ≤ 3 участников; автоматическое переключение на SFU при `SfuThresholdParticipants` (4) с переназначением ретранслятора через `GroupVideoSignaling(SfuAssigned)`. Рассылка в FullMesh, только ретрансляция в режиме SFU. Тип пакета сигнализации `GroupVideoSignaling = 35`.
- ✅ **Симуляция транспорта BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Фреймирование GATT MTU через `BleGattFramer` (1024 Б/фрейм, `[2Б count][2Б index][payload]`), реестр одноузловых статичных пиров, трансляция объявлений. Все ограничения `BleMaxPayloadBytes` соблюдены.
- ✅ **Симуляция транспорта Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Явный жизненный цикл `ConnectAsync`/`DisconnectAsync`, прямая доставка больших нагрузок (без фреймирования), двунаправленные события `PeerConnected`/`PeerDisconnected`.
- ✅ **Симуляция транспорта NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU фрейма 4096 Б, реестр 500 пиров, `ConnectedPeerCount`, `IsAvailable` настраивается во время выполнения.
- ✅ **Симуляционные тесты включения RF** — Двухузловые тесты интероперабельности (`SimulatedTransportTests`): круговой обход `MeshPacket` по BLE + NearLink, передача нагрузки 64 КБ по WiFi Direct. Программный уровень полностью верифицирован; для аппаратной валидации необходима лабораторная сессия с физическими устройствами.

**Готово (транспортный уровень C# — все fail-fast):**
- ✅ **Настоящий транспорт BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT-сервер). Полный тест включения RF в `samples/AetherNet.BleRfTest/`.
- ✅ **Настоящий транспорт Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket порт 8888) + `android/green/` (`WifiP2pManager`). Тест RF в `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **HTTP-транспорт ретрансляции (Aether Purple)** — `HttpRelayTransportService` с длинным опросом 10 секунд, `PowerCostRelative = 100`, всегда последний вариант. Сервер ретрансляции в `samples/AetherNet.RelayServer/` (минимальный API ASP.NET Core, порт 5200). Тест RF в `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` реализует `HostApduService` с AID `F061657468657200`. `WinNfcStubTransportService` документирует два пути приближения на Windows: (1) NDEF-over-BLE-GATT с порогом RSSI ≥ −40 дБм (симулирует подключение касанием без NFC-кремния, `IsAvailable = Bluetooth present`); (2) считыватель USB ACR122U через `Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). Путь обновления: реализовать `ITransportService` когда Microsoft выпустит первоклассный P2P NFC API.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — полная реализация ArkTS для HarmonyOS 5.0.1 (API 13) с использованием `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` зондируется во время выполнения. `WinNearLinkStubTransportService` + `android/teal/` документируют приближение SSAP-over-BLE: BLE GATT с UUID сервиса Aether SLE `61657468-6572-0003-0000-000000000000` — API-аналогично SSAP, не совместимо на уровне проводного протокола с реальным оборудованием NearLink. Путь обновления: заменить вызовы BLE GATT на вызовы SDK `ssapc_*`/`ssaps_*`; UUID и слот `TransportManager` не меняются.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` документируют приближение Meshtastic-over-BLE-LR: полный формат Meshtastic (16-байтовый заголовок + AES-256-CTR protobuf) по BLE 5.0 Coded PHY S=8 (~1,3 км на открытом воздухе), с управляемой flood-маршрутизацией и окном конкуренции с взвешиванием RSSI. Федерация мостовых узлов с реальным оборудованием LoRa работает автоматически (тот же формат пакета Meshtastic, без трансляции). Путь обновления: заменить BLE LR-радио на AT-команды SX1276/SX1278 или SPI-драйвер; формат пакета и маршрутизация не меняются.

**Открыто — отслеживается в `OPEN_ISSUES.md`:**
- Включение RF на реальном оборудовании: сквозной тест интероперабельности двух узлов на физических устройствах BLE / Wi-Fi Direct (симуляционные тесты проходят; необходима аппаратная лабораторная сессия)
- NearLink: `harmonyos/teal/` завершён; требует оборудования Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (NearLink-кремний отсутствует на устройствах не Huawei). Windows + Android автоматически используют приближение SSAP-over-BLE.
- LoRa / CircleLink: радиомодуль необходим для настоящего диапазона LoRa. Без него формат Meshtastic переносится по BLE LR (~1,3 км), а федерация мостовых узлов с реальным оборудованием LoRa доступна.

**Внешние вклады пока не принимаются:**
- Протокол всё ещё находится в активной разработке. Внешние вклады в настоящее время не принимаются.
- Реализация транспорта NearLink, примеры интеграции Android/iOS, дополнительные транспортные бэкенды, бенчмарки производительности и фаззинг протокола отслеживаются внутри компании и будут открыты, когда проект достигнет стабильной точки публичного вклада.

## Структура проекта

```
aether-protocol/
  src/
    AetherNet.Core/          Модели протокола, константы, сериализация пакетов
    AetherNet.Security/      Signal Protocol, Ed25519, подпись пакетов
    AetherNet.Transport/     Абстракции транспорта, NearLink, симулятор внутри процесса
    AetherNet.Messaging/     Обработка и ретрансляция сообщений
    AetherNet.Storage/       Персистентное хранение DTN
    AetherNet.Streaming/     Адаптивная потоковая передача, видеомодели и интерфейсы
    AetherNet.Voice/         Голосовые звонки и групповой голос
    AetherNet.Content/       Верификация контента и передача по частям
  samples/
    AetherNet.Demo.Console/  Интерактивная демонстрация
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Реализация на Rust
  typescript/             Реализация на TypeScript
  python/                 Реализация на Python
  go/                     Реализация на Go
  kotlin/                 Реализация на Kotlin/JVM
  swift/                  Реализация на Swift
  c/                      Реализация на C
  docs/
    PROTOCOL_SPEC.md      Спецификация протокола в стиле RFC
```

## Добавление нового транспорта

Реализуйте `ITransportService`:

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implement SendAsync, IsConnected, DataReceived
}
```

Зарегистрируйте его в DI, и `TransportManager` автоматически включит его в выбор транспорта, упорядоченный по энергозатратам.

## Сравнение с аналогами

| Протокол | Ограничение | Преимущество Aether |
|----------|-----------|-----------------|
| **Briar** | Только Android, зависит от Tor | Кросс-платформенный, чистый mesh |
| **Meshtastic** | Только LoRa (макс. 30 кбит/с) | Мультитранспортный (BLE + WiFi + NearLink), поддержка голоса и потоковой передачи |
| **Reticulum** | Python, малое сообщество | 8 языков, совместимость проводного протокола между всеми |
| **libp2p** | Предполагает интернет-магистраль | Сначала офлайн, работает без инфраструктуры |
| **Yggdrasil** | Оверлейная сеть, нужен интернет | Физический уровень mesh, работает без интернета |
| **Signal** | Нет mesh, требует интернет | Работает офлайн, P2P, mesh-ретрансляция, то же E2E-шифрование |

## Точки расширения

Протокол работает автономно. Эти интерфейсы позволяют подключить собственный бэкенд при необходимости:

- `IAetherNetIncentiveProvider` — вознаграждение узлов, ретранслирующих трафик (заглушка по умолчанию: альтруистичная ретрансляция)
- `IAetherNetBackendClient` — синхронизация с сервером при наличии интернета (заглушка по умолчанию: полностью офлайн)
- `IAetherNetFeatureFlagProvider` — переключение возможностей протокола во время выполнения (заглушка по умолчанию: всё включено)

Все три поставляются с заглушками. Уберите их — ничего не сломается.

## Участие в разработке

Внешние вклады пока не открыты. Проект всё ещё находится в активной разработке. Следите за объявлениями об открытии публичного окна вкладов.

## Безопасность

См. [SECURITY.md](SECURITY.md) для политики ответственного раскрытия.

## Лицензия

Лицензия MIT. См. [LICENSE](LICENSE).
