# AetherNet — Offline-First-Mesh-Netzwerkprotokoll

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet ist ein quelloffenes, MIT-lizenziertes Mesh-Netzwerkprotokoll** zum Senden von Nachrichten, Dateien, Sprache und Video an Personen in der Nähe — mit **keinem Internet, keinen Servern und keiner Registrierung**. Geräte verbinden sich direkt über Bluetooth, Wi-Fi Direct, NearLink und LoRa; wenn der Empfänger außer Reichweite ist, springen Nachrichten über andere Geräte und warten bis zu 72 Stunden auf eine Route. Es liefert **byte-für-byte identische Implementierungen in acht Programmiersprachen** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift und C.

Dateien, Nachrichten und Streams mit Personen in der Nähe teilen. Kein WLAN. Keine mobilen Daten. Keine Registrierung. Wie AirDrop, aber funktioniert mit jedem, auf jeder Plattform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Ein Protokoll, acht Sprachen, identisch auf der Leitung.** Aether ist in **C#, Rust, TypeScript, Python, Go, Kotlin, Swift und C** implementiert — und jedes Paket ist über alle hinweg byte-für-byte identisch, erzwungen durch ein gemeinsames sprachübergreifendes Fixture-Korpus in CI. Bauen Sie Ihren Knoten in einer der acht Sprachen; er ist mit allen anderen interoperabel. Diese README ist außerdem in 11 menschlichen Sprachen verfügbar (Links oben).

## Was kann man damit tun?

**Vorlesungsnotizen teilen, ohne Daten zu verbrauchen.**

Sie sind in einer Lerngruppe. Jemand hat Prüfungsaufgaben auf dem Telefon. Aether sendet sie direkt über Bluetooth auf Ihr Gerät — ohne Hotspot, ohne WhatsApp-Gruppe, ohne Dateigrößenbeschränkung. Wenn jemand in der Gruppe außer Reichweite ist, springt die Datei über andere Geräte, bis sie ankommt. Nachrichten warten bei Bedarf bis zu 72 Stunden auf eine Route.

```
  [Sie] ──BLE──▶ [Freund] ──WiFi──▶ [Freundes Freund]
    notes.pdf           weitergeleitet, verschlüsselt
```

**Herausfinden, was um Sie herum passiert.**

Sie sind bei einer Campus-Veranstaltung oder einem Festival. Aether entdeckt andere Geräte in der Nähe über Bluetooth und WiFi Direct — kein App-Feed, kein Algorithmus. Sie sehen, was wirklich um Sie herum ist, nicht was beworben wird.

**Einen Notruf senden, wenn kein Signal vorhanden ist.**

Ihr Telefon hat keinen Empfang. Aether sendet eine Notfallnachricht an jedes Gerät in Reichweite, und diese Geräte leiten sie weiter. Kein Mobilfunkmast erforderlich.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: erreicht jedes Gerät in Reichweite
```

**Private Gruppenkanäle erstellen.**

Ein Kanal für Ihren Wohnheimsflur, Ihre Gesellschaft, Ihr Projektteam. Nur verifizierte Mitglieder können Nachrichten lesen oder senden. Kein Server speichert die Unterhaltung.

**Gegenstände an Personen in der Nähe verkaufen.**

Ein Lehrbuch zum Verkauf anbieten. Personen, die sich im Bereich des Mesh befinden, sehen es. Kein Marktplatzkonto, keine Einstell-Gebühren — nur Nähe.

**Gemeinsam einen Film über das Mesh ansehen.**

Ihre Gruppe veranstaltet einen Filmabend. Jemand hat die Datei. Aether synchronisiert die Wiedergabe auf jedem Gerät — Play, Pause, Suche — alles im Gleichschritt. Wenn nur einige Personen die Datei haben, verteilt das Mesh sie in Echtzeit als P2P-Stream. Jeder zahlt über SDPKT, um sie zu kaufen, wenn niemand sie hat.

## Wie es funktioniert

Geräte kommunizieren direkt miteinander über Bluetooth, WiFi Direct oder NearLink. Keine Internetverbindung, kein Server, keine zentrale Infrastruktur.

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

Wenn eine Nachricht ihr Ziel nicht direkt erreichen kann, wird sie über andere Geräte weitergeleitet. Diese Relay-Geräte können nicht lesen, was sie transportieren — jede Nachricht ist mit AES-256-GCM verschlüsselt. Jedes Paket ist mit Ed25519-Identitätsschlüsseln signiert, und gefälschte Pakete werden vom Netzwerk verworfen.

> **Hinweis zur Sicherheitsreife (vor dem Einsatz lesen):** Echtes X3DH (4 X25519-DHs), der vollständige Signal Double Ratchet (DH-Rotationsschritt beim Empfang, KDF_RK, 0x01/0x02 Chain-Ratchet) und der Einmal-Pre-Key-Pool (Standard 100 OPKs, FIFO, lock-geschützt) sind in **allen 8 Sprachen** implementiert und an ein gemeinsames sprachübergreifendes Fixture-Korpus unter `fixtures/signal/` gebunden. Das einzige verbleibende offene Element ist das physische RF-Bring-up auf echter BLE-Hardware (verfolgt in `OPEN_ISSUES.md`).

Keine Konten, keine Telefonnummern, keine E-Mail-Adressen. Sie erzeugen ein Schlüsselpaar und sind im Netzwerk.

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

**Routing** — AODV mit signierten Routen-Antworten. Jede Routen-Antwort ist mit dem Ed25519-Schlüssel des Ziels signiert, sodass kein Gerät vorgeben kann, ein Ziel zu sein, das es nicht ist.

**Store-and-Forward** — Wenn keine Live-Route vorhanden ist, werden Pakete bis zu 72 Stunden aufbewahrt, bis ein Pfad sich öffnet.

**Transportauswahl** — Das Protokoll wählt den richtigen Transport je Paket. Kleine Steuernachrichten werden über BLE übertragen. Massentransfers verwenden WiFi Direct. NearLink, wenn verfügbar.

**Sprache, Video und Streaming** — Videoanrufe mit Codec-Aushandlung (H.264/H.265/VP8), transportabhängige Qualitätswahl, Gruppenvideos mit automatischem SFU-Relay, synchronisiertes Watch-Together mit RTT-Kompensation und adaptives Bitrate-Streaming.

**Replay-Schutz** — Nonce-Deduplizierung mit einem 5-Minuten-Zeitstempel-Frischheitsfenster.

## Was Sie bekommen — jeder Dienst, in jeder Sprache

Aether ist nicht nur ein Transport. Jeder vom Protokoll reservierte Pakettyp ist jetzt ein **echter, funktionierender Dienst in allen 8 Sprachen**, und jeder serialisiert zu **byteidentischen Leitungspaketen** — ein vom Go-Knoten gebautes Paket wird unverändert vom Swift-, Rust-, C-, Python-, TypeScript-, Kotlin- oder C#-Knoten dekodiert. Jeder Dienst ist an ein gemeinsames sprachübergreifendes Fixture unter `fixtures/<service>/` gebunden und wird durch sprachspezifische Unit-Tests geprüft, wobei Swift und C zusätzlich auf dem macOS-Build-Server verifiziert werden.

| Fähigkeit | Was sie tut | Pakettyp(en) | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Präsenz-Beacon & -Abfrage** | „Ich bin hier“ ankündigen und „Wer ist in der Nähe?“ fragen — über eine **rotierende, schlüsselabgeleitete ephemere ID** (nicht Ihre echte Identität) plus einen groben Geohash | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Leichtgewichtiges Liveness-Keep-Alive zwischen verbundenen Peers | 10 | `fixtures/heartbeat/` | ✅ |
| **Profil-Sync** | Eine signierte Profilkarte mit einem Peer über das Mesh austauschen | 23 | `fixtures/profiles/` | ✅ |
| **Ephemere-ID-Ankündigung** | Einem Freund privat Ihre aktuelle rotierende Routing-ID mitteilen, damit er Sie auch nach deren Rotation noch erreichen kann | 56 | `fixtures/erid/` | ✅ |
| **Pre-Key-Austausch** | Ein Signal-Pre-Key-Bundle über das Mesh anfordern und zustellen, um eine Ende-zu-Ende-Sitzung mit jemandem aufzubauen, den Sie nie getroffen haben | 25, 26 | `fixtures/prekey/` | ✅ |
| **Kanäle** | Signierte Nachrichten an einen privaten, nur für Mitglieder zugänglichen Gruppenkanal | 7 | `fixtures/channels/` | ✅ |
| **Push-to-Talk** | Walkie-Talkie-Sprach-Frames (opake kodierte Audio-Nutzlast) | 15 | `fixtures/media/` | ✅ |
| **Bildschirmfreigabe** | Bildschirmfreigabe-Video-Frames (opake kodierte Video-Nutzlast) | 32 | `fixtures/media/` | ✅ |
| **Anrufsteuerung** | Klingel-/Annehmen-/Ablehnen-/Auflegen-Signalisierung für Sprach- und Videoanrufe | 27 | `fixtures/videocall/` | ✅ |
| **SOS-Bestätigung** | Dem Absender bestätigen, dass sein Notruf-Broadcast empfangen wurde | 6 | `fixtures/sos/` | ✅ |
| **Space-Breadcrumbs** | Standortmarkierte Entdeckungskrümel für die „Was ist um mich herum“-Schicht | 40 | `fixtures/space/` | ✅ |
| **Forge-Ankündigung** | Ein abgeleitetes/geschmiedetes Inhaltsartefakt dem Mesh ankündigen | 41 | `fixtures/forge/` | ✅ |
| **Vault-Shard-Anfrage** | Einen Erasure-codierten Speicher-Shard abrufen (beliebige K von N Shards bauen die Datei wieder auf) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandbreitenmessung** | Verbindungsdurchsatz per Probe/Ack/Gossip messen, damit das Mesh über die dickste Leitung routet (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Diese sitzen oben auf den bereits vollständigen Diensten **Messaging, 1-zu-1- und Gruppensprache, Videoanrufe, Live-Streaming, Watch-Together, AODV-Routing, DTN-Store-and-Forward und SOS-Flood** — ebenfalls in allen 8 Sprachen implementiert.

> **Was „gebaut“ hier genau bedeutet.** Jeder Dienst erzeugt und verarbeitet sein Leitungspaket, löst die richtigen Ereignisse aus und ist an ein Byte-Level-Fixture gebunden, das die gesamte Sprachfamilie erfüllen muss. Ihre Anwendung verdrahtet den Dienst mit seiner Signal-Sitzung, Routing-Tabelle und dem lokalen Zustand. Dies ist die Protokollschicht — bewiesen in Code, Tests und sprachübergreifenden Byte-Fixtures — auf demselben ehrlichen RF-Fundament wie alles andere: Jeder Pfad, der letztlich über ein Funkgerät läuft, ist feldunverifiziert, bis das in `OPEN_ISSUES.md` verfolgte Hardware-Bring-up abgeschlossen ist.

## Sicherheit & Datenschutz

Über die Leitungsdienst-Suite hinaus liefert Aether eine kleine **Sicherheits- & Datenschutzschicht** — Identitätsschlüsselverwaltung und Anti-Tracking auf der Verbindungsschicht. Wie alles andere ist jede in **allen 8 Sprachen** implementiert und an ein gemeinsames sprachübergreifendes Fixture unter `fixtures/<feature>/` gebunden (Swift und C zusätzlich auf dem macOS-Build-Server verifiziert). Dies sind *nicht* vier weitere der 18 Leitungsdienste: drei definieren überhaupt **keinen neuen Leitungspakettyp**, und der vierte trägt seine eigenen Umschläge **innerhalb des bestehenden DTN/Mesh-Pfads** statt als neues reserviertes Paket.

| Fähigkeit | Was sie tut | Schicht | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Wiederherstellungsphrasen-Backup** | Eine Identität als **24-Wort-BIP-39**-Phrase sichern und auf jedem Gerät wiederherstellen. Standard-BIP-39 (gegen die offiziellen Trezor-Vektoren verifiziert), SHA-256-prüfsummiert, sodass ein falsch getipptes Wort *zurückgewiesen* wird, nie stillschweigend falsch. Kein Server, kein Verwahrer — die Phrase **ist** die Identität. | lokal | `fixtures/bip39/` | ✅ |
| **Bluetooth-Tracking-Schutz** | Leitet eine rotierende, schlüsselabgeleitete BLE-**Service-UUID** (HMAC-SHA256, 15-Minuten-Fenster) und **auflösbare private Adressen** (IRK + die RFC-Funktion `ah`, AES-128) ab — das Anti-Tracking-Material, das ein BLE-Werbetreibender braucht, damit ein passiver Scanner ihn nicht über Zeit oder Ort hinweg verknüpfen kann. | Verbindungsschicht | `fixtures/bleprivacy/` | ✅ |
| **Panik-Löschung** | Eine **Nötigungs-PIN** (SHA-256, zeitkonstant verglichen), die unter Zwang jeden Identitätsschlüssel sicher löscht — mit Zufall überschreiben, dann nullen — sodass nichts wiederherstellbar bleibt. | lokal | `fixtures/panicwipe/` | ✅ |
| **Mehrgeräte-Sync** | **Dezentraler, serverloser** Sync über Ihre *eigenen* Geräte: ein Ed25519-signierter **DeviceLink** koppelt sie, und Last-Write-Wins-**SyncRecord**-Umschläge gleichen den Zustand ab — Ende-zu-Ende-verschlüsselt über das bestehende DTN/Mesh übertragen, ohne Cloud-Konto und ohne Sync-Server. | über DTN | `fixtures/sync/` | ✅ |

**Eine ehrliche Asymmetrie.** Der Mehrgeräte-`DeviceLink` ist Ed25519-signiert, und diese Signatur ist **byteidentisch über 7 der 8 Sprachen**. Apples CryptoKit *randomisiert* Ed25519-Signaturen absichtlich, sodass auf Swift die 64 Signatur-Bytes jedes Mal abweichen — aber der **signierte Körper ist byteidentisch** und jeder Link verifiziert sich weiterhin auf allen 8 SDKs, sodass Swift **Verifikations**-Parität statt Signatur-Byte-Parität erreicht. Das ist eine Eigenschaft der Plattform-Kryptographie, kein Defekt, und es ist die einzige Stelle über diese vier Funktionen hinweg, an der „byteidentisch“ ein Sternchen trägt. Die vollständigen Leitungsformate stehen in [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12; das Bedrohungsmodell steht in [`THREAT_MODEL.md`](THREAT_MODEL.md).

## Transporte

Jeder Transport hat einen Farbnamen, der im gesamten Quellcode verwendet wird. `IsAvailable` sperrt hardwareblockierte Pfade — der `TransportManager` überspringt sie und fällt auf den nächsten verfügbaren Transport zurück.

**Statuslegende:** ✅ echt, gebaut & verifiziert · ⏳ echt, Verifizierung in Arbeit · ⚠️ echt auf einigen Plattformen, Stub auf anderen · ❌ Stub (noch kein Transportcode).

| Farbe | Name | Reichweite | Bandbreite | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Echt — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Echt — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC-Relay | Unbegrenzt | ~10 Mbps | ✅ Echt — Windows; Relay-Server in `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Internet-Datenkanal | Unbegrenzt | ~100 Mbps | ✅ Echt in allen 8 Sprachen — **Loopback-verifiziert in allen 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust lassen jeweils zwei Peers Bytes über einen echten ICE-Datenkanal austauschen) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Echt auf Android (`android/white/`); Windows = echtes BLE-GATT + RSSI −40 dBm Näherungsannäherung (`WinNfcBleTransportService`, kompiliert net9/10, laufzeit-unverifiziert) — `Windows.Networking.Proximity` in Win 11 entfernt |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Echt auf HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — On-Device-Verifizierung ausstehend); Android + Windows = echte SSAP-over-BLE-Annäherung (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; kompiliert + unit-getestet, laufzeit-unverifiziert) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37,5 kbps | ⚠️ Echter RYLR SX127x/SX126x-Seriell-Treiber (`LoRaSerialTransport` in C#/Go/Rust/C; kompiliert, laufzeit-unverifiziert — benötigt ein physisches Modul); BLE-Coded-PHY-Bridge weiterhin ein dokumentiertes Design |

Die Funktransporte sind nur dort echt, wo Plattformcode existiert (C#/Windows, Kotlin/Android, HarmonyOS). Die acht Sprachbibliotheken liefern ansonsten einen **In-Process-Simulations**-Transport zum Testen — **WebRTC ist der erste echte Transport, der allen gemeinsam ist** (vollständig; sprachübergreifend Loopback-verifiziert).

Die Priorität richtet sich nach den Stromkosten: Das Funk-Mesh wird bevorzugt, dann WebRTC als direkter Internetpfad, mit dem HTTP/QUIC-Relay als letztem Mittel.

## Einsatzstufen

Aether funktioniert auf jeder Plattform, die Bluetooth oder WLAN unterstützt. Die Stufe hängt vom Zielbetriebssystem ab.

---

### Standardstufe — jede Plattform

Android · Windows · Linux · macOS · iOS

Aether läuft auf jedem Gerät mit Bluetooth- oder WLAN-Hardware. Wo ein Funk physisch fehlt, wird jeder blockierte Transport durch das Verfügbare angenähert. Diese Annäherungen sind jetzt **echter Code** (kompiliergeprüft; **laufzeit-unverifiziert**, ausstehend eines 2-Geräte-/Hardware-RF-Tests):

- **NearLink (Aether Teal)** — echte SSAP-over-BLE-GATT-Annäherung (Aether-SLE-UUID `61657468-6572-0003-…`) auf Android (`android/teal/AetherNetSleService`) und Windows (`WinNearLinkBleTransportService`); kompiliert + unit-getestet, laufzeit-unverifiziert. Das echte NearLink-Radio existiert nur auf HarmonyOS (`harmonyos/teal/`, On-Device-Verifizierung ausstehend).
- **LoRa (Aether Red)** — echter RYLR SX127x/SX126x-Seriell-Treiber (`LoRaSerialTransport` in **allen 8 Sprachen** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; jeder Port kompiliergeprüft, einschließlich Swift + C auf dem Mac-Build-Server; laufzeit-unverifiziert — benötigt ein physisches Modul). Die Meshtastic-over-BLE-Coded-PHY-Bridge (~1,3 km) bleibt ein dokumentiertes Design; echtes Langstrecken-LoRa benötigt einen LoRa-fähigen Knoten (Gateway, SBC oder robustes Handgerät mit einem LoRa-Modul).
- **NFC (Aether White)** — echt auf Android (HCE). Windows hat jetzt eine echte BLE-GATT + RSSI −40 dBm Näherungsannäherung (`WinNfcBleTransportService`, kompiliert net9/10; laufzeit-unverifiziert); ACR122U PC/SC, wenn ein Lesegerät vorhanden ist.

Was überall echt und identisch ist: **BLE, Wi-Fi Direct, das HTTP/QUIC-Relay und der WebRTC-P2P-Transport (Loopback-verifiziert in allen 8 Sprachen)**, plus Signal-Protokoll-Sicherheit (X3DH + Double Ratchet), AODV-Routing, DTN-Store-and-Forward, SOS-Broadcast, Sprache und Streaming.

**Ehrlicher Status:** BLE + Wi-Fi Direct + Relay sind produktionsecht; **WebRTC P2P ist echt und Loopback-verifiziert in allen 8 Sprachen** (zwei Peers tauschen Bytes über einen echten ICE-Datenkanal aus — Rust auf der `.201`-Linux-Box mit funktionierendem UDP-ICE bestätigt); die NearLink-/LoRa-/NFC-auf-Windows-Annäherungen sind jetzt echter Code, der kompiliert (LoRa kompiliergeprüft in allen 8, inkl. Swift + C auf dem Mac-Build-Server; NearLink-Android auch unit-getestet), aber **laufzeit-unverifiziert** — noch kein Hardware-/2-Geräte-RF-Test. Sie nehmen im Code am Mesh teil; setzen Sie diese drei nicht in der Erwartung feldbewährter RF ein.

---

### Native Stufe — CircleOS / OpenHarmony

CircleOS · HarmonyOS · jedes auf OpenHarmony basierende Betriebssystem

CircleOS basiert auf OpenHarmony, das NearLink (SLE)-Silizium und das `@kit.NearLinkKit`-SDK als erstklassige Betriebssystemfähigkeit liefert. Auf CircleOS- und HarmonyOS-Geräten mit NearLink-Hardware ist keine Annäherung erforderlich — `harmonyos/teal/` verwendet das echte SLE-Radio direkt:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Dies ist nicht nur eine bessere Version der Standardstufe. Auf der NearLink-Schicht ist es ein grundlegend anderes Netzwerk:

| Fähigkeit | Standardstufe (BLE-Annäherung) | Native Stufe (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink-Reichweite** | ~100 m (BLE) | **600 m** |
| **NearLink-Bandbreite** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink-Latenz** | ~10 ms (BLE) | **20 µs** |
| **NearLink-Stromverbrauch** | BLE-Basislinie | **60 % weniger als BLE 5.0** |
| **Gleichzeitige NearLink-Peers** | ~7 (BLE-Verbindungslimit) | **500+** |
| **NearLink-Quelle** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Echtes SLE-Radio (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Nativ | Nativ (identisch) |
| **Signal-Protokoll-Sicherheit** | Vollständig | Vollständig (identisch) |
| **Routing / DTN / SOS** | Vollständig | Vollständig (identisch) |
| **Aether Tag-Identität** | Unterstützt | Unterstützt (identisch) |

---

### Zwischen den Stufen wechseln

Es sind keine Code-Änderungen erforderlich. Die Stufe wird zur Laufzeit durch `IsAvailable` an jedem Transportdienst bestimmt:

1. Auf einem CircleOS- oder HarmonyOS-Gerät mit NearLink-Silizium gibt `IsAvailable` beim NearLink-Transport `true` zurück (hardware-geprüft über Berechtigungsprüfung + passiven Scan-Versuch).
2. `TransportManager` befördert NearLink automatisch in die Prioritätsposition — niedrigste Stromkosten, höchste Bandbreite.
3. App-Code, Paketformat, Routing-Algorithmus, Sicherheitsschicht und Aether-Tags sind über beide Stufen hinweg identisch.

Ein Knoten auf der Standardstufe und ein Knoten auf der nativen Stufe können frei kommunizieren — sie teilen dasselbe Leitungsformat, dieselben Signal-Protokoll-Sitzungen und dieselben Aether-Tags. Der Stufenunterschied betrifft nur das für NearLink-Pakete verwendete Radio, nicht das Protokoll darüber.

---

> **Intern werden diese Stufen als Asterix-Variante (Standard) und Obelix-Variante (nativ) bezeichnet.** Asterix arbeitet gut mit dem, was verfügbar ist. Obelix — auf CircleOS mit nativem NearLink — arbeitet mit dauerhaft erhöhten Fähigkeiten, so wie Obelix die Stärke des Zaubertranks trägt, ohne erneut trinken zu müssen.

---

## Implementierungen

Aether ist in 8 Sprachen entwickelt, damit es auf Telefonen, Laptops, Tablets und Mikrocontrollern läuft. Alle Implementierungen erzeugen leitungskompatible Pakete — eine vom Rust-Knoten verschlüsselte Nachricht kann vom Python-Knoten weitergeleitet und vom Swift-Knoten entschlüsselt werden.

| Sprache | Verzeichnis | Leitungsformat | Routing/DTN/SOS | X3DH | Double Ratchet | OPK-Pool | Sprache/Gruppe | Streaming/Video/Watch |
|---------|-------------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Alle 8 Sprachen erzeugen byteidentische Leitungspakete, verifiziert durch 17 kanonische Leitungsformat-Fixtures und 6 Signal-Testvektoren in CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Routing (AODV-artiges RREQ/RREP), DTN-Store-and-Forward, SOS-Broadcast, Sprache, Streaming und Sicherheits-Hardening-Dienste sind in jeder Sprache mit **~3.000 Tests** über alle 8 Implementierungen hinweg implementiert:

| Sprache | Tests | CI-Plattform |
|---------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Gesamt** | **~3.000** | |

Die sprachübergreifende Signal-Interoperabilität ist in `fixtures/signal/` mit gemeinsamen Testvektoren für X3DH (`x3dh_basic`), den symmetrischen Ratchet (`ratchet_step_basic`, `ratchet_step_three_iterations`), KDF_RK (`kdf_rk_basic`) und den vollständigen X3DH-Sitzungs-Roundtrip (`x3dh_session_msg1`, `x3dh_session_reply`) verankert. Jede Implementierung muss byteidentische Ausgaben gegenüber diesen Fixtures erzeugen. Alle 8 Sprachen liefern nun eine vollständige Signal-Sitzung (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Über Leitungsformat und Signal hinaus ist auch die **gesamte Wire-Service-Suite** — Präsenz, Heartbeat, Profil-Sync, Ephemere-ID-Ankündigung, Pre-Key-Austausch, Kanäle, Push-to-Talk, Bildschirmfreigabe, Anrufsteuerung, SOS-Bestätigung, Space-Breadcrumbs, Forge-Ankündigung, Vault-Shard-Anfrage und Bandbreitenmessung (siehe **Was Sie bekommen — jeder Dienst, in jeder Sprache**) — ebenso in allen 8 Sprachen implementiert und an eigene Fixtures gebunden (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/` und Geschwister). Kein Feature ist auf der Protokollschicht C#-exklusiv.

## Schnellstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Die Demo führt Sie durch 8 Schritte: Ed25519-Identitätsschlüssel für drei Knoten (Alice, Bob, Charlie) generieren, Signal-Protokoll-Sitzungen aufbauen, verschlüsselte Nachrichten senden, eine Nachricht durch Charlie weiterleiten (der sie nicht lesen kann), das binäre Leitungsformat zeigen und Forward Secrecy über 5 aufeinanderfolgende Nachrichten demonstrieren. Die Ausgabe ist farbkodiert und pausiert zwischen den Schritten.

**Eine Nachricht in C# senden:**

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

Die Demo generiert Identitätsschlüssel für zwei Knoten, tauscht Pre-Key-Bundles aus, baut verschlüsselte Sitzungen auf, sendet verschlüsselte Nachrichten in beide Richtungen, erstellt und signiert Mesh-Pakete, verifiziert Signaturen und serialisiert Pakete in das binäre Leitungsformat. Sie demonstriert auch die In-Process-Transport-Schicht.

**Eine Nachricht in Rust senden:**

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

Die Demo erstellt zwei Knoten in einem simulierten Netzwerk, generiert Ed25519-Schlüssel, baut Signal-Protokoll-Sitzungen auf, erstellt und signiert ein Paket, serialisiert es in ein C#-kompatibles Binärformat, verschlüsselt eine geheime Nachricht, entschlüsselt sie auf dem anderen Knoten, sendet sie über den Transport und verifiziert den Hin- und Rücklauf.

**Eine Nachricht in TypeScript senden:**

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

Die Demo führt 8 Demonstrationen durch: Ed25519-Schlüsselerzeugung und Manipulationserkennung, Knotenerstellung mit Fähigkeiten, Signal-Protokoll-X3DH-Schlüsselaustausch, AES-256-GCM-Ver- und Entschlüsselung, Paketserialisierung, Paketsignierung mit Replay-Erkennung, In-Process-Transport und ein vollständiger End-to-End-Fluss, der alle Schichten kombiniert.

**Eine Nachricht in Python senden:**

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

Die Demo führt 5 Demonstrationen durch: Paketserialisierungs-Hin-und-Rückläufe, Ed25519-Signierung mit Manipulationserkennung, Signal-Protokoll-Sitzungsaufbau mit verschlüsseltem Messaging in beide Richtungen, In-Process-Transport zwischen zwei Peers und Nonce-Deduplizierung für Replay-Schutz.

**Eine Nachricht in Go senden:**

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

Die Demo führt durch 11 Schritte: Schlüsselerzeugung, Knotenerstellung mit Fähigkeiten, Signal-Protokoll-Initialisierung, Pre-Key-Bundle-Austausch, Sitzungsaufbau, Paketerstellung und -signierung, Serialisierung, Deserialisierung mit Signaturverifizierung, Ende-zu-Ende-Verschlüsselung mit Key-Ratcheting, Replay-Angriffserkennung und In-Process-Transport.

**Eine Nachricht in Kotlin senden:**

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

Die Demo führt 5 Tests durch: Paketserialisierungs-Hin-und-Rückläufe, Ed25519-Signierung mit Manipulationsablehnung, Signal-Protokoll-Sitzungsaufbau mit AES-256-GCM-Verschlüsselung, In-Process-Transport-Nachrichtenübermittlung und einen vollständigen End-to-End-Fluss, bei dem Alice ein Paket signiert und Bob es nach dem Transport verifiziert.

**Eine Nachricht in Swift senden:**

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

Die Demo führt 7 Demonstrationen durch: Ed25519-Schlüsselerzeugung, Paketerstellung und -signierung, Serialisierung in das binäre Leitungsformat, Deserialisierung mit Integritätsprüfungen, AES-256-GCM-Ver- und Entschlüsselung, HMAC-SHA256-Nachrichtenauthentifizierung und HKDF-SHA256-Schlüsselableitung.

**Eine Nachricht in C senden:**

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

## Roadmap

Was bereits implementiert ist und was als Nächstes kommt.

**Erledigt (sprachübergreifend verifiziert, alle 8 Implementierungen):**
- Leitungsformat: byteidentisch über 8 Sprachen, verankert durch 17 kanonische Fixtures und sprachübergreifende Assertions in CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9-Job-Matrix (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, plus Fixture-Integritätsjob) in `.github/workflows/ci.yml`.
- Ed25519-Paketsignierung und -verifizierung
- AES-256-GCM-Verschlüsselung
- HKDF / HMAC-Schlüsselableitungsprimitive
- Paketserialisierung + Signierlayout (LE + 4-Byte-int32-Felder)
- In-Process-Transport-Simulator (für Entwicklung und Tests)
- AODV-inspirierter Routing-Dienst mit RREQ/RREP, signierten Routen-Antworten, Dedup, TTL-Weiterleitung
- DTN-Store-and-Forward-Dienst mit Custody-Transfer, Geohash-bewusster Replikation, 72h-TTL
- SOS-Broadcast-Dienst mit Flood, Dedup, Self-Origin-Guard, Rate-Limit (3/Std.)
- Erweiterungspunkte: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (Noop-Standardwerte)
- **~3.000 Tests** über alle 8 Sprachen (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — alle grün in CI
- ✅ **Echtes X3DH-Ephemeral-Key (8 Sprachen)** — 4 X25519-DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) mit HKDF-SHA256-Root-Ableitung. Verankert durch `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Double-Ratchet-Ausrichtung familienweit** — vollständiges Signal §5 mit HMAC-SHA256 + 0x01/0x02-Domain-Trennung im symmetrischen Ratchet, HKDF-SHA256 KDF_RK im DH-Ratchet-Schritt, DH-Rotation beim Empfang. Verifiziert durch `ratchet_step_basic`-, `ratchet_step_three_iterations`-, `kdf_rk_basic`-Fixtures.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 mit HEAD abgeglichen** — siehe `docs/PROTOCOL_SPEC.md`.

**Erledigt (alle 8 Sprachen):**
- ✅ **Sprachanrufe (1-zu-1)** — Signalisierungs-Zustandsmaschine (Offer/Answer/Hangup/Cancel/Timeout) + binärer Frame-Transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N Bytes). Routenaware Zustellung über `IRoutingService`.
- ✅ **Gruppensprache** — hostgesteuertes Mitgliedschaft (invite/kick/leave), Per-Frame-Schlüsselerzeugungsfeld, Unicast-Fan-out an alle aktuellen Mitglieder, host-kontrollierte Schlüsselrotation bei Mitgliedschaftsänderung.
- ✅ **Live-Streaming** — Herausgeber sendet `StreamAnnounce`; Abonnenten senden `StreamSubscribe`; binäre `StreamSegment`-Frames (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N Bytes) Unicast an jeden Abonnenten.
- ✅ **Videoanrufe (1-zu-1)** — Codec-/Auflösungs-/FPS-/Bitrate-Aushandlung in der Signalisierung, Keyframe-Anfrage- und Qualitätsänderungssignale, binäres `VideoFrame`-Format passend zum Sprach-Layout.
- ✅ **Watch Together** — Host sendet autoritative `WatchSync`-Befehle (play/pause/seek/speed); Follower wenden mit RTT-Kompensation an (`position = positionMs + elapsed × playbackSpeed`); Fire-and-Forget `WatchReaction`.
- ✅ **Einmal-Pre-Key (OPK)-Pool** — Standard 100, FIFO-Ausgabe, lazy Top-Up, lock-geschützter Verbrauch über alle 8 Sprachen. Schließt die Einzel-OPK-Nebenläufigkeitsgefahr.
- ✅ **C: vollständige Signal-Sitzung** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` in `c/src/signal_protocol.c`; 6 Zwei-Knoten-E2E-Tests in `c/tests/test_signal_session.c`. Alle 8 Sprachen haben nun ein vollständiges sitzungsfähiges Signal-Protokoll.

**Erledigt (alle 8 Sprachen — die vollständige Wire-Service-Suite):**
- ✅ **Jeder reservierte Pakettyp ist jetzt ein echter, byteidentischer Dienst in allen 8 Sprachen.** Präsenz-Beacon/-Abfrage (21/22), Heartbeat (10), Profil-Sync (23), Ephemere-Routing-ID-Ankündigung (56), Pre-Key-Austausch (25/26), Kanäle (7), Push-to-Talk (15), Bildschirmfreigabe (32), Anrufsteuerung (27), SOS-Bestätigung (6), Space-Breadcrumbs (40), Forge-Ankündigung (41), Vault-Shard-Anfrage (42) und Bandbreitenmessung / ABMF (53/54/55). Jeder ist ein schlanker Dienst (erzeugen + verarbeiten + Ereignis), den der Host mit seiner Signal-Sitzung und Routing-Tabelle verdrahtet; jeder ist an ein gemeinsames sprachübergreifendes Fixture gebunden (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) und wird durch sprachspezifische Unit-Tests geprüft, wobei Swift und C auf dem macOS-Build-Server verifiziert werden. Siehe **Was Sie bekommen — jeder Dienst, in jeder Sprache**.

**Erledigt (nur C#-Referenz):**
- ✅ **Demo-Schritt 9 — MessagingService + DTN-Fallback Ende-zu-Ende** — `samples/AetherNet.Demo.Console` führt durch echtes Signal-verschlüsseltes Messaging mit DTN-Store-and-Forward, wenn der Empfänger offline ist.
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security`-Bridge** — `SignalMessageEnvelopeCipher` macht die Messaging-Schicht standardmäßig Ende-zu-Ende-verschlüsselt; Nachrichten ohne Signal-Sitzung werden in die Warteschlange gestellt, nie unsicher gesendet.
- ✅ **Adaptives Bitrate-Streaming** — `AdaptiveBitrateController` mit spezifikationsvorgeschriebenen Bitrate-Leitern für Profil A (Echtzeit), B (Live-Broadcast) und C (VOD). Der Herausgeber wählt die höchste nachhaltige Stufe (20 % Headroom) und sendet `StreamAbandon` (`PacketType.StreamAbandon`) anstelle eines Segments, wenn er unter dem Boden liegt. `IStreamingService` stellt `UpdateBandwidthEstimate` und `GetCurrentBitrateRung` bereit.
- ✅ **Watch Together: BitTorrent-Ingest + ChipIn-Gruppenfinanzierung** — `TorrentInfo`/`TorrentFile`-Modelle; `WatchTogetherService` behandelt `PacketType.TorrentMetadata` und löst `TorrentReceived` aus. `ChipInPool`/`ChipInContribution`-Zustandsmaschine (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync`/`ContributeAsync`/`GetChipIn` auf `IWatchTogetherService`.
- ✅ **Gruppenvideoaufrufe mit automatischem SFU-Relay** — `GroupVideoService`/`IGroupVideoService`. FullMesh-Topologie für ≤ 3 Teilnehmer; automatischer Wechsel zu SFU bei `SfuThresholdParticipants` (4) mit Relay-Neuzuweisung über `GroupVideoSignaling(SfuAssigned)`. Fan-out im FullMesh, nur Relay-Senden im SFU-Modus. Signalisierungs-Pakettyp `GroupVideoSignaling = 35`.
- ✅ **BLE-GATT-Transport-Simulation** — `SimulatedBleGattTransportService` (`IBleTransportService`). GATT-MTU-Framing über `BleGattFramer` (1024 B/Frame, `[2B count][2B index][payload]`), In-Process-statische Peer-Registry, Werbungsbroadcast. Alle `BleMaxPayloadBytes`-Einschränkungen durchgesetzt.
- ✅ **Wi-Fi-Direct-Transport-Simulation** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Expliziter `ConnectAsync`/`DisconnectAsync`-Lebenszyklus, direkte Großnutzlast-Zustellung (kein Framing), bidirektionale `PeerConnected`/`PeerDisconnected`-Ereignisse.
- ✅ **NearLink-Transport-Simulation** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096-B-Frame-MTU, 500-Peer-Registry, `ConnectedPeerCount`, `IsAvailable` zur Laufzeit einstellbar.
- ✅ **RF-Bring-up-Simulationstests** — Zwei-Knoten-Interop-Tests (`SimulatedTransportTests`): BLE + NearLink `MeshPacket`-Hin-und-Rücklauf, WiFi-Direct-64-KB-Nutzlastübertragung. Software-Schicht vollständig verifiziert; physische Gerätesitzung für Hardware-Validierung erforderlich.

**Erledigt (C#-Transport-Schicht — alle fail-fast):**
- ✅ **BLE-GATT-Echtzeit-Transport** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT-Server). Vollständiger RF-Bring-up-Test in `samples/AetherNet.BleRfTest/`.
- ✅ **Wi-Fi-Direct-Echtzeit-Transport** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket Port 8888) + `android/green/` (`WifiP2pManager`). RF-Test in `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **HTTP-Relay-Transport (Aether Purple)** — `HttpRelayTransportService` mit 10-sekündigem Long-Poll, `PowerCostRelative = 100`, immer letztes Mittel. Relay-Server in `samples/AetherNet.RelayServer/` (ASP.NET Core Minimal-API, Port 5200). RF-Test in `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implementiert `HostApduService` mit AID `F061657468657200`. `WinNfcStubTransportService` dokumentiert zwei Windows-Annäherungspfade: (1) NDEF-over-BLE-GATT mit RSSI-Gate ≥ −40 dBm (simuliert Tap-to-Connect ohne NFC-Silizium, `IsAvailable = Bluetooth vorhanden`); (2) ACR122U-USB-Lesegerät über `Windows.Devices.SmartCards` PC/SC (`IsAvailable = kontaktloses Lesegerät aufgelistet`). Upgrade-Pfad: `ITransportService` implementieren, wenn Microsoft eine erstklassige P2P-NFC-API liefert.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — vollständige HarmonyOS 5.0.1 (API 13) ArkTS-Implementierung mit `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` zur Laufzeit geprüft. `WinNearLinkStubTransportService` + `android/teal/` dokumentieren die SSAP-over-BLE-Annäherung: BLE GATT mit Aether-SLE-Service-UUID `61657468-6572-0003-0000-000000000000` — API-analog zu SSAP, nicht leitungskompatibel mit echter NearLink-Hardware. Upgrade-Pfad: BLE-GATT-Aufrufe durch `ssapc_*`/`ssaps_*`-SDK-Aufrufe ersetzen; UUIDs und `TransportManager`-Slot unverändert.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` dokumentieren die Meshtastic-over-BLE-LR-Annäherung: vollständiges Meshtastic-Leitungsformat (16-Byte-Header + AES-256-CTR-Protobuf) über BLE 5.0 Coded PHY S=8 (~1,3 km outdoor), mit verwalteter Flood-Routing- und RSSI-gewichteter Contention-Window. Bridge-Node-Verbund mit echter LoRa-Hardware funktioniert automatisch (gleicher Meshtastic-Paketformat, keine Übersetzung). Upgrade-Pfad: BLE-LR-Radio durch SX1276/SX1278-AT-Befehl oder SPI-Treiber ersetzen; Paketformat und Routing unverändert.

**Offen — verfolgt in `OPEN_ISSUES.md`:**
- RF-Bring-up auf echter Hardware: Ende-zu-Ende-Zwei-Knoten-Interop-Test auf physischen BLE-/Wi-Fi-Direct-Geräten (Simulationstests bestehen; Hardware-Lab-Sitzung erforderlich)
- NearLink: `harmonyos/teal/` vollständig; erfordert Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 Hardware (NearLink-Silizium nicht auf Nicht-Huawei-Geräten vorhanden). Windows + Android fallen automatisch auf SSAP-over-BLE-Annäherung zurück.
- LoRa / CircleLink: Funkmodul für echte LoRa-Reichweite erforderlich. Ohne eines wird das Meshtastic-Leitungsformat über BLE LR (~1,3 km) übertragen und Bridge-Node-Verbund mit echter LoRa-Hardware ist verfügbar.
- ✅ **(GELÖST v1.2.0)** Consumer-Protokolloberfläche (Wave 16/17) — `IDtnService.BundleReceived`-Ereignis für eingehende Bundles ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), Anwendungsschicht-Namens-/Discovery-Verzeichnis ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), Autoren-Tipping-Schnittstelle ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Alle 3 additiv über 8 Sprachen mit byte-gleichen sprachübergreifenden Fixtures ausgeliefert. Siehe CHANGELOG.

**Noch nicht für externe Beiträge offen:**
- Das Protokoll befindet sich noch in aktiver Entwicklung. Externe Beiträge werden derzeit nicht akzeptiert.
- NearLink-Transport-Implementierung, Android/iOS-Integrationsbeispiele, zusätzliche Transport-Backends, Performance-Benchmarks und Protokoll-Fuzzing werden intern verfolgt und werden geöffnet, wenn das Projekt einen stabilen öffentlichen Beitragspunkt erreicht.

## Projektstruktur

```
aether-protocol/
  src/
    AetherNet.Core/          Protokollmodelle, Konstanten, Paketserialisierung
    AetherNet.Security/      Signal-Protokoll, Ed25519, Paketsignierung
    AetherNet.Transport/     Transport-Abstraktionen, NearLink, In-Process-Simulator
    AetherNet.Messaging/     Nachrichtenbehandlung und -weiterleitung
    AetherNet.Storage/       DTN-Store-and-Forward-Persistenz
    AetherNet.Streaming/     Adaptives Bitrate-Streaming, Videomodelle und -schnittstellen
    AetherNet.Voice/         Sprachanrufe und Gruppensprache
    AetherNet.Content/       Inhaltsverifizierung und segmentierter Transfer
  samples/
    AetherNet.Demo.Console/  Interaktive Demo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust-Implementierung
  typescript/             TypeScript-Implementierung
  python/                 Python-Implementierung
  go/                     Go-Implementierung
  kotlin/                 Kotlin/JVM-Implementierung
  swift/                  Swift-Implementierung
  c/                      C-Implementierung
  docs/
    PROTOCOL_SPEC.md      RFC-artige Protokollspezifikation
```

## Einen neuen Transport hinzufügen

`ITransportService` implementieren:

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

Im DI registrieren; `TransportManager` schließt ihn automatisch in die Transportauswahl ein, sortiert nach Stromkosten.

## Vergleich

| Protokoll | Einschränkung | Aether-Vorteil |
|-----------|--------------|----------------|
| **Briar** | Nur Android, Tor-abhängig | Plattformübergreifend, reines Mesh |
| **Meshtastic** | Nur LoRa (max. 30 kbps) | Multi-Transport (BLE + WiFi + NearLink), sprach- und streamingfähig |
| **Reticulum** | Python, kleine Community | 8 Sprachen, leitungskompatibel über alle |
| **libp2p** | Setzt Internet-Backbone voraus | Offline-first, funktioniert ohne Infrastruktur |
| **Yggdrasil** | Overlay-Netzwerk, benötigt Internet | Physical-Layer-Mesh, funktioniert ohne Internet |
| **Signal** | Kein Mesh, benötigt Internet | Funktioniert offline, P2P, Mesh-Relay, gleiche E2E-Verschlüsselung |

## Häufig gestellte Fragen

**Funktioniert AetherNet ohne Internet?**
Ja — es ist Offline-First. Geräte kommunizieren direkt über Bluetooth, Wi-Fi Direct, NearLink oder LoRa, und Relay-Nachrichten springen Hop für Hop über andere Geräte, ohne dass eine Internetverbindung, ein Mobilfunkmast oder ein Server erforderlich ist. Wenn keine Live-Route besteht, werden Nachrichten (verzögerungstolerantes Store-and-Forward) bis zu 72 Stunden aufbewahrt, bis sich eine öffnet.

**Ist es Ende-zu-Ende-verschlüsselt?**
Ja. AetherNet verwendet das Signal-Protokoll (X3DH-Schlüsselvereinbarung plus den Double Ratchet über X25519) für die Ende-zu-Ende-Verschlüsselung, AES-256-GCM für Nachrichten-Nutzlasten und Ed25519-Signaturen auf jedem Paket. Geräte, die eine Nachricht weiterleiten, können sie nicht lesen.

**Welche Transporte verwendet es?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), ein LoRa/CircleLink-Seriell-Funkgerät, ein HTTP/QUIC-Relay und WebRTC für direktes Internet-Peer-to-Peer. Das Protokoll wählt automatisch den verfügbaren Transport mit dem geringsten Stromverbrauch je Paket und fällt auf den nächsten zurück.

**In welchen Programmiersprachen ist es verfügbar?**
Acht — C#, Rust, TypeScript, Python, Go, Kotlin, Swift und C. Jede Implementierung erzeugt byteidentische Leitungspakete, erzwungen durch ein gemeinsames sprachübergreifendes Fixture-Korpus in CI, sodass ein von einer Sprache gebautes Paket von jeder anderen unverändert dekodiert wird.

**Wie unterscheidet es sich von Meshtastic, Briar oder Bridgefy?**
Meshtastic ist nur LoRa; AetherNet ist Multi-Transport (Bluetooth + Wi-Fi + NearLink + LoRa) und transportiert neben Nachrichten auch Sprache, Video und Streaming. Briar ist nur für Android und routet über Tor; AetherNet ist plattformübergreifend und reines Mesh. Anders als geschlossene SDKs ist AetherNet MIT-lizenziert und offen in acht Sprachen implementiert. Die Vergleichstabelle oben enthält die Details.

**Ist es produktionsreif?**
Die Protokollschicht — Leitungsformat, Signal-Sicherheit, Routing, DTN-Store-and-Forward und die vollständige Dienst-Suite — ist über alle acht Sprachen hinweg implementiert und getestet. Funktransporte sind dort echt, wo Plattformcode existiert (Bluetooth und Wi-Fi auf Windows und Android, WebRTC überall) und andernorts feldunverifiziert, ausstehend eines Hardware-Bring-ups, das ehrlich in `OPEN_ISSUES.md` verfolgt wird. Lesen Sie die Statushinweise in jedem Abschnitt, bevor Sie einsetzen.

**Unter welcher Lizenz steht es?**
MIT — kostenlos für kommerzielle und Open-Source-Nutzung. Siehe [LICENSE](LICENSE).

**Wer entwickelt AetherNet?**
Es wird als das offene Protokoll hinter dem Mesh-Ökosystem von The Geek Network entwickelt, gebaut in Südafrika für Kommunikation, die mit oder ohne mobile Daten funktioniert.

## Erweiterungspunkte

Das Protokoll funktioniert eigenständig. Diese Schnittstellen ermöglichen es Ihnen, Ihr eigenes Backend einzusetzen, wenn Sie eines möchten:

- `IAetherNetIncentiveProvider` — Knoten belohnen, die Traffic weiterleiten (Noop-Standard: altruistische Weiterleitung)
- `IAetherNetBackendClient` — Mit einem Server synchronisieren, wenn Internet verfügbar ist (Noop-Standard: vollständig offline)
- `IAetherNetFeatureFlagProvider` — Protokollfunktionen zur Laufzeit umschalten (Noop-Standard: alles aktiviert)

Alle drei werden mit Noop-Implementierungen geliefert. Entfernen Sie sie, und nichts bricht.

## Mitwirken

Externe Beiträge sind noch nicht offen. Das Projekt befindet sich noch in aktiver Entwicklung. Schauen Sie wieder vorbei, wenn wir ein öffentliches Beitragsfenster ankündigen.

## Sicherheit

Siehe [SECURITY.md](SECURITY.md) für die Richtlinie zur verantwortungsvollen Offenlegung.

## Lizenz

MIT-Lizenz. Siehe [LICENSE](LICENSE).

## Übersetzungen

Diese README wird auf Englisch gepflegt und in 10 weitere Sprachen unter [`docs/i18n/`](docs/i18n/) übersetzt: Français, Español, العربية, 中文简体, 日本語, Deutsch, Português (BR), Русский, فارسی und 한국어. Die **englische Version ist die maßgebliche Quelle** — wo eine Übersetzung und der englische Text nicht übereinstimmen, ist der englische Text maßgeblich, und Übersetzungen können ihm um ein oder zwei Releases hinterherhinken. Das Protokoll, der Code, die Fixtures und das beschriebene Verhalten sind identisch, egal in welcher Sprache Sie lesen.
