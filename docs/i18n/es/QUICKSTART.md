# Inicio Rápido — integra Aether en tu aplicación .NET en 5 minutos

Esta guía te lleva desde un `Program.cs` en blanco hasta dos nodos — Alice y Bob —
intercambiando un mensaje cifrado de extremo a extremo. Todo compila contra HEAD
(`b8b3d22`) de [`bhengubv/aether-protocol`](../) en .NET 10.

> ¿Buscas la arquitectura completa? Consulta [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md).
> ¿Buscas qué protege y qué no protege la criptografía? Consulta
> [`THREAT_MODEL.md`](THREAT_MODEL.md). Las limitaciones conocidas se rastrean en
> [`OPEN_ISSUES.md`](../OPEN_ISSUES.md).

---

## 1. Instalación

Las bibliotecas Aether aún no están publicadas en NuGet. Por ahora, usa una
`<ProjectReference>` al repositorio local:

```xml
<ItemGroup>
  <ProjectReference Include="../aether-protocol/src/AetherNet.DependencyInjection/AetherNet.DependencyInjection.csproj" />
  <ProjectReference Include="../aether-protocol/src/AetherNet.Storage/AetherNet.Storage.csproj" />
</ItemGroup>
```

`AetherNet.DependencyInjection` incluye transitivamente `AetherNet.Core`,
`AetherNet.Security`, `AetherNet.Messaging`, `AetherNet.Transport`, `AetherNet.Streaming`,
`AetherNet.Voice` y `AetherNet.Content` — todo lo que necesitas para el stack de mensajería.
`AetherNet.Storage` es una dependencia separada solo si quieres persistencia en disco
(ver Sección 6).

Una vez que el paquete se publique en NuGet, esto se convierte en:

```bash
dotnet add package AetherNet.DependencyInjection
dotnet add package AetherNet.Storage   # opcional, para persistencia
```

Las API del paquete no cambiarán entre el flujo de referencia de proyecto y el
flujo de NuGet.

---

## 2. Configuración — registro completo del stack canónico

La extensión de DI `AddAetherNetProtocol(...)` devuelve un builder fluido. Cada
capacidad es opt-in: un host que solo necesita enrutamiento encadena `.AddRouting()`
y se detiene ahí. A continuación se muestra el stack completo que un adoptante típico necesita.

```csharp
using AetherNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

const string LocalUhid = "aether:alice:01";

builder.Services.AddHealthChecks();          // prerequisito del lado del host para AddHealthChecks() a continuación
builder.Services
    .AddAetherNetProtocol(opts => opts.LocalUhid = LocalUhid)
    .AddSignalProtocol()                     // X3DH + Double Ratchet (registra ISignalProtocolService, IPacketSigningService)
    .AddRouting()                            // RREQ/RREP estilo AODV + InMemoryRouteStore
    .AddDtn()                                // custodia store-and-forward de 72h + InMemoryDtnBundleStore
    .AddSosBroadcast()                       // inundación de emergencia
    .AddMessaging()                          // mensajes cifrados 1 a 1, requiere AddSignalProtocol + AddRouting
    .AddInProcessTransport(LocalUhid)        // simulador en memoria (reemplazar con BLE / Wi-Fi Direct en producción)
    .AddHealthChecks();                      // cuatro registros IHealthCheck a nivel de protocolo

using var app = builder.Build();
await app.StartAsync();
```

`AddAetherNetProtocol` y cada método encadenado son idempotentes en el mismo
`IServiceCollection` — llamarlos dos veces no registra duplicados. El orden
importa en un punto: `AddMessaging()` lanza `InvalidOperationException` si
`AddSignalProtocol()` o `AddRouting()` no se llamaron primero.

El `InProcessTransport` es para pruebas y demos. En producción implementas
`AetherNet.Transport.Abstractions.ITransportService` para tu capa física (BLE
GATT, Wi-Fi Direct, NearLink, LoRa, …) y registras un `IMeshSender` que
transfiere paquetes a él. Los servicios Routing/DTN/Messaging se ejecutan entonces sin cambios
encima de él.

---

## 3. Establecer una sesión

X3DH es asimétrico. El **iniciador** procesa un bundle publicado por el
**respondedor**; la sesión del respondedor se establece automáticamente cuando recibe el
primer mensaje cifrado del iniciador (un "mensaje PreKey").

```csharp
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publica un bundle: clave de identidad + clave pre-firmada + una clave pre-key de un solo uso.
var bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");

// Alice procesa el bundle. Se ejecutan cuatro DHs X25519; la clave raíz resultante
// alimenta su cadena de envío Double Ratchet.
await alice.ProcessPreKeyBundleAsync(bobBundle);

Debug.Assert(alice.HasSession("aether:bob:02"));        // true
Debug.Assert(bob.HasSession("aether:alice:01") == false); // false — se establece automáticamente en el primer mensaje recibido
```

`PreKeyBundle` es un DTO simple. Los hosts lo publican como prefieran — directamente
peer-to-peer sobre la malla (tipos de paquete `PreKeyRequest` / `PreKeyResponse`,
ver PROTOCOL_SPEC §2.5), a través de un directorio backend o entregado manualmente. El
protocolo no impone un transporte para los bundles.

---

## 4. Enviar y recibir

La ruta de extremo a extremo más corta (sin DI, sin enrutamiento, solo el cifrador):

```csharp
using System.Text;

var ciphertext = await alice.EncryptAsync("aether:bob:02",
    Encoding.UTF8.GetBytes("The mesh is alive."));

// Transmite el ciphertext por tu transporte. En Bob:
var plaintext = await bob.DecryptAsync("aether:alice:01", ciphertext);
Console.WriteLine(Encoding.UTF8.GetString(plaintext)); // "The mesh is alive."
```

En producción envuelves el ciphertext en un `MeshPacket`, lo firmas con
`PacketSigningService.SignPacketAsync`, y dejas que `MessagingService.SendAsync`
gestione el enrutamiento, los reintentos y el fallback DTN:

```csharp
using AetherNet.Messaging;
using AetherNet.Messaging.Models;

var messaging = serviceProvider.GetRequiredService<IMessagingService>();

messaging.MessageReceived += (_, msg) =>
{
    // msg.EncryptedContent ya ha sido descifrado por la capa de mensajería.
    Console.WriteLine($"From {msg.SenderUhid}: {Encoding.UTF8.GetString(msg.EncryptedContent)}");
};

var outgoing = new MeshMessage { RecipientUhid = "aether:bob:02", MessageType = "text" };
var handed = await messaging.SendAsync(outgoing, Encoding.UTF8.GetBytes("hi from Alice"));
// handed == true  -> el ciphertext salió por la malla, DTN o relay backend
// handed == false -> en cola en la bandeja de salida; ProcessOutboxAsync lo reintentará
```

`MessagingService` pone en cola los mensajes — nunca los envía en texto claro — cuando
no existe todavía una sesión Signal con el destinatario. Suscríbete a `SessionRequired`
para saber cuándo obtener el bundle de pre-key de un peer y llamar a
`alice.ProcessPreKeyBundleAsync(...)`.

---

## 5. Viaje de ida y vuelta de dos nodos en 50 líneas

Este es un script ejecutable. Cópialo en `Program.cs`, agrega una `<ProjectReference>`
a `AetherNet.Security.csproj` (que incluye `AetherNet.Core` y la criptografía BCL),
y ejecuta `dotnet run`.

```csharp
using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;

var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
var bob   = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

// Bob publica un bundle; Alice lo procesa. Después de esto, Alice puede cifrar
// para Bob; la sesión de Bob se establece automáticamente cuando descifra el primer
// mensaje de Alice (que lleva metadatos X3DH como "mensaje PreKey").
PreKeyBundle bobBundle = await bob.GeneratePreKeyBundleAsync("aether:bob:02");
_ = await alice.GeneratePreKeyBundleAsync("aether:alice:01");
await alice.ProcessPreKeyBundleAsync(bobBundle);

// --- Alice -> Bob -----------------------------------------------------------
EncryptedPayload outbound = await alice.EncryptAsync(
    "aether:bob:02",
    Encoding.UTF8.GetBytes("hello bob"));

// Producción: serializa `outbound` (o envuélvelo en un MeshPacket y llama a
// PacketSigningService.SignPacketAsync) y envía los bytes por tu
// transporte. El receptor reconstruye el EncryptedPayload y llama a
// DecryptAsync. Aquí ambos nodos comparten un proceso así que solo pasamos
// el registro directamente.
byte[] plaintextBytes = await bob.DecryptAsync("aether:alice:01", outbound);
Console.WriteLine($"Bob got: \"{Encoding.UTF8.GetString(plaintextBytes)}\"");

// --- Bob -> Alice (la sesión ya está activa en ambas direcciones) ------------------
EncryptedPayload reply = await bob.EncryptAsync(
    "aether:alice:01",
    Encoding.UTF8.GetBytes("ack"));
byte[] replyPlain = await alice.DecryptAsync("aether:bob:02", reply);
Console.WriteLine($"Alice got: \"{Encoding.UTF8.GetString(replyPlain)}\"");
```

Salida esperada:

```
Bob got: "hello bob"
Alice got: "ack"
```

Para una demo de extremo a extremo más completa — incluyendo firma de paquetes, relay multi-salto
a través de Charlie, MessagingService y fallback de custodia DTN — ejecuta la consola incluida:

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

El paso de custodia DTN (Paso 9 de la demo) es el patrón canónico para
la configuración en producción: `MessagingService` + `RoutingService` + `DtnService`
compuestos contra un adaptador `IMeshSender` sobre el transporte real.

---

## 6. Persistencia (almacén clave-valor)

Por defecto, `SignalProtocolService` mantiene cada sesión, clave de identidad, clave
pre-firmada y clave pre-key de un solo uso en memoria del proceso. Un fallo significa:
identidad perdida (no se puede descifrar ninguna sesión anterior), pool OPK perdido (X3DH del respondedor
comienza a fallar para nuevos iniciadores), estado Double Ratchet perdido (el secreto hacia adelante
está intacto pero el orden de los mensajes se rompe).

`AetherNet.Storage.FileSystemKeyValueStore` es un `IKeyValueStore` mínimo respaldado en disco
(un archivo por entrada, renombrado atómico de archivo temporal). Conéctalo
a través de los adaptadores `KeyValue*Store`:

```csharp
using AetherNet.Storage;
using AetherNet.Security.Services;

var kv = new FileSystemKeyValueStore(
    rootDirectory: Path.Combine(AppContext.BaseDirectory, "aether-state"),
    @namespace: "alice");

// Conecta el mismo almacén KV a AMBOS adaptadores para que la identidad, las sesiones y
// las pre-keys sobrevivan a un reinicio.
var preKeys = new KeyValuePreKeyStore(kv);
// ISignalSessionStore es interno — KeyValueSignalSessionStore también es interno.
// En un host Wave-3+, registra el constructor SignalProtocolService con estado persistente
// a través de tu raíz de composición (o reemplaza el registro predeterminado
// AddSignalProtocol() con tu propia fábrica).
```

`FileSystemKeyValueStore` es intencionalmente simple: sin compactación, sin
transacciones entre claves, sin cifrado en reposo. Para cifrado en reposo superpone
`EncryptedKeyValueStore` sobre el sistema de archivos (o tu propio KV) y proporciona un
`IDataAtRestKeyProvider` — el host posee el envoltorio de clave, no el protocolo.

También puedes registrar un `IRouteStore`, `IDtnBundleStore` y
`IMessageStore` no predeterminados en el contenedor DI antes de encadenar
`.AddRouting()` / `.AddDtn()` / `.AddMessaging()` — el builder usa
`TryAdd*` y respeta lo que hayas puesto en el contenedor primero. Los
adaptadores `KeyValueRouteStore`, `KeyValueDtnBundleStore` y `KeyValueMessageStore`
en `AetherNet.Storage` cubren esos slots contra cualquier `IKeyValueStore`.

---

## 7. Observabilidad

Aether incluye instrumentación OpenTelemetry de primera clase. Suscríbete a un
medidor y una fuente de actividad — ambos son cadenas estables y las bibliotecas
no dependen de ningún SDK OTel específico:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("AetherNet.Protocol"))
    .WithTracing(t => t.AddSource("AetherNet.Protocol"));
```

Lo que obtienes:

- **Contadores**: `aethernet.messages.encrypted`, `aethernet.messages.decrypted`,
  `aethernet.signatures.validated`, `aethernet.signatures.rejected`,
  `aethernet.nonces.replayed`, `aethernet.timestamps.stale`,
  `aethernet.sessions.established`, `aethernet.ratchet.dh_steps`,
  `aethernet.route.requests_emitted`, `aethernet.route.replies_received`,
  `aethernet.route.cache_hits`, `aethernet.dtn.bundles_accepted`,
  `aethernet.dtn.bundles_delivered`, `aethernet.dtn.bundles_expired`,
  `aethernet.sos.broadcasts`, `aethernet.sos.rebroadcasts_suppressed`,
  `aethernet.messaging.messages_sent`, `aethernet.messaging.messages_queued`,
  `aethernet.messaging.dtn_fallback`.
- **Histogramas** (ms): `aethernet.encrypt.latency`, `aethernet.decrypt.latency`,
  `aethernet.route.lookup_latency`, `aethernet.sign.verify_latency`.
- **Actividades** con etiquetas UHID saneadas de PII:
  `AetherNet.Encrypt`, `AetherNet.Decrypt`, `AetherNet.DhRatchet.Step`,
  `AetherNet.Sign.Packet`, `AetherNet.Verify.Packet`, más spans de enrutamiento y DTN.

Cuando no hay ningún listener adjunto, las rutas calientes no asignan nada — el contador `Add`
degrada a una lectura volátil y `StartActivity` devuelve `null`.

El inventario completo de instrumentos y el contrato PII viven en
`src/AetherNet.Core/Diagnostics/AetherNetTelemetry.cs`.

---

## 8. Comprobaciones de salud

`AddHealthChecks()` (el método builder de Aether) registra cuatro comprobaciones a nivel de protocolo
contra el `HealthCheckService` del host. Cada una escribe `data` estructurada
útil para dashboards.

| Nombre de la comprobación | Lo que monitorea | Saludable → Degradado → No saludable |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------|
| `aether-routing`            | `IRoutingService.GetAllRoutes().Count`                     | < 10 000 → ≥ 10 000 → ≥ 50 000 (predeterminados; ajustables)   |
| `aether-dtn`                | bundles activos en custodia                                 | < 80% capacidad → ≥ 80% → ≥ `DtnMaxBundlesPerNode`             |
| `aether-signal`             | OPKs disponibles + conteo de sesiones activas              | umbral OPK → no saludable por debajo de `MinAvailableOpks` (predeterminado 10); techo de sesiones → degradado por encima de 1 000 |
| `aether-messaging-outbox`   | profundidad de bandeja de salida pendiente + crecimiento entre muestras | < 100 → ≥ 100 → ≥ 100 Y en crecimiento |

Ajusta mediante los bags `AetherNetOptions.Routing`, `Dtn`, `Signal` y `Messaging`. El
host debe llamar a `services.AddHealthChecks()` antes del `.AddHealthChecks()` del builder de Aether
para que los registros sean visibles para `MapHealthChecks(...)`.

---

## 9. Qué sigue

- **`docs/PROTOCOL_SPEC.md`** — formato de cable, enrutamiento, intercambio de claves, DTN, tabla
  completa de tipos de paquetes y el algoritmo canónico `BuildSignableData`.
- **`docs/THREAT_MODEL.md`** — contra qué defiende la criptografía, qué está
  explícitamente fuera del alcance y los supuestos en los que se basan las afirmaciones de seguridad.
- **`OPEN_ISSUES.md`** — limitaciones conocidas, elementos rastreados del roadmap y la
  brecha en la maquinaria de sesión del lenguaje C.
- **`SECURITY.md`** — política de divulgación responsable.
- **`samples/AetherNet.Demo.Console/Program.cs`** — recorrido ejecutable de extremo a extremo en 9 pasos.
  El Paso 9 (MessagingService + DTN) es el patrón de configuración en producción.
- **`fixtures/signal/`** — vectores de prueba multilenguaje. Si estás portando
  Aether a otro lenguaje, estas son las salidas fijadas a bytes que tu
  implementación debe producir.

¿Encontraste un error? Regístralo en GitHub. ¿Encontraste una vulnerabilidad? Consulta `SECURITY.md`.
