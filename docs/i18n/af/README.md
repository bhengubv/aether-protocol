# AetherNet — vanlyn-eerste mesh-netwerkprotokol

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet is 'n oopbron, MIT-gelisensieerde mesh-netwerkprotokol** vir die stuur van boodskappe, lêers, stem en video na mense naby jou — met **geen internet, geen bedieners, en geen registrasie nie**. Toestelle verbind direk oor Bluetooth, Wi-Fi Direct, NearLink en LoRa; wanneer die ontvanger buite bereik is, hop boodskappe deur ander toestelle en wag tot 72 uur vir 'n roete. Dit stuur **greep-vir-greep identiese implementasies in agt programmeertale** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift en C.

Deel lêers, boodskappe en strome met mense naby jou. Geen WiFi nie. Geen mobiele data nie. Geen registrasie nie. Soos AirDrop, behalwe dat dit met almal werk, op elke platform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Een protokol, agt tale, identies op die draad.** Aether is geïmplementeer in **C#, Rust, TypeScript, Python, Go, Kotlin, Swift en C** — en elke pakkie is greep-vir-greep identies oor almal daarvan, afgedwing deur 'n gedeelde kruistaal-fixture-korpus in CI. Bou jou node in enige van die agt; dit werk saam met al die ander. Hierdie README is ook beskikbaar in 11 menslike tale (skakels hierbo).

## Wat kan jy daarmee doen?

**Deel klasnotas sonder om data te spandeer.**

Jy is in 'n studiegroep. Iemand het ou vraestelle op hul foon. Aether stuur hulle direk na jou toestel oor Bluetooth — geen hotspot, geen WhatsApp-groep, geen lêergrootte-limiet nie. As iemand in die groep buite bereik is, hop die lêer deur ander toestelle totdat dit hulle bereik. Boodskappe wag tot 72 uur vir 'n roete indien nodig.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Vind uit wat rondom jou gebeur.**

Jy is by 'n kampusgeleentheid of 'n fees. Aether ontdek ander toestelle naby jou oor Bluetooth en WiFi Direct — geen app-voer, geen algoritme nie. Jy sien wat werklik rondom jou is, nie wat bevorder word nie.

**Stuur 'n SOS wanneer daar geen sein is nie.**

Jou foon het geen ontvangs nie. Aether saai 'n noodboodskap uit na elke toestel binne bereik, en daardie toestelle gee dit aan. Geen seltoring nodig nie.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Skep private groepkanale.**

'n Kanaal vir jou koshuisvloer, jou vereniging, jou projekspan. Slegs geverifieerde lede kan boodskappe lees of stuur. Geen bediener stoor die gesprek nie.

**Verkoop dinge aan mense naby jou.**

Lys 'n handboek te koop. Mense wat binne bereik van die mesh stap, sien dit. Geen markplek-rekening, geen lyskostes nie — net nabyheid.

**Kyk saam 'n fliek, oor die mesh.**

Jou groep het 'n flieksaand. Iemand het die lêer. Aether sinkroniseer terugspeel oor elke toestel — speel, pouseer, soek — alles in pas. As net sommige mense die lêer het, versprei die mesh dit intyds as 'n P2P-stroom. Almal dra by via SDPKT om dit te koop as niemand dit het nie.

## Hoe dit werk

Toestelle praat direk met mekaar met Bluetooth, WiFi Direct of NearLink. Geen internetverbinding, geen bediener, geen sentrale infrastruktuur nie.

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

Wanneer 'n boodskap nie sy bestemming direk kan bereik nie, hop dit deur ander toestelle. Daardie aanstuur-toestelle kan nie lees wat hulle dra nie — elke boodskap is geënkripteer met AES-256-GCM. Elke pakkie is onderteken met Ed25519-identiteitsleutels, en vervalste pakkies word deur die netwerk laat val.

> **Sekuriteitsvolwassenheid-nota (lees voordat jy uitstuur):** Werklike X3DH (4 X25519 DH's), die volledige Signal Double Ratchet (DH-rotasiestap by ontvangs, KDF_RK, 0x01/0x02-kettingratel), en die eenmalige voorsleutel-poel (verstek 100 OPKs, FIFO, slot-beskerm) is geïmplementeer in **al 8 tale** en vasgepen aan 'n gedeelde kruistaal-fixture-korpus onder `fixtures/signal/`. Die enigste oorblywende oop item is fisiese RF-inbedryfstelling op werklike BLE-hardeware (nagespoor in `OPEN_ISSUES.md`).

Geen rekeninge, geen telefoonnommers, geen e-posse nie. Jy genereer 'n sleutelpaar en jy is op die netwerk.

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

**Roetering** — AODV met ondertekende roete-antwoorde. Elke roete-antwoord word onderteken deur die bestemming se Ed25519-sleutel, sodat geen toestel kan voorgee dat dit 'n bestemming is wat dit nie is nie.

**Stoor-en-stuur** — Wanneer daar geen lewende roete is nie, word pakkies vir tot 72 uur gehou totdat 'n pad oopgaan.

**Transportkeuse** — Die protokol kies die regte transport per pakkie. Klein beheerboodskappe gaan oor BLE. Grootmaat-oordragte gebruik WiFi Direct. NearLink wanneer beskikbaar.

**Stem, video en stroom** — Video-oproepe met kodek-onderhandeling (H.264/H.265/VP8), transport-bewuste gehaltekeuse, groepvideo met outomatiese SFU-aanstuur, gesinkroniseerde saam-kyk met RTT-kompensasie, en aanpasbare bittempo-stroom.

**Herspeel-beskerming** — Nonce-ontdubbeling met 'n 5-minuut-tydstempel-varsheidsvenster.

## Wat jy kry — elke diens, in elke taal

Aether is nie net 'n transport nie. Elke pakkietipe wat deur die protokol gereserveer is, is nou 'n **werklike, werkende diens in al 8 tale**, en elkeen serialiseer na **greep-identiese draadpakkies** — 'n pakkie wat deur die Go-node gebou is, word ongewysig gedekodeer deur die Swift-, Rust-, C-, Python-, TypeScript-, Kotlin- of C#-node. Elke diens is vasgepen aan 'n gedeelde kruistaal-fixture onder `fixtures/<service>/` en getoets deur per-taal-eenheidstoetse, met Swift en C wat addisioneel op die macOS-bouserver geverifieer word.

| Vermoë | Wat dit doen | Pakkietipe(s) | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Teenwoordigheidsbaken & -navraag** | Kondig aan "Ek is hier" en vra "wie is rondom?" — oor 'n **roterende, sleutel-afgeleide efemere ID** (nie jou werklike identiteit nie) plus 'n growwe geohash | 21, 22 | `fixtures/presence/` | ✅ |
| **Hartklop** | Liggewig-lewendigheid-onderhoud tussen gekoppelde eweknieë | 10 | `fixtures/heartbeat/` | ✅ |
| **Profielsinkronisasie** | Ruil 'n ondertekende profielkaart met 'n eweknie oor die mesh | 23 | `fixtures/profiles/` | ✅ |
| **Efemere-ID-aankondiging** | Vertel privaat vir 'n vriend jou huidige roterende roeterings-ID sodat hulle jou steeds kan bereik nadat dit roteer | 56 | `fixtures/erid/` | ✅ |
| **Voorsleutel-uitruil** | Versoek en lewer 'n Signal-voorsleutelbundel oor die mesh, om 'n end-tot-end-sessie te begin met iemand wat jy nog nooit ontmoet het nie | 25, 26 | `fixtures/prekey/` | ✅ |
| **Kanale** | Ondertekende boodskappe na 'n private, slegs-lede-groepkanaal | 7 | `fixtures/channels/` | ✅ |
| **Druk-om-te-praat** | Walkie-talkie-stemrame (ondeursigtige geënkodeerde klanklading) | 15 | `fixtures/media/` | ✅ |
| **Skermdeling** | Skermdeling-videorame (ondeursigtige geënkodeerde videolading) | 32 | `fixtures/media/` | ✅ |
| **Oproepbeheer** | Lui / aanvaar / weier / ophang-seinontwerp vir stem- en video-oproepe | 27 | `fixtures/videocall/` | ✅ |
| **SOS-erkenning** | Bevestig aan die sender dat hul nooduitsaai ontvang is | 6 | `fixtures/sos/` | ✅ |
| **Ruimte-broodkrummels** | Ligging-gemerkte ontdekkingskrummels vir die "wat is rondom my"-laag | 40 | `fixtures/space/` | ✅ |
| **Smee-aankondiging** | Adverteer 'n afgeleide/gesmede inhoud-artefak na die mesh | 41 | `fixtures/forge/` | ✅ |
| **Kluis-skerf-versoek** | Haal 'n uitwissings-gekodeerde bergingskerf op (enige K van N skerwe herbou die lêer) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandwydte-meting** | Peil / erken / skinder skakel-deurset sodat die mesh oor die vetste pyp roeteer (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Hierdie sit bo-op die reeds-voltooide **boodskappe, 1-tot-1- en groepstem, video-oproepe, lewendige stroom, saam-kyk, AODV-roetering, DTN-stoor-en-stuur, en SOS-vloed** dienste — ook geïmplementeer in al 8 tale.

> **Wat "gebou" hier presies beteken.** Elke diens produseer en hanteer sy draadpakkie, wek die regte gebeurtenisse op, en is vasgepen aan 'n greepvlak-fixture wat die hele taalfamilie moet ewenaar. Jou toepassing bedraad die diens aan sy Signal-sessie, roeteringstabel en plaaslike toestand. Dit is die protokollaag — bewys in kode, toetse en kruistaal-greep-fixtures — op dieselfde eerlike RF-grondslag as alles anders: enige pad wat uiteindelik op 'n radio ry, is veldonbevestig totdat die hardeware-inbedryfstelling wat in `OPEN_ISSUES.md` nagespoor word, plaasvind.

## Transporte

Elke transport het 'n kleurnaam wat regdeur die kodebasis gebruik word. `IsAvailable` beheer hardeware-geblokkeerde paaie — die `TransportManager` slaan hulle oor en val terug na die volgende beskikbare transport.

**Statussleutel:** ✅ werklik, gebou & geverifieer · ⏳ werklik, verifikasie aan die gang · ⚠️ werklik op sommige platforms, stomp op ander · ❌ stomp (nog geen transportkode nie).

| Kleur | Naam | Bereik | Bandwydte | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Werklik — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Werklik — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC-aanstuur | Onbeperk | ~10 Mbps | ✅ Werklik — Windows; aanstuurbediener in `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Internet-datakanaal | Onbeperk | ~100 Mbps | ✅ Werklik in al 8 tale — **teruglus-geverifieer in al 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust het elk twee eweknieë wat grepe oor 'n werklike ICE-datakanaal uitruil) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Werklik op Android (`android/white/`); Windows = werklike BLE-GATT + RSSI −40 dBm-nabyheidsbenadering (`WinNfcBleTransportService`, kompileer net9/10, looptyd-onbevestig) — `Windows.Networking.Proximity` verwyder in Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Werklik op HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — hangend op-toestel-verifikasie); Android + Windows = werklike SSAP-oor-BLE-benadering (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; kompilasie + eenheidstoets geverifieer, looptyd-onbevestig) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Werklike RYLR SX127x/SX126x-reeksdrywer (`LoRaSerialTransport` in C#/Go/Rust/C; kompileer, looptyd-onbevestig — benodig 'n fisiese module); BLE Coded-PHY-brug steeds 'n gedokumenteerde ontwerp |

Die radiotransporte is slegs werklik waar platformkode bestaan (C#/Windows, Kotlin/Android, HarmonyOS). Die agt taalbiblioteke stuur andersins 'n **in-proses-simulasie**-transport vir toetsing — **WebRTC is die eerste werklike transport wat aan almal gemeenskaplik is** (voltooi; teruglus-geverifieer oor die tale).

Prioriteit is volgens kragkoste: die radio-mesh word verkies, dan WebRTC as 'n direkte internetpad, met die HTTP/QUIC-aanstuur as laaste uitweg.

## Ontplooiingsvlakke

Aether werk op enige platform wat Bluetooth of Wi-Fi ondersteun. Die vlak waarop jy is, hang af van die OS wat jy teiken.

---

### Standaardvlak — enige platform

Android · Windows · Linux · macOS · iOS

Aether loop op enige toestel met Bluetooth- of Wi-Fi-hardeware. Waar 'n radio fisies afwesig is, word elke geblokkeerde transport benader oor wat beskikbaar is. Hierdie benaderings is nou **werklike kode** (kompilasie-geverifieer; **looptyd-onbevestig** hangend 'n 2-toestel-/hardeware-RF-toets):

- **NearLink (Aether Teal)** — werklike SSAP-oor-BLE-GATT-benadering (Aether SLE UUID `61657468-6572-0003-…`) op Android (`android/teal/AetherNetSleService`) en Windows (`WinNearLinkBleTransportService`); kompilasie + eenheidstoets geverifieer, looptyd-onbevestig. Die werklike NearLink-radio bestaan slegs op HarmonyOS (`harmonyos/teal/`, hangend op-toestel-verifikasie).
- **LoRa (Aether Red)** — werklike RYLR SX127x/SX126x-reeksdrywer (`LoRaSerialTransport` in **al 8 tale** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; elke poort kompilasie-geverifieer, insluitend Swift + C op die Mac-bouserver; looptyd-onbevestig — benodig 'n fisiese module). Die Meshtastic-oor-BLE-Coded-PHY-brug (~1.3 km) bly 'n gedokumenteerde ontwerp; werklike langafstand-LoRa benodig 'n LoRa-vermoënde node (poort, SBC, of robuuste handtoestel met 'n LoRa-module).
- **NFC (Aether White)** — werklik op Android (HCE). Windows het nou 'n werklike BLE-GATT + RSSI −40 dBm-nabyheidsbenadering (`WinNfcBleTransportService`, kompileer net9/10; looptyd-onbevestig); ACR122U PC/SC wanneer 'n leser teenwoordig is.

Wat werklik en identies oral is: **BLE, Wi-Fi Direct, die HTTP/QUIC-aanstuur, en die WebRTC P2P-transport (teruglus-geverifieer in al 8 tale)**, plus Signal Protocol-sekuriteit (X3DH + Double Ratchet), AODV-roetering, DTN-stoor-en-stuur, SOS-uitsaai, stem en stroom.

**Eerlike status:** BLE + Wi-Fi Direct + aanstuur is produksie-werklik; **WebRTC P2P is werklik en teruglus-geverifieer in al 8 tale** (twee eweknieë ruil grepe oor 'n werklike ICE-datakanaal uit — Rust bevestig op die `.201` Linux-boks met werkende UDP ICE); die NearLink- / LoRa- / NFC-op-Windows-benaderings is nou werklike kode wat kompileer (LoRa kompilasie-geverifieer in al 8, insl. Swift + C op die Mac-bouserver; NearLink-Android ook eenheid-getoets) maar is **looptyd-onbevestig** — nog geen hardeware-/2-toestel-RF-toets nie. Hulle neem in kode aan die mesh deel; moenie daardie drie ontplooi met die verwagting van veld-bewese RF nie.

---

### Inheemse vlak — CircleOS / OpenHarmony

CircleOS · HarmonyOS · enige OpenHarmony-gebaseerde OS

CircleOS is gebou op OpenHarmony, wat NearLink (SLE)-silikon en die `@kit.NearLinkKit`-SDK as 'n eersteklas-OS-vermoë stuur. Op CircleOS- en HarmonyOS-toestelle met NearLink-hardeware is geen benadering nodig nie — `harmonyos/teal/` gebruik die werklike SLE-radio direk:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Dit is nie net 'n beter weergawe van die standaardvlak nie. By die NearLink-laag is dit 'n kategories ander netwerk:

| Vermoë | Standaardvlak (BLE-benadering) | Inheemse vlak (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink-bereik** | ~100 m (BLE) | **600 m** |
| **NearLink-bandwydte** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink-latensie** | ~10 ms (BLE) | **20 µs** |
| **NearLink-krag** | BLE-basislyn | **60% minder as BLE 5.0** |
| **Gelyktydige NearLink-eweknieë** | ~7 (BLE-verbindingslimiet) | **500+** |
| **NearLink-bron** | SSAP-oor-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Werklike SLE-radio (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP-aanstuur** | Inheems | Inheems (identies) |
| **Signal Protocol-sekuriteit** | Volledig | Volledig (identies) |
| **Roetering / DTN / SOS** | Volledig | Volledig (identies) |
| **Aether Tag-identiteit** | Ondersteun | Ondersteun (identies) |

---

### Beweeg tussen vlakke

Geen kodeveranderinge word vereis nie. Die vlak word tydens looptyd bepaal deur `IsAvailable` op elke transportdiens:

1. Op 'n CircleOS- of HarmonyOS-toestel met NearLink-silikon gee `IsAvailable` op die NearLink-transport `true` terug (hardeware-gepeil via toestemmingskontrole + passiewe skandeerpoging).
2. `TransportManager` bevorder NearLink outomaties na prioriteitsposisie — laagste kragkoste, hoogste bandwydte.
3. App-kode, pakkieformaat, roeteringsalgoritme, sekuriteitslaag en Aether Tags is identies oor beide vlakke.

'n Node op die standaardvlak en 'n node op die inheemse vlak kan vrylik kommunikeer — hulle deel dieselfde draadformaat, dieselfde Signal Protocol-sessies, en dieselfde Aether Tags. Die vlakverskil beïnvloed slegs die radio wat vir NearLink-pakkies gebruik word, nie die protokol daarbo nie.

---

> **Intern word hierdie vlakke na verwys as die Asterix-variant (standaard) en die Obelix-variant (inheems).** Asterix werk goed met wat beskikbaar is. Obelix — wat op CircleOS met inheemse NearLink loop — werk teen permanent verhoogde vermoë, op die manier wat Obelix die towerdrankie se krag dra sonder om weer te drink.

---

## Implementasies

Aether is gebou in 8 tale sodat dit op fone, skootrekenaars, tablette en mikrobeheerders loop. Alle implementasies produseer draad-versoenbare pakkies — 'n boodskap wat deur die Rust-node geënkripteer is, kan deur die Python-node aangestuur word en deur die Swift-node gedekripteer word.

| Taal | Gids | Draadformaat | Roetering/DTN/SOS | X3DH | Double Ratchet | OPK-poel | Stem/Groep | Stroom/Video/Kyk |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Al 8 tale produseer greep-identiese draadpakkies, geverifieer deur 14 kanonieke draadformaat-fixtures en 4 Signal-toetsvektore wat in CI loop (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Roetering (AODV-styl RREQ/RREP), DTN-stoor-en-stuur, SOS-uitsaai, stem, stroom, en sekuriteitsverhardingsdienste is in elke taal geïmplementeer met **~3,000 toetse** oor al 8 implementasies:

| Taal | Toetse | CI-platform |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Totaal** | **~3,000** | |

Kruistaal-Signal-interop is veranker aan `fixtures/signal/` met gedeelde toetsvektore vir X3DH (`x3dh_basic`), die simmetriese ratel (`ratchet_step_basic`, `ratchet_step_three_iterations`), en KDF_RK (`kdf_rk_basic`). Elke implementasie moet greep-identiese uitsette teen daardie fixtures produseer. Al 8 tale stuur nou 'n volledige Signal-sessie (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Verby draadformaat en Signal is die **hele draaddiens-suite** — teenwoordigheid, hartklop, profielsinkronisasie, efemere-ID-aankondiging, voorsleutel-uitruil, kanale, druk-om-te-praat, skermdeling, oproepbeheer, SOS-erkenning, ruimte-broodkrummels, smee-aankondiging, kluis-skerf-versoek, en bandwydte-meting (sien **Wat jy kry**) — eweneens geïmplementeer in al 8 tale en vasgepen aan sy eie fixtures (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, en broers en susters). Geen kenmerk is C#-alleen by die protokollaag nie.

## Vinnige begin

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Die demo lei jou deur 8 stappe: genereer Ed25519-identiteitsleutels vir drie nodes (Alice, Bob, Charlie), vestig Signal Protocol-sessies, stuur geënkripteerde boodskappe, stuur 'n boodskap deur Charlie aan (wat dit nie kan lees nie), wys die binêre draadformaat, en demonstreer voorwaartse geheimhouding oor 5 opeenvolgende boodskappe. Uitvoer is kleur-gekodeer en pouseer tussen stappe.

**Stuur 'n boodskap in C#:**

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

Die demo genereer identiteitsleutels vir twee nodes, ruil voorsleutelbundels uit, vestig geënkripteerde sessies, stuur geënkripteerde boodskappe in beide rigtings, skep en onderteken mesh-pakkies, verifieer handtekeninge, en serialiseer pakkies na binêre draadformaat. Dit demonstreer ook die in-proses-transportlaag.

**Stuur 'n boodskap in Rust:**

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

Die demo skep twee nodes in 'n gesimuleerde netwerk, genereer Ed25519-sleutels, vestig Signal Protocol-sessies, skep en onderteken 'n pakkie, serialiseer dit na C#-versoenbare binêre formaat, enkripteer 'n geheime boodskap, dekripteer dit op die ander node, stuur dit deur die transport, en verifieer die heen-en-weer-reis.

**Stuur 'n boodskap in TypeScript:**

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

Die demo loop 8 demonstrasies: Ed25519-sleutelgenerasie en peutering-opsporing, node-skepping met vermoëns, Signal Protocol X3DH-sleuteluitruil, AES-256-GCM-enkripsie en -dekripsie, pakkie-serialisasie, pakkie-ondertekening met herspeel-opsporing, in-proses-transport, en 'n volledige end-tot-end-vloei wat alle lae kombineer.

**Stuur 'n boodskap in Python:**

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

Die demo loop 5 demonstrasies: pakkie-serialisasie heen-en-weer-reise, Ed25519-ondertekening met peutering-opsporing, Signal Protocol-sessievestiging met geënkripteerde boodskappe in beide rigtings, in-proses-transport tussen twee eweknieë, en nonce-ontdubbeling vir herspeel-beskerming.

**Stuur 'n boodskap in Go:**

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

Die demo lei deur 11 stappe: sleutelgenerasie, node-skepping met vermoëns, Signal Protocol-inisialisasie, voorsleutelbundel-uitruil, sessievestiging, pakkie-skepping en -ondertekening, serialisasie, deserialisasie met handtekeningverifikasie, end-tot-end-enkripsie met sleutelratel, herspeel-aanval-opsporing, en in-proses-transport.

**Stuur 'n boodskap in Kotlin:**

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

Die demo loop 5 toetse: pakkie-serialisasie heen-en-weer-reise, Ed25519-ondertekening met peutering-verwerping, Signal Protocol-sessievestiging met AES-256-GCM-enkripsie, in-proses-transport-boodskaplewering, en 'n volledige end-tot-end-vloei waar Alice 'n pakkie onderteken en Bob dit na transport verifieer.

**Stuur 'n boodskap in Swift:**

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

Die demo loop 7 demonstrasies: Ed25519-sleutelgenerasie, pakkie-skepping en -ondertekening, serialisasie na binêre draadformaat, deserialisasie met integriteitskontroles, AES-256-GCM-enkripsie en -dekripsie, HMAC-SHA256-boodskapstawing, en HKDF-SHA256-sleutelafleiding.

**Stuur 'n boodskap in C:**

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

## Padkaart

Wat gebou is en wat volgende is.

**Klaar (kruistaal-geverifieer, al 8 implementasies):**
- Draadformaat: greep-identies oor 8 tale, veranker deur 14 kanonieke fixtures en kruistaal-bewerings in CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9-taak-matriks (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, plus fixture-integriteitstaak) in `.github/workflows/ci.yml`.
- Ed25519-pakkie-ondertekening en -verifikasie
- AES-256-GCM-enkripsie
- HKDF / HMAC-sleutelafleiding-primitiewe
- Pakkie-serialisasie + ondertekening-uitleg (LE + 4-greep-int32-velde)
- In-proses-transport-simuleerder (vir ontwikkeling en toetse)
- AODV-geïnspireerde roeteringsdiens met RREQ/RREP, ondertekende roete-antwoorde, ontdubbeling, TTL-aanstuur
- DTN-stoor-en-stuur-diens met bewaringsoordrag, geohash-bewuste replikasie, 72u TTL
- SOS-uitsaai-diens met vloed, ontdubbeling, self-oorsprong-wag, tempo-limiet (3/uur)
- Uitbreidbaarheidsnate: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (Noop-verstek)
- **~3,000 toetse** oor al 8 tale (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — almal groen in CI
- ✅ **Werklike X3DH-efemere sleutel (8 tale)** — 4 X25519 DH's (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) met HKDF-SHA256-wortelafleiding. Vasgepen deur `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Double Ratchet-belyning familie-wyd** — volledige Signal §5 met HMAC-SHA256 + 0x01/0x02-domeinskeiding in die simmetriese ratel, HKDF-SHA256 KDF_RK in die DH-ratel-stap, DH-rotasie by ontvangs. Geverifieer deur `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`-fixtures.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 versoen met HEAD** — sien `docs/PROTOCOL_SPEC.md`.

**Klaar (al 8 tale):**
- ✅ **Stem-oproepe (1-tot-1)** — seingewing-toestandsmasjien (Offer/Answer/Hangup/Cancel/Timeout) + binêre raam-transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N grepe). Roete-bewuste lewering via `IRoutingService`.
- ✅ **Groepstem** — gasheer-gedrewe lidmaatskap (invite/kick/leave), per-raam-sleutelgenerasie-veld, unicast-uitwaaiering na alle huidige lede, gasheer-beheerde sleutelrotasie by lidmaatskapverandering.
- ✅ **Lewendige stroom** — uitgewer saai `StreamAnnounce` uit; intekenaars stuur `StreamSubscribe`; binêre `StreamSegment`-rame (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N grepe) unicast na elke intekenaar.
- ✅ **Video-oproepe (1-tot-1)** — kodek/resolusie/fps/bittempo-onderhandeling in seingewing, keyframe-versoek- en gehalteveranderingseine, binêre `VideoFrame`-formaat wat by stemuitleg pas.
- ✅ **Saam Kyk** — gasheer stuur gesaghebbende `WatchSync`(play/pause/seek/speed)-opdragte uit; volgelinge pas toe met RTT-kompensasie (`position = positionMs + elapsed × playbackSpeed`); vuur-en-vergeet `WatchReaction`.
- ✅ **Eenmalige voorsleutel(OPK)-poel** — verstek 100, FIFO-uitgifte, luie aanvulling, slot-beskermde verbruik oor al 8 tale. Sluit die enkel-OPK-gelyktydigheidsgevaar toe.
- ✅ **C: volledige Signal-sessie** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` in `c/src/signal_protocol.c`; 6 twee-node-E2E-toetse in `c/tests/test_signal_session.c`. Al 8 tale het nou volledige sessie-vermoënde Signal Protocol.

**Klaar (al 8 tale — die volledige draaddiens-suite):**
- ✅ **Elke gereserveerde pakkietipe is nou 'n werklike, greep-identiese diens in al 8 tale.** Teenwoordigheidsbaken/-navraag (21/22), hartklop (10), profielsinkronisasie (23), efemere-roeterings-ID-aankondiging (56), voorsleutel-uitruil (25/26), kanale (7), druk-om-te-praat (15), skermdeling (32), oproepbeheer (27), SOS-erkenning (6), ruimte-broodkrummels (40), smee-aankondiging (41), kluis-skerf-versoek (42), en bandwydte-meting / ABMF (53/54/55). Elkeen is 'n dun diens (produseer + hanteer + gebeurtenis) wat die gasheer bedraad aan sy Signal-sessie en roeteringstabel; elkeen is vasgepen aan 'n gedeelde kruistaal-fixture (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) en getoets deur per-taal-eenheidstoetse, met Swift en C wat op die macOS-bouserver geverifieer word. Sien **Wat jy kry**.

**Klaar (slegs C#-verwysing):**
- ✅ **Demo Stap 9 — MessagingService + DTN-terugval end-tot-end** — `samples/AetherNet.Demo.Console` lei deur werklike-Signal-geënkripteerde boodskappe met DTN-stoor-en-stuur wanneer die ontvanger vanlyn is.
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security`-brug** — `SignalMessageEnvelopeCipher` maak die boodskaplaag by verstek end-tot-end-geënkripteer; boodskappe sonder 'n Signal-sessie word in 'n tou geplaas, nooit onveilig gestuur nie.
- ✅ **Aanpasbare bittempo-stroom** — `AdaptiveBitrateController` met spec-verpligte bittempo-leers vir Profiel A (intyds), B (lewendige uitsaai), en C (VOD). Uitgewer kies die hoogste volhoubare sport (20% speelruimte) en stuur `StreamAbandon` (`PacketType.StreamAbandon`) uit in plaas van 'n segment wanneer onder die vloer. `IStreamingService` stel `UpdateBandwidthEstimate` en `GetCurrentBitrateRung` bloot.
- ✅ **Saam Kyk: BitTorrent-inname + ChipIn-groepfinansiering** — `TorrentInfo` / `TorrentFile`-modelle; `WatchTogetherService` hanteer `PacketType.TorrentMetadata` en vuur `TorrentReceived`. `ChipInPool` / `ChipInContribution`-toestandsmasjien (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` op `IWatchTogetherService`.
- ✅ **Groepvideo-oproepe met outomatiese SFU-aanstuur** — `GroupVideoService` / `IGroupVideoService`. FullMesh-topologie vir ≤ 3 deelnemers; outomatiese oorskakeling na SFU by `SfuThresholdParticipants` (4) met aanstuur-hertoewysing via `GroupVideoSignaling(SfuAssigned)`. Uitwaaiering in FullMesh, slegs-aanstuur-stuur in SFU-modus. Seingewing-pakkietipe `GroupVideoSignaling = 35`.
- ✅ **BLE GATT-transport-simulasie** — `SimulatedBleGattTransportService` (`IBleTransportService`). GATT MTU-omraming via `BleGattFramer` (1024 B/raam, `[2B count][2B index][payload]`), in-proses statiese eweknie-register, advertensie-uitsaai. Alle `BleMaxPayloadBytes`-beperkings afgedwing.
- ✅ **Wi-Fi Direct-transport-simulasie** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Eksplisiete `ConnectAsync`/`DisconnectAsync`-lewensiklus, direkte groot-lading-lewering (geen omraming), tweerigting-`PeerConnected`/`PeerDisconnected`-gebeurtenisse.
- ✅ **NearLink-transport-simulasie** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096 B raam-MTU, 500-eweknie-register, `ConnectedPeerCount`, `IsAvailable` tydens looptyd stelbaar.
- ✅ **RF-inbedryfstelling-simulasietoetse** — Twee-node-interop-toetse (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` heen-en-weer, WiFi Direct 64 KB lading-oordrag. Sagtewarelaag volledig geverifieer; fisiese toestel-labsessie nodig vir op-hardeware-validering.

**Klaar (C#-transportlaag — almal misluk-vinnig):**
- ✅ **BLE GATT werklike transport** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT-bediener). Volledige RF-inbedryfstelling-toets in `samples/AetherNet.BleRfTest/`.
- ✅ **Wi-Fi Direct werklike transport** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket poort 8888) + `android/green/` (`WifiP2pManager`). RF-toets in `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **HTTP-aanstuur-transport (Aether Purple)** — `HttpRelayTransportService` met 10-sekonde-lang-peiling, `PowerCostRelative = 100`, altyd laaste uitweg. Aanstuurbediener in `samples/AetherNet.RelayServer/` (ASP.NET Core minimale API, poort 5200). RF-toets in `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implementeer `HostApduService` met AID `F061657468657200`. `WinNfcStubTransportService` dokumenteer twee Windows-benaderingspaaie: (1) NDEF-oor-BLE-GATT met RSSI-hek ≥ −40 dBm (simuleer tik-om-te-verbind sonder NFC-silikon, `IsAvailable = Bluetooth teenwoordig`); (2) ACR122U USB-leser via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = kontaklose leser opgesom`). Opgraderingspad: implementeer `ITransportService` wanneer Microsoft 'n eersteparty-P2P-NFC-API stuur.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — volledige HarmonyOS 5.0.1 (API 13) ArkTS-implementasie met `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` tydens looptyd gepeil. `WinNearLinkStubTransportService` + `android/teal/` dokumenteer die SSAP-oor-BLE-benadering: BLE GATT met Aether SLE-diens-UUID `61657468-6572-0003-0000-000000000000` — API-analoog aan SSAP, nie draad-versoenbaar met werklike NearLink-hardeware nie. Opgraderingspad: vervang BLE GATT-oproepe met `ssapc_*`/`ssaps_*`-SDK-oproepe; UUIDs en `TransportManager`-gleuf onveranderd.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` dokumenteer die Meshtastic-oor-BLE-LR-benadering: volledige Meshtastic-draadformaat (16-greep-kop + AES-256-CTR protobuf) oor BLE 5.0 Coded PHY S=8 (~1.3 km buite), met bestuurde-vloed-roetering en RSSI-geweegde wedywerings-venster. Brug-node-federasie met werklike LoRa-hardeware werk outomaties (dieselfde Meshtastic-pakkieformaat, geen vertaling). Opgraderingspad: vervang BLE LR-radio met SX1276/SX1278 AT-opdrag of SPI-drywer; pakkieformaat en roetering onveranderd.

**Oop — nagespoor in `OPEN_ISSUES.md`:**
- RF-inbedryfstelling op werklike hardeware: end-tot-end twee-node-interop-toets op fisiese BLE- / Wi-Fi Direct-toestelle (simulasietoetse slaag; hardeware-labsessie nodig)
- NearLink: `harmonyos/teal/` voltooi; benodig Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6-hardeware (NearLink-silikon nie teenwoordig op nie-Huawei-toestelle nie). Windows + Android val outomaties terug na SSAP-oor-BLE-benadering.
- LoRa / CircleLink: radiomodule vereis vir ware LoRa-bereik. Sonder een word die Meshtastic-draadformaat oor BLE LR (~1.3 km) gedra en is brug-node-federasie met werklike LoRa-hardeware beskikbaar.
- ✅ **(OPGELOS v1.2.0)** Verbruikersprotokol-oppervlak (Golf 16/17) — `IDtnService.BundleReceived`-gebeurtenis vir inkomende bundels ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), toepassingslaag-benaming/-ontdekkingsgids ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), outeur-fooibetaling-koppelvlak ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Al 3 additief gestuur oor 8 tale met greep-gelyke kruistaal-fixtures. Sien CHANGELOG.

**Nog nie oop vir eksterne bydrae nie:**
- Die protokol is steeds onder aktiewe ontwikkeling. Eksterne bydraes word nie op hierdie tydstip aanvaar nie.
- NearLink-transport-implementasie, Android/iOS-integrasievoorbeelde, addisionele transport-agtergronde, prestasiemaatstawwe, en protokol-fuzzing word intern nagespoor en sal oopgemaak word wanneer die projek 'n stabiele publieke bydraepunt bereik.

## Projekstruktuur

```
aether-protocol/
  src/
    AetherNet.Core/          Protocol models, constants, packet serialization
    AetherNet.Security/      Signal Protocol, Ed25519, packet signing
    AetherNet.Transport/     Transport abstractions, NearLink, in-process simulator
    AetherNet.Messaging/     Message handling and relay
    AetherNet.Storage/       DTN store-and-forward persistence
    AetherNet.Streaming/     Adaptive bitrate streaming, video models and interfaces
    AetherNet.Voice/         Voice calls and group voice
    AetherNet.Content/       Content verification and chunked transfer
  samples/
    AetherNet.Demo.Console/  Interactive demo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Rust implementation
  typescript/             TypeScript implementation
  python/                 Python implementation
  go/                     Go implementation
  kotlin/                 Kotlin/JVM implementation
  swift/                  Swift implementation
  c/                      C implementation
  docs/
    PROTOCOL_SPEC.md      RFC-style protocol specification
```

## Voeg 'n nuwe transport by

Implementeer `ITransportService`:

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

Registreer dit in DI en `TransportManager` sal dit outomaties in transportkeuse insluit, gesorteer volgens kragkoste.

## Hoe dit vergelyk

| Protokol | Beperking | Aether-voordeel |
|----------|-----------|-----------------|
| **Briar** | Slegs-Android, Tor-afhanklik | Kruisplatform, suiwer mesh |
| **Meshtastic** | Slegs LoRa (30 kbps maks) | Multi-transport (BLE + WiFi + NearLink), stem- en stroom-vermoënd |
| **Reticulum** | Python, klein gemeenskap | 8 tale, draad-versoenbaar oor almal daarvan |
| **libp2p** | Neem internet-ruggraat aan | Vanlyn-eerste, werk met nul infrastruktuur |
| **Yggdrasil** | Oorlegnetwerk, benodig internet | Fisiese-laag-mesh, werk sonder internet |
| **Signal** | Geen mesh, benodig internet | Werk vanlyn, P2P, mesh-aanstuur, dieselfde E2E-enkripsie |

## Gereelde vrae

**Werk AetherNet sonder die internet?**
Ja — dit is vanlyn-eerste. Toestelle praat direk oor Bluetooth, Wi-Fi Direct, NearLink of LoRa en stuur boodskappe hop-vir-hop deur ander toestelle aan, sonder dat 'n internetverbinding, seltoring of bediener nodig is. Wanneer daar geen lewende roete bestaan nie, word boodskappe gehou (vertraging-verdraagsame stoor-en-stuur) vir tot 72 uur totdat een oopgaan.

**Is dit end-tot-end-geënkripteer?**
Ja. AetherNet gebruik die Signal Protocol (X3DH-sleutelooreenkoms plus die Double Ratchet oor X25519) vir end-tot-end-enkripsie, AES-256-GCM vir boodskapladings, en Ed25519-handtekeninge op elke pakkie. Toestelle wat 'n boodskap aanstuur, kan dit nie lees nie.

**Watter transporte gebruik dit?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), 'n LoRa/CircleLink-reeksradio, 'n HTTP/QUIC-aanstuur, en WebRTC vir direkte internet-eweknie-tot-eweknie. Die protokol kies outomaties die laagste-krag beskikbare transport per pakkie en val terug na die volgende.

**In watter programmeertale is dit beskikbaar?**
Agt — C#, Rust, TypeScript, Python, Go, Kotlin, Swift en C. Elke implementasie produseer greep-identiese draadpakkies, afgedwing deur 'n gedeelde kruistaal-fixture-korpus in CI, sodat 'n pakkie wat deur een taal gebou is, ongewysig deur enige ander gedekodeer word.

**Hoe verskil dit van Meshtastic, Briar of Bridgefy?**
Meshtastic is slegs-LoRa; AetherNet is multi-transport (Bluetooth + Wi-Fi + NearLink + LoRa) en dra stem, video en stroom sowel as boodskappe. Briar is slegs-Android en roeteer oor Tor; AetherNet is kruisplatform en suiwer mesh. Anders as geslote SDK's, is AetherNet MIT-gelisensieer en openlik in agt tale geïmplementeer. Die vergelykingstabel hierbo het die besonderhede.

**Is dit produksie-gereed?**
Die protokollaag — draadformaat, Signal-sekuriteit, roetering, DTN-stoor-en-stuur, en die volledige diens-suite — is oor al agt tale geïmplementeer en getoets. Radiotransporte is werklik waar platformkode bestaan (Bluetooth en Wi-Fi op Windows en Android, WebRTC oral) en veldonbevestig elders hangend hardeware-inbedryfstelling, wat eerlik in `OPEN_ISSUES.md` nagespoor word. Lees die statusnotas in elke afdeling voordat jy ontplooi.

**Onder watter lisensie is dit?**
MIT — gratis vir kommersiële en oopbron-gebruik. Sien [LICENSE](LICENSE).

**Wie bou AetherNet?**
Dit word ontwikkel as die oop protokol agter The Geek Network se mesh-ekosisteem, gebou in Suid-Afrika vir kommunikasie wat met of sonder mobiele data werk.

## Uitbreidingspunte

Die protokol werk op sy eie. Hierdie koppelvlakke laat jou toe om jou eie agtergrond in te prop as jy een wil hê:

- `IAetherNetIncentiveProvider` — beloon nodes wat verkeer aanstuur (geen-werking-verstek: altruïstiese aanstuur)
- `IAetherNetBackendClient` — sinkroniseer met 'n bediener wanneer internet beskikbaar is (geen-werking-verstek: heeltemal vanlyn)
- `IAetherNetFeatureFlagProvider` — skakel protokolkenmerke tydens looptyd aan/af (geen-werking-verstek: alles geaktiveer)

Al drie stuur met geen-werking-implementasies. Verwyder hulle en niks breek nie.

## Bydraes

Eksterne bydraes is nog nie oop nie. Die projek is steeds onder aktiewe ontwikkeling. Kom weer terug wanneer ons 'n publieke bydraevenster aankondig.

## Sekuriteit

Sien [SECURITY.md](SECURITY.md) vir verantwoordelike openbaarmakingsbeleid.

## Lisensie

MIT-lisensie. Sien [LICENSE](LICENSE).

## Vertalings

Hierdie README word ook in die ander tale wat in die taalbalk boaan hierdie lêer gelys word, onderhou, onder [`docs/i18n/`](docs/i18n/) — wat Europese, Oos-Asiatiese, Midde-Oosterse, Suid-Asiatiese, Suidoos-Asiatiese, en Afrika-tale strek, want 'n netwerk wat gebou is vir mense sonder data behoort nie 'n voordeur te hê wat net die goed-verbondes kan lees nie. Die **Engelse weergawe is die bron van waarheid**: waar 'n vertaling en die Engelse teks verskil, is die Engelse teks gesaghebbend, en vertalings mag dit met 'n vrystelling of twee agterlaat. Die protokol, kode, fixtures en gedrag wat beskryf word, is identies ongeag watter taal jy lees.
