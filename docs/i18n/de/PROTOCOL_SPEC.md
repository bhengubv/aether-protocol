# Aether Mesh-Netzwerkprotokoll-Spezifikation

**Version:** 2.0
**Status:** Abgeglichen mit HEAD (2026-05-05)
**Datum:** 2026-03-15 (erster Entwurf); 2026-05-05 (§2, §4, §10, §11 abgeglichen, §3/§9 verifiziert)
**Autoren:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **Hinweis für Leser.** Frühere Entwürfe dieses Dokuments stammen aus
> der Zeit vor der 8-sprachigen Wire-Format-Ausrichtung und dem
> familienweiten Port auf X25519 + Signal Double Ratchet. Ab dem
> 2026-05-05 beschreiben §2 (Paketformat), §3 (Routing), §4
> (Schlüsselaustausch), §9 (DTN) das implementierte Protokoll; §10
> (Video-Streaming) und §11 (Watch Together) beschreiben das
> Zielprotokoll — sie sind wire-definiert und fixture-getestet, aber
> der Codec / BitTorrent / ChipIn-Pipeline ist noch nicht mit dem
> Gerüst verbunden. Die C#-Referenz ist überall maßgeblich, wo dieses
> Dokument und die Implementierung voneinander abweichen.
>
> - Kanonische Wire-Bytes: `fixtures/expected/*.bin` (10 benannte Fälle)
> - Referenz-Serialisierer: `src/Aether.Core/Protocol/PacketSerializer.cs`
> - Referenz-Signal-Stack: `src/Aether.Security/Services/SignalProtocolService.cs`
> - Referenz-Routing: `src/Aether.Core/Routing/RoutingService.cs`
> - Referenz-DTN: `src/Aether.Core/Dtn/DtnService.cs`
> - Sprachübergreifender Wire-Interop-Nachweis: `fixtures/README.md`
> - Sprachübergreifender Signal-Interop-Nachweis: `fixtures/signal/README.md`

---

## Inhaltsverzeichnis

1. [Zusammenfassung](#1-abstract)
2. [Paketformat](#2-packet-format)
3. [Routing-Algorithmus](#3-routing-algorithm)
4. [Schlüsselaustausch](#4-key-exchange)
5. [Anforderungen an die Transportschicht](#5-transport-layer-requirements)
6. [Erkennungsprotokoll](#6-discovery-protocol)
7. [Sicherheitsmodell](#7-security-model)
8. [SOS-Broadcast](#8-sos-broadcast)
9. [DTN Store-and-Forward](#9-dtn-store-and-forward)
10. [Video-Streaming](#10-video-streaming)
11. [Watch Together](#11-watch-together)

---

## 1. Zusammenfassung

Aether ist ein dezentralisiertes Mesh-Netzwerkprotokoll, das für Umgebungen mit
intermittierender oder fehlender Internetverbindung ausgelegt ist. Es bietet
Multi-Hop-Paket-Routing über heterogene Kurzstrecken-Transporte (Bluetooth Low Energy,
Wi-Fi Direct, NearLink), Ende-zu-Ende-Verschlüsselung mittels eines von X3DH abgeleiteten
Schlüsselaustauschs mit einem symmetrischen Ratchet, verzögerungstolerante
Store-and-Forward-Zustellung und einen Notfall-SOS-Flood-Mechanismus. Das Protokoll ist
transport-agnostisch: Jede physische Schicht, die Byte-Arrays zwischen Peers senden und
empfangen kann, ist ein gültiger Aether-Transport. Knoten werden durch Universal Hardware
Identifiers (UHIDs) identifiziert und über Ed25519-Identitätsschlüssel authentifiziert.
Aether ist als universelle Netzwerkschicht gedacht — jede Anwendung im Ökosystem
registriert Aether-Dienste, und Knoten ohne Internetverbindung erreichen das größere
Netzwerk über Gateway-Peers, die Mesh-Traffic ins Internet überbrücken.

---

## 2. Paketformat

> Abgeglichen am 2026-05-05 gegen `src/Aether.Core/Protocol/PacketSerializer.cs`
> und die 10 Fixture-Fälle unter `fixtures/expected/`.

### 2.1. MeshPacket-Wire-Layout

Jede Aether-Nachricht wird in einem `MeshPacket` eingekapselt. Die Felder erscheinen
auf dem Wire in **genau** dieser Reihenfolge:

| Off | Feld             | Typ                             | Größe      | Hinweise |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = unsigniert (Legacy), `2` = signiert (aktuell) |
| 1   | Type             | uint8                           | 1          | Pakettypaufzählung (siehe §2.4) |
| 2   | Id               | UUID, RFC 4122 Big-Endian       | 16         | Paketbezeichner zur Deduplizierung. **Big-Endian**-Bytereihenfolge, NICHT .NETs gemischter Guid-Standard. |
| 18  | Priority         | uint8                           | 1          | Prioritätsstufe (0 = normal, 255 = SOS). **Wire-Feld ist 1 Byte; Werte >255 müssen begrenzt werden.** |
| 19  | Ttl              | int32, Little-Endian            | 4          | Time-to-Live, bei jedem Hop dekrementiert. **4-Byte-int32**, NICHT 1-Byte-uint8 — Werte bis ~2³¹-1 sind gültig. |
| 23  | TimestampMs      | int64, Little-Endian            | 8          | Unix-Epoch-Millisekunden (UTC). |
| 31  | SourceUhid Len   | uint16, Little-Endian           | 2          | Länge von `SourceUhid` in UTF-8-Bytes. Max 65535. |
| 33  | SourceUhid       | UTF-8-Bytes                     | N          | UHID des Absenders; leer erlaubt, aber unüblich. |
| 33+N | DestinationUhid Len | uint16, Little-Endian        | 2          | Länge von `DestinationUhid` in UTF-8-Bytes. |
| ... | DestinationUhid  | UTF-8-Bytes                     | M          | UHID des Empfängers; leerer String für Broadcast. |
| ... | PacketNonce Len  | uint16, Little-Endian           | 2          | Länge von `PacketNonce` in Bytes. Standardwert: 8. |
| ... | PacketNonce      | bytes                           | P          | Kryptographisch zufälliger Nonce zur Replay-Prävention. |
| ... | Payload Len      | int32, Little-Endian            | 4          | Länge von `Payload` in Bytes. Negative Werte sind ein Fehler. |
| ... | Payload          | bytes                           | Q          | Anwendungsdaten. Interpretation abhängig von `Type`. |
| ... | Signature Len    | uint16, Little-Endian           | 2          | Länge von `Signature` in Bytes. 0 (unsigniert) oder 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | Ed25519-Signatur über signable data (siehe §2.3). |

**Längen-Präfix-Breiten** variieren je nach Feld — `SourceUhid`, `DestinationUhid`,
`PacketNonce` und `Signature` verwenden **2-Byte-(uint16)**-Längenpräfixe;
`Payload` verwendet ein **4-Byte-(int32)**-Längenpräfix, da Payloads 64 KiB überschreiten
können.

### 2.2. Minimale Paketgröße

Mit jedem variabel langen Feld leer (Null-Längen-UHIDs, Null-Längen-Nonce,
Null-Längen-Payload, Null-Längen-Signatur) beträgt die Wire-Größe:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

Die 50-Byte / 52-Byte-Angaben in früheren Entwürfen dieser Spezifikation waren falsch.

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

Ein durchgearbeitetes Beispiel finden Sie in `fixtures/expected/basic_data.bin` (83 Bytes,
kanonische Eingabe in `fixtures/inputs.json`). Implementierungen werden gegen das
vollständige Fixture-Corpus validiert — jede Abweichung schlägt den
sprachübergreifenden Fixture-Verifier-Test fehl.

### 2.4. Aufbau der signierbaren Daten

Die Signatur (Feld `Signature` auf dem Wire) wird über eine separate kanonische
Byte-Sequenz berechnet — **nicht** über die Wire-Bytes selbst. Dies ermöglicht es,
das Wire-Layout weiterzuentwickeln, ohne Signaturen zu brechen, und erlaubt es
Zwischenstellen, die Integrität zu verifizieren, ohne den Klartext-Payload zu sehen
(nur sein SHA-256-Hash wird signiert).

Die signierbare Byte-Sequenz ist die Verkettung:

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

> Beachten Sie die absichtliche Abweichung vom Wire-Layout in §2.1: Die signierbaren
> Daten verwenden **4-Byte-int32** für `Type`, `Length`, `Ttl` und `Priority`,
> während der Wire 1-Byte / 2-Byte / 4-Byte / 1-Byte verwendet.
> Dies ist beabsichtigt — die signierbare Form ist sprachübergreifend portabel und
> verwendet Felder fester Breite; die Wire-Form ist für die BLE-PDU-Sparsamkeit
> kompakt. Implementierungen müssen `Priority` auf `[0,255]` begrenzen, bevor sie in
> die signierbaren Bytes kodiert werden, da der Empfänger (der den Wire-Byte 0..255
> sieht) sonst einen anderen signierbaren Puffer ableitet und die Verifizierung
> fehlschlägt.

Die Referenzimplementierung befindet sich in `src/Aether.Security/Services/
PacketSigningService.cs::BuildSignableData` und ist Pflichtlektüre für Portierungen.

### 2.5. Pakettypen

| Wert | Name              | Richtung      | Beschreibung |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV-Routenanfrage |
| 2     | RouteReply        | Unicast       | AODV-Routenantwort (MUSS vom Ziel signiert werden) |
| 3     | Data              | Unicast       | Anwendungsdaten |
| 4     | Ack               | Unicast       | Zustellungsbestätigung |
| 5     | SosBroadcast      | Flood         | Notfall-Broadcast (siehe Abschnitt 8) |
| 6     | SosAck            | Unicast       | SOS-Bestätigung |
| 7     | ChannelMessage    | Multicast     | Gruppen-Kanal-Nachricht |
| 8     | ChunkRequest      | Unicast       | P2P-Inhalts-Chunk-Anfrage |
| 9     | ChunkData         | Unicast       | P2P-Inhalts-Chunk-Antwort |
| 10    | Heartbeat         | Broadcast     | Periodisches Lebendigkeitssignal |
| 11    | StreamAnnounce    | Broadcast     | Live-Stream-Ankündigung |
| 12    | StreamSegment     | Unicast/Tree  | Live-Stream-Mediensegment |
| 13    | StreamSubscribe   | Unicast       | Anfrage zum Beitritt zum Stream-Relay-Baum |
| 14    | StreamUnsubscribe | Unicast       | Stream-Relay-Baum verlassen |
| 15    | VoicePtt          | Unicast       | Push-to-Talk-Sprachrahmen |
| 16    | VoiceCall         | Unicast       | Echtzeit-Sprachanruf-Rahmen |
| 17    | VoiceSignaling    | Unicast       | Sprachanruf-Aufbau/-Abbau |
| 18    | DtnBundle         | Unicast       | DTN-Store-and-Forward-Bundle (siehe Abschnitt 9) |
| 19    | DtnCustodyAck     | Unicast       | DTN-Custody-Transfer-Bestätigung |
| 20    | DtnDeliveryReceipt| Unicast       | DTN-Ende-zu-Ende-Zustellungsbestätigung |
| 21    | PresenceBeacon    | Broadcast     | Präsenz- und Verfügbarkeitsankündigung |
| 22    | PresenceQuery     | Unicast       | Präsenzstatusanfrage |
| 23    | ProfileSync       | Unicast       | Profil-Metadaten-Synchronisierung |
| 24    | TipPacket         | Unicast       | Knoten-Tipping (abgerechnet über LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | Anforderung des Pre-Key-Bundles eines Peers |
| 26    | PreKeyResponse    | Unicast       | Zustellung des Pre-Key-Bundles |
| 27    | VideoCall         | Unicast       | Verschlüsselter Videorahmen (H.264/H.265/VP8-NAL-Unit) |
| 28    | VideoSignaling    | Unicast       | Video-Anruf-Aufbau: Angebot, Antwort, Ablehnung, Bye, Codec-Aushandlung |
| 29    | WatchSync         | Unicast       | Synchronisierter Wiedergabebefehl: Abspielen, Pausieren, Spulen, Geschwindigkeit |
| 30    | WatchReaction     | Multicast     | Zeitgestempelte Emoji- oder Sprachreaktion während Watch-Together |
| 31    | VideoFrame        | Unicast/SFU   | Gruppen-Videorahmen (SFU-Relay verteilt an Teilnehmer) |
| 32    | ScreenShare       | Unicast       | Bildschirmfreigabe-Rahmen (gleiche Pipeline wie Video, separat gekennzeichnet) |
| 33    | WatchChunkRequest | Unicast       | Prioritäts-Chunk-Anfrage gewichtet zur Wiedergabeposition |
| 34    | TorrentMetadata   | Multicast     | BitTorrent-.torrent-Datei oder Magnet-Link-Metadatenaustausch |

### 2.6. Knotenkapazitäten

Knoten bewerben ihre Fähigkeiten als Bitfeld:

| Bit | Wert | Fähigkeit   | Beschreibung |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth-Low-Energy-Transport verfügbar |
| 1   | 2     | WifiDirect  | Wi-Fi-Direct-Transport verfügbar |
| 2   | 4     | Gateway     | Internet-Gateway (überbrückt Mesh ins IP-Netzwerk) |
| 3   | 8     | Relay       | Bereit, Pakete für andere weiterzuleiten |
| 4   | 16    | Sos         | SOS-Broadcast-fähig |
| 5   | 32    | Streaming   | Live-Streaming-Relay-fähig |
| 6   | 64    | Voice       | Sprachanruf-Relay-fähig |
| 7   | 128   | DtnCarrier  | DTN-Store-and-Forward-Träger |
| 8   | 256   | NearLink    | NearLink-Transport verfügbar |
| 9   | 512   | Video       | Video-Kodierung/-Dekodierung fähig |

---

## 3. Routing-Algorithmus

Aether verwendet ein reaktives Routing-Protokoll basierend auf Ad-hoc On-demand Distance
Vector (AODV)-Routing, erweitert um kryptografische Routenauthentifizierung und
QoS-gewichtete Routenauswahl.

### 3.1. Routenanfrage (RREQ)

Wenn ein Knoten ein Paket an ein Ziel senden muss, für das keine Route bekannt ist,
initiiert er eine Routenanfrage:

1. Der Urheber erstellt ein `MeshPacket` mit `Type = RouteRequest`, setzt `SourceUhid`
   auf sich selbst, `DestinationUhid` auf das Ziel und `TTL = 7` (Standard).
2. Das Paket wird an alle direkt verbundenen Peers gebroadcastet.
3. Jeder Zwischenknoten, der eine RREQ empfängt:
   a. Prüft, ob er diese RREQ bereits anhand der Paket-`Id` gesehen hat. Falls ja,
      lässt er das Paket stillschweigend fallen (Deduplizierung). Der Deduplizierungs-Cache
      hält bis zu `DeduplicationCacheSize` Einträge (Standard 10.000) und wird vollständig
      geleert, sobald die Kapazitätsgrenze erreicht ist.
   b. Installiert eine **Rückwärtsroute** zum RREQ-Urheber. Die Rückwärtsroute
      zeichnet die UHID des Peers auf, von dem die RREQ empfangen wurde, als nächsten
      Hop. Die Hop-Anzahl wird aus `DefaultTtl - packet.Ttl + 1` abgeleitet.
   c. Wenn er das Ziel ist, generiert er eine RREP (siehe Abschnitt 3.2).
   d. Wenn er eine vorhandene gültige Route zum Ziel hat, KANN er im Namen des Ziels
      eine RREP generieren.
   e. Andernfalls dekrementiert er TTL und rebroadcastet die RREQ.
4. Der Urheber wartet mit einem Timeout von **5.000 ms** (`RouteTimeoutMs`) auf eine RREP.
   Falls keine RREP eintrifft, schlägt die Routenentdeckung fehl.

### 3.2. Routenantwort (RREP)

Wenn das Ziel (oder ein Zwischenknoten mit gültiger Route) eine Routenantwort generiert:

1. Ein `MeshPacket` mit `Type = RouteReply` wird erstellt, mit `SourceUhid` auf den
   Zielknoten und `DestinationUhid` auf den RREQ-Urheber gesetzt.
2. **SICHERHEITSANFORDERUNG:** Die RREP MUSS vom Ed25519-Identitätsschlüssel des
   Zielknotens signiert werden. Die Signatur deckt die Standard-Signable-Daten
   (Abschnitt 2.3) ab. Dies verhindert Routenvergiftung durch bösartige
   Zwischenknoten.
3. Die RREP wird per Unicast entlang der während der RREQ-Ausbreitung installierten
   Rückwärtsroute zurückgesendet.
4. Jeder Zwischenknoten, der die RREP weiterleitet:
   a. Verifiziert die RREP-Signatur gegen den öffentlichen Schlüssel der behaupteten
      Quelle (falls bekannt). Falls die Verifizierung fehlschlägt, wird die RREP
      verworfen und eine Warnung protokolliert.
   b. Installiert eine **Vorwärtsroute** zur RREP-Quelle (dem Zielknoten) mit dem
      Absender der RREP als nächsten Hop.
   c. Dekrementiert TTL und leitet in Richtung des RREQ-Urhebers weiter.
5. Wenn die RREP den Urheber erreicht, wird die ausstehende Routenanfrage (verfolgt
   über `TaskCompletionSource`) mit der installierten Route aufgelöst.

### 3.3. Routenwartung

- **TTL-basierter Ablauf:** Jeder Routeneintrag trägt einen `ExpiresAt`-Zeitstempel,
  der auf `now + 300 Sekunden` (`RouteExpirySeconds`) gesetzt wird. Routen werden
  nicht implizit aktualisiert; sie müssen nach Ablauf über einen neuen RREQ/RREP-Zyklus
  neu eingerichtet werden.
- **Periodisches Bereinigen:** Der Protokolldienst führt einen periodischen Heartbeat
  aus (standardmäßig alle 300 Sekunden). Während jedes Zyklus werden abgelaufene Routen
  sowohl aus dem In-Memory-`ConcurrentDictionary` als auch aus dem SQLite-Backing-Store
  entfernt.
- **RREQ-Dedup-Bereinigung:** Die Menge der gesehenen RREQ-IDs wird geleert, wenn
  sie `DeduplicationCacheSize` (Standard 10.000) Einträge überschreitet.

### 3.4. Routenqualität und QoS

Jeder `RouteEntry` trägt einen `QualityScore` im Bereich [0, 100], der für neu
entdeckte Routen mit 50 initialisiert wird. Der Score berücksichtigt:

- **Hop-Anzahl:** Weniger Hops deuten im Allgemeinen auf eine schnellere Route hin.
- **Latenz:** Gemessene Round-Trip-Zeit, wenn verfügbar.
- **Peer-Zuverlässigkeit:** Der Zuverlässigkeitsscore des nächsten Hop-Peers (siehe Abschnitt 3.5).

Knoten, die am Tipping-Anreizsystem teilnehmen, erhalten einen QoS-Boost auf ihren
Routenqualitätsscore. Dies ist eine weiche Präferenz: Nicht-Tipper erhalten immer
Service, aber konsequente Tipper können geringfügig bessere Routenauswahl erfahren.
Die Boost-Stufen sind:

| Stufe   | Konsistenz-Schwellenwert | QoS-Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. Peer-Zuverlässigkeitsbewertung

Jedem bekannten Peer wird ein Zuverlässigkeitsscore im Bereich [0, 100] zugewiesen,
der mit 50 initialisiert wird (`DefaultReliabilityScore`). Der Score wird basierend auf
beobachtetem Verhalten angepasst:

| Ereignis              | Delta |
|----------------------|-------|
| Erfolgreiches Relay  | +2    |
| Fehlgeschlagenes Relay | -5  |
| SOS-Relay            | +5    |
| Chunk geliefert      | +1    |
| Chunk-Lieferfehler   | -10   |

Zuverlässigkeitsscores werden in SQLite persistiert und beim Start in den Speicher
geladen. Der Score beeinflusst die Routenauswahl: Routen über zuverlässigere Peers
werden bevorzugt.

---

## 4. Schlüsselaustausch

> Abgeglichen am 2026-05-05 gegen die C#-Referenzimplementierung unter
> `src/Aether.Security/Services/SignalProtocolService.cs` und das
> sprachübergreifende Fixture-Corpus unter `fixtures/signal/`. Die
> C#-Referenz liefert vollständiges X3DH + Double Ratchet (Signal §3 + §5)
> über X25519. Go, Python, TypeScript, Rust, Swift und Kotlin wurden auf
> denselben Envelope portiert und sind auf der Ebene der X3DH- und
> KDF_RK-Fixtures byte-äquivalent. C liefert nur die X25519 + KDF_RK +
> Symmetric-Ratchet-Primitive — ausreichend für den Fixture-Verifier, noch
> keine vollständige Session-Maschinerie. Wo dieser Abschnitt vom Code
> abweicht, ist der Code maßgeblich; öffnen Sie ein Issue in `OPEN_ISSUES.md`.

Aether implementiert **X3DH** (Extended Triple Diffie-Hellman, Signal §3) für den
asynchronen Sitzungsaufbau, unmittelbar gefolgt vom **Signal Double Ratchet** (Signal §5)
für laufende Forward-Secrecy und Post-Compromise-Sicherheit. Die gesamte
Sitzungs-Kryptografie läuft über Curve25519: **X25519** (RFC 7748) für ECDH und
**Ed25519** (RFC 8032) für Signaturen.

### 4.1. Identitätsschlüssel

Jeder Knoten generiert beim ersten Start **zwei** langfristige Schlüsselpaare (kein
XEdDSA; die einfachere Dual-Key-Anordnung ist, was jede Implementierung ausliefert):

- **Ed25519-Schlüsselpaar** — 32-Byte-Seed (privat), 32-Byte-öffentlicher Schlüssel.
  Wird für Paketsignaturen (§2.4), `SignedPreKeySignature` (§4.3),
  RREP-Authentifizierung (§3.2) und Tip-Signaturen verwendet.
- **X25519-Schlüsselpaar** — 32-Byte-Rohprivat- und öffentliche Schlüssel. Wird für
  die vier X3DH-DH-Operationen (§4.4) verwendet.

Referenz: `SignalProtocolService.InitializeIdentityKeys`. Private Schlüssel leben nur
auf dem Gerät; öffentliche Schlüssel werden in `PreKeyBundle` veröffentlicht.

Ein 30-tägiges P-256 → Ed25519-Migrationsfenster wird für die *Signaturverifizierung*
nur bei eingehenden Paketen eingehalten — siehe §7.5. Pre-Key-Bundles selbst sind auf
dem Wire ausschließlich X25519.

### 4.2. Kurvenauswahl

X3DH und der Double Ratchet verwenden ausschließlich **X25519**. P-256 wird bei der
Sitzungseinrichtung in keiner aktuellen Implementierung verwendet. Ein früherer
Entwurf dieser Spezifikation beschrieb P-256-ECDH; dieser Text stammt aus der Zeit
vor dem familienweiten Port auf X25519 vom 2026-05-05 und ist nicht mehr korrekt.

### 4.3. Pre-Key-Bundle

Ein Pre-Key-Bundle wird veröffentlicht, damit ein Initiator eine Sitzung aufbauen kann,
ohne dass der Responder online ist (Signal §3.4):

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

Referenz: `Aether.Security.Models.PreKeyBundle`. Die Wire-Shape-Vereinbarung ist
über alle 8 Sprachen hinweg gleich.

**Einmaliger Pre-Key-(OPK)-Pool.** Jeder Responder pflegt einen Pool von `OpkPoolSize`
(Standard 100, entsprechend Signal's veröffentlichter Empfehlung) X25519-OPKs. Die
Bundle-Generierung entnimmt die nächste ungenutzte ID aus einer FIFO-Warteschlange
und füllt den Pool dann wieder auf seine Zielgröße auf. Jedes OPK wird genau einmal
verbraucht: der Responder entfernt und nulliert den privaten Teil bei der ersten
PreKey-Nachricht, die seine ID referenziert. Gleichzeitige Initiatoren, die um dieselbe
OPK-ID wetteifern, werden sehen, dass genau ein `EstablishResponderSession` unter
`_preKeyLock` erfolgreich ist; der Verlierer löst `CryptographicException` aus.

Referenz: `SignalProtocolService.TopUpOpkPoolNoLock` (Zeilen 494–518),
`SignalProtocolService.EstablishResponderSession` (Zeilen 636–718). Pool-Semantik
wird durch `tests/Aether.Core.Tests/PreKeyPoolTests.cs` geprüft.

**Rotation des signierten Pre-Keys (SPK).** SPK wird beim ersten Bundle-Aufruf
träge generiert und bei nachfolgenden Aufrufen wiederverwendet, sodass gleichzeitige
Initiatoren, die Bundles vor der X3DH-Ausführung abrufen, gegenseitig keine Bundles
ungültig machen. Periodische SPK-Rotation (Signal §3.3 empfiehlt wöchentlich) ist
eine explizite Operation, kein Nebeneffekt der Bundle-Generierung.

Pre-Key-IDs werden aus `RandomNumberGenerator.GetInt32(1, int.MaxValue)` mit
explizitem Kollisions-Retry gezogen (bis zu 64 Versuche, bevor ausgelöst wird).

### 4.4. Sitzungsaufbau (X3DH)

Das vollständige X3DH (Signal §3.3) läuft auf der Initiator-Seite. Vier DH-Operationen
werden über X25519 berechnet:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

wobei `IK_A` / `IK_B` die X25519-Identitätsschlüssel sind, `EK_A` ein frischer,
nur für diese Sitzung generierter X25519-Ephemeralschlüssel ist, `SPK_B` der
signierte Pre-Key des Responders ist und `OPK_B` der Einmal-Pre-Key des Responders
ist. Der initiale Root-Key ist:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

Die `info`-Konstante `aether-x3dh-root-v1` ist über jede Implementierung hinweg
identisch und wird durch `fixtures/signal/expected/x3dh_basic.json` (Feld `root_key_hex`)
festgelegt.

Referenz: `SignalProtocolService.ProcessPreKeyBundleAsync` (Zeilen 554–626).
Verifizierungspfad:
`fixtures/signal/inputs.json` Fall `x3dh_basic` →
`fixtures/signal/expected/x3dh_basic.json`.

**Bundle-Verifizierung.** Vor der Ausführung eines DH verifiziert der Initiator
`SignedPreKeySignature` gegen `IdentityKey` mit Ed25519. Eine fehlgeschlagene
Verifizierung löst `CryptographicException` aus und das Bundle wird verworfen.
Öffentliche Schlüsselgrößen werden gegen `X25519Service.PublicKeySize` (32) validiert;
fehlerhafte Bundles werden abgelehnt.

**Sitzungs-Priming.** Am Ende von `ProcessPreKeyBundleAsync` wird eine `SignalSession`
erstellt mit:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal-kanonische X3DH ↔
  Double-Ratchet-Integration: der X3DH-Ephemeralschlüssel des Initiators wird sein
  erstes DH-Ratchet-Schlüsselpaar (`DHs`).
- `RemoteEphemeralPub = SPK_B` — der signierte Pre-Key des Responders wird als
  initialer Peer-Ratchet-Schlüssel (`DHr`) behandelt.
- `SendChainKey = null`, `RecvChainKey = null` — beide Chain-Keys werden bei der
  ersten Sendung / dem ersten DH-Ratchet-Empfang träge abgeleitet.
- `PendingPreKeyMessage = true` — kennzeichnet, dass der nächste ausgehende
  `EncryptAsync`-Aufruf eine PreKey-Nachricht (`MessageType=1`) senden MUSS.

Alle DH-Ausgaben und das verkettete gemeinsame Geheimnis werden im
`finally`-Block über `CryptographicOperations.ZeroMemory` nulliert.

**Verweigerung unsicherer Sendung.** Wenn `EncryptAsync` für einen Peer ohne Sitzung
aufgerufen wird, löst der Aufruf `InvalidOperationException` aus. Es gibt keinen
UHID-abgeleiteten Fallback-Pfad. Hosts sollen die Nachricht in die Warteschlange stellen
(siehe `MessagingService` + `SignalMessageEnvelopeCipher`) und nach Abschluss des
Sitzungsaufbaus erneut versuchen.

### 4.5. Double Ratchet (Signal §5)

Jede Seite pflegt ein rotierendes X25519-Ratchet-Schlüsselpaar (`DHs`) und eine Kopie
des zuletzt gesehenen öffentlichen Ratchet-Schlüssels des Peers (`DHr`). Bei jeder
Nachricht veröffentlicht der Sender seinen aktuellen `DHs`-Public; immer wenn der
Empfänger ein neues `DHr` beobachtet, führt er einen **DH-Ratchet-Schritt** durch, der
die Chain über `KDF_RK(RK, DH(myDHs, newDHr))` neu verknüpft — sowohl den Root-Key
als auch einen frischen Chain-Key neu ableitend.

#### 4.5.1. KDF_RK

`KDF_RK` ist HKDF-SHA256 über einen 64-Byte-Block, aufgeteilt 32+32 in den neuen
Root-Key und den neuen Chain-Key:

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
`fixtures/signal/inputs.json` Fall `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Symmetrischer Ratchet

Gemäß Signal §5.1 werden Nachrichten- und Chain-Keys aus einem Chain-Key mittels
HMAC-SHA256 mit Einzelbyte-Domänentrennung abgeleitet:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Referenz: `SignalProtocolService.RatchetChainKey` (Zeilen 876–881).
Festgelegt durch `fixtures/signal/inputs.json` Fälle `ratchet_step_basic` und
`ratchet_step_three_iterations`.

Der frühere Entwurf dieser Spezifikation beschrieb `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` und einen separaten `chain_key`-Vortrieb über
`HMAC(chain_key, 0x01)`. Das war nicht Signal-konform und wurde nie implementiert; es
wurde durch die kanonische 0x01/0x02-Aufteilung ersetzt.

#### 4.5.3. DH-Ratchet-Schritt beim Empfang

Ausgelöst, wenn der `SenderEphemeralKeyX25519` der eingehenden Nachricht vom
zwischengespeicherten `RemoteEphemeralPub` abweicht (zeitkonstanter Vergleich).

1. Ausgehenden Zähler als `PreviousChainCount` speichern (Signal §5: PN), damit der
   Peer übersprungene Schlüssel über die Grenze hinweg berechnen kann.
2. `SendCounter` und `RecvCounter` auf 0 zurücksetzen; neuen `RemoteEphemeralPub`
   installieren.
3. Neue Empfangskette ableiten: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Altes `myDHs`-Privat nullieren; neues X25519-Schlüsselpaar generieren.
5. Neue Sendekette ableiten: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Referenz: `SignalProtocolService.DhRatchetReceive` (Zeilen 726–772).

#### 4.5.4. Träge Sendeketten-Ableitung

Die erste Sendung des Initiators führt einen **Halbschritt** statt eines vollständigen
DH-Ratchets aus — X3DH hat bereits `DHs` und `DHr` platziert, daher muss nur die
Sendekette abgeleitet werden:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` wird hier *nicht* rotiert. Es wird nur bei einem echten empfangsseitigen
DH-Ratchet-Schritt rotiert.

Referenz: `SignalProtocolService.DhRatchetSendOnly` (Zeilen 780–796).

#### 4.5.5. Übersprungene Nachrichtenschlüssel

Wenn Nachrichten in falscher Reihenfolge ankommen, wird der Nachrichtenschlüssel jedes
übersprungenen Zählers in `SkippedMessageKeys` zwischengespeichert, mit dem Schlüssel
`(Hex(remoteEphPub):counter)`. Die Remote-Pub-Bindung ist wesentlich — außer-der-Reihe
ankommende Nachrichten aus einer früheren Kette (verschiedenes `DHr`) können nach einem
DH-Ratchet-Schritt noch ankommen und benötigen ihr eigenes kettenspezifisches Schlüsselset.

Grenzen:

- Das Überspringen von mehr als `MaxSkippedKeys` (1000) Einträgen in einer einzigen
  Lücke löst `CryptographicException` aus und erzwingt eine Sitzungsneueinrichtung.
- Beim Überqueren einer DH-Ratchet-Grenze überspringt der Empfänger zunächst bis zu
  `PreviousChainCount`-Schlüssel auf der *alten* Kette, dann führt er den
  DH-Ratchet-Schritt durch, bevor er Schlüssel auf der neuen Kette ableitet.

Referenz: `SignalProtocolService.SkipMessageKeys` (Zeilen 804–830) und
die In-Decrypt-Skip-Schleife (Zeilen 366–388).

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

Referenz: `Aether.Security.Models.EncryptedPayload` (Zeilen 55–66 von
`SecurityModels.cs`). Das Feld `InitiatorEphemeralKeyX25519` ist ein Rückwärtskompatibilitäts-
Alias für das Pre-Double-Ratchet-Wire-Envelope und entspricht `SenderEphemeralKeyX25519`
bei PreKey-Nachrichten; neue Konsumenten sollten es ignorieren.

AES-GCM-Parameter: 256-Bit-Schlüssel, 96-Bit-Nonce (`AesNonceSize = 12`),
128-Bit-Tag (`AesTagSize = 16`), Tag verkettet mit Ciphertext.
Nachrichtenschlüssel werden in `finally`-Blöcken unmittelbar nach der AES-GCM-
Verschlüsselung/-Entschlüsselung nulliert.

### 4.7. Status pro Sprache

| Sprache     | X3DH (4 DHs) | Double Ratchet | OPK-Pool       | Fixture-verifiziert |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | vollständig  | vollständig (§5) | Pool, Standard 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | nur Primitive — `aether_x25519_*`, `aether_signal_kdf_rk` | nicht implementiert | — | nur kdf_rk_basic |

Alle 7 sitzungsfähigen Sprachen (C# + Go + TypeScript + Python + Kotlin + Swift + Rust)
liefern den 100-Schlüssel-FIFO-OPK-Pool mit träger Auffüllung und sperrengeschütztem
Verbrauch, der dem C#-Referenzvertrag entspricht. C liefert nur Primitive; die vollständige
Sitzungsmaschinerie wird in `OPEN_ISSUES.md` Punkt 11 verfolgt.

---

## 5. Anforderungen an die Transportschicht

Aether ist transport-agnostisch. Jeder physische Kommunikationskanal, der den
`ITransportService`-Vertrag erfüllt, kann am Mesh teilnehmen.

### 5.1. ITransportService-Schnittstellenvertrag

Jede Transportimplementierung MUSS Folgendes bereitstellen:

**Eigenschaften:**

| Eigenschaft        | Typ    | Beschreibung |
|--------------------|--------|-------------|
| `Name`             | string | Menschenlesbarer Bezeichner (z. B. "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Ob der Transport auf diesem Gerät derzeit verwendbar ist |
| `MaxBandwidthBps`  | int64  | Maximaler Durchsatz in Bytes pro Sekunde |
| `MaxRangeMeters`   | int32  | Maximale Kommunikationsreichweite in Metern |
| `PowerCostRelative`| int32  | Relativer Stromverbrauch (1 = niedrig, 10 = hoch) |
| `MaxConcurrentPeers` | int32 | Maximale gleichzeitige Peer-Verbindungen |

**Methoden:**

| Methode         | Signatur | Beschreibung |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Sendet ein Byte-Array an einen bestimmten Peer. Gibt bei Erfolg true zurück. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Sendet einen Stream an einen Peer (für große Übertragungen, Sprache, Video). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Prüft, ob eine aktive Verbindung zu einem Peer besteht. |

**Ereignisse:**

| Ereignis       | Signatur | Beschreibung |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Wird ausgelöst, wenn Daten von einem Peer ankommen. |

### 5.2. Transportauswahlalgorithmus

Der `TransportManager` wählt den optimalen Transport für jedes Paket basierend auf:

1. **Verfügbarkeit:** Nur Transporte, bei denen `IsAvailable == true` gilt, werden berücksichtigt.
2. **Payload-Größe:** Wenn die Payload-Größe bei oder unter `BleMaxPayloadBytes` (1.024 Bytes)
   liegt, wird BLE aus Effizienzgründen bevorzugt. Größere Payloads bevorzugen Wi-Fi Direct.
3. **Stromkostengewichtung:** Unter den verfügbaren Transporten werden niedrigere
   `PowerCostRelative`-Werte für Routineverkehr bevorzugt. Hochprioritätspakete (SOS,
   Sprache) können diese Präferenz überschreiben.
4. **Peer-Konnektivität:** Wenn ein Transport bereits eine aktive Verbindung zum
   Ziel-Peer hat (`IsConnected` gibt true zurück), wird er bevorzugt, um Verbindungsaufbau-
   Overhead zu vermeiden.
5. **Fallback:** Wenn kein lokaler Transport das Ziel erreichen kann, wird das Paket für
   das Server-Relay über AetherAPI in die Warteschlange gestellt.

### 5.3. Referenz-Transporte

| Transport    | Maximalbandbreite | Reichweite | Stromkosten | Max. Peers | Hinweise |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbit/s      | 100 m    | 1         | 7        | Primäre Erkennung + kleine Pakete |
| Wi-Fi Direct| ~250 Mbit/s    | 200 m    | 5         | 8        | Große Übertragungen, Streaming, Sprache |
| NearLink    | ~900 Mbit/s    | 200 m    | 3         | 16       | Huawei/HiSilicon, hoher Durchsatz |

**BLE-Payload-Grenze:** Pakete, die 1.024 Bytes (`BleMaxPayloadBytes`) überschreiten,
werden automatisch zu Wi-Fi Direct oder NearLink geleitet. BLE wird für
Erkennungsankündigungen, kleine Steuerpakete (RREQ/RREP, Präsenz-Beacons) und
Messaging mit geringer Bandbreite verwendet.

**Wi-Fi Direct**-Verbindungstimeout beträgt 10.000 ms (`WifiDirectTimeoutMs`) mit
maximal 8 gleichzeitigen Peers (`MaxWifiDirectPeers`).

---

## 6. Erkennungsprotokoll

### 6.1. BLE-Werbung

Aether-Knoten entdecken sich hauptsächlich durch BLE-Werbung. Um eine dauerhafte
Verfolgung über statische Identifikatoren zu verhindern, verwendet das Protokoll zwei
Datenschutzmechanismen: rotierende Service-UUIDs und Identity Resolving Keys.

**Werbe-Zyklus:** 2 Sekunden Scanning ein, 8 Sekunden aus (`BleScanOnMs`/`BleScanOffMs`).
Das Werbeintervall beträgt 1.000 ms (`BleAdvertiseIntervalMs`). Ein zufälliger Jitter von
0-2.000 ms (`BleScanJitterMaxMs`) wird zum Scan-Intervall hinzugefügt, um die Erkennung
von Timing-Mustern zu verhindern.

**Peer-Timeout:** Ein Peer, der innerhalb von 30 Sekunden nicht wieder entdeckt wird,
gilt als verloren (`PeerLost`-Ereignis).

### 6.2. Rotierende Service-UUID

Um eine langfristige BLE-Fingerabdrucknahme zu verhindern, rotiert die in Werbungen
verwendete Service-UUID alle 15 Minuten (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

Der `rotation_key` ist ein 32-Byte-Schlüssel, der einmal pro Knoten generiert und in
sicherem Speicher abgelegt wird. Alle Aether-Knoten, die denselben Rotationsschlüssel
teilen, leiten für ein gegebenes Zeitfenster dieselbe UUID ab, was gegenseitige
Entdeckung ohne Offenlegung eines dauerhaften Bezeichners ermöglicht.

Eine statische Fallback-UUID (`A3E7-1001-0001-0000-000000000000`) wird für 90 Tage
während der Umstellung vom nicht-rotierenden Schema beibehalten.

### 6.3. Identity Resolving Key (IRK)

Jeder Knoten generiert einen 128-Bit-Identity Resolving Key (IRK), der in sicherem
Speicher abgelegt wird. Der IRK wird mit vertrauenswürdigen Peers während des
Schlüsselaustauschs geteilt.

**Generierung der Resolvable Private Address (RPA):**

1. Berechne `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 Bytes).
2. Setze die zwei signifikantesten Bits von `prand[0]` auf `01` (RPA-Flag gemäß BLE-Spec).
3. Berechne `hash = AES-128-ECB(IRK, pad(prand))`, wobei `prand` die Bytes 13-15 einer
   16-Byte-null-aufgefüllten Eingabe belegt.
4. Konstruiere RPA: `hash[0..2] || prand[0..2]` (insgesamt 6 Bytes).

**RPA-Auflösung:** Ein Knoten, der den IRK eines Peers besitzt, kann verifizieren, ob
eine beobachtete RPA zu diesem Peer gehört, indem er den Hash aus der `prand`-Komponente
der RPA neu berechnet. Die Auflösungszeit beträgt ungefähr O(N), wobei N die Anzahl der
bekannten IRKs ist, und wurde mit ~0,1 ms für 100 Peers gemessen.

Die RPA rotiert im gleichen 15-Minuten-Zyklus wie die Service-UUID.

### 6.4. Geohash-basierte Nähe

Knoten kodieren optional ihren Standort als Geohash. Zum Datenschutz wird der Geohash
auf 4 Zeichen gekürzt, was eine Auflösung von ungefähr 39 km x 20 km ergibt. Diese
Granularität ist ausreichend für:

- Nähebasierte Kanalentdeckung
- DTN-epidemisches Routing (Replikation in Richtung des letzten bekannten
  Geohash-Bereichs des Empfängers)
- Geografischer Kontext für SOS-Alarme

Der vollständig aufgelöste Geohash wird niemals über das Mesh übertragen. Nur die
gekürzte Form wird geteilt, und nur wenn der Datenschutzgrad des Knotens dies erlaubt
(`PrivacyLevel.Full` oder `PrivacyLevel.Partial`).

---

## 7. Sicherheitsmodell

### 7.1. Bedrohungsmodell

Aether geht von folgenden Angreiferfähigkeiten aus:

- **Passives Abhören:** Der Angreifer kann alle BLE-Werbungen und den gesamten
  Mesh-Traffic in Funkreichweite beobachten.
- **Aktive Injektion:** Der Angreifer kann Pakete einschleusen, modifizieren oder
  wiederspielen.
- **Sybil-Angriff:** Der Angreifer kann mehrere gefälschte Knotenidentitäten erstellen.
- **Selektiver Denial of Service:** Der Angreifer kann als Relay-Knoten selektiv Pakete
  fallen lassen.

### 7.2. Was geschützt wird

| Eigenschaft | Schutzstufe | Mechanismus |
|----------|-----------------|-----------|
| Nachrichteninhalt | Vollständige Vertraulichkeit | AES-256-GCM mit pro-Nachricht-Schlüsseln (Abschnitt 4.5) |
| Absenderidentität | Teilweise | UHID in Paket-Headern sichtbar; BLE-Adresse rotiert (Abschnitt 6) |
| Empfängeridentität | Teilweise | Ziel-UHID in gerouteten Paketen sichtbar; Broadcast-Pakete haben leeres Ziel |
| Routing-Metadaten | Minimal | Zwischenknoten sehen Quell-/Ziel-UHIDs und TTL |
| Nachrichtenreihenfolge | Geschützt | Zähler im symmetrischen Ratchet verhindern Neuanordnung |
| Nachrichtenintegrität | Vollständig | Ed25519-Signatur auf jedem Paket (v2) |

### 7.3. Angriffswiderstand

**Replay-Angriffe:**
Jedes Paket trägt einen 8-Byte kryptografisch zufälligen Nonce und einen
Millisekunden-genauen Zeitstempel. Relay-Knoten pflegen einen Deduplizierungs-Cache
mit `(SenderUhid, NonceValue)`-Paaren mit einem 5-Minuten-TTL (`MaxPacketAgeSeconds = 300`).
Ein Paket mit einem doppelten Nonce vom selben Absender wird fallen gelassen. Pakete
mit Zeitstempeln, die älter als 5 Minuten sind, werden unabhängig vom Nonce abgelehnt.

Der Nonce-Dedup-Cache wird alle 60 Sekunden bereinigt. Abgelaufene Einträge (älter als
5 Minuten) werden entfernt.

**Man-in-the-Middle (MitM):**
- Route-Reply-Pakete MÜSSEN eine gültige Ed25519-Signatur des behaupteten Zielknotens
  tragen. Zwischenknoten können RREPs nicht fälschen, da sie nicht den privaten Schlüssel
  des Ziels besitzen.
- Pre-Key-Bundles enthalten eine `SignedPreKeySignature` (Ed25519) über den `SignedPreKey`,
  was den ephemeren ECDH-Schlüssel an die langfristige Identität bindet.
- Die Sitzungseinrichtung (Abschnitt 4.4) bindet die Sitzung kryptografisch an die
  Identitäten beider Parteien durch den Pre-Key-Verifizierungsschritt.

**Sybil-Angriffe:**
- Der Zuverlässigkeitsscore jedes Knotens beginnt bei 50 und wird basierend auf
  beobachtetem Verhalten angepasst (Abschnitt 3.5). Neu erstellte Sybil-Knoten haben
  keine akkumulierte Reputation.
- Knoten mit niedrigen Zuverlässigkeitsscores (nahe 0) werden bei der Routenauswahl
  deprioritisiert.
- Der DTN-epidemische Routing-Algorithmus verwendet Geohash-Nähe und Relay-Erfolgshistorie
  zur Auswahl von Replikationszielen, was es Sybil-Knoten schwerer macht, Traffic ohne
  echte Relay-Beiträge anzuziehen.

**Flooding-Angriffe:**
- TTL wird bei jedem Hop dekrementiert und Pakete mit TTL = 0 werden fallen gelassen.
  Das Standard-TTL von 7 begrenzt den Explosionsradius jedes Broadcasts.
- RREQ-Deduplizierung nach Paket-ID verhindert Amplifikation durch Broadcast-Stürme.
  Der Dedup-Cache wird geleert, wenn er `DeduplicationCacheSize` (Standard 10.000)
  Einträge überschreitet.
- SOS-Broadcasts sind auf 3 pro Stunde pro Knoten rate-limitiert (Abschnitt 8).

### 7.4. Schlüssel-Nullierung

Alle intermediären kryptografischen Materialien werden sofort nach Verwendung nulliert:

- `sharedSecret` aus der ECDH-Schlüsselvereinbarung: nulliert nach HKDF-Ableitung.
- `messageKey` aus dem Chain-Ratchet: nulliert nach AES-GCM-Verschlüsselung/-Entschlüsselung.
- `skippedKey` aus der Außer-der-Reihe-Entschlüsselung: nulliert nach Verwendung und
  aus der Map entfernt.
- Abgeleitete `RootKey`, `SendChainKey`, `RecvChainKey`: aus dem Einrichtungskontext
  nulliert (die Sitzung behält ihre eigenen Kopien).

Die Nullierung verwendet `CryptographicOperations.ZeroMemory`, was garantiert nicht vom
Compiler wegoptimiert wird.

### 7.5. P-256 auf Ed25519-Migration

Das Protokoll unterstützt ein 30-tägiges Übergangsfenster von ECDSA-P-256-Identitätsschlüsseln
(Protokollversion 1) auf Ed25519 (Protokollversion 2):

1. Protokollversion-1-Pakete (unsigniert) werden während des Übergangszeitraums akzeptiert.
2. Die Signaturverifizierung versucht zunächst Ed25519. Wenn der öffentliche Schlüssel
   länger als 32 Bytes ist (was auf einen DER-kodierten P-256-Schlüssel hinweist), fällt
   sie auf P-256-ECDSA-Verifizierung zurück.
3. Nach dem 30-tägigen Fenster werden Protokollversion-1-Pakete abgelehnt.
4. Knoten, die nicht migriert sind, müssen sich mit einem neuen Ed25519-Identitätsschlüssel
   neu initialisieren.

### 7.6. Jurisdiktionsbewusstsein

Das Protokoll definiert Jurisdiktionsstufen zur Handhabung unterschiedlicher gesetzlicher
Anforderungen an Verschlüsselung und Mesh-Netzwerke:

| Stufe | Verhalten | Beispiel-Jurisdiktionen |
|------|----------|-----------------------|
| 1    | Freier Betrieb | Südafrika, Kenia, Ghana |
| 2    | Modifizierter Betrieb | Nigeria, Indien, EU, USA, UK |
| 3    | Nur Mesh (hohes Risiko) | China, Russland, Iran, VAE, Myanmar |
| 4    | Unbekannt (Standard nur Mesh) | Alle anderen |

Die Stufenauswahl beeinflusst die Funktionsverfügbarkeit (z. B. können Tipping/Finanz-
Funktionen in Stufe 3 deaktiviert sein), schwächt jedoch die Verschlüsselung nicht.
Ende-zu-Ende-Verschlüsselung wird unabhängig von der Jurisdiktion stets angewendet.

---

## 8. SOS-Broadcast

Der SOS-Mechanismus ist ein dualer Notfall-Flood, der für Situationen konzipiert wurde,
in denen ein Benutzer in Gefahr ist und nahe gelegene Mesh-Peers und/oder das Internet
gleichzeitig erreichen muss.

### 8.1. Broadcast-Parameter

| Parameter | Wert | Beschreibung |
|-----------|-------|-------------|
| TTL       | 15    | Doppelt des normalen Standards (7), gewährleistet breitere Ausbreitung |
| Priority  | 999   | Maximale Priorität; verdrängt allen anderen Traffic in Relay-Warteschlangen |
| Rate-Limit| 3/Stunde | Pro-Knoten-Limit zur Missbrauchsprävention |
| Ziel      | leer  | Broadcast an alle Peers (kein bestimmtes Ziel) |

### 8.2. Flood-Algorithmus

1. Der Urheber erstellt ein SOS-Paket mit `Type = SosBroadcast`, `TTL = 15`,
   `Priority = 999` und einem leeren `DestinationUhid`.
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
3. **Dualer Versand:** Der SOS wird gleichzeitig gesendet über:
   - **Mesh-Flood:** Broadcast an alle verbundenen Peers über alle verfügbaren Transporte.
   - **API-Aufruf:** Gesendet an AetherAPI für serverseitige Verteilung und Überbrückung
     zur PanikAPI (SMS/E-Mail-Versand).
4. Beide Pfade sind relativ zueinander Fire-and-Forget. Wenn der API-Aufruf fehlschlägt,
   läuft der Mesh-Flood unabhängig weiter.

### 8.3. Relay-Verhalten

Wenn ein Knoten ein SOS-Paket empfängt:

1. Deduplizierung anhand der Paket-`Id` prüfen. Falls bereits gesehen, stillschweigend
   fallen lassen.
2. Den Payload deserialisieren und das `SosReceived`-Ereignis für die lokale UI auslösen.
3. Den Alarm zur Liste der aktiven Alarme hinzufügen.
4. Wenn `TTL > 1`, TTL dekrementieren und **an ALLE Peers ohne Rücksicht auf den
   Routingtabellenstatus rebroadcasten**. SOS-Pakete umgehen normales Routing — sie
   fluten bedingungslos.

### 8.4. Rate-Limiting

Jeder Knoten pflegt ein gleitendes Fenster mit aktuellen Broadcast-Zeitstempeln. Vor
dem Initiieren eines neuen SOS:

1. Einträge, die älter als 1 Stunde sind, aus der Warteschlange entfernen.
2. Wenn die Warteschlange 3 oder mehr Einträge enthält (`MaxSosBroadcastsPerHour`),
   wird der Broadcast abgelehnt.
3. Bei erfolgreichem Versand wird der aktuelle Zeitstempel eingereiht.

Rate-Limiting gilt nur für ursprüngliche SOS-Broadcasts, nicht für das Weiterleiten.

### 8.5. SOS-PanikAPI-Brücke

SOS-Broadcasts, die über das Mesh empfangen werden, können für eine traditionelle
Notfallreaktion an PanikAPI weitergeleitet werden (SMS an Kontakte, E-Mail-Alarme).
Umgekehrt können PanikAPI-Notfallsitzungen für das Gemeinschaftsbewusstsein ins Mesh
gebroadcastet werden. Loop-Prävention wird erreicht durch Kennzeichnung der Quelle
(`direct` vs. `mesh_forward`) und einem `internet_forwarded`-Flag auf Mesh-Broadcasts.

---

## 9. DTN Store-and-Forward

Das Delay-Tolerant Networking (DTN)-Subsystem ermöglicht die Nachrichtenzustellung,
wenn kein Ende-zu-Ende-Pfad zwischen Absender und Empfänger existiert. Bundles werden
auf Zwischenknoten gespeichert und opportunistisch weitergeleitet, wenn sich die
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

1. **Erstellung:** Der Absender erstellt ein Bundle mit einem verschlüsselten Payload
   (verschlüsselt über die Signal-Sitzung mit dem Empfänger). `Status = Pending`,
   `CopyCount = 1`.
2. **Sofortiger Zustellungsversuch:** Der Absender versucht zunächst direktes Mesh-Routing
   (RREQ/RREP). Wenn eine Route existiert, wird das Bundle sofort zugestellt und `Status`
   wechselt zu `Delivered`.
3. **Server-Relay-Versuch:** Wenn Mesh-Routing fehlschlägt, versucht der Absender, über
   AetherAPI weiterzuleiten. Wenn der Server den Empfänger erreichen kann (oder die
   Nachricht in die Warteschlange stellt), ist die Zustellung erfolgreich.
4. **Store-and-Forward:** Wenn sowohl Mesh als auch Server-Relay fehlschlagen, verbleibt
   das Bundle im lokalen Speicher (`Pending`-Status) und wartet auf den nächsten
   Zustellungsscan.

### 9.3. Zustellungsscan

Ein periodischer Scan läuft alle 60 Sekunden (`DtnScanIntervalSeconds`):

1. Alle ausstehenden Bundles aus SQLite laden (Wahrheitsquelle).
2. Für jedes ausstehende Bundle:
   a. Mesh-Route zum Empfänger versuchen.
   b. Server-Relay versuchen.
   c. Wenn beides fehlschlägt und `CopyCount < MaxCopies`, epidemische Replikation
      versuchen (Abschnitt 9.4).
3. Abgelaufene Bundles entfernen (`ExpiresAt <= now`).

### 9.4. Epidemisches Routing

Wenn direkte Zustellung und Server-Relay beide fehlschlagen, werden Bundles mithilfe
des epidemischen Routings an nahe gelegene Peers repliziert:

1. Der `EpidemicRoutingService` wählt Replikationsziele aus der aktuellen Peer-Liste aus.
2. Die Zielauswahl berücksichtigt:
   - **Geohash-Nähe:** Peers, deren Geohash näher am letzten bekannten Geohash des
     Empfängers liegt, werden bevorzugt.
   - **Relay-Verlauf:** Peers mit höheren Zuverlässigkeitsscores werden bevorzugt.
   - **Kopierbudget:** Die Replikation stoppt, wenn `CopyCount >= MaxCopies` (Standard: 3).
3. Jede Replikation sendet ein `DtnBundle`-Paket an den ausgewählten Peer.
4. Bei Empfang ruft der DTN-Dienst des Peers `AcceptCustodyAsync` auf.

### 9.5. Custody-Transfer

Wenn ein Knoten ein DTN-Bundle empfängt, das für einen anderen Knoten bestimmt ist:

1. **Kapazitätsprüfung:** Der Knoten prüft seine aktuelle Bundle-Anzahl gegen
   `DtnMaxBundlesPerNode` (50). Bei Kapazitätsgrenzen wird die Custody abgelehnt.
2. **Akzeptieren:** Der Bundle-Status wird auf `InCustody` gesetzt, die Hop-Anzahl
   wird inkrementiert und das Bundle wird in SQLite persistiert.
3. **Custody-Aufzeichnung:** Ein `CustodyRecord` wird erstellt, der den Transfer
   dokumentiert (von, nach, Zeitstempel).
4. **Kopieranzahl-Inkrementierung:** Die `CopyCount` des Bundles wird im persistenten
   Speicher inkrementiert.
5. **Bestätigung:** Ein `DtnCustodyAck`-Paket wird mit `Accepted = true` an den
   übertragenden Knoten zurückgesendet.
6. Der akzeptierende Knoten übernimmt die Verantwortung für Zustellungsversuche bei
   nachfolgenden Scans.

### 9.6. Zustellungsbeleg

Wenn der beabsichtigte Empfänger ein DTN-Bundle empfängt:

1. Der Bundle-Status wird auf `Delivered` aktualisiert.
2. Ein `DtnDeliveryReceipt` wird über Mesh-Routing (mit Server-Relay-Fallback) an den
   ursprünglichen Absender zurückgesendet:
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. Bei Empfang des Belegs entfernt der Absender das Bundle aus seinem Speicher und
   löst das `BundleDelivered`-Ereignis aus.
4. Der Beleg wird auch zur Analyse an AetherAPI synchronisiert.

### 9.7. Bundle-Ablauf

- Standard-Bundle-TTL beträgt 72 Stunden (`DtnBundleTtlHours`).
- Abgelaufene Bundles werden während des periodischen Zustellungsscans bereinigt.
- Bundles mit `Expired`- oder `Delivered`-Status werden sowohl aus dem In-Memory-Cache
  als auch aus SQLite entfernt.

### 9.8. Kapazitätsgrenzen

| Parameter               | Standard | Beschreibung |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | Maximale Bundle-Lebensdauer |
| `DtnMaxCopies`          | 3       | Maximale Kopien pro Bundle im Netzwerk |
| `DtnMaxBundlesPerNode`  | 50      | Maximale Bundles, die ein einzelner Knoten trägt |
| `DtnScanIntervalSeconds`| 60      | Häufigkeit des Zustellungsscans |

---

## 10. Video-Streaming

> **Status zum 2026-05-05 — Design + C#-Gerüst, keine ausgelieferte
> Codec-Pipeline.** Die Pakettypen `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) sind
> wire-definiert und passieren den sprachübergreifenden Fixture-Corpus
> hin und zurück. Das C#-Modul `Aether.Streaming` liefert Schnittstellen,
> Modelle und Skeleton-Dienste (`StreamingService`, `VideoCallService`,
> `WatchTogetherService`), die Routing/DI-Nähte und Unicast-Segment-Fan-out
> verdrahten — aber kein tatsächliches Video-Encode/Decode ist daran
> gebunden. Die anderen 7 Sprachen haben nur Wire-Typen. Das
> Forward-Design-Dokument unter `docs/adaptive-secure-streaming-spec.md`
> ist die Zielarchitektur. Behandeln Sie den folgenden Prosatext als
> Spezifikation dessen, was diese Dienste implementieren WERDEN; konsultieren
> Sie `OPEN_ISSUES.md` für Produktionsreifelücken.

Aether unterstützt drei Videomodi: Peer-to-Peer-Videoanrufe, Gruppen-Video (unbegrenzte
Teilnehmer mit dynamischer Topologie) und Live-Broadcast. Alle Videorahmen werden mit
Signal Protocol verschlüsselt und mit Ed25519 signiert.

### 10.1. Transport-Fähigkeitsmatrix

Vor dem Initiieren eines Videoanrufs fragt der Urheber die Transportschicht ab, um die
beste verfügbare Verbindung zum Peer zu ermitteln. Der Transport bestimmt, welche
Videoqualität möglich ist:

| Transport | Video-Unterstützung | Max. Auflösung | Empfohlener Codec | Max. Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | Nein (nur Audio) | — | — | 64 Kbps | Nur Sync-Pakete |
| NearLink | Leicht | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Vollständig | 1080p | H.264 | 3000 Kbps | Alle Modi |
| Internet | Vollständig | 720p | H.264 | 1500 Kbps | Alle Modi |
| CircleLink | Nein (nur Audio) | — | — | 64 Kbps | Nur Sync-Pakete |

Wenn der einzige verfügbare Transport BLE oder CircleLink ist, stuft der Videoanrufdienst
automatisch auf einen Sprachanruf zurück.

### 10.2. Video-Codecs

| Enum-Wert | Codec | Anwendungsfall |
|------------|-------|----------|
| 0 | H.264 | Standard. Weit verbreitet, gute Kompression. |
| 1 | H.265 | Bessere Kompression. Wird auf NearLink (bandbreitenbeschränkt) verwendet. |
| 2 | VP8 | Lizenzgebührenfreie Alternative. |

### 10.3. Video-Auflösungen

| Enum-Wert | Auflösung | Typische Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P-Videoanruf-Ablauf

1. **Fähigkeitsprüfung**: Der Urheber fragt `GetVideoCapabilityAsync(peerUhid)` ab,
   um den besten Transport, die maximale Auflösung und den empfohlenen Codec zu ermitteln.
2. **Angebot**: Der Urheber sendet ein `VideoSignaling`-Paket (Typ 28) mit
   `SignalType = Offer`, einschließlich bevorzugtem Codec, maximaler Auflösung und
   maximaler Bitrate.
3. **Antwort/Ablehnung**: Der Angerufene antwortet mit `SignalType = Answer` (Codec
   auf den kleinsten gemeinsamen Nenner aushandeln) oder `SignalType = Reject`.
4. **Aktiver Anruf**: Beide Knoten tauschen `VideoCall`-Pakete (Typ 27) aus, die
   H.264/H.265/VP8-NAL-Units enthalten. Jeder Rahmen enthält eine Sequenznummer für
   die Jitter-Buffer-Anordnung und ein Keyframe-Flag.
5. **Bildschirmfreigabe**: Jede Partei kann die Bildschirmfreigabe umschalten.
   `VideoSignaling` mit `SignalType = ScreenShareStart/Stop` benachrichtigt den Peer.
   Bildschirmfreigabe-Rahmen verwenden `PacketType.ScreenShare` (Typ 32), aber die
   gleiche Verarbeitungspipeline.
6. **Anruf beenden**: Jede Partei sendet `VideoSignaling` mit `SignalType = Bye`.

Alle Signalisierungs- und Rahmen-Payloads werden mit Signal Protocol (X3DH-Sitzung)
verschlüsselt. Der verschlüsselte Payload wird als JSON-kodiertes `EncryptedPayload`
innerhalb des `MeshPacket.Payload`-Felds serialisiert.

### 10.5. Videoanruf-Zustandsmaschine

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

Zustände: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. Gruppen-Video

Gruppen-Videositzungen unterstützen unbegrenzte Teilnehmer. Die Topologie wird
dynamisch basierend auf der Teilnehmeranzahl ausgewählt:

- **FullMesh** (2-3 Teilnehmer): Jeder Teilnehmer sendet einen Stream an jeden anderen
  Teilnehmer. Einfach, geringe Latenz.
- **SFU** (4+ Teilnehmer, Schwellenwert: `SfuThresholdParticipants = 4`): Ein Knoten
  wird als SFU-Relay gewählt. Jeder Teilnehmer sendet einen Stream an das Relay, das
  ihn an alle anderen verteilt. Der Relay-Knoten verdient Tipps über die Anreizschicht.

Topologiewechsel erfolgen automatisch: Wenn der 4. Teilnehmer beitritt, wechselt die
Sitzung von FullMesh zu SFU. Wenn Teilnehmer die Sitzung verlassen und die Anzahl
unter 4 fällt, kehrt sie zurück.

Gruppen-Videorahmen verwenden `PacketType.VideoFrame` (Typ 31). Im SFU-Modus werden
Rahmen an die UHID des Relay-Knotens gesendet, der sie rebroadcastet.

### 10.7. Jitter-Buffer

Der Video-Jitter-Buffer arbeitet unabhängig vom Sprach-Jitter-Buffer (der 20-ms-Opus-
Rahmen verarbeitet):

- **Bereich**: 60 ms Minimum, 500 ms Maximum.
- **Adaptive Tiefe**: Verfolgt die Inter-Rahmen-Jitter über Exponential Moving Average
  (EMA). Puffertiefe = 2× Jitter-Schätzung, begrenzt auf [60, 500] ms.
- **Keyframe-bewusstes Fallen lassen**: Bei Pufferüberlauf werden Nicht-Keyframe-
  (P/B)-Rahmen zuerst fallen gelassen. I-Rahmen (Keyframes) werden nie fallen gelassen
  — sie sind für die Decoder-Wiederherstellung erforderlich.
- **Lückenbehandlung**: Wenn eine Sequenzlücke erkannt wird, überspringt der Buffer
  zum nächsten verfügbaren Keyframe, anstatt unbegrenzt zu warten.

### 10.8. Video-Signalisierungstypen

| Enum-Wert | Typ | Beschreibung |
|------------|------|-------------|
| 0 | Offer | Videoanruf-Initiierung mit Codec/Auflösungs-Präferenz |
| 1 | Answer | Anrufannahme mit ausgehandelten Parametern |
| 2 | Reject | Anrufablehnung |
| 3 | Bye | Anrufbeendigung |
| 4 | Upgrade | Anfrage nach höherer Qualität (z. B. Transport verbessert) |
| 5 | Downgrade | Anfrage nach niedrigerer Qualität (z. B. Bandbreite gesunken) |
| 6 | ScreenShareStart | Peer hat begonnen, seinen Bildschirm zu teilen |
| 7 | ScreenShareStop | Peer hat aufgehört, seinen Bildschirm zu teilen |

### 10.9. Verschlüsselungsmodell

| Modus | Verschlüsselung | Schlüsselverteilung |
|------|-----------|-----------------|
| P2P-Videoanruf | Signal Protocol pro Rahmen | X3DH-Schlüsselvereinbarung |
| Gruppen-Video | Gruppen-Kanalschlüssel (AES-GCM) | Über Signal Protocol bei Sitzungserstellung verteilt |
| Bildschirmfreigabe | Wie übergeordneter Anrufmodus | Vom Videoanruf-Sitzungsschlüssel geerbt |

---

## 11. Watch Together

> **Status zum 2026-05-05 — Design + C#-Gerüst, gleiche Reife wie
> § 10.** Pakettypen `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) sind wire-definiert und
> fixture-getestet. `Aether.Streaming.WatchTogetherService` stellt das
> Koordinationsskelett bereit (Sitzungszustand, Sync-Befehl-Weiterleitung
> über `IMeshSender`, RTT-Kompensations-Helfer); BitTorrent-Ingest, ChipIn-
> SDPKT-Abrechnung und Chunk-Abruf von Peers sind in keiner Sprache
> implementiert. Behandeln Sie den folgenden Prosatext als Zielprotokoll;
> das Forward-Design-Dokument unter `docs/adaptive-secure-streaming-spec.md`
> deckt dasselbe Thema detaillierter ab.

Watch Together ermöglicht synchronisierte Medienwiedergabe über eine Gruppe von
Mesh-Peers. Der Host hat exklusive Kontrolle über die Wiedergabe (Abspielen, Pausieren,
Spulen, Geschwindigkeit). Sync-Befehle enthalten Wanduhr-Zeitstempel für die
RTT-Kompensation.

### 11.1. Watch-Modi

| Enum-Wert | Modus | Datenfluss | Transport-Anforderung |
|------------|------|-----------|----------------------|
| 0 | SharedFile | Nur Sync-Pakete (< 100 Bytes jeweils) | Beliebig (funktioniert über BLE) |
| 1 | StreamFromHost | P2P-Chunk-Transfer (verwendet P2pContentService wieder) | WiFi Direct oder Internet |
| 2 | BitTorrent | Mesh + externer Schwarm über Gateway-Knoten | WiFi Direct oder Internet |

### 11.2. SharedFile-Modus

Beide Teilnehmer haben dieselbe Datei (abgeglichen per SHA-256-Inhalts-Hash). Nur
`WatchSync`-Pakete werden ausgetauscht. Dies ist der bandbreiteneffizienteste Modus
und funktioniert über BLE.

1. Host erstellt eine Watch-Sitzung mit `contentHash` (SHA-256 der Datei).
2. Teilnehmer treten bei und melden `IsReady = true`, wenn ihr Player geladen ist.
3. Sitzung startet, wenn ALLE Teilnehmer bereit gemeldet haben.
4. Host sendet Abspielen/Pausieren/Spulen/Geschwindigkeits-Befehle als `WatchSync`-Pakete
   (Typ 29).
5. Empfänger wenden RTT-Kompensation an:
   `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. StreamFromHost-Modus

Nur der Host hat die Datei. Der Host generiert ein `ContentManifest` (unter Wiederverwendung
des P2P-Inhaltssystems) und Teilnehmer laden Chunks über das Mesh herunter.

- Chunk-Auswahl verwendet `SequentialFromPosition`-Strategie (nicht `RarestFirst`):
  priorisiert Chunks vor der aktuellen Wiedergabeposition und füllt dann zum Seeding auf.
- Pufferziel: 30 Sekunden voraus (`WatchTogetherBufferAheadSeconds`).
- Auto-Pause: Wenn der Puffer EINES Teilnehmers unter 10 Sekunden fällt
  (`WatchTogetherMinBufferSeconds`), wird die Sitzung automatisch für alle Teilnehmer
  mit einem `BufferUnderrun`-Sync-Befehl pausiert. Die Wiedergabe wird fortgesetzt,
  wenn alle Teilnehmer einen ausreichenden Puffer haben (`BufferReady`).
- Wenn Zuschauer Chunks herunterladen, werden sie zu Seedern für andere Zuschauer
  (BitTorrent-ähnliches Schwärmen im Mesh).

### 11.4. BitTorrent-Modus

Ein Teilnehmer teilt eine `.torrent`-Datei oder einen Magnet-Link im Gruppen-Chat.
Das `TorrentMetadata`-Paket (Typ 34) verteilt die Torrent-Informationen an alle
Sitzungsteilnehmer.

**Mesh-to-Swarm-Brücke:**
- Gateway-Knoten (Knoten mit Internet) laden Stücke aus dem externen BitTorrent-Schwarm
  herunter.
- Gateway-Knoten verschlüsseln heruntergeladene Stücke für die Mesh-Verteilung neu
  und seeden zu Mesh-Peers.
- Mesh-Peers ohne Internet empfangen Stücke von Gateway-Knoten und voneinander.
- Die P2P-Inhalts-Engine übersetzt zwischen BitTorrents Stück-Modell und Aethers
  Chunk-Modell.

Sobald genug Inhalt gepuffert ist, beginnt die Watch-Together-Wiedergabe unter Verwendung
desselben Sync-Protokolls wie im SharedFile-Modus.

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

| Enum-Wert | Typ | Beschreibung |
|------------|------|-------------|
| 0 | Play | Wiedergabe an der angegebenen Position fortsetzen |
| 1 | Pause | An der angegebenen Position pausieren |
| 2 | Seek | Zur angegebenen Position springen |
| 3 | Speed | Wiedergabegeschwindigkeit ändern |
| 4 | BufferUnderrun | Auto-Pause — Puffer eines Teilnehmers ist kritisch niedrig |
| 5 | BufferReady | Fortsetzen — alle Teilnehmer haben ausreichend Puffer |

### 11.7. RTT-Kompensation

Sync-Befehle enthalten ein `WallClockMs`-Feld (Unix-Epoch-Millisekunden). Wenn ein
Empfänger einen Sync-Befehl verarbeitet:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Für Play- und BufferReady-Befehle: `adjustedPosition = commandPosition + networkDelay`
4. Für Pause- und Seek-Befehle: Position wird genau angewendet (keine Anpassung
   erforderlich, da die Wiedergabe stoppt/springt).

Dies stellt sicher, dass alle Teilnehmer innerhalb der halben Netzwerk-RTT synchronisiert
sind.

### 11.8. Reaktionen

Teilnehmer können während der Wiedergabe auf den Inhalt reagieren:

- **Emoji-Reaktionen**: `WatchReaction`-Paket (Typ 30) mit `Type = Emoji`, das die
  Emoji-Zeichenfolge und die Medienposition zum Zeitpunkt der Reaktion trägt.
- **Sprachkommentare**: `WatchReaction`-Paket mit `Type = VoiceComment`, das
  Opus-kodierte Audiodaten trägt (maximal 10 Sekunden). Sprachdaten sind im
  `VoiceData`-Feld der Reaktion enthalten.

Reaktionen werden an alle Sitzungsteilnehmer gebroadcastet. Sie sind auf die
Medienposition zeitgestempelt, was eine wiedergabesynchronisierte Anzeige ermöglicht.

### 11.9. ChipIn — Gruppen-Inhaltserwerb

ChipIn ermöglicht es Gruppenmitgliedern, Mittel (in ZAR, abgerechnet über SDPKT-Wallets
durch LedgerAPI) zusammenzulegen, um gemeinsam Inhalte für das gemeinsame Ansehen zu
erwerben.

**Zustandsmaschine:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

Zustände: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**Ablauf:**
1. Initiator erstellt einen `ChipInPool` mit Zielbetrag und Inhaltsbeschreibung.
2. Teilnehmer tragen Beträge über SDPKT-Wallet-Transaktionen bei.
3. Wenn `CollectedAmount >= TargetAmount`, wechselt der Zustand zu `Funded`.
4. Das System erwirbt den Inhalt (z. B. initiiert einen BitTorrent-Download).
5. Sobald der Inhalt verfügbar ist, wechselt der Zustand zu `Acquired` und
   Watch-Together kann beginnen.

Jeder Beitrag wird mit einer SDPKT-Transaktions-ID für den Prüfpfad aufgezeichnet.

### 11.10. Verschlüsselungsmodell

| Modus | Verschlüsselung | Schlüsselverteilung |
|------|-----------|-----------------|
| Watch-Sync-Befehle | Kanal-/Gesprächsschlüssel | Vorhandene Signal-Protocol-Sitzung |
| Inhalts-Chunks (StreamFromHost) | Inhaltsschlüssel pro Manifest | Über Signal Protocol verteilt |
| BitTorrent-Stücke | Beim Ingest neu verschlüsselt | Gateway lädt Klartext aus Schwarm herunter, verschlüsselt für Mesh |
| Watch-Reaktionen | Sitzungsschlüssel | Aus Gesprächsschlüssel abgeleitet |

### 11.11. Feature-Flags

Alle Video- und Watch-Together-Funktionen sind hinter Feature-Flags gesperrt (alle
standardmäßig deaktiviert):

| Flag | Eltern | Beschreibung |
|------|--------|-------------|
| AETHER_VIDEO_CALL | AETHER_VOICE | P2P- und Gruppen-Videoanrufe |
| AETHER_VIDEO_GROUP | AETHER_VIDEO_CALL | Mehrteilige Videositzungen |
| AETHER_SCREEN_SHARE | AETHER_VIDEO_CALL | Bildschirmfreigabe in Videoanrufen |
| AETHER_WATCH_TOGETHER | AETHER_CONTENT_P2P | Synchronisierte Medienwiedergabe |
| AETHER_WATCH_REACTIONS | AETHER_WATCH_TOGETHER | Emoji- und Sprachreaktionen |
| AETHER_TORRENT_INGEST | AETHER_CONTENT_P2P | BitTorrent-Dateiakzeptanz für Mesh-Verteilung |

Feature-Flags haben übergeordnete Abhängigkeiten: Ein untergeordnetes Flag kann nur
aktiviert werden, wenn sein übergeordnetes Flag ebenfalls aktiviert ist. Dies ermöglicht
schrittweises Ausrollen.

---

## Anhang A: Konstantenreferenz

Alle Protokollkonstanten sind in `ProtocolConstants` definiert und werden hier zur
Referenz wiedergegeben:

### Routing
| Konstante             | Wert   |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE-Erkennung
| Konstante                 | Wert   |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Sicherheit
| Konstante                 | Wert   |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Konstante                  | Wert  |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 255   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Konstante                 | Wert   |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transport
| Konstante                 | Wert    |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Heartbeat
| Konstante                      | Wert  |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### Präsenz
| Konstante                          | Wert  |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### Sprache
| Konstante                 | Wert  |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### Streaming
| Konstante                    | Wert  |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

### Video
| Konstante                       | Wert  |
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

| Begriff | Definition |
|------|------------|
| **UHID** | Universal Hardware Identifier. Eine eindeutige Zeichenfolge zur Identifizierung eines Mesh-Knotens, abgeleitet aus Geräteidentität und kryptografischen Schlüsseln. |
| **RREQ** | Route Request (Routenanfrage). Ein Broadcast-Paket zur Entdeckung eines Pfads zu einem Zielknoten. |
| **RREP** | Route Reply (Routenantwort). Ein Unicast-Paket, das entlang der durch eine RREQ eingerichteten Rückwärtsroute zurückgesendet wird. |
| **IRK** | Identity Resolving Key. Ein 128-Bit-Schlüssel zur Generierung und Auflösung von BLE-Resolvable Private Addresses. |
| **RPA** | Resolvable Private Address. Eine 6-Byte-BLE-Adresse, die periodisch rotiert, aber von Peers, die den IRK des Absenders besitzen, aufgelöst werden kann. |
| **X3DH** | Extended Triple Diffie-Hellman. Ein Schlüsselvereinbarungsprotokoll für den asynchronen Sitzungsaufbau. |
| **DTN** | Delay-Tolerant Networking. Ein Store-and-Forward-Paradigma für Umgebungen mit intermittierender Konnektivität. |
| **Gateway** | Ein Mesh-Knoten mit Internetkonnektivität, der Mesh-Traffic zu/von IP-basierten Diensten überbrückt. |
| **HKDF** | HMAC-based Key Derivation Function. Wird verwendet, um mehrere Schlüssel aus einem einzigen gemeinsamen Geheimnis abzuleiten. |
| **Pre-Key-Bundle** | Ein veröffentlichter Satz von Schlüsseln, der es einem Absender ermöglicht, eine verschlüsselte Sitzung einzurichten, ohne dass der Empfänger online ist. |
| **SFU** | Selective Forwarding Unit. Ein Relay-Knoten, der einen Video-Stream von jedem Sender empfängt und ihn an alle anderen Teilnehmer verteilt, wodurch die Upload-Bandbreite pro Knoten reduziert wird. |
| **ChipIn** | Gruppenfinanzierungsmechanismus, bei dem Teilnehmer SDPKT-Mittel zusammenlegen, um gemeinsam Inhalte für das gemeinsame Ansehen zu erwerben. |
| **NAL** | Network Abstraction Layer. Das Einkapselungsformat, das von H.264- und H.265-Codecs zur Paketierung von Videorahmen verwendet wird. |

---

## Anhang C: Referenzen

1. C. Perkins, E. Belding-Royer, S. Das, „Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, Juli 2003.
2. M. Marlinspike, T. Perrin, „The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, „The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, „HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, Mai 2010.
5. K. Fall, „A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, „Bluetooth Core Specification v5.0," Dezember 2016 (Resolvable Private Address, Abschnitt 1.3.2.2).
7. NIST, „Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., „High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
