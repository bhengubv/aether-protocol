# Aether Mesh Networking Protocol - реализация на Python

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Реализация протокола mesh-сети Aether на Python, обеспечивающая совместимость криптографических операций на уровне проводного формата с эталонной реализацией на C#.

## Обзор

Aether — это децентрализованный протокол mesh-сети, разработанный для сред с нестабильным или отсутствующим подключением к интернету. Этот пакет Python предоставляет:

- **Подпись Ed25519**: генерация ключей, подпись и верификация с использованием PyNaCl
- **Signal Protocol X3DH**: асинхронный обмен ключами с ECDH P-256
- **Шифрование AES-256-GCM**: симметричное шифрование на уровне сообщений с 12-байтовыми nonce
- **Деривация ключей HKDF-SHA256**: деривация ключей согласно RFC 5869 с контекстно-специфичными info-строками
- **Симметричный трещоточный механизм**: деривация ключей сообщений на основе HMAC-SHA256 с прямой секретностью
- **Сериализация пакетов**: бинарный проводной формат с порядком байтов little-endian, совместимый с реализацией на C#
- **Защита от атак повторного воспроизведения**: дедупликация на основе nonce с TTL 5 минут
- **Внутрипроцессный транспорт**: эмулированный транспорт для тестирования mesh-коммуникаций

## Установка

### Из PyPI (после публикации)
```bash
pip install aether-protocol
```

### Из исходников
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Зависимости для разработки
```bash
pip install -e ".[dev]"
```

## Быстрый старт

```python
import asyncio
from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.signal_protocol import SignalProtocolService
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Генерация ключей Ed25519
private_key, public_key = Ed25519SigningService.generate_keypair()

# Подпись сообщения
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Верификация подписи
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Архитектура

### Структура пакета

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherMeshNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## Ключевые возможности

### 1. Сервис подписи Ed25519

Использует PyNaCl (libsodium) для криптографических операций:

```python
from aethermesh.security.ed25519_service import Ed25519SigningService

# Генерация пары ключей
private_key, public_key = Ed25519SigningService.generate_keypair()

# Подпись данных
signature = Ed25519SigningService.sign(private_key, data)

# Верификация подписи
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Размеры ключей:**
- Закрытый ключ: 32 байта (seed Ed25519)
- Открытый ключ: 32 байта (точка Ed25519)
- Подпись: 64 байта

### 2. Signal Protocol

Реализует обмен ключами X3DH с симметричным трещоточным механизмом для прямой секретности:

```python
from aethermesh.security.signal_protocol import SignalProtocolService

# Создание экземпляров протокола
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Боб публикует набор предварительных ключей
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Алиса обрабатывает набор для установления сеанса
await alice_signal.process_pre_key_bundle(bob_bundle)

# Алиса шифрует сообщение
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Для двунаправленной связи Боб также должен обработать набор Алисы
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Боб расшифровывает сообщение
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**Деривация ключей:**
- Использует HKDF-SHA256 с солью: `"AetherMeshSignal"`
- Info корневого ключа: `"aether-root-v1"`
- Info цепочки отправки: `"aether-chain-send-v1"`
- Info цепочки приёма: `"aether-chain-recv-v1"`

**Симметричный трещоточный механизм:**
- Использует HMAC-SHA256 с цепочечным ключом
- Выводит новые ключи сообщений и продвигает цепочку с каждым сообщением
- Поддерживает до 1000 пропущенных ключей для доставки не по порядку
- Шифрование на уровне сообщений: AES-256-GCM со случайным 12-байтовым nonce

### 3. Сериализация пакетов

Бинарный формат, совместимый на уровне проводного формата с реализацией на C#:

```python
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Создание пакета
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Сериализация в бинарный формат
binary = PacketSerializer.serialize(packet)

# Десериализация из бинарного формата
decoded_packet = PacketSerializer.deserialize(binary)
```

**Проводной формат (little-endian):**
- Версия протокола: 1 байт
- Тип пакета: 1 байт
- Идентификатор пакета: 16 байт (UUID)
- Приоритет: 1 байт
- TTL: 4 байта (int32)
- TimestampMs: 8 байт (int64)
- Длина SourceUhid: 2 байта + данные UTF-8
- Длина DestinationUhid: 2 байта + данные UTF-8
- Длина PacketNonce: 2 байта + данные
- Длина Payload: 4 байта + данные
- Длина Signature: 2 байта + данные

### 4. Подпись пакетов

Подписывает пакеты с использованием Ed25519 и обнаруживает атаки повторного воспроизведения:

```python
from aethermesh.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Подпись пакета
signing_service.sign_packet(packet, private_key)

# Верификация пакета (включая проверку повторных воспроизведений)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Подписываемые данные:**
Согласно разделу 2.3 спецификации протокола, подпись охватывает:
- PacketNonce (8 байт)
- TimestampMs (8 байт, int64 little-endian)
- Type (4 байта, int32 little-endian)
- SourceUhid (длина + UTF-8)
- DestinationUhid (длина + UTF-8)
- SHA-256(Payload) (32 байта)
- Ttl (4 байта, int32 little-endian)
- Priority (4 байта, int32 little-endian)

**Защита от повторных воспроизведений:**
- Хранит кэш просмотренных пар (sender_uhid, nonce)
- TTL каждой записи кэша — 5 минут
- Автоматическая очистка каждые 60 секунд

### 5. Транспортные сервисы

Абстрактный базовый класс для физических транспортов (BLE, Wi-Fi Direct и т.д.):

```python
from aethermesh.transport.in_process import InProcessTransport

# Создание экземпляров внутрипроцессного транспорта
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Регистрация обратного вызова для входящих сообщений
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Отправка сообщения
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**Возможности InProcessTransport:**
- Глобальный реестр узлов на уровне класса
- Потокобезопасность с использованием threading.Lock
- Идеально подходит для тестирования и локальной симуляции mesh
- Свойства: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Справочник по константам

Все константы протокола определены в `aether/constants.py`:

### Криптография
- `ED25519_PRIVATE_KEY_SIZE`: 32 байта
- `ED25519_PUBLIC_KEY_SIZE`: 32 байта
- `ED25519_SIGNATURE_SIZE`: 64 байта
- `AES_GCM_NONCE_SIZE`: 12 байт
- `AES_GCM_TAG_SIZE`: 16 байт
- `MAX_SKIPPED_KEYS`: 1000

### Маршрутизация
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(Полный список см. в `constants.py`)

## Запуск демонстрации

Демонстрирует все основные возможности с цветным выводом:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

Демонстрация охватывает:
1. Генерацию ключей Ed25519 и подпись
2. Создание узла с помощью AetherMeshNode
3. Обмен ключами Signal Protocol X3DH
4. Шифрование и расшифровку сообщений
5. Сериализацию/десериализацию пакетов
6. Подпись пакетов и обнаружение атак повторного воспроизведения
7. Коммуникацию через внутрипроцессный транспорт
8. Полный сквозной рабочий процесс шифрования

## Зависимости

### Среда выполнения
- `pynacl>=1.5.0` — подпись Ed25519 через libsodium
- `cryptography>=41.0.0` — ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Разработка
- `pytest>=7.4.0` — фреймворк тестирования
- `pytest-asyncio>=0.21.0` — поддержка асинхронных тестов
- `black>=23.0.0` — форматирование кода
- `mypy>=1.5.0` — статическая проверка типов
- `ruff>=0.1.0` — линтинг

## Совместимость

**Версия Python:** 3.10+

**Платформа:** кросс-платформенная (Windows, macOS, Linux)

**Криптографический бэкенд:** использует системные бэкенды libsodium и библиотеки cryptography, обеспечивая согласованное поведение на всех платформах.

## Ссылки на протокол

- **Маршрутизация AODV:** RFC 3561
- **Соглашение о ключах X3DH:** Signal Foundation, ноябрь 2016
- **Double Ratchet:** Signal Foundation, ноябрь 2016
- **HKDF:** RFC 5869 (HMAC-based Extract-and-Expand)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB и др., 2012

## Соображения безопасности

### Обнуление ключей
Промежуточные криптографические материалы обнуляются после использования:
- Общие секреты из ECDH
- Ключи сообщений из симметричного трещоточного механизма
- Производный ключевой материал в контексте установления сеанса

В Python истинное обнуление памяти на месте ограничено, но чувствительные данные немедленно удаляются из области видимости переменных после использования.

### Модель угроз
Aether предполагает:
- Пассивное прослушивание BLE/Wi-Fi
- Активную инъекцию пакетов и атаки повторного воспроизведения
- Sybil-атаки через создание поддельных узлов
- Избирательный отказ в обслуживании

Защитные меры включают:
- **Конфиденциальность:** ключи на уровне сообщений AES-256-GCM
- **Целостность:** подписи пакетов Ed25519
- **Защита от повторных воспроизведений:** дедупликация на основе nonce
- **Прямая секретность:** симметричный трещоточный механизм с ключами на уровне сообщений
- **Аутентификация маршрутов:** подписанные ответы на маршрутные запросы

### Ограничения
- Доставка сообщений не по порядку поддерживается до 1000 сообщений
- Сообщения за пределами этого промежутка отвергаются
- Адреса BLE меняются каждые 15 минут (в Python не реализовано)
- Окно миграции P-256 на Ed25519 составляет 30 дней (запасной вариант пока не реализован)

## Тестирование

Запуск набора тестов:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## Лицензия

Лицензия MIT — подробности см. в файле LICENSE

## Участие в разработке

Для внесения улучшений:

1. Убедитесь, что код соответствует стилю PEP 8 (используйте `black` для форматирования)
2. Добавьте аннотации типов ко всем функциям
3. Включите строки документации для публичных API
4. Запустите `mypy` для проверки типов
5. Добавьте тесты для новых возможностей

## Ссылки

- Спецификация протокола Aether: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Эталонная реализация на C#: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
