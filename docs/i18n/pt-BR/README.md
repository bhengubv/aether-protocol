# AetherNet — protocolo de rede mesh offline-first

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**O AetherNet é um protocolo de rede mesh de código aberto, licenciado sob MIT** para enviar mensagens, arquivos, voz e vídeo a pessoas próximas — com **nenhuma internet, nenhum servidor e nenhum cadastro**. Os dispositivos se conectam diretamente por Bluetooth, Wi-Fi Direct, NearLink e LoRa; quando o destinatário está fora do alcance, as mensagens saltam por outros dispositivos e aguardam até 72 horas por uma rota. Ele entrega **implementações byte a byte idênticas em oito linguagens de programação** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift e C.

Compartilhe arquivos, mensagens e streams com pessoas próximas. Sem Wi-Fi. Sem dados móveis. Sem cadastro. Como o AirDrop, mas funciona com qualquer pessoa, em qualquer plataforma.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Um protocolo, oito linguagens, idêntico no fio.** O Aether é implementado em **C#, Rust, TypeScript, Python, Go, Kotlin, Swift e C** — e cada pacote é byte a byte idêntico entre todas elas, garantido por um corpus de fixtures multilinguagem compartilhado no CI. Construa seu nó em qualquer uma das oito; ele interopera com todas as outras. Este README também está disponível em 11 idiomas humanos (links acima).

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

## O que você recebe — todo serviço, em todas as linguagens

O Aether não é apenas um transporte. Todo tipo de pacote reservado pelo protocolo é agora um **serviço real e funcional em todas as 8 linguagens**, e cada um deles serializa para **pacotes de fio byte-idênticos** — um pacote construído pelo nó Go é decodificado, sem alteração, pelo nó Swift, Rust, C, Python, TypeScript, Kotlin ou C#. Cada serviço está vinculado a uma fixture multilinguagem compartilhada em `fixtures/<service>/` e exercitado por testes unitários por linguagem, com Swift e C adicionalmente verificados no servidor de build macOS.

| Capacidade | O que faz | Tipo(s) de pacote | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Beacon e consulta de presença** | Anuncia "estou aqui" e pergunta "quem está por perto?" — através de um **ID efêmero rotativo, derivado de chave** (não sua identidade real) mais um geohash grosseiro | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Keep-alive leve de liveness entre peers vinculados | 10 | `fixtures/heartbeat/` | ✅ |
| **Sincronização de perfil** | Troca um cartão de perfil assinado com um peer pela mesh | 23 | `fixtures/profiles/` | ✅ |
| **Anúncio de ID efêmero** | Informa em privado a um amigo seu ID de roteamento rotativo atual para que ele ainda consiga te alcançar depois que ele rotacionar | 56 | `fixtures/erid/` | ✅ |
| **Troca de pré-chave** | Solicita e entrega um bundle de pré-chave Signal pela mesh, para inicializar uma sessão de ponta a ponta com alguém que você nunca encontrou | 25, 26 | `fixtures/prekey/` | ✅ |
| **Canais** | Mensagens assinadas para um canal de grupo privado, exclusivo para membros | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Frames de voz tipo walkie-talkie (payload de áudio codificado opaco) | 15 | `fixtures/media/` | ✅ |
| **Compartilhamento de tela** | Frames de vídeo de compartilhamento de tela (payload de vídeo codificado opaco) | 32 | `fixtures/media/` | ✅ |
| **Controle de chamada** | Sinalização de tocar / aceitar / recusar / desligar para chamadas de voz e vídeo | 27 | `fixtures/videocall/` | ✅ |
| **Confirmação de SOS** | Confirma ao remetente que seu broadcast de emergência foi recebido | 6 | `fixtures/sos/` | ✅ |
| **Migalhas de espaço** | Migalhas de descoberta com tag de localização para a camada "o que está ao meu redor" | 40 | `fixtures/space/` | ✅ |
| **Anúncio de forge** | Anuncia um artefato de conteúdo derivado/forjado para a mesh | 41 | `fixtures/forge/` | ✅ |
| **Requisição de shard do vault** | Busca um shard de armazenamento com código de apagamento (quaisquer K de N shards reconstroem o arquivo) | 42 | `fixtures/vaultshard/` | ✅ |
| **Medição de largura de banda** | Sonda / confirma / dissemina a vazão do link para que a mesh roteie pelo cano mais largo (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Estes ficam sobre os serviços já completos de **mensageria, voz 1-para-1 e em grupo, chamadas de vídeo, transmissão ao vivo, watch-together, roteamento AODV, DTN store-and-forward e flood de SOS** — também implementados em todas as 8 linguagens.

> **O que "construído" significa aqui, com precisão.** Cada serviço produz e trata seu pacote de fio, dispara os eventos corretos e está vinculado a uma fixture em nível de byte que toda a família de linguagens deve corresponder. Sua aplicação conecta o serviço à sua sessão Signal, tabela de roteamento e estado local. Esta é a camada de protocolo — comprovada em código, testes e fixtures de byte multilinguagem — no mesmo terreno honesto de RF que todo o resto: qualquer caminho que em última instância trafega por um rádio permanece não verificado em campo até a inicialização de hardware rastreada em `OPEN_ISSUES.md`.

## Segurança e privacidade

Além do conjunto de serviços de fio, o Aether inclui uma pequena **camada de segurança e privacidade** — gerenciamento de chaves de identidade e proteção anti-rastreamento na camada de enlace. Como todo o resto, cada uma é implementada em **todas as 8 linguagens** e vinculada a uma fixture multilinguagem compartilhada em `fixtures/<feature>/` (Swift e C verificados adicionalmente no servidor de build macOS). Estes *não* são mais quatro dos 18 serviços de fio: três não definem **nenhum novo tipo de pacote de fio**, e o quarto carrega seus próprios envelopes **dentro do caminho DTN/mesh existente** em vez de como um novo pacote reservado.

| Capacidade | O que faz | Camada | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Backup por frase de recuperação** | Faz backup de uma identidade como uma frase **BIP-39 de 24 palavras** e a restaura em qualquer dispositivo. BIP-39 padrão (verificado contra os vetores oficiais da Trezor), com checksum SHA-256 de modo que uma palavra digitada errada é *rejeitada*, nunca silenciosamente incorreta. Sem servidor, sem custodiante — a frase **é** a identidade. | local | `fixtures/bip39/` | ✅ |
| **Proteção anti-rastreamento Bluetooth** | Deriva um **Service UUID** BLE rotativo, derivado de chave (HMAC-SHA256, janela de 15 minutos) e **endereços privados resolvíveis** (IRK + a função RFC `ah`, AES-128) — o material anti-rastreamento que um anunciante BLE precisa para que um scanner passivo não consiga vinculá-lo ao longo do tempo ou do lugar. | camada de enlace | `fixtures/bleprivacy/` | ✅ |
| **Apagamento de pânico** | Um **PIN de coação** (SHA-256, comparado em tempo constante) que, sob coação, apaga com segurança cada chave de identidade — sobrescrever com aleatório e depois zerar — sem deixar nada a recuperar. | local | `fixtures/panicwipe/` | ✅ |
| **Sincronização multidispositivo** | Sincronização **descentralizada, sem servidor** entre seus *próprios* dispositivos: um **DeviceLink** assinado com Ed25519 os emparelha, e envelopes **SyncRecord** de último-a-escrever-vence reconciliam o estado — transportados com criptografia de ponta a ponta sobre o DTN/mesh existente, sem conta na nuvem e sem servidor de sincronização. | sobre DTN | `fixtures/sync/` | ✅ |

**Uma assimetria honesta.** O `DeviceLink` multidispositivo é assinado com Ed25519, e essa assinatura é **byte-idêntica em 7 das 8 linguagens**. O CryptoKit da Apple *randomiza* deliberadamente as assinaturas Ed25519, então no Swift os 64 bytes de assinatura diferem a cada vez — mas o **corpo assinado é byte-idêntico** e cada link ainda se verifica em todos os 8 SDKs, de modo que o Swift atinge paridade de **verificação** em vez de paridade de bytes de assinatura. Isso é uma propriedade da criptografia da plataforma, não um defeito, e é o único lugar entre estas quatro funcionalidades onde "byte-idêntico" leva um asterisco. Os formatos de fio completos estão em [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12; o modelo de ameaças está em [`THREAT_MODEL.md`](THREAT_MODEL.md).

## Transportes

Cada transporte tem um nome de cor usado em todo o código-fonte. `IsAvailable` protege caminhos bloqueados por hardware — o `TransportManager` os ignora e usa o próximo transporte disponível.

**Legenda de status:** ✅ real, construído e verificado · ⏳ real, verificação em andamento · ⚠️ real em algumas plataformas, stub em outras · ❌ stub (ainda sem código de transporte).

| Cor | Nome | Alcance | Banda | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Real — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Real — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | Relay HTTP / QUIC | Ilimitado | ~10 Mbps | ✅ Real — Windows; servidor relay em `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Canal de dados de internet | Ilimitado | ~100 Mbps | ✅ Real em todas as 8 linguagens — **verificado em loopback nas 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust — cada um tem dois peers trocando bytes por um canal de dados ICE real) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Real no Android (`android/white/`); Windows = BLE-GATT real + aproximação de proximidade por RSSI −40 dBm (`WinNfcBleTransportService`, compila net9/10, runtime não verificado) — `Windows.Networking.Proximity` removido no Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Real no HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — verificação em dispositivo pendente); Android + Windows = aproximação SSAP-over-BLE real (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; compilação + teste unitário verificados, runtime não verificado) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Driver serial RYLR SX127x/SX126x real (`LoRaSerialTransport` em C#/Go/Rust/C; compila, runtime não verificado — precisa de um módulo físico); a ponte BLE Coded-PHY ainda é um design documentado |

Os transportes de rádio são reais apenas onde há código de plataforma (C#/Windows, Kotlin/Android, HarmonyOS). As oito bibliotecas de linguagem de resto entregam um transporte de **simulação em processo** para testes — **o WebRTC é o primeiro transporte real comum a todas elas** (completo; verificado em loopback entre as linguagens).

A prioridade segue o custo de energia: a mesh de rádio é preferida, depois o WebRTC como caminho direto de internet, com o relay HTTP/QUIC como último recurso.

## Níveis de implantação

O Aether funciona em qualquer plataforma que suporte Bluetooth ou Wi-Fi. O nível em que você está depende do sistema operacional alvo.

---

### Nível padrão — qualquer plataforma

Android · Windows · Linux · macOS · iOS

O Aether roda em qualquer dispositivo com hardware Bluetooth ou Wi-Fi. Onde um rádio está fisicamente ausente, cada transporte bloqueado é aproximado usando o que está disponível. Essas aproximações são agora **código real** (compilação verificada; **runtime não verificado**, pendente de um teste de RF em 2 dispositivos / hardware):

- **NearLink (Aether Teal)** — aproximação SSAP-over-BLE-GATT real (UUID Aether SLE `61657468-6572-0003-…`) no Android (`android/teal/AetherNetSleService`) e Windows (`WinNearLinkBleTransportService`); compilação + teste unitário verificados, runtime não verificado. O rádio NearLink real existe apenas no HarmonyOS (`harmonyos/teal/`, verificação em dispositivo pendente).
- **LoRa (Aether Red)** — driver serial RYLR SX127x/SX126x real (`LoRaSerialTransport` em **todas as 8 linguagens** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; cada port com compilação verificada, incluindo Swift + C no servidor de build Mac; runtime não verificado — precisa de um módulo físico). A ponte Meshtastic-over-BLE-Coded-PHY (~1.3 km) continua sendo um design documentado; LoRa de longo alcance real precisa de um nó com capacidade LoRa (gateway, SBC ou handset robusto com um módulo LoRa).
- **NFC (Aether White)** — real no Android (HCE). O Windows agora tem uma aproximação de proximidade BLE-GATT + RSSI −40 dBm real (`WinNfcBleTransportService`, compila net9/10; runtime não verificado); ACR122U PC/SC quando um leitor está presente.

O que é real e idêntico em toda parte: **BLE, Wi-Fi Direct, o relay HTTP/QUIC e o transporte WebRTC P2P (verificado em loopback em todas as 8 linguagens)**, mais a segurança Signal Protocol (X3DH + Double Ratchet), roteamento AODV, DTN store-and-forward, broadcast SOS, voz e streaming.

**Status honesto:** BLE + Wi-Fi Direct + relay são reais e prontos para produção; **o WebRTC P2P é real e verificado em loopback em todas as 8 linguagens** (dois peers trocam bytes por um canal de dados ICE real — Rust confirmado na máquina Linux `.201` com ICE UDP funcionando); as aproximações NearLink / LoRa / NFC-no-Windows são agora código real que compila (LoRa com compilação verificada nas 8, incl. Swift + C no servidor de build Mac; NearLink-Android também testado unitariamente) mas está **não verificado em runtime** — ainda sem teste de RF em hardware / 2 dispositivos. Elas participam da mesh em código; não implante essas três esperando RF comprovado em campo.

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

Todas as 8 linguagens produzem pacotes wire byte-idênticos, verificados por 17 fixtures canônicas de formato wire e 6 vetores de teste Signal executados no CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Roteamento (RREQ/RREP no estilo AODV), DTN store-and-forward, broadcast SOS, voz, streaming e serviços de hardening de segurança estão implementados em todas as linguagens com **~3.000 testes** em todas as 8 implementações:

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

A interoperabilidade Signal entre linguagens é ancorada em `fixtures/signal/` com vetores de teste compartilhados para X3DH (`x3dh_basic`), o ratchet simétrico (`ratchet_step_basic`, `ratchet_step_three_iterations`), KDF_RK (`kdf_rk_basic`) e o round-trip completo de sessão X3DH (`x3dh_session_msg1`, `x3dh_session_reply`). Cada implementação deve produzir saídas byte-idênticas em relação a essas fixtures. Todas as 8 linguagens agora incluem uma sessão Signal completa (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Além do formato wire e do Signal, toda a **suíte de serviços de fio** — presença, heartbeat, sincronização de perfil, anúncio de ID efêmero, troca de pré-chave, canais, push-to-talk, compartilhamento de tela, controle de chamada, confirmação de SOS, migalhas de espaço, anúncio de forge, requisição de shard do vault e medição de largura de banda (veja **O que você recebe**) — também é implementada em todas as 8 linguagens e vinculada às suas próprias fixtures (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/` e irmãs). Nenhum recurso é exclusivo do C# na camada de protocolo.

## Início Rápido

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
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

## Roteiro

O que foi construído e o que vem a seguir.

**Concluído (verificado entre linguagens, todas as 8 implementações):**
- Formato wire: byte-idêntico entre 8 linguagens, ancorado por 17 fixtures canônicas e asserções multilinguagem no CI (`fixtures/expected/*.bin`)
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
- ✅ **C: sessão Signal completa** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` em `c/src/signal_protocol.c`; 6 testes E2E de dois nós em `c/tests/test_signal_session.c`. Todas as 8 linguagens agora têm Signal Protocol com sessão completa.

**Concluído (todas as 8 linguagens — a suíte completa de serviços de fio):**
- ✅ **Todo tipo de pacote reservado é agora um serviço real e byte-idêntico em todas as 8 linguagens.** Beacon/consulta de presença (21/22), heartbeat (10), sincronização de perfil (23), anúncio de ID de roteamento efêmero (56), troca de pré-chave (25/26), canais (7), push-to-talk (15), compartilhamento de tela (32), controle de chamada (27), confirmação de SOS (6), migalhas de espaço (40), anúncio de forge (41), requisição de shard do vault (42) e medição de largura de banda / ABMF (53/54/55). Cada um é um serviço enxuto (produz + trata + evento) que o host conecta à sua sessão Signal e tabela de roteamento; cada um está vinculado a uma fixture multilinguagem compartilhada (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) e exercitado por testes unitários por linguagem, com Swift e C verificados no servidor de build macOS. Veja **O que você recebe**.

**Concluído (apenas referência C#):**
- ✅ **Demo Etapa 9 — MessagingService + DTN fallback de ponta a ponta** — `samples/AetherNet.Demo.Console` percorre mensagens criptografadas Signal reais com DTN store-and-forward quando o destinatário está offline.
- ✅ **Bridge `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` torna a camada de mensagens criptografada de ponta a ponta por padrão; mensagens sem sessão Signal são enfileiradas, nunca enviadas de forma insegura.
- ✅ **Streaming de taxa de bits adaptativa** — `AdaptiveBitrateController` com escadas de bitrate definidas pela especificação para os Perfis A (tempo real), B (transmissão ao vivo) e C (VOD). O publicador seleciona o degrau sustentável mais alto (20% de margem) e emite `StreamAbandon` (`PacketType.StreamAbandon`) em vez de um segmento quando abaixo do piso. `IStreamingService` expõe `UpdateBandwidthEstimate` e `GetCurrentBitrateRung`.
- ✅ **Watch Together: ingestão BitTorrent + ChipIn de financiamento coletivo** — modelos `TorrentInfo` / `TorrentFile`; `WatchTogetherService` trata `PacketType.TorrentMetadata` e dispara `TorrentReceived`. Máquina de estado `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` em `IWatchTogetherService`.
- ✅ **Chamadas de vídeo em grupo com relay SFU automático** — `GroupVideoService` / `IGroupVideoService`. Topologia FullMesh para ≤ 3 participantes; switch automático para SFU no `SfuThresholdParticipants` (4) com reatribuição de relay via `GroupVideoSignaling(SfuAssigned)`. Fan-out em FullMesh, envio somente via relay no modo SFU. Tipo de pacote de sinalização `GroupVideoSignaling = 35`.
- ✅ **Simulação de transporte BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Framing de MTU GATT via `BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), registro estático de peers em processo, broadcast de anúncio. Todas as restrições `BleMaxPayloadBytes` aplicadas.
- ✅ **Simulação de transporte Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Ciclo de vida explícito `ConnectAsync`/`DisconnectAsync`, entrega direta de payloads grandes (sem framing), eventos bidirecionais `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulação de transporte NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU de frame de 4096 B, registro de 500 peers, `ConnectedPeerCount`, `IsAvailable` configurável em tempo de execução.
- ✅ **Testes de simulação de inicialização RF** — Testes de interoperabilidade de dois nós (`SimulatedTransportTests`): ida e volta de `MeshPacket` BLE + NearLink, transferência de payload de 64 KB via WiFi Direct. Camada de software totalmente verificada; sessão de laboratório em dispositivo físico necessária para validação em hardware.

**Concluído (camada de transporte C# — todos fail-fast):**
- ✅ **Transporte BLE GATT real** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (servidor Android GATT). Teste completo de inicialização RF em `samples/AetherNet.BleRfTest/`.
- ✅ **Transporte Wi-Fi Direct real** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket porta 8888) + `android/green/` (`WifiP2pManager`). Teste RF em `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Transporte relay HTTP (Aether Purple)** — `HttpRelayTransportService` com long-poll de 10 segundos, `PowerCostRelative = 100`, sempre último recurso. Servidor relay em `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, porta 5200). Teste RF em `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implementa `HostApduService` com AID `F061657468657200`. `WinNfcStubTransportService` documenta dois caminhos de aproximação no Windows: (1) NDEF-over-BLE-GATT com gate de RSSI ≥ −40 dBm (simula tap-to-connect sem silício NFC, `IsAvailable = Bluetooth presente`); (2) leitor USB ACR122U via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = leitor sem contato enumerado`). Caminho de upgrade: implementar `ITransportService` quando a Microsoft lançar uma API P2P NFC oficial.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implementação ArkTS HarmonyOS 5.0.1 (API 13) completa usando `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` verificado em tempo de execução. `WinNearLinkStubTransportService` + `android/teal/` documentam a aproximação SSAP-over-BLE: BLE GATT com UUID do serviço Aether SLE `61657468-6572-0003-0000-000000000000` — análogo à API do SSAP, sem compatibilidade de wire com hardware NearLink real. Caminho de upgrade: substituir chamadas BLE GATT por chamadas SDK `ssapc_*`/`ssaps_*`; UUIDs e slot no `TransportManager` inalterados.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` documentam a aproximação Meshtastic-over-BLE-LR: formato wire Meshtastic completo (header de 16 bytes + AES-256-CTR protobuf) sobre BLE 5.0 Coded PHY S=8 (~1.3 km ao ar livre), com roteamento managed-flood e janela de contenção ponderada por RSSI. A federação de nós-ponte com hardware LoRa real funciona automaticamente (mesmo formato de pacote Meshtastic, sem tradução). Caminho de upgrade: substituir rádio BLE LR por driver AT-command ou SPI SX1276/SX1278; formato de pacote e roteamento inalterados.

**Em aberto — rastreado em `OPEN_ISSUES.md`:**
- Inicialização RF em hardware real: teste de interoperabilidade de ponta a ponta de dois nós em dispositivos BLE / Wi-Fi Direct físicos (testes de simulação passam; sessão de laboratório em hardware necessária)
- NearLink: `harmonyos/teal/` completo; requer hardware Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silício NearLink não presente em dispositivos não-Huawei). Windows + Android fazem fallback para aproximação SSAP-over-BLE automaticamente.
- LoRa / CircleLink: módulo de rádio necessário para alcance LoRa real. Sem ele, o formato wire Meshtastic é transportado sobre BLE LR (~1.3 km) e a federação de nós-ponte com hardware LoRa real está disponível.
- ✅ **(RESOLVIDO v1.2.0)** Superfície de protocolo para consumidores (Wave 16/17) — evento `IDtnService.BundleReceived` para bundles de entrada ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), diretório de nomeação/descoberta na camada de aplicação ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), interface de gorjeta a autores ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Os 3 foram entregues de forma aditiva nas 8 linguagens com fixtures multilinguagem byte-idênticas. Veja CHANGELOG.

**Ainda não aberto para contribuição externa:**
- O protocolo ainda está em desenvolvimento ativo. Contribuições externas não estão sendo aceitas no momento.
- Implementação de transporte NearLink, exemplos de integração Android/iOS, backends de transporte adicionais, benchmarks de desempenho e fuzzing de protocolo são rastreados internamente e serão abertos quando o projeto atingir um ponto estável de contribuição pública.

## Estrutura do Projeto

```
aether-protocol/
  src/
    AetherNet.Core/          Modelos de protocolo, constantes, serialização de pacotes
    AetherNet.Security/      Signal Protocol, Ed25519, assinatura de pacotes
    AetherNet.Transport/     Abstrações de transporte, NearLink, simulador em processo
    AetherNet.Messaging/     Tratamento e retransmissão de mensagens
    AetherNet.Storage/       Persistência DTN store-and-forward
    AetherNet.Streaming/     Streaming de taxa de bits adaptativa, modelos e interfaces de vídeo
    AetherNet.Voice/         Chamadas de voz e voz em grupo
    AetherNet.Content/       Verificação de conteúdo e transferência em chunks
  samples/
    AetherNet.Demo.Console/  Demo interativo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
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

## Perguntas frequentes

**O AetherNet funciona sem internet?**
Sim — ele é offline-first. Os dispositivos se comunicam diretamente por Bluetooth, Wi-Fi Direct, NearLink ou LoRa e retransmitem mensagens salto a salto por outros dispositivos, sem necessidade de conexão à internet, torre de celular ou servidor. Quando não existe rota ativa, as mensagens ficam retidas (store-and-forward tolerante a atrasos) por até 72 horas até que uma se abra.

**É criptografado de ponta a ponta?**
Sim. O AetherNet usa o Signal Protocol (acordo de chaves X3DH mais o Double Ratchet sobre X25519) para criptografia de ponta a ponta, AES-256-GCM para os payloads das mensagens e assinaturas Ed25519 em cada pacote. Os dispositivos que retransmitem uma mensagem não conseguem lê-la.

**Quais transportes ele usa?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), um rádio serial LoRa/CircleLink, um relay HTTP/QUIC e WebRTC para peer-to-peer direto pela internet. O protocolo seleciona automaticamente o transporte disponível de menor consumo por pacote e faz fallback para o próximo.

**Em quais linguagens de programação ele está disponível?**
Oito — C#, Rust, TypeScript, Python, Go, Kotlin, Swift e C. Cada implementação produz pacotes de fio byte-idênticos, garantido por um corpus de fixtures multilinguagem compartilhado no CI, de modo que um pacote construído por uma linguagem é decodificado sem alteração por qualquer outra.

**Como ele se diferencia do Meshtastic, Briar ou Bridgefy?**
O Meshtastic é somente LoRa; o AetherNet é multi-transporte (Bluetooth + Wi-Fi + NearLink + LoRa) e carrega voz, vídeo e streaming, além de mensagens. O Briar é somente Android e roteia pelo Tor; o AetherNet é multiplataforma e mesh puro. Diferentemente de SDKs fechados, o AetherNet é licenciado sob MIT e implementado abertamente em oito linguagens. A tabela de comparação acima tem os detalhes.

**Ele está pronto para produção?**
A camada de protocolo — formato de fio, segurança Signal, roteamento, DTN store-and-forward e a suíte completa de serviços — está implementada e testada em todas as oito linguagens. Os transportes de rádio são reais onde há código de plataforma (Bluetooth e Wi-Fi no Windows e Android, WebRTC em toda parte) e não verificados em campo nos demais casos, pendentes da inicialização de hardware, que é rastreada honestamente em `OPEN_ISSUES.md`. Leia as notas de status em cada seção antes de implantar.

**Sob qual licença ele está?**
MIT — livre para uso comercial e de código aberto. Veja [LICENSE](LICENSE).

**Quem constrói o AetherNet?**
Ele é desenvolvido como o protocolo aberto por trás do ecossistema mesh da The Geek Network, construído na África do Sul para comunicação que funciona com ou sem dados móveis.

## Pontos de Extensão

O protocolo funciona de forma independente. Estas interfaces permitem conectar seu próprio backend, se quiser:

- `IAetherNetIncentiveProvider` — recompensa nós que retransmitem tráfego (padrão noop: retransmissão altruísta)
- `IAetherNetBackendClient` — sincroniza com um servidor quando há internet disponível (padrão noop: totalmente offline)
- `IAetherNetFeatureFlagProvider` — ativa/desativa funcionalidades do protocolo em tempo de execução (padrão noop: tudo habilitado)

Os três vêm com implementações noop. Remova-os e nada quebra.

## Contribuição

Contribuições externas ainda não estão abertas. O projeto ainda está em desenvolvimento ativo. Verifique novamente quando anunciarmos uma janela de contribuição pública.

## Segurança

Veja [SECURITY.md](SECURITY.md) para a política de divulgação responsável.

## Licença

Licença MIT. Veja [LICENSE](LICENSE).

## Traduções

Este README é mantido em inglês e traduzido para outros 10 idiomas em [`docs/i18n/`](docs/i18n/): Français, Español, العربية, 中文简体, 日本語, Deutsch, Português (BR), Русский, فارسی e 한국어. A **versão em inglês é a fonte da verdade** — quando uma tradução e o texto em inglês divergem, o texto em inglês é o autoritativo, e as traduções podem ficar atrás dele por uma ou duas releases. O protocolo, código, fixtures e comportamento descritos são idênticos independentemente do idioma que você leia.
