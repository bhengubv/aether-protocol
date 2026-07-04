# Aether Protocol — Modelo de Amenazas

**Revisado contra HEAD `b8b3d22` (2026-05-06).** Este documento describe lo que la capa
de protocolo criptográfico de `aether-protocol` defiende, lo que está explícitamente fuera
del alcance y las suposiciones en las que se basan las afirmaciones de seguridad. Es
intencionalmente honesto: un atacante que lea esto debe poder enumerar cada ataque que el
protocolo **no** detiene, y no debe ser engañado por el marketing del README.

El documento complementario es [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7
(Modelo de seguridad). Donde los dos divergen, la implementación en
`src/AetherNet.Security/` es la autoridad.

---

## 1. Alcance

### Lo que `aether-protocol` ES

Una biblioteca de mensajería cifrada de extremo a extremo al estilo del Protocolo Signal
más una primitiva de red en malla (enrutamiento estilo AODV + almacenamiento y reenvío
DTN + inundación SOS). Las garantías de seguridad principales son:

1. **Confidencialidad** — los cuerpos de los mensajes están cifrados con AES-256-GCM bajo
   claves por mensaje derivadas de un Double Ratchet (Signal §5).
2. **Autenticidad** — cada `MeshPacket` lleva una firma Ed25519 sobre un búfer canónico
   de datos firmables (PROTOCOL_SPEC §2.4).
3. **Protección contra repetición** — los paquetes se descartan si hay un duplicado de
   `(SourceUhid, PacketNonce)` dentro de una ventana de frescura de 5 minutos.
4. **Secreto hacia adelante y post-compromiso** — el Double Ratchet renegocia en cada
   cambio de clave pública DH en un ciclo de ida y vuelta; un atacante que compromete una
   clave de sesión no puede recuperar ni mensajes pasados ni futuros.

### Lo que `aether-protocol` NO ES

- **No es un reemplazo de seguridad a nivel de transporte.** Use TLS para
  cliente→servidor. El E2EE de Aether es para tráfico de malla entre pares; en el momento
  en que un paquete sale de la malla hacia un backend centralizado, la seguridad del
  transporte de ese backend es responsabilidad del anfitrión.
- **No es un sistema de gestión de claves.** El anfitrión proporciona almacenamiento
  duradero para material de identidad y pre-claves a través de `IPreKeyStore` (o cualquier
  adaptador respaldado por `IKeyValueStore`). La integración con almacén de claves de
  hardware, la atestación de TPM, la recuperación por custodia de claves y el cifrado en
  reposo son responsabilidad del anfitrión.
- **No es un sistema de autenticación.** Aether autentica que "el titular de la
  clave-de-identidad-X dijo este paquete". Mapear la clave-de-identidad-X a "el humano
  Alice" es responsabilidad de la UX del anfitrión (comparación de número de seguridad,
  intercambio de huella digital fuera de banda, cadena de confianza previa).
- **No es una red de privacidad.** El cable revela el tipo de mensaje, la longitud del
  paquete, el UHID de origen, el UHID de destino, el conteo de saltos y el tiempo. No es
  Tor.

---

## 2. Ataques defendidos

### 2.1. Escucha clandestina en tránsito

Cada carga útil está cifrada con AES-256-GCM bajo una clave por mensaje derivada de la
cadena simétrica del Double Ratchet (Signal §5.1, HMAC-SHA256 con separación de dominio
`0x01`/`0x02`). Un atacante que captura cada paquete entre Alice y Bob no recupera nada
sin una de sus claves de sesión.

Verificado por `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs`
y los vectores de `fixtures/signal/expected/ratchet_step_basic.json` entre idiomas.

### 2.2. Falsificación de mensajes

Cada paquete Wave-2 lleva una firma Ed25519 sobre el búfer canónico
`BuildSignableData(packet)` (`src/AetherNet.Security/Services/PacketSigningService.cs`,
PROTOCOL_SPEC §2.4). Los paquetes falsificados fallan la verificación y se descartan en
cada salto que conoce la clave pública de identidad del origen. Los paquetes de Respuesta
de Ruta (RREP) están firmados por el destino reclamado — los nodos intermedios no pueden
suplantar destinos porque no poseen la clave privada Ed25519 del destino.

### 2.3. Ataques de repetición

`PacketSigningService.VerifyPacketAsync`:

- Rechaza paquetes cuyo `TimestampMs` difiere más de 5 minutos del UTC local
  (`FreshnessWindowMs = 5 * 60 * 1000`).
- Mantiene un mapa de deduplicación en memoria con clave `(SourceUhid, PacketNonce)`
  con TTL de 5 minutos. La clave de deduplicación se cambió de solo `nonce` a
  `(source, nonce)` en el commit `5bd52a9` para corregir dos modos de falla:
  colisiones de nonce entre distintos remitentes que descartaban tráfico legítimo, y
  ataques de pre-registro donde un adversario planta un nonce contra un destinatario para
  bloquear el primer paquete del remitente legítimo.

Contadores: `aethernet.nonces.replayed`, `aethernet.timestamps.stale`.

### 2.4. Secreto hacia adelante (compromiso de clave pasada)

El Double Ratchet deriva una nueva clave de cadena de envío en cada paso de rotación DH
(KDF_RK, HKDF-SHA256 sobre `salt = current_root_key`,
`info = "aether-ratchet-rk-v1"`, bloque de 64 bytes dividido 32+32 en nueva
clave raíz y de cadena — `src/AetherNet.Security/Services/SignalProtocolService.cs`).
Un atacante que compromete el estado de sesión actual no puede descifrar ningún mensaje
anterior: cada clave de mensaje anterior fue derivada y puesta a cero
(`CryptographicOperations.ZeroMemory`) antes del siguiente paso del trinquete.

### 2.5. Seguridad post-compromiso (recuperación de claves futuras)

Cuando el lado receptor observa un nuevo `SenderEphemeralKeyX25519` en un mensaje
entrante, ejecuta un paso de DH-ratchet en recepción (Signal §5.2). El estado de sesión
en caché del atacante queda obsoleto en el siguiente ciclo de ida y vuelta; un atacante
que toma una instantánea de una sesión y se aleja ya no puede descifrar mensajes una vez
que las partes legítimas han intercambiado una ronda.

El paso de rotación DH en recepción se implementó en los 8 idiomas — ver
`OPEN_ISSUES.md` ítem 2 para la lista de commits del ecosistema.

### 2.6. Repetición de pre-clave de un solo uso

Cada pre-clave de un solo uso (OPK) se consume exactamente una vez. La referencia en C#
incluye un pool de 100 OPK con emisión FIFO, recarga diferida en cada generación de
paquete, y consumo de instancia única protegido por cerrojo
(`SignalProtocolService.TopUpOpkPoolNoLock`, verificado por
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`). Una OPK se elimina y pone a cero en el
momento en que el respondedor la consume durante X3DH, por lo que un mensaje PreKey
repetido que reutilice el mismo id de OPK no puede establecer una sesión.

**Resuelto (los 8 idiomas).** Los otros siete idiomas con capacidad de sesión
incluyen ahora el mismo pool de 100 OPK con emisión FIFO de una sola vez, recarga
diferida y consumo de instancia única protegido por cerrojo, cerrando el anterior
riesgo de concurrencia de una sola OPK por sesión: Rust
(`rust/src/security/signal_protocol.rs` — `DEFAULT_OPK_POOL_SIZE = 100`,
`available_opk_ids: VecDeque<i32>`, `top_up_opk_pool`), Go
(`go/security/signal_protocol.go` — `DefaultOpkPoolSize = 100`,
`topUpOpkPoolLocked`), Python
(`python/aethernet/security/signal_protocol.py` —
`DEFAULT_OPK_POOL_SIZE = 100`, `available_opk_ids: Deque`,
`_top_up_opk_pool_locked`), TypeScript
(`typescript/src/security/PreKeyStore.ts` — pool FIFO consumido-una-vez,
`typescript/tests/opk_pool.test.ts`), Kotlin
(`kotlin/src/main/kotlin/aethernet/security/SignalProtocol.kt` —
`DEFAULT_OPK_POOL_SIZE = 100`, `ArrayDeque<Int>`, `topUpOpkPoolNoLock`),
Swift (`swift/Sources/AetherNetProtocol/Security/SignalProtocol.swift` —
`defaultOpkPoolSize = 100`, FIFO `removeFirst()`, `topUpOpkPool`) y C
(`c/src/signal_protocol.c` — `AETHERNET_SIGNAL_OPK_POOL_SIZE = 100`, pool
sembrado 1..100 en `aethernet_signal_service_init`, emisión del primer no
consumido, OPK puesta a cero + marcada `consumed` en el X3DH del respondedor).

### 2.7. Divergencia de cable entre idiomas

Cada implementación debe producir salidas byte-idénticas contra el corpus de fixtures en
`fixtures/`:

- `fixtures/expected/*.bin` — 17 fixtures de serialización de paquetes, con igualdad de bytes
  verificada en los 8 lenguajes en CI.
- `fixtures/signal/expected/x3dh_basic.json` — matemática X3DH (4 X25519 DH,
  HKDF-SHA256 raíz con `info = "aether-x3dh-root-v1"`).
- `fixtures/signal/expected/ratchet_step_basic.json`,
  `ratchet_step_three_iterations.json` — KDF del trinquete simétrico.
- `fixtures/signal/expected/kdf_rk_basic.json` — paso de DH-ratchet.

Una divergencia en la cadena HKDF info, el orden de bytes o el relleno de cualquier
idioma falla su compilación de `SignalFixtureTests`. La interoperabilidad compatible con
el cable es, por lo tanto, un invariante en tiempo de compilación, no una esperanza en
tiempo de ejecución.

### 2.8. Compromiso DH estático-estático (el X3DH roto anterior)

Antes del 2026-05-05, la implementación C# de `KEY_EXCHANGE` usaba la clave de identidad
del nodo local para ambas operaciones DH — un colapso estático-estático que rompía la
propiedad de secreto hacia adelante de la clave efímera X3DH. Cerrado por el commit
`07a93f5`: X3DH real ahora realiza los 4 DH canónicos
`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`
con un efímero fresco por sesión. Ver `OPEN_ISSUES.md` §1.

### 2.9. Bucles de enrutamiento y tormentas de difusión

`RoutingService` deduplica paquetes RREQ por `(originUhid, broadcastId)`
en una caché acotada (10 000 entradas por defecto; `ProtocolConstants.RouteRequestDedupCacheSize`).
El TTL se decrementa en cada salto y los paquetes con `Ttl == 0` se descartan.
Las difusiones SOS tienen límite de velocidad de 3/hora por origen y la supresión del
propio origen evita que un nodo redifunda su propio SOS.

### 2.10. DoS por agotamiento del pool OPK

El pool de OPK está acotado (`OpkPoolSize`, por defecto 100) y la verificación de salud
de Signal devuelve `Unhealthy` cuando las OPK disponibles caen por debajo de
`SignalOptionsBag.MinAvailableOpks` (por defecto 10). Los anfitriones conectan alertas
al estado de salud `aether-signal`. Un atacante que agota las OPK obteniendo paquetes no
puede superar el tamaño del pool configurado; el X3DH del respondedor sigue funcionando
para paquetes ya emitidos y se recupera cuando la recarga se ejecuta en la siguiente
generación de paquetes.

### 2.11. Rastreo pasivo de dispositivos por BLE

Un escáner pasivo que registra una MAC BLE estable o un Service UUID estable puede
seguir un dispositivo a través del tiempo y el lugar. `BlePrivacy`
(`src/AetherNet.Security/Privacy/BlePrivacy.cs`) cierra el vector de vinculación de
identificadores: el Service UUID anunciado se vuelve a derivar cada 15 minutos como
`HMAC-SHA256(rotation_key, window)` (PROTOCOL_SPEC §12.3), y los pares se direccionan
mediante direcciones privadas resolubles (IRK + `ah`) en lugar de una MAC fija. Sin la
clave de rotación o el IRK, dos anuncios no pueden vincularse. Fijado a
`fixtures/bleprivacy/`.

**Riesgo residual.** Esto solo cierra el vector de identificador BLE — **no** convierte
a Aether en una red de privacidad (§1). Una vez que un paquete está en la malla, la
cabecera `MeshPacket` en texto claro sigue exponiendo el UHID de origen/destino, el
tipo, la longitud y el tiempo (el análisis de tráfico permanece fuera del alcance,
§3.3), y la toma de huellas a nivel RF no se aborda. Emitir los identificadores
rotativos por el aire es tarea de la pila BLE del anfitrión — la biblioteca solo los
deriva.

### 2.12. Divulgación de clave bajo coacción (duress)

Un adversario con posesión física que coacciona al usuario para que desbloquee.
`PanicWipe` (`src/AetherNet.Security/Privacy/PanicWipe.cs`) acepta un **PIN de
coacción** — comparado contra un `SHA-256(pin)` almacenado en tiempo constante (sin
fuga de tiempo por salida anticipada) — que borra de forma segura cada clave de
identidad (sobrescritura con aleatorio, luego puesta a cero) a lo largo del manifiesto
de nombres de clave, de modo que el dispositivo entregado no contiene ninguna identidad
utilizable. Fijado a `fixtures/panicwipe/`.

**Riesgo residual.** De mejor esfuerzo y explícitamente acotado: **no** defiende contra
una imagen forense capturada *antes* del borrado, el nivelado de desgaste de la flash
que preserva una copia previa de los bytes de la clave, un adversario que obliga a
revelar el PIN *genuino*, o la coacción después de que los mensajes ya fueron leídos.
La comparación en tiempo constante mitiga el tiempo de adivinación del PIN, no un
adversario de canal lateral completo (§3.2).

### 2.13. Pérdida del único dispositivo (recuperación)

No es un atacante, sino el fallo de disponibilidad que supone perder la única copia de
una identidad. La copia de seguridad con frase de recuperación
(`src/AetherNet.Security/Backup/`) codifica la semilla de identidad Ed25519 de 32 bytes
como una frase BIP-39 de 24 palabras con suma de verificación (PROTOCOL_SPEC §12.4) que
restaura la identidad en cualquier dispositivo — ningún servidor ni custodio la
retiene.

**Riesgo residual — una nueva superficie de robo.** La frase **es** la identidad:
cualquiera que lea las 24 palabras puede suplantar por completo al usuario, sin
revocación. Cambia un riesgo de pérdida de dispositivo por un riesgo de secreto en
papel. La biblioteca codifica/decodifica y calcula la suma de verificación de la frase;
la visualización segura, el almacenamiento y la frase de contraseña BIP-39 opcional son
responsabilidad del anfitrión.

### 2.14. Inyección de dispositivo malicioso en la sincronización multidispositivo

Un atacante que intenta insertar un dispositivo que controla en el conjunto de
sincronización de una víctima, o falsificar registros de sincronización. Un
`DeviceLink` (`src/AetherNet.Security/Sync/`) está **firmado con Ed25519 por la clave
de identidad** (PROTOCOL_SPEC §12.1), de modo que solo el titular de la identidad puede
autorizar un nuevo dispositivo — un enlace sin firmar o con clave incorrecta falla la
verificación. Las cargas útiles `SyncRecord` viajan cifradas de extremo a extremo
dentro de la ruta DTN/malla, por lo que los relés las transportan pero no pueden
leerlas. Fijado a `fixtures/sync/`.

**Riesgo residual.** Esto autentica el *enlazado*, no el comportamiento posterior del
dispositivo enlazado: un dispositivo que se enlaza legítimamente y *luego* se
compromete ve todo el estado sincronizado — la sincronización no tiene secreto hacia
adelante por registro. La reconciliación es último-en-escribir-gana sobre
`(created_at_ms, logical_clock, device_id, record_id)`, por lo que un dispositivo
enlazado con un reloj desviado puede sesgar qué registro gana; la integridad del reloj
es asunto del anfitrión. La paridad byte a byte de las firmas conlleva la excepción de
Swift/CryptoKit señalada en PROTOCOL_SPEC §12.1.

---

## 3. Fuera del alcance

Estos son ataques reales que el protocolo **no** detiene. Algunos son teóricamente
mitigables en una versión futura; otros son fundamentalmente una preocupación del
anfitrión.

### 3.1. Compromiso del punto final

Si un atacante tiene acceso root al dispositivo de Alice puede leer los bytes privados de
su clave de identidad desde la memoria y descifrar cada sesión que tiene. El protocolo
asume que la memoria del proceso del dispositivo es de confianza. Las mitigaciones
(almacén de claves de plataforma, SGX, almacenes de claves respaldados por hardware) son
explícitamente responsabilidad del anfitrión — ver la Sección 4.

### 3.2. Ataques de canal lateral

La implementación de referencia usa
`CryptographicOperations.FixedTimeEquals` para la comparación de claves públicas del
trinquete (`SignalProtocolService.ConstantTimeEquals`) pero no está específicamente
reforzada contra:

- Canales laterales de temporización en AES-GCM (la BCL .NET `AesGcm` está acelerada
  por hardware en CPUs con AES-NI; la temporización del fallback por software no está
  auditada).
- Canales laterales de análisis de potencia (puramente software — sin contramedidas de
  hardware).
- Temporización de caché en rutas de derivación de claves (HKDF-SHA256 vía la BCL).

Un ataque en laboratorio de grado estado-nación sobre un dispositivo desbloqueado robado
es plausible.

### 3.3. Análisis de tráfico

El formato de cable revela:

- **Tipo** del paquete (1 byte en el desplazamiento 1 — RREQ vs Data vs SOS está en
  texto claro).
- **Longitud** del paquete (las cargas útiles no están rellenas).
- **UHIDs de origen y destino** (UTF-8, en texto claro).
- **Marcas de tiempo**, **TTL** y **prioridad**.

El relleno, el tráfico de cobertura y el enrutamiento cebolla no están implementados. Un
adversario que puede observar pasivamente el tráfico BLE / Wi-Fi puede construir un grafo
de contactos y un perfil de temporización de cada conversación, aunque no pueda leer el
contenido. Esta es una limitación conocida; su mitigación requeriría romper el formato de
cable y no está en el mapa de ruta actual.

### 3.4. Ataques cuánticos

X25519 (RFC 7748) y Ed25519 (RFC 8032) se rompen bajo una computadora cuántica
suficientemente grande que ejecuta el algoritmo de Shor. El protocolo **no es
post-cuántico**. Una futura migración a un esquema híbrido
Kyber + X25519 / Dilithium + Ed25519 es una preocupación conocida pero no está
programada. El texto cifrado existente registrado hoy por un adversario que apuesta por
"recolectar ahora, descifrar después" está en riesgo si llega un CRQC dentro del
horizonte temporal relevante.

### 3.5. Mensajería grupal a escala

`AetherNet.Security` incluye un punto de extensión `IGroupKeyProvider`, pero el protocolo
Signal Sender Keys completo (la construcción de mensajería grupal asíncrona que usa
Signal) **no** está implementado en HEAD. Los anfitriones que necesitan mensajería grupal
hoy recurren a N sesiones por pares — lo que funciona pero tiene un costo O(N) por envío
al grupo. PROTOCOL_SPEC §7 cubre solo las amenazas de un solo destinatario.

### 3.6. Verificación de identidad en el primer contacto (TOFU)

Aether autentica que "el par que tiene la clave-de-identidad-X firmó esto". **No**
autentica que "la clave-de-identidad-X realmente pertenece al humano Alice que el usuario
espera que sea su interlocutor". En el primer contacto, un hombre en el medio activo que
controla la red durante el primer intercambio de paquetes puede sustituir su propia clave
de identidad, firmar su propio paquete y enrutar el tráfico en ambas direcciones de forma
transparente.

Esta es la debilidad estándar "Trust On First Use" de Signal. La mitigación canónica es
la comparación de número de seguridad / huella digital fuera de banda (en persona, a
través de un canal separado, en una pantalla de verificación compartida previamente). El
protocolo actualmente no expone una superficie de API pública para la derivación del
número de seguridad; rastreando como una brecha (aún no en `OPEN_ISSUES.md`) — la UX del
anfitrión no debe pretender que está verificado por defecto.

### 3.7. Ataques a nivel de red en el transporte subyacente

La interferencia de señal (BLE, Wi-Fi, NearLink), la denegación de servicio a nivel RF y
los ataques contra los flujos de emparejamiento/vinculación del transporte están fuera del
alcance. El transporte (`ITransportService`) se trata como un tubo de bytes opaco. Un
interferidor que posee el espectro impide que Aether entregue cualquier cosa.

### 3.8. Ataques de enrutamiento más allá de la ventana de deduplicación

La inundación Sybil por nodos de corta vida que aún no han acumulado una puntuación de
confiabilidad, el descarte oportunista de relés que no activa la heurística de
confiabilidad, y los ataques de agotamiento de recursos que permanecen por debajo de los
límites de velocidad no están específicamente mitigados. La puntuación de confiabilidad
(PROTOCOL_SPEC §3.5) desprioriza los nodos demostrados como malos pero no es un protocolo
de enrutamiento completamente resiliente a Bizancio.

---

## 4. Suposiciones para que las afirmaciones de seguridad sean válidas

Las defensas de la Sección 2 están condicionadas a los siguientes invariantes. Si
cualquiera de ellos se rompe, la propiedad de seguridad correspondiente se pierde.

1. **Durabilidad de la clave de identidad.** El anfitrión almacena los pares de claves de
   identidad Ed25519 + X25519 de largo plazo de forma duradera y segura (p. ej. vía
   `IPreKeyStore` contra un `FileSystemKeyValueStore` envuelto en
   `EncryptedKeyValueStore`, o contra el almacén de claves de la plataforma). La pérdida
   de una clave de identidad = compromiso total de la cuenta; el titular de la clave
   privada puede firmar cualquier cosa como el par original.

2. **Correctitud del CSPRNG.** `RandomNumberGenerator.GetBytes` y
   `RandomNumberGenerator.GetInt32` en la plataforma objetivo producen salida
   criptográficamente segura. Todo el protocolo — claves efímeras, nonces AES-GCM, nonces
   de paquetes, ids de OPK — depende de esto. En plataformas donde la fuente de
   aleatoriedad de la BCL está degradada (algunos objetivos embebidos, pools de entropía
   de Linux rotos) todo el árbol de confianza falla.

3. **Reloj del sistema dentro de ±5 minutos UTC.** La protección contra repetición está
   acotada por marca de tiempo. Un dispositivo con un reloj muy incorrecto rechaza cada
   paquete (reloj-demasiado-antiguo) o acepta repeticiones indefinidamente
   (reloj-demasiado-nuevo). Los anfitriones DEBEN incluir una verificación de cordura
   contra una fuente de tiempo de confianza al iniciar la aplicación.

4. **Consumo atómico de OPK.** Cuando un `ConsumeOneTimePreKeyAsync(id)` respaldado por
   `IPreKeyStore` se ejecuta concurrentemente con una operación X3DH del respondedor
   contra el mismo id, el consumo DEBE tener éxito o fallar atómicamente. El pool C# de
   referencia serializa el consumo bajo `_preKeyLock`; un almacén proporcionado por el
   anfitrión en un backend no transaccional (p. ej. un almacén de archivos simple con
   lectura-modificación-escritura) puede permitir que la misma OPK sea consumida dos
   veces, rompiendo la propiedad 2.6.
   `KeyValuePreKeyStore` usa `IKeyValueStore.RemoveAsync` directamente
   para el consumo — atómico siempre que el remove del KV subyacente sea atómico.

5. **Verificación de identidad en el primer contacto.** La clave pública de identidad del
   par fue verificada fuera de banda (número de seguridad, huella digital, directorio de
   confianza) antes del primer mensaje intercambiado — o el anfitrión acepta el riesgo
   TOFU y está dispuesto a detectar un cambio de clave en el próximo contacto. Sin esto,
   §3.6 es una ventana MitM abierta.

6. **La memoria del proceso del anfitrión no es legible por el adversario.** §3.1.

---

## 5. Debilidades conocidas + mitigaciones

### 5.1. MitM en el primer contacto (TOFU)

**Debilidad:** un atacante activo que controla el enlace entre pares durante el primer
intercambio de paquetes puede sustituir su propio paquete y enrutar el tráfico.
**Mitigación:** la UX del anfitrión debe exponer un flujo de comparación de número de
seguridad / huella digital de clave pública antes de tratar un contacto como verificado.
Una superficie de API pública para la derivación del número de seguridad aún no está
disponible en `AetherNet.Security`; rastreando como brecha.

### 5.2. Retraso en la rotación de la clave pre-firmada

**Debilidad:** hasta que el anfitrión llame a `RotateSignedPreKeyAsync`, el mismo SPK se
sirve en cada paquete. Un adversario que aprende la clave privada del SPK (p. ej. vía
§3.1 compromiso del punto final) puede ejecutar X3DH contra cualquier paquete capturado
desde la última rotación.
**Mitigación:** programar llamadas diarias a `RotateSignedPreKeyAsync`. Las
`SignedPreKeyRotationOptions` predeterminadas retienen 3 SPK anteriores para que los
mensajes en vuelo firmados bajo una clave recientemente rotada aún se descifren durante
la ventana de rotación. El intervalo de rotación predeterminado es de 7 días — los
adoptantes que ejecutan contra usuarios activamente atacados deben acortar esto.

### 5.3. Estado de sesión en memoria sin persistencia

**Debilidad:** si `SignalProtocolService` se construye sin un `sessionStore`, un bloqueo
o reinicio del proceso pierde cada sesión activa. El secreto hacia adelante está intacto
(las claves perdidas no pueden recuperarse) pero el siguiente mensaje del par fallará al
descifrar porque la cadena de recepción ya no existe.
**Mitigación:** conectar `KeyValueSignalSessionStore` contra un `IKeyValueStore` duradero
para cualquier despliegue en producción. La demostración de consola de ejemplo usa
`InMemoryDtnBundleStore` etc. por claridad; los anfitriones en producción no deben
hacerlo.

### 5.4. Ventana de transición del byte de bandera de compresión

**Debilidad:** `MessagingService` tiene un punto de extensión de compresión Brotli opcional
que antepone un byte de bandera incondicional al envelope de texto claro. Un par que
ejecute código anterior a la compresión leerá mal el byte de bandera como el primer byte
de la carga útil de la aplicación.
**Mitigación:** los adoptantes configuran `MessagingOptions.Compression.Enabled = false`
hasta que todos los pares tengan los nuevos componentes. El byte de bandera será
controlado por una futura negociación de capacidades. Ver la nota de migración en
`CompressionOptions`.

### 5.5. Brecha del lenguaje C — RESUELTA

**Antigua debilidad:** la implementación en C solo incluía los primitivos X25519 +
KDF_RK más el verificador de fixtures, sin una API completa de
`SignalProtocolService`.
**Resuelto.** `c/src/signal_protocol.c` ahora implementa el servicio de sesión
completo — establecimiento X3DH (verificación de la firma Ed25519 del SPK y luego
los 4 DH canónicos `DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) ||
DH(EK_A,OPK_B)` con raíz HKDF-SHA256 en
`aethernet_signal_process_pre_key_bundle`), el ciclo de vida OPK / SPK (pool
sembrado 1..100 en `aethernet_signal_service_init`, proyección de bundle
`has_pre_key`, OPK consumida en el lado del respondedor) e integración completa del
Double Ratchet (`dh_ratchet_receive`, `dh_ratchet_send_only`,
`aethernet_signal_encrypt` / `aethernet_signal_decrypt` sobre AES-256-GCM).
Los viajes de ida y vuelta E2E de dos nodos viven en `c/tests/test_signal_session.c`.
Los anfitriones en objetivos basados en C ahora pueden ejecutar tráfico cifrado de
extremo a extremo sobre la superficie C.

### 5.6. El pool OPK es solo de C# — RESUELTO

**Antigua debilidad:** el pool de 100 OPK con emisión FIFO y consumo atómico (defensa
2.6) era una característica exclusiva de C#; los demás idiomas emitían una sola OPK por
sesión, de modo que bajo carga de iniciadores simultáneos dos respondedores que competían
por el mismo origen de bundle podían observar la misma OPK y el X3DH podía producir una
discrepancia en el estado de sesión.
**Resuelto (los 8 idiomas).** Cada idioma con capacidad de sesión incluye ahora el mismo
pool de 100 OPK con emisión FIFO de una sola vez, recarga diferida y consumo de instancia
única protegido por cerrojo — ver la evidencia por idioma archivo:símbolo enumerada bajo
la defensa 2.6. El riesgo de iniciadores simultáneos está cerrado; no se requiere
ninguna serialización del consumo de bundles del lado del anfitrión.

### 5.7. Firma de demostración en lenguajes distintos de C#

**Debilidad:** los programas de demostración por idioma (Go, Python, TS, Rust, Swift,
Kotlin, C) firman los bytes serializados completos del cable para visualización en lugar
del búfer canónico `BuildSignableData`. El código de biblioteca en esos idiomas es
correcto — solo las demostraciones toman el atajo, pero es confuso para quien porta.
**Mitigación:** rastreado como `OPEN_ISSUES.md` §10. Tratar el Paso 3 de la demo en C#
como el flujo canónico.

---

## 6. Reportar problemas de seguridad

Ver [`SECURITY.md`](../SECURITY.md) para la política de divulgación responsable. Enviar
correo electrónico a `security@thegeeknetwork.co.za` con pasos de reproducción; esperar
acuse de recibo dentro de 48 horas y una evaluación inicial dentro de 7 días.

Los problemas que están fuera del alcance según la Sección 3 son igualmente bienvenidos
— preferimos saber de qué no nos estamos defendiendo a que un usuario descubra la brecha
en producción.
