# Aether Protocol — Bedrohungsmodell

**Überprüft gegen HEAD `b8b3d22` (2026-05-06).** Dieses Dokument beschreibt,
wogegen die kryptografische Protokollschicht von `aether-protocol` schützt,
was explizit außerhalb des Geltungsbereichs liegt und welche Annahmen die
Sicherheitsaussagen voraussetzen. Es ist bewusst ehrlich gestaltet: Ein Angreifer,
der dieses Dokument liest, sollte in der Lage sein, jeden Angriff aufzulisten,
den das Protokoll **nicht** stoppt, und sollte nicht durch das Marketing in der
README in die Irre geführt werden.

Das begleitende Dokument ist [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7
(Sicherheitsmodell). Wenn die beiden voneinander abweichen, ist die Implementierung
in `src/AetherNet.Security/` maßgebend.

---

## 1. Geltungsbereich

### Was `aether-protocol` IST

Eine Signal-Protocol-artige Ende-zu-Ende-verschlüsselte Messaging-Bibliothek
sowie ein Mesh-Netzwerk-Primitiv (AODV-artiges Routing + DTN-Store-and-Forward +
SOS-Flood). Die grundlegenden Sicherheitsgarantien sind:

1. **Vertraulichkeit** — Nachrichteninhalte werden mit AES-256-GCM unter
   pro-Nachricht-Schlüsseln verschlüsselt, die aus einem Double Ratchet abgeleitet
   werden (Signal §5).
2. **Authentizität** — Jedes `MeshPacket` trägt eine Ed25519-Signatur über einen
   kanonischen signierbaren Datenpuffer (PROTOCOL_SPEC §2.4).
3. **Replay-Schutz** — Pakete werden bei doppeltem
   `(SourceUhid, PacketNonce)` innerhalb eines 5-minütigen Frischheitsfensters
   verworfen.
4. **Forward- und Post-Compromise-Secrecy** — Der Double Ratchet schlüsselt bei
   jeder DH-Pubkey-Änderung eines Roundtrips neu; ein Angreifer, der einen
   Sitzungsschlüssel kompromittiert, kann weder vergangene noch zukünftige
   Nachrichten wiederherstellen.

### Was `aether-protocol` NICHT IST

- **Kein Ersatz für Transport-Layer-Security.** Verwenden Sie TLS für
  Client→Server. Aethers E2EE ist für Peer-to-Peer-Mesh-Traffic; sobald ein
  Paket das Mesh in ein zentralisiertes Backend verlässt, liegt die
  Transportsicherheit dieses Backends in der Verantwortung des Hosts.
- **Kein Schlüsselverwaltungssystem.** Der Host stellt dauerhaften Speicher
  für Identitäts- und Pre-Key-Material über `IPreKeyStore` (oder einen
  `IKeyValueStore`-basierten Adapter) bereit. Hardware-Keystore-Integration,
  TPM-Attestierung, Key-Escrow-Recovery und Verschlüsselung im Ruhezustand
  liegen in der Verantwortung des Hosts.
- **Kein Authentifizierungssystem.** Aether authentifiziert, dass „der Inhaber
  von Identitätsschlüssel-X dieses Paket gesendet hat". Die Zuordnung von
  Identitätsschlüssel-X zu „dem Menschen Alice" ist die UX-Verantwortung des
  Hosts (Safety-Number-Vergleich, Out-of-Band-Fingerabdruckaustausch,
  vorherige Vertrauenskette).
- **Kein Datenschutznetzwerk.** Der Draht offenbart Nachrichtentyp, Paketlänge,
  Quell-UHID, Ziel-UHID, Hop-Anzahl und Zeitstempel. Es ist kein Tor.

---

## 2. Abgewehrte Angriffe

### 2.1. Abhören während der Übertragung

Jede Nutzlast wird mit AES-256-GCM unter einem pro-Nachricht-Schlüssel verschlüsselt,
der aus der symmetrischen Kette des Double Ratchets abgeleitet wird (Signal §5.1,
HMAC-SHA256 mit `0x01`/`0x02` Domain-Separation). Ein Angreifer, der jedes Paket
zwischen Alice und Bob abfängt, erhält ohne einen ihrer Sitzungsschlüssel nichts.

Verifiziert durch `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs`
und die sprachübergreifenden `fixtures/signal/expected/ratchet_step_basic.json`-Vektoren.

### 2.2. Nachrichtenfälschung

Jedes Wave-2-Paket trägt eine Ed25519-Signatur über den kanonischen
`BuildSignableData(packet)`-Puffer (`src/AetherNet.Security/Services/PacketSigningService.cs`,
PROTOCOL_SPEC §2.4). Gefälschte Pakete schlagen bei der Verifizierung fehl und werden
an jedem Hop verworfen, der den öffentlichen Identitätsschlüssel der Quelle kennt.
Route-Reply-Pakete (RREP) werden vom behaupteten Ziel signiert — Zwischenknoten können
Ziele nicht vortäuschen, da sie den privaten Ed25519-Schlüssel des Ziels nicht besitzen.

### 2.3. Replay-Angriffe

`PacketSigningService.VerifyPacketAsync`:

- Verwirft Pakete, deren `TimestampMs` mehr als 5 Minuten von der lokalen UTC abweicht
  (`FreshnessWindowMs = 5 * 60 * 1000`).
- Pflegt eine In-Memory-Dedup-Map, indiziert nach `(SourceUhid, PacketNonce)`
  mit einer 5-minütigen TTL. Der Dedup-Schlüssel wurde in Commit `5bd52a9` von
  `nonce` allein auf `(source, nonce)` geändert, um zwei Fehlermodi zu beheben:
  Sender-übergreifende Nonce-Kollisionen, die legitimen Traffic verwerfen, und
  Pre-Registration-Angriffe, bei denen ein Angreifer eine Nonce gegen einen Empfänger
  platziert, um das erste Paket des legitimen Senders zu blockieren.

Zähler: `aethernet.nonces.replayed`, `aethernet.timestamps.stale`.

### 2.4. Forward Secrecy (Vergangenheitsschlüssel-Kompromittierung)

Der Double Ratchet leitet bei jedem DH-Rotationsschritt einen neuen Sendekettenschlüssel
ab (KDF_RK, HKDF-SHA256 über `salt = current_root_key`,
`info = "aether-ratchet-rk-v1"`, 64-Byte-Block aufgeteilt 32+32 in neuen Root-
und Kettenschlüssel — `src/AetherNet.Security/Services/SignalProtocolService.cs`).
Ein Angreifer, der den aktuellen Sitzungszustand kompromittiert, kann keine
vorherige Nachricht entschlüsseln: Jeder vorherige Nachrichtenschlüssel wurde
abgeleitet und genullt (`CryptographicOperations.ZeroMemory`), bevor der nächste
Ratchet-Schritt ausgeführt wurde.

### 2.5. Post-Compromise Security (Zukunftsschlüssel-Wiederherstellung)

Wenn die Empfangsseite einen neuen `SenderEphemeralKeyX25519` in einer eingehenden
Nachricht beobachtet, führt sie beim Empfang einen DH-Ratchet-Schritt aus (Signal §5.2).
Der zwischengespeicherte Sitzungszustand des Angreifers wird beim sehr nächsten
Roundtrip veraltet; ein Angreifer, der einen Sitzungsstatus-Snapshot erstellt und
sich entfernt, kann nach einem einzigen Austausch der legitimen Parteien keine
Nachrichten mehr entschlüsseln.

Der DH-Rotationsschritt beim Empfang wurde in allen 8 Sprachen implementiert —
siehe `OPEN_ISSUES.md` Punkt 2 für die familienweite Commit-Liste.

### 2.6. One-Time-Pre-Key-Replay

Jeder One-Time-Pre-Key (OPK) wird genau einmal verbraucht. Die C#-Referenz
liefert einen 100-OPK-Pool mit FIFO-Ausgabe, lazy Top-Up bei jeder Bundle-Generierung
und lock-geschütztem Single-Shot-Verbrauch (`SignalProtocolService.TopUpOpkPoolNoLock`,
verifiziert durch `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`). Ein OPK wird
entfernt und genullt, sobald der Responder ihn während X3DH verbraucht, sodass
eine wiederholte PreKey-Nachricht, die dieselbe OPK-ID wiederverwendet, keine
Sitzung aufbauen kann.

Die anderen 7 Sprachen geben noch einen einzelnen OPK pro Sitzung aus — funktional
korrekt für sequenzielle Arbeitslasten, setzt aber bei gleichzeitigen Bundle-Fetches
einem Nebenläufigkeitsrisiko aus. Verfolgt als `OPEN_ISSUES.md` §9.

### 2.7. Sprachübergreifende Wire-Drift

Jede Implementierung muss byte-identische Ausgaben gegen das Fixture-Korpus unter
`fixtures/` erzeugen:

- `fixtures/expected/*.bin` — 10 Paketserialisierungs-Fixtures, 122
  sprachübergreifende Byte-Gleichheits-Assertions in CI.
- `fixtures/signal/expected/x3dh_basic.json` — X3DH-Mathematik (4 X25519-DHs,
  HKDF-SHA256 Root mit `info = "aether-x3dh-root-v1"`).
- `fixtures/signal/expected/ratchet_step_basic.json`,
  `ratchet_step_three_iterations.json` — symmetrische Ratchet-KDFs.
- `fixtures/signal/expected/kdf_rk_basic.json` — DH-Ratchet-Schritt.

Eine Drift im HKDF-Info-String, der Byte-Reihenfolge oder dem Padding einer
Sprache bricht den `SignalFixtureTests`-Build dieser Sprache. Wire-kompatible
Interoperabilität ist daher eine Build-Zeit-Invariante, keine Laufzeithoffnung.

### 2.8. Static-Static-DH-Kompromittierung (das früher fehlerhafte X3DH)

Vor 2026-05-05 verwendete die C#-`KEY_EXCHANGE`-Implementierung den Identitätsschlüssel
des lokalen Knotens für beide DH-Operationen — ein Static-Static-Kollaps, der die
ephemere Schlüssel-Forward-Secrecy-Eigenschaft des X3DH brach. Geschlossen durch
Commit `07a93f5`: Das echte X3DH führt nun die kanonischen 4 DHs aus:
`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`
mit einem frischen sitzungsbezogenen Ephemeral. Siehe `OPEN_ISSUES.md` §1.

### 2.9. Routing-Schleifen und Broadcast-Stürme

`RoutingService` dedupliziert RREQ-Pakete nach `(originUhid, broadcastId)` in
einem begrenzten Cache (Standard 10.000 Einträge; `ProtocolConstants.RouteRequestDedupCacheSize`).
TTL wird bei jedem Hop dekrementiert und Pakete mit `Ttl == 0` werden verworfen.
SOS-Broadcasts sind auf 3/Stunde pro Ursprung rate-begrenzt, und die
Selbst-Ursprung-Unterdrückung verhindert, dass ein Knoten sein eigenes SOS
weiterüberträgt.

### 2.10. DoS via OPK-Pool-Erschöpfung

Der OPK-Pool ist begrenzt (`OpkPoolSize`, Standard 100), und der Signal-Health-Check
meldet `Unhealthy`, wenn die verfügbaren OPKs unter
`SignalOptionsBag.MinAvailableOpks` (Standard 10) fallen. Hosts schalten Alarme
am `aether-signal`-Gesundheitsstatus. Ein Angreifer, der OPKs durch Bundle-Fetches
erschöpft, kann die konfigurierte Poolgröße nicht überschreiten; das X3DH des
Responders funktioniert für bereits ausgegebene Bundles weiter und erholt sich,
sobald der Top-Up bei der nächsten Bundle-Generierung ausgeführt wird.

### 2.11. Passives BLE-Geräte-Tracking

Ein passiver Scanner, der eine stabile BLE-MAC oder eine stabile Service-UUID
protokolliert, kann ein Gerät über Zeit und Ort hinweg verfolgen. `BlePrivacy`
(`src/AetherNet.Security/Privacy/BlePrivacy.cs`) schließt den Identifier-Verkettungs-
Vektor: Die beworbene Service-UUID wird alle 15 Minuten als
`HMAC-SHA256(rotation_key, window)` neu abgeleitet (PROTOCOL_SPEC §12.3), und Peers
werden über auflösbare private Adressen (IRK + `ah`) statt über eine feste MAC
adressiert. Ohne den Rotationsschlüssel oder den IRK lassen sich zwei Werbebotschaften
nicht verketten. Angeheftet an `fixtures/bleprivacy/`.

**Restrisiko.** Dies schließt nur den BLE-Identifier-Vektor — es macht Aether **nicht**
zu einem Datenschutznetzwerk (§1). Sobald ein Paket im Mesh ist, offenbart der
Klartext-`MeshPacket`-Header weiterhin Quell-/Ziel-UHID, Typ, Länge und Timing
(Traffic-Analyse bleibt außerhalb des Geltungsbereichs, §3.3), und RF-Layer-
Fingerprinting wird nicht adressiert. Das Aussenden der rotierenden Identifikatoren
über die Luft ist Aufgabe des Host-BLE-Stacks — die Bibliothek leitet sie lediglich ab.

### 2.12. Erzwungene Schlüsseloffenlegung (Nötigung)

Ein Angreifer im physischen Besitz, der den Benutzer zum Entsperren nötigt.
`PanicWipe` (`src/AetherNet.Security/Privacy/PanicWipe.cs`) akzeptiert eine
**Nötigungs-PIN** — in konstanter Zeit gegen einen gespeicherten `SHA-256(pin)`
abgeglichen (kein Timing-Leck durch vorzeitigen Abbruch) —, die jeden Identitätsschlüssel
sicher löscht (Überschreiben mit Zufallsdaten, dann Nullen) über das gesamte
Schlüsselnamen-Manifest, sodass das ausgehändigte Gerät keine nutzbare Identität mehr
enthält. Angeheftet an `fixtures/panicwipe/`.

**Restrisiko.** Best-Effort und ausdrücklich begrenzt: Es verteidigt **nicht** gegen
ein forensisches Abbild, das *vor* der Löschung erstellt wurde, gegen Flash-Wear-
Leveling, das eine frühere Kopie der Schlüssel-Bytes bewahrt, gegen einen Angreifer,
der die *echte* PIN erzwingt, oder gegen Nötigung, nachdem Nachrichten bereits gelesen
wurden. Der Vergleich in konstanter Zeit mildert das Timing beim PIN-Raten, nicht einen
vollständigen Seitenkanal-Angreifer (§3.2).

### 2.13. Verlust des einzigen Geräts (Wiederherstellung)

Kein Angreifer, sondern der Verfügbarkeitsausfall durch den Verlust der einzigen Kopie
einer Identität. Das Wiederherstellungsphrasen-Backup (`src/AetherNet.Security/Backup/`)
kodiert den 32-Byte-Ed25519-Identitäts-Seed als prüfsummengesicherte 24-Wort-BIP-39-
Phrase (PROTOCOL_SPEC §12.4), die die Identität auf jedem Gerät wiederherstellt — kein
Server und kein Verwahrer hält sie.

**Restrisiko — eine neue Diebstahlsfläche.** Die Phrase **ist** die Identität: Wer die
24 Wörter liest, kann den Benutzer vollständig imitieren, ohne jede Widerrufsmöglichkeit.
Sie tauscht ein Geräteverlust-Risiko gegen ein Papiergeheimnis-Risiko. Die Bibliothek
kodiert/dekodiert und prüfsummt die Phrase; sichere Anzeige, Speicherung und die
optionale BIP-39-Passphrase liegen in der Verantwortung des Hosts.

### 2.14. Einschleusung eines betrügerischen Geräts in die Multi-Geräte-Synchronisation

Ein Angreifer, der versucht, ein von ihm kontrolliertes Gerät in das Sync-Set eines
Opfers einzuschleusen oder Sync-Datensätze zu fälschen. Ein `DeviceLink`
(`src/AetherNet.Security/Sync/`) ist **Ed25519-signiert durch den Identitätsschlüssel**
(PROTOCOL_SPEC §12.1), sodass nur der Identitätsinhaber ein neues Gerät autorisieren
kann — ein unsigniertes oder falsch signiertes Link schlägt bei der Verifizierung fehl.
`SyncRecord`-Nutzlasten reisen E2E-verschlüsselt innerhalb des DTN-/Mesh-Pfads, sodass
Relays sie zwar tragen, aber nicht lesen können. Angeheftet an `fixtures/sync/`.

**Restrisiko.** Dies authentifiziert die *Verknüpfung*, nicht das spätere Verhalten des
verknüpften Geräts: Ein Gerät, das legitim verknüpft und *dann* kompromittiert wird,
sieht den gesamten synchronisierten Zustand — die Synchronisation hat keine
Forward-Secrecy pro Datensatz. Die Abstimmung erfolgt nach Last-Write-Wins über
`(created_at_ms, logical_clock, device_id, record_id)`, sodass ein verknüpftes Gerät
mit einer verschobenen Uhr beeinflussen kann, welcher Datensatz gewinnt; die
Uhr-Integrität ist Sache des Hosts. Die Signatur-Byte-Parität trägt die in
PROTOCOL_SPEC §12.1 vermerkte Swift/CryptoKit-Ausnahme.

---

## 3. Außerhalb des Geltungsbereichs

Dies sind reale Angriffe, die das Protokoll **nicht** stoppt. Einige sind theoretisch
in einem zukünftigen Release abschwächbar; andere sind grundlegend eine Host-Angelegenheit.

### 3.1. Endpunkt-Kompromittierung

Wenn ein Angreifer Root-Zugang auf Alices Gerät hat, kann er die privaten Bytes
ihres Identitätsschlüssels aus dem Speicher lesen und jede Sitzung entschlüsseln,
die sie hält. Das Protokoll setzt voraus, dass der Prozessspeicher des Geräts
vertrauenswürdig ist. Gegenmaßnahmen (Plattform-Keystore, SGX, hardware-gestützte
Keystores) liegen explizit in der Verantwortung des Hosts — siehe Abschnitt 4.

### 3.2. Seitenkanalangriffe

Die Referenzimplementierung verwendet `CryptographicOperations.FixedTimeEquals`
für den Ratchet-Pubkey-Vergleich (`SignalProtocolService.ConstantTimeEquals`),
ist jedoch nicht spezifisch gehärtet gegen:

- Timing-Seitenkanäle in AES-GCM (die .NET BCL `AesGcm` ist hardware-beschleunigt
  auf AES-NI-fähigen CPUs; das Timing des Software-Fallbacks ist nicht auditiert).
- Power-Analyse-Seitenkanäle (reine Software — keine Hardware-Gegenmaßnahmen).
- Cache-Timing auf Schlüsselableitungspfaden (HKDF-SHA256 über die BCL).

Ein Angriff auf Labor-Niveau eines Nationalstaats auf einem gestohlenen, entsperrten
Gerät ist plausibel.

### 3.3. Traffic-Analyse

Das Wire-Format offenbart:

- Paket-**Typ** (1 Byte bei Offset 1 — RREQ vs. Data vs. SOS ist im Klartext).
- Paket-**Länge** (Nutzlasten werden nicht aufgefüllt).
- **Quell- und Ziel-UHIDs** (UTF-8, im Klartext).
- **Zeitstempel**, **TTL** und **Priorität**.

Padding, Cover-Traffic und Onion-Routing sind nicht implementiert. Ein Angreifer,
der passiv BLE-/Wi-Fi-Traffic beobachten kann, kann einen Kontaktgraphen und ein
Timing-Profil jedes Gesprächs erstellen, auch wenn er den Inhalt nicht lesen kann.
Dies ist eine bekannte Einschränkung; eine Abschwächung würde einen Wire-Format-
Bruch erfordern und ist nicht auf der aktuellen Roadmap.

### 3.4. Quantenangriffe

X25519 (RFC 7748) und Ed25519 (RFC 8032) brechen beide unter einem ausreichend
großen Quantencomputer, der den Shor-Algorithmus ausführt. Das Protokoll ist
**nicht post-quanten**. Eine zukünftige Migration zu einem hybriden
Kyber + X25519 / Dilithium + Ed25519-Schema ist ein bekanntes Anliegen, ist jedoch
nicht geplant. Bestehende Chiffretexte, die heute von einem Angreifer aufgezeichnet
werden, der auf „Jetzt ernten, später entschlüsseln" setzt, sind gefährdet, wenn ein
CRQC innerhalb des relevanten Zeithorizonts eintrifft.

### 3.5. Gruppen-Messaging im großen Maßstab

`AetherNet.Security` liefert eine `IGroupKeyProvider`-Naht, aber das vollständige
Signal Sender Keys-Protokoll (die asynchrone Gruppen-Messaging-Konstruktion, die
Signal verwendet) ist ab HEAD **nicht** implementiert. Hosts, die heute
Gruppen-Messaging benötigen, fallen auf N paarweise Sitzungen zurück — was
funktioniert, aber O(N)-Kosten pro Gruppenversand hat. PROTOCOL_SPEC §7
behandelt nur Bedrohungen mit einem einzigen Empfänger.

### 3.6. Identitätsverifizierung beim ersten Kontakt (TOFU)

Aether authentifiziert, dass „der Peer, der Identitätsschlüssel-X hält, dies
signiert hat". Es authentifiziert **nicht**, dass „Identitätsschlüssel-X tatsächlich
dem Menschen Alice gehört, mit dem der Benutzer zu sprechen erwartet". Beim ersten
Kontakt kann ein aktiver Man-in-the-Middle, der das Netzwerk während des allerersten
Bundle-Austauschs kontrolliert, seinen eigenen Identitätsschlüssel einsetzen, sein
eigenes Bundle signieren und den Traffic in beide Richtungen transparent als
Proxy weiterleiten.

Dies ist die Standard-Signal-„Trust On First Use"-Schwäche. Die kanonische
Gegenmaßnahme ist der Safety-Number-/Fingerabdruckvergleich Out-of-Band (persönlich,
über einen separaten Kanal, auf einem vorgeteilten Verifizierungsbildschirm).
Das Protokoll stellt derzeit keine öffentliche API-Oberfläche für die
Safety-Number-Ableitung bereit; wird als Lücke verfolgt (noch nicht in
`OPEN_ISSUES.md`) — Host-UX sollte nicht vorgeben, standardmäßig verifiziert zu sein.

### 3.7. Netzwerkschichtangriffe auf den zugrunde liegenden Transport

Signal-Jamming (BLE, Wi-Fi, NearLink), RF-Layer-Denial-of-Service und Angriffe
auf die Pairing-/Bonding-Flows des Transports liegen außerhalb des Geltungsbereichs.
Der Transport (`ITransportService`) wird als opake Byte-Pipe behandelt. Ein Jammer,
der das Spektrum besitzt, verhindert, dass Aether irgendetwas übermittelt.

### 3.8. Routing-Angriffe jenseits des Dedup-Fensters

Sybil-Flooding durch kurzlebige Knoten, die noch kein Zuverlässigkeits-Score
angesammelt haben, opportunistisches Relay-Dropping, das die Zuverlässigkeitsheuristik
nicht auslöst, und Ressourcenerschöpfungsangriffe, die unter den Rate-Limits bleiben,
werden nicht spezifisch abgeschwächt. Der Zuverlässigkeits-Score (PROTOCOL_SPEC §3.5)
deprioritisiert nachweislich schlechte Knoten, ist jedoch kein vollständig
ausgearbeitetes Byzantine-resilientes Routing-Protokoll.

---

## 4. Voraussetzungen für die Gültigkeit von Sicherheitsaussagen

Die Abwehrmaßnahmen in Abschnitt 2 setzen folgende Invarianten voraus. Wenn eine
davon bricht, geht die entsprechende Sicherheitseigenschaft verloren.

1. **Identitätsschlüssel-Dauerhaftigkeit.** Der Host speichert die langfristigen
   Ed25519- + X25519-Identitätsschlüsselpaare dauerhaft und sicher (z.B. über
   `IPreKeyStore` gegen einen `FileSystemKeyValueStore`, eingehüllt in
   `EncryptedKeyValueStore`, oder gegen den Plattform-Keystore). Verlust eines
   Identitätsschlüssels = vollständige Kontokompromittierung; der Inhaber des
   privaten Schlüssels kann alles als der ursprüngliche Peer signieren.

2. **CSPRNG-Korrektheit.** `RandomNumberGenerator.GetBytes` und
   `RandomNumberGenerator.GetInt32` auf der Zielplattform produzieren
   kryptografisch sichere Ausgaben. Das gesamte Protokoll — ephemere Schlüssel,
   AES-GCM-Nonces, Paket-Nonces, OPK-IDs — hängt davon ab. Auf Plattformen,
   auf denen die BCL-Zufallsquelle beeinträchtigt ist (einige eingebettete Ziele,
   fehlerhafte Linux-Entropiepools), bricht der gesamte Vertrauensbaum zusammen.

3. **Systemuhr innerhalb von ±5 Minuten UTC.** Der Replay-Schutz ist
   zeitstempel-gefenstert. Ein Gerät mit einer stark falschen Uhr lehnt entweder
   jedes Paket ab (Uhr zu alt) oder akzeptiert Replays unbegrenzt (Uhr zu neu).
   Hosts SOLLTEN beim App-Start eine Plausibilitätsprüfung gegen eine vertrauenswürdige
   Zeitquelle durchführen.

4. **Atomarer OPK-Verbrauch.** Wenn ein `IPreKeyStore`-gestütztes
   `ConsumeOneTimePreKeyAsync(id)` gleichzeitig mit einer Responder-X3DH-Operation
   gegen dieselbe ID ausgeführt wird, MUSS der Verbrauch atomar erfolgreich sein
   oder scheitern. Der C#-Referenzpool serialisiert den Verbrauch unter `_preKeyLock`;
   ein Host-gelieferter Store auf einem nicht-transaktionalen Backend (z.B. ein
   naiver Datei-Store mit Read-Modify-Write) kann denselben OPK zweimal verbrauchen
   lassen, was Eigenschaft 2.6 bricht. `KeyValuePreKeyStore` verwendet direkt
   `IKeyValueStore.RemoveAsync` für den Verbrauch — atomar, sofern das Remove des
   zugrunde liegenden KV atomar ist.

5. **First-Contact-Identitätsverifizierung.** Der öffentliche Identitätsschlüssel
   des Peers wurde Out-of-Band verifiziert (Safety-Number, Fingerabdruck, vertrauenswürdiges
   Verzeichnis), bevor die erste Nachricht ausgetauscht wurde — oder der Host
   akzeptiert das TOFU-Risiko und ist damit zufrieden, eine Schlüsseländerung beim
   nächsten Kontakt zu erkennen. Ohne dies ist §3.6 ein offenes MitM-Fenster.

6. **Prozessspeicher des Hosts ist nicht für Angreifer lesbar.** §3.1.

---

## 5. Bekannte Schwachstellen und Gegenmaßnahmen

### 5.1. First-Contact-MitM (TOFU)

**Schwachstelle:** Ein aktiver Angreifer, der den Peer-to-Peer-Link während
des allerersten Bundle-Austauschs kontrolliert, kann sein eigenes Bundle
einsetzen und als Proxy für den Traffic fungieren.
**Gegenmaßnahme:** Host-UX muss einen Safety-Number-/Public-Key-Fingerabdruckvergleichs-
Fluss bereitstellen, bevor ein Kontakt als verifiziert behandelt wird. Eine öffentliche
API-Oberfläche für die Safety-Number-Ableitung ist noch nicht in `AetherNet.Security`
enthalten; wird als Lücke verfolgt.

### 5.2. Verzögerung bei der Signed-Pre-Key-Rotation

**Schwachstelle:** Bis der Host `RotateSignedPreKeyAsync` aufruft, wird derselbe
SPK in jedem Bundle ausgeliefert. Ein Angreifer, der den privaten SPK-Schlüssel
lernt (z.B. über §3.1 Endpunkt-Kompromittierung), kann X3DH gegen jedes erfasste
Bundle ausführen, das seit der letzten Rotation datiert.
**Gegenmaßnahme:** Planen Sie tägliche `RotateSignedPreKeyAsync`-Aufrufe. Die
Standard-`SignedPreKeyRotationOptions` behalten 3 vorherige SPKs bei, sodass
in-flight-Nachrichten, die unter einem kürzlich rotierten Schlüssel signiert sind,
während des Rotationsfensters noch entschlüsselt werden. Das Standard-Rotationsintervall
beträgt 7 Tage — Anwender, die gegen aktiv angegriffene Benutzer arbeiten,
sollten dies verkürzen.

### 5.3. In-Memory-Sitzungszustand ohne Persistenz

**Schwachstelle:** Wenn `SignalProtocolService` ohne einen `sessionStore` konstruiert
wird, verliert ein Prozessabsturz oder -neustart jede aktive Sitzung. Forward Secrecy
ist intakt (die verlorenen Schlüssel können nicht wiederhergestellt werden), aber die
nächste Nachricht vom Peer kann nicht entschlüsselt werden, da die Empfangskette
fehlt.
**Gegenmaßnahme:** Verbinden Sie `KeyValueSignalSessionStore` mit einem dauerhaften
`IKeyValueStore` für jede Produktionsbereitstellung. Das Beispiel-Konsolen-Demo
verwendet `InMemoryDtnBundleStore` usw. aus Gründen der Klarheit; Produktionshosts
dürfen dies nicht tun.

### 5.4. Übergangs-Byte beim Wire-Kompressions-Flag

**Schwachstelle:** `MessagingService` hat eine optionale Brotli-Kompressionsnaht,
die ein bedingungsloses Flag-Byte zur Klartextumhüllung voranstellt. Ein Peer,
der Pre-Kompression-Code ausführt, liest das Flag-Byte fälschlicherweise als erstes
Byte der Anwendungsnutzlast.
**Gegenmaßnahme:** Anwender setzen `MessagingOptions.Compression.Enabled = false`,
bis jeder Peer die neuen Bits hat. Das Flag-Byte wird durch eine zukünftige
Capability-Negotiation-Handshake gesteuert. Siehe den Migrationsvermerk zu
`CompressionOptions`.

### 5.5. C-Sprach-Lücke

**Schwachstelle:** Die C-Implementierung liefert nur die X25519- + KDF_RK-Primitive
sowie den Fixture-Verifikator. Sie implementiert **nicht** die vollständige
`SignalProtocolService`-API (X3DH-Sitzungsaufbau, OPK-/SPK-Lebenszyklus,
DH-Ratchet-Integration). Hosts, die Aether auf C-basierten Mikrocontrollern
einsetzen, können die aktuelle C-Oberfläche nicht für Ende-zu-Ende-verschlüsselten
Traffic verwenden. Verfolgt als `OPEN_ISSUES.md` §11.

### 5.6. OPK-Pool ist nur C#

**Schwachstelle:** Der 100-OPK-Pool mit FIFO-Ausgabe und atomarem Verbrauch
(Abwehr 2.6) ist ein C#-Referenzmerkmal. Die Go-, Python-, TypeScript-, Rust-,
Swift- und Kotlin-Implementierungen geben noch einen einzelnen OPK pro Sitzung aus.
Unter simultaner Initiator-Last können zwei Responder, die um dieselbe Bundle-Quelle
konkurrieren, beide denselben OPK beobachten, und X3DH kann einen Sitzungszustand-
Mismatch produzieren.
**Gegenmaßnahme:** Für die betroffenen Sprachen den Bundle-Verbrauch host-seitig
serialisieren (ein Initiator auf einmal pro Peer). Verfolgt als `OPEN_ISSUES.md` §9.

### 5.7. Demo-Signing in Nicht-C#-Sprachen

**Schwachstelle:** Die pro-Sprache-Demo-Programme (Go, Python, TS, Rust, Swift,
Kotlin, C) signieren zur Visualisierung die vollständig serialisierten Wire-Bytes
anstatt des kanonischen `BuildSignableData`-Puffers. Der Bibliothekscode in diesen
Sprachen ist korrekt — nur die Demos nehmen die Abkürzung, aber das ist für
Portierende verwirrend.
**Gegenmaßnahme:** Verfolgt als `OPEN_ISSUES.md` §10. Behandeln Sie Schritt 3 des
C#-Demos als den kanonischen Ablauf.

---

## 6. Sicherheitsprobleme melden

Siehe [`SECURITY.md`](../SECURITY.md) für die Richtlinie zur verantwortungsvollen
Offenlegung. Senden Sie eine E-Mail an `security@thegeeknetwork.co.za` mit
Reproduktionsschritten; erwarten Sie eine Bestätigung innerhalb von 48 Stunden und
eine erste Einschätzung innerhalb von 7 Tagen.

Probleme, die gemäß Abschnitt 3 außerhalb des Geltungsbereichs liegen, sind
dennoch willkommene Berichte — wir möchten lieber wissen, was wir nicht abwehren,
als dass ein Benutzer die Lücke in der Produktion entdeckt.
