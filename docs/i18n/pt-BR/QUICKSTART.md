# Início Rápido — integre o Aether ao seu app .NET em 5 minutos

Este guia leva você de um `Program.cs` em branco a dois nós — Alice e Bob —
trocando uma mensagem criptografada de ponta a ponta. Tudo compila contra o HEAD
(`b8b3d22`) de [`bhengubv/aether-protocol`](../) no .NET 10.

> Procurando a arquitetura completa? Veja [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> Procurando o que a criptografia protege e o que não protege? Veja
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Limitações conhecidas são rastreadas em
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. Instalação

As bibliotecas Aether ainda não estão publicadas no NuGet. Por enquanto, use uma
`<ProjectReference>` para o repositório local:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/Aether.DependencyInjection/Aether.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/Aether.Storage/Aether.Storage.csproj" />
</ItemGroup>
```

`Aether.DependencyInjection` puxa transitivamente `Aether.Core`,
`Aether.Security`, `Aether.Messaging`, `Aether.Transport`, `Aether.Streaming`,
`Aether.Voice` e `Aether.Content` — tudo que você precisa para a pilha de mensagens.
`Aether.Storage` é uma dependência separada somente se você quiser persistência em
disco (veja a Seção 6).

Assim que o pacote for publicado no NuGet, isso se tornará:

```bash
dotnet add package Aether.DependencyInjection
dotnet add package Aether.Storage   # opcional, para persistência
```

As APIs dos pacotes não mudarão entre o fluxo de referência de projeto e o fluxo
do NuGet.

---

## 2. Configure tudo — registro completo da pilha canônica

A extensão de DI `AddAetherProtocol(...)` retorna um builder fluente. Cada
capacidade é opt-in: um host que só precisa de roteamento encadeia `.AddRouting()`
e para por aí. Abaixo está a pilha completa que um adotante típico deseja.

```csharp
using Aether.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // pré-requisito do lado do host para AddHealthChecks() abaixo
builder.Services
    .AddAetherProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (registra ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // RREQ/RREP no estilo AODV + InMemoryRouteStore
    .AddDtn()                                // custódia store-and-forward de 72h + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // flood de emergência
    .AddMessaging()                          // mensagens criptografadas 1-para-1, requer AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // simulador em memória (substitua por BLE / Wi-Fi Direct em produção)
    .AddHealthChecks();                      // quatro registros de IHealthCheck no nível do protocolo

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherProtocol` e todos os métodos encadeados são idempotentes no mesmo
`IServiceCollection` — chamá-los duas vezes não duplica o registro. A ordem
importa em um lugar: `AddMessaging()` lança `InvalidOperationException` se
`AddSignalProtocol()` ou `AddRouting()` não foi chamado antes.

O `InProcessTransport` é para testes e demos. Em produção, você implementa
`Aether.Transport.Abstractions.ITransportService` para sua camada física (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) e registra um `IMeshSender` que
encaminha pacotes para ela. Os serviços de Roteamento/DTN/Mensagens rodam
inalterados sobre essa camada.

---

## 3. Estabeleça uma sessão

O X3DH é assimétrico. O **iniciador** processa um bundle publicado pelo
**respondente**; a sessão do respondente se estabelece automaticamente quando ele
recebe a primeira mensagem criptografada do iniciador (uma "mensagem PreKey").

```csharp
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publica um bundle: chave de identidade + chave pré-assinada + uma chave pré-uso única.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice processa o bundle. Quatro X25519 DHs são executados; a chave raiz resultante
// semeia a cadeia de envio do Double Ratchet dela.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — se estabelece automaticamente na primeira mensagem recebida
```

`PreKeyBundle` é um DTO simples. Os hosts o publicam como quiserem — diretamente
peer-to-peer pela mesh (tipos de pacote `PreKeyRequest` / `PreKeyResponse`,
veja PROTOCOL_SPEC §2.5), via um diretório backend ou entregue manualmente. O
protocolo não impõe um transporte para bundles.

---

## 4. Enviar e receber

O caminho de ponta a ponta mais curto (sem DI, sem roteamento, apenas o cifrador):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Transmita o ciphertext pelo seu transporte. No lado de Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

Em produção, você envolve o ciphertext em um `MeshPacket`, assina com
`PacketSigningService.SignPacketAsync` e deixa `MessagingService.SendAsync`
cuidar do roteamento, retentativas e fallback DTN:

```csharp
using Aether.Messaging;
using Aether.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent já foi descriptografado pela camada de mensagens.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> o ciphertext saiu pela mesh, DTN ou relay backend
// handed == false -> enfileirado na caixa de saída; ProcessOutboxAsync vai tentar novamente
```

`MessagingService` enfileira mensagens — nunca as envia em texto claro — quando
ainda não existe uma sessão Signal com o destinatário. Assine `SessionRequired`
para saber quando buscar o bundle de pré-chave de um peer e chamar
`alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. Ida e volta de dois nós em 50 linhas

Este é um script executável. Copie para `Program.cs`, adicione uma `<ProjectReference>`
para `Aether.Security.csproj` (que puxa `Aether.Core` e a criptografia BCL), e
execute `dotnet run`.

```csharp
using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publica um bundle; Alice o processa. Após isso, Alice pode criptografar
// para Bob; a sessão de Bob se estabelece automaticamente quando ele descriptografa
// a primeira mensagem de Alice (que carrega metadados X3DH como "mensagem PreKey").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Produção: serialze `outbound` (ou envolva em um MeshPacket e chame
// PacketSigningService.SignPacketAsync) e envie os bytes pelo seu
// transporte. O receptor reconstrói o EncryptedPayload e chama
// DecryptAsync. Aqui ambos os nós compartilham o mesmo processo, então apenas
// passamos o registro diretamente.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (sessão agora ativa em ambas as direções) -----------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

Saída esperada:

```
Bob got: "hello bob"
Alice got: "ack"
```

Para um demo de ponta a ponta mais completo — incluindo assinatura de pacotes, relay
multi-hop pelo Charlie, MessagingService e fallback de custódia DTN — execute o
console incluído:

```bash
dotnet run --project samples/Aether.Demo.Console
```

A etapa de custódia DTN (Etapa 9 do demo) é o padrão canônico de integração para
produção: `MessagingService` + `RoutingService` + `DtnService` compostos contra um
adaptador `IMeshSender` sobre o transporte real.

---

## 6. Persistência (armazenamento chave-valor)

Por padrão, `SignalProtocolService` mantém cada sessão, chave de identidade, chave
pré-assinada e chave pré-uso única na memória do processo. Uma falha significa:
identidade perdida (não é possível descriptografar nenhuma sessão anterior), pool OPK
perdido (o X3DH do respondente começa a falhar para novos iniciadores), estado do
Double Ratchet perdido (o sigilo futuro está intacto, mas a ordenação de mensagens
quebra).

`Aether.Storage.FileSystemKeyValueStore` é um `IKeyValueStore` mínimo com suporte
em disco (um arquivo por entrada, renomeação atômica via arquivo temporário). Conecte-o
pelos adaptadores `KeyValue*Store`:

```csharp
using Aether.Storage;
using Aether.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Conecte o mesmo armazenamento KV em AMBOS os adaptadores para que identidade,
// sessões e pré-chaves sobrevivam a uma reinicialização.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore é interno — KeyValueSignalSessionStore também é interno.
// Em um host Wave-3+, registre o construtor de SignalProtocolService com estado
// persistente pelo seu composition root (ou substitua o registro padrão
// AddSignalProtocol() pela sua própria factory).
```

`FileSystemKeyValueStore` é intencionalmente simples: sem compactação, sem
transações entre chaves, sem criptografia em repouso. Para criptografia em repouso,
sobreponha `EncryptedKeyValueStore` sobre o sistema de arquivos (ou seu próprio KV)
e forneça um `IDataAtRestKeyProvider` — o host é proprietário do wrapper de chave,
não o protocolo.

Você também pode registrar um `IRouteStore`, `IDtnBundleStore` e `IMessageStore`
não padrão no contêiner de DI antes de encadear `.AddRouting()` / `.AddDtn()` /
`.AddMessaging()` — o builder usa `TryAdd*` e respeita o que você colocar no
contêiner primeiro. Os adaptadores `KeyValueRouteStore`, `KeyValueDtnBundleStore`
e `KeyValueMessageStore` em `Aether.Storage` cobrem esses slots contra qualquer
`IKeyValueStore`.

---

## 7. Observabilidade

O Aether traz instrumentação OpenTelemetry de primeira classe. Assine um
medidor e uma fonte de atividade — ambos são strings estáveis e as bibliotecas
não dependem de nenhum SDK OTel específico:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Aether.Protocol"))
    .WithTracing(t => t.AddSource("Aether.Protocol"));
```

O que você obtém:

- **Contadores**: `aether.messages.encrypted`, `aether.messages.decrypted`,
  `aether.signatures.validated`, `aether.signatures.rejected`,
  `aether.nonces.replayed`, `aether.timestamps.stale`,
  `aether.sessions.established`, `aether.ratchet.dh_steps`,
  `aether.route.requests_emitted`, `aether.route.replies_received`,
  `aether.route.cache_hits`, `aether.dtn.bundles_accepted`,
  `aether.dtn.bundles_delivered`, `aether.dtn.bundles_expired`,
  `aether.sos.broadcasts`, `aether.sos.rebroadcasts_suppressed`,
  `aether.messaging.messages_sent`, `aether.messaging.messages_queued`,
  `aether.messaging.dtn_fallback`.
- **Histogramas** (ms): `aether.encrypt.latency`, `aether.decrypt.latency`,
  `aether.route.lookup_latency`, `aether.sign.verify_latency`.
- **Atividades** com tags UHID sanitizadas para PII:
  `Aether.Encrypt`, `Aether.Decrypt`, `Aether.DhRatchet.Step`,
  `Aether.Sign.Packet`, `Aether.Verify.Packet`, além de spans de roteamento e DTN.

Quando nenhum listener está conectado, os caminhos quentes não alocam nada —
`Add` de contador degrada para uma leitura volatile e `StartActivity` retorna `null`.

O inventário completo de instrumentos e o contrato de PII vivem em
`src/Aether.Core/Diagnostics/AetherTelemetry.cs`.

---

## 8. Health checks

`AddHealthChecks()` (o método do builder Aether) registra quatro verificações no
nível do protocolo no `HealthCheckService` do host. Cada uma grava `data` estruturado
útil para dashboards.

| Nome da verificação | O que monitora | Saudável → Degradado → Não saudável |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10.000 → ≥ 10.000 → ≥ 50.000 (padrões; ajustáveis)          |
| `aether-dtn`                | bundles ativos em custódia                                 | < 80% capacidade → ≥ 80% → ≥ `DtnMaxBundlesPerNode`            |
| `aether-signal`             | OPKs disponíveis + contagem de sessões ativas              | piso OPK → não saudável abaixo de `MinAvailableOpks` (padrão 10); teto de sessão → degradado acima de 1.000 |
| `aether-messaging-outbox`   | profundidade da caixa de saída pendente + crescimento entre amostras | < 100 → ≥ 100 → ≥ 100 E crescendo                      |

Ajuste via bags `AetherOptions.Routing`, `Dtn`, `Signal` e `Messaging`. O host
deve chamar `services.AddHealthChecks()` antes do `.AddHealthChecks()` do builder
Aether para que os registros fiquem visíveis para `MapHealthChecks(...)`.

---

## 9. Próximos passos

- **`docs/PROTOCOL_SPEC.md`** — formato wire, roteamento, troca de chaves, DTN, tabela
  completa de tipos de pacote e o algoritmo canônico `BuildSignableData`.
- **`docs/THREAT_MODEL.md`** — o que a criptografia defende, o que está explicitamente
  fora de escopo e as suposições das quais as afirmações de segurança dependem.
- **`OPEN_ISSUES.md`** — limitações conhecidas, itens de roadmap rastreados e a lacuna
  na maquinaria de sessão em linguagem C.
- **`SECURITY.md`** — política de divulgação responsável.
- **`samples/Aether.Demo.Console/Program.cs`** — guia executável de ponta a ponta
  com 9 etapas. A Etapa 9 (MessagingService + DTN) é o padrão de integração para
  produção.
- **`fixtures/signal/`** — vetores de teste multilinguagem. Se você está portando
  o Aether para outra linguagem, estas são as saídas byte-fixadas que sua
  implementação deve corresponder.

Encontrou um bug? Registre no GitHub. Encontrou uma vulnerabilidade? Veja `SECURITY.md`.
