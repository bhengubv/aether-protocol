# AetherNet — protocolo de red mesh offline-first

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet es un protocolo de red mesh de código abierto con licencia MIT** para enviar mensajes, archivos, voz y video a personas cercanas — **sin internet, sin servidores y sin registro**. Los dispositivos se conectan directamente por Bluetooth, Wi-Fi Direct, NearLink y LoRa; cuando el destinatario está fuera de alcance, los mensajes saltan a través de otros dispositivos y esperan hasta 72 horas para encontrar una ruta. Se distribuye con **implementaciones byte por byte idénticas en ocho lenguajes de programación** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift y C.

Comparte archivos, mensajes y streams con personas cercanas. Sin WiFi. Sin datos móviles. Sin registro. Como AirDrop, excepto que funciona con todos, en todas las plataformas.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Un protocolo, ocho lenguajes, idéntico a nivel de cable.** Aether está implementado en **C#, Rust, TypeScript, Python, Go, Kotlin, Swift y C** — y cada paquete es byte por byte idéntico en todos ellos, garantizado por un corpus de fixtures multilenguaje compartido en CI. Construye tu nodo en cualquiera de los ocho; interopera con todos los demás. Este README también está disponible en 11 idiomas humanos (enlaces arriba).

## ¿Qué puedes hacer con él?

**Comparte apuntes de clase sin gastar datos.**

Estás en un grupo de estudio. Alguien tiene exámenes anteriores en su teléfono. Aether los envía directamente a tu dispositivo por Bluetooth — sin punto de acceso, sin grupo de WhatsApp, sin límite de tamaño de archivo. Si alguien del grupo está fuera del alcance, el archivo salta a través de otros dispositivos hasta llegar a ellos. Los mensajes esperan hasta 72 horas para encontrar una ruta si es necesario.

```
  [Tú] ──BLE──▶ [Amigo] ──WiFi──▶ [Amigo del Amigo]
    notes.pdf           retransmitido, cifrado
```

**Entérate de lo que pasa a tu alrededor.**

Estás en un evento universitario o un festival. Aether descubre otros dispositivos cercanos por Bluetooth y WiFi Direct — sin feed de aplicación, sin algoritmo. Ves lo que está realmente a tu alrededor, no lo que está promocionado.

**Envía un SOS cuando no hay señal.**

Tu teléfono no tiene cobertura. Aether difunde un mensaje de emergencia a todos los dispositivos en rango, y esos dispositivos lo transmiten. No se necesita torre celular.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Inundación: llega a todos los dispositivos en rango
```

**Crea canales de grupo privados.**

Un canal para tu piso del dormitorio, tu asociación, tu equipo de proyecto. Solo los miembros verificados pueden leer o enviar mensajes. Ningún servidor almacena la conversación.

**Vende cosas a personas cercanas.**

Publica un libro de texto en venta. Las personas que pasen por el rango de la malla lo verán. Sin cuenta en un marketplace, sin tarifas de publicación — solo proximidad.

**Ve una película juntos, a través de la malla.**

Tu grupo hace una noche de cine. Alguien tiene el archivo. Aether sincroniza la reproducción en todos los dispositivos — play, pausa, avance — todo en perfecta sincronía. Si solo algunos tienen el archivo, la malla lo distribuye en tiempo real como un stream P2P. Todos contribuyen mediante SDPKT para comprarlo si nadie lo tiene.

## Cómo funciona

Los dispositivos se comunican directamente entre sí usando Bluetooth, WiFi Direct o NearLink. Sin conexión a internet, sin servidor, sin infraestructura central.

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

Cuando un mensaje no puede llegar a su destino directamente, salta a través de otros dispositivos. Esos dispositivos de retransmisión no pueden leer lo que transportan — cada mensaje está cifrado con AES-256-GCM. Cada paquete está firmado con claves de identidad Ed25519, y los paquetes falsificados son descartados por la red.

> **Nota de madurez de seguridad (leer antes de desplegar):** X3DH real (4 DHs X25519), el Double Ratchet completo de Signal (paso de rotación DH al recibir, KDF_RK, cadena ratchet 0x01/0x02), y el pool de claves pre-key de un solo uso (100 OPKs por defecto, FIFO, protegido con bloqueo) están implementados en **los 8 lenguajes** y anclados a un corpus de fixtures compartido multilenguaje en `fixtures/signal/`. El único punto pendiente es la puesta en marcha de RF físico en hardware BLE real (rastreado en `OPEN_ISSUES.md`).

Sin cuentas, sin números de teléfono, sin correos electrónicos. Generas un par de claves y ya estás en la red.

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

**Enrutamiento** — AODV con respuestas de ruta firmadas. Cada respuesta de ruta está firmada con la clave Ed25519 del destino, por lo que ningún dispositivo puede pretender ser un destino que no es.

**Almacenamiento y reenvío** — Cuando no hay ruta activa, los paquetes se retienen hasta 72 horas hasta que se abre un camino.

**Selección de transporte** — El protocolo elige el transporte adecuado por paquete. Los mensajes de control pequeños van por BLE. Las transferencias masivas usan WiFi Direct. NearLink cuando está disponible.

**Voz, video y streaming** — Videollamadas con negociación de códec (H.264/H.265/VP8), selección de calidad adaptada al transporte, video grupal con retransmisión SFU automática, sincronización de Watch Together con compensación de RTT y streaming de bitrate adaptativo.

**Protección contra repetición** — Deduplicación de nonces con una ventana de frescura de marca de tiempo de 5 minutos.

## Lo que obtienes — cada servicio, en cada lenguaje

Aether no es solo un transporte. Cada tipo de paquete reservado por el protocolo es ahora un **servicio real y funcional en los 8 lenguajes**, y cada uno se serializa a **paquetes de cable byte-idénticos** — un paquete construido por el nodo Go es decodificado, sin cambios, por el nodo Swift, Rust, C, Python, TypeScript, Kotlin o C#. Cada servicio está anclado a un fixture multilenguaje compartido en `fixtures/<service>/` y ejercitado por pruebas unitarias por lenguaje, con Swift y C verificados adicionalmente en el servidor de compilación macOS.

| Capacidad | Qué hace | Tipo(s) de paquete | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Baliza y consulta de presencia** | Anuncia "estoy aquí" y pregunta "¿quién anda cerca?" — mediante un **ID efímero rotativo derivado de clave** (no tu identidad real) más un geohash aproximado | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Keep-alive ligero de vida entre peers enlazados | 10 | `fixtures/heartbeat/` | ✅ |
| **Sincronización de perfil** | Intercambia una tarjeta de perfil firmada con un peer a través de la malla | 23 | `fixtures/profiles/` | ✅ |
| **Anuncio de ID efímero** | Comunica de forma privada a un amigo tu ID de enrutamiento rotativo actual para que aún pueda alcanzarte después de que rote | 56 | `fixtures/erid/` | ✅ |
| **Intercambio de pre-key** | Solicita y entrega un bundle de pre-key de Signal a través de la malla, para arrancar una sesión de extremo a extremo con alguien que nunca has conocido | 25, 26 | `fixtures/prekey/` | ✅ |
| **Canales** | Mensajes firmados a un canal de grupo privado, solo para miembros | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Frames de voz tipo walkie-talkie (payload de audio codificado opaco) | 15 | `fixtures/media/` | ✅ |
| **Compartir pantalla** | Frames de video de compartir pantalla (payload de video codificado opaco) | 32 | `fixtures/media/` | ✅ |
| **Control de llamada** | Señalización de timbre / aceptar / rechazar / colgar para llamadas de voz y video | 27 | `fixtures/videocall/` | ✅ |
| **Confirmación de SOS** | Confirma al emisor que su difusión de emergencia fue recibida | 6 | `fixtures/sos/` | ✅ |
| **Migas de espacio** | Migas de descubrimiento etiquetadas por ubicación para la capa de "qué hay a mi alrededor" | 40 | `fixtures/space/` | ✅ |
| **Anuncio de forja** | Anuncia a la malla un artefacto de contenido derivado/forjado | 41 | `fixtures/forge/` | ✅ |
| **Solicitud de shard de bóveda** | Obtiene un shard de almacenamiento codificado por borrado (cualquier K de N shards reconstruye el archivo) | 42 | `fixtures/vaultshard/` | ✅ |
| **Medición de ancho de banda** | Sondea / confirma / difunde el rendimiento del enlace para que la malla enrute por la tubería más ancha (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Estos se apoyan sobre los servicios ya completos de **mensajería, voz 1 a 1 y grupal, videollamadas, streaming en vivo, Watch Together, enrutamiento AODV, DTN store-and-forward y difusión de inundación SOS** — también implementados en los 8 lenguajes.

> **Qué significa "construido" aquí, con precisión.** Cada servicio produce y maneja su paquete de cable, dispara los eventos correctos y está anclado a un fixture a nivel de byte que toda la familia de lenguajes debe cumplir. Tu aplicación conecta el servicio a su sesión Signal, tabla de enrutamiento y estado local. Esta es la capa de protocolo — probada en código, pruebas y fixtures de byte multilenguaje — sobre la misma base honesta de RF que todo lo demás: cualquier camino que en última instancia viaje sobre una radio está sin verificar en campo hasta la puesta en marcha de hardware rastreada en `OPEN_ISSUES.md`.

## Seguridad y privacidad

Más allá del conjunto de servicios de cable, Aether incluye una pequeña **capa de seguridad y privacidad** — gestión de claves de identidad y protección anti-rastreo en la capa de enlace. Como todo lo demás, cada una está implementada en **los 8 lenguajes** y anclada a un fixture multilenguaje compartido en `fixtures/<feature>/` (Swift y C verificados adicionalmente en el servidor de compilación macOS). Estos *no* son cuatro servicios de cable más de los 18: tres no definen **ningún nuevo tipo de paquete de cable** en absoluto, y el cuarto lleva sus propios sobres **dentro del camino DTN/malla existente** en lugar de como un nuevo paquete reservado.

| Capacidad | Qué hace | Capa | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Respaldo por frase de recuperación** | Respalda una identidad como una frase **BIP-39 de 24 palabras** y la restaura en cualquier dispositivo. BIP-39 estándar (verificado contra los vectores oficiales de Trezor), con suma de verificación SHA-256 de modo que una palabra mal escrita es *rechazada*, nunca silenciosamente incorrecta. Sin servidor, sin custodio — la frase **es** la identidad. | local | `fixtures/bip39/` | ✅ |
| **Protección anti-rastreo Bluetooth** | Deriva un **UUID de servicio** BLE rotativo, derivado de clave (HMAC-SHA256, ventana de 15 minutos) y **direcciones privadas resolubles** (IRK + la función RFC `ah`, AES-128) — el material anti-rastreo que un anunciante BLE necesita para que un escáner pasivo no pueda vincularlo a lo largo del tiempo o del lugar. | capa de enlace | `fixtures/bleprivacy/` | ✅ |
| **Borrado de pánico** | Un **PIN de coacción** (SHA-256, comparado en tiempo constante) que, bajo coacción, borra de forma segura cada clave de identidad — sobrescribir con aleatorio y luego poner a cero — sin dejar nada por recuperar. | local | `fixtures/panicwipe/` | ✅ |
| **Sincronización multidispositivo** | Sincronización **descentralizada, sin servidor** entre tus *propios* dispositivos: un **DeviceLink** firmado con Ed25519 los empareja, y sobres **SyncRecord** de último-en-escribir-gana reconcilian el estado — transportados cifrados de extremo a extremo sobre el DTN/malla existente, sin cuenta en la nube ni servidor de sincronización. | sobre DTN | `fixtures/sync/` | ✅ |

**Una asimetría honesta.** El `DeviceLink` multidispositivo está firmado con Ed25519, y esa firma es **byte-idéntica en 7 de los 8 lenguajes**. CryptoKit de Apple *aleatoriza* deliberadamente las firmas Ed25519, así que en Swift los 64 bytes de firma difieren cada vez — pero el **cuerpo firmado es byte-idéntico** y cada enlace se verifica igualmente en los 8 SDK, de modo que Swift alcanza paridad de **verificación** en lugar de paridad de bytes de firma. Eso es una propiedad de la criptografía de la plataforma, no un defecto, y es el único lugar en estas cuatro funcionalidades donde "byte-idéntico" lleva un asterisco. Los formatos de cable completos están en [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12; el modelo de amenazas está en [`THREAT_MODEL.md`](THREAT_MODEL.md).

## Transportes

Cada transporte tiene un nombre de color utilizado en todo el código fuente. `IsAvailable` bloquea los caminos impedidos por hardware — el `TransportManager` los omite y recurre al siguiente transporte disponible.

**Clave de estado:** ✅ real, construido y verificado · ⏳ real, verificación en curso · ⚠️ real en algunas plataformas, stub en otras · ❌ stub (aún sin código de transporte).

| Color | Nombre | Alcance | Ancho de banda | Estado |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Real — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Real — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | Relay HTTP / QUIC | Ilimitado | ~10 Mbps | ✅ Real — Windows; servidor de relay en `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Canal de datos de internet | Ilimitado | ~100 Mbps | ✅ Real en los 8 lenguajes — **verificado en loopback en los 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust: cada uno hace que dos peers intercambien bytes sobre un canal de datos ICE real) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Real en Android (`android/white/`); Windows = BLE-GATT real + aproximación de proximidad RSSI −40 dBm (`WinNfcBleTransportService`, compila net9/10, sin verificar en runtime) — `Windows.Networking.Proximity` eliminado en Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Real en HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — pendiente de verificación en dispositivo); Android + Windows = aproximación SSAP-sobre-BLE real (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; compilación + pruebas unitarias verificadas, sin verificar en runtime) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Driver serial RYLR SX127x/SX126x real (`LoRaSerialTransport` en C#/Go/Rust/C; compila, sin verificar en runtime — necesita un módulo físico); el puente BLE Coded-PHY sigue siendo un diseño documentado |

Los transportes de radio son reales solo donde existe código de plataforma (C#/Windows, Kotlin/Android, HarmonyOS). Las ocho bibliotecas de lenguaje, por lo demás, incluyen un transporte de **simulación en proceso** para pruebas — **WebRTC es el primer transporte real común a todas ellas** (completo; verificado en loopback en todos los lenguajes).

La prioridad se basa en el costo de energía: se prefiere la malla de radio, luego WebRTC como camino directo a internet, con el relay HTTP/QUIC como último recurso.

## Niveles de despliegue

Aether funciona en cualquier plataforma que soporte Bluetooth o Wi-Fi. El nivel en el que te encuentras depende del sistema operativo que uses como objetivo.

---

### Nivel estándar — cualquier plataforma

Android · Windows · Linux · macOS · iOS

Aether se ejecuta en cualquier dispositivo con hardware Bluetooth o Wi-Fi. Cuando una radio está físicamente ausente, cada transporte bloqueado se aproxima usando lo que está disponible. Estas aproximaciones son ahora **código real** (verificado en compilación; **sin verificar en runtime** a la espera de una prueba de RF con 2 dispositivos / hardware):

- **NearLink (Aether Teal)** — aproximación SSAP-sobre-BLE-GATT real (UUID de Aether SLE `61657468-6572-0003-…`) en Android (`android/teal/AetherNetSleService`) y Windows (`WinNearLinkBleTransportService`); compilación + pruebas unitarias verificadas, sin verificar en runtime. La radio NearLink real existe solo en HarmonyOS (`harmonyos/teal/`, pendiente de verificación en dispositivo).
- **LoRa (Aether Red)** — driver serial RYLR SX127x/SX126x real (`LoRaSerialTransport` en **los 8 lenguajes** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; cada port verificado en compilación, incluidos Swift + C en el servidor de compilación Mac; sin verificar en runtime — necesita un módulo físico). El puente Meshtastic-sobre-BLE-Coded-PHY (~1.3 km) sigue siendo un diseño documentado; el LoRa real de largo alcance necesita un nodo con capacidad LoRa (gateway, SBC o teléfono resistente con un módulo LoRa).
- **NFC (Aether White)** — real en Android (HCE). Windows ahora tiene una aproximación de proximidad BLE-GATT + RSSI −40 dBm real (`WinNfcBleTransportService`, compila net9/10; sin verificar en runtime); ACR122U PC/SC cuando hay un lector presente.

Lo que es real e idéntico en todas partes: **BLE, Wi-Fi Direct, el relay HTTP/QUIC y el transporte WebRTC P2P (verificado en loopback en los 8 lenguajes)**, más la seguridad de Signal Protocol (X3DH + Double Ratchet), enrutamiento AODV, DTN store-and-forward, difusión SOS, voz y streaming.

**Estado honesto:** BLE + Wi-Fi Direct + relay son reales para producción; **WebRTC P2P es real y verificado en loopback en los 8 lenguajes** (dos peers intercambian bytes sobre un canal de datos ICE real — Rust confirmado en la máquina Linux `.201` con ICE UDP funcionando); las aproximaciones NearLink / LoRa / NFC-en-Windows son ahora código real que compila (LoRa verificado en compilación en los 8, incl. Swift + C en el servidor de compilación Mac; NearLink-Android también con pruebas unitarias) pero está **sin verificar en runtime** — aún sin prueba de RF con hardware / 2 dispositivos. Participan en la malla en código; no despliegues esos tres esperando RF probada en campo.

---

### Nivel nativo — CircleOS / OpenHarmony

CircleOS · HarmonyOS · cualquier sistema operativo basado en OpenHarmony

CircleOS está construido sobre OpenHarmony, que incluye silicio NearLink (SLE) y el SDK `@kit.NearLinkKit` como capacidad de primera clase del sistema operativo. En dispositivos CircleOS y HarmonyOS con hardware NearLink, no se necesita ninguna aproximación — `harmonyos/teal/` usa la radio SLE real directamente:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Esto no es solo una versión mejorada del nivel estándar. En la capa NearLink es una red categorialmente diferente:

| Capacidad | Nivel estándar (aproximación BLE) | Nivel nativo (CircleOS / OpenHarmony) |
|---|---|---|
| **Alcance NearLink** | ~100 m (BLE) | **600 m** |
| **Ancho de banda NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latencia NearLink** | ~10 ms (BLE) | **20 µs** |
| **Consumo NearLink** | Línea base BLE | **60% menos que BLE 5.0** |
| **Peers NearLink simultáneos** | ~7 (límite de conexión BLE) | **500+** |
| **Fuente NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Radio SLE real (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / Relay HTTP** | Nativo | Nativo (idéntico) |
| **Seguridad Signal Protocol** | Completo | Completo (idéntico) |
| **Enrutamiento / DTN / SOS** | Completo | Completo (idéntico) |
| **Identidad Aether Tag** | Compatible | Compatible (idéntico) |

---

### Cambiar entre niveles

No se requieren cambios de código. El nivel se determina en tiempo de ejecución por `IsAvailable` en cada servicio de transporte:

1. En un dispositivo CircleOS o HarmonyOS con silicio NearLink, `IsAvailable` en el transporte NearLink devuelve `true` (verificado por hardware mediante comprobación de permisos + intento de escaneo pasivo).
2. El `TransportManager` promueve automáticamente NearLink a la posición de prioridad — menor costo de energía, mayor ancho de banda.
3. El código de la aplicación, el formato de cable, el algoritmo de enrutamiento, la capa de seguridad y los Aether Tags son idénticos en ambos niveles.

Un nodo del nivel estándar y un nodo del nivel nativo pueden comunicarse libremente — comparten el mismo formato de cable, las mismas sesiones Signal Protocol y los mismos Aether Tags. La diferencia de nivel solo afecta a la radio utilizada para los paquetes NearLink, no al protocolo superior.

---

> **Internamente, estos niveles se denominan variante Asterix (estándar) y variante Obelix (nativo).** Asterix funciona bien con lo que está disponible. Obelix — ejecutándose en CircleOS con NearLink nativo — opera con capacidad permanentemente elevada, de la misma forma que Obelix lleva la fuerza de la poción mágica sin necesidad de beberla de nuevo.

---

## Implementaciones

Aether está construido en 8 lenguajes para que funcione en teléfonos, portátiles, tabletas y microcontroladores. Todas las implementaciones producen paquetes compatibles a nivel de cable — un mensaje cifrado por el nodo Rust puede ser retransmitido por el nodo Python y descifrado por el nodo Swift.

| Lenguaje | Directorio | Formato de cable | Enrutamiento/DTN/SOS | X3DH | Double Ratchet | Pool OPK | Voz/Grupo | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Los 8 lenguajes producen paquetes de cable byte-idénticos, verificados por 14 fixtures canónicos de formato de cable y 4 vectores de prueba Signal ejecutados en CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). El enrutamiento (RREQ/RREP estilo AODV), DTN store-and-forward, difusión SOS, voz, streaming y servicios de refuerzo de seguridad están implementados en todos los lenguajes con **~3,000 pruebas** en las 8 implementaciones:

| Lenguaje | Pruebas | Plataforma CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3,000** | |

La interoperabilidad Signal multilenguaje está anclada a `fixtures/signal/` con vectores de prueba compartidos para X3DH (`x3dh_basic`), el ratchet simétrico (`ratchet_step_basic`, `ratchet_step_three_iterations`) y KDF_RK (`kdf_rk_basic`). Cada implementación debe producir salidas byte-idénticas contra esos fixtures. Los 8 lenguajes incluyen ahora una sesión Signal completa (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Más allá del formato de cable y Signal, la **suite completa de servicios de cable** — presencia, heartbeat, sincronización de perfil, anuncio de ID efímero, intercambio de pre-key, canales, push-to-talk, compartir pantalla, control de llamada, confirmación de SOS, migas de espacio, anuncio de forja, solicitud de shard de bóveda y medición de ancho de banda (ver **Lo que obtienes**) — también está implementada en los 8 lenguajes y anclada a sus propios fixtures (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, y afines). Ninguna característica es exclusiva de C# en la capa de protocolo.

## Inicio Rápido

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (SDK de .NET 10)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

La demo te lleva a través de 8 pasos: generación de claves de identidad Ed25519 para tres nodos (Alice, Bob, Charlie), establecimiento de sesiones Signal Protocol, envío de mensajes cifrados, retransmisión de un mensaje a través de Charlie (quien no puede leerlo), visualización del formato de cable binario y demostración de secreto perfecto hacia adelante en 5 mensajes consecutivos. La salida está codificada por colores y hace pausas entre pasos.

**Enviar un mensaje en C#:**

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

La demo genera claves de identidad para dos nodos, intercambia bundles de pre-keys, establece sesiones cifradas, envía mensajes cifrados en ambas direcciones, crea y firma paquetes mesh, verifica firmas y serializa paquetes al formato binario de cable. También demuestra la capa de transporte en proceso.

**Enviar un mensaje en Rust:**

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

La demo crea dos nodos en una red simulada, genera claves Ed25519, establece sesiones Signal Protocol, crea y firma un paquete, lo serializa al formato binario compatible con C#, cifra un mensaje secreto, lo descifra en el otro nodo, lo envía por el transporte y verifica el viaje de ida y vuelta.

**Enviar un mensaje en TypeScript:**

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

La demo ejecuta 8 demostraciones: generación de claves Ed25519 y detección de manipulación, creación de nodos con capacidades, intercambio de claves X3DH en Signal Protocol, cifrado y descifrado AES-256-GCM, serialización de paquetes, firma de paquetes con detección de repetición, transporte en proceso y un flujo completo de extremo a extremo combinando todas las capas.

**Enviar un mensaje en Python:**

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

La demo ejecuta 5 demostraciones: viajes de ida y vuelta de serialización de paquetes, firma Ed25519 con detección de manipulación, establecimiento de sesión Signal Protocol con mensajería cifrada en ambas direcciones, transporte en proceso entre dos peers y deduplicación de nonces para protección contra repetición.

**Enviar un mensaje en Go:**

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

La demo recorre 11 pasos: generación de claves, creación de nodos con capacidades, inicialización de Signal Protocol, intercambio de bundles de pre-keys, establecimiento de sesión, creación y firma de paquetes, serialización, deserialización con verificación de firma, cifrado de extremo a extremo con ratcheting de claves, detección de ataques de repetición y transporte en proceso.

**Enviar un mensaje en Kotlin:**

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

La demo ejecuta 5 pruebas: viajes de ida y vuelta de serialización de paquetes, firma Ed25519 con rechazo de manipulación, establecimiento de sesión Signal Protocol con cifrado AES-256-GCM, entrega de mensajes por transporte en proceso y un flujo completo de extremo a extremo donde Alice firma un paquete y Bob lo verifica después del transporte.

**Enviar un mensaje en Swift:**

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

La demo ejecuta 7 demostraciones: generación de claves Ed25519, creación y firma de paquetes, serialización al formato binario de cable, deserialización con verificaciones de integridad, cifrado y descifrado AES-256-GCM, autenticación de mensajes HMAC-SHA256 y derivación de claves HKDF-SHA256.

**Enviar un mensaje en C:**

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

## Hoja de Ruta

Lo que está construido y lo que viene a continuación.

**Completado (verificado multilenguaje, las 8 implementaciones):**
- Formato de cable: byte-idéntico en 8 lenguajes, anclado por 14 fixtures canónicos y aserciones multilenguaje en CI (`fixtures/expected/*.bin`)
- ✅ **CI de GitHub Actions** — matriz de 9 trabajos (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, más trabajo de integridad de fixtures) en `.github/workflows/ci.yml`.
- Firma y verificación de paquetes Ed25519
- Cifrado AES-256-GCM
- Primitivas de derivación de claves HKDF / HMAC
- Diseño de serialización de paquetes + firma (LE + campos int32 de 4 bytes)
- Simulador de transporte en proceso (para desarrollo y pruebas)
- Servicio de enrutamiento inspirado en AODV con RREQ/RREP, respuestas de ruta firmadas, deduplicación, reenvío TTL
- Servicio DTN store-and-forward con transferencia de custodia, replicación geohash-aware, TTL de 72h
- Servicio de difusión SOS con inundación, deduplicación, guardia de auto-origen, límite de velocidad (3/hr)
- Puntos de extensibilidad: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (valores predeterminados Noop)
- **~3,000 pruebas** en los 8 lenguajes (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — todas en verde en CI
- ✅ **Clave efímera X3DH real (8 lenguajes)** — 4 DHs X25519 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) con derivación de raíz HKDF-SHA256. Anclado por `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Alineación Double Ratchet en toda la familia** — Signal §5 completo con HMAC-SHA256 + separación de dominio 0x01/0x02 en el ratchet simétrico, HKDF-SHA256 KDF_RK en el paso DH-ratchet, rotación DH al recibir. Verificado por los fixtures `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 reconciliado con HEAD** — ver `docs/PROTOCOL_SPEC.md`.

**Completado (los 8 lenguajes):**
- ✅ **Llamadas de voz (1 a 1)** — máquina de estados de señalización (Offer/Answer/Hangup/Cancel/Timeout) + transporte de frames binarios (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Entrega con conocimiento de ruta mediante `IRoutingService`.
- ✅ **Voz grupal** — membresía controlada por el anfitrión (invitar/expulsar/salir), campo de generación de clave por frame, distribución unicast a todos los miembros actuales, rotación de clave controlada por el anfitrión en cambios de membresía.
- ✅ **Streaming en vivo** — el publicador difunde `StreamAnnounce`; los suscriptores envían `StreamSubscribe`; frames binarios `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast a cada suscriptor.
- ✅ **Videollamadas (1 a 1)** — negociación de códec/resolución/fps/bitrate en señalización, señales de solicitud de keyframe y cambio de calidad, formato `VideoFrame` binario que coincide con el diseño de voz.
- ✅ **Watch Together** — el anfitrión emite comandos `WatchSync` autoritativos (play/pausa/avance/velocidad); los seguidores los aplican con compensación RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` de disparar y olvidar.
- ✅ **Pool de pre-key de un solo uso (OPK)** — predeterminado 100, emisión FIFO, recarga diferida, consumo protegido por bloqueo en los 8 lenguajes. Cierra el peligro de concurrencia de OPK único.
- ✅ **C: sesión Signal completa** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` en `c/src/signal_protocol.c`; 6 pruebas E2E de dos nodos en `c/tests/test_signal_session.c`. Los 8 lenguajes ahora tienen Signal Protocol completo con capacidad de sesión.

**Completado (los 8 lenguajes — la suite completa de servicios de cable):**
- ✅ **Cada tipo de paquete reservado es ahora un servicio real y byte-idéntico en los 8 lenguajes.** Baliza/consulta de presencia (21/22), heartbeat (10), sincronización de perfil (23), anuncio de ID-de-enrutamiento-efímero (56), intercambio de pre-key (25/26), canales (7), push-to-talk (15), compartir pantalla (32), control de llamada (27), confirmación de SOS (6), migas de espacio (40), anuncio de forja (41), solicitud de shard de bóveda (42) y medición de ancho de banda / ABMF (53/54/55). Cada uno es un servicio ligero (producir + manejar + evento) que el anfitrión conecta a su sesión Signal y tabla de enrutamiento; cada uno está anclado a un fixture multilenguaje compartido (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) y ejercitado por pruebas unitarias por lenguaje, con Swift y C verificados en el servidor de compilación macOS. Ver **Lo que obtienes**.

**Completado (solo referencia C#):**
- ✅ **Demo Paso 9 — MessagingService + DTN fallback de extremo a extremo** — `samples/AetherNet.Demo.Console` recorre mensajería cifrada con Signal real y DTN store-and-forward cuando el destinatario está sin conexión.
- ✅ **Puente `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` hace que la capa de mensajería esté cifrada de extremo a extremo por defecto; los mensajes sin sesión Signal se ponen en cola, nunca se envían de forma insegura.
- ✅ **Streaming de bitrate adaptativo** — `AdaptiveBitrateController` con escaleras de bitrate según la especificación para el Perfil A (tiempo real), B (transmisión en vivo) y C (VOD). El publicador selecciona el peldaño sostenible más alto (margen del 20%) y emite `StreamAbandon` (`PacketType.StreamAbandon`) en lugar de un segmento cuando está por debajo del suelo. `IStreamingService` expone `UpdateBandwidthEstimate` y `GetCurrentBitrateRung`.
- ✅ **Watch Together: ingestión BitTorrent + financiación grupal ChipIn** — modelos `TorrentInfo` / `TorrentFile`; `WatchTogetherService` gestiona `PacketType.TorrentMetadata` y dispara `TorrentReceived`. Máquina de estados `ChipInPool` / `ChipInContribution` (Recolectando → Financiado → Comprando → Adquirido / Fallido / Reembolsado); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` en `IWatchTogetherService`.
- ✅ **Videollamadas grupales con relay SFU automático** — `GroupVideoService` / `IGroupVideoService`. Topología FullMesh para ≤ 3 participantes; cambio automático a SFU en `SfuThresholdParticipants` (4) con reasignación de relay mediante `GroupVideoSignaling(SfuAssigned)`. Distribución en FullMesh, envío solo por relay en modo SFU. Tipo de paquete de señalización `GroupVideoSignaling = 35`.
- ✅ **Simulación de transporte BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Encuadrado MTU GATT mediante `BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), registro de peers estático en proceso, difusión de anuncios. Todas las restricciones `BleMaxPayloadBytes` aplicadas.
- ✅ **Simulación de transporte Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Ciclo de vida `ConnectAsync`/`DisconnectAsync` explícito, entrega directa de payloads grandes (sin encuadrado), eventos bidireccionales `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulación de transporte NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU de frame de 4096 B, registro de 500 peers, `ConnectedPeerCount`, `IsAvailable` configurable en tiempo de ejecución.
- ✅ **Pruebas de simulación de puesta en marcha RF** — Pruebas de interoperabilidad de dos nodos (`SimulatedTransportTests`): viaje de ida y vuelta de `MeshPacket` BLE + NearLink, transferencia de payload de 64 KB Wi-Fi Direct. Capa de software completamente verificada; se necesita sesión de laboratorio de dispositivos físicos para validación en hardware.

**Completado (capa de transporte C# — todos fail-fast):**
- ✅ **Transporte BLE GATT real** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (servidor GATT Android). Prueba de puesta en marcha RF completa en `samples/AetherNet.BleRfTest/`.
- ✅ **Transporte Wi-Fi Direct real** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket puerto 8888) + `android/green/` (`WifiP2pManager`). Prueba RF en `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Transporte relay HTTP (Aether Purple)** — `HttpRelayTransportService` con long-poll de 10 segundos, `PowerCostRelative = 100`, siempre último recurso. Servidor de relay en `samples/AetherNet.RelayServer/` (API mínima ASP.NET Core, puerto 5200). Prueba RF en `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implementa `HostApduService` con AID `F061657468657200`. `WinNfcStubTransportService` documenta dos rutas de aproximación Windows: (1) NDEF-over-BLE-GATT con puerta RSSI ≥ −40 dBm (simula toque para conectar sin silicio NFC, `IsAvailable = Bluetooth presente`); (2) lector USB ACR122U mediante `Windows.Devices.SmartCards` PC/SC (`IsAvailable = lector sin contacto enumerado`). Ruta de actualización: implementar `ITransportService` cuando Microsoft publique una API NFC P2P de primera parte.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implementación ArkTS completa para HarmonyOS 5.0.1 (API 13) usando `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` sondeado en tiempo de ejecución. `WinNearLinkStubTransportService` + `android/teal/` documentan la aproximación SSAP-over-BLE: BLE GATT con UUID de servicio SLE de Aether `61657468-6572-0003-0000-000000000000` — análogo en API a SSAP, no compatible a nivel de cable con hardware NearLink real. Ruta de actualización: reemplazar llamadas BLE GATT con llamadas SDK `ssapc_*`/`ssaps_*`; UUIDs y ranura `TransportManager` sin cambios.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` documentan la aproximación Meshtastic-over-BLE-LR: formato de cable Meshtastic completo (encabezado de 16 bytes + protobuf AES-256-CTR) sobre BLE 5.0 Coded PHY S=8 (~1.3 km en exteriores), con enrutamiento de inundación gestionada y ventana de contención ponderada por RSSI. La federación de nodos puente con hardware LoRa real funciona automáticamente (mismo formato de paquete Meshtastic, sin traducción). Ruta de actualización: reemplazar radio BLE LR con driver AT-command o SPI SX1276/SX1278; formato de paquete y enrutamiento sin cambios.

**Abierto — rastreado en `OPEN_ISSUES.md`:**
- Puesta en marcha RF en hardware real: prueba de interoperabilidad de extremo a extremo de dos nodos en dispositivos físicos BLE / Wi-Fi Direct (las pruebas de simulación pasan; se necesita sesión de laboratorio de hardware)
- NearLink: `harmonyos/teal/` completo; requiere hardware Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silicio NearLink no presente en dispositivos no Huawei). Windows + Android recaen en la aproximación SSAP-over-BLE automáticamente.
- LoRa / CircleLink: se requiere módulo de radio para el alcance LoRa real. Sin él, el formato de cable Meshtastic se transporta sobre BLE LR (~1.3 km) y la federación de nodos puente con hardware LoRa real está disponible.
- ✅ **(RESUELTO v1.2.0)** Superficie de protocolo para consumidores (Wave 16/17) — evento `IDtnService.BundleReceived` para bundles entrantes ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), directorio de nombrado/descubrimiento a nivel de aplicación ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), interfaz de propinas a autores ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Los 3 se enviaron de forma aditiva en los 8 lenguajes con fixtures multilenguaje byte-iguales. Ver CHANGELOG.

**Aún no abierto para contribuciones externas:**
- El protocolo todavía está en desarrollo activo. Las contribuciones externas no se están aceptando en este momento.
- La implementación del transporte NearLink, ejemplos de integración Android/iOS, backends de transporte adicionales, benchmarks de rendimiento y fuzzing del protocolo se rastrean internamente y se abrirán cuando el proyecto alcance un punto de contribución pública estable.

## Estructura del Proyecto

```
aether-protocol/
  src/
    AetherNet.Core/          Modelos del protocolo, constantes, serialización de paquetes
    AetherNet.Security/      Signal Protocol, Ed25519, firma de paquetes
    AetherNet.Transport/     Abstracciones de transporte, NearLink, simulador en proceso
    AetherNet.Messaging/     Manejo y retransmisión de mensajes
    AetherNet.Storage/       Persistencia DTN store-and-forward
    AetherNet.Streaming/     Streaming de bitrate adaptativo, modelos e interfaces de video
    AetherNet.Voice/         Llamadas de voz y voz grupal
    AetherNet.Content/       Verificación de contenido y transferencia por fragmentos
  samples/
    AetherNet.Demo.Console/  Demo interactiva
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Implementación en Rust
  typescript/             Implementación en TypeScript
  python/                 Implementación en Python
  go/                     Implementación en Go
  kotlin/                 Implementación en Kotlin/JVM
  swift/                  Implementación en Swift
  c/                      Implementación en C
  docs/
    PROTOCOL_SPEC.md      Especificación del protocolo estilo RFC
```

## Agregar un Nuevo Transporte

Implementa `ITransportService`:

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

Regístralo en DI y el `TransportManager` lo incluirá automáticamente en la selección de transporte, ordenado por costo de energía.

## Comparación

| Protocolo | Limitación | Ventaja de Aether |
|----------|-----------|-----------------|
| **Briar** | Solo Android, dependiente de Tor | Multiplataforma, malla pura |
| **Meshtastic** | Solo LoRa (30 kbps máx) | Multi-transporte (BLE + WiFi + NearLink), capaz de voz y streaming |
| **Reticulum** | Python, comunidad pequeña | 8 lenguajes, compatibles a nivel de cable entre todos ellos |
| **libp2p** | Asume backbone de internet | Offline-first, funciona sin infraestructura |
| **Yggdrasil** | Red superpuesta, necesita internet | Malla en capa física, funciona sin internet |
| **Signal** | Sin malla, requiere internet | Funciona sin conexión, P2P, relay de malla, mismo cifrado E2E |

## Preguntas frecuentes

**¿Funciona AetherNet sin internet?**
Sí — es offline-first. Los dispositivos se comunican directamente por Bluetooth, Wi-Fi Direct, NearLink o LoRa y retransmiten los mensajes salto a salto a través de otros dispositivos, sin necesidad de conexión a internet, torre celular ni servidor. Cuando no existe una ruta activa, los mensajes se retienen (store-and-forward tolerante a retrasos) hasta 72 horas hasta que se abra una.

**¿Está cifrado de extremo a extremo?**
Sí. AetherNet usa el Signal Protocol (acuerdo de claves X3DH más el Double Ratchet sobre X25519) para el cifrado de extremo a extremo, AES-256-GCM para los payloads de los mensajes y firmas Ed25519 en cada paquete. Los dispositivos que retransmiten un mensaje no pueden leerlo.

**¿Qué transportes utiliza?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), una radio serial LoRa/CircleLink, un relay HTTP/QUIC y WebRTC para peer-to-peer directo por internet. El protocolo selecciona automáticamente el transporte disponible de menor consumo por paquete y recurre al siguiente.

**¿En qué lenguajes de programación está disponible?**
Ocho — C#, Rust, TypeScript, Python, Go, Kotlin, Swift y C. Cada implementación produce paquetes de cable byte-idénticos, garantizados por un corpus de fixtures multilenguaje compartido en CI, de modo que un paquete construido por un lenguaje es decodificado sin cambios por cualquier otro.

**¿En qué se diferencia de Meshtastic, Briar o Bridgefy?**
Meshtastic es solo LoRa; AetherNet es multi-transporte (Bluetooth + Wi-Fi + NearLink + LoRa) y transporta voz, video y streaming además de mensajes. Briar es solo Android y enruta sobre Tor; AetherNet es multiplataforma y de malla pura. A diferencia de los SDK cerrados, AetherNet tiene licencia MIT y está implementado abiertamente en ocho lenguajes. La tabla de comparación de arriba tiene los detalles.

**¿Está listo para producción?**
La capa de protocolo — formato de cable, seguridad Signal, enrutamiento, DTN store-and-forward y la suite completa de servicios — está implementada y probada en los ocho lenguajes. Los transportes de radio son reales donde existe código de plataforma (Bluetooth y Wi-Fi en Windows y Android, WebRTC en todas partes) y están sin verificar en campo en el resto, a la espera de la puesta en marcha de hardware, que se rastrea con honestidad en `OPEN_ISSUES.md`. Lee las notas de estado en cada sección antes de desplegar.

**¿Bajo qué licencia está?**
MIT — gratuita para uso comercial y de código abierto. Ver [LICENSE](LICENSE).

**¿Quién construye AetherNet?**
Se desarrolla como el protocolo abierto detrás del ecosistema mesh de The Geek Network, construido en Sudáfrica para una comunicación que funciona con o sin datos móviles.

## Puntos de Extensión

El protocolo funciona de forma independiente. Estas interfaces te permiten conectar tu propio backend si lo deseas:

- `IAetherNetIncentiveProvider` — recompensar nodos que retransmiten tráfico (predeterminado no-op: retransmisión altruista)
- `IAetherNetBackendClient` — sincronizar con un servidor cuando hay internet disponible (predeterminado no-op: completamente sin conexión)
- `IAetherNetFeatureFlagProvider` — activar o desactivar características del protocolo en tiempo de ejecución (predeterminado no-op: todo habilitado)

Los tres se incluyen con implementaciones no-op. Elimínalos y nada se rompe.

## Contribuciones

Las contribuciones externas aún no están abiertas. El proyecto todavía está en desarrollo activo. Vuelve a consultar cuando anunciemos una ventana de contribución pública.

## Seguridad

Consulta [SECURITY.md](SECURITY.md) para la política de divulgación responsable.

## Licencia

Licencia MIT. Consulta [LICENSE](LICENSE).

## Traducciones

Este README se mantiene en inglés y se traduce a 10 idiomas adicionales bajo [`docs/i18n/`](docs/i18n/): Français, Español, العربية, 中文简体, 日本語, Deutsch, Português (BR), Русский, فارسی y 한국어. La **versión en inglés es la fuente de verdad** — cuando una traducción y el texto en inglés difieran, el texto en inglés es el autoritativo, y las traducciones pueden ir por detrás de él una versión o dos. El protocolo, el código, los fixtures y el comportamiento descritos son idénticos sin importar en qué idioma leas.
