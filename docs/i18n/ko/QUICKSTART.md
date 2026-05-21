# 빠른 시작 — 5분 안에 .NET 앱에 Aether 연동하기

이 가이드는 빈 `Program.cs`에서 시작하여 종단 간 암호화 메시지를 교환하는 두 노드(Alice와 Bob)를 만들어 줍니다. 모든 코드는 .NET 10의 [`bhengubv/aether-protocol`](../) HEAD(`b8b3d22`)를 기준으로 컴파일됩니다.

> 전체 아키텍처가 필요하십니까? [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md)를 참조하십시오.
> 암호화가 무엇을 보호하고 무엇을 보호하지 않는지 알고 싶으십니까? [`THREAT_MODEL.md`](THREAT_MODEL.md)를 참조하십시오. 알려진 제한 사항은
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md)에서 추적됩니다.

---

## 1. 설치

Aether 라이브러리는 아직 NuGet에 게시되지 않았습니다. 현재는
로컬 저장소에 `<ProjectReference>`를 사용하십시오:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/Aether.DependencyInjection/Aether.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/Aether.Storage/Aether.Storage.csproj" />
</ItemGroup>
```

`Aether.DependencyInjection`은 `Aether.Core`,
`Aether.Security`, `Aether.Messaging`, `Aether.Transport`, `Aether.Streaming`,
`Aether.Voice`, `Aether.Content`를 전이적으로 가져옵니다 — 메시지 스택에 필요한 모든 것입니다. `Aether.Storage`는 디스크 기반 영속성을 원하는 경우에만 필요한 별도 의존성입니다 (섹션 6 참조).

패키지가 NuGet에 게시되면 다음과 같이 됩니다:

```bash
dotnet add package Aether.DependencyInjection
dotnet add package Aether.Storage   # 선택 사항, 영속성을 위해
```

패키지 API는 프로젝트 참조 방식과 NuGet 방식 사이에서 변경되지 않습니다.

---

## 2. 연동 — 정식 전체 스택 등록

DI 확장 `AddAetherProtocol(...)`은 플루언트 빌더를 반환합니다. 각
기능은 옵트인 방식입니다: 라우팅만 필요한 호스트는 `.AddRouting()`만
체인하면 됩니다. 아래는 일반적인 채택자가 원하는 전체 스택입니다.

```csharp
using Aether.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // host-side prerequisite for AddHealthChecks() below
builder.Services
    .AddAetherProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (registers ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // AODV-style RREQ/RREP + InMemoryRouteStore
    .AddDtn()                                // 72h store-and-forward custody + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // emergency flood
    .AddMessaging()                          // 1-to-1 encrypted messages, requires AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // in-memory simulator (replace with BLE / Wi-Fi Direct in production)
    .AddHealthChecks();                      // four protocol-level IHealthCheck registrations

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherProtocol`과 체인된 모든 메서드는 동일한 `IServiceCollection`에서
멱등성이 있습니다 — 두 번 호출해도 이중 등록이 되지 않습니다. 순서가 중요한
곳은 한 곳입니다: `AddSignalProtocol()` 또는 `AddRouting()` 중 하나라도
먼저 호출되지 않으면 `AddMessaging()`은 `InvalidOperationException`을 발생시킵니다.

`InProcessTransport`는 테스트 및 데모용입니다. 프로덕션에서는 물리적 계층
(BLE GATT, Wi-Fi Direct, NearLink, LoRa, …)을 위해
`Aether.Transport.Abstractions.ITransportService`를 구현하고, 패킷을 해당 계층으로
연결하는 `IMeshSender`를 등록합니다. 그 위에서 라우팅/DTN/메시지 서비스가 변경 없이 실행됩니다.

---

## 3. 세션 수립

X3DH는 비대칭입니다. **개시자**는 **응답자**가 게시한 번들을 처리합니다;
응답자의 세션은 개시자의 첫 번째 암호화 메시지 ("PreKey 메시지")를 수신하면
자동으로 수립됩니다.

```csharp
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle: identity key + signed pre-key + one one-time pre-key.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice processes the bundle. Four X25519 DHs run; the resulting root key
// seeds her Double Ratchet sending chain.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — auto-establishes on first received message
```

`PreKeyBundle`은 일반 DTO입니다. 호스트는 원하는 방식으로 게시할 수 있습니다 —
메시를 통해 직접 피어 간으로 (`PreKeyRequest` / `PreKeyResponse` 패킷 타입,
PROTOCOL_SPEC §2.5 참조), 백엔드 디렉터리를 통해, 또는 직접 전달. 프로토콜은
번들 전송 방식을 강제하지 않습니다.

---

## 4. 전송 및 수신

가장 짧은 종단 간 경로 (DI 없음, 라우팅 없음, 암호화만):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Wire the ciphertext over your transport. On Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

프로덕션에서는 암호문을 `MeshPacket`에 래핑하고, `PacketSigningService.SignPacketAsync`로
서명하고, `MessagingService.SendAsync`가 라우팅, 재시도, DTN 폴백을 처리하도록 합니다:

```csharp
using Aether.Messaging;
using Aether.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent has already been decrypted by the messaging layer.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> ciphertext exited via the mesh, DTN, or backend relay
// handed == false -> queued in the outbox; ProcessOutboxAsync will retry
```

수신자와 Signal 세션이 아직 없을 때 `MessagingService`는 메시지를 큐에 저장합니다 —
절대 평문으로 전송하지 않습니다. 피어의 사전 키 번들을 가져와
`alice.ProcessPreKeyBundleAsync(...)`를 호출해야 하는 시점을 알려면 `SessionRequired`를
구독하십시오.

---

## 5. 50줄로 구현하는 양방향 왕복

실행 가능한 스크립트입니다. `Program.cs`에 복사하고, `Aether.Security.csproj`에
`<ProjectReference>`를 추가한 후 (`Aether.Core`와 BCL 암호화를 가져옵니다),
`dotnet run`을 실행하십시오.

```csharp
using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publishes a bundle; Alice processes it. After this, Alice can encrypt
// to Bob; Bob's session auto-establishes when he decrypts Alice's first
// message (which carries X3DH metadata as a "PreKey message").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Production: serialize `outbound` (or wrap in a MeshPacket and call
// PacketSigningService.SignPacketAsync) and ship the bytes over your
// transport. The receiver reconstructs the EncryptedPayload and calls
// DecryptAsync. Here both nodes share a process so we just hand the
// record across.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (session is now live in both directions) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

예상 출력:

```
Bob got: "hello bob"
Alice got: "ack"
```

패킷 서명, Charlie를 통한 다중 홉 중계, MessagingService, DTN 보관 폴백을 포함하는
더 풍부한 종단 간 데모를 위해 번들된 콘솔을 실행하십시오:

```bash
dotnet run --project samples/Aether.Demo.Console
```

DTN 보관 단계 (데모의 9단계)는 프로덕션 연동의 정석 패턴입니다:
실제 전송 위의 `IMeshSender` 어댑터에 대해 `MessagingService` + `RoutingService` + `DtnService`를 구성합니다.

---

## 6. 영속성 (키-값 저장소)

기본적으로 `SignalProtocolService`는 모든 세션, 신원 키, 서명된 사전 키,
1회용 사전 키를 프로세스 메모리에 보관합니다. 크래시 발생 시: 신원 손실
(이전 세션 복호화 불가), OPK 풀 손실 (새 개시자의 응답자 X3DH가 실패하기 시작),
Double Ratchet 상태 손실 (전달 비밀성은 유지되나 메시지 순서가 깨짐).

`Aether.Storage.FileSystemKeyValueStore`는 최소한의 디스크 기반
`IKeyValueStore`입니다 (항목당 파일 하나, 원자적 임시 파일 이름 변경). `KeyValue*Store`
어댑터를 통해 연결하십시오:

```csharp
using Aether.Storage;
using Aether.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Plug the same KV store into BOTH adapters so identity, sessions, and
// pre-keys all survive a restart.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore is internal — KeyValueSignalSessionStore is also internal.
// In a Wave-3+ host, register the persistent-state-aware SignalProtocolService
// constructor through your composition root (or replace the default
// AddSignalProtocol() registration with your own factory).
```

`FileSystemKeyValueStore`는 의도적으로 단순합니다: 압축 없음, 교차 키 트랜잭션 없음,
저장 시 암호화 없음. 저장 시 암호화를 위해서는 파일 시스템 (또는 자체 KV) 위에
`EncryptedKeyValueStore`를 레이어링하고 `IDataAtRestKeyProvider`를 제공하십시오 —
호스트가 키 래퍼를 소유하며, 프로토콜은 소유하지 않습니다.

`.AddRouting()` / `.AddDtn()` / `.AddMessaging()`을 체인하기 전에
DI 컨테이너에 기본값이 아닌 `IRouteStore`, `IDtnBundleStore`, `IMessageStore`를
등록할 수도 있습니다 — 빌더는 `TryAdd*`를 사용하고 컨테이너에 먼저 넣은 것을
존중합니다. `Aether.Storage`의 `KeyValueRouteStore`, `KeyValueDtnBundleStore`,
`KeyValueMessageStore` 어댑터는 모든 `IKeyValueStore`에 대해 해당 슬롯을 커버합니다.

---

## 7. 관찰 가능성

Aether는 1급 OpenTelemetry 계측을 제공합니다. 하나의 미터와 하나의 활동 소스를 구독하십시오 — 두 가지 모두 안정적인 문자열이며 라이브러리는 특정 OTel SDK에 의존하지 않습니다:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Aether.Protocol"))
    .WithTracing(t => t.AddSource("Aether.Protocol"));
```

제공되는 항목:

- **카운터**: `aether.messages.encrypted`, `aether.messages.decrypted`,
  `aether.signatures.validated`, `aether.signatures.rejected`,
  `aether.nonces.replayed`, `aether.timestamps.stale`,
  `aether.sessions.established`, `aether.ratchet.dh_steps`,
  `aether.route.requests_emitted`, `aether.route.replies_received`,
  `aether.route.cache_hits`, `aether.dtn.bundles_accepted`,
  `aether.dtn.bundles_delivered`, `aether.dtn.bundles_expired`,
  `aether.sos.broadcasts`, `aether.sos.rebroadcasts_suppressed`,
  `aether.messaging.messages_sent`, `aether.messaging.messages_queued`,
  `aether.messaging.dtn_fallback`.
- **히스토그램** (ms): `aether.encrypt.latency`, `aether.decrypt.latency`,
  `aether.route.lookup_latency`, `aether.sign.verify_latency`.
- **PII가 삭제된 UHID 태그가 있는 활동**:
  `Aether.Encrypt`, `Aether.Decrypt`, `Aether.DhRatchet.Step`,
  `Aether.Sign.Packet`, `Aether.Verify.Packet`, 라우팅 및 DTN 스팬 포함.

리스너가 연결되지 않은 경우 핫 패스에서 아무것도 할당하지 않습니다 — 카운터 `Add`는
휘발성 읽기로 저하되고 `StartActivity`는 `null`을 반환합니다.

전체 계측 목록과 PII 계약은
`src/Aether.Core/Diagnostics/AetherTelemetry.cs`에 있습니다.

---

## 8. 헬스 체크

`AddHealthChecks()` (Aether 빌더 메서드)는 호스트의 `HealthCheckService`에
4개의 프로토콜 수준 체크를 등록합니다. 각 체크는 대시보드에 유용한 구조화된 `data`를 씁니다.

| 체크 이름 | 모니터링 항목 | 정상 → 저하 → 비정상 |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10,000 → ≥ 10,000 → ≥ 50,000 (기본값; 조정 가능)             |
| `aether-dtn`                | 보관 중인 활성 번들                                          | 용량의 80% 미만 → ≥ 80% → ≥ `DtnMaxBundlesPerNode`              |
| `aether-signal`             | 사용 가능한 OPK + 활성 세션 수                               | OPK 하한 → `MinAvailableOpks` (기본 10) 미만 시 비정상; 세션 상한 → 1,000 초과 시 저하 |
| `aether-messaging-outbox`   | 대기 중인 발신함 깊이 + 샘플 간 증가량                        | < 100 → ≥ 100 → ≥ 100이면서 증가 중                             |

`AetherOptions.Routing`, `Dtn`, `Signal`, `Messaging` 속성으로 조정하십시오. 호스트는
`MapHealthChecks(...)`에서 등록이 보이려면 Aether 빌더의 `.AddHealthChecks()` 전에
`services.AddHealthChecks()`를 호출해야 합니다.

---

## 9. 다음 단계

- **`docs/PROTOCOL_SPEC.md`** — 와이어 형식, 라우팅, 키 교환, DTN, 전체
  패킷 타입 테이블, 정식 `BuildSignableData` 알고리즘.
- **`docs/THREAT_MODEL.md`** — 암호화가 방어하는 것, 명시적으로 범위 밖인 것,
  보안 주장이 의존하는 가정.
- **`OPEN_ISSUES.md`** — 알려진 제한 사항, 추적된 로드맵 항목,
  C 언어 세션 메커니즘 격차.
- **`SECURITY.md`** — 책임 있는 공개 정책.
- **`samples/Aether.Demo.Console/Program.cs`** — 실행 가능한 9단계 종단 간
  워크스루. 9단계 (MessagingService + DTN)가 프로덕션 연동 패턴입니다.
- **`fixtures/signal/`** — 언어 간 테스트 벡터. Aether를 다른 언어로 포팅하는 경우,
  구현체가 일치해야 하는 바이트 고정 출력입니다.

버그를 발견하셨습니까? GitHub에 신고해 주십시오. 취약점을 발견하셨습니까? `SECURITY.md`를 참조하십시오.
