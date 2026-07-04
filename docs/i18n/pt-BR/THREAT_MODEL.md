# Aether Protocol — Modelo de Ameaças

**Revisado em relação ao HEAD `b8b3d22` (2026-05-06).** Este documento descreve
contra o que a camada de protocolo criptográfico do `aether-protocol` se defende,
o que está explicitamente fora do escopo, e as premissas das quais as alegações de
segurança dependem. É intencionalmente honesto: um atacante que leia este documento
deve ser capaz de enumerar cada ataque que o protocolo **não** interrompe, e não deve
ser enganado pelo marketing do README.

O documento complementar é [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7
(Modelo de Segurança). Quando os dois divergirem, a implementação em
`src/AetherNet.Security/` é a autoridade.

---

## 1. Escopo

### O que `aether-protocol` É

Uma biblioteca de mensagens com criptografia ponta a ponta no estilo Signal Protocol, além de
um primitivo de rede mesh (roteamento estilo AODV + armazenamento e encaminhamento DTN + flood SOS).
As garantias de segurança principais são:

1. **Confidencialidade** — os corpos das mensagens são criptografados com AES-256-GCM sob
   chaves por mensagem derivadas de um Double Ratchet (Signal §5).
2. **Autenticidade** — cada `MeshPacket` carrega uma assinatura Ed25519 sobre um
   buffer de dados assinável canônico (PROTOCOL_SPEC §2.4).
3. **Proteção contra replay** — pacotes com `(SourceUhid, PacketNonce)` duplicados são
   descartados dentro de uma janela de frescor de 5 minutos.
4. **Sigilo futuro e pós-comprometimento** — o Double Ratchet regenera as chaves a cada
   mudança de DH-pubkey em um roundtrip; um atacante que comprometa uma chave de sessão não
   recupera nem mensagens passadas nem futuras.

### O que `aether-protocol` NÃO É

- **Não é um substituto para segurança na camada de transporte.** Use TLS para comunicação
  cliente→servidor. O E2EE do Aether é para tráfego mesh ponto a ponto; no momento em que um
  pacote sai da mesh para um backend centralizado, a segurança de transporte desse backend é
  responsabilidade do host.
- **Não é um sistema de gerenciamento de chaves.** O host fornece armazenamento durável para
  material de identidade e pré-chave via `IPreKeyStore` (ou qualquer adaptador baseado em
  `IKeyValueStore`). Integração com keystore de hardware, atestação TPM, recuperação por
  escrow de chave e criptografia em repouso são todas responsabilidades do host.
- **Não é um sistema de autenticação.** O Aether autentica que "o portador da chave de
  identidade X disse este pacote". Mapear a chave de identidade X para "o ser humano Alice"
  é responsabilidade de UX do host (comparação de número de segurança, troca de impressão
  digital fora de banda, cadeia de confiança prévia).
- **Não é uma rede de privacidade.** O fio revela tipo de mensagem, comprimento do pacote,
  UHID de origem, UHID de destino, contagem de saltos e temporização. Não é o Tor.

---

## 2. Ataques defendidos

### 2.1. Escuta em trânsito

Cada payload é criptografado com AES-256-GCM sob uma chave por mensagem derivada
da cadeia simétrica do Double Ratchet (Signal §5.1, HMAC-SHA256 com
separação de domínio `0x01`/`0x02`). Um atacante que capture todos os pacotes
entre Alice e Bob não recupera nada sem uma das suas chaves de sessão.

Verificado por `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs`
e pelos vetores de referência entre linguagens em `fixtures/signal/expected/ratchet_step_basic.json`.

### 2.2. Falsificação de mensagens

Cada pacote Wave-2 carrega uma assinatura Ed25519 sobre o buffer
`BuildSignableData(packet)` canônico (`src/AetherNet.Security/Services/PacketSigningService.cs`,
PROTOCOL_SPEC §2.4). Pacotes falsificados falham na verificação e são descartados em
cada salto que conhece a chave pública de identidade da origem. Pacotes Route Reply (RREP)
são assinados pelo destino declarado — nós intermediários não podem se passar pelo destino
porque não possuem a chave privada Ed25519 do destino.

### 2.3. Ataques de replay

`PacketSigningService.VerifyPacketAsync`:

- Rejeita pacotes cujo `TimestampMs` esteja a mais de 5 minutos do UTC local
  (`FreshnessWindowMs = 5 * 60 * 1000`).
- Mantém um mapa de deduplicação em memória com chave `(SourceUhid, PacketNonce)`
  com TTL de 5 minutos. A chave de dedup foi alterada de apenas `nonce` para
  `(source, nonce)` no commit `5bd52a9` para corrigir dois modos de falha:
  colisões de nonce entre remetentes derrubando tráfego legítimo, e ataques de
  pré-registro onde um adversário planta um nonce contra um destinatário para
  bloquear o primeiro pacote do remetente legítimo.

Contadores: `aethernet.nonces.replayed`, `aethernet.timestamps.stale`.

### 2.4. Sigilo futuro (comprometimento de chave passada)

O Double Ratchet deriva uma nova chave de cadeia de envio a cada passo de rotação DH
(KDF_RK, HKDF-SHA256 sobre `salt = current_root_key`,
`info = "aether-ratchet-rk-v1"`, bloco de 64 bytes dividido 32+32 em nova
chave raiz e de cadeia — `src/AetherNet.Security/Services/SignalProtocolService.cs`).
Um atacante que comprometa o estado atual da sessão não consegue descriptografar nenhuma
mensagem anterior: cada chave de mensagem anterior foi derivada e zerada
(`CryptographicOperations.ZeroMemory`) antes do próximo passo do ratchet.

### 2.5. Segurança pós-comprometimento (recuperação de chaves futuras)

Quando o lado receptor observa um novo `SenderEphemeralKeyX25519` em uma
mensagem de entrada, ele executa um passo de DH-ratchet no recebimento (Signal §5.2). O
estado de sessão armazenado em cache pelo atacante fica obsoleto no próximo roundtrip; um
atacante que tira um snapshot de uma sessão e se afasta não consegue mais descriptografar
mensagens assim que as partes legítimas trocarem uma rodada.

O passo de rotação DH no recebimento foi implementado em todas as 8 linguagens — veja
`OPEN_ISSUES.md` item 2 para a lista de commits entre implementações.

### 2.6. Replay de pré-chave de uso único

Cada pré-chave de uso único (OPK) é consumida exatamente uma vez. A referência em C#
conta com um pool de 100 OPKs com emissão FIFO, recarga preguiçosa a cada geração de
bundle e consumo único protegido por lock
(`SignalProtocolService.TopUpOpkPoolNoLock`, verificado por
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`). Uma OPK é removida e zerada no momento
em que o respondedor a consome durante o X3DH, então uma mensagem PreKey repetida
que reutilize o mesmo id de OPK não consegue estabelecer uma sessão.

**Resolvido (todas as 8 linguagens).** As outras sete linguagens capazes de
sessão agora contam com o mesmo pool de 100 OPKs com emissão FIFO única, recarga
preguiçosa e consumo único protegido por lock, fechando o risco anterior de
concorrência de OPK-única-por-sessão: Rust
(`rust/src/security/signal_protocol.rs` — `DEFAULT_OPK_POOL_SIZE = 100`,
`available_opk_ids: VecDeque<i32>`, `top_up_opk_pool`), Go
(`go/security/signal_protocol.go` — `DefaultOpkPoolSize = 100`,
`topUpOpkPoolLocked`), Python
(`python/aethernet/security/signal_protocol.py` —
`DEFAULT_OPK_POOL_SIZE = 100`, `available_opk_ids: Deque`,
`_top_up_opk_pool_locked`), TypeScript
(`typescript/src/security/PreKeyStore.ts` — pool FIFO consumido uma vez,
`typescript/tests/opk_pool.test.ts`), Kotlin
(`kotlin/src/main/kotlin/aethernet/security/SignalProtocol.kt` —
`DEFAULT_OPK_POOL_SIZE = 100`, `ArrayDeque<Int>`, `topUpOpkPoolNoLock`),
Swift (`swift/Sources/AetherNetProtocol/Security/SignalProtocol.swift` —
`defaultOpkPoolSize = 100`, FIFO `removeFirst()`, `topUpOpkPool`) e C
(`c/src/signal_protocol.c` — `AETHERNET_SIGNAL_OPK_POOL_SIZE = 100`, pool
semeado 1..100 em `aethernet_signal_service_init`, emissão do primeiro não
consumido, OPK zerada + marcada como `consumed` no X3DH do respondedor).

### 2.7. Deriva de fio entre linguagens

Toda implementação deve produzir saídas idênticas ao nível de byte em relação ao
corpus de fixtures em `fixtures/`:

- `fixtures/expected/*.bin` — 17 fixtures de serialização de pacotes, com igualdade de bytes
  verificada nas 8 linguagens no CI.
- `fixtures/signal/expected/x3dh_basic.json` — matemática X3DH (4 DHs X25519,
  raiz HKDF-SHA256 com `info = "aether-x3dh-root-v1"`).
- `fixtures/signal/expected/ratchet_step_basic.json`,
  `ratchet_step_three_iterations.json` — KDFs do ratchet simétrico.
- `fixtures/signal/expected/kdf_rk_basic.json` — passo de DH-ratchet.

Uma deriva na string de info HKDF, ordem de bytes ou preenchimento de qualquer linguagem
falha no build de `SignalFixtureTests`. A interoperabilidade compatível com o fio é,
portanto, um invariante em tempo de compilação, não uma esperança em tempo de execução.

### 2.8. Comprometimento DH estático-estático (o X3DH quebrado anterior)

Antes de 2026-05-05, a implementação `KEY_EXCHANGE` em C# usava a chave de identidade
do nó local para ambas as operações DH — um colapso estático-estático que quebrava a
propriedade de sigilo futuro por chave efêmera do X3DH. Corrigido pelo commit `07a93f5`:
o X3DH real agora realiza os 4 DHs canônicos
`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`
com um efêmero novo por sessão. Veja `OPEN_ISSUES.md` §1.

### 2.9. Loops de roteamento e tempestades de broadcast

O `RoutingService` deduplica pacotes RREQ por `(originUhid, broadcastId)`
em um cache limitado (padrão 10.000 entradas; `ProtocolConstants.RouteRequestDedupCacheSize`).
O TTL é decrementado a cada salto e pacotes com `Ttl == 0` são descartados.
Broadcasts SOS têm taxa limitada a 3/hora por origem e a supressão de auto-origem
impede que um nó reenvie seu próprio SOS.

### 2.10. DoS por esgotamento do pool OPK

O pool OPK é limitado (`OpkPoolSize`, padrão 100) e o health check do Signal
levanta `Unhealthy` quando as OPKs disponíveis ficam abaixo de
`SignalOptionsBag.MinAvailableOpks` (padrão 10). Os hosts configuram alertas no
status de saúde `aether-signal`. Um atacante que esgote OPKs ao buscar bundles
não consegue exceder o tamanho configurado do pool; o X3DH do respondedor continua
funcionando para bundles já emitidos e se recupera quando a recarga é executada na
próxima geração de bundle.

### 2.11. Rastreamento passivo de dispositivos por BLE

Um scanner passivo que registra uma MAC BLE estável ou um Service UUID estável pode
seguir um dispositivo ao longo do tempo e do lugar. O `BlePrivacy`
(`src/AetherNet.Security/Privacy/BlePrivacy.cs`) fecha o vetor de vinculação de
identificadores: o Service UUID anunciado é rederivado a cada 15 minutos como
`HMAC-SHA256(rotation_key, window)` (PROTOCOL_SPEC §12.3), e os pares são endereçados
por endereços privados resolvíveis (IRK + `ah`) em vez de uma MAC fixa. Sem a chave de
rotação ou o IRK, dois anúncios não podem ser vinculados. Fixado em
`fixtures/bleprivacy/`.

**Risco residual.** Isso fecha apenas o vetor de identificador BLE — **não** torna o
Aether uma rede de privacidade (§1). Uma vez que um pacote está na mesh, o cabeçalho
`MeshPacket` em texto claro ainda expõe o UHID de origem/destino, o tipo, o comprimento
e a temporização (a análise de tráfego permanece fora do escopo, §3.3), e a tomada de
impressão digital na camada RF não é tratada. Emitir os identificadores rotativos no ar
é tarefa da pilha BLE do host — a biblioteca apenas os deriva.

### 2.12. Divulgação de chave sob coação (duress)

Um adversário com posse física que coage o usuário a desbloquear. O `PanicWipe`
(`src/AetherNet.Security/Privacy/PanicWipe.cs`) aceita um **PIN de coação** — comparado
com um `SHA-256(pin)` armazenado em tempo constante (sem vazamento de temporização por
saída antecipada) — que apaga com segurança cada chave de identidade (sobrescrita com
aleatório, depois zerada) por todo o manifesto de nomes de chave, de modo que o
dispositivo entregue não contém nenhuma identidade utilizável. Fixado em
`fixtures/panicwipe/`.

**Risco residual.** De melhor esforço e explicitamente limitado: **não** defende contra
uma imagem forense capturada *antes* do apagamento, o nivelamento de desgaste da flash
que preserva uma cópia anterior dos bytes da chave, um adversário que force o PIN
*genuíno*, ou coação depois que as mensagens já foram lidas. A comparação em tempo
constante mitiga a temporização de adivinhação do PIN, não um adversário de canal
lateral completo (§3.2).

### 2.13. Perda do único dispositivo (recuperação)

Não é um atacante, mas a falha de disponibilidade de perder a única cópia de uma
identidade. O backup por frase de recuperação (`src/AetherNet.Security/Backup/`)
codifica a semente de identidade Ed25519 de 32 bytes como uma frase BIP-39 de 24
palavras com soma de verificação (PROTOCOL_SPEC §12.4) que restaura a identidade em
qualquer dispositivo — nenhum servidor ou custodiante a retém.

**Risco residual — uma nova superfície de roubo.** A frase **é** a identidade: qualquer
um que leia as 24 palavras pode se passar totalmente pelo usuário, sem revogação. Ela
troca um risco de perda de dispositivo por um risco de segredo em papel. A biblioteca
codifica/decodifica e calcula a soma de verificação da frase; a exibição segura, o
armazenamento e a frase-senha BIP-39 opcional são responsabilidade do host.

### 2.14. Injeção de dispositivo malicioso na sincronização multidispositivo

Um atacante que tenta inserir um dispositivo que controla no conjunto de sincronização
de uma vítima, ou forjar registros de sincronização. Um `DeviceLink`
(`src/AetherNet.Security/Sync/`) é **assinado com Ed25519 pela chave de identidade**
(PROTOCOL_SPEC §12.1), de modo que apenas o portador da identidade pode autorizar um
novo dispositivo — um link não assinado ou com chave incorreta falha na verificação. As
cargas úteis `SyncRecord` trafegam criptografadas de ponta a ponta dentro do caminho
DTN/mesh, então os relés as transportam mas não conseguem lê-las. Fixado em
`fixtures/sync/`.

**Risco residual.** Isso autentica a *vinculação*, não o comportamento posterior do
dispositivo vinculado: um dispositivo que é legitimamente vinculado e *então*
comprometido vê todo o estado sincronizado — a sincronização não tem sigilo futuro por
registro. A reconciliação é último-a-escrever-vence sobre
`(created_at_ms, logical_clock, device_id, record_id)`, então um dispositivo vinculado
com um relógio desviado pode enviesar qual registro vence; a integridade do relógio é
assunto do host. A paridade byte a byte das assinaturas carrega a exceção do
Swift/CryptoKit observada em PROTOCOL_SPEC §12.1.

---

## 3. Fora do escopo

Estes são ataques reais que o protocolo **não** interrompe. Alguns são teoricamente
mitigáveis em uma versão futura; outros são fundamentalmente uma preocupação do host.

### 3.1. Comprometimento do endpoint

Se um atacante tiver acesso root ao dispositivo de Alice, ele poderá ler os bytes
privados da chave de identidade dela na memória e descriptografar cada sessão que ela
possuir. O protocolo pressupõe que a memória do processo do dispositivo é confiável.
Mitigações (keystore da plataforma, SGX, keystores suportados por hardware) são
explicitamente responsabilidade do host — veja a Seção 4.

### 3.2. Ataques de canal lateral

A implementação de referência usa
`CryptographicOperations.FixedTimeEquals` para comparação de ratchet-pubkey
(`SignalProtocolService.ConstantTimeEquals`), mas não é especificamente
endurecida contra:

- Canais laterais de temporização em AES-GCM (o `AesGcm` da BCL do .NET é acelerado
  por hardware em CPUs com suporte a AES-NI; o tempo do fallback de software não é auditado).
- Canais laterais de análise de energia (puramente software — sem contramedidas de hardware).
- Temporização de cache em caminhos de derivação de chave (HKDF-SHA256 via BCL).

Um ataque de laboratório de nível estatal em um dispositivo desbloqueado roubado é plausível.

### 3.3. Análise de tráfego

O formato de fio revela:

- **Tipo** de pacote (1 byte no deslocamento 1 — RREQ vs. Data vs. SOS está em
  texto claro).
- **Comprimento** do pacote (os payloads não são preenchidos).
- **UHIDs de origem e destino** (UTF-8, em texto claro).
- **Timestamps**, **TTL** e **prioridade**.

Preenchimento, tráfego de cobertura e roteamento em cebola não estão implementados. Um
adversário que possa observar passivamente o tráfego BLE / Wi-Fi pode construir um
grafo de contatos e um perfil de temporização de cada conversa, mesmo sem conseguir
ler o conteúdo. Esta é uma limitação conhecida; a mitigação exigiria uma quebra no
formato de fio e não está no roteiro atual.

### 3.4. Ataques quânticos

X25519 (RFC 7748) e Ed25519 (RFC 8032) ambos se quebram sob um
computador quântico suficientemente grande executando o algoritmo de Shor. O
protocolo **não é pós-quântico**. Uma futura migração para um esquema híbrido
Kyber + X25519 / Dilithium + Ed25519 é uma preocupação conhecida, mas não está
agendada. O texto cifrado existente gravado hoje por um adversário apostando em
"coletar agora, descriptografar depois" está em risco se um CRQC chegar dentro do
horizonte de tempo relevante.

### 3.5. Mensagens em grupo em escala

`AetherNet.Security` disponibiliza uma costura `IGroupKeyProvider`, mas o protocolo
completo Signal Sender Keys (a construção assíncrona de mensagens em grupo que o Signal
usa) **não está** implementado a partir do HEAD. Hosts que precisam de mensagens em grupo
hoje recorrem a N sessões pairwise — o que funciona, mas tem custo O(N) por envio ao grupo.
PROTOCOL_SPEC §7 cobre apenas ameaças a destinatários únicos.

### 3.6. Verificação de identidade no primeiro contato (TOFU)

O Aether autentica que "o par que possui a chave de identidade X assinou isto". Ele
**não** autentica que "a chave de identidade X realmente pertence ao ser humano Alice
que o usuário espera estar conversando". No primeiro contato, um homem-no-meio ativo que
controla a rede durante a troca do primeiro bundle pode substituir sua própria chave de
identidade, assinar seu próprio bundle e intermediar o tráfego em ambas as direções de
forma transparente.

Esta é a fraqueza padrão do "Trust On First Use" do Signal. A mitigação canônica é a
comparação de número de segurança / impressão digital fora de banda (pessoalmente, por
um canal separado, em uma tela de verificação pré-compartilhada). O protocolo atualmente
não expõe uma superfície de API pública para derivação de número de segurança; rastreado
como uma lacuna (ainda não em `OPEN_ISSUES.md`) — o UX do host não deve fingir que a
verificação é feita por padrão.

### 3.7. Ataques na camada de rede sobre o transporte subjacente

Interferência de sinal (BLE, Wi-Fi, NearLink), negação de serviço na camada RF e ataques
contra os fluxos de pareamento/vinculação do transporte estão fora do escopo.
O transporte (`ITransportService`) é tratado como um tubo de bytes opaco.
Um interferidor que controle o espectro impede o Aether de entregar qualquer coisa.

### 3.8. Ataques de roteamento além da janela de dedup

Inundação Sybil por nós de curta duração que ainda não acumularam uma pontuação de
confiabilidade, abandono oportunista de relay que não aciona a heurística de confiabilidade,
e ataques de esgotamento de recursos que ficam abaixo dos limites de taxa não são
especificamente mitigados. A pontuação de confiabilidade (PROTOCOL_SPEC §3.5) desprioritiza
nós comprovadamente ruins, mas não é um protocolo de roteamento totalmente resiliente
a Byzantine.

---

## 4. Premissas para que as alegações de segurança sejam válidas

As defesas da Seção 2 são baseadas nos seguintes invariantes. Se qualquer um deles
for quebrado, a propriedade de segurança correspondente é perdida.

1. **Durabilidade da chave de identidade.** O host armazena os pares de chaves de
   identidade de longo prazo Ed25519 + X25519 de forma durável e segura (ex.: via
   `IPreKeyStore` contra um `FileSystemKeyValueStore` encapsulado em
   `EncryptedKeyValueStore`, ou contra o keystore da plataforma). A perda de uma
   chave de identidade equivale ao comprometimento total da conta; o portador da
   chave privada pode assinar qualquer coisa como o par original.

2. **Correção do CSPRNG.** `RandomNumberGenerator.GetBytes` e
   `RandomNumberGenerator.GetInt32` na plataforma de destino produzem saída
   criptograficamente segura. Todo o protocolo — chaves efêmeras, nonces AES-GCM,
   nonces de pacote, ids de OPK — depende disso. Em plataformas onde a fonte aleatória
   da BCL é degradada (alguns alvos embarcados, pools de entropia Linux quebrados), toda
   a árvore de confiança cai.

3. **Relógio do sistema dentro de ±5 minutos UTC.** A proteção contra replay é baseada
   em janela de timestamp. Um dispositivo com um relógio muito errado rejeita todos os
   pacotes (relógio muito atrasado) ou aceita replays indefinidamente (relógio muito
   adiantado). Os hosts DEVEM enviar uma verificação de sanidade contra uma fonte de
   tempo confiável na inicialização do aplicativo.

4. **Consumo atômico de OPK.** Quando um `ConsumeOneTimePreKeyAsync(id)` baseado em
   `IPreKeyStore` é executado simultaneamente com uma operação X3DH de respondedor
   contra o mesmo id, o consume DEVE ter sucesso ou falhar atomicamente. O pool C#
   de referência serializa o consumo sob `_preKeyLock`; um store fornecido pelo host
   em um backend não transacional (ex.: um store de arquivo ingênuo com
   leitura-modificação-escrita) pode permitir que a mesma OPK seja consumida duas vezes,
   quebrando a propriedade 2.6. `KeyValuePreKeyStore` usa `IKeyValueStore.RemoveAsync`
   diretamente para o consumo — atômico desde que o remove do KV subjacente seja atômico.

5. **Verificação de identidade no primeiro contato.** A chave pública de identidade do
   par foi verificada fora de banda (número de segurança, impressão digital, diretório
   confiável) antes da primeira mensagem trocada — ou o host aceita o risco TOFU e
   está disposto a detectar uma mudança de chave no próximo contato. Sem isso, §3.6
   é uma janela aberta de MitM.

6. **A memória do processo do host não é legível pelo adversário.** §3.1.

---

## 5. Fraquezas conhecidas + mitigações

### 5.1. MitM no primeiro contato (TOFU)

**Fraqueza:** um atacante ativo que controla o link ponto a ponto durante a primeira
troca de bundle pode substituir seu próprio bundle e intermediar o tráfego.
**Mitigação:** o UX do host deve expor um fluxo de comparação de número de segurança /
impressão digital de chave pública antes de tratar um contato como verificado. Uma
superfície de API pública para derivação de número de segurança ainda não está disponível
no `AetherNet.Security`; rastreado como lacuna.

### 5.2. Atraso na rotação da pré-chave assinada

**Fraqueza:** até que o host chame `RotateSignedPreKeyAsync`, o mesmo SPK é servido
em cada bundle. Um adversário que aprenda a chave privada do SPK (ex.: via comprometimento
de endpoint §3.1) pode executar X3DH contra qualquer bundle capturado desde a última rotação.
**Mitigação:** agende chamadas diárias de `RotateSignedPreKeyAsync`. As
`SignedPreKeyRotationOptions` padrão retêm 3 SPKs anteriores para que mensagens em
trânsito assinadas sob uma chave recém-rotacionada ainda possam ser descriptografadas
durante a janela de rotação. O intervalo de rotação padrão é de 7 dias — adotantes
que operam contra usuários ativamente visados devem encurtar este intervalo.

### 5.3. Estado de sessão em memória sem persistência

**Fraqueza:** se `SignalProtocolService` for construído sem um `sessionStore`,
uma falha ou reinicialização do processo perde cada sessão ativa. O sigilo futuro está
intacto (as chaves perdidas não podem ser recuperadas), mas a próxima mensagem do par
falhará na descriptografia porque a cadeia de recebimento desapareceu.
**Mitigação:** conecte `KeyValueSignalSessionStore` a um `IKeyValueStore` durável
em qualquer implantação de produção. O console de demonstração de exemplo usa
`InMemoryDtnBundleStore` etc. para clareza; os hosts de produção não devem fazer o mesmo.

### 5.4. Janela de transição do byte de flag de compressão no fio

**Fraqueza:** o `MessagingService` tem uma costura opcional de compressão Brotli que
antepõe um byte de flag incondicional ao envelope de texto simples. Um par executando
código anterior à compressão lerá erroneamente o byte de flag como o primeiro byte do
payload da aplicação.
**Mitigação:** os adotantes definem `MessagingOptions.Compression.Enabled =
false` até que todos os pares tenham os novos bits. O byte de flag será controlado por
uma futura negociação de capacidades no handshake. Veja a nota de migração em
`CompressionOptions`.

### 5.5. Lacuna na linguagem C — RESOLVIDA

**Fraqueza anterior:** a implementação em C disponibilizava apenas os primitivos
X25519 + KDF_RK mais o verificador de fixtures, sem uma API completa de
`SignalProtocolService`.
**Resolvido.** `c/src/signal_protocol.c` agora implementa o serviço de sessão
completo — estabelecimento X3DH (verificação da assinatura Ed25519 da SPK e então
os 4 DHs canônicos `DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) ||
DH(EK_A,OPK_B)` com root HKDF-SHA256 em
`aethernet_signal_process_pre_key_bundle`), o ciclo de vida OPK / SPK (pool
semeado 1..100 em `aethernet_signal_service_init`, projeção de bundle
`has_pre_key`, OPK consumida no lado do respondedor) e integração completa do
Double-Ratchet (`dh_ratchet_receive`, `dh_ratchet_send_only`,
`aethernet_signal_encrypt` / `aethernet_signal_decrypt` sobre AES-256-GCM).
Round-trips E2E de dois nós vivem em `c/tests/test_signal_session.c`. Hosts em
alvos baseados em C agora podem trafegar tráfego criptografado ponta a ponta na
superfície C.

### 5.6. Pool OPK exclusivo do C# — RESOLVIDO

**Fraqueza anterior:** o pool de 100 OPKs com emissão FIFO e consumo atômico
(defesa 2.6) era um recurso exclusivo do C#; as outras linguagens emitiam uma
única OPK por sessão, então, sob carga de iniciadores simultâneos, dois
respondedores competindo pela mesma fonte de bundle podiam observar a mesma OPK
e o X3DH podia produzir uma incompatibilidade de estado de sessão.
**Resolvido (todas as 8 linguagens).** Toda linguagem capaz de sessão agora conta
com o mesmo pool de 100 OPKs com emissão FIFO única, recarga preguiçosa e consumo
único protegido por lock — veja a evidência arquivo:símbolo por linguagem
enumerada na defesa 2.6. O risco de iniciadores simultâneos está fechado; nenhuma
serialização do consumo de bundle no lado do host é necessária.

### 5.7. Assinatura de demonstração em linguagens não C#

**Fraqueza:** os programas de demonstração por linguagem (Go, Python, TS, Rust,
Swift, Kotlin, C) assinam os bytes serializados completos do fio para visualização,
em vez do buffer canônico `BuildSignableData`. O código da biblioteca nessas linguagens
está correto — apenas as demonstrações tomam o atalho, mas é confuso para quem está
fazendo ports.
**Mitigação:** rastreado como `OPEN_ISSUES.md` §10. Trate o Passo 3 da demonstração
em C# como o fluxo canônico.

---

## 6. Reportar problemas de segurança

Veja [`SECURITY.md`](../SECURITY.md) para a política de divulgação responsável.
Envie um e-mail para `security@thegeeknetwork.co.za` com os passos de reprodução;
espere confirmação em 48 horas e uma avaliação inicial em 7 dias.

Problemas que estão fora do escopo de acordo com a Seção 3 ainda são bem-vindos como
relatos — preferimos saber do que não estamos nos defendendo do que ter um usuário
descobrir a lacuna em produção.
