# AetherNet — yarjejeniyar sadarwar mesh mai ba da fifiko ga aiki ba tare da layi ba

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet yarjejeniyar sadarwar mesh ce ta buɗaɗɗen tushe, mai lasisin MIT** don aika saƙonni, fayiloli, murya, da bidiyo zuwa mutanen da ke kusa — tare da **babu intanet, babu sabar, kuma babu rajista**. Na'urori suna haɗuwa kai tsaye ta Bluetooth, Wi-Fi Direct, NearLink, da LoRa; lokacin da mai karɓa ya fita daga zango, saƙonni suna tsallake ta wasu na'urori kuma suna jira har sa'o'i 72 don samun hanya. Yana zuwa da **aiwatarwa iri ɗaya baiti-da-baiti a cikin harsunan shirye-shirye takwas** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, da C.

Ka raba fayiloli, saƙonni, da kuma yaɗuwar bidiyo (streams) da mutanen da ke kusa da kai. Babu WiFi. Babu bayanan wayar salula (mobile data). Babu rajista. Kamar AirDrop, sai dai yana aiki da kowa, a kan kowace dandamali (platform).

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Yarjejeniya ɗaya, harsuna takwas, iri ɗaya a kan waya (wire).** An gina Aether da **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, da C** — kuma kowace fakiti (packet) iri ɗaya ce baiti-da-baiti a duk cikinsu, wanda aka tabbatar da shi ta hanyar rukunin gwaji (fixture corpus) da ake rabawa tsakanin harsuna a CI. Ka gina node ɗinka a kowanne cikin takwas ɗin; zai iya aiki tare da sauran duka. Wannan README kuma ana samun sa a cikin harsunan mutane 11 (hanyoyin haɗi a sama).

## Me za ka iya yi da shi?

**Raba bayanan lacca ba tare da ɓata data ba.**

Kana cikin ƙungiyar karatu. Wani yana da tsofaffin takardun jarrabawa a wayarsa. Aether yana aike su kai tsaye zuwa na'urarka ta Bluetooth — babu hotspot, babu ƙungiyar WhatsApp, babu iyakan girman fayil. Idan wani a cikin ƙungiyar ya fita daga zango, fayil ɗin yana tsallake ta wasu na'urori har sai ya kai gare shi. Saƙonni na iya jira har sa'o'i 72 don samun hanya idan an buƙata.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Gano abin da ke faruwa a kewayenka.**

Kana wani biki na jami'a ko wani biki na jama'a. Aether yana gano wasu na'urorin da ke kusa ta Bluetooth da WiFi Direct — babu labari na manhaja (app feed), babu algorithm. Kana ganin abin da ke gaskiya a kewayenka, ba abin da aka tallata ba.

**Aika SOS lokacin da babu siginar waya.**

Wayarka ba ta da siginar waya. Aether yana watsa saƙon gaggawa zuwa kowace na'ura da ke cikin zango, kuma waɗannan na'urorin suna wucewa da shi. Ba a buƙatar hasumiyar sadarwa (cell tower).

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Ƙirƙiri tashoshin ƙungiya masu zaman kansu.**

Tasha don benen zamanka (res floor), ƙungiyar ka, ko tawagar aikinka. Membobin da aka tabbatar kaɗai ne za su iya karantawa ko aika saƙonni. Babu sabar da ke ajiye tattaunawar.

**Sayar da abubuwa ga mutanen da ke kusa.**

Ka lissafa littafin karatu don sayarwa. Mutanen da ke tafiya cikin zangon mesh suna ganin sa. Babu asusun kasuwa, babu kuɗin lissafi — kusanci kawai.

**Kalli fim tare, a fadin mesh.**

Ƙungiyarka na da daren kallon fim. Wani na da fayil ɗin. Aether yana daidaita kunnawa a kan kowace na'ura — kunnawa (play), tsayarwa (pause), tsallakewa (seek) — duka a tare cikin daidaituwa. Idan wasu mutane kaɗai ke da fayil ɗin, mesh yana rarraba shi a nan take a matsayin yaɗuwar P2P. Kowa yana ba da gudummawa ta SDPKT don siyan sa idan babu wanda yake da shi.

## Yadda yake aiki

Na'urori suna magana kai tsaye da juna ta amfani da Bluetooth, WiFi Direct, ko NearLink. Babu haɗin intanet, babu sabar, babu tsakiyar kayan more rayuwa (central infrastructure).

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

Lokacin da saƙo ba zai iya kai wa inda za shi ba kai tsaye, yana tsallake ta wasu na'urori. Waɗannan na'urorin masu tura saƙo ba za su iya karanta abin da suke ɗauka ba — an ɓoye kowane saƙo da AES-256-GCM. An sa hannu a kan kowace fakiti da makullan asali na Ed25519, kuma cibiyar sadarwa tana jefar da fakitin da aka ƙirƙira ta hanyar ƙarya.

> **Bayani game da balagar tsaro (a karanta kafin turawa):** X3DH na gaskiya (4 X25519 DHs), cikakken Signal Double Ratchet (matakin juyawar DH a lokacin karɓa, KDF_RK, 0x01/0x02 chain ratchet), da tafkin makullan gaba na lokaci ɗaya (one-time pre-key pool) (tsohon saiti 100 OPKs, FIFO, mai kariyar kulle) an aiwatar da su a cikin **dukkan harsuna 8** kuma an ɗaure su ga rukunin gwaji (fixture corpus) da ake rabawa tsakanin harsuna a ƙarƙashin `fixtures/signal/`. Abu ɗaya kawai da ya rage buɗe shi ne kunna RF na zahiri a kan ainihin kayan aikin BLE (ana bibiyar sa a cikin `OPEN_ISSUES.md`).

Babu asusai, babu lambobin waya, babu imel. Kana samar da makullin biyu (keypair) sannan kana kan cibiyar sadarwa.

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

**Zaɓen hanya (Routing)** — AODV tare da amsoshin hanya masu sa hannu. Ana sa hannu a kan kowace amsar hanya ta makullin Ed25519 na inda za a je, don haka babu na'urar da za ta iya yin kamar cewa ita ce inda za a je alhali ba ita ba ce.

**Ajiye-da-tura (Store-and-forward)** — Lokacin da babu hanya mai aiki, ana riƙe fakiti har sa'o'i 72 har sai an sami hanya.

**Zaɓen jigilar sadarwa (Transport selection)** — Yarjejeniyar tana zaɓar madaidaicin jigilar sadarwa ga kowace fakiti. Ƙananan saƙonnin sarrafawa suna wucewa ta BLE. Manyan canja wuri suna amfani da WiFi Direct. NearLink lokacin da ake da shi.

**Murya, bidiyo, da yaɗuwa (streaming)** — Kiran bidiyo tare da tattaunawar codec (H.264/H.265/VP8), zaɓen inganci mai sanin jigilar sadarwa, bidiyo na ƙungiya tare da tura SFU ta atomatik, kallo-tare da aka daidaita tare da rama RTT, da yaɗuwa mai daidaita bitrate.

**Kariyar sake kunnawa (Replay protection)** — Kawar da maimaituwar nonce tare da tagar sabuntawar lokaci na mintuna 5.

## Abin da kake samu — kowace hidima, a cikin kowane harshe

Aether ba wai kawai jigilar sadarwa ba ce. Kowace irin fakiti da yarjejeniyar ta ajiye yanzu ta zama **ainihin hidima mai aiki a cikin dukkan harsuna 8**, kuma kowanne yana zama **fakitin waya iri ɗaya baiti-da-baiti** — fakitin da node ɗin Go ya gina ana warware shi, ba tare da canji ba, ta node ɗin Swift, Rust, C, Python, TypeScript, Kotlin, ko C#. An ɗaure kowace hidima ga rukunin gwaji (fixture) da ake rabawa tsakanin harsuna a ƙarƙashin `fixtures/<service>/` kuma ana gwada shi ta gwaje-gwajen naúra na kowane harshe, tare da tabbatar da Swift da C ƙari a kan sabar gina ta macOS.

| Damar aiki | Abin da yake yi | Nau'in fakiti | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Fitilar kasancewa & tambaya (Presence beacon & query)** | Sanar da "Ina nan" da tambaya "wanene ke kusa?" — ta hanyar **ID na wucin gadi mai juyawa, wanda aka samo daga makulli** (ba ainihin asalinka ba) tare da geohash mai kauri | 21, 22 | `fixtures/presence/` | ✅ |
| **Bugun zuciya (Heartbeat)** | Ci gaba da rayuwa mai sauƙi tsakanin abokan da aka haɗa | 10 | `fixtures/heartbeat/` | ✅ |
| **Daidaita bayanin martaba (Profile sync)** | Musanya katin bayanin martaba mai sa hannu da abokin tarayya ta mesh | 23 | `fixtures/profiles/` | ✅ |
| **Sanarwar Ephemeral-ID** | A ɓoye faɗa wa aboki ID na zaɓen hanya mai juyawa na yanzu don su iya kai wa gare ka har yanzu bayan ya juya | 56 | `fixtures/erid/` | ✅ |
| **Musanya pre-key** | Nemi da isar da fakitin Signal pre-key ta mesh, don fara zaman ƙarshe-zuwa-ƙarshe da wanda ba ka taɓa saduwa da shi ba | 25, 26 | `fixtures/prekey/` | ✅ |
| **Tashoshi (Channels)** | Saƙonni masu sa hannu zuwa tasha mai zaman kanta, ta membobi kaɗai | 7 | `fixtures/channels/` | ✅ |
| **Danna-ka-yi-magana (Push-to-talk)** | Firaman muryar walkie-talkie (nauyin sauti da aka ɓoye) | 15 | `fixtures/media/` | ✅ |
| **Raba allo (Screen share)** | Firaman bidiyon raba allo (nauyin bidiyo da aka ɓoye) | 32 | `fixtures/media/` | ✅ |
| **Sarrafa kira (Call control)** | Siginar kiran waya / karɓa / ƙi / kashewa don kiran murya da bidiyo | 27 | `fixtures/videocall/` | ✅ |
| **Amincewa da SOS (SOS acknowledgement)** | Tabbatar wa mai aikawa cewa an karɓi watsa gaggawarsu | 6 | `fixtures/sos/` | ✅ |
| **Alamomin sarari (Space breadcrumbs)** | Alamomin gano wuri masu ɗauke da alamar wuri don layin "abin da ke kewayena" | 40 | `fixtures/space/` | ✅ |
| **Sanarwar forge (Forge announce)** | Talla wani abu na abun ciki da aka samo/ƙirƙira ga mesh | 41 | `fixtures/forge/` | ✅ |
| **Buƙatar shard na vault (Vault shard request)** | Ɗauko shard na ajiya da aka lambanta (kowane K na N shards yana sake gina fayil ɗin) | 42 | `fixtures/vaultshard/` | ✅ |
| **Aunawar bandwidth (Bandwidth measurement)** | Bincike / amincewa / yaɗa yawan kayan haɗin gwiwa don mesh ya zaɓi hanya ta bututu mafi kauri (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Waɗannan suna zaune a saman hidimomin da suka riga suka kammala na **saƙonni, murya na 1-zuwa-1 da na ƙungiya, kiran bidiyo, yaɗuwa kai tsaye, kallo-tare, zaɓen hanya na AODV, ajiye-da-tura na DTN, da ambaliyar SOS** — waɗanda kuma aka aiwatar da su a cikin dukkan harsuna 8.

> **Ma'anar "an gina" a nan, daidai.** Kowace hidima tana samarwa da sarrafa fakitin waya ta, tana tayar da abubuwan da suka dace, kuma an ɗaure ta ga fixture na matakin baiti wanda dukkan iyalin harshe dole su daidaita da shi. Manhajarka tana haɗa hidimar zuwa zamanta na Signal, teburin zaɓen hanya, da yanayin gida. Wannan shi ne matakin yarjejeniya — an tabbatar da shi a cikin lamba, gwaje-gwaje, da fixtures na baiti tsakanin harsuna — a kan tushen RF mai gaskiya iri ɗaya kamar kowane abu: duk wata hanya da a ƙarshe ke hawa a kan rediyo ba a tabbatar da ita a fagen aiki ba har sai kunna kayan aiki da ake bibiyar sa a cikin `OPEN_ISSUES.md`.

## Jigilar sadarwa (Transports)

Kowace jigilar sadarwa tana da sunan launi da ake amfani da shi ko'ina cikin lambar codebase. `IsAvailable` yana sarrafa hanyoyin da kayan aiki suka toshe — `TransportManager` yana tsallake su kuma yana komawa ga jigilar sadarwa ta gaba da ke akwai.

**Makullin matsayi:** ✅ na gaskiya, an gina & tabbatar · ⏳ na gaskiya, tabbatarwa na ci gaba · ⚠️ na gaskiya a wasu dandamali, stub a wasu · ❌ stub (babu lambar jigilar sadarwa har yanzu).

| Launi | Suna | Zango | Bandwidth | Matsayi |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Na gaskiya — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Na gaskiya — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | Marar iyaka | ~10 Mbps | ✅ Na gaskiya — Windows; sabar relay a cikin `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Tashar bayanan intanet | Marar iyaka | ~100 Mbps | ✅ Na gaskiya a cikin dukkan harsuna 8 — **an tabbatar da loopback a cikin dukkan 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust kowanne yana da abokai biyu suna musanya baiti ta ainihin tashar bayanan ICE) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Na gaskiya a Android (`android/white/`); Windows = ainihin BLE-GATT + kimanta kusanci na RSSI −40 dBm (`WinNfcBleTransportService`, yana harhaɗawa net9/10, ba a tabbatar da lokacin aiki ba) — an cire `Windows.Networking.Proximity` a cikin Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Na gaskiya a HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — ana jiran tabbatarwa a kan na'ura); Android + Windows = ainihin kimanta SSAP-over-BLE (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; an tabbatar da harhaɗawa + gwajin naúra, ba a tabbatar da lokacin aiki ba) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Ainihin direba na RYLR SX127x/SX126x serial (`LoRaSerialTransport` a C#/Go/Rust/C; yana harhaɗawa, ba a tabbatar da lokacin aiki ba — yana buƙatar module na zahiri); gadar BLE Coded-PHY har yanzu ƙira ce da aka rubuta |

Jigilar sadarwa na rediyo na gaskiya ne kawai inda lambar dandamali ta wanzu (C#/Windows, Kotlin/Android, HarmonyOS). Ɗakunan karatun harsuna takwas in ba haka ba suna aikawa da jigilar sadarwa ta **kwaikwayo cikin tsari (in-process simulation)** don gwaji — **WebRTC ita ce jigilar sadarwa ta gaskiya ta farko da take na kowanne cikinsu** (an kammala; an tabbatar da loopback a fadin harsunan).

Fifiko yana ne bisa tsadar wutar lantarki: ana fifita mesh na rediyo, sannan WebRTC a matsayin hanyar intanet kai tsaye, tare da HTTP/QUIC relay a matsayin mafita ta ƙarshe.

## Matakan turawa (Deployment tiers)

Aether yana aiki a kan kowace dandamali da ke tallafa wa Bluetooth ko Wi-Fi. Matakin da kake kai ya dogara da OS ɗin da kake nufi.

---

### Matakin daidaitacce (Standard tier) — kowace dandamali

Android · Windows · Linux · macOS · iOS

Aether yana gudana a kan kowace na'ura mai kayan aikin Bluetooth ko Wi-Fi. Inda rediyo ba ya nan a zahiri, ana kimanta kowace jigilar sadarwa da aka toshe a kan abin da ke akwai. Waɗannan kimantawa yanzu **ainihin lamba ne** (an tabbatar da harhaɗawa; **ba a tabbatar da lokacin aiki ba** ana jiran gwajin RF na na'urori 2 / na kayan aiki):

- **NearLink (Aether Teal)** — ainihin kimanta SSAP-over-BLE-GATT (Aether SLE UUID `61657468-6572-0003-…`) a Android (`android/teal/AetherNetSleService`) da Windows (`WinNearLinkBleTransportService`); an tabbatar da harhaɗawa + gwajin naúra, ba a tabbatar da lokacin aiki ba. Ainihin rediyon NearLink yana wanzu ne kawai a kan HarmonyOS (`harmonyos/teal/`, ana jiran tabbatarwa a kan na'ura).
- **LoRa (Aether Red)** — ainihin direba na RYLR SX127x/SX126x serial (`LoRaSerialTransport` a cikin **dukkan harsuna 8** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; an tabbatar da harhaɗawa kowace fassara, gami da Swift + C a kan sabar gina ta Mac; ba a tabbatar da lokacin aiki ba — yana buƙatar module na zahiri). Gadar Meshtastic-over-BLE-Coded-PHY (~1.3 km) ta rage ƙira da aka rubuta; ainihin LoRa mai dogon zango yana buƙatar node mai iya LoRa (gateway, SBC, ko wayar hannu mai ƙarfi da module na LoRa).
- **NFC (Aether White)** — na gaskiya a Android (HCE). Windows yanzu yana da ainihin kimanta kusanci na BLE-GATT + RSSI −40 dBm (`WinNfcBleTransportService`, yana harhaɗawa net9/10; ba a tabbatar da lokacin aiki ba); ACR122U PC/SC lokacin da mai karatu ke nan.

Abin da ke gaskiya kuma iri ɗaya ko'ina: **BLE, Wi-Fi Direct, HTTP/QUIC relay, da jigilar sadarwa ta WebRTC P2P (an tabbatar da loopback a cikin dukkan harsuna 8)**, tare da tsaron Signal Protocol (X3DH + Double Ratchet), zaɓen hanya na AODV, ajiye-da-tura na DTN, watsa SOS, murya, da yaɗuwa.

**Matsayi mai gaskiya:** BLE + Wi-Fi Direct + relay na gaskiya ne na samarwa; **WebRTC P2P na gaskiya ne kuma an tabbatar da loopback a cikin dukkan harsuna 8** (abokai biyu suna musanya baiti ta ainihin tashar bayanan ICE — an tabbatar da Rust a kan akwatin Linux na `.201` tare da UDP ICE mai aiki); kimantawar NearLink / LoRa / NFC-a-Windows yanzu ainihin lamba ne da ke harhaɗawa (an tabbatar da harhaɗawar LoRa a cikin dukkan 8, gami da Swift + C a kan sabar gina ta Mac; an kuma gwada NearLink-Android ta gwajin naúra) amma **ba a tabbatar da lokacin aiki ba** — babu gwajin RF na kayan aiki / na'urori 2 tukuna. Suna shiga cikin mesh a cikin lamba; kada ka tura waɗannan uku kana tsammanin RF da aka tabbatar a fagen aiki.

---

### Matakin na asali (Native tier) — CircleOS / OpenHarmony

CircleOS · HarmonyOS · kowace OS da ta dogara da OpenHarmony

An gina CircleOS a kan OpenHarmony, wanda ke aikawa da silicon na NearLink (SLE) da `@kit.NearLinkKit` SDK a matsayin damar OS na aji na farko. A kan na'urorin CircleOS da HarmonyOS masu kayan aikin NearLink, ba a buƙatar kimantawa — `harmonyos/teal/` yana amfani da ainihin rediyon SLE kai tsaye:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Wannan ba wai kawai ingantacciyar sigar matakin daidaitacce ba ce. A matakin NearLink, cibiyar sadarwa ce ta daban gaba ɗaya:

| Damar aiki | Matakin daidaitacce (kimanta BLE) | Matakin na asali (CircleOS / OpenHarmony) |
|---|---|---|
| **Zango na NearLink** | ~100 m (BLE) | **600 m** |
| **Bandwidth na NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latsi na NearLink (latency)** | ~10 ms (BLE) | **20 µs** |
| **Wutar NearLink** | tushen BLE | **60% ƙasa da BLE 5.0** |
| **Abokan NearLink na lokaci ɗaya** | ~7 (iyakan haɗin BLE) | **500+** |
| **Tushen NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Ainihin rediyon SLE (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Na asali | Na asali (iri ɗaya) |
| **Tsaron Signal Protocol** | Cikakke | Cikakke (iri ɗaya) |
| **Zaɓen hanya / DTN / SOS** | Cikakke | Cikakke (iri ɗaya) |
| **Asalin Aether Tag** | Ana tallafawa | Ana tallafawa (iri ɗaya) |

---

### Motsawa tsakanin matakai

Ba a buƙatar wani canji na lamba. Ana ƙayyade matakin a lokacin aiki ta `IsAvailable` a kan kowace hidimar jigilar sadarwa:

1. A kan na'urar CircleOS ko HarmonyOS mai silicon na NearLink, `IsAvailable` a kan jigilar sadarwa na NearLink yana dawo da `true` (an bincika kayan aiki ta hanyar duba izini + gwajin bincike na wucewa).
2. `TransportManager` yana ɗaga NearLink zuwa matsayin fifiko ta atomatik — mafi ƙarancin tsadar wuta, mafi girman bandwidth.
3. Lambar manhaja, tsarin fakiti, algorithm na zaɓen hanya, matakin tsaro, da Aether Tags iri ɗaya ne a fadin matakai biyu.

Node a matakin daidaitacce da node a matakin na asali za su iya sadarwa cikin 'yanci — suna raba tsarin waya iri ɗaya, zaman Signal Protocol iri ɗaya, da Aether Tags iri ɗaya. Bambancin matakin yana shafar rediyon da ake amfani da shi don fakitin NearLink kaɗai, ba yarjejeniyar da ke sama da shi ba.

---

> **A ciki ana kiran waɗannan matakai da sunan bambancin Asterix (daidaitacce) da bambancin Obelix (na asali).** Asterix yana aiki da kyau da abin da ke akwai. Obelix — yana gudana a kan CircleOS tare da NearLink na asali — yana aiki a matakin da aka ɗaga na dindindin, kamar yadda Obelix ke ɗauke da ƙarfin maganin sihiri ba tare da buƙatar sha kuma ba.

---

## Aiwatarwa (Implementations)

An gina Aether da harsuna 8 don ya gudana a kan wayoyi, kwamfyutoci masu ɗauke, allunan hannu, da microcontrollers. Duk aiwatarwa suna samar da fakiti masu jituwa a kan waya — saƙon da node ɗin Rust ya ɓoye ana iya tura shi ta node ɗin Python kuma a warware shi ta node ɗin Swift.

| Harshe | Directory | Tsarin waya | Routing/DTN/SOS | X3DH | Double Ratchet | Tafkin OPK | Murya/Ƙungiya | Yaɗuwa/Bidiyo/Kallo |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Duk harsuna 8 suna samar da fakitin waya iri ɗaya baiti-da-baiti, an tabbatar ta 14 fixtures na tsarin waya na canonical da 4 vectors na gwajin Signal da ake gudanarwa a CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). An aiwatar da zaɓen hanya (nau'in AODV RREQ/RREP), ajiye-da-tura na DTN, watsa SOS, murya, yaɗuwa, da hidimomin ƙarfafa tsaro a cikin kowane harshe tare da **~3,000 gwaje-gwaje** a fadin dukkan aiwatarwa 8:

| Harshe | Gwaje-gwaje | Dandamalin CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Jimla** | **~3,000** | |

An ɗaure Signal interop tsakanin harsuna ga `fixtures/signal/` tare da vectors na gwaji da ake rabawa don X3DH (`x3dh_basic`), ratchet mai daidaituwa (`ratchet_step_basic`, `ratchet_step_three_iterations`), da KDF_RK (`kdf_rk_basic`). Kowace aiwatarwa dole ta samar da fitattu iri ɗaya baiti-da-baiti a kan waɗannan fixtures. Duk harsuna 8 yanzu suna aikawa da cikakken zaman Signal (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Bayan tsarin waya da Signal, **dukkan tarin hidimomin waya** — presence, heartbeat, daidaita bayanin martaba, sanarwar ephemeral-ID, musanya pre-key, channels, push-to-talk, raba allo, sarrafa kira, amincewa da SOS, alamomin sarari, sanarwar forge, buƙatar shard na vault, da aunawar bandwidth (duba **Abin da kake samu**) — haka nan an aiwatar da su a cikin dukkan harsuna 8 kuma an ɗaure su ga fixtures ɗinsu na kansu (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, da 'yan uwansu). Babu wani fasali da ke na C#-kaɗai a matakin yarjejeniya.

## Fara da sauri (Quickstart)

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Demo yana bi da kai ta matakai 8: samar da makullan asali na Ed25519 don node uku (Alice, Bob, Charlie), kafa zaman Signal Protocol, aika saƙonni da aka ɓoye, tura saƙo ta Charlie (wanda ba zai iya karanta shi ba), nuna tsarin waya na binary, da nuna sirrin gaba (forward secrecy) a fadin saƙonni 5 a jere. Fitarwa tana da launi kuma tana tsayawa tsakanin matakai.

**Aika saƙo a cikin C#:**

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

Demo yana samar da makullan asali don node biyu, yana musanya fakitin pre-key, yana kafa zaman da aka ɓoye, yana aika saƙonni da aka ɓoye a hanyoyi biyu, yana ƙirƙira da sa hannu a fakitin mesh, yana tabbatar da sa hannu, kuma yana zama fakiti zuwa tsarin waya na binary. Yana kuma nuna matakin jigilar sadarwa cikin tsari.

**Aika saƙo a cikin Rust:**

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

Demo yana ƙirƙira node biyu a cibiyar sadarwa da aka kwaikwayo, yana samar da makullan Ed25519, yana kafa zaman Signal Protocol, yana ƙirƙira da sa hannu a fakiti, yana zama shi zuwa tsarin binary mai jituwa da C#, yana ɓoye saƙon sirri, yana warware shi a ɗayan node, yana aika shi ta jigilar sadarwa, kuma yana tabbatar da zagayowar.

**Aika saƙo a cikin TypeScript:**

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

Demo yana gudanar da nune-nune 8: samar da makullan Ed25519 da gano canjawa, ƙirƙirar node tare da damar aiki, musanya makullin X3DH na Signal Protocol, ɓoyewa da warware AES-256-GCM, zama fakiti, sa hannu a fakiti tare da gano sake kunnawa, jigilar sadarwa cikin tsari, da cikakken kwararowa ƙarshe-zuwa-ƙarshe da ke haɗa dukkan matakai.

**Aika saƙo a cikin Python:**

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

Demo yana gudanar da nune-nune 5: zagayowar zama fakiti, sa hannu na Ed25519 tare da gano canjawa, kafa zaman Signal Protocol tare da saƙonni da aka ɓoye a hanyoyi biyu, jigilar sadarwa cikin tsari tsakanin abokai biyu, da kawar da maimaituwar nonce don kariyar sake kunnawa.

**Aika saƙo a cikin Go:**

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

Demo yana bi ta matakai 11: samar da makulli, ƙirƙirar node tare da damar aiki, ƙaddamar da Signal Protocol, musanya fakitin pre-key, kafa zama, ƙirƙira da sa hannu a fakiti, zama, warwarewa tare da tabbatar da sa hannu, ɓoyewa ƙarshe-zuwa-ƙarshe tare da juyawar makulli, gano harin sake kunnawa, da jigilar sadarwa cikin tsari.

**Aika saƙo a cikin Kotlin:**

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

Demo yana gudanar da gwaje-gwaje 5: zagayowar zama fakiti, sa hannu na Ed25519 tare da ƙin canjawa, kafa zaman Signal Protocol tare da ɓoyewa na AES-256-GCM, isar da saƙon jigilar sadarwa cikin tsari, da cikakken kwararowa ƙarshe-zuwa-ƙarshe inda Alice ta sa hannu a fakiti kuma Bob ya tabbatar da shi bayan jigilar sadarwa.

**Aika saƙo a cikin Swift:**

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

Demo yana gudanar da nune-nune 7: samar da makullan Ed25519, ƙirƙira da sa hannu a fakiti, zama zuwa tsarin waya na binary, warwarewa tare da bincike na mutunci, ɓoyewa da warware AES-256-GCM, tabbatar da saƙo na HMAC-SHA256, da samun makulli na HKDF-SHA256.

**Aika saƙo a cikin C:**

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

## Taswirar hanya (Roadmap)

Abin da aka gina da abin da ke gaba.

**An gama (an tabbatar tsakanin harsuna, dukkan aiwatarwa 8):**
- Tsarin waya: iri ɗaya baiti-da-baiti a fadin harsuna 8, an ɗaure ta 14 fixtures na canonical da tabbatarwar tsakanin harsuna a CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — matrix na ayyuka 9 (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, tare da aikin mutuncin fixture) a cikin `.github/workflows/ci.yml`.
- Sa hannu da tabbatar da fakiti na Ed25519
- Ɓoyewa na AES-256-GCM
- Muhimman abubuwan samun makulli na HKDF / HMAC
- Zama fakiti + shirin sa hannu (LE + filaye na int32 na baiti 4)
- Mai kwaikwayon jigilar sadarwa cikin tsari (don haɓakawa da gwaje-gwaje)
- Hidimar zaɓen hanya mai wahayi daga AODV tare da RREQ/RREP, amsoshin hanya masu sa hannu, kawar da maimaituwa, tura TTL
- Hidimar ajiye-da-tura na DTN tare da canja wurin kula, kwafi mai sanin geohash, TTL na sa'o'i 72
- Hidimar watsa SOS tare da ambaliya, kawar da maimaituwa, kariyar asalin-kai, iyaka-yawan (3/awa)
- Wuraren haɗa faɗaɗawa: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (tsoffin Noop)
- **~3,000 gwaje-gwaje** a fadin dukkan harsuna 8 (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — duk kore a CI
- ✅ **Ainihin makullin wucin gadi na X3DH (harsuna 8)** — 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) tare da samun tushen HKDF-SHA256. An ɗaure ta `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Daidaituwar Double Ratchet a duk iyali** — cikakken Signal §5 tare da HMAC-SHA256 + 0x01/0x02 rarraba yanki a cikin ratchet mai daidaituwa, HKDF-SHA256 KDF_RK a matakin DH-ratchet, juyawar DH a lokacin karɓa. An tabbatar ta `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` fixtures.
- ✅ **An daidaita PROTOCOL_SPEC §2 / §3 / §4 / §9 da HEAD** — duba `docs/PROTOCOL_SPEC.md`.

**An gama (dukkan harsuna 8):**
- ✅ **Kiran murya (1-zuwa-1)** — injin yanayin sigina (Offer/Answer/Hangup/Cancel/Timeout) + jigilar firaman binary (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Isarwa mai sanin hanya ta `IRoutingService`.
- ✅ **Muryar ƙungiya** — memba mai jagorancin runduna (gayyata/kori/bar), filin samar da makulli na kowace firam, watsawa ta unicast zuwa dukkan membobin yanzu, juyawar makulli mai sarrafa runduna a lokacin canjin memba.
- ✅ **Yaɗuwa kai tsaye (Live streaming)** — mai bugawa yana watsa `StreamAnnounce`; masu biyan kuɗi suna aika `StreamSubscribe`; firaman `StreamSegment` na binary (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast zuwa kowane mai biyan kuɗi.
- ✅ **Kiran bidiyo (1-zuwa-1)** — tattaunawar codec/ƙuduri/fps/bitrate a cikin sigina, siginar buƙatar keyframe da canjin inganci, tsarin `VideoFrame` na binary da ke daidaita da shirin murya.
- ✅ **Kallo Tare (Watch Together)** — runduna tana fitar da umarnin `WatchSync` (kunnawa/tsayawa/tsallakewa/gudu) mai iko; mabiya suna aiwatarwa tare da rama RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` na kunna-ka-manta.
- ✅ **Tafkin makullan gaba na lokaci ɗaya (OPK)** — tsoho 100, fitar da FIFO, cikawa mai jinkiri, cinyewa mai kariyar kulle a fadin dukkan harsuna 8. Yana rufe haɗarin haɗin OPK ɗaya.
- ✅ **C: cikakken zaman Signal** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` a cikin `c/src/signal_protocol.c`; 6 gwaje-gwajen E2E na node-biyu a cikin `c/tests/test_signal_session.c`. Duk harsuna 8 yanzu suna da Signal Protocol mai iya zama cikakke.

**An gama (dukkan harsuna 8 — dukkan tarin hidimomin waya):**
- ✅ **Kowace irin fakiti da aka ajiye yanzu ta zama ainihin hidima iri ɗaya baiti-da-baiti a cikin dukkan harsuna 8.** Fitilar kasancewa/tambaya (21/22), heartbeat (10), daidaita bayanin martaba (23), sanarwar ephemeral-routing-ID (56), musanya pre-key (25/26), channels (7), push-to-talk (15), raba allo (32), sarrafa kira (27), amincewa da SOS (6), alamomin sarari (40), sanarwar forge (41), buƙatar shard na vault (42), da aunawar bandwidth / ABMF (53/54/55). Kowanne hidima ce siriri (samar + sarrafa + abin faruwa) da runduna ke haɗawa zuwa zamanta na Signal da teburin zaɓen hanya; an ɗaure kowanne ga fixture da ake rabawa tsakanin harsuna (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) kuma ana gwada shi ta gwaje-gwajen naúra na kowane harshe, tare da tabbatar da Swift da C a kan sabar gina ta macOS. Duba **Abin da kake samu**.

**An gama (na tunani na C# kaɗai):**
- ✅ **Demo Mataki 9 — MessagingService + DTN fallback ƙarshe-zuwa-ƙarshe** — `samples/AetherNet.Demo.Console` yana bi ta saƙonnin da aka ɓoye da ainihin Signal tare da ajiye-da-tura na DTN lokacin da mai karɓa ba ya kan layi.
- ✅ **Gadar `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` yana sa matakin saƙonni ya zama an ɓoye ƙarshe-zuwa-ƙarshe ta tsohuwa; saƙonnin da ba su da zaman Signal ana jera su, ba a taɓa aika su cikin rashin tsaro ba.
- ✅ **Yaɗuwa mai daidaita bitrate** — `AdaptiveBitrateController` tare da matakan bitrate da spec ya wajabta don Profile A (na lokaci-na-gaske), B (watsa kai tsaye), da C (VOD). Mai bugawa yana zaɓar mataki mafi girma mai ɗorewa (kaso 20% na sarari) kuma yana fitar da `StreamAbandon` (`PacketType.StreamAbandon`) maimakon wani sashe lokacin da ke ƙasa da bene. `IStreamingService` yana fallasa `UpdateBandwidthEstimate` da `GetCurrentBitrateRung`.
- ✅ **Kallo Tare: shigar da BitTorrent + tallafin ƙungiyar ChipIn** — samfuran `TorrentInfo` / `TorrentFile`; `WatchTogetherService` yana sarrafa `PacketType.TorrentMetadata` kuma yana kunna `TorrentReceived`. Injin yanayin `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` a kan `IWatchTogetherService`.
- ✅ **Kiran bidiyo na ƙungiya tare da tura SFU ta atomatik** — `GroupVideoService` / `IGroupVideoService`. Topology na FullMesh don ≤ 3 mahalarta; canja ta atomatik zuwa SFU a `SfuThresholdParticipants` (4) tare da sake ba da aikin relay ta `GroupVideoSignaling(SfuAssigned)`. Watsawa a FullMesh, aikawa ta relay-kaɗai a yanayin SFU. Nau'in fakitin sigina `GroupVideoSignaling = 35`.
- ✅ **Kwaikwayon jigilar sadarwa na BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Firaman MTU na GATT ta `BleGattFramer` (1024 B/firam, `[2B count][2B index][payload]`), rijistar abokan tarayya na tsaye cikin tsari, watsa talla. Duk ƙuntatawar `BleMaxPayloadBytes` an tilasta su.
- ✅ **Kwaikwayon jigilar sadarwa na Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Zagayowar `ConnectAsync`/`DisconnectAsync` bayyananna, isar da manyan nauyi kai tsaye (babu firam), abubuwan `PeerConnected`/`PeerDisconnected` na hanyoyi biyu.
- ✅ **Kwaikwayon jigilar sadarwa na NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU na firam na 4096 B, rijistar abokai 500, `ConnectedPeerCount`, `IsAvailable` mai saitawa a lokacin aiki.
- ✅ **Gwaje-gwajen kwaikwayon kunna RF** — gwaje-gwajen interop na node-biyu (`SimulatedTransportTests`): zagayowar `MeshPacket` na BLE + NearLink, canja wurin nauyin 64 KB na WiFi Direct. An tabbatar da matakin software gaba ɗaya; ana buƙatar zaman lab na na'ura ta zahiri don tabbatarwa a kan kayan aiki.

**An gama (matakin jigilar sadarwa na C# — duk fail-fast):**
- ✅ **Ainihin jigilar sadarwa na BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (sabar GATT na Android). Cikakken gwajin kunna RF a cikin `samples/AetherNet.BleRfTest/`.
- ✅ **Ainihin jigilar sadarwa na Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Gwajin RF a cikin `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Jigilar sadarwa na HTTP relay (Aether Purple)** — `HttpRelayTransportService` tare da long-poll na daƙiƙa 10, `PowerCostRelative = 100`, koyaushe mafita ta ƙarshe. Sabar relay a cikin `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). Gwajin RF a cikin `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` yana aiwatar da `HostApduService` tare da AID `F061657468657200`. `WinNfcStubTransportService` yana rubuta hanyoyin kimanta Windows guda biyu: (1) NDEF-over-BLE-GATT tare da ƙofar RSSI ≥ −40 dBm (yana kwaikwayon danna-don-haɗawa ba tare da silicon na NFC ba, `IsAvailable = Bluetooth present`); (2) ACR122U USB reader ta `Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). Hanyar haɓakawa: aiwatar da `ITransportService` lokacin da Microsoft ya aika da API na P2P NFC na farko.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — cikakken aiwatarwar ArkTS na HarmonyOS 5.0.1 (API 13) ta amfani da `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); ana bincika `isAvailable` a lokacin aiki. `WinNearLinkStubTransportService` + `android/teal/` suna rubuta kimanta SSAP-over-BLE: BLE GATT tare da Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — kwatankwacin API da SSAP, ba mai jituwa a kan waya da ainihin kayan aikin NearLink ba. Hanyar haɓakawa: maye gurbin kiran BLE GATT da kiran SDK na `ssapc_*`/`ssaps_*`; UUIDs da wurin `TransportManager` ba a canza su.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` suna rubuta kimanta Meshtastic-over-BLE-LR: cikakken tsarin waya na Meshtastic (kai na baiti 16 + protobuf na AES-256-CTR) a kan BLE 5.0 Coded PHY S=8 (~1.3 km waje), tare da zaɓen hanya na managed-flood da tagar takara mai nauyi na RSSI. Haɗin gwiwar node-gada da ainihin kayan aikin LoRa yana aiki ta atomatik (tsarin fakitin Meshtastic iri ɗaya, babu fassara). Hanyar haɓakawa: maye gurbin rediyon BLE LR da direba na SX1276/SX1278 AT-command ko SPI; tsarin fakiti da zaɓen hanya ba a canza su.

**A buɗe — ana bibiyar sa a cikin `OPEN_ISSUES.md`:**
- Kunna RF a kan ainihin kayan aiki: gwajin interop na ƙarshe-zuwa-ƙarshe na node-biyu a kan na'urorin BLE / Wi-Fi Direct na zahiri (gwaje-gwajen kwaikwayo suna wucewa; ana buƙatar zaman lab na kayan aiki)
- NearLink: `harmonyos/teal/` cikakke; yana buƙatar kayan aikin Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silicon na NearLink ba ya kan na'urorin da ba na Huawei ba). Windows + Android suna komawa ga kimanta SSAP-over-BLE ta atomatik.
- LoRa / CircleLink: ana buƙatar module na rediyo don ainihin zango na LoRa. Ba tare da ɗaya ba, ana ɗaukar tsarin waya na Meshtastic a kan BLE LR (~1.3 km) kuma haɗin gwiwar node-gada da ainihin kayan aikin LoRa yana samuwa.
- ✅ **(AN WARWARE v1.2.0)** Fuskar yarjejeniyar mabukaci (Wave 16/17) — abin faruwa na `IDtnService.BundleReceived` don fakitocin shigowa ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), directory na sanya suna/gano na matakin-manhaja ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), fuskar ba-da-tip ga marubuci ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Duk 3 an aika su ta ƙari a fadin harsuna 8 tare da fixtures na tsakanin harsuna iri ɗaya baiti-da-baiti. Duba CHANGELOG.

**Har yanzu ba a buɗe don gudummawar waje ba:**
- Yarjejeniyar har yanzu tana ƙarƙashin ci gaba mai aiki. Ba a karɓar gudummawar waje a wannan lokacin.
- Aiwatar da jigilar sadarwa na NearLink, misalan haɗin Android/iOS, ƙarin backends na jigilar sadarwa, ma'aunin aiki, da fuzzing na yarjejeniya ana bibiyar su a ciki kuma za a buɗe su lokacin da aikin ya kai wani wuri na gudummawar jama'a mai ƙarko.

## Tsarin Aiki (Project Structure)

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

## Ƙara Sabuwar Jigilar Sadarwa (Adding a New Transport)

Aiwatar da `ITransportService`:

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

Ka yi rajistar sa a cikin DI kuma `TransportManager` zai haɗa shi ta atomatik cikin zaɓen jigilar sadarwa, an tsara shi bisa tsadar wuta.

## Yadda Yake Kwatanta (How It Compares)

| Yarjejeniya | Iyaka | Fa'idar Aether |
|----------|-----------|-----------------|
| **Briar** | Android-kaɗai, mai dogaro da Tor | Fadin dandamali, mesh mai tsafta |
| **Meshtastic** | LoRa kaɗai (30 kbps mafi girma) | Jigilar sadarwa da yawa (BLE + WiFi + NearLink), mai iya murya da yaɗuwa |
| **Reticulum** | Python, ƙaramar al'umma | Harsuna 8, masu jituwa a kan waya a fadin dukkansu |
| **libp2p** | Yana ɗauka akwai kashin bayan intanet | Rashin-layi-na-farko, yana aiki da babu kayan more rayuwa |
| **Yggdrasil** | Cibiyar sadarwa ta rufi, yana buƙatar intanet | Mesh na matakin-zahiri, yana aiki ba tare da intanet ba |
| **Signal** | Babu mesh, yana buƙatar intanet | Yana aiki ba tare da layi ba, P2P, mesh relay, ɓoyewa iri ɗaya na E2E |

## Tambayoyin da ake yawan yi (Frequently asked questions)

**Shin AetherNet yana aiki ba tare da intanet ba?**
Eh — yana ba da fifiko ga aiki ba tare da layi ba. Na'urori suna magana kai tsaye ta Bluetooth, Wi-Fi Direct, NearLink, ko LoRa kuma suna tsallake saƙonni mataki-da-mataki ta wasu na'urori, ba tare da buƙatar haɗin intanet, hasumiyar sadarwa, ko sabar ba. Lokacin da babu hanya mai aiki, ana riƙe saƙonni (ajiye-da-tura mai jurewa jinkiri) har sa'o'i 72 har sai ɗaya ya buɗe.

**Shin an ɓoye shi ƙarshe-zuwa-ƙarshe?**
Eh. AetherNet yana amfani da Signal Protocol (yarjejeniyar makulli na X3DH tare da Double Ratchet a kan X25519) don ɓoyewa ƙarshe-zuwa-ƙarshe, AES-256-GCM don nauyin saƙonni, da sa hannu na Ed25519 a kan kowace fakiti. Na'urorin da ke tura saƙo ba za su iya karanta shi ba.

**Wadanne jigilar sadarwa yake amfani da su?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), rediyon serial na LoRa/CircleLink, HTTP/QUIC relay, da WebRTC don intanet kai tsaye na peer-to-peer. Yarjejeniyar tana zaɓar jigilar sadarwa mafi ƙarancin wuta da ke akwai ta atomatik ga kowace fakiti kuma tana komawa ga na gaba.

**A wadanne harsunan shirye-shirye ake samun sa?**
Takwas — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, da C. Kowace aiwatarwa tana samar da fakitin waya iri ɗaya baiti-da-baiti, wanda aka tabbatar ta hanyar rukunin gwaji (fixture corpus) da ake rabawa tsakanin harsuna a CI, don haka fakitin da harshe ɗaya ya gina ana warware shi ba tare da canji ba ta kowanne.

**Ta yaya ya bambanta da Meshtastic, Briar, ko Bridgefy?**
Meshtastic LoRa-kaɗai ne; AetherNet jigilar sadarwa da yawa ne (Bluetooth + Wi-Fi + NearLink + LoRa) kuma yana ɗauke da murya, bidiyo, da yaɗuwa gami da saƙonni. Briar Android-kaɗai ne kuma yana zaɓen hanya ta Tor; AetherNet fadin dandamali ne kuma mesh mai tsafta. Ba kamar SDKs da aka rufe ba, AetherNet mai lasisin MIT ne kuma an aiwatar da shi a fili a cikin harsuna takwas. Teburin kwatanci na sama yana da cikakkun bayanai.

**Shin a shirye yake don samarwa (production)?**
Matakin yarjejeniya — tsarin waya, tsaron Signal, zaɓen hanya, ajiye-da-tura na DTN, da dukkan tarin hidimomi — an aiwatar da su kuma an gwada su a fadin dukkan harsuna takwas. Jigilar sadarwa na rediyo na gaskiya ne inda lambar dandamali ta wanzu (Bluetooth da Wi-Fi a Windows da Android, WebRTC ko'ina) kuma ba a tabbatar da su a fagen aiki ba a wani wuri ana jiran kunna kayan aiki, wanda ake bibiyar sa cikin gaskiya a `OPEN_ISSUES.md`. Ka karanta bayanan matsayi a cikin kowane sashe kafin turawa.

**Wane lasisi ne yake ƙarƙashinsa?**
MIT — kyauta don amfanin kasuwanci da na buɗaɗɗen tushe. Duba [LICENSE](LICENSE).

**Wa ke gina AetherNet?**
An haɓaka shi a matsayin buɗaɗɗiyar yarjejeniyar da ke bayan tsarin mesh na The Geek Network, an gina shi a Afirka ta Kudu don sadarwar da ke aiki tare da ko ba tare da bayanan wayar salula ba.

## Wuraren Faɗaɗawa (Extension Points)

Yarjejeniyar tana aiki da kanta. Waɗannan fuskoki suna ba ka damar haɗa backend naka idan kana son ɗaya:

- `IAetherNetIncentiveProvider` — sakawa nodes da ke tura zirga-zirga (tsohuwar no-op: tura mai son kai)
- `IAetherNetBackendClient` — daidaita da sabar lokacin da intanet ke akwai (tsohuwar no-op: gaba ɗaya ba tare da layi ba)
- `IAetherNetFeatureFlagProvider` — kunna/kashe fasalulukan yarjejeniya a lokacin aiki (tsohuwar no-op: an kunna komai)

Duk uku suna aikawa da aiwatarwar no-op. Ka cire su kuma babu abin da zai lalace.

## Ba da Gudummawa (Contributing)

Gudummawar waje ba a buɗe tukuna. Aikin har yanzu yana ƙarƙashin ci gaba mai aiki. Ka duba baya lokacin da muka sanar da tagar gudummawar jama'a.

## Tsaro (Security)

Duba [SECURITY.md](SECURITY.md) don manufar bayyanawa mai alhaki.

## Lasisi (License)

Lasisin MIT. Duba [LICENSE](LICENSE).

## Fassarori (Translations)

Ana kuma kula da wannan README a cikin sauran harsunan da aka lissafa a mashaya harshe a saman wannan fayil, a ƙarƙashin [`docs/i18n/`](docs/i18n/) — yana ratsa harsunan Turai, Gabashin Asiya, Gabas ta Tsakiya, Kudancin Asiya, Kudu-maso-gabashin Asiya, da na Afirka, saboda cibiyar sadarwa da aka gina don mutanen da ba su da data bai kamata ta kasance da ƙofa ta gaba wadda masu haɗin gwiwa mai kyau kaɗai za su iya karantawa ba. **Sigar Turanci ita ce tushen gaskiya**: inda fassara da rubutun Turanci suka saɓa, rubutun Turanci shi ne mai iko, kuma fassarori na iya lakawa da shi ta fitowa ɗaya ko biyu. Yarjejeniya, lamba, fixtures, da halayen da aka bayyana iri ɗaya ne komai harshen da kake karantawa.
