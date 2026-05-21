```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

Compartilhe arquivos, mensagens e streams com pessoas próximas. Sem Wi-Fi. Sem dados móveis. Sem cadastro. Como o AirDrop, mas funciona com qualquer pessoa, em qualquer plataforma.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## O que você pode fazer com ele?

**Compartilhe notas de aula sem gastar dados.**

Você está em um grupo de estudos. Alguém tem provas antigas no celular. O Aether as envia diretamente para o seu dispositivo via Bluetooth — sem hotspot, sem grupo de WhatsApp, sem limite de tamanho. Se alguém no grupo estiver fora do alcance, o arquivo passa de dispositivo em dispositivo até chegar. Mensagens aguardam até 72 horas por uma rota, se necessário.

```
  [Você] ──BLE──▶ [Amigo] ──WiFi──▶ [Amigo do Amigo]
    notes.pdf           repassado, criptografado
```

**Descubra o que está acontecendo ao seu redor.**

Você está em um evento no campus ou em um festival. O Aether descobre outros dispositivos próximos via Bluetooth e Wi-Fi Direct — sem feed de app, sem algoritmo. Você vê o que está realmente ao seu redor, não o que está sendo promovido.

**Envie um SOS quando não há sinal.**

Seu telefone não tem cobertura. O Aether transmite uma mensagem de emergência para todos os dispositivos ao alcance, e esses dispositivos a repassam. Nenhuma torre de celular é necessária.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: chega a todos os dispositivos ao alcance
```

**Crie canais de grupo privados.**

Um canal para o seu andar do dormitório, sua sociedade, sua equipe de projeto. Apenas membros verificados podem ler ou enviar mensagens. Nenhum servidor armazena a conversa.

**Venda coisas para pessoas próximas.**

Anuncie um livro didático para venda. Pessoas que passam dentro do alcance da mesh o veem. Sem conta em marketplace, sem taxas de anúncio — apenas proximidade.

**Assista a um filme juntos, pela mesh.**

Seu grupo vai assistir um filme. Alguém tem o arquivo. O Aether sincroniza a reprodução em todos os dispositivos — play, pause, avançar — tudo em sincronia. Se apenas algumas pessoas têm o arquivo, a mesh o distribui em tempo real como um stream P2P. Todos contribuem via SDPKT para comprá-lo se ninguém o tiver.

## Como funciona

Os dispositivos se comunicam diretamente entre si usando Bluetooth, Wi-Fi Direct ou NearLink. Sem conexão à internet, sem servidor, sem infraestrutura central.

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

Quando uma mensagem não consegue chegar diretamente ao destino, ela passa por outros dispositivos. Esses dispositivos de retransmissão não conseguem ler o que estão carregando — cada mensagem é criptografada com AES-256-GCM. Cada pacote é assinado com chaves de identidade Ed25519, e pacotes forjados são descartados pela rede.

> **Nota sobre maturidade de segurança (leia antes de colocar em produção):** X3DH real (4 X25519 DHs), o Double Ratchet Signal completo (passo de rotação DH no recebimento, KDF_RK, ratchet de cadeia 0x01/0x02) e o pool de chaves pré-uso únicas (padrão: 100 OPKs, FIFO, com proteção de lock) estão implementados em **todas as 8 linguagens** e vinculados a um corpus de fixtures compartilhado multilinguagem em `fixtures/signal/`. O único item aberto restante é a inicialização de RF físico em hardware BLE real (rastreado em `OPEN_ISSUES.md`).

Sem contas, sem números de telefone, sem e-mails. Você gera um par de chaves e já está na rede.

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

**Roteamento** — AODV com respostas de rota assinadas. Cada resposta de rota é assinada pela chave Ed25519 do destino, para que nenhum dispositivo possa se passar por um destino que não é.

**Store-and-forward** — Quando não há rota ativa, os pacotes ficam retidos por até 72 horas até que um caminho se abra.

**Seleção de transporte** — O protocolo escolhe o transporte correto por pacote. Mensagens de controle pequenas vão pelo BLE. Transferências em massa usam Wi-Fi Direct. NearLink quando disponível.

**Voz, vídeo e streaming** — Chamadas de vídeo com negociação de codec (H.264/H.265/VP8), seleção de qualidade adaptada ao transporte, vídeo em grupo com relay SFU automático, watch-together sincronizado com compensação de RTT, e streaming de taxa de bits adaptativa.

**Proteção contra replay** — Deduplicação de nonce com janela de validade de timestamp de 5 minutos.

## Transportes

Cada transporte tem um nome de cor usado em todo o código-fonte. `IsAvailable` protege caminhos bloqueados por hardware — o `TransportManager` os ignora automaticamente e usa o próximo transporte disponível.

| Cor | Nome | Alcance | Banda | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | Relay HTTP celular | Ilimitado | ~10 Mbps | ✅ Windows — servidor relay em `samples/Aether.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE (`android/white/`); Windows: NDEF-over-BLE-GATT + ACR122U PC/SC aproximado (`Windows.Networking.Proximity` removido no Win 11) |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`; Windows + Android: aproximação SSAP-over-BLE (análoga à API, sem compatibilidade de wire) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Formato wire Meshtastic sobre BLE LR (~1.3 km); troca de rádio para SX1276/SX1278 quando módulo LoRa presente |

Ordem de prioridade no `TransportManager`: NearLink → BLE (≤ 1 KB) → Wi-Fi Direct → NFC → LoRa → HTTP Relay (último recurso, `PowerCostRelative = 100`).

## Níveis de implantação

O Aether funciona em qualquer plataforma que suporte Bluetooth ou Wi-Fi. O nível em que você está depende do sistema operacional alvo.

---

### Nível padrão — qualquer plataforma

Android · Windows · Linux · macOS · iOS

O Aether roda completamente em qualquer dispositivo com hardware Bluetooth ou Wi-Fi. Onde um rádio está fisicamente ausente, cada transporte bloqueado é aproximado usando o que está disponível:

- **NearLink (Aether Teal)** — aproximado sobre BLE GATT usando o UUID canônico do serviço Aether SLE (`61657468-6572-0003-0000-000000000000`). A camada de protocolo de aplicação SSAP é idêntica à API do GATT. A camada de rádio (BPSK/QPSK/8PSK, códigos Polar, canais de 1–4 MHz) não é — nós do nível padrão não conseguem trocar bytes brutos com hardware NearLink real; eles interoperam com outros nós Aether do nível padrão.
- **LoRa (Aether Red)** — aproximado usando o formato wire Meshtastic completo sobre BLE 5.0 Coded PHY (S=8, ~1.3 km ao ar livre). A federação de nós-ponte com hardware LoRa real funciona automaticamente — o mesmo formato de pacote Meshtastic percorre todos os saltos sem tradução.
- **NFC (Aether White)** — aproximado via NDEF-over-BLE-GATT com um gate de proximidade por RSSI (≥ −40 dBm ≈ 5–10 cm) que reproduz a semântica de toque para conexão. O caminho PC/SC via leitor NFC USB também é suportado no Windows.

Todas as demais capacidades — BLE, Wi-Fi Direct, relay HTTP, segurança Signal Protocol (X3DH + Double Ratchet), roteamento AODV, DTN store-and-forward, broadcast SOS, voz, streaming — são nativas e idênticas ao nível nativo.

**Este é um deployment completamente funcional e pronto para produção.** A maioria dos apps começa aqui.

---

### Nível nativo — CircleOS / OpenHarmony

CircleOS · HarmonyOS · qualquer SO baseado em OpenHarmony

O CircleOS é construído sobre o OpenHarmony, que traz o silício NearLink (SLE) e o SDK `@kit.NearLinkKit` como uma capacidade nativa de primeira classe do SO. Em dispositivos CircleOS e HarmonyOS com hardware NearLink, nenhuma aproximação é necessária — `harmonyos/teal/` usa o rádio SLE real diretamente:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Este não é apenas uma versão melhorada do nível padrão. Na camada NearLink, é uma rede categoricamente diferente:

| Capacidade | Nível padrão (aproximação BLE) | Nível nativo (CircleOS / OpenHarmony) |
|---|---|---|
| **Alcance NearLink** | ~100 m (BLE) | **600 m** |
| **Banda NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latência NearLink** | ~10 ms (BLE) | **20 µs** |
| **Consumo NearLink** | linha de base BLE | **60% menos que BLE 5.0** |
| **Peers NearLink simultâneos** | ~7 (limite de conexão BLE) | **500+** |
| **Origem NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Rádio SLE real (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / relay HTTP** | Nativo | Nativo (idêntico) |
| **Segurança Signal Protocol** | Completa | Completa (idêntica) |
| **Roteamento / DTN / SOS** | Completo | Completo (idêntico) |
| **Identidade Aether Tag** | Suportada | Suportada (idêntica) |

---

### Transição entre níveis

Nenhuma alteração de código é necessária. O nível é determinado em tempo de execução pelo `IsAvailable` em cada serviço de transporte:

1. Em um dispositivo CircleOS ou HarmonyOS com silício NearLink, `IsAvailable` no transporte NearLink retorna `true` (verificado via checagem de permissão + tentativa de scan passivo).
2. O `TransportManager` automaticamente promove o NearLink para a posição de prioridade — menor custo de energia, maior largura de banda.
3. O código do app, formato de pacote, algoritmo de roteamento, camada de segurança e Aether Tags são idênticos em ambos os níveis.

Um nó no nível padrão e um nó no nível nativo podem se comunicar livremente — eles compartilham o mesmo formato wire, as mesmas sessões Signal Protocol e as mesmas Aether Tags. A diferença de nível afeta apenas o rádio usado para pacotes NearLink, não o protocolo acima dele.

---

> **Internamente, esses níveis são chamados de variante Asterix (padrão) e variante Obelix (nativo).** O Asterix trabalha bem com o que está disponível. O Obelix — rodando no CircleOS com NearLink nativo — opera com capacidade permanentemente elevada, da mesma forma que o Obelix carrega a força da poção mágica sem precisar bebê-la novamente.

---

## Implementações

O Aether é construído em 8 linguagens para rodar em smartphones, laptops, tablets e microcontroladores. Todas as implementações produzem pacotes wire-compatíveis — uma mensagem criptografada pelo nó Rust pode ser retransmitida pelo nó Python e descriptografada pelo nó Swift.

| Linguagem | Diretório | Formato wire | Roteamento/DTN/SOS | X3DH | Double Ratchet | Pool OPK | Voz/Grupo | Streaming/Vídeo/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Todas as 8 linguagens produzem pacotes wire byte-idênticos, verificados por 14 fixtures canônicas de formato wire e 4 vetores de teste Signal executados no CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Roteamento (RREQ/RREP no estilo AODV), DTN store-and-forward, broadcast SOS, voz, streaming e serviços de hardening de segurança estão implementados em todas as linguagens com **~3.000 testes** em todas as 8 implementações:

| Linguagem | Testes | Plataforma CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3.000** | |

A interoperabilidade Signal entre linguagens é ancorada em `fixtures/signal/` com vetores de teste compartilhados para X3DH (`x3dh_basic`), o ratchet simétrico (`ratchet_step_basic`, `ratchet_step_three_iterations`) e KDF_RK (`kdf_rk_basic`). Cada implementação deve produzir saídas byte-idênticas em relação a essas fixtures. Todas as 8 linguagens agora incluem uma sessão Signal completa (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

## Início Rápido

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/Aether.Demo.Console
```

O demo percorre 8 etapas: geração de chaves de identidade Ed25519 para três nós (Alice, Bob, Charlie), estabelecimento de sessões Signal Protocol, envio de mensagens criptografadas, retransmissão de uma mensagem pelo Charlie (que não consegue lê-la), exibição do formato wire binário e demonstração de sigilo futuro em 5 mensagens consecutivas. A saída é colorida e faz pausas entre as etapas.

**Enviando uma mensagem em C#:**

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

O demo gera chaves de identidade para dois nós, troca bundles de pré-chave, estabelece sessões criptografadas, envia mensagens criptografadas em ambas as direções, cria e assina pacotes mesh, verifica assinaturas e serializa pacotes no formato wire binário. Também demonstra a camada de transporte em processo.

**Enviando uma mensagem em Rust:**

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

O demo cria dois nós em uma rede simulada, gera chaves Ed25519, estabelece sessões Signal Protocol, cria e assina um pacote, serializa-o para o formato binário compatível com C#, criptografa uma mensagem secreta, descriptografa-a no outro nó, envia-a pelo transporte e verifica o ida e volta.

**Enviando uma mensagem em TypeScript:**

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

O demo executa 8 demonstrações: geração de chave Ed25519 e detecção de adulteração, criação de nó com capacidades, troca de chave X3DH do Signal Protocol, criptografia e descriptografia AES-256-GCM, serialização de pacotes, assinatura de pacotes com detecção de replay, transporte em processo e um fluxo completo de ponta a ponta combinando todas as camadas.

**Enviando uma mensagem em Python:**

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

O demo executa 5 demonstrações: ida e volta de serialização de pacotes, assinatura Ed25519 com detecção de adulteração, estabelecimento de sessão Signal Protocol com troca de mensagens criptografadas em ambas as direções, transporte em processo entre dois peers e deduplicação de nonce para proteção contra replay.

**Enviando uma mensagem em Go:**

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

O demo percorre 11 etapas: geração de chaves, criação de nó com capacidades, inicialização do Signal Protocol, troca de bundle de pré-chave, estabelecimento de sessão, criação e assinatura de pacotes, serialização, desserialização com verificação de assinatura, criptografia de ponta a ponta com ratcheting de chave, detecção de ataque de replay e transporte em processo.

**Enviando uma mensagem em Kotlin:**

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

O demo executa 5 testes: ida e volta de serialização de pacotes, assinatura Ed25519 com rejeição de adulteração, estabelecimento de sessão Signal Protocol com criptografia AES-256-GCM, entrega de mensagem via transporte em processo e um fluxo completo de ponta a ponta onde Alice assina um pacote e Bob o verifica após o transporte.

**Enviando uma mensagem em Swift:**

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

O demo executa 7 demonstrações: geração de chave Ed25519, criação e assinatura de pacotes, serialização no formato wire binário, desserialização com verificações de integridade, criptografia e descriptografia AES-256-GCM, autenticação de mensagem HMAC-SHA256 e derivação de chave HKDF-SHA256.

**Enviando uma mensagem em C:**

```c
aether_mesh_packet_t *packet = aether_packet_new();
packet->type = AETHER_PACKET_TYPE_DATA;
packet->ttl = 7;

aether_packet_set_source_uhid(packet, "alice");
aether_packet_set_destination_uhid(packet, "bob");
aether_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aether_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aether_ed25519_sign(private_key, signable, signable_len, signature);
aether_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aether_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aether_packet_free(packet);
```

## Roteiro

O que foi construído e o que vem a seguir.

**Concluído (verificado entre linguagens, todas as 8 implementações):**
- Formato wire: byte-idêntico entre 8 linguagens, ancorado por 14 fixtures canônicas e asserções multilinguagem no CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — matriz de 9 jobs (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, mais job de integridade de fixtures) em `.github/workflows/ci.yml`.
- Assinatura e verificação de pacotes Ed25519
- Criptografia AES-256-GCM
- Primitivos de derivação de chave HKDF / HMAC
- Serialização de pacotes + layout de assinatura (LE + campos int32 de 4 bytes)
- Simulador de transporte em processo (para desenvolvimento e testes)
- Serviço de roteamento inspirado em AODV com RREQ/RREP, respostas de rota assinadas, dedup, encaminhamento TTL
- Serviço DTN store-and-forward com transferência de custódia, replicação com geohash, TTL de 72h
- Serviço de broadcast SOS com flood, dedup, guarda de auto-origem, limite de taxa (3/hora)
- Pontos de extensibilidade: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (padrões Noop)
- **~3.000 testes** em todas as 8 linguagens (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — todos verdes no CI
- ✅ **Chave efêmera X3DH real (8 linguagens)** — 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) com derivação de root via HKDF-SHA256. Ancorado por `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Alinhamento do Double Ratchet em toda a família** — Signal §5 completo com HMAC-SHA256 + separação de domínio 0x01/0x02 no ratchet simétrico, HKDF-SHA256 KDF_RK no passo DH-ratchet, rotação DH no recebimento. Verificado pelas fixtures `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 reconciliados com HEAD** — veja `docs/PROTOCOL_SPEC.md`.

**Concluído (todas as 8 linguagens):**
- ✅ **Chamadas de voz (1-para-1)** — máquina de estado de sinalização (Offer/Answer/Hangup/Cancel/Timeout) + transporte de frame binário (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Entrega com consciência de rota via `IRoutingService`.
- ✅ **Voz em grupo** — associação controlada pelo host (convidar/expulsar/sair), campo de geração de chave por frame, fan-out unicast para todos os membros atuais, rotação de chave controlada pelo host na mudança de associação.
- ✅ **Live streaming** — o publicador transmite `StreamAnnounce`; os assinantes enviam `StreamSubscribe`; frames binários `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast para cada assinante.
- ✅ **Chamadas de vídeo (1-para-1)** — negociação de codec/resolução/fps/bitrate na sinalização, sinais de requisição de keyframe e mudança de qualidade, formato binário `VideoFrame` correspondente ao layout de voz.
- ✅ **Watch Together** — o host emite comandos `WatchSync` autoritativos (play/pause/seek/speed); seguidores aplicam com compensação de RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` fire-and-forget.
- ✅ **Pool de chave pré-uso única (OPK)** — padrão 100, emissão FIFO, reposição lazy, consumo com proteção de lock em todas as 8 linguagens. Corrige a vulnerabilidade de concorrência de OPK único.
- ✅ **C: sessão Signal completa** — `aether_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` em `c/src/signal_protocol.c`; 6 testes E2E de dois nós em `c/tests/test_signal_session.c`. Todas as 8 linguagens agora têm Signal Protocol com sessão completa.

**Concluído (apenas referência C#):**
- ✅ **Demo Etapa 9 — MessagingService + DTN fallback de ponta a ponta** — `samples/Aether.Demo.Console` percorre mensagens criptografadas Signal reais com DTN store-and-forward quando o destinatário está offline.
- ✅ **Bridge `Aether.Messaging` ↔ `Aether.Security`** — `SignalMessageEnvelopeCipher` torna a camada de mensagens criptografada de ponta a ponta por padrão; mensagens sem sessão Signal são enfileiradas, nunca enviadas de forma insegura.
- ✅ **Streaming de taxa de bits adaptativa** — `AdaptiveBitrateController` com escadas de bitrate definidas pela especificação para os Perfis A (tempo real), B (transmissão ao vivo) e C (VOD). O publicador seleciona o degrau sustentável mais alto (20% de margem) e emite `StreamAbandon` (`PacketType.StreamAbandon`) em vez de um segmento quando abaixo do piso. `IStreamingService` expõe `UpdateBandwidthEstimate` e `GetCurrentBitrateRung`.
- ✅ **Watch Together: ingestão BitTorrent + ChipIn de financiamento coletivo** — modelos `TorrentInfo` / `TorrentFile`; `WatchTogetherService` trata `PacketType.TorrentMetadata` e dispara `TorrentReceived`. Máquina de estado `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` em `IWatchTogetherService`.
- ✅ **Chamadas de vídeo em grupo com relay SFU automático** — `GroupVideoService` / `IGroupVideoService`. Topologia FullMesh para ≤ 3 participantes; switch automático para SFU no `SfuThresholdParticipants` (4) com reatribuição de relay via `GroupVideoSignaling(SfuAssigned)`. Fan-out em FullMesh, envio somente via relay no modo SFU. Tipo de pacote de sinalização `GroupVideoSignaling = 35`.
- ✅ **Simulação de transporte BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Framing de MTU GATT via `BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), registro estático de peers em processo, broadcast de anúncio. Todas as restrições `BleMaxPayloadBytes` aplicadas.
- ✅ **Simulação de transporte Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Ciclo de vida explícito `ConnectAsync`/`DisconnectAsync`, entrega direta de payloads grandes (sem framing), eventos bidirecionais `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulação de transporte NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU de frame de 4096 B, registro de 500 peers, `ConnectedPeerCount`, `IsAvailable` configurável em tempo de execução.
- ✅ **Testes de simulação de inicialização RF** — Testes de interoperabilidade de dois nós (`SimulatedTransportTests`): ida e volta de `MeshPacket` BLE + NearLink, transferência de payload de 64 KB via WiFi Direct. Camada de software totalmente verificada; sessão de laboratório em dispositivo físico necessária para validação em hardware.

**Concluído (camada de transporte C# — todos fail-fast):**
- ✅ **Transporte BLE GATT real** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (servidor Android GATT). Teste completo de inicialização RF em `samples/Aether.BleRfTest/`.
- ✅ **Transporte Wi-Fi Direct real** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket porta 8888) + `android/green/` (`WifiP2pManager`). Teste RF em `samples/Aether.WifiDirectRfTest/`.
- ✅ **Transporte relay HTTP (Aether Purple)** — `HttpRelayTransportService` com long-poll de 10 segundos, `PowerCostRelative = 100`, sempre último recurso. Servidor relay em `samples/Aether.RelayServer/` (ASP.NET Core minimal API, porta 5200). Teste RF em `samples/Aether.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implementa `HostApduService` com AID `F061657468657200`. `WinNfcStubTransportService` documenta dois caminhos de aproximação no Windows: (1) NDEF-over-BLE-GATT com gate de RSSI ≥ −40 dBm (simula tap-to-connect sem silício NFC, `IsAvailable = Bluetooth presente`); (2) leitor USB ACR122U via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = leitor sem contato enumerado`). Caminho de upgrade: implementar `ITransportService` quando a Microsoft lançar uma API P2P NFC oficial.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implementação ArkTS HarmonyOS 5.0.1 (API 13) completa usando `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` verificado em tempo de execução. `WinNearLinkStubTransportService` + `android/teal/` documentam a aproximação SSAP-over-BLE: BLE GATT com UUID do serviço Aether SLE `61657468-6572-0003-0000-000000000000` — análogo à API do SSAP, sem compatibilidade de wire com hardware NearLink real. Caminho de upgrade: substituir chamadas BLE GATT por chamadas SDK `ssapc_*`/`ssaps_*`; UUIDs e slot no `TransportManager` inalterados.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` documentam a aproximação Meshtastic-over-BLE-LR: formato wire Meshtastic completo (header de 16 bytes + AES-256-CTR protobuf) sobre BLE 5.0 Coded PHY S=8 (~1.3 km ao ar livre), com roteamento managed-flood e janela de contenção ponderada por RSSI. A federação de nós-ponte com hardware LoRa real funciona automaticamente (mesmo formato de pacote Meshtastic, sem tradução). Caminho de upgrade: substituir rádio BLE LR por driver AT-command ou SPI SX1276/SX1278; formato de pacote e roteamento inalterados.

**Em aberto — rastreado em `OPEN_ISSUES.md`:**
- Inicialização RF em hardware real: teste de interoperabilidade de ponta a ponta de dois nós em dispositivos BLE / Wi-Fi Direct físicos (testes de simulação passam; sessão de laboratório em hardware necessária)
- NearLink: `harmonyos/teal/` completo; requer hardware Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silício NearLink não presente em dispositivos não-Huawei). Windows + Android fazem fallback para aproximação SSAP-over-BLE automaticamente.
- LoRa / CircleLink: módulo de rádio necessário para alcance LoRa real. Sem ele, o formato wire Meshtastic é transportado sobre BLE LR (~1.3 km) e a federação de nós-ponte com hardware LoRa real está disponível.

**Ainda não aberto para contribuição externa:**
- O protocolo ainda está em desenvolvimento ativo. Contribuições externas não estão sendo aceitas no momento.
- Implementação de transporte NearLink, exemplos de integração Android/iOS, backends de transporte adicionais, benchmarks de desempenho e fuzzing de protocolo são rastreados internamente e serão abertos quando o projeto atingir um ponto estável de contribuição pública.

## Estrutura do Projeto

```
aether-protocol/
  src/
    Aether.Core/          Modelos de protocolo, constantes, serialização de pacotes
    Aether.Security/      Signal Protocol, Ed25519, assinatura de pacotes
    Aether.Transport/     Abstrações de transporte, NearLink, simulador em processo
    Aether.Messaging/     Tratamento e retransmissão de mensagens
    Aether.Storage/       Persistência DTN store-and-forward
    Aether.Streaming/     Streaming de taxa de bits adaptativa, modelos e interfaces de vídeo
    Aether.Voice/         Chamadas de voz e voz em grupo
    Aether.Content/       Verificação de conteúdo e transferência em chunks
  samples/
    Aether.Demo.Console/  Demo interativo
  tests/
    Aether.Security.Tests/
    Aether.Protocol.Tests/
  rust/                   Implementação Rust
  typescript/             Implementação TypeScript
  python/                 Implementação Python
  go/                     Implementação Go
  kotlin/                 Implementação Kotlin/JVM
  swift/                  Implementação Swift
  c/                      Implementação C
  docs/
    PROTOCOL_SPEC.md      Especificação de protocolo no estilo RFC
```

## Adicionando um Novo Transporte

Implemente `ITransportService`:

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

Registre no DI e o `TransportManager` o incluirá automaticamente na seleção de transporte, ordenado por custo de energia.

## Comparação com Outros Protocolos

| Protocolo | Limitação | Vantagem do Aether |
|----------|-----------|-----------------|
| **Briar** | Somente Android, dependente do Tor | Multiplataforma, mesh puro |
| **Meshtastic** | Apenas LoRa (máx. 30 kbps) | Multi-transporte (BLE + WiFi + NearLink), capacidade de voz e streaming |
| **Reticulum** | Python, comunidade pequena | 8 linguagens, wire-compatível entre todas |
| **libp2p** | Pressupõe backbone de internet | Offline-first, funciona sem infraestrutura |
| **Yggdrasil** | Rede overlay, precisa de internet | Mesh de camada física, funciona sem internet |
| **Signal** | Sem mesh, exige internet | Funciona offline, P2P, relay em mesh, mesma criptografia E2E |

## Pontos de Extensão

O protocolo funciona de forma independente. Estas interfaces permitem conectar seu próprio backend, se quiser:

- `IAetherIncentiveProvider` — recompensa nós que retransmitem tráfego (padrão noop: retransmissão altruísta)
- `IAetherBackendClient` — sincroniza com um servidor quando há internet disponível (padrão noop: totalmente offline)
- `IAetherFeatureFlagProvider` — ativa/desativa funcionalidades do protocolo em tempo de execução (padrão noop: tudo habilitado)

Os três vêm com implementações noop. Remova-os e nada quebra.

## Contribuição

Contribuições externas ainda não estão abertas. O projeto ainda está em desenvolvimento ativo. Verifique novamente quando anunciarmos uma janela de contribuição pública.

## Segurança

Veja [SECURITY.md](SECURITY.md) para a política de divulgação responsável.

## Licença

Licença MIT. Veja [LICENSE](LICENSE).
