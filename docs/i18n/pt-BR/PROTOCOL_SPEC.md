# Especificação do Protocolo de Rede em Malha Aether

**Versão:** 2.0
**Status:** Reconciliado com HEAD (2026-05-05)
**Data:** 2026-03-15 (rascunho inicial); 2026-05-05 (§2, §4, §10, §11 reconciliados, §3/§9 verificados)
**Autores:** The Other Bhengu (Pty) Ltd t/a The Geek e Bhengu B.V.

> **Aviso ao leitor.** Rascunhos anteriores deste documento antecedem o
> alinhamento de formato de wire em 8 linguagens e o port para X25519 +
> Signal Double Ratchet em toda a família. A partir de 2026-05-05, §2
> (Formato de Pacote), §3 (Roteamento), §4 (Troca de Chaves), §9 (DTN)
> descrevem o protocolo implementado; §10 (Streaming de Vídeo) e §11
> (Watch Together) descrevem o protocolo alvo — eles são wire-definidos
> e testados por fixtures, mas os pipelines de codec / BitTorrent /
> ChipIn ainda não estão vinculados ao scaffolding. A referência em C#
> é autoritativa em todo lugar onde este documento e a implementação divergem.
>
> - Bytes de wire canônicos: `fixtures/expected/*.bin` (10 casos nomeados)
> - Serializador de referência: `src/AetherMesh.Core/Protocol/PacketSerializer.cs`
> - Pilha Signal de referência: `src/AetherMesh.Security/Services/SignalProtocolService.cs`
> - Roteamento de referência: `src/AetherMesh.Core/Routing/RoutingService.cs`
> - DTN de referência: `src/AetherMesh.Core/Dtn/DtnService.cs`
> - Prova de interoperabilidade wire entre linguagens: `fixtures/README.md`
> - Prova de interoperabilidade Signal entre linguagens: `fixtures/signal/README.md`

---

## Índice

1. [Resumo](#1-abstract)
2. [Formato de Pacote](#2-packet-format)
3. [Algoritmo de Roteamento](#3-routing-algorithm)
4. [Troca de Chaves](#4-key-exchange)
5. [Requisitos da Camada de Transporte](#5-transport-layer-requirements)
6. [Protocolo de Descoberta](#6-discovery-protocol)
7. [Modelo de Segurança](#7-security-model)
8. [Broadcast SOS](#8-sos-broadcast)
9. [DTN Store-and-Forward](#9-dtn-store-and-forward)
10. [Streaming de Vídeo](#10-video-streaming)
11. [Watch Together](#11-watch-together)

---

## 1. Resumo

O Aether é um protocolo de rede em malha descentralizado projetado para ambientes com
conectividade à internet intermitente ou ausente. Ele oferece roteamento de pacotes
multi-salto sobre transportes heterogêneos de curto alcance (Bluetooth Low Energy,
Wi-Fi Direct, NearLink), criptografia de ponta a ponta usando um acordo de chaves
derivado de X3DH com um ratchet simétrico, entrega store-and-forward tolerante a atrasos
e um mecanismo de flood de SOS de emergência. O protocolo é agnóstico quanto ao
transporte: qualquer camada física capaz de enviar e receber arrays de bytes entre pares
é um transporte Aether válido. Os nós são identificados por Identificadores Universais
de Hardware (UHIDs) e autenticados por chaves de identidade Ed25519. O Aether foi
concebido como uma camada de rede universal — todo aplicativo do ecossistema registra
serviços Aether, e nós sem conectividade à internet alcançam a rede mais ampla por meio
de pares gateway que fazem a ponte entre o tráfego de malha e a internet.

---

## 2. Formato de Pacote

> Reconciliado em 2026-05-05 com `src/AetherMesh.Core/Protocol/PacketSerializer.cs`
> e os 10 casos de fixture em `fixtures/expected/`.

### 2.1. Layout de Wire do MeshPacket

Toda mensagem Aether é encapsulada em um `MeshPacket`. Os campos aparecem no
wire **exatamente** nesta ordem:

| Off | Campo            | Tipo                            | Tamanho    | Observações |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = não assinado (legado), `2` = assinado (atual) |
| 1   | Type             | uint8                           | 1          | Enumeração de tipo de pacote (ver §2.4) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | Identificador de pacote para deduplicação. Ordem de bytes **big-endian**, NÃO o padrão mixed-endian Guid do .NET. |
| 18  | Priority         | uint8                           | 1          | Nível de prioridade (0 = normal, 255 = SOS). **Campo de wire é 1 byte; valores >255 devem ser limitados.** |
| 19  | Ttl              | int32, little-endian            | 4          | Time-to-live, decrementado em cada salto. **int32 de 4 bytes**, NÃO uint8 de 1 byte — valores até ~2³¹-1 são válidos. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Milissegundos do epoch Unix (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | Comprimento de `SourceUhid` em bytes UTF-8. Máx 65535. |
| 33  | SourceUhid       | Bytes UTF-8                     | N          | UHID do remetente; vazio é permitido, mas incomum. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | Comprimento de `DestinationUhid` em bytes UTF-8. |
| ... | DestinationUhid  | Bytes UTF-8                     | M          | UHID do destinatário; string vazia para broadcast. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | Comprimento de `PacketNonce` em bytes. Valor padrão: 8. |
| ... | PacketNonce      | bytes                           | P          | Nonce criptograficamente aleatório para prevenção de replay. |
| ... | Payload Len      | int32, little-endian            | 4          | Comprimento de `Payload` em bytes. Valores negativos são um erro. |
| ... | Payload          | bytes                           | Q          | Dados da aplicação. Interpretação depende de `Type`. |
| ... | Signature Len    | uint16, little-endian           | 2          | Comprimento de `Signature` em bytes. 0 (não assinado) ou 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | Assinatura Ed25519 sobre os dados assináveis (ver §2.3). |

**As larguras do prefixo de comprimento** variam por campo — `SourceUhid`, `DestinationUhid`,
`PacketNonce` e `Signature` usam prefixos de comprimento de **2 bytes (uint16)**;
`Payload` usa um prefixo de comprimento de **4 bytes (int32)** porque os payloads podem
exceder 64 KiB.

### 2.2. Tamanho Mínimo do Pacote

Com todos os campos de comprimento variável vazios (UHIDs de comprimento zero, nonce
de comprimento zero, payload de comprimento zero, assinatura de comprimento zero),
o tamanho no wire é:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

Os valores de 50 bytes / 52 bytes em rascunhos anteriores desta especificação estavam incorretos.

### 2.3. Diagrama do Formato de Wire

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

Para um exemplo elaborado, consulte `fixtures/expected/basic_data.bin` (83 bytes,
entrada canônica em `fixtures/inputs.json`). As implementações são validadas
contra o corpus completo de fixtures — qualquer divergência falha o teste de
verificador de fixture entre linguagens.

### 2.4. Construção dos Dados Assináveis

A assinatura (campo `Signature` no wire) é computada sobre uma sequência de bytes
canônica separada — **não** sobre os bytes de wire em si. Isso permite que o layout
de wire evolua sem quebrar assinaturas, e permite que nós intermediários verifiquem
a integridade sem ver o payload em plaintext (apenas seu hash SHA-256 é assinado).

A sequência de bytes assináveis é a concatenação:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> Observe a divergência deliberada em relação ao layout de wire do §2.1: os dados
> assináveis usam **int32 de 4 bytes** para `Type`, `Length`, `Ttl` e `Priority`,
> enquanto o wire usa 1 byte / 2 bytes / 4 bytes / 1 byte respectivamente.
> Isso é intencional — a forma assinável é portável entre linguagens e usa campos
> de largura fixa; a forma de wire é compacta para economia de PDU BLE.
> As implementações devem limitar `Priority` a `[0,255]` antes de codificá-lo
> nos bytes assináveis; caso contrário, o receptor (que vê o byte de wire 0..255)
> derivará um buffer assinável diferente e a verificação falhará.

A implementação de referência está em `src/AetherMesh.Security/Services/
PacketSigningService.cs::BuildSignableData` e é leitura obrigatória para ports.

### 2.5. Tipos de Pacote

| Valor | Nome              | Direção       | Descrição |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | Solicitação de Rota AODV |
| 2     | RouteReply        | Unicast       | Resposta de Rota AODV (DEVE ser assinada pelo destino) |
| 3     | Data              | Unicast       | Dados da aplicação |
| 4     | Ack               | Unicast       | Confirmação de entrega |
| 5     | SosBroadcast      | Flood         | Broadcast de emergência (ver Seção 8) |
| 6     | SosAck            | Unicast       | Confirmação de SOS |
| 7     | ChannelMessage    | Multicast     | Mensagem de canal de grupo |
| 8     | ChunkRequest      | Unicast       | Solicitação de chunk de conteúdo P2P |
| 9     | ChunkData         | Unicast       | Resposta de chunk de conteúdo P2P |
| 10    | Heartbeat         | Broadcast     | Sinal periódico de liveness |
| 11    | StreamAnnounce    | Broadcast     | Anúncio de stream ao vivo |
| 12    | StreamSegment     | Unicast/Tree  | Segmento de mídia de stream ao vivo |
| 13    | StreamSubscribe   | Unicast       | Solicitação para entrar na árvore de relay do stream |
| 14    | StreamUnsubscribe | Unicast       | Sair da árvore de relay do stream |
| 15    | VoicePtt          | Unicast       | Frame de voz push-to-talk |
| 16    | VoiceCall         | Unicast       | Frame de chamada de voz em tempo real |
| 17    | VoiceSignaling    | Unicast       | Configuração/encerramento de chamada de voz |
| 18    | DtnBundle         | Unicast       | Bundle DTN store-and-forward (ver Seção 9) |
| 19    | DtnCustodyAck     | Unicast       | Confirmação de transferência de custódia DTN |
| 20    | DtnDeliveryReceipt| Unicast       | Confirmação de entrega ponta a ponta DTN |
| 21    | PresenceBeacon    | Broadcast     | Anúncio de presença e disponibilidade |
| 22    | PresenceQuery     | Unicast       | Solicitação de status de presença |
| 23    | ProfileSync       | Unicast       | Sincronização de metadados de perfil |
| 24    | TipPacket         | Unicast       | Gorjeta de nó (liquidada via LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | Solicitação do bundle de pré-chave do par |
| 26    | PreKeyResponse    | Unicast       | Entrega do bundle de pré-chave |
| 27    | VideoCall         | Unicast       | Frame de vídeo criptografado (unidade NAL H.264/H.265/VP8) |
| 28    | VideoSignaling    | Unicast       | Configuração de chamada de vídeo: offer, answer, reject, bye, negociação de codec |
| 29    | WatchSync         | Unicast       | Comando de reprodução sincronizada: play, pause, seek, speed |
| 30    | WatchReaction     | Multicast     | Reação de emoji ou voz com timestamp durante watch-together |
| 31    | VideoFrame        | Unicast/SFU   | Frame de vídeo em grupo (SFU relay distribui para os participantes) |
| 32    | ScreenShare       | Unicast       | Frame de compartilhamento de tela (mesmo pipeline de vídeo, sinalizado separadamente) |
| 33    | WatchChunkRequest | Unicast       | Solicitação de chunk prioritária orientada à posição de reprodução |
| 34    | TorrentMetadata   | Multicast     | Arquivo .torrent do BitTorrent ou troca de metadados de magnet link |

### 2.6. Capacidades do Nó

Os nós anunciam suas capacidades como um bitfield:

| Bit | Valor | Capacidade  | Descrição |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Transporte Bluetooth Low Energy disponível |
| 1   | 2     | WifiDirect  | Transporte Wi-Fi Direct disponível |
| 2   | 4     | Gateway     | Gateway de internet (faz a ponte entre malha e rede IP) |
| 3   | 8     | Relay       | Disposto a retransmitir pacotes para outros |
| 4   | 16    | Sos         | Capaz de broadcast SOS |
| 5   | 32    | Streaming   | Capaz de relay de streaming ao vivo |
| 6   | 64    | Voice       | Capaz de relay de chamadas de voz |
| 7   | 128   | DtnCarrier  | Transportador DTN store-and-forward |
| 8   | 256   | NearLink    | Transporte NearLink disponível |
| 9   | 512   | Video       | Capaz de codificação/decodificação de vídeo |

---

## 3. Algoritmo de Roteamento

O Aether usa um protocolo de roteamento reativo baseado no roteamento AODV (Ad-hoc
On-demand Distance Vector), estendido com autenticação criptográfica de rotas e
seleção de rotas ponderada por QoS.

### 3.1. Route Request (RREQ)

Quando um nó precisa enviar um pacote a um destino para o qual não possui rota,
ele inicia uma Solicitação de Rota:

1. O originador cria um `MeshPacket` com `Type = RouteRequest`, define `SourceUhid`
   para si mesmo, `DestinationUhid` para o alvo e `TTL = 7` (o padrão).
2. O pacote é transmitido em broadcast para todos os pares conectados diretamente.
3. Cada nó intermediário que recebe um RREQ:
   a. Verifica se já viu este RREQ pelo `Id` do pacote. Se sim, descarta o pacote
      silenciosamente (deduplicação). O cache de deduplicação contém até `DeduplicationCacheSize`
      entradas (padrão 10.000) e é totalmente limpo quando o limite é atingido.
   b. Instala uma **rota reversa** para o originador do RREQ. A rota reversa registra
      o UHID do par do qual o RREQ foi recebido como o próximo salto. A contagem de
      saltos é derivada de `DefaultTtl - packet.Ttl + 1`.
   c. Se ele for o destino, gera um RREP (ver Seção 3.2).
   d. Se tiver uma rota válida existente para o destino, PODE gerar um RREP em nome
      do destino.
   e. Caso contrário, decrementa o TTL e retransmite o RREQ em broadcast.
4. O originador aguarda um RREP com um timeout de **5.000 ms** (`RouteTimeoutMs`).
   Se nenhum RREP chegar, a descoberta de rota falha.

### 3.2. Route Reply (RREP)

Quando o destino (ou um nó intermediário com uma rota válida) gera uma Resposta de Rota:

1. Um `MeshPacket` com `Type = RouteReply` é criado, com `SourceUhid` definido para
   o nó de destino e `DestinationUhid` para o originador do RREQ.
2. **REQUISITO DE SEGURANÇA:** O RREP DEVE ser assinado pela chave de identidade Ed25519
   do nó de destino. A assinatura cobre os dados assináveis padrão (Seção 2.3). Isso
   impede o envenenamento de rotas por nós intermediários maliciosos.
3. O RREP é enviado por unicast de volta pela rota reversa instalada durante a propagação do RREQ.
4. Cada nó intermediário que encaminha o RREP:
   a. Verifica a assinatura do RREP em relação à chave pública da origem declarada
      (se conhecida). Se a verificação falhar, o RREP é descartado e um aviso é registrado.
   b. Instala uma **rota direta** para a origem do RREP (o nó de destino) com o
      remetente do RREP como próximo salto.
   c. Decrementa o TTL e encaminha em direção ao originador do RREQ.
5. Quando o RREP alcança o originador, a solicitação de rota pendente (rastreada
   via `TaskCompletionSource`) é resolvida com a rota instalada.

### 3.3. Manutenção de Rotas

- **Expiração baseada em TTL:** Cada entrada de rota carrega um timestamp `ExpiresAt`
  definido como `now + 300 segundos` (`RouteExpirySeconds`). As rotas não são
  atualizadas implicitamente; elas devem ser reestabelecidas via um novo ciclo
  RREQ/RREP após a expiração.
- **Poda periódica:** O serviço de protocolo executa um heartbeat periódico (padrão
  a cada 300 segundos). Durante cada ciclo, ele remove rotas expiradas tanto do
  `ConcurrentDictionary` em memória quanto do armazenamento de backup em SQLite.
- **Poda de dedup RREQ:** O conjunto de IDs de RREQ vistos é limpo quando excede
  `DeduplicationCacheSize` (padrão 10.000) entradas.

### 3.4. Qualidade de Rota e QoS

Cada `RouteEntry` carrega um `QualityScore` no intervalo [0, 100], inicializado em
50 para rotas recém-descobertas. A pontuação considera:

- **Contagem de saltos:** Menos saltos geralmente indica uma rota mais rápida.
- **Latência:** Tempo de ida e volta medido quando disponível.
- **Confiabilidade do par:** A pontuação de confiabilidade do par do próximo salto
  (ver Seção 3.5).

Nós que participam do sistema de incentivo de gorjetas recebem um bônus de QoS em
sua pontuação de qualidade de rota. Isso é uma preferência suave: não-contribuintes
sempre recebem serviço, mas contribuintes consistentes podem ter uma seleção de
rota marginalmente melhor. Os níveis de bônus são:

| Nível   | Limite de consistência | Bônus de QoS |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. Pontuação de Confiabilidade do Par

A cada par conhecido é atribuída uma pontuação de confiabilidade no intervalo [0, 100],
inicializada em 50 (`DefaultReliabilityScore`). A pontuação é ajustada com base no
comportamento observado:

| Evento               | Delta |
|----------------------|-------|
| Relay bem-sucedido   | +2    |
| Relay falhado        | -5    |
| Relay SOS            | +5    |
| Chunk servido        | +1    |
| Falha ao servir chunk| -10   |

As pontuações de confiabilidade são persistidas no SQLite e carregadas em memória na
inicialização. A pontuação influencia a seleção de rotas: rotas através de pares mais
confiáveis são preferidas.

---

## 4. Troca de Chaves

> Reconciliado em 2026-05-05 com a implementação de referência em C# em
> `src/AetherMesh.Security/Services/SignalProtocolService.cs` e o corpus de
> fixtures entre linguagens em `fixtures/signal/`. A referência em C#
> inclui X3DH completo + Double Ratchet (Signal §3 + §5) sobre X25519.
> Go, Python, TypeScript, Rust, Swift e Kotlin foram portados para o mesmo
> envelope e são byte-equivalentes no nível de fixture X3DH e KDF_RK.
> C inclui apenas as primitivas X25519 + KDF_RK + ratchet simétrico —
> suficiente para o verificador de fixture, sem maquinaria completa de
> sessão ainda. Onde esta seção discordar do código, o código é autoritativo;
> registre um problema em `OPEN_ISSUES.md`.

O Aether implementa **X3DH** (Extended Triple Diffie-Hellman, Signal §3) para
estabelecimento de sessão assíncrono, seguido imediatamente pelo **Signal Double
Ratchet** (Signal §5) para sigilo de encaminhamento contínuo e segurança
pós-comprometimento. Todo o criptografia de sessão opera sobre Curve25519:
**X25519** (RFC 7748) para ECDH e **Ed25519** (RFC 8032) para assinatura.

### 4.1. Chaves de Identidade

Cada nó gera **dois** pares de chaves de longo prazo na primeira inicialização
(sem XEdDSA; o arranjo de chave dual mais simples é o que toda implementação
inclui):

- **Par de chaves Ed25519** — seed de 32 bytes (privado), chave pública de 32 bytes.
  Usado para assinatura de pacotes (§2.4), `SignedPreKeySignature` (§4.3),
  autenticação RREP (§3.2) e assinaturas de gorjeta.
- **Par de chaves X25519** — chaves privada e pública raw de 32 bytes. Usadas
  para as quatro operações DH do X3DH (§4.4).

Referência: `SignalProtocolService.InitializeIdentityKeys`. As chaves privadas
ficam apenas no dispositivo; as chaves públicas são publicadas em `PreKeyBundle`.

Uma janela de migração P-256 → Ed25519 de 30 dias é respeitada para *verificação
de assinatura* apenas em pacotes de entrada — ver §7.5. Os bundles de pré-chave
são apenas X25519 no wire.

### 4.2. Escolha de Curva

X3DH e o Double Ratchet usam **X25519** exclusivamente. P-256 *não* é usado no
estabelecimento de sessão por nenhuma implementação atual. Um rascunho anterior
desta especificação descrevia P-256 ECDH; esse texto é anterior ao port de toda
a família para X25519 em 2026-05-05 e não é mais preciso.

### 4.3. Bundle de Pré-Chave

Um bundle de pré-chave é publicado para que um iniciador possa estabelecer uma
sessão sem que o respondente esteja online (Signal §3.4):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

Referência: `AetherMesh.Security.Models.PreKeyBundle`. O contrato de forma de wire é
o mesmo em todas as 8 linguagens.

**Pool de pré-chave de uso único (OPK).** Cada respondente mantém um pool de
`OpkPoolSize` (padrão 100, espelhando as orientações publicadas do Signal) X25519
OPKs. A geração de bundle retira o próximo ID não utilizado de uma fila FIFO e
depois reabastece o pool até seu tamanho alvo. Cada OPK é consumida exatamente
uma vez: o respondente remove e zera a metade privada na primeira mensagem PreKey
que referencia seu ID. Iniciadores concorrentes competindo pelo mesmo ID de OPK
verão exatamente um `EstablishResponderSession` ter sucesso sob `_preKeyLock`;
o perdedor lança `CryptographicException`.

Referência: `SignalProtocolService.TopUpOpkPoolNoLock` (linhas 494–518),
`SignalProtocolService.EstablishResponderSession` (linhas 636–718). A semântica
do pool é exercida por `tests/AetherMesh.Core.Tests/PreKeyPoolTests.cs`.

**Rotação de signed pre-key (SPK).** A SPK é gerada preguiçosamente na primeira
chamada de bundle e reutilizada nas chamadas subsequentes para que iniciadores
concorrentes buscando bundles antes do X3DH executar não invalidem os bundles uns
dos outros. A rotação periódica de SPK (Signal §3.3 recomenda semanal) é uma
operação explícita, não um efeito colateral da geração de bundle.

Os IDs de pré-chave são obtidos de `RandomNumberGenerator.GetInt32(1, int.MaxValue)`
com tentativa explícita de colisão (até 64 tentativas antes de lançar).

### 4.4. Estabelecimento de Sessão (X3DH)

O X3DH completo (Signal §3.3) é executado no lado do iniciador. Quatro operações
DH são computadas sobre X25519:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

onde `IK_A` / `IK_B` são as chaves de identidade X25519, `EK_A` é uma efêmera
X25519 recém-criada apenas para esta sessão, `SPK_B` é a signed pre-key do
respondente e `OPK_B` é a pré-chave de uso único do respondente. A chave raiz
inicial é:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

A constante `info` `aether-x3dh-root-v1` é idêntica em todas as implementações
e está fixada em `fixtures/signal/expected/x3dh_basic.json` (campo `root_key_hex`).

Referência: `SignalProtocolService.ProcessPreKeyBundleAsync` (linhas 554–626).
Caminho de verificação: caso `x3dh_basic` em `fixtures/signal/inputs.json` →
`fixtures/signal/expected/x3dh_basic.json`.

**Verificação do bundle.** Antes de qualquer DH ser executado, o iniciador
verifica `SignedPreKeySignature` em relação a `IdentityKey` usando Ed25519. Uma
falha na verificação lança `CryptographicException` e o bundle é descartado.
Os tamanhos de chave pública são validados em relação a `X25519Service.PublicKeySize`
(32); bundles malformados são rejeitados.

**Primagem da sessão.** No final de `ProcessPreKeyBundleAsync`, um `SignalSession`
é criado com:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — integração canônica Signal X3DH ↔
  Double-Ratchet: a efêmera X3DH do iniciador se torna seu primeiro par de chaves
  DH-ratchet (`DHs`).
- `RemoteEphemeralPub = SPK_B` — a signed pre-key do respondente é tratada como
  a chave de ratchet do par inicial (`DHr`).
- `SendChainKey = null`, `RecvChainKey = null` — ambas as chaves de cadeia são
  derivadas preguiçosamente no primeiro envio / primeiro recebimento DH-ratchet.
- `PendingPreKeyMessage = true` — sinaliza que a próxima chamada de saída a
  `EncryptAsync` DEVE emitir uma mensagem PreKey (`MessageType=1`).

Todas as saídas DH e o segredo compartilhado concatenado são zerados no bloco
`finally` via `CryptographicOperations.ZeroMemory`.

**Recusa em enviar de forma insegura.** Se `EncryptAsync` for chamado para um par
sem sessão, a chamada lança `InvalidOperationException`. Não há caminho de
fallback derivado de UHID. Os hosts devem enfileirar a mensagem
(ver `MessagingService` + `SignalMessageEnvelopeCipher`) e tentar novamente após
a conclusão do estabelecimento de sessão.

### 4.5. Double Ratchet (Signal §5)

Cada lado mantém um par de chaves X25519 ratchet rotativo (`DHs`) e uma cópia
da última chave pública de ratchet vista do par (`DHr`). Em cada mensagem, o
remetente publica seu `DHs` público atual; sempre que o receptor observa um novo
`DHr`, ele executa um **passo DH-ratchet** que reemite a chave da cadeia via
`KDF_RK(RK, DH(myDHs, newDHr))` — re-derivando tanto a chave raiz quanto
uma chave de cadeia fresca.

#### 4.5.1. KDF_RK

`KDF_RK` é HKDF-SHA256 sobre um bloco de 64 bytes, dividido 32+32 na nova
chave raiz e na nova chave de cadeia:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

Referência: `SignalProtocolService.KdfRk` (linhas 857–868). Fixado em
`fixtures/signal/inputs.json` caso `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Ratchet Simétrico

Por Signal §5.1, as chaves de mensagem e de cadeia são derivadas de uma chave
de cadeia usando HMAC-SHA256 com separação de domínio de byte único:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Referência: `SignalProtocolService.RatchetChainKey` (linhas 876–881).
Fixado pelos casos `ratchet_step_basic` e `ratchet_step_three_iterations`
em `fixtures/signal/inputs.json`.

O rascunho anterior desta especificação descrevia `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` e um `chain_key` separado
avançado via `HMAC(chain_key, 0x01)`. Isso não era Signal e nunca foi
implementado; foi substituído pela divisão canônica 0x01/0x02.

#### 4.5.3. Passo DH-Ratchet ao Receber

Acionado quando o `SenderEphemeralKeyX25519` da mensagem de entrada difere
do `RemoteEphemeralPub` em cache (comparação em tempo constante).

1. Salvar o contador de saída como `PreviousChainCount` (Signal §5: PN) para
   que o par possa computar chaves ignoradas através do limite.
2. Resetar `SendCounter` e `RecvCounter` para 0; instalar o novo
   `RemoteEphemeralPub`.
3. Derivar nova cadeia de recebimento: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Zerar o `myDHs` privado antigo; gerar um novo par de chaves X25519.
5. Derivar nova cadeia de envio: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Referência: `SignalProtocolService.DhRatchetReceive` (linhas 726–772).

#### 4.5.4. Derivação Lazy da Cadeia de Envio

O primeiro envio do iniciador executa um **meio-passo** em vez de um
DH-ratchet completo — o X3DH já posicionou `DHs` e `DHr`, portanto apenas
a cadeia de envio precisa ser derivada:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` *não* é rotacionado aqui. Ele é rotacionado apenas em um passo DH-ratchet
verdadeiro no lado do recebimento.

Referência: `SignalProtocolService.DhRatchetSendOnly` (linhas 780–796).

#### 4.5.5. Chaves de Mensagem Ignoradas

Quando mensagens chegam fora de ordem, a chave de mensagem de cada contador
ignorado é armazenada em cache em `SkippedMessageKeys`, indexada por
`(Hex(remoteEphPub):counter)`. A vinculação com a chave pública remota é
essencial — mensagens fora de ordem de uma cadeia anterior (diferente `DHr`)
ainda podem chegar após um passo DH-ratchet e precisam de seu próprio conjunto
de chaves por cadeia.

Limites:

- Ignorar mais de `MaxSkippedKeys` (1000) entradas em uma única lacuna
  lança `CryptographicException` e força o reestabelecimento da sessão.
- Cruzando um limite de DH-ratchet, o receptor primeiro ignora até
  `PreviousChainCount` chaves na cadeia *antiga*, então executa o passo
  DH-ratchet antes de derivar chaves na nova cadeia.

Referência: `SignalProtocolService.SkipMessageKeys` (linhas 804–830) e
o loop de skip no decrypt (linhas 366–388).

### 4.6. Formato de Payload Criptografado

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

Referência: `AetherMesh.Security.Models.EncryptedPayload` (linhas 55–66 de
`SecurityModels.cs`). O campo `InitiatorEphemeralKeyX25519` é um alias de
compatibilidade retroativa para o envelope de wire pré-Double-Ratchet e é
igual a `SenderEphemeralKeyX25519` em mensagens PreKey; novos consumidores
devem ignorá-lo.

Parâmetros AES-GCM: chave de 256 bits, nonce de 96 bits (`AesNonceSize = 12`),
tag de 128 bits (`AesTagSize = 16`), tag concatenada ao ciphertext.
As chaves de mensagem são zeradas em blocos `finally` imediatamente após o
encrypt/decrypt AES-GCM.

### 4.7. Status por Linguagem

| Linguagem   | X3DH (4 DHs) | Double Ratchet | Pool OPK       | Verificado por fixtures |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | completo     | completo (§5)  | pool, padrão 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | apenas primitivas — `aethermesh_x25519_*`, `aethermesh_signal_kdf_rk` | não implementado | — | apenas kdf_rk_basic |

Todas as 7 linguagens capazes de sessão (C# + Go + TypeScript + Python + Kotlin + Swift + Rust)
incluem o pool OPK FIFO de 100 chaves com reabastecimento lazy e consumo protegido por lock,
correspondendo ao contrato de referência em C#. C inclui apenas primitivas; a maquinaria
completa de sessão é monitorada em `OPEN_ISSUES.md` item 11.

---

## 5. Requisitos da Camada de Transporte

O Aether é agnóstico quanto ao transporte. Qualquer canal de comunicação física que
satisfaça o contrato de `ITransportService` pode participar da malha.

### 5.1. Contrato da Interface ITransportService

Toda implementação de transporte DEVE expor o seguinte:

**Propriedades:**

| Propriedade        | Tipo   | Descrição |
|--------------------|--------|-------------|
| `Name`             | string | Identificador legível por humanos (ex.: "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Se o transporte está atualmente utilizável neste dispositivo |
| `MaxBandwidthBps`  | int64  | Taxa de transferência máxima em bytes por segundo |
| `MaxRangeMeters`   | int32  | Alcance máximo de comunicação em metros |
| `PowerCostRelative`| int32  | Consumo relativo de energia (1 = baixo, 10 = alto) |
| `MaxConcurrentPeers` | int32 | Máximo de conexões simultâneas com pares |

**Métodos:**

| Método         | Assinatura | Descrição |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Envia um array de bytes para um par específico. Retorna true em caso de sucesso. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Envia um stream para um par (para grandes transferências, voz, vídeo). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Verifica se uma conexão está ativa com um par. |

**Eventos:**

| Evento         | Assinatura | Descrição |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Disparado quando dados chegam de um par. |

### 5.2. Algoritmo de Seleção de Transporte

O `TransportManager` seleciona o transporte ideal para cada pacote com base em:

1. **Disponibilidade:** Apenas transportes onde `IsAvailable == true` são considerados.
2. **Tamanho do payload:** Se o tamanho do payload estiver em ou abaixo de `BleMaxPayloadBytes`
   (1.024 bytes), o BLE é preferido por eficiência energética. Payloads maiores preferem
   Wi-Fi Direct.
3. **Ponderação de custo de energia:** Entre os transportes disponíveis, valores
   menores de `PowerCostRelative` são preferidos para tráfego de rotina. Pacotes de
   alta prioridade (SOS, voz) podem substituir essa preferência.
4. **Conectividade com o par:** Se um transporte já tem uma conexão ativa com o par
   alvo (`IsConnected` retorna true), ele é preferido para evitar a sobrecarga de
   configuração de conexão.
5. **Fallback:** Se nenhum transporte local puder alcançar o alvo, o pacote é
   enfileirado para relay via servidor pela AetherMeshAPI.

### 5.3. Transportes de Referência

| Transporte   | Largura de banda máx. | Alcance máx. | Custo de energia | Máx. de pares | Observações |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | Descoberta primária + pacotes pequenos |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | Grandes transferências, streaming, voz |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon, alta taxa de transferência |

**Limite de payload BLE:** Pacotes excedendo 1.024 bytes (`BleMaxPayloadBytes`) são
automaticamente roteados para Wi-Fi Direct ou NearLink. O BLE é usado para anúncios
de descoberta, pequenos pacotes de controle (RREQ/RREP, beacons de presença) e
mensagens de baixa largura de banda.

O timeout de conexão **Wi-Fi Direct** é de 10.000 ms (`WifiDirectTimeoutMs`) com um
máximo de 8 pares simultâneos (`MaxWifiDirectPeers`).

---

## 6. Protocolo de Descoberta

### 6.1. Publicidade BLE

Os nós Aether se descobrem principalmente por meio de publicidade BLE. Para evitar
rastreamento persistente via identificadores estáticos, o protocolo emprega dois
mecanismos de privacidade: UUIDs de Serviço rotativos e Identity Resolving Keys.

**Ciclo de publicidade:** 2 segundos de varredura ativa, 8 segundos desligado
(`BleScanOnMs`/`BleScanOffMs`). O intervalo de publicidade é de 1.000 ms
(`BleAdvertiseIntervalMs`). Um jitter aleatório de 0-2.000 ms (`BleScanJitterMaxMs`)
é adicionado ao intervalo de varredura para prevenir a detecção de padrões de temporização.

**Timeout de par:** Um par não redescoberto em 30 segundos é considerado perdido
(evento `PeerLost`).

### 6.2. UUID de Serviço Rotativo

Para evitar fingerprinting de longo prazo via BLE, o UUID de Serviço usado nos
anúncios rotaciona a cada 15 minutos (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

A `rotation_key` é uma chave de 32 bytes gerada uma vez por nó e armazenada em
armazenamento seguro. Todos os nós Aether que compartilham a mesma rotation_key
derivarão o mesmo UUID para uma dada janela de tempo, permitindo a descoberta
mútua sem revelar um identificador permanente.

Um UUID de fallback estático (`A3E7-1001-0001-0000-000000000000`) é mantido por
90 dias durante a transição do esquema não-rotativo.

### 6.3. Identity Resolving Key (IRK)

Cada nó gera uma Identity Resolving Key (IRK) de 128 bits armazenada em
armazenamento seguro. A IRK é compartilhada com pares confiáveis durante a
troca de chaves.

**Geração de Resolvable Private Address (RPA):**

1. Computar `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 bytes).
2. Definir os dois bits mais significativos de `prand[0]` como `01` (flag RPA
   conforme especificação BLE).
3. Computar `hash = AES-128-ECB(IRK, pad(prand))` onde `prand` ocupa os bytes
   13-15 de uma entrada de 16 bytes preenchida com zeros.
4. Construir RPA: `hash[0..2] || prand[0..2]` (6 bytes no total).

**Resolução de RPA:** Um nó que possui a IRK de um par pode verificar se um RPA
observado pertence a esse par recomputando o hash a partir do componente `prand`
do RPA. O tempo de resolução é aproximadamente O(N) onde N é o número de IRKs
conhecidas, com benchmark de ~0,1ms para 100 pares.

O RPA rotaciona no mesmo ciclo de 15 minutos que o UUID de Serviço.

### 6.4. Proximidade Baseada em Geohash

Os nós codificam opcionalmente sua localização como um geohash. Para privacidade,
o geohash é truncado para 4 caracteres, fornecendo uma resolução de aproximadamente
39km x 20km. Essa granularidade é suficiente para:

- Descoberta de canal baseada em proximidade
- Roteamento epidêmico DTN (replicar em direção à área do último geohash conhecido
  do destinatário)
- Contexto geográfico de alertas SOS

O geohash de precisão total nunca é transmitido pela malha. Apenas a forma truncada
é compartilhada, e somente quando o nível de privacidade do nó permite
(`PrivacyLevel.Full` ou `PrivacyLevel.Partial`).

---

## 7. Modelo de Segurança

### 7.1. Modelo de Ameaças

O Aether assume as seguintes capacidades do adversário:

- **Escuta passiva:** O adversário pode observar todos os anúncios BLE e o tráfego
  de malha dentro do alcance de rádio.
- **Injeção ativa:** O adversário pode injetar, modificar ou reproduzir pacotes.
- **Ataque Sybil:** O adversário pode criar múltiplas identidades de nó falsas.
- **Negação de serviço seletiva:** O adversário pode descartar seletivamente
  pacotes como nó relay.

### 7.2. O que É Protegido

| Propriedade | Nível de proteção | Mecanismo |
|----------|-----------------|-----------|
| Conteúdo da mensagem | Confidencialidade total | AES-256-GCM com chaves por mensagem (Seção 4.5) |
| Identidade do remetente | Parcial | UHID visível nos cabeçalhos do pacote; endereço BLE rotaciona (Seção 6) |
| Identidade do receptor | Parcial | UHID de destino visível em pacotes roteados; pacotes broadcast têm destino vazio |
| Metadados de roteamento | Mínima | Nós intermediários veem UHIDs de origem/destino e TTL |
| Ordenação de mensagens | Protegida | Contadores no ratchet simétrico impedem reordenação |
| Integridade da mensagem | Total | Assinatura Ed25519 em todos os pacotes (v2) |

### 7.3. Resistência a Ataques

**Ataques de replay:**
Cada pacote carrega um nonce de 8 bytes criptograficamente aleatório e um timestamp
de precisão de milissegundos. Os nós relay mantêm um cache de deduplicação de pares
`(SenderUhid, NonceValue)` com TTL de 5 minutos (`MaxPacketAgeSeconds = 300`). Um
pacote com nonce duplicado do mesmo remetente é descartado. Pacotes com timestamps
mais antigos que 5 minutos são rejeitados independentemente do nonce.

O cache de dedup de nonce é limpo a cada 60 segundos. Entradas expiradas (mais
antigas que 5 minutos) são removidas.

**Man-in-the-middle (MITM):**
- Os pacotes Route Reply DEVEM carregar uma assinatura Ed25519 válida do nó de
  destino declarado. Os nós intermediários não podem forjar RREPs porque não possuem
  a chave privada do destino.
- Os bundles de pré-chave incluem uma `SignedPreKeySignature` (Ed25519) sobre a
  `SignedPreKey`, vinculando a chave ECDH efêmera à identidade de longo prazo.
- O estabelecimento de sessão (Seção 4.4) vincula criptograficamente a sessão às
  identidades de ambas as partes por meio da etapa de verificação de pré-chave.

**Ataques Sybil:**
- A pontuação de confiabilidade de cada nó começa em 50 e é ajustada com base no
  comportamento observado (Seção 3.5). Nós Sybil recém-criados não têm reputação acumulada.
- Nós com pontuações de confiabilidade baixas (próximas a 0) são despriorizados na
  seleção de rotas.
- O algoritmo de roteamento epidêmico DTN usa proximidade de geohash e histórico de
  sucesso de relay para selecionar alvos de replicação, dificultando que nós Sybil
  atraiam tráfego sem contribuições genuínas de relay.

**Ataques de flooding:**
- O TTL é decrementado em cada salto e pacotes com TTL = 0 são descartados. O TTL
  padrão de 7 limita o raio de explosão de qualquer broadcast.
- A deduplicação de RREQ por ID de pacote previne amplificação por tempestades de
  broadcast. O cache de dedup é limpo quando excede `DeduplicationCacheSize`
  (padrão 10.000) entradas.
- Os broadcasts SOS têm limite de taxa de 3 por hora por nó (Seção 8).

### 7.4. Zeragem de Chaves

Todo material criptográfico intermediário é zerado imediatamente após o uso:

- `sharedSecret` do acordo de chaves ECDH: zerado após a derivação HKDF.
- `messageKey` do ratchet de cadeia: zerado após encrypt/decrypt AES-GCM.
- `skippedKey` do deciframento fora de ordem: zerado após uso e removido do mapa.
- `RootKey`, `SendChainKey`, `RecvChainKey` derivados: zerados do contexto de
  estabelecimento (a sessão retém suas próprias cópias).

A zeragem usa `CryptographicOperations.ZeroMemory`, que é garantida para não ser
otimizada pelo compilador.

### 7.5. Migração de P-256 para Ed25519

O protocolo suporta uma janela de transição de 30 dias de chaves de identidade
ECDSA P-256 (Versão de Protocolo 1) para Ed25519 (Versão de Protocolo 2):

1. Pacotes da Versão de Protocolo 1 (não assinados) são aceitos durante o período
   de transição.
2. A verificação de assinatura primeiro tenta Ed25519. Se a chave pública tiver
   mais de 32 bytes (indicando uma chave P-256 codificada em DER), ela recai na
   verificação ECDSA P-256.
3. Após a janela de 30 dias, os pacotes da Versão de Protocolo 1 são rejeitados.
4. Nós que não migraram devem reinicializar com uma nova identidade Ed25519.

### 7.6. Consciência de Jurisdição

O protocolo define camadas de jurisdição para lidar com requisitos legais variados
em torno de criptografia e rede em malha:

| Camada | Comportamento | Jurisdições de exemplo |
|------|----------|-----------------------|
| 1    | Operar livremente | África do Sul, Quênia, Gana |
| 2    | Operação modificada | Nigéria, Índia, UE, EUA, Reino Unido |
| 3    | Apenas malha (alto risco) | China, Rússia, Irã, EAU, Mianmar |
| 4    | Desconhecido (padrão apenas malha) | Todos os demais |

A seleção de camada afeta a disponibilidade de funcionalidades (ex.: as funcionalidades
de gorjeta/financeiras podem ser desativadas na Camada 3), mas não enfraquece a
criptografia. A criptografia de ponta a ponta é sempre aplicada independentemente
da jurisdição.

---

## 8. Broadcast SOS

O mecanismo SOS é um flood de emergência de caminho duplo projetado para situações
em que um usuário está em perigo e precisa alcançar pares de malha próximos e/ou
a internet simultaneamente.

### 8.1. Parâmetros de Broadcast

| Parâmetro  | Valor | Descrição |
|-----------|-------|-------------|
| TTL       | 15    | O dobro do padrão normal (7), garantindo maior propagação |
| Priority  | 999   | Prioridade máxima; preempta todo o outro tráfego nas filas de relay |
| Limite de taxa | 3/hora | Limite por nó para prevenir abuso |
| Destination | vazio | Broadcast para todos os pares (sem destino específico) |

### 8.2. Algoritmo de Flood

1. O originador constrói um pacote SOS com `Type = SosBroadcast`, `TTL = 15`,
   `Priority = 999` e um `DestinationUhid` vazio.
2. O payload é codificado em JSON e contém:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **Despacho de caminho duplo:** O SOS é enviado simultaneamente via:
   - **Flood de malha:** Broadcast para todos os pares conectados via todos os
     transportes disponíveis.
   - **Chamada de API:** Enviado para a AetherMeshAPI para distribuição no lado do
     servidor e ponte para a PanikAPI (despacho por SMS/email).
4. Ambos os caminhos são fire-and-forget em relação ao outro. Se a chamada de API
   falhar, o flood de malha prossegue de forma independente.

### 8.3. Comportamento de Relay

Quando um nó recebe um pacote SOS:

1. Verificar a deduplicação pelo `Id` do pacote. Se já foi visto, descartar silenciosamente.
2. Deserializar o payload e disparar o evento `SosReceived` para a UI local.
3. Adicionar o alerta à lista de alertas ativos.
4. Se `TTL > 1`, decrementar TTL e **rebroadcastar para TODOS os pares**
   independentemente do estado da tabela de roteamento. Os pacotes SOS contornam
   o roteamento normal — eles fazem flood incondicionalmente.

### 8.4. Limite de Taxa

Cada nó mantém uma janela deslizante de timestamps de broadcast recentes. Antes
de iniciar um novo SOS:

1. Remover entradas mais antigas que 1 hora da fila.
2. Se a fila contiver 3 ou mais entradas (`MaxSosBroadcastsPerHour`), o broadcast
   é rejeitado.
3. Após o despacho bem-sucedido, o timestamp atual é enfileirado.

O limite de taxa aplica-se apenas a broadcasts SOS de origem, não ao relay.

### 8.5. Ponte SOS-PanikAPI

Os broadcasts SOS recebidos via malha podem ser encaminhados para a PanikAPI para
resposta de emergência tradicional (SMS para contatos, alertas por email). De forma
inversa, as sessões de emergência da PanikAPI podem ser transmitidas em broadcast
para a malha para conscientização da comunidade. A prevenção de loop é alcançada
marcando a origem (`direct` vs `mesh_forward`) e um flag `internet_forwarded` nos
broadcasts de malha.

---

## 9. DTN Store-and-Forward

O subsistema de Redes Tolerantes a Atrasos (DTN) permite a entrega de mensagens
quando não existe nenhum caminho de ponta a ponta entre o remetente e o destinatário.
Os bundles são armazenados em nós intermediários e encaminhados oportunisticamente
conforme a conectividade muda.

### 9.1. Formato do Bundle

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. Ciclo de Vida do Bundle

1. **Criação:** O remetente cria um bundle com um payload criptografado (criptografado
   via a sessão Signal com o destinatário). `Status = Pending`, `CopyCount = 1`.
2. **Tentativa de entrega imediata:** O remetente primeiro tenta o roteamento de malha
   direto (RREQ/RREP). Se uma rota existir, o bundle é entregue imediatamente e
   `Status` transita para `Delivered`.
3. **Tentativa de relay via servidor:** Se o roteamento de malha falhar, o remetente
   tenta retransmitir pela AetherMeshAPI. Se o servidor puder alcançar o destinatário
   (ou enfileirar a mensagem), a entrega é bem-sucedida.
4. **Store-and-forward:** Se ambas as tentativas de malha e relay via servidor
   falharem, o bundle permanece no armazenamento local (status `Pending`) aguardando
   o próximo scan de entrega.

### 9.3. Scan de Entrega

Um scan periódico é executado a cada 60 segundos (`DtnScanIntervalSeconds`):

1. Carregar todos os bundles pendentes do SQLite (fonte da verdade).
2. Para cada bundle pendente:
   a. Tentar rota de malha para o destinatário.
   b. Tentar relay via servidor.
   c. Se ambos falharem e `CopyCount < MaxCopies`, tentar replicação epidêmica
      (Seção 9.4).
3. Remover bundles expirados (`ExpiresAt <= now`).

### 9.4. Roteamento Epidêmico

Quando a entrega direta e o relay via servidor ambos falham, os bundles são
replicados para pares próximos usando roteamento epidêmico:

1. O `EpidemicRoutingService` seleciona alvos de replicação da lista de pares atual.
2. A seleção de alvos considera:
   - **Proximidade de geohash:** Pares cujo geohash está mais próximo do último
     geohash conhecido do destinatário são preferidos.
   - **Histórico de relay:** Pares com pontuações de confiabilidade mais altas
     são preferidos.
   - **Orçamento de cópias:** A replicação para quando `CopyCount >= MaxCopies`
     (padrão: 3).
3. Cada replicação envia um pacote `DtnBundle` para o par selecionado.
4. Ao receber, o serviço DTN do par invoca `AcceptCustodyAsync`.

### 9.5. Transferência de Custódia

Quando um nó recebe um bundle DTN destinado a outro nó:

1. **Verificação de capacidade:** O nó verifica sua contagem atual de bundles em
   relação a `DtnMaxBundlesPerNode` (50). Se estiver na capacidade máxima, a
   custódia é rejeitada.
2. **Aceitar:** O status do bundle é definido como `InCustody`, a contagem de saltos
   é incrementada e o bundle é persistido no SQLite.
3. **Registro de custódia:** Um `CustodyRecord` é criado documentando a transferência
   (de, para, timestamp).
4. **Incremento de contagem de cópias:** O `CopyCount` do bundle é incrementado no
   armazenamento persistente.
5. **Confirmação:** Um pacote `DtnCustodyAck` é enviado de volta ao nó transferente
   com `Accepted = true`.
6. O nó aceitante torna-se responsável por tentar a entrega nos scans subsequentes.

### 9.6. Recibo de Entrega

Quando o destinatário pretendido recebe um bundle DTN:

1. O status do bundle é atualizado para `Delivered`.
2. Um `DtnDeliveryReceipt` é enviado de volta ao remetente original via roteamento
   de malha (com fallback de relay via servidor):
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. Ao receber o recibo, o remetente remove o bundle de seu store e dispara o evento
   `BundleDelivered`.
4. O recibo também é sincronizado com a AetherMeshAPI para análises.

### 9.7. Expiração de Bundles

- O TTL padrão de bundle é 72 horas (`DtnBundleTtlHours`).
- Os bundles expirados são limpos durante o scan periódico de entrega.
- Bundles com status `Expired` ou `Delivered` são removidos tanto do cache em
  memória quanto do SQLite.

### 9.8. Limites de Capacidade

| Parâmetro               | Padrão | Descrição |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | Tempo de vida máximo do bundle |
| `DtnMaxCopies`          | 3       | Máximo de cópias por bundle na rede |
| `DtnMaxBundlesPerNode`  | 50      | Máximo de bundles que um único nó carregará |
| `DtnScanIntervalSeconds`| 60      | Frequência do scan de entrega |

---

## 10. Streaming de Vídeo

> **Status em 2026-05-05 — design + scaffolding em C#, sem pipeline de codec em
> produção.** Os tipos de pacote `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) são wire-definidos
> e fazem round-trip via o corpus de fixtures entre linguagens.
> O módulo C# `AetherMesh.Streaming` inclui interfaces, modelos e serviços esqueleto
> (`StreamingService`, `VideoCallService`, `WatchTogetherService`) que conectam
> junções de roteamento/DI e fan-out de segmento unicast — mas nenhum encode/decode
> de vídeo real está vinculado a eles. As outras 7 linguagens têm apenas tipos de
> wire. O documento de design prospectivo em
> `docs/adaptive-secure-streaming-spec.md` é a arquitetura alvo.
> Trate o texto abaixo como a especificação do que esses serviços IMPLEMENTARÃO;
> consulte `OPEN_ISSUES.md` para as lacunas de prontidão para produção.


O Aether suporta três modos de vídeo: chamadas de vídeo peer-to-peer, vídeo em grupo
(participantes ilimitados com topologia dinâmica) e transmissão ao vivo. Todos os
frames de vídeo são criptografados com Signal Protocol e assinados com Ed25519.

### 10.1. Matriz de Capacidade de Transporte

Antes de iniciar uma chamada de vídeo, o originador consulta a camada de transporte
para determinar a melhor conexão disponível com o par. O transporte determina que
qualidade de vídeo é possível:

| Transporte | Suporte a vídeo | Resolução máx. | Codec recomendado | Taxa máx. | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | Não (apenas áudio) | — | — | 64 Kbps | Apenas pacotes de sync |
| NearLink | Leve | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Total | 1080p | H.264 | 3000 Kbps | Todos os modos |
| Internet | Total | 720p | H.264 | 1500 Kbps | Todos os modos |
| CircleLink | Não (apenas áudio) | — | — | 64 Kbps | Apenas pacotes de sync |

Se o único transporte disponível for BLE ou CircleLink, o serviço de chamada de
vídeo rebaixa automaticamente para uma chamada de voz.

### 10.2. Codecs de Vídeo

| Valor do enum | Codec | Caso de uso |
|------------|-------|----------|
| 0 | H.264 | Padrão. Amplamente suportado, boa compressão. |
| 1 | H.265 | Melhor compressão. Usado no NearLink (largura de banda limitada). |
| 2 | VP8 | Alternativa sem royalties. |

### 10.3. Resoluções de Vídeo

| Valor do enum | Resolução | Taxa típica |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. Fluxo de Chamada de Vídeo P2P

1. **Verificação de capacidade**: O originador consulta `GetVideoCapabilityAsync(peerUhid)`
   para determinar o melhor transporte, resolução máxima e codec recomendado.
2. **Offer**: O originador envia um pacote `VideoSignaling` (tipo 28) com
   `SignalType = Offer`, incluindo codec preferido, resolução máxima e taxa máxima.
3. **Answer/Reject**: O receptor responde com `SignalType = Answer` (negociando
   o codec para o menor denominador comum) ou `SignalType = Reject`.
4. **Chamada ativa**: Ambos os nós trocam pacotes `VideoCall` (tipo 27) contendo
   unidades NAL H.264/H.265/VP8. Cada frame inclui um número de sequência para
   ordenação do jitter buffer e um flag de keyframe.
5. **Compartilhamento de tela**: Qualquer parte pode ativar/desativar o compartilhamento
   de tela. `VideoSignaling` com `SignalType = ScreenShareStart/Stop` notifica o par.
   Os frames de compartilhamento de tela usam `PacketType.ScreenShare` (tipo 32),
   mas o mesmo pipeline de processamento.
6. **Encerrar chamada**: Qualquer parte envia `VideoSignaling` com `SignalType = Bye`.

Todos os payloads de sinalização e frame são criptografados com Signal Protocol
(sessão X3DH). O payload criptografado é serializado como `EncryptedPayload` codificado
em JSON dentro do campo `MeshPacket.Payload`.

### 10.5. Máquina de Estados de Chamada de Vídeo

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

Estados: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. Vídeo em Grupo

As sessões de vídeo em grupo suportam participantes ilimitados. A topologia é
selecionada dinamicamente com base na contagem de participantes:

- **FullMesh** (2-3 participantes): Cada participante envia um stream para todos
  os outros participantes. Simples, baixa latência.
- **SFU** (4+ participantes, limite: `SfuThresholdParticipants = 4`): Um nó é
  eleito como relay SFU. Cada participante envia um stream para o relay, que o
  distribui para todos os outros. O nó relay recebe gorjetas via a camada de incentivo.

As mudanças de topologia são automáticas: quando o 4º participante entra, a sessão
transita de FullMesh para SFU. Quando os participantes saem e a contagem cai abaixo
de 4, ela transita de volta.

Os frames de vídeo em grupo usam `PacketType.VideoFrame` (tipo 31). No modo SFU,
os frames são enviados para o UHID do nó relay, que os rebroadcasta.

### 10.7. Jitter Buffer

O jitter buffer de vídeo opera independentemente do jitter buffer de voz (que
lida com frames Opus de 20ms):

- **Intervalo**: mínimo de 60ms, máximo de 500ms.
- **Profundidade adaptativa**: Rastreia o jitter entre frames via Média Móvel
  Exponencial (EMA). Profundidade do buffer = 2× estimativa de jitter, limitada
  a [60, 500] ms.
- **Descarte consciente de keyframe**: Quando o buffer transborda, frames não-keyframe
  (P/B) são descartados primeiro. Os frames I (keyframes) nunca são descartados —
  eles são necessários para a recuperação do decodificador.
- **Tratamento de lacunas**: Quando uma lacuna de sequência é detectada, o buffer
  pula para o próximo keyframe disponível em vez de aguardar indefinidamente.

### 10.8. Tipos de Sinalização de Vídeo

| Valor do enum | Tipo | Descrição |
|------------|------|-------------|
| 0 | Offer | Iniciação de chamada de vídeo com preferência de codec/resolução |
| 1 | Answer | Aceitação de chamada com parâmetros negociados |
| 2 | Reject | Rejeição de chamada |
| 3 | Bye | Encerramento de chamada |
| 4 | Upgrade | Solicitar qualidade maior (ex.: transporte melhorou) |
| 5 | Downgrade | Solicitar qualidade menor (ex.: queda de largura de banda) |
| 6 | ScreenShareStart | O par começou a compartilhar a tela |
| 7 | ScreenShareStop | O par parou de compartilhar a tela |

### 10.9. Modelo de Criptografia

| Modo | Criptografia | Distribuição de chaves |
|------|-----------|-----------------|
| Chamada de vídeo P2P | Signal Protocol por frame | Acordo de chaves X3DH |
| Vídeo em grupo | Chave de canal de grupo (AES-GCM) | Distribuída via Signal Protocol na criação da sessão |
| Compartilhamento de tela | Igual ao modo da chamada pai | Herdada da sessão de chamada de vídeo |

---

## 11. Watch Together

> **Status em 2026-05-05 — design + scaffolding em C#, mesma maturidade que
> §10.** Os tipos de pacote `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) são wire-definidos e
> testados por fixtures. `AetherMesh.Streaming.WatchTogetherService` fornece o
> esqueleto de coordenação (estado de sessão, propagação de comando sync via
> `IMeshSender`, helpers de compensação RTT); a ingestão BitTorrent, o
> liquidamento SDPKT ChipIn e o fetch de chunk de pares não estão implementados
> em nenhuma linguagem. Trate o texto abaixo como o protocolo alvo; o documento
> de design prospectivo em `docs/adaptive-secure-streaming-spec.md` cobre o
> mesmo terreno com mais detalhes.


O Watch Together permite reprodução de mídia sincronizada entre um grupo de pares
de malha. O host tem controle exclusivo sobre a reprodução (play, pause, seek, speed).
Os comandos de sync incluem timestamps de relógio de parede para compensação de RTT.

### 11.1. Modos de Watch

| Valor do enum | Modo | Fluxo de dados | Requisito de transporte |
|------------|------|-----------|----------------------|
| 0 | SharedFile | Apenas pacotes de sync (< 100 bytes cada) | Qualquer (funciona via BLE) |
| 1 | StreamFromHost | Transferência de chunk P2P (reutiliza P2pContentService) | WiFi Direct ou Internet |
| 2 | BitTorrent | Malha + swarm externo via nós gateway | WiFi Direct ou Internet |

### 11.2. Modo SharedFile

Ambos os participantes têm o mesmo arquivo (combinado por hash de conteúdo SHA-256).
Apenas pacotes `WatchSync` são trocados. Este é o modo mais eficiente em largura de
banda e funciona via BLE.

1. O host cria uma sessão de watch com `contentHash` (SHA-256 do arquivo).
2. Os participantes entram e reportam `IsReady = true` quando seu player está carregado.
3. A sessão começa quando TODOS os participantes reportam pronto.
4. O host envia comandos de play/pause/seek/speed como pacotes `WatchSync` (tipo 29).
5. Os receptores aplicam compensação RTT: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. Modo StreamFromHost

Apenas o host tem o arquivo. O host gera um `ContentManifest` (reutilizando o
sistema de conteúdo P2P) e os participantes baixam chunks via a malha.

- A seleção de chunk usa a estratégia `SequentialFromPosition` (não `RarestFirst`):
  prioriza chunks à frente da posição de reprodução atual, depois preenche
  retroativamente para seeding.
- Alvo de buffer: 30 segundos à frente (`WatchTogetherBufferAheadSeconds`).
- Pausa automática: Se o buffer de QUALQUER participante cair abaixo de 10 segundos
  (`WatchTogetherMinBufferSeconds`), a sessão pausa automaticamente todos os
  participantes com um comando sync `BufferUnderrun`. A reprodução retoma quando
  todos os participantes tiverem buffer suficiente (`BufferReady`).
- À medida que os espectadores baixam chunks, eles se tornam seeders para outros
  espectadores (swarming no estilo BitTorrent dentro da malha).

### 11.4. Modo BitTorrent

Um participante compartilha um arquivo `.torrent` ou magnet link no chat de grupo.
O pacote `TorrentMetadata` (tipo 34) distribui as informações do torrent para todos
os participantes da sessão.

**Ponte Malha-para-Swarm:**
- Os nós gateway (nós com internet) baixam peças do swarm BitTorrent externo.
- Os nós gateway re-criptografam as peças baixadas para distribuição na malha e
  fazem seeding para os pares de malha.
- Os pares de malha sem internet recebem peças dos nós gateway e uns dos outros.
- O motor de conteúdo P2P traduz entre o modelo de peça do BitTorrent e o modelo
  de chunk do Aether.

Uma vez que conteúdo suficiente está em buffer, a reprodução watch-together começa
usando o mesmo protocolo de sync que o modo SharedFile.

### 11.5. Máquina de Estados da Sessão de Watch

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

Estados: `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. Tipos de Comando Sync

| Valor do enum | Tipo | Descrição |
|------------|------|-------------|
| 0 | Play | Retomar reprodução na posição especificada |
| 1 | Pause | Pausar na posição especificada |
| 2 | Seek | Pular para a posição especificada |
| 3 | Speed | Alterar velocidade de reprodução |
| 4 | BufferUnderrun | Pausa automática — o buffer de um participante está criticamente baixo |
| 5 | BufferReady | Retomar — todos os participantes têm buffer suficiente |

### 11.7. Compensação RTT

Os comandos sync incluem um campo `WallClockMs` (milissegundos do epoch Unix). Quando
um receptor processa um comando sync:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Para comandos Play e BufferReady: `adjustedPosition = commandPosition + networkDelay`
4. Para comandos Pause e Seek: a posição é aplicada exatamente (sem ajuste necessário
   pois a reprodução está parando/pulando).

Isso garante que todos os participantes estejam sincronizados dentro de metade do RTT da rede.

### 11.8. Reações

Os participantes podem reagir ao conteúdo durante a reprodução:

- **Reações de emoji**: pacote `WatchReaction` (tipo 30) com `Type = Emoji`, carregando
  a string de emoji e a posição da mídia no momento da reação.
- **Comentários de voz**: pacote `WatchReaction` com `Type = VoiceComment`, carregando
  dados de áudio codificados em Opus (máximo de 10 segundos). Os dados de voz são
  incluídos no campo `VoiceData` da reação.

As reações são transmitidas em broadcast para todos os participantes da sessão. Elas
são marcadas com timestamp da posição da mídia, permitindo exibição sincronizada com
a reprodução.

### 11.9. ChipIn — Aquisição de Conteúdo em Grupo

O ChipIn permite que membros do grupo reúnam fundos (em ZAR, liquidados via carteiras
SDPKT pela LedgerAPI) para adquirir coletivamente conteúdo para assistir em grupo.

**Máquina de estados:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

Estados: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**Fluxo:**
1. O iniciador cria um `ChipInPool` com o valor alvo e a descrição do conteúdo.
2. Os participantes contribuem com valores via transações de carteira SDPKT.
3. Quando `CollectedAmount >= TargetAmount`, o estado transita para `Funded`.
4. O sistema adquire o conteúdo (ex.: inicia um download BitTorrent).
5. Uma vez que o conteúdo está disponível, o estado transita para `Acquired` e o
   watch-together pode começar.

Cada contribuição é registrada com um ID de transação SDPKT para trilha de auditoria.

### 11.10. Modelo de Criptografia

| Modo | Criptografia | Distribuição de chaves |
|------|-----------|-----------------|
| Comandos sync de watch | Chave de canal/conversa | Sessão Signal Protocol existente |
| Chunks de conteúdo (StreamFromHost) | Chave de conteúdo por manifest | Distribuída via Signal Protocol |
| Peças BitTorrent | Re-criptografadas na ingestão | O gateway baixa cleartext do swarm, criptografa para a malha |
| Reações de watch | Chave de sessão | Derivada da chave de conversa |

### 11.11. Feature Flags

Todas as funcionalidades de vídeo e watch-together estão bloqueadas por feature flags
(todas desativadas por padrão):

| Flag | Pai | Descrição |
|------|--------|-------------|
| AETHERMESH_VIDEO_CALL | AETHERMESH_VOICE | Chamadas de vídeo P2P e em grupo |
| AETHERMESH_VIDEO_GROUP | AETHERMESH_VIDEO_CALL | Sessões de vídeo com múltiplas partes |
| AETHERMESH_SCREEN_SHARE | AETHERMESH_VIDEO_CALL | Compartilhamento de tela em chamadas de vídeo |
| AETHERMESH_WATCH_TOGETHER | AETHERMESH_CONTENT_P2P | Reprodução de mídia sincronizada |
| AETHERMESH_WATCH_REACTIONS | AETHERMESH_WATCH_TOGETHER | Reações de emoji e voz |
| AETHERMESH_TORRENT_INGEST | AETHERMESH_CONTENT_P2P | Aceitação de arquivos BitTorrent para distribuição na malha |

As feature flags têm dependências de pai: uma flag filha só pode ser habilitada se seu
pai também estiver habilitado. Isso permite rollout progressivo.

---

## Apêndice A: Referência de Constantes

Todas as constantes do protocolo são definidas em `ProtocolConstants` e reproduzidas
aqui para referência:

### Roteamento
| Constante             | Valor  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### Descoberta BLE
| Constante                  | Valor  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherMeshBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Segurança
| Constante                  | Valor  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constante                   | Valor |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constante                  | Valor  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transporte
| Constante                  | Valor   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Heartbeat
| Constante                      | Valor |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### Presença
| Constante                          | Valor |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### Voz
| Constante                  | Valor |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### Streaming
| Constante                    | Valor |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### Vídeo
| Constante                       | Valor |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

---

## Apêndice B: Glossário

| Termo | Definição |
|------|------------|
| **UHID** | Universal Hardware Identifier. Uma string única que identifica um nó de malha, derivada da identidade do dispositivo e das chaves criptográficas. |
| **RREQ** | Route Request. Um pacote de broadcast usado para descobrir um caminho para um nó de destino. |
| **RREP** | Route Reply. Um pacote unicast enviado de volta pela rota reversa estabelecida por um RREQ. |
| **IRK** | Identity Resolving Key. Uma chave de 128 bits usada para gerar e resolver Resolvable Private Addresses BLE. |
| **RPA** | Resolvable Private Address. Um endereço BLE de 6 bytes que rotaciona periodicamente, mas pode ser resolvido por pares que possuem a IRK do remetente. |
| **X3DH** | Extended Triple Diffie-Hellman. Um protocolo de acordo de chaves que permite o estabelecimento de sessão assíncrono. |
| **DTN** | Delay-Tolerant Networking. Um paradigma de store-and-forward para ambientes com conectividade intermitente. |
| **Gateway** | Um nó de malha que possui conectividade à internet e faz a ponte entre o tráfego de malha e serviços baseados em IP. |
| **HKDF** | HMAC-based Key Derivation Function. Usado para derivar múltiplas chaves de um único segredo compartilhado. |
| **Bundle de pré-chave** | Um conjunto de chaves publicadas que permite ao remetente estabelecer uma sessão criptografada sem que o destinatário esteja online. |
| **SFU** | Selective Forwarding Unit. Um nó relay que recebe um stream de vídeo de cada remetente e o distribui para todos os outros participantes, reduzindo a largura de banda de upload por nó. |
| **ChipIn** | Mecanismo de financiamento coletivo onde os participantes reúnem fundos SDPKT para adquirir conteúdo coletivamente para assistir em grupo. |
| **NAL** | Network Abstraction Layer. O formato de encapsulamento usado pelos codecs H.264 e H.265 para encapsular frames de vídeo em pacotes. |

---

## Apêndice C: Referências

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, julho de 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, novembro de 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, novembro de 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, maio de 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," dezembro de 2016 (Resolvable Private Address, Seção 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, novembro de 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
