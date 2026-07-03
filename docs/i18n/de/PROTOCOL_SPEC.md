# Aether Mesh-Netzwerkprotokoll – Spezifikation

**Version:** 2.0
**Status:** Abgeglichen mit HEAD (2026-05-05)
**Datum:** 2026-03-15 (Ersterstellung); 2026-05-05 (§2, §4, §10, §11 abgeglichen, §3/§9 verifiziert)
**Autoren:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **Hinweis für Leser.** Frühere Entwürfe dieses Dokuments stammen aus der
> Zeit vor der Angleichung des Wire-Formats über 8 Programmiersprachen sowie
> vor der systemweiten Umstellung auf X25519 + Signal Double Ratchet.
> Ab dem 2026-05-05 beschreiben §2 (Paketformat), §3 (Routing), §4
> (Schlüsselaustausch) und §9 (DTN) das implementierte Protokoll; §10
> (Video-Streaming) und §11 (Watch Together) beschreiben das Zielprotokoll –
> sie sind wire-definiert und mittels Fixtures getestet, jedoch sind die
> Codec-/BitTorrent-/ChipIn-Pipelines noch nicht an das Gerüst gebunden.
> Die C#-Referenzimplementierung ist überall dort maßgeblich, wo dieses
> Dokument und die Implementierung voneinander abweichen.
>
> - Kanonische Wire-Bytes: `fixtures/expected/*.bin` (10 benannte Testfälle)
> - Referenz-Serializer: `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - Referenz-Signal-Stack: `src/AetherNet.Security/Services/SignalProtocolService.cs`
> - Referenz-Routing: `src/AetherNet.Core/Routing/RoutingService.cs`
> - Referenz-DTN: `src/AetherNet.Core/Dtn/DtnService.cs`
> - Nachweis der sprachübergreifenden Wire-Interoperabilität: `fixtures/README.md`
> - Nachweis der sprachübergreifenden Signal-Interoperabilität: `fixtures/signal/README.md`

---

## Inhaltsverzeichnis

1. [Zusammenfassung](#1-zusammenfassung)
2. [Paketformat](#2-paketformat)
3. [Routing-Algorithmus](#3-routing-algorithmus)
4. [Schlüsselaustausch](#4-schlüsselaustausch)
5. [Anforderungen an die Transportschicht](#5-anforderungen-an-die-transportschicht)
6. [Discovery-Protokoll](#6-discovery-protokoll)
7. [Sicherheitsmodell](#7-sicherheitsmodell)
8. [SOS-Broadcast](#8-sos-broadcast)
9. [DTN Store-and-Forward](#9-dtn-store-and-forward)
10. [Video-Streaming](#10-video-streaming)
11. [Watch Together](#11-watch-together)
12. [Sicherheits- und Datenschutzschicht](#12-security--privacy-layer)

---

## 1. Zusammenfassung

Aether ist ein dezentralisiertes Mesh-Netzwerkprotokoll, das für Umgebungen mit unbeständiger oder fehlender Internetverbindung konzipiert ist. Es bietet Multi-Hop-Paketvermittlung über heterogene Kurzstrecken-Transportkanäle (Bluetooth Low Energy, Wi-Fi Direct, NearLink), Ende-zu-Ende-Verschlüsselung mittels eines von X3DH abgeleiteten Schlüsselaustauschs mit symmetrischer Ratsche, verzögerungstolerante Store-and-Forward-Zustellung sowie einen Notruf-SOS-Flutmechanismus. Das Protokoll ist transport-agnostisch: Jede physische Schicht, die Byte-Arrays zwischen Peers senden und empfangen kann, ist ein gültiger Aether-Transport. Knoten werden durch Universal Hardware Identifiers (UHIDs) identifiziert und über Ed25519-Identitätsschlüssel authentifiziert. Aether ist als universelle Netzwerkschicht gedacht – jede Anwendung des Ökosystems registriert Aether-Dienste, und Knoten ohne Internetverbindung erreichen das breitere Netzwerk über Gateway-Peers, die Mesh-Datenverkehr ins Internet weiterleiten.

---

## 2. Paketformat

> Abgeglichen am 2026-05-05 mit `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> und den 10 Fixture-Testfällen unter `fixtures/expected/`.

### 2.1. MeshPacket Wire-Layout

Jede Aether-Nachricht wird in einem `MeshPacket` gekapselt. Die Felder erscheinen
auf dem Wire in **genau** dieser Reihenfolge:

| Off | Field            | Type                            | Size       | Notes |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = unsigned (legacy), `2` = signed (current) |
| 1   | Type             | uint8                           | 1          | Packet type enumeration (see §2.4) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | Packet identifier for deduplication. **Big-endian** byte order, NOT .NET's mixed-endian Guid default. |
| 18  | Priority         | uint8                           | 1          | Priority level (0 = normal, 255 = SOS). **Wire field is 1 byte; values >255 must be clamped.** |
| 19  | Ttl              | int32, little-endian            | 4          | Time-to-live, decremented at each hop. **4-byte int32**, NOT 1-byte uint8 — values up to ~2³¹-1 are valid. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Unix epoch milliseconds (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | Length of `SourceUhid` in UTF-8 bytes. Max 65535. |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | Sender's UHID; empty allowed but unusual. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | Length of `DestinationUhid` in UTF-8 bytes. |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | Recipient's UHID; empty string for broadcast. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | Length of `PacketNonce` in bytes. Standard value: 8. |
| ... | PacketNonce      | bytes                           | P          | Cryptographically random nonce for replay prevention. |
| ... | Payload Len      | int32, little-endian            | 4          | Length of `Payload` in bytes. Negative values are an error. |
| ... | Payload          | bytes                           | Q          | Application data. Interpretation depends on `Type`. |
| ... | Signature Len    | uint16, little-endian           | 2          | Length of `Signature` in bytes. 0 (unsigned) or 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | Ed25519 signature over signable data (see §2.3). |

**Breite der Längenpräfixe** variiert je nach Feld – `SourceUhid`, `DestinationUhid`,
`PacketNonce` und `Signature` verwenden **2-Byte-(uint16)**-Längenpräfixe;
`Payload` verwendet einen **4-Byte-(int32)**-Längenpräfix, da Nutzdaten 64 KiB
überschreiten können.

### 2.2. Minimale Paketgröße

Wenn jedes Feld variabler Länge leer ist (UHIDs der Länge null, Nonce der Länge
null, Nutzdaten der Länge null, Signatur der Länge null), beträgt die Wire-Größe:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

Die in früheren Entwürfen dieser Spezifikation genannten Werte von 50 bzw. 52 Bytes
waren falsch.

### 2.3. Wire-Format-Diagramm

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

Ein ausgeführtes Beispiel findet sich unter `fixtures/expected/basic_data.bin`
(83 Bytes, kanonische Eingabe in `fixtures/inputs.json`). Implementierungen werden
gegen das vollständige Fixture-Korpus validiert – jede Abweichung lässt den
sprachübergreifenden Fixture-Verifier-Test fehlschlagen.

### 2.4. Aufbau der signierbaren Daten

Die Signatur (Feld `Signature` auf dem Wire) wird über eine separate kanonische
Byte-Sequenz berechnet – **nicht** über die Wire-Bytes selbst. Dies ermöglicht
die Weiterentwicklung des Wire-Layouts ohne Signaturbruch und erlaubt
Zwischenknoten die Integritätsprüfung, ohne den Klartext-Payload zu sehen
(nur dessen SHA-256-Hash wird signiert).

Die signierbare Byte-Sequenz ist die Konkatenation:

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

> Beachten Sie die bewusste Abweichung vom Wire-Layout in §2.1: Die
> signierbaren Daten verwenden **4-Byte-int32** für `Type`, `Length`, `Ttl`
> und `Priority`, während das Wire 1 Byte / 2 Byte / 4 Byte / 1 Byte
> verwendet. Dies ist beabsichtigt – die signierbare Form ist
> sprachübergreifend portierbar und verwendet Felder fester Breite; die
> Wire-Form ist kompakt für BLE-PDU-Effizienz. Implementierungen müssen
> `Priority` vor der Kodierung in signierbare Bytes auf `[0,255]` begrenzen,
> da der Empfänger (der den Wire-Byte 0..255 sieht) andernfalls einen
> abweichenden signierbaren Puffer ableitet und die Verifikation fehlschlägt.

Die Referenzimplementierung befindet sich in `src/AetherNet.Security/Services/
PacketSigningService.cs::BuildSignableData` und ist für Portierungen
Pflichtlektüre.

### 2.5. Pakettypen

| Value | Name              | Direction     | Description |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV Route Request |
| 2     | RouteReply        | Unicast       | AODV Route Reply (MUST be signed by destination) |
| 3     | Data              | Unicast       | Application data |
| 4     | Ack               | Unicast       | Delivery acknowledgment |
| 5     | SosBroadcast      | Flood         | Emergency broadcast (see Section 8) |
| 6     | SosAck            | Unicast       | SOS acknowledgment |
| 7     | ChannelMessage    | Multicast     | Group channel message |
| 8     | ChunkRequest      | Unicast       | P2P content chunk request |
| 9     | ChunkData         | Unicast       | P2P content chunk response |
| 10    | Heartbeat         | Broadcast     | Periodic liveness signal |
| 11    | StreamAnnounce    | Broadcast     | Live stream advertisement |
| 12    | StreamSegment     | Unicast/Tree  | Live stream media segment |
| 13    | StreamSubscribe   | Unicast       | Request to join stream relay tree |
| 14    | StreamUnsubscribe | Unicast       | Leave stream relay tree |
| 15    | VoicePtt          | Unicast       | Push-to-talk voice frame |
| 16    | VoiceCall         | Unicast       | Real-time voice call frame |
| 17    | VoiceSignaling    | Unicast       | Voice call setup/teardown |
| 18    | DtnBundle         | Unicast       | DTN store-and-forward bundle (see Section 9) |
| 19    | DtnCustodyAck     | Unicast       | DTN custody transfer acknowledgment |
| 20    | DtnDeliveryReceipt| Unicast       | DTN end-to-end delivery confirmation |
| 21    | PresenceBeacon    | Broadcast     | Presence and availability announcement |
| 22    | PresenceQuery     | Unicast       | Presence status request |
| 23    | ProfileSync       | Unicast       | Profile metadata synchronization |
| 24    | TipPacket         | Unicast       | Node tipping (settled via LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | Request peer's pre-key bundle |
| 26    | PreKeyResponse    | Unicast       | Pre-key bundle delivery |
| 27    | VideoCall         | Unicast       | Encrypted video frame (H.264/H.265/VP8 NAL unit) |
| 28    | VideoSignaling    | Unicast       | Video call setup: offer, answer, reject, bye, codec negotiation |
| 29    | WatchSync         | Unicast       | Synchronized playback command: play, pause, seek, speed |
| 30    | WatchReaction     | Multicast     | Timestamped emoji or voice reaction during watch-together |
| 31    | VideoFrame        | Unicast/SFU   | Group video frame (SFU relay distributes to participants) |
| 32    | ScreenShare       | Unicast       | Screen share frame (same pipeline as video, flagged separately) |
| 33    | WatchChunkRequest | Unicast       | Priority chunk request biased to playback position |
| 34    | TorrentMetadata   | Multicast     | BitTorrent .torrent file or magnet link metadata exchange |

### 2.6. Knotenkapazitäten

Knoten teilen ihre Fähigkeiten als Bitfeld mit:

| Bit | Value | Capability  | Description |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth Low Energy transport available |
| 1   | 2     | WifiDirect  | Wi-Fi Direct transport available |
| 2   | 4     | Gateway     | Internet gateway (bridges mesh to IP network) |
| 3   | 8     | Relay       | Willing to relay packets for others |
| 4   | 16    | Sos         | SOS broadcast capable |
| 5   | 32    | Streaming   | Live streaming relay capable |
| 6   | 64    | Voice       | Voice call relay capable |
| 7   | 128   | DtnCarrier  | DTN store-and-forward carrier |
| 8   | 256   | NearLink    | NearLink transport available |
| 9   | 512   | Video       | Video encoding/decoding capable |

---

## 3. Routing-Algorithmus

Aether verwendet ein reaktives Routing-Protokoll auf Basis des Ad-hoc On-demand
Distance Vector (AODV)-Routings, erweitert um kryptografische Routenauthentifizierung
und QoS-gewichtete Routenauswahl.

### 3.1. Route Request (RREQ)

Wenn ein Knoten ein Paket an ein Ziel senden muss, für das keine Route bekannt ist,
initiiert er einen Route Request:

1. Der Ursprungsknoten erstellt ein `MeshPacket` mit `Type = RouteRequest`, setzt
   `SourceUhid` auf sich selbst, `DestinationUhid` auf das Ziel und `TTL = 7`
   (Standardwert).
2. Das Paket wird an alle direkt verbundenen Peers gesendet (Broadcast).
3. Jeder Zwischenknoten, der einen RREQ empfängt:
   a. Prüft anhand der Paket-`Id`, ob dieser RREQ bereits gesehen wurde. Falls ja,
      wird das Paket stillschweigend verworfen (Deduplizierung). Der Deduplizierungs-
      Cache fasst bis zu `DeduplicationCacheSize` Einträge (Standard 10.000) und wird
      vollständig geleert, sobald die Grenze erreicht ist.
   b. Installiert eine **Rückwärtsroute** zum RREQ-Ursprungsknoten. Die Rückwärtsroute
      speichert die UHID des Peers, von dem der RREQ empfangen wurde, als nächsten Hop.
      Die Hop-Anzahl wird aus `DefaultTtl - packet.Ttl + 1` abgeleitet.
   c. Ist er das Ziel, generiert er ein RREP (siehe Abschnitt 3.2).
   d. Besitzt er eine gültige Route zum Ziel, DARF er stellvertretend ein RREP erzeugen.
   e. Andernfalls dekrementiert er die TTL und leitet den RREQ weiter (Re-Broadcast).
4. Der Ursprungsknoten wartet mit einem Timeout von **5.000 ms** (`RouteTimeoutMs`)
   auf ein RREP. Bleibt ein RREP aus, schlägt die Routenentdeckung fehl.

### 3.2. Route Reply (RREP)

Wenn das Ziel (oder ein Zwischenknoten mit gültiger Route) eine Route Reply erzeugt:

1. Ein `MeshPacket` mit `Type = RouteReply` wird erstellt, `SourceUhid` auf den
   Zielknoten und `DestinationUhid` auf den RREQ-Ursprungsknoten gesetzt.
2. **SICHERHEITSANFORDERUNG:** Das RREP MUSS vom Ed25519-Identitätsschlüssel des
   Zielknotens signiert sein. Die Signatur deckt die standardmäßigen signierbaren
   Daten ab (Abschnitt 2.3). Dies verhindert Route-Poisoning durch bösartige
   Zwischenknoten.
3. Das RREP wird per Unicast entlang der während der RREQ-Weiterleitung installierten
   Rückwärtsroute zurückgeleitet.
4. Jeder Zwischenknoten, der das RREP weiterleitet:
   a. Verifiziert die RREP-Signatur anhand des öffentlichen Schlüssels der angegebenen
      Quelle (sofern bekannt). Schlägt die Verifikation fehl, wird das RREP verworfen
      und eine Warnung protokolliert.
   b. Installiert eine **Vorwärtsroute** zum RREP-Absender (dem Zielknoten) mit dem
      RREP-Sender als nächstem Hop.
   c. Dekrementiert die TTL und leitet das Paket Richtung RREQ-Ursprungsknoten weiter.
5. Wenn das RREP den Ursprungsknoten erreicht, wird die ausstehende Routenanfrage
   (verfolgt über `TaskCompletionSource`) mit der installierten Route aufgelöst.

### 3.3. Routenpflege

- **TTL-basierter Ablauf:** Jeder Routeneintrag trägt einen `ExpiresAt`-Zeitstempel,
  der auf `jetzt + 300 Sekunden` (`RouteExpirySeconds`) gesetzt ist. Routen werden
  nicht implizit erneuert; sie müssen nach Ablauf durch einen neuen RREQ/RREP-Zyklus
  neu aufgebaut werden.
- **Periodisches Bereinigen:** Der Protokolldienst führt einen periodischen Heartbeat
  durch (standardmäßig alle 300 Sekunden). In jedem Zyklus werden abgelaufene Routen
  sowohl aus dem In-Memory-`ConcurrentDictionary` als auch aus dem SQLite-Backing-Store
  entfernt.
- **RREQ-Dedup-Bereinigung:** Der Satz gesehener RREQ-IDs wird geleert, wenn er
  `DeduplicationCacheSize` (Standard 10.000) Einträge überschreitet.

### 3.4. Routenqualität und QoS

Jeder `RouteEntry` trägt einen `QualityScore` im Bereich [0, 100], der für neu
entdeckte Routen auf 50 initialisiert wird. Der Score berücksichtigt:

- **Hop-Anzahl:** Weniger Hops deuten im Allgemeinen auf eine schnellere Route hin.
- **Latenz:** Gemessene Umlaufzeit, sofern verfügbar.
- **Peer-Zuverlässigkeit:** Der Zuverlässigkeitswert des nächsten Hop-Peers
  (siehe Abschnitt 3.5).

Knoten, die am Tipping-Anreizsystem teilnehmen, erhalten einen QoS-Bonus auf ihren
Routenqualitätswert. Dies ist eine weiche Präferenz: Nicht-Tipper erhalten stets
Dienst, aber regelmäßige Tipper erfahren möglicherweise eine geringfügig bessere
Routenauswahl. Die Bonus-Stufen sind:

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. Peer-Zuverlässigkeitsbewertung

Jedem bekannten Peer wird ein Zuverlässigkeitswert im Bereich [0, 100] zugewiesen,
der auf 50 (`DefaultReliabilityScore`) initialisiert wird. Der Wert wird auf
Grundlage beobachteten Verhaltens angepasst:

| Event                | Delta |
|----------------------|-------|
| Successful relay     | +2    |
| Failed relay         | -5    |
| SOS relay            | +5    |
| Chunk served         | +1    |
| Chunk serve failure  | -10   |

Zuverlässigkeitswerte werden in SQLite gespeichert und beim Start in den Speicher
geladen. Der Wert beeinflusst die Routenauswahl: Routen über zuverlässigere Peers
werden bevorzugt.

---

## 4. Schlüsselaustausch

> Abgeglichen am 2026-05-05 mit der C#-Referenzimplementierung unter
> `src/AetherNet.Security/Services/SignalProtocolService.cs` und dem
> sprachübergreifenden Fixture-Korpus unter `fixtures/signal/`. Die
> C#-Referenz implementiert vollständiges X3DH + Double Ratchet (Signal §3 +
> §5) über X25519. Go, Python, TypeScript, Rust, Swift und Kotlin wurden auf
> denselben Umschlag portiert und sind auf Fixture-Ebene (X3DH und KDF_RK)
> byte-äquivalent. C implementiert nur die primitiven X25519- + KDF_RK- +
> Symmetric-Ratchet-Bausteine – ausreichend für den Fixture-Verifier, noch
> keine vollständige Session-Maschinerie. Bei Abweichungen zwischen diesem
> Abschnitt und dem Code ist der Code maßgeblich; bitte einen Issue in
> `OPEN_ISSUES.md` anlegen.

Aether implementiert **X3DH** (Extended Triple Diffie-Hellman, Signal §3) für
den asynchronen Sitzungsaufbau, dem unmittelbar der **Signal Double Ratchet**
(Signal §5) für fortlaufende Forward Secrecy und Post-Compromise Security folgt.
Die gesamte Sitzungskryptografie läuft über Curve25519: **X25519** (RFC 7748)
für ECDH und **Ed25519** (RFC 8032) für Signaturen.

### 4.1. Identitätsschlüssel

Jeder Knoten generiert beim ersten Start **zwei** langfristige Schlüsselpaare
(kein XEdDSA; die einfachere Dual-Key-Anordnung ist das, was jede Implementierung
liefert):

- **Ed25519-Schlüsselpaar** – 32-Byte-Seed (privat), 32-Byte-öffentlicher Schlüssel.
  Verwendet für Paketsignaturen (§2.4), `SignedPreKeySignature` (§4.3),
  RREP-Authentifizierung (§3.2) und Tip-Signaturen.
- **X25519-Schlüsselpaar** – 32-Byte rohe private und öffentliche Schlüssel.
  Verwendet für die vier X3DH-DH-Operationen (§4.4).

Referenz: `SignalProtocolService.InitializeIdentityKeys`. Private Schlüssel
verbleiben ausschließlich auf dem Gerät; öffentliche Schlüssel werden im
`PreKeyBundle` veröffentlicht.

Ein 30-tägiges P-256 → Ed25519-Migrationsfenster wird **ausschließlich** für die
*Signaturverifizierung* eingehender Pakete berücksichtigt – siehe §7.5. Pre-Key-
Bundles selbst sind auf dem Wire ausschließlich X25519.

### 4.2. Kurven-Auswahl

X3DH und der Double Ratchet verwenden **ausschließlich X25519**. P-256 wird in
keiner aktuellen Implementierung für den Sitzungsaufbau verwendet. Ein früherer
Entwurf dieser Spezifikation beschrieb P-256-ECDH; dieser Text stammt aus der
Zeit vor der systemweiten Umstellung auf X25519 am 2026-05-05 und ist nicht mehr
zutreffend.

### 4.3. Pre-Key-Bundle

Ein Pre-Key-Bundle wird veröffentlicht, damit ein Initiator eine Sitzung aufbauen
kann, ohne dass der Responder online ist (Signal §3.4):

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

Referenz: `AetherNet.Security.Models.PreKeyBundle`. Der Wire-Shape-Vertrag ist über
alle 8 Sprachen hinweg identisch.

**One-Time Pre-Key (OPK)-Pool.** Jeder Responder verwaltet einen Pool von
`OpkPoolSize` (Standard 100, entsprechend Signal-Empfehlung) X25519-OPKs.
Die Bundle-Erzeugung entnimmt die nächste unbenutzte ID aus einer FIFO-Warteschlange
und füllt den Pool anschließend auf seine Zielgröße auf. Jede OPK wird exakt einmal
verbraucht: Der Responder entfernt und überschreibt die private Hälfte beim ersten
PreKey-Message, das ihre ID referenziert. Konkurrierende Initiatoren, die um dieselbe
OPK-ID wetteifern, werden unter `_preKeyLock` genau einmal `EstablishResponderSession`
erfolgreich ausführen; der Verlierer löst eine `CryptographicException` aus.

Referenz: `SignalProtocolService.TopUpOpkPoolNoLock` (Zeilen 494–518),
`SignalProtocolService.EstablishResponderSession` (Zeilen 636–718). Pool-Semantik
wird durch `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs` geprüft.

**Signed Pre-Key (SPK)-Rotation.** Der SPK wird beim ersten Bundle-Aufruf lazily
erzeugt und bei nachfolgenden Aufrufen wiederverwendet, damit konkurrierende
Initiatoren, die Bundles vor dem X3DH-Lauf abrufen, sich nicht gegenseitig
entwerten. Die periodische SPK-Rotation (Signal §3.3 empfiehlt wöchentlich) ist
eine explizite Operation und kein Nebeneffekt der Bundle-Erzeugung.

Pre-Key-IDs werden aus `RandomNumberGenerator.GetInt32(1, int.MaxValue)` mit
expliziter Kollisionswiederholung gezogen (bis zu 64 Versuche, danach Ausnahme).

### 4.4. Sitzungsaufbau (X3DH)

Das vollständige X3DH (Signal §3.3) läuft auf Initiatorseite. Vier DH-Operationen
werden über X25519 berechnet:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

Dabei sind `IK_A` / `IK_B` die X25519-Identitätsschlüssel, `EK_A` ein frisch für
diese Sitzung erzeugtes X25519-Ephemeral, `SPK_B` der signierte Pre-Key des
Responders und `OPK_B` der One-Time Pre-Key des Responders. Der initiale Root-Key
lautet:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

Die `info`-Konstante `aether-x3dh-root-v1` ist über alle Implementierungen
identisch und durch `fixtures/signal/expected/x3dh_basic.json` (Feld
`root_key_hex`) festgelegt.

Referenz: `SignalProtocolService.ProcessPreKeyBundleAsync` (Zeilen 554–626).
Verifikationspfad: Testfall `x3dh_basic` in `fixtures/signal/inputs.json` →
`fixtures/signal/expected/x3dh_basic.json`.

**Bundle-Verifikation.** Bevor DH-Operationen ausgeführt werden, verifiziert der
Initiator `SignedPreKeySignature` gegen `IdentityKey` mittels Ed25519. Eine
fehlgeschlagene Verifikation löst eine `CryptographicException` aus und das
Bundle wird verworfen. Öffentliche Schlüsselgrößen werden gegen
`X25519Service.PublicKeySize` (32) geprüft; fehlerhafte Bundles werden abgewiesen.

**Sitzungs-Priming.** Am Ende von `ProcessPreKeyBundleAsync` wird eine
`SignalSession` erstellt mit:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` – Signal-kanonische X3DH ↔
  Double-Ratchet-Integration: Das X3DH-Ephemeral des Initiators wird sein erstes
  DH-Ratchet-Schlüsselpaar (`DHs`).
- `RemoteEphemeralPub = SPK_B` – Der signierte Pre-Key des Responders wird als
  initialer Peer-Ratchet-Schlüssel (`DHr`) behandelt.
- `SendChainKey = null`, `RecvChainKey = null` – Beide Chain-Keys werden lazily
  beim ersten Senden / ersten DH-Ratchet-Empfang abgeleitet.
- `PendingPreKeyMessage = true` – Signalisiert, dass der nächste ausgehende
  `EncryptAsync`-Aufruf eine PreKey-Message (`MessageType=1`) ausgeben MUSS.

Alle DH-Ausgaben und das verkettete Shared Secret werden im `finally`-Block via
`CryptographicOperations.ZeroMemory` genullt.

**Verweigerung unsicherer Übertragung.** Wird `EncryptAsync` für einen Peer ohne
bestehende Sitzung aufgerufen, wirft der Aufruf `InvalidOperationException`.
Es gibt keinen UHID-basierten Fallback-Pfad. Hosts sollen die Nachricht
einreihen (siehe `MessagingService` + `SignalMessageEnvelopeCipher`) und nach
Abschluss des Sitzungsaufbaus erneut versuchen.

### 4.5. Double Ratchet (Signal §5)

Jede Seite verwaltet ein rotierendes X25519-Ratchet-Schlüsselpaar (`DHs`) und
eine Kopie des zuletzt gesehenen Ratchet-Public-Keys des Peers (`DHr`). Bei
jeder Nachricht veröffentlicht der Sender seinen aktuellen `DHs`-Public-Key;
sobald der Empfänger ein neues `DHr` beobachtet, führt er einen
**DH-Ratchet-Schritt** durch, der die Chain via `KDF_RK(RK, DH(myDHs, newDHr))`
neu schlüsselt – sowohl Root-Key als auch Fresh-Chain-Key werden neu abgeleitet.

#### 4.5.1. KDF_RK

`KDF_RK` ist HKDF-SHA256 über einen 64-Byte-Block, aufgeteilt 32+32 in den
neuen Root-Key und den neuen Chain-Key:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

Referenz: `SignalProtocolService.KdfRk` (Zeilen 857–868). Festgelegt durch
Testfall `kdf_rk_basic` in `fixtures/signal/inputs.json` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Symmetrische Ratsche

Gemäß Signal §5.1 werden Message-Keys und Chain-Keys aus einem Chain-Key mittels
HMAC-SHA256 mit Ein-Byte-Domain-Separation abgeleitet:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Referenz: `SignalProtocolService.RatchetChainKey` (Zeilen 876–881).
Festgelegt durch Testfälle `ratchet_step_basic` und
`ratchet_step_three_iterations` in `fixtures/signal/inputs.json`.

Ein früherer Entwurf dieser Spezifikation beschrieb `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` und einen separaten `chain_key`-Vorschub
via `HMAC(chain_key, 0x01)`. Das war nicht-Signal-konform und wurde nie
implementiert; es wurde durch den kanonischen 0x01/0x02-Split ersetzt.

#### 4.5.3. DH-Ratchet-Schritt beim Empfang

Ausgelöst, wenn sich der `SenderEphemeralKeyX25519` der eingehenden Nachricht
vom gecachten `RemoteEphemeralPub` unterscheidet (Constant-Time-Vergleich).

1. Ausgehenden Zähler als `PreviousChainCount` speichern (Signal §5: PN), damit
   der Peer übersprungene Keys über die Grenze hinweg berechnen kann.
2. `SendCounter` und `RecvCounter` auf 0 zurücksetzen; den neuen
   `RemoteEphemeralPub` installieren.
3. Neuen Empfangs-Chain ableiten: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Altes `myDHs`-Private nullen; frisches X25519-Schlüsselpaar erzeugen.
5. Neuen Sende-Chain ableiten: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Referenz: `SignalProtocolService.DhRatchetReceive` (Zeilen 726–772).

#### 4.5.4. Lazy-Sende-Chain-Ableitung

Der erste Sendevorgang des Initiators führt einen **Halb-Schritt** statt eines
vollständigen DH-Ratchets durch – X3DH hat `DHs` und `DHr` bereits gesetzt, sodass
nur der Sende-Chain abgeleitet werden muss:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` wird *nicht* hier rotiert. Es wird nur bei einem echten empfangsseitigen
DH-Ratchet-Schritt rotiert.

Referenz: `SignalProtocolService.DhRatchetSendOnly` (Zeilen 780–796).

#### 4.5.5. Übersprungene Message-Keys

Wenn Nachrichten außer der Reihe ankommen, wird der Message-Key jedes
übersprungenen Zählers in `SkippedMessageKeys` gecacht, verschlüsselt mit
`(Hex(remoteEphPub):counter)`. Die Remote-Pub-Bindung ist essenziell – außer-der-
Reihe-Nachrichten einer früheren Chain (anderes `DHr`) können nach einem
DH-Ratchet-Schritt noch eintreffen und benötigen ihren eigenen Per-Chain-Keyset.

Grenzen:

- Mehr als `MaxSkippedKeys` (1000) Einträge in einer einzelnen Lücke zu überspringen
  löst eine `CryptographicException` aus und erzwingt eine Sitzungs-Neuetablierung.
- An einer DH-Ratchet-Grenze überspringt der Empfänger zunächst bis zu
  `PreviousChainCount` Keys der *alten* Chain, führt dann den DH-Ratchet-Schritt
  durch und leitet erst danach Keys der neuen Chain ab.

Referenz: `SignalProtocolService.SkipMessageKeys` (Zeilen 804–830) und die
In-Decrypt-Skip-Schleife (Zeilen 366–388).

### 4.6. Verschlüsseltes Payload-Format

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

Referenz: `AetherNet.Security.Models.EncryptedPayload` (Zeilen 55–66 in
`SecurityModels.cs`). Das Feld `InitiatorEphemeralKeyX25519` ist ein
Rückwärtskompatibilitäts-Alias für den Pre-Double-Ratchet-Wire-Umschlag und
entspricht `SenderEphemeralKeyX25519` bei PreKey-Messages; neue Konsumenten
sollen es ignorieren.

AES-GCM-Parameter: 256-Bit-Schlüssel, 96-Bit-Nonce (`AesNonceSize = 12`),
128-Bit-Tag (`AesTagSize = 16`), Tag wird an den Ciphertext angehängt.
Message-Keys werden in `finally`-Blöcken unmittelbar nach AES-GCM-Verschlüsselung/
Entschlüsselung genullt.

### 4.7. Status je Programmiersprache

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | primitives only — `aethernet_x25519_*`, `aethernet_signal_kdf_rk` | not implemented | — | kdf_rk_basic only |

Alle 7 session-fähigen Sprachen (C# + Go + TypeScript + Python + Kotlin + Swift + Rust)
implementieren den 100-Key-FIFO-OPK-Pool mit Lazy-Top-Up und lock-geschütztem Verbrauch,
entsprechend dem C#-Referenzvertrag. C implementiert nur Primitive; vollständige
Session-Maschinerie wird in `OPEN_ISSUES.md` Punkt 11 verfolgt.

---

## 5. Anforderungen an die Transportschicht

Aether ist transport-agnostisch. Jeder physische Kommunikationskanal, der den
`ITransportService`-Vertrag erfüllt, kann am Mesh teilnehmen.

### 5.1. ITransportService Interface-Vertrag

Jede Transport-Implementierung MUSS Folgendes bereitstellen:

**Eigenschaften:**

| Property           | Type   | Description |
|--------------------|--------|-------------|
| `Name`             | string | Human-readable identifier (e.g., "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Whether the transport is currently usable on this device |
| `MaxBandwidthBps`  | int64  | Maximum throughput in bytes per second |
| `MaxRangeMeters`   | int32  | Maximum communication range in meters |
| `PowerCostRelative`| int32  | Relative power consumption (1 = low, 10 = high) |
| `MaxConcurrentPeers` | int32 | Maximum simultaneous peer connections |

**Methoden:**

| Method         | Signature | Description |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Send a byte array to a specific peer. Returns true on success. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Send a stream to a peer (for large transfers, voice, video). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Check if a connection is active to a peer. |

**Ereignisse:**

| Event          | Signature | Description |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Fired when data arrives from a peer. |

### 5.2. Transport-Auswahlalgorithmus

Der `TransportManager` wählt für jedes Paket den optimalen Transport auf Basis
folgender Kriterien:

1. **Verfügbarkeit:** Nur Transporte, bei denen `IsAvailable == true`, werden
   berücksichtigt.
2. **Payload-Größe:** Liegt die Payload-Größe bei oder unter `BleMaxPayloadBytes`
   (1.024 Bytes), wird BLE aus Energieeffizienzgründen bevorzugt. Größere Payloads
   bevorzugen Wi-Fi Direct.
3. **Leistungskostengewichtung:** Unter verfügbaren Transporten werden niedrigere
   `PowerCostRelative`-Werte für Routineverkehr bevorzugt. Hochprioritätspakete
   (SOS, Sprache) können diese Präferenz übersteuern.
4. **Peer-Konnektivität:** Besteht zu einem Transport bereits eine aktive Verbindung
   zum Ziel-Peer (`IsConnected` gibt true zurück), wird er bevorzugt, um
   Verbindungsaufbau-Overhead zu vermeiden.
5. **Fallback:** Kann kein lokaler Transport das Ziel erreichen, wird das Paket
   für den Server-Relay über AetherNetAPI eingereiht.

### 5.3. Referenz-Transporte

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | Notes |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | Primary discovery + small packets |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | Large transfers, streaming, voice |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon, high throughput |

**BLE-Payload-Grenze:** Pakete, die 1.024 Bytes (`BleMaxPayloadBytes`) überschreiten,
werden automatisch über Wi-Fi Direct oder NearLink geleitet. BLE wird für
Discovery-Advertisements, kleine Steuerungspakete (RREQ/RREP, Presence-Beacons)
und Nachrichten mit geringer Bandbreite verwendet.

**Wi-Fi Direct**: Verbindungs-Timeout beträgt 10.000 ms (`WifiDirectTimeoutMs`)
bei maximal 8 gleichzeitigen Peers (`MaxWifiDirectPeers`).

---

## 6. Discovery-Protokoll

### 6.1. BLE-Advertising

Aether-Knoten entdecken sich primär durch BLE-Advertising. Um persistentes Tracking
über statische Identifikatoren zu verhindern, setzt das Protokoll zwei
Datenschutzmechanismen ein: rotierende Service-UUIDs und Identity Resolving Keys.

**Advertising-Zyklus:** 2 Sekunden Scan an, 8 Sekunden aus (`BleScanOnMs`/
`BleScanOffMs`). Das Advertise-Intervall beträgt 1.000 ms (`BleAdvertiseIntervalMs`).
Zum Scan-Intervall wird ein zufälliger Jitter von 0–2.000 ms (`BleScanJitterMaxMs`)
hinzugefügt, um die Erkennung von Timing-Mustern zu verhindern.

**Peer-Timeout:** Ein Peer, der innerhalb von 30 Sekunden nicht erneut entdeckt
wird, gilt als verloren (`PeerLost`-Ereignis).

### 6.2. Rotierende Service-UUID

Um langfristiges BLE-Fingerprinting zu verhindern, rotiert die in Advertisements
verwendete Service-UUID alle 15 Minuten (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

Der `rotation_key` ist ein 32-Byte-Schlüssel, der einmalig pro Knoten erzeugt und
in sicherem Speicher abgelegt wird. Alle Aether-Knoten, die denselben Rotation-Key
teilen, leiten für ein gegebenes Zeitfenster dieselbe UUID ab und ermöglichen so
die gegenseitige Entdeckung ohne Preisgabe eines permanenten Identifikators.

Eine statische Fallback-UUID (`A3E7-1001-0001-0000-000000000000`) wird 90 Tage
lang während des Übergangs vom nicht-rotierenden Schema aufrechterhalten.

### 6.3. Identity Resolving Key (IRK)

Jeder Knoten erzeugt einen 128-Bit Identity Resolving Key (IRK), der in sicherem
Speicher abgelegt wird. Der IRK wird beim Schlüsselaustausch mit vertrauenswürdigen
Peers geteilt.

**Resolvable Private Address (RPA)-Erzeugung:**

1. `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` berechnen (3 Bytes).
2. Die zwei signifikantesten Bits von `prand[0]` auf `01` setzen (RPA-Flag gemäß
   BLE-Spezifikation).
3. `hash = AES-128-ECB(IRK, pad(prand))` berechnen, wobei `prand` die Bytes 13–15
   einer 16-Byte-null-aufgefüllten Eingabe belegt.
4. RPA konstruieren: `hash[0..2] || prand[0..2]` (insgesamt 6 Bytes).

**RPA-Auflösung:** Ein Knoten, der den IRK eines Peers besitzt, kann überprüfen, ob
eine beobachtete RPA zu diesem Peer gehört, indem er den Hash aus der
`prand`-Komponente der RPA neu berechnet. Die Auflösungszeit beträgt ca. O(N), wobei
N die Anzahl bekannter IRKs ist; Benchmark: ~0,1 ms für 100 Peers.

Die RPA rotiert im selben 15-Minuten-Zyklus wie die Service-UUID.

### 6.4. Geohash-basierte Nähe

Knoten kodieren optional ihren Standort als Geohash. Aus Datenschutzgründen wird
der Geohash auf 4 Zeichen gekürzt, was eine Auflösung von ca. 39 km × 20 km bietet.
Diese Granularität ist ausreichend für:

- Nähebasierte Channel-Entdeckung
- DTN-Epidemie-Routing (Replikation Richtung letzter bekannter Geohash-Region des
  Empfängers)
- Geografischen Kontext von SOS-Warnungen

Der vollpräzise Geohash wird niemals über das Mesh übertragen. Nur die gekürzte Form
wird geteilt, und nur wenn das Datenschutzniveau des Knotens dies erlaubt
(`PrivacyLevel.Full` oder `PrivacyLevel.Partial`).

---

## 7. Sicherheitsmodell

### 7.1. Bedrohungsmodell

Aether geht von folgenden Fähigkeiten des Angreifers aus:

- **Passives Abhören:** Der Angreifer kann alle BLE-Advertisements und Mesh-Verkehr
  in Funkreichweite beobachten.
- **Aktive Einschleusung:** Der Angreifer kann Pakete einschleusen, verändern oder
  wiederholen.
- **Sybil-Angriff:** Der Angreifer kann mehrere gefälschte Knotenidentitäten erstellen.
- **Selektive Dienstverweigerung:** Der Angreifer kann als Relay-Knoten Pakete selektiv
  verwerfen.

### 7.2. Was geschützt wird

| Property | Protection Level | Mechanism |
|----------|-----------------|-----------|
| Nachrichteninhalt | Vollständige Vertraulichkeit | AES-256-GCM mit Schlüsseln pro Nachricht (Abschnitt 4.5) |
| Absenderidentität | Teilweise | UHID sichtbar in Paketheadern; BLE-Adresse rotiert (Abschnitt 6) |
| Empfängeridentität | Teilweise | Ziel-UHID in gerouteten Paketen sichtbar; Broadcast-Pakete haben leeres Ziel |
| Routing-Metadaten | Minimal | Zwischenknoten sehen Quell-/Ziel-UHIDs und TTL |
| Nachrichtenreihenfolge | Geschützt | Zähler in symmetrischer Ratsche verhindern Umsortierung |
| Nachrichtenintegrität | Vollständig | Ed25519-Signatur auf jedem Paket (v2) |

### 7.3. Angriffsresistenz

**Replay-Angriffe:**
Jedes Paket trägt eine kryptografisch zufällige 8-Byte-Nonce und einen
millisekunden-genauen Zeitstempel. Relay-Knoten führen einen Deduplizierungs-Cache
mit `(SenderUhid, NonceValue)`-Paaren mit einer TTL von 5 Minuten
(`MaxPacketAgeSeconds = 300`). Ein Paket mit einer duplizierten Nonce desselben
Senders wird verworfen. Pakete mit Zeitstempeln älter als 5 Minuten werden
unabhängig von der Nonce abgelehnt.

Der Nonce-Dedup-Cache wird alle 60 Sekunden bereinigt. Abgelaufene Einträge
(älter als 5 Minuten) werden entfernt.

**Man-in-the-Middle (MITM):**
- Route-Reply-Pakete MÜSSEN eine gültige Ed25519-Signatur des angeblichen Zielknotens
  tragen. Zwischenknoten können keine RREPs fälschen, da sie den privaten Schlüssel
  des Ziels nicht besitzen.
- Pre-Key-Bundles enthalten eine `SignedPreKeySignature` (Ed25519) über den
  `SignedPreKey`, die den ephemeren ECDH-Schlüssel an die langfristige Identität bindet.
- Der Sitzungsaufbau (Abschnitt 4.4) bindet die Sitzung durch den Pre-Key-
  Verifikationsschritt kryptografisch an die Identitäten beider Parteien.

**Sybil-Angriffe:**
- Der Zuverlässigkeitswert jedes Knotens startet bei 50 und wird auf Basis
  beobachteten Verhaltens angepasst (Abschnitt 3.5). Neu erstellte Sybil-Knoten
  haben keine angesammelte Reputation.
- Knoten mit niedrigen Zuverlässigkeitswerten (gegen 0) werden bei der Routenauswahl
  deprioritisiert.
- Der DTN-Epidemie-Routing-Algorithmus verwendet Geohash-Nähe und
  Relay-Erfolgshistorie zur Auswahl von Replikationszielen, was es für Sybil-Knoten
  schwerer macht, Datenverkehr anzuziehen, ohne echte Relay-Beiträge zu leisten.

**Flooding-Angriffe:**
- Die TTL wird an jedem Hop dekrementiert, und Pakete mit TTL = 0 werden verworfen.
  Die Standard-TTL von 7 begrenzt den Wirkungsradius eines Broadcasts.
- RREQ-Deduplizierung anhand der Paket-ID verhindert Verstärkung durch
  Broadcast-Stürme. Der Dedup-Cache wird geleert, wenn er `DeduplicationCacheSize`
  (Standard 10.000) Einträge überschreitet.
- SOS-Broadcasts sind auf 3 pro Stunde pro Knoten ratenbegrenzt (Abschnitt 8).

### 7.4. Schlüssel-Nullung

Alle intermediären kryptografischen Materialien werden unmittelbar nach der
Verwendung genullt:

- `sharedSecret` aus ECDH-Schlüsselvereinbarung: genullt nach HKDF-Ableitung.
- `messageKey` aus Chain-Ratsche: genullt nach AES-GCM-Verschlüsselung/Entschlüsselung.
- `skippedKey` aus Außer-der-Reihe-Entschlüsselung: genullt nach Verwendung und aus
  der Map entfernt.
- Abgeleiteter `RootKey`, `SendChainKey`, `RecvChainKey`: aus dem Etablierungskontext
  genullt (die Sitzung behält ihre eigenen Kopien).

Die Nullung erfolgt mittels `CryptographicOperations.ZeroMemory`, das garantiert
nicht vom Compiler wegoptimiert wird.

### 7.5. P-256 zu Ed25519-Migration

Das Protokoll unterstützt ein 30-tägiges Übergangsfenster von ECDSA-P-256-
Identitätsschlüsseln (Protokollversion 1) zu Ed25519 (Protokollversion 2):

1. Protokollversion-1-Pakete (unsigniert) werden während des Übergangszeitraums
   akzeptiert.
2. Die Signaturverifikation versucht zunächst Ed25519. Ist der öffentliche Schlüssel
   länger als 32 Bytes (Hinweis auf einen DER-kodierten P-256-Schlüssel), wird auf
   P-256-ECDSA-Verifikation zurückgegriffen.
3. Nach dem 30-tägigen Fenster werden Protokollversion-1-Pakete abgewiesen.
4. Knoten, die nicht migriert haben, müssen sich mit einem neuen
   Ed25519-Identitätsschlüssel neu initialisieren.

### 7.6. Jurisdiktionsbewusstsein

Das Protokoll definiert Jurisdiktionsstufen, um unterschiedliche rechtliche
Anforderungen an Verschlüsselung und Mesh-Netzwerke zu handhaben:

| Tier | Behavior | Example Jurisdictions |
|------|----------|-----------------------|
| 1    | Freier Betrieb | South Africa, Kenya, Ghana |
| 2    | Modifizierter Betrieb | Nigeria, India, EU, US, UK |
| 3    | Nur Mesh (hohes Risiko) | China, Russia, Iran, UAE, Myanmar |
| 4    | Unbekannt (Standard: nur Mesh) | All others |

Die Stufenauswahl beeinflusst die Funktionsverfügbarkeit (z. B. können
Tipping-/Finanzierfunktionen in Stufe 3 deaktiviert sein), schwächt jedoch nicht
die Verschlüsselung. Ende-zu-Ende-Verschlüsselung wird stets angewendet,
unabhängig von der Jurisdiktion.

---

## 8. SOS-Broadcast

Der SOS-Mechanismus ist ein dualpfadiger Notfall-Flood, der für Situationen
konzipiert ist, in denen ein Nutzer in Gefahr ist und nahegelegene Mesh-Peers
und/oder das Internet gleichzeitig erreichen muss.

### 8.1. Broadcast-Parameter

| Parameter | Value | Description |
|-----------|-------|-------------|
| TTL       | 15    | Doppelt der normalen Vorgabe (7), für breitere Ausbreitung |
| Priority  | 999   | Maximale Priorität; verdrängt allen anderen Datenverkehr in Relay-Warteschlangen |
| Rate limit| 3/hour| Grenze pro Knoten zur Missbrauchsverhinderung |
| Destination| empty | Broadcast an alle Peers (kein spezifisches Ziel) |

### 8.2. Flood-Algorithmus

1. Der Ursprungsknoten erstellt ein SOS-Paket mit `Type = SosBroadcast`,
   `TTL = 15`, `Priority = 999` und einem leeren `DestinationUhid`.
2. Der Payload ist JSON-kodiert und enthält:
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
3. **Dualpfad-Versendung:** Der SOS wird gleichzeitig versendet über:
   - **Mesh-Flood:** Broadcast an alle verbundenen Peers über alle verfügbaren
     Transporte.
   - **API-Aufruf:** Gesendet an AetherNetAPI für serverseitige Verteilung und
     Bridging zu PanikAPI (SMS-/E-Mail-Versendung).
4. Beide Pfade sind relativ zueinander Fire-and-Forget. Schlägt der API-Aufruf
   fehl, wird der Mesh-Flood unabhängig fortgesetzt.

### 8.3. Relay-Verhalten

Wenn ein Knoten ein SOS-Paket empfängt:

1. Deduplizierung anhand der Paket-`Id` prüfen. Wurde das Paket bereits gesehen,
   still verwerfen.
2. Den Payload deserialisieren und das `SosReceived`-Ereignis für die lokale UI
   auslösen.
3. Den Alarm zur Liste aktiver Alarme hinzufügen.
4. Ist `TTL > 1`, TTL dekrementieren und **an ALLE Peers re-broadcasten**,
   unabhängig vom Routing-Tabellenzustand. SOS-Pakete umgehen normales Routing –
   sie fluten bedingungslos.

### 8.4. Ratenbegrenzung

Jeder Knoten führt ein Schiebefenster mit kürzlichen Broadcast-Zeitstempeln.
Vor dem Initiieren eines neuen SOS:

1. Einträge, die älter als 1 Stunde sind, aus der Warteschlange entfernen.
2. Enthält die Warteschlange 3 oder mehr Einträge (`MaxSosBroadcastsPerHour`),
   wird der Broadcast abgelehnt.
3. Nach erfolgreicher Versendung wird der aktuelle Zeitstempel eingereiht.

Die Ratenbegrenzung gilt nur für das Initiieren von SOS-Broadcasts, nicht für
das Weiterleiten.

### 8.5. SOS-PanikAPI-Bridge

SOS-Broadcasts, die über das Mesh empfangen werden, können an PanikAPI für
traditionelle Notfallreaktion (SMS an Kontakte, E-Mail-Benachrichtigungen)
weitergeleitet werden. Umgekehrt können PanikAPI-Notsitzungen für
Community-Bewusstsein ins Mesh gebroadcastet werden. Schleifenvermeidung wird
durch Kennzeichnung der Quelle (`direct` vs. `mesh_forward`) und eines
`internet_forwarded`-Flags bei Mesh-Broadcasts erreicht.

---

## 9. DTN Store-and-Forward

Das Delay-Tolerant-Networking-(DTN-)Subsystem ermöglicht die Nachrichtenzustellung,
wenn kein Ende-zu-Ende-Pfad zwischen Sender und Empfänger existiert. Bundles werden
auf Zwischenknoten gespeichert und opportunistisch weitergeleitet, sobald sich die
Konnektivität ändert.

### 9.1. Bundle-Format

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

### 9.2. Bundle-Lebenszyklus

1. **Erstellung:** Der Sender erstellt ein Bundle mit einem verschlüsselten Payload
   (verschlüsselt über die Signal-Sitzung mit dem Empfänger). `Status = Pending`,
   `CopyCount = 1`.
2. **Sofortiger Zustellversuch:** Der Sender versucht zunächst direktes Mesh-Routing
   (RREQ/RREP). Existiert eine Route, wird das Bundle sofort zugestellt und `Status`
   wechselt zu `Delivered`.
3. **Server-Relay-Versuch:** Schlägt Mesh-Routing fehl, versucht der Sender die
   Weiterleitung über AetherNetAPI. Kann der Server den Empfänger erreichen (oder die
   Nachricht einreihen), gelingt die Zustellung.
4. **Store-and-Forward:** Scheitern sowohl Mesh- als auch Server-Relay, verbleibt das
   Bundle im lokalen Speicher (Status `Pending`) und wartet auf den nächsten
   Zustellungsscan.

### 9.3. Zustellungsscan

Ein periodischer Scan läuft alle 60 Sekunden (`DtnScanIntervalSeconds`):

1. Alle ausstehenden Bundles aus SQLite laden (maßgebliche Datenquelle).
2. Für jedes ausstehende Bundle:
   a. Mesh-Route zum Empfänger versuchen.
   b. Server-Relay versuchen.
   c. Scheitern beide und `CopyCount < MaxCopies`, Epidemie-Replikation versuchen
      (Abschnitt 9.4).
3. Abgelaufene Bundles entfernen (`ExpiresAt <= jetzt`).

### 9.4. Epidemie-Routing

Wenn direkte Zustellung und Server-Relay beide scheitern, werden Bundles über
Epidemie-Routing an nahegelegene Peers repliziert:

1. Der `EpidemicRoutingService` wählt Replikationsziele aus der aktuellen Peer-Liste.
2. Die Zielauswahl berücksichtigt:
   - **Geohash-Nähe:** Peers, deren Geohash dem letzten bekannten Geohash des
     Empfängers näher ist, werden bevorzugt.
   - **Relay-Historie:** Peers mit höheren Zuverlässigkeitswerten werden bevorzugt.
   - **Kopienbudget:** Die Replikation stoppt, wenn `CopyCount >= MaxCopies`
     (Standard: 3).
3. Jede Replikation sendet ein `DtnBundle`-Paket an den ausgewählten Peer.
4. Beim Empfang ruft der DTN-Dienst des Peers `AcceptCustodyAsync` auf.

### 9.5. Custody-Transfer

Wenn ein Knoten ein DTN-Bundle empfängt, das für einen anderen Knoten bestimmt ist:

1. **Kapazitätsprüfung:** Der Knoten prüft seine aktuelle Bundle-Anzahl gegen
   `DtnMaxBundlesPerNode` (50). Bei voller Kapazität wird der Custody abgelehnt.
2. **Annehmen:** Der Bundle-Status wird auf `InCustody` gesetzt, die Hop-Anzahl
   inkrementiert und das Bundle in SQLite gespeichert.
3. **Custody-Eintrag:** Ein `CustodyRecord` wird erstellt, der den Transfer
   dokumentiert (von, an, Zeitstempel).
4. **Kopienanzahl-Inkrementierung:** Die `CopyCount` des Bundles wird im
   persistenten Speicher inkrementiert.
5. **Bestätigung:** Ein `DtnCustodyAck`-Paket wird mit `Accepted = true` an den
   übertragenden Knoten zurückgesendet.
6. Der annehmende Knoten übernimmt die Verantwortung für Zustellversuche bei
   nachfolgenden Scans.

### 9.6. Zustellungsquittung

Wenn der beabsichtigte Empfänger ein DTN-Bundle erhält:

1. Der Bundle-Status wird auf `Delivered` aktualisiert.
2. Eine `DtnDeliveryReceipt` wird über Mesh-Routing (mit Server-Relay-Fallback) an
   den ursprünglichen Sender zurückgesendet:
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. Nach Erhalt der Quittung entfernt der Sender das Bundle aus seinem Speicher und
   löst das `BundleDelivered`-Ereignis aus.
4. Die Quittung wird ebenfalls zur Analyse an AetherNetAPI synchronisiert.

### 9.7. Bundle-Ablauf

- Die Standard-Bundle-TTL beträgt 72 Stunden (`DtnBundleTtlHours`).
- Abgelaufene Bundles werden während des periodischen Zustellungsscans bereinigt.
- Bundles im Status `Expired` oder `Delivered` werden sowohl aus dem In-Memory-Cache
  als auch aus SQLite entfernt.

### 9.8. Kapazitätsgrenzen

| Parameter               | Default | Description |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | Maximum bundle lifetime |
| `DtnMaxCopies`          | 3       | Maximum copies per bundle across the network |
| `DtnMaxBundlesPerNode`  | 50      | Maximum bundles a single node will carry |
| `DtnScanIntervalSeconds`| 60      | Delivery scan frequency |

---

## 10. Video-Streaming

> **Stand 2026-05-05 – Design und C#-Gerüst, keine produktive Codec-Pipeline.**
> Die Pakettypen `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) sind
> wire-definiert und durchlaufen erfolgreich den sprachübergreifenden Fixture-
> Korpus. Das C#-Modul `AetherNet.Streaming` enthält Interfaces, Modelle und
> Skeleton-Dienste (`StreamingService`, `VideoCallService`,
> `WatchTogetherService`), die Routing-/DI-Nähte und Unicast-Segment-Fan-Out
> verdrahten – jedoch ist kein tatsächliches Video-Encode/Decode daran
> gebunden. Die anderen 7 Sprachen haben nur Wire-Typen. Das
> Vorwärts-Design-Dokument unter `docs/adaptive-secure-streaming-spec.md` ist
> die Zielarchitektur. Der folgende Prosatext ist die Spezifikation dessen,
> was diese Dienste implementieren WERDEN; offene Produktions-Reifepunkte
> sind in `OPEN_ISSUES.md` vermerkt.

Aether unterstützt drei Video-Modi: Peer-to-Peer-Videoanrufe, Gruppen-Video
(unbegrenzte Teilnehmer mit dynamischer Topologie) und Live-Broadcast. Alle
Video-Frames werden mit dem Signal-Protokoll verschlüsselt und mit Ed25519 signiert.

### 10.1. Transport-Fähigkeitsmatrix

Vor dem Initiieren eines Videoanrufs fragt der Originator die Transportschicht ab,
um die beste verfügbare Verbindung zum Peer zu bestimmen. Der Transport bestimmt,
welche Videoqualität möglich ist:

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | No (audio-only) | — | — | 64 Kbps | Sync packets only |
| NearLink | Light | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Full | 1080p | H.264 | 3000 Kbps | All modes |
| Internet | Full | 720p | H.264 | 1500 Kbps | All modes |
| CircleLink | No (audio-only) | — | — | 64 Kbps | Sync packets only |

Steht ausschließlich BLE oder CircleLink als Transport zur Verfügung, stuft der
Video-Anrufdienst automatisch auf einen Sprachanruf herab.

### 10.2. Video-Codecs

| Enum Value | Codec | Use Case |
|------------|-------|----------|
| 0 | H.264 | Standard. Weit verbreitet, gute Kompression. |
| 1 | H.265 | Bessere Kompression. Verwendet auf NearLink (bandbreitenbeschränkt). |
| 2 | VP8 | Lizenzfreie Alternative. |

### 10.3. Video-Auflösungen

| Enum Value | Resolution | Typical Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P-Videoanruf-Ablauf

1. **Fähigkeitsprüfung**: Der Originator ruft `GetVideoCapabilityAsync(peerUhid)`
   auf, um den besten Transport, die maximale Auflösung und den empfohlenen Codec zu
   bestimmen.
2. **Angebot**: Der Originator sendet ein `VideoSignaling`-Paket (Typ 28) mit
   `SignalType = Offer`, einschließlich bevorzugtem Codec, maximaler Auflösung und
   maximaler Bitrate.
3. **Annahme/Ablehnung**: Der Angerufene antwortet mit `SignalType = Answer`
   (Codec-Aushandlung auf den kleinsten gemeinsamen Nenner) oder `SignalType = Reject`.
4. **Aktiver Anruf**: Beide Knoten tauschen `VideoCall`-Pakete (Typ 27) mit
   H.264/H.265/VP8-NAL-Units aus. Jeder Frame enthält eine Sequenznummer für die
   Jitter-Buffer-Ordnung und ein Keyframe-Flag.
5. **Bildschirmfreigabe**: Jede Partei kann die Bildschirmfreigabe umschalten.
   `VideoSignaling` mit `SignalType = ScreenShareStart/Stop` benachrichtigt den Peer.
   Bildschirmfreigabe-Frames verwenden `PacketType.ScreenShare` (Typ 32), aber
   dieselbe Verarbeitungs-Pipeline.
6. **Anruf beenden**: Jede Partei sendet `VideoSignaling` mit `SignalType = Bye`.

Alle Signalisierungs- und Frame-Payloads werden mit dem Signal-Protokoll
(X3DH-Sitzung) verschlüsselt. Der verschlüsselte Payload wird als JSON-kodiertes
`EncryptedPayload` innerhalb des `MeshPacket.Payload`-Felds serialisiert.

### 10.5. Videoanruf-Zustandsmaschine

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

Zustände: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`,
`Failed(5)`, `Rejected(6)`.

### 10.6. Gruppen-Video

Gruppen-Video-Sitzungen unterstützen eine unbegrenzte Teilnehmerzahl. Die Topologie
wird dynamisch je nach Teilnehmeranzahl ausgewählt:

- **FullMesh** (2–3 Teilnehmer): Jeder Teilnehmer sendet einen Stream an jeden
  anderen. Einfach, geringe Latenz.
- **SFU** (ab 4 Teilnehmern, Schwellenwert: `SfuThresholdParticipants = 4`): Ein
  Knoten wird als SFU-Relay gewählt. Jeder Teilnehmer sendet einen Stream an das
  Relay, das ihn an alle anderen verteilt. Der Relay-Knoten verdient Tipps über
  die Anreizschicht.

Topologie-Wechsel erfolgen automatisch: Tritt der 4. Teilnehmer bei, wechselt die
Sitzung von FullMesh zu SFU. Wenn Teilnehmer die Sitzung verlassen und die Anzahl
unter 4 fällt, wechselt sie zurück.

Gruppen-Video-Frames verwenden `PacketType.VideoFrame` (Typ 31). Im SFU-Modus werden
Frames an die UHID des Relay-Knotens gesendet, der sie weiterbroadcastet.

### 10.7. Jitter-Buffer

Der Video-Jitter-Buffer arbeitet unabhängig vom Sprach-Jitter-Buffer (der
20-ms-Opus-Frames verarbeitet):

- **Bereich**: Minimum 60 ms, Maximum 500 ms.
- **Adaptive Tiefe**: Verfolgt Inter-Frame-Jitter über Exponential Moving Average
  (EMA). Buffer-Tiefe = 2× Jitter-Schätzung, begrenzt auf [60, 500] ms.
- **Keyframe-bewusstes Verwerfen**: Bei Buffer-Überlauf werden zuerst
  Nicht-Keyframe-(P/B-)Frames verworfen. I-Frames (Keyframes) werden nie verworfen –
  sie sind für die Decoder-Wiederherstellung erforderlich.
- **Lückenbehandlung**: Wird eine Sequenzlücke erkannt, springt der Buffer zum
  nächsten verfügbaren Keyframe, anstatt unbegrenzt zu warten.

### 10.8. Video-Signalisierungstypen

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Offer | Videoanruf-Initiierung mit Codec-/Auflösungspräferenz |
| 1 | Answer | Anrufannahme mit ausgehandelten Parametern |
| 2 | Reject | Anrufablehnung |
| 3 | Bye | Anrufbeendigung |
| 4 | Upgrade | Anforderung höherer Qualität (z. B. Transport verbessert) |
| 5 | Downgrade | Anforderung niedrigerer Qualität (z. B. Bandbreiteneinbruch) |
| 6 | ScreenShareStart | Peer hat Bildschirmfreigabe begonnen |
| 7 | ScreenShareStop | Peer hat Bildschirmfreigabe beendet |

### 10.9. Verschlüsselungsmodell

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| P2P-Videoanruf | Signal-Protokoll pro Frame | X3DH-Schlüsselvereinbarung |
| Gruppen-Video | Gruppenkanal-Schlüssel (AES-GCM) | Über Signal-Protokoll bei Sitzungserstellung verteilt |
| Bildschirmfreigabe | Wie übergeordneter Anruf-Modus | Vom Videoanruf-Session geerbt |

---

## 11. Watch Together

> **Stand 2026-05-05 – Design und C#-Gerüst, gleicher Reifegrad wie §10.**
> Die Pakettypen `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) sind wire-definiert und
> fixture-getestet. `AetherNet.Streaming.WatchTogetherService` stellt das
> Koordinationsgerüst bereit (Sitzungszustand, Sync-Befehlsweiterleitung via
> `IMeshSender`, RTT-Kompensations-Hilfsmittel); BitTorrent-Ingest, ChipIn-
> SDPKT-Abrechnung und Chunk-Fetch-von-Peers sind in keiner Sprache
> implementiert. Der folgende Prosatext ist das Zielprotokoll; das
> Vorwärts-Design-Dokument unter `docs/adaptive-secure-streaming-spec.md`
> behandelt dasselbe Thema ausführlicher.

Watch Together ermöglicht synchronisierte Medienwiedergabe für eine Gruppe von
Mesh-Peers. Der Host hat die exklusive Kontrolle über die Wiedergabe (Play, Pause,
Seek, Speed). Sync-Befehle enthalten Wanduhr-Zeitstempel zur RTT-Kompensation.

### 11.1. Watch-Modi

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | Nur Sync-Pakete (< 100 Bytes je) | Beliebig (funktioniert über BLE) |
| 1 | StreamFromHost | P2P-Chunk-Transfer (verwendet P2pContentService) | WiFi Direct oder Internet |
| 2 | BitTorrent | Mesh + externer Swarm über Gateway-Knoten | WiFi Direct oder Internet |

### 11.2. SharedFile-Modus

Beide Teilnehmer haben dieselbe Datei (abgeglichen per SHA-256-Inhalts-Hash). Nur
`WatchSync`-Pakete werden ausgetauscht. Dies ist der bandbreiteneffizienteste Modus
und funktioniert über BLE.

1. Der Host erstellt eine Watch-Sitzung mit `contentHash` (SHA-256 der Datei).
2. Teilnehmer treten bei und melden `IsReady = true`, sobald ihr Player geladen ist.
3. Die Sitzung startet, wenn ALLE Teilnehmer bereit gemeldet haben.
4. Der Host sendet Play-/Pause-/Seek-/Speed-Befehle als `WatchSync`-Pakete (Typ 29).
5. Empfänger wenden RTT-Kompensation an:
   `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. StreamFromHost-Modus

Nur der Host hat die Datei. Der Host erzeugt ein `ContentManifest` (unter
Wiederverwendung des P2P-Content-Systems), und Teilnehmer laden Chunks über das
Mesh herunter.

- Die Chunk-Auswahl verwendet die Strategie `SequentialFromPosition` (nicht
  `RarestFirst`): Chunks vor der aktuellen Wiedergabeposition werden priorisiert,
  dann rückwärts für das Seeding aufgefüllt.
- Pufferziel: 30 Sekunden voraus (`WatchTogetherBufferAheadSeconds`).
- Auto-Pause: Fällt der Puffer EINES Teilnehmers unter 10 Sekunden
  (`WatchTogetherMinBufferSeconds`), pausiert die Sitzung alle Teilnehmer
  automatisch mit einem `BufferUnderrun`-Sync-Befehl. Die Wiedergabe setzt fort,
  wenn alle Teilnehmer ausreichend Puffer haben (`BufferReady`).
- Wenn Zuschauer Chunks herunterladen, werden sie zu Seedern für andere Zuschauer
  (BitTorrent-ähnliches Swarming im Mesh).

### 11.4. BitTorrent-Modus

Ein Teilnehmer teilt eine `.torrent`-Datei oder einen Magnet-Link im Gruppen-Chat.
Das `TorrentMetadata`-Paket (Typ 34) verteilt die Torrent-Informationen an alle
Sitzungsteilnehmer.

**Mesh-to-Swarm-Bridge:**
- Gateway-Knoten (Knoten mit Internet) laden Stücke aus dem externen BitTorrent-Swarm
  herunter.
- Gateway-Knoten verschlüsseln heruntergeladene Stücke für die Mesh-Verteilung und
  seeden an Mesh-Peers.
- Mesh-Peers ohne Internet empfangen Stücke von Gateway-Knoten und voneinander.
- Die P2P-Content-Engine übersetzt zwischen BitTorrents Stücke-Modell und Aethers
  Chunk-Modell.

Sobald genug Inhalt gepuffert ist, beginnt die Watch-Together-Wiedergabe unter
Verwendung desselben Sync-Protokolls wie im SharedFile-Modus.

### 11.5. Watch-Sitzungs-Zustandsmaschine

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

Zustände: `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. Sync-Befehlstypen

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Play | Wiedergabe an angegebener Position fortsetzen |
| 1 | Pause | An angegebener Position pausieren |
| 2 | Seek | Zur angegebenen Position springen |
| 3 | Speed | Wiedergabegeschwindigkeit ändern |
| 4 | BufferUnderrun | Auto-Pause – Puffer eines Teilnehmers kritisch niedrig |
| 5 | BufferReady | Fortsetzen – alle Teilnehmer haben ausreichend Puffer |

### 11.7. RTT-Kompensation

Sync-Befehle enthalten ein `WallClockMs`-Feld (Unix-Epoch-Millisekunden). Wenn ein
Empfänger einen Sync-Befehl verarbeitet:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Für Play- und BufferReady-Befehle: `adjustedPosition = commandPosition + networkDelay`
4. Für Pause- und Seek-Befehle: Position wird exakt angewendet (keine Anpassung
   erforderlich, da die Wiedergabe stoppt oder springt).

Dies stellt sicher, dass alle Teilnehmer innerhalb der halben Netzwerk-RTT
synchronisiert sind.

### 11.8. Reaktionen

Teilnehmer können während der Wiedergabe auf den Inhalt reagieren:

- **Emoji-Reaktionen**: `WatchReaction`-Paket (Typ 30) mit `Type = Emoji`, das den
  Emoji-String und die Medienposition zum Zeitpunkt der Reaktion enthält.
- **Sprachkommentare**: `WatchReaction`-Paket mit `Type = VoiceComment`, das
  Opus-kodierte Audiodaten (maximal 10 Sekunden) enthält. Sprachdaten sind im Feld
  `VoiceData` der Reaktion enthalten.

Reaktionen werden an alle Sitzungsteilnehmer gebroadcastet. Sie sind der
Medienposition zeitgestempelt, was eine wiedergabesynchronisierte Anzeige ermöglicht.

### 11.9. ChipIn – Gemeinschaftliche Inhaltsbeschaffung

ChipIn ermöglicht Gruppenmitgliedern, Mittel (in ZAR, abgerechnet über SDPKT-Wallets
durch LedgerAPI) zusammenzulegen, um Inhalte gemeinsam für das gemeinschaftliche
Anschauen zu erwerben.

**Zustandsmaschine:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

Zustände: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`,
`Refunded(5)`.

**Ablauf:**
1. Der Initiator erstellt einen `ChipInPool` mit Zielbetrag und Inhaltsbeschreibung.
2. Teilnehmer leisten Beiträge über SDPKT-Wallet-Transaktionen.
3. Wenn `CollectedAmount >= TargetAmount`, wechselt der Zustand zu `Funded`.
4. Das System erwirbt den Inhalt (z. B. leitet einen BitTorrent-Download ein).
5. Sobald der Inhalt verfügbar ist, wechselt der Zustand zu `Acquired` und
   Watch-Together kann beginnen.

Jeder Beitrag wird mit einer SDPKT-Transaktions-ID für den Prüfpfad aufgezeichnet.

### 11.10. Verschlüsselungsmodell

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| Watch-Sync-Befehle | Kanal-/Gesprächsschlüssel | Bestehende Signal-Protokoll-Sitzung |
| Inhalts-Chunks (StreamFromHost) | Inhaltsschlüssel pro Manifest | Über Signal-Protokoll verteilt |
| BitTorrent-Stücke | Beim Ingest neu verschlüsselt | Gateway lädt Klartext aus Swarm, verschlüsselt für Mesh |
| Watch-Reaktionen | Sitzungsschlüssel | Vom Gesprächsschlüssel abgeleitet |

### 11.11. Feature-Flags

Alle Video- und Watch-Together-Funktionen werden durch Feature-Flags abgesichert
(alle standardmäßig deaktiviert):

| Flag | Parent | Description |
|------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | P2P- und Gruppen-Videoanrufe |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | Mehrparteien-Videositzungen |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | Bildschirmfreigabe bei Videoanrufen |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | Synchronisierte Medienwiedergabe |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | Emoji- und Sprachreaktionen |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | BitTorrent-Dateiakzeptanz für Mesh-Verteilung |

Feature-Flags haben übergeordnete Abhängigkeiten: Ein untergeordnetes Flag kann
nur aktiviert werden, wenn sein übergeordnetes Flag ebenfalls aktiviert ist.
Dies ermöglicht einen schrittweisen Rollout.

---

## 12. Sicherheits- und Datenschutzschicht

> Hinzugefügt in 2.3.0. Referenzimplementierung: `src/AetherNet.Security/Backup/` (Wiederherstellungsphrase), `src/AetherNet.Security/Privacy/` (BLE-Tracking-Schutz, Panik-Löschung) und `src/AetherNet.Security/Sync/` (Multi-Geräte-Synchronisation). Sprachübergreifende Byte-Vektoren: `fixtures/bip39/`, `fixtures/bleprivacy/`, `fixtures/panicwipe/`, `fixtures/sync/`.

Diese Schicht ist additiv und unabhängig von der Paket-Suite in §2. Nur die **Multi-Geräte-Synchronisation** (§12.1–12.2) und das **Adressschema des BLE-Tracking-Schutzes** (§12.3) besitzen Byte- bzw. On-Air-Formate; die **Wiederherstellungsphrasen-Sicherung** (§12.4) und die **Panik-Löschung** (§12.5) sind rein lokal und werden hier der Vollständigkeit halber spezifiziert. Alle sind in allen acht Sprachen Byte-für-Byte identisch implementiert, mit der einzigen in §12.1 genannten Ausnahme der Ed25519-Signatur.

### 12.1. DeviceLink (Gerätekopplung)

Ein `DeviceLink` ist eine Ed25519-signierte Zusicherung, dass der öffentliche Schlüssel eines Geräts zu einer Identität gehört, und wird verwendet, um die eigenen Geräte eines Benutzers für die Multi-Geräte-Synchronisation zu koppeln. Der **signierte Rumpf** ist:

| Off | Field | Type | Size | Notes |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01`; jeden anderen Wert beim Lesen ablehnen |
| 1 | device_id_len | uint16, little-endian | 2 | UTF-8-Byte-Länge von `device_id` |
| 3 | device_id | UTF-8 bytes | N | Kennung des gekoppelten Geräts |
| 3+N | device_public_key | bytes | 32 | der öffentliche Ed25519-Schlüssel des gekoppelten Geräts |
| 35+N | issued_at_ms | int64, little-endian | 8 | Unix-Epoch-Millisekunden |

Der serialisierte `DeviceLink` ist der signierte Rumpf gefolgt von einer **64-Byte-Ed25519-Signatur** über diesen Rumpf, berechnet mit dem privaten *Identitäts*-Schlüssel. Die Verifikation berechnet den Rumpf neu und prüft die Signatur gegen den öffentlichen Identitätsschlüssel.

> **Ausnahme zur Byte-Parität der Signatur.** Der signierte Rumpf und das Verifikationsergebnis sind in allen acht Sprachen identisch, und die 64 Signatur-**Bytes** sind in sieben davon Byte-für-Byte identisch. Apples CryptoKit randomisiert Ed25519-Signaturen (RFC 8032 §8, „hedged signing"), sodass die Swift-Signatur bei jedem Aufruf abweicht, dabei aber gültig und sprachübergreifend verifizierbar bleibt. Interoperabilität MUSS sich auf die *Verifikation* stützen, niemals auf den Vergleich von Signatur-Bytes.

### 12.2. SyncRecord (Last-Write-Wins-Sync-Umschlag)

Ein `SyncRecord` ist eine replizierte Änderung am geräteeigenen Multi-Geräte-Zustand eines Benutzers, abgeglichen nach Last-Write-Wins. Records reisen Ende-zu-Ende-verschlüsselt innerhalb des bestehenden DTN-/Mesh-Pfads (`encrypted_payload` ist opaker Chiffretext) — sie sind **kein** neuer `MeshPacket`-Typ.

| Off | Field | Type | Size | Notes |
|-----|-------|------|------|-------|
| 0 | format_version | uint8 | 1 | `0x01` |
| 1 | record_id | UUID, RFC 4122 big-endian | 16 | dieselbe Big-Endian-Konvention wie §2.1 |
| 17 | op | uint8 | 1 | `0`=Upsert, `1`=Delete, `2`=Read; > 2 ablehnen |
| 18 | logical_clock | int64, little-endian | 8 | pro Gerät monoton steigender Zähler |
| 26 | created_at_ms | int64, little-endian | 8 | Unix-Epoch-Millisekunden |
| 34 | device_id_len | uint16, little-endian | 2 | UTF-8-Byte-Länge |
| 36 | device_id | UTF-8 bytes | N | Ursprungsgerät |
| 36+N | item_id_len | uint16, little-endian | 2 | UTF-8-Byte-Länge |
| 38+N | item_id | UTF-8 bytes | M | zu synchronisierender logischer Schlüssel |
| 38+N+M | payload_len | int32, little-endian | 4 | Chiffretext-Länge; negative Werte ablehnen |
| 42+N+M | encrypted_payload | bytes | payload_len | opaker Ende-zu-Ende-Chiffretext |

**Abgleich (Last-Write-Wins).** Zwischen zwei Records für dieselbe `item_id` wird der Gewinner bestimmt, indem der Reihe nach verglichen wird, bis sich einer unterscheidet: `created_at_ms`, dann `logical_clock`, dann `device_id` (ordinaler Byte-Vergleich), dann `record_id` (Big-Endian-Byte-Vergleich). Die Ordnung ist total und deterministisch, sodass jedes Gerät unabhängig von der Ankunftsreihenfolge auf denselben Gewinner konvergiert.

### 12.3. BLE-Tracking-Schutz

Zwei Ableitungen erlauben es einem Gerät, Advertising zu senden, ohne von einem passiven Scanner verfolgbar zu sein. Beide sind reine Funktionen, fixiert an `fixtures/bleprivacy/`; ihr On-Air-Senden ist Aufgabe des Host-BLE-Stacks.

- **Rotierende Service-UUID.** `window = floor(unix_time_seconds / 900)` (eine 15-Minuten-Epoche). Die annoncierte 128-Bit-Service-UUID sind die ersten 16 Bytes von `HMAC-SHA256(ble_rotation_key, LE_int64(window))`. Ein Scanner, der die UUID protokolliert, kann zwei Fenster ohne den Rotationsschlüssel nicht verknüpfen.
- **Auflösbare private Adresse (RPA).** Gemäß der Bluetooth-Funktion `ah`: `hash = ah(IRK, prand)`, wobei `ah` AES-128 über den 24-Bit-`prand` (auf 128 Bit aufgefüllt) ist und die unteren 24 Bit genommen werden. Die 48-Bit-Adresse ist `hash(24) || prand(24)`, wobei die obersten zwei Bits von `prand` auf `0b01` gesetzt werden, um sie als auflösbar zu kennzeichnen. Ein Peer, der die IRK besitzt, löst die Adresse auf, indem er `ah` neu berechnet und den Hash vergleicht.

### 12.4. Wiederherstellungsphrasen-Sicherung (lokal)

Eine Identität ist ein Ed25519-Schlüsselpaar, dessen 32-Byte-Privatsaat (256 Bit) als **24-Wort-BIP-39**-Mnemonik über die offizielle englische Wortliste codiert wird, mit der standardmäßigen SHA-256-Prüfsumme (ein falsch getipptes Wort besteht die Prüfsumme nicht und wird abgelehnt, statt stillschweigend eine andere Identität zu ergeben). Dies ist Standard-BIP-39 — gegen die offiziellen Trezor-Testvektoren verifiziert und in allen acht Sprachen Byte-für-Byte reproduziert — sodass die Phrase die Identität auf jedem Gerät ohne Server oder Verwahrer wiederherstellt. Es gibt kein Wire-Format; die Phrase berührt niemals das Netzwerk.

### 12.5. Panik-Löschung (lokal)

Unter Zwang löst eine **Zwangs-PIN** — in konstanter Zeit gegen einen gespeicherten `SHA-256(pin)` verglichen — eine sichere Löschung des gesamten Identitätsschlüsselmaterials aus: jeder Puffer wird mit Zufalls-Bytes überschrieben und dann genullt, über ein festes Manifest von Identitätsschlüsselnamen (Identitäts-Schlüsselpaar, Geräte-Salt, DRK sowie den BLE-Rotationsschlüssel / die IRK aus §12.3). Es gibt kein Wire-Format; die Operation ist vollständig lokal auf dem Gerät.

---

## Anhang A: Konstantenreferenz

Alle Protokollkonstanten sind in `ProtocolConstants` definiert und werden hier
zur Referenz wiedergegeben:

### Routing
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE-Discovery
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherNetBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Sicherheit
| Constant                  | Value  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constant                   | Value |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constant                  | Value  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transport
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Heartbeat
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### Präsenz
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### Sprache
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### Streaming
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### Video
| Constant                       | Value |
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

## Anhang B: Glossar

| Term | Definition |
|------|------------|
| **UHID** | Universal Hardware Identifier. Eine eindeutige Zeichenkette zur Identifikation eines Mesh-Knotens, abgeleitet aus Geräteidentität und kryptografischen Schlüsseln. |
| **RREQ** | Route Request. Ein Broadcast-Paket zur Entdeckung eines Pfades zu einem Zielknoten. |
| **RREP** | Route Reply. Ein Unicast-Paket, das entlang der durch einen RREQ errichteten Rückwärtsroute zurückgesendet wird. |
| **IRK** | Identity Resolving Key. Ein 128-Bit-Schlüssel zur Erzeugung und Auflösung von BLE Resolvable Private Addresses. |
| **RPA** | Resolvable Private Address. Eine 6-Byte-BLE-Adresse, die periodisch rotiert, aber von Peers aufgelöst werden kann, die den IRK des Senders besitzen. |
| **X3DH** | Extended Triple Diffie-Hellman. Ein Schlüsselvereinbarungsprotokoll für den asynchronen Sitzungsaufbau. |
| **DTN** | Delay-Tolerant Networking. Ein Store-and-Forward-Paradigma für Umgebungen mit unbeständiger Konnektivität. |
| **Gateway** | Ein Mesh-Knoten mit Internetanbindung, der Mesh-Datenverkehr zu/von IP-basierten Diensten überbrückt. |
| **HKDF** | HMAC-based Key Derivation Function. Wird zur Ableitung mehrerer Schlüssel aus einem einzigen gemeinsamen Geheimnis verwendet. |
| **Pre-key bundle** | Ein veröffentlichter Schlüsselsatz, der es einem Sender ermöglicht, eine verschlüsselte Sitzung aufzubauen, ohne dass der Empfänger online sein muss. |
| **SFU** | Selective Forwarding Unit. Ein Relay-Knoten, der einen Video-Stream von jedem Sender empfängt und ihn an alle anderen Teilnehmer verteilt, wodurch die Upload-Bandbreite pro Knoten reduziert wird. |
| **ChipIn** | Gemeinschaftlicher Finanzierungsmechanismus, bei dem Teilnehmer SDPKT-Mittel zusammenlegen, um Inhalte gemeinsam für das gemeinschaftliche Anschauen zu erwerben. |
| **NAL** | Network Abstraction Layer. Das Kapselungsformat der H.264- und H.265-Codecs für die Paketierung von Video-Frames. |

---

## Anhang C: Referenzen

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
