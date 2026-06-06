# Aether Mesh Networking Protocol - Implementação em Python

[English](../../../../python/README.md) · [Français](../../fr/python/README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Uma implementação em Python do protocolo de rede mesh Aether, fornecendo operações criptográficas compatíveis com o formato de transmissão da implementação de referência em C#.

## Visão Geral

Aether é um protocolo de rede mesh descentralizado projetado para ambientes com conectividade à internet intermitente ou ausente. Este pacote Python fornece:

- **Assinatura Ed25519**: Geração de chaves, assinatura e verificação usando PyNaCl
- **Signal Protocol X3DH**: Troca de chaves assíncrona com ECDH P-256
- **Criptografia AES-256-GCM**: Criptografia simétrica por mensagem com nonces de 12 bytes
- **Derivação de Chaves HKDF-SHA256**: Derivação de chaves compatível com RFC 5869, com strings de informação específicas por contexto
- **Ratchet Simétrico**: Derivação de chaves de mensagem baseada em HMAC-SHA256 com sigilo futuro
- **Serialização de Pacotes**: Formato binário little-endian compatível com a implementação em C#
- **Prevenção de Ataques de Replay**: Deduplicação baseada em nonce com TTL de 5 minutos
- **Transporte em Processo**: Transporte simulado para testes de comunicação mesh

## Instalação

### Via PyPI (quando publicado)
```bash
pip install aether-protocol
```

### A partir do Código-Fonte
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Dependências de Desenvolvimento
```bash
pip install -e ".[dev]"
```

## Início Rápido

```python
import asyncio
from aethernet.security.ed25519_service import Ed25519SigningService
from aethernet.security.signal_protocol import SignalProtocolService
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Arquitetura

### Estrutura do Pacote

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNetNode, PeerInfo, RouteEntry)
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

## Funcionalidades Principais

### 1. Serviço de Assinatura Ed25519

Utiliza PyNaCl (libsodium) para operações criptográficas:

```python
from aethernet.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Tamanhos de Chave:**
- Chave privada: 32 bytes (semente Ed25519)
- Chave pública: 32 bytes (ponto Ed25519)
- Assinatura: 64 bytes

### 2. Signal Protocol

Implementa a troca de chaves X3DH com ratchet simétrico para sigilo futuro:

```python
from aethernet.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**Derivação de Chaves:**
- Utiliza HKDF-SHA256 com salt: `"AetherNetSignal"`
- Info da chave raiz: `"aether-root-v1"`
- Info da cadeia de envio: `"aether-chain-send-v1"`
- Info da cadeia de recebimento: `"aether-chain-recv-v1"`

**Ratchet Simétrico:**
- Utiliza HMAC-SHA256 com a chave de cadeia
- Deriva novas chaves de mensagem e avança a cadeia a cada mensagem
- Suporta até 1000 chaves ignoradas para entrega fora de ordem
- Criptografia por mensagem: AES-256-GCM com nonce aleatório de 12 bytes

### 3. Serialização de Pacotes

Formato binário compatível com o formato de transmissão da implementação em C#:

```python
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**Formato de Transmissão (Little-Endian):**
- Versão do protocolo: 1 byte
- Tipo do pacote: 1 byte
- ID do pacote: 16 bytes (UUID)
- Prioridade: 1 byte
- TTL: 4 bytes (int32)
- TimestampMs: 8 bytes (int64)
- Comprimento do SourceUhid: 2 bytes + dados UTF-8
- Comprimento do DestinationUhid: 2 bytes + dados UTF-8
- Comprimento do PacketNonce: 2 bytes + dados
- Comprimento do Payload: 4 bytes + dados
- Comprimento da Assinatura: 2 bytes + dados

### 4. Assinatura de Pacotes

Assina pacotes usando Ed25519 e detecta ataques de replay:

```python
from aethernet.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Dados Assináveis:**
Conforme a seção 2.3 da especificação do protocolo, a assinatura cobre:
- PacketNonce (8 bytes)
- TimestampMs (8 bytes, int64 little-endian)
- Type (4 bytes, int32 little-endian)
- SourceUhid (comprimento + UTF-8)
- DestinationUhid (comprimento + UTF-8)
- SHA-256(Payload) (32 bytes)
- Ttl (4 bytes, int32 little-endian)
- Priority (4 bytes, int32 little-endian)

**Prevenção de Replay:**
- Mantém cache de pares (sender_uhid, nonce) vistos
- TTL de 5 minutos por entrada no cache
- Limpeza automática a cada 60 segundos

### 5. Serviços de Transporte

Classe base abstrata para transportes físicos (BLE, Wi-Fi Direct, etc.):

```python
from aethernet.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**Funcionalidades do InProcessTransport:**
- Registro global de nós a nível de classe
- Thread-safe com threading.Lock
- Ideal para testes e simulação de mesh local
- Propriedades: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Referência de Constantes

Todas as constantes do protocolo estão definidas em `aether/constants.py`:

### Criptografia
- `ED25519_PRIVATE_KEY_SIZE`: 32 bytes
- `ED25519_PUBLIC_KEY_SIZE`: 32 bytes
- `ED25519_SIGNATURE_SIZE`: 64 bytes
- `AES_GCM_NONCE_SIZE`: 12 bytes
- `AES_GCM_TAG_SIZE`: 16 bytes
- `MAX_SKIPPED_KEYS`: 1000

### Roteamento
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(Consulte `constants.py` para a lista completa)

## Executando a Demonstração

Demonstra todas as funcionalidades principais com saída colorida:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

A demonstração cobre:
1. Geração de chaves Ed25519 e assinatura
2. Criação de nós com AetherNetNode
3. Troca de chaves X3DH do Signal Protocol
4. Criptografia e descriptografia de mensagens
5. Serialização/desserialização de pacotes
6. Assinatura de pacotes e detecção de ataques de replay
7. Comunicação via transporte em processo
8. Fluxo completo de criptografia ponta a ponta

## Dependências

### Em Tempo de Execução
- `pynacl>=1.5.0` - Assinatura Ed25519 via libsodium
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Desenvolvimento
- `pytest>=7.4.0` - Framework de testes
- `pytest-asyncio>=0.21.0` - Suporte a testes assíncronos
- `black>=23.0.0` - Formatação de código
- `mypy>=1.5.0` - Verificação de tipos estática
- `ruff>=0.1.0` - Linting

## Compatibilidade

**Versão do Python:** 3.10+

**Plataforma:** Multiplataforma (Windows, macOS, Linux)

**Backend Criptográfico:** Utiliza os backends libsodium do sistema e a biblioteca cryptography, garantindo comportamento consistente entre plataformas.

## Referências do Protocolo

- **Roteamento AODV:** RFC 3561
- **Acordo de Chaves X3DH:** Signal Foundation, novembro de 2016
- **Double Ratchet:** Signal Foundation, novembro de 2016
- **HKDF:** RFC 5869 (Extract-and-Expand baseado em HMAC)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB et al., 2012

## Considerações de Segurança

### Zeragem de Chaves
O material criptográfico intermediário é zerado após o uso:
- Segredos compartilhados do ECDH
- Chaves de mensagem do ratchet simétrico
- Material de chave derivado no contexto de estabelecimento

Em Python, a zeragem real de memória in-place é limitada, mas dados sensíveis são eliminados do escopo de variáveis imediatamente após o uso.

### Modelo de Ameaça
O Aether assume:
- Escuta passiva em BLE/Wi-Fi
- Injeção ativa de pacotes e replay
- Ataques Sybil via criação de nós falsos
- Negação seletiva de serviço

As proteções incluem:
- **Confidencialidade:** Chaves AES-256-GCM por mensagem
- **Integridade:** Assinaturas Ed25519 nos pacotes
- **Prevenção de Replay:** Deduplicação baseada em nonce
- **Sigilo Futuro:** Ratchet simétrico com chaves por mensagem
- **Autenticação de Rota:** Respostas de Rota assinadas

### Limitações
- A entrega de mensagens fora de ordem é suportada para até 1000 mensagens
- Mensagens além do intervalo são rejeitadas
- Os endereços BLE se renovam a cada 15 minutos (não implementado em Python)
- A janela de migração de P-256 para Ed25519 é de 30 dias (fallback ainda não implementado)

## Testes

Execute o conjunto de testes:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## Licença

Licença MIT - Consulte o arquivo LICENSE para mais detalhes

## Contribuindo

Para contribuir com melhorias:

1. Certifique-se de que o código segue o estilo PEP 8 (use `black` para formatação)
2. Adicione anotações de tipo a todas as funções
3. Inclua docstrings para APIs públicas
4. Execute `mypy` para verificação de tipos
5. Adicione testes para novas funcionalidades

## Referências

- Especificação do Protocolo Aether: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Implementação de Referência em C#: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
