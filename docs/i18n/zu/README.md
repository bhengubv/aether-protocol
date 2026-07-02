# AetherNet — iphrothokholi ye-mesh networking eqale nge-offline

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**I-AetherNet iyiphrothokholi ye-mesh networking evulekile (open-source), enelayisensi ye-MIT** yokuthumela imilayezo, amafayela, izwi, kanye nevidiyo kubantu abaseduze — **ngaphandle kwe-inthanethi, ngaphandle kwamaseva, futhi ngaphandle kokubhalisa**. Amadivayisi axhumana ngokuqondile nge-Bluetooth, Wi-Fi Direct, NearLink, kanye ne-LoRa; uma umamukeli engaphandle kwebanga, imilayezo iyaqhasha idlule kwamanye amadivayisi bese ilinda kuze kube amahora angama-72 ukuthola indlela. Ithumela **izenzo ezifana ibhayithi ngebhayithi ngezilimi zokuhlela eziyisishiyagalombili** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, kanye no-C.

Yabelana ngamafayela, imilayezo, kanye nezikwele (streams) nabantu abaseduze. Akukho WiFi. Akukho idatha yeselula. Akukho ukubhalisa. Njenge-AirDrop, kuphela ukuthi kusebenza nawo wonke umuntu, kuwo wonke uhlelo (platform).

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Iphrothokholi eyodwa, izilimi eziyisishiyagalombili, efanayo ncamashi ku-wire.** I-Aether yakhiwe ngo-**C#, Rust, TypeScript, Python, Go, Kotlin, Swift, kanye no-C** — futhi lonke iphakethe (packet) liyafana ibhayithi nebhayithi kuzo zonke lezi zilimi, kuqinisekiswa yi-corpus yezifixture ezisatshalaliswa phakathi kwezilimi ku-CI. Yakha inodi yakho nganoma iyiphi kwezisishiyagalombili; iyasebenzisana nazo zonke ezinye. Le README iyatholakala nasezilimini zabantu eziyi-11 (izixhumanisi zingenhla).

## Yini ongayenza ngayo?

**Yabelana ngamanothi ekhilasi ungachithi idatha.**

Usesiqoqweni sokufunda. Kukhona onephepha lakudala aliphephile efonini yakhe. I-Aether iwathumela ngokuqondile edivayisini yakho nge-Bluetooth — akukho hotspot, akukho iqembu le-WhatsApp, akukho umkhawulo wobukhulu befayela. Uma othile eqenjini engaphandle kwebanga, ifayela liyaqhasha lidlule kwamanye amadivayisi kuze kufike kuye. Imilayezo ilinda kuze kube amahora angama-72 ukuthola indlela uma kudingeka.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Thola ukuthi kwenzekani eduze kwakho.**

Usemcimbini wasekhampasi noma emgubhweni. I-Aether ithola amanye amadivayisi aseduze nge-Bluetooth kanye ne-WiFi Direct — akukho ifidi ye-app, akukho i-algorithm. Ubona lokho okukhona ngempela eduze kwakho, hhayi lokho okukhangiswayo.

**Thumela i-SOS uma kungekho isignali.**

Ifoni yakho ayinakwamukela signali. I-Aether isakaza umyalezo waphuthuma kuwo wonke amadivayisi asebangeni, futhi lawo madivayisi ayawudlulisela. Akukho mbhoshongo weselula odingekayo.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Dala amashaneli ayimfihlo eqembu.**

Ishaneli lesitezi sakho sendawo yokuhlala, sombutho wakho, noma sithimba sephrojekthi yakho. Yizinhlaka eziqinisekisiwe kuphela ezingafunda noma zithumele imilayezo. Akukho iseva egcina ingxoxo.

**Thengisa izinto kubantu abaseduze.**

Faka incwadi yesikole ukuze ithengiswe. Abantu abahamba abasebangeni le-mesh bayayibona. Akukho i-akhawunti ye-marketplace, akukho izimali zokufaka uhlu — nje ukusondelana.

**Bukelani ifilimu ndawonye, kuyo yonke i-mesh.**

Iqembu lakho linobusuku bamafilimu. Othile unefayela. I-Aether ivumelanisa ukudlala kuwo wonke amadivayisi — dlala, misa, seka — konke ngesikhathi esisodwa. Uma abanye abantu kuphela abanefayela, i-mesh iyalisabalalisa ngesikhathi sangempela njenge-P2P stream. Wonke umuntu ufaka isandla nge-SDPKT ukuze bayithenge uma kungekho muntu onayo.

## Isebenza kanjani

Amadivayisi akhulumisana ngokuqondile esebenzisa i-Bluetooth, i-WiFi Direct, noma i-NearLink. Akukho ukuxhumana kwe-inthanethi, akukho iseva, akukho ingqalasizinda emaphakathi.

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

Uma umyalezo ungakwazi ukufinyelela lapho uya khona ngokuqondile, uyaqhasha udlule kwamanye amadivayisi. Lawo madivayisi adlulisayo awakwazi ukufunda lokho awaphethe — wonke umyalezo ubethelwe (encrypted) nge-AES-256-GCM. Lonke iphakethe lisayinwa ngokhiye bobunikazi be-Ed25519, futhi amaphakethe amanga alahlwa yinethiwekhi.

> **Inothi ngokuvuthwa kwezokuphepha (funda ngaphambi kokuthumela):** I-X3DH yangempela (ama-X25519 DHs amane), i-Signal Double Ratchet egcwele (isinyathelo se-DH-rotation ekwamukeleni, i-KDF_RK, i-0x01/0x02 chain ratchet), kanye ne-one-time pre-key pool (okuzenzakalelayo yi-100 OPKs, i-FIFO, ivikelwe nge-lock) sekwenziwe **kuzo zonke izilimi eziyi-8** futhi kuboshelwe ku-corpus yezifixture ezisatshalaliswa phakathi kwezilimi ngaphansi kwe-`fixtures/signal/`. Into eyodwa esele evuliwe yi-physical RF bring-up ku-hardware ye-BLE yangempela (ilandelelwa ku-`OPEN_ISSUES.md`).

Akukho ma-akhawunti, akukho izinombolo zocingo, akukho ama-imeyili. Ukhiqiza i-keypair bese usenethiwekhini.

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

**Ukwedlulisa (Routing)** — I-AODV enezimpendulo zendlela ezisayiniwe. Yonke impendulo yendlela isayinwa ngokhiye we-Ed25519 walapho iya khona, ngakho akukho divayisi engazenza sengathi iyilapho iya khona kanti akunjalo.

**Ukugcina-nokudlulisela (Store-and-forward)** — Uma kungekho ndlela ephilayo, amaphakethe abanjelwa kuze kube amahora angama-72 kuze kuvuleke indlela.

**Ukukhethwa kwe-transport** — Iphrothokholi ikhetha i-transport efanele iphakethe ngephakethe. Imilayezo yokulawula emincane ihamba nge-BLE. Ukudlulisa okukhulu kusebenzisa i-WiFi Direct. I-NearLink lapho itholakala.

**Izwi, ividiyo, kanye ne-streaming** — Amakholi evidiyo anokuxoxisana kwe-codec (H.264/H.265/VP8), ukukhethwa kwekhwalithi okuqaphela i-transport, ividiyo yeqembu ene-auto SFU relay, ukubukela-ndawonye okuvumelanisiwe ne-RTT compensation, kanye ne-adaptive bitrate streaming.

**Ukuvikela ku-replay** — I-Nonce deduplication nge-window yobusha be-timestamp yemizuzu emi-5.

## Okutholayo — yonke insiza, kuzo zonke izilimi

I-Aether ayiyona nje i-transport. Zonke izinhlobo zephakethe ezigodliwe iphrothokholi manje sekuyinsiza **eyangempela, esebenzayo kuzo zonke izilimi eziyi-8**, futhi zonke ziserialize zibe **amaphakethe e-wire afana ibhayithi ngebhayithi** — iphakethe elakhiwe yinodi ye-Go liyahluzwa, lingashintshiwe, yinodi ye-Swift, Rust, C, Python, TypeScript, Kotlin, noma C#. Insiza ngayinye iboshelwe ku-fixture esatshalaliswa phakathi kwezilimi ngaphansi kwe-`fixtures/<service>/` futhi ivivinywa ngezivivinyo zeyunithi ngazinye zezilimi, kanti i-Swift ne-C ngaphezu kwalokho ziqinisekiswa ku-macOS build server.

| Ikhono | Kwenzani | Uhlobo(izinhlobo) lwephakethe | I-Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **I-Presence beacon & query** | Yazisa "Ngilapha" bese ubuza "ubani oseduze?" — ngesimo se-**rotating, key-derived ephemeral ID** (hhayi ubunikazi bakho bangempela) kanye ne-geohash engacatshangelwe kahle | 21, 22 | `fixtures/presence/` | ✅ |
| **I-Heartbeat** | I-liveness keep-alive elula phakathi kwezinhlaka ezixhunyiwe | 10 | `fixtures/heartbeat/` | ✅ |
| **I-Profile sync** | Shintshanisa ikhadi lephrofayela elisayiniwe nozakwenu kuyo i-mesh | 23 | `fixtures/profiles/` | ✅ |
| **I-Ephemeral-ID announce** | Tshela umngane wakho ngasese i-rotating routing ID yakho yamanje ukuze akwazi ukukufinyelela ngisho nangemva kokuba ijikeleze | 56 | `fixtures/erid/` | ✅ |
| **I-Pre-key exchange** | Cela bese ulethe i-Signal pre-key bundle kuyo i-mesh, ukuze uqale iseshini ye-end-to-end nomuntu ongakaze umhlangabeze | 25, 26 | `fixtures/prekey/` | ✅ |
| **Amashaneli (Channels)** | Imilayezo esayiniwe eya kushaneli eliyimfihlo, elezinhlaka kuphela | 7 | `fixtures/channels/` | ✅ |
| **I-Push-to-talk** | Amafreyimu ezwi e-walkie-talkie (i-payload yomsindo obhaliwe engabonakali) | 15 | `fixtures/media/` | ✅ |
| **Ukwabelana ngesikrini** | Amafreyimu evidiyo yokwabelana ngesikrini (i-payload yevidiyo ebhaliwe engabonakali) | 32 | `fixtures/media/` | ✅ |
| **Ukulawula amakholi** | Ukusayina okwe-Ring / accept / decline / hang-up okwamakholi ezwi kanye nevidiyo | 27 | `fixtures/videocall/` | ✅ |
| **Ukuqinisekiswa kwe-SOS** | Qinisekisa kumthumeli ukuthi ukusakaza kwakhe kwaphuthuma kwamukelwa | 6 | `fixtures/sos/` | ✅ |
| **I-Space breadcrumbs** | Ama-discovery crumbs anelebula lendawo ohlwini lwe-"okuseduze kwami" | 40 | `fixtures/space/` | ✅ |
| **I-Forge announce** | Khangisa i-artefact yokuqukethwe okuvela/okwenziwe kuyo i-mesh | 41 | `fixtures/forge/` | ✅ |
| **Isicelo se-Vault shard** | Landa i-erasure-coded storage shard (noma iyiphi i-K ye-N shards yakha kabusha ifayela) | 42 | `fixtures/vaultshard/` | ✅ |
| **Ukukala i-Bandwidth** | I-Probe / ack / gossip ye-throughput yesixhumanisi ukuze i-mesh idlule kwipayipi enonile kunazo zonke (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Lezi zihlala phezu kwezinsiza esezivele ziphelele **ze-messaging, izwi le-1-to-1 nele-qembu, amakholi evidiyo, i-live streaming, ukubukela-ndawonye, i-AODV routing, i-DTN store-and-forward, kanye ne-SOS flood** — nazo ezenziwe kuzo zonke izilimi eziyi-8.

> **Ukuthi "kwakhiwe" kusho ukuthini lapha, ngokunembile.** Insiza ngayinye ikhiqiza futhi iphathe iphakethe layo le-wire, iphakamise imicimbi efanele, futhi iboshelwe ku-fixture yezinga lebhayithi okumele umndeni wonke wezilimi uyifice. I-application yakho ixhuma insiza ku-Signal session yayo, ku-routing table, kanye nesimo sasendaweni. Lena yingqimba yephrothokholi — efakazelwe kukhodi, ezivivinyweni, kanye nakuma-byte-fixtures phakathi kwezilimi — kusisekelo se-RF esifanayo esiqotho nakho konke okunye: noma iyiphi indlela ekugcineni ehamba ku-radio ayikaqinisekiswa ensimini kuze kube i-hardware bring-up elandelelwa ku-`OPEN_ISSUES.md`.

## Ama-Transport

I-transport ngayinye inegama lombala elisetshenziswa kuyo yonke i-codebase. I-`IsAvailable` ivala izindlela ezivinjelwe yi-hardware — i-`TransportManager` iyazeqa bese ibuyela ku-transport elandelayo etholakalayo.

**Ukhiye wesimo:** ✅ yangempela, yakhiwe futhi yaqinisekiswa · ⏳ yangempela, ukuqinisekiswa kusaqhubeka · ⚠️ yangempela kwezinye izinhlelo, i-stub kwezinye · ❌ i-stub (ayikho ikhodi ye-transport okwamanje).

| Umbala | Igama | Ibanga | I-Bandwidth | Isimo |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Yangempela — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Yangempela — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | Engenamkhawulo | ~10 Mbps | ✅ Yangempela — Windows; i-relay server ku-`samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | I-inthanethi data channel | Engenamkhawulo | ~100 Mbps | ✅ Yangempela kuzo zonke izilimi eziyi-8 — **iqinisekiswe nge-loopback kuzo zonke eziyi-8** (i-C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust ngayinye inezinhlaka ezimbili ezishintshanisa amabhayithi ku-ICE data channel yangempela) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Yangempela ku-Android (`android/white/`); Windows = i-BLE-GATT yangempela + i-RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`, iyahlanganisa net9/10, i-runtime ayikaqinisekiswa) — i-`Windows.Networking.Proximity` yasuswa ku-Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Yangempela ku-HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — ukuqinisekiswa kudivayisi kusalindile); Android + Windows = i-SSAP-over-BLE approximation yangempela (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; kuqinisekiswe ukuhlanganisa + isivivinyo seyunithi, i-runtime ayikaqinisekiswa) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ I-RYLR SX127x/SX126x serial driver yangempela (`LoRaSerialTransport` ku-C#/Go/Rust/C; iyahlanganisa, i-runtime ayikaqinisekiswa — idinga imojuli ephathekayo); i-BLE Coded-PHY bridge isengumklamo obhaliwe |

Ama-radio transport angempela kuphela lapho ikhodi yohlelo ikhona (C#/Windows, Kotlin/Android, HarmonyOS). Amalabhulali ezilimi eziyisishiyagalombili ngale kwalokho athumela i-transport **ye-in-process simulation** yokuhlola — **i-WebRTC iyi-transport yangempela yokuqala evamile kuwo wonke** (iphelele; iqinisekiswe nge-loopback kuzo zonke izilimi).

Ukubaluleka kususelwa kuzindleko zamandla: i-radio mesh ikhethwa kakhulu, bese kulandela i-WebRTC njengendlela eqondile ye-inthanethi, ne-HTTP/QUIC relay njengesixazululo sokugcina.

## Amazinga okuthumela (Deployment tiers)

I-Aether isebenza kunoma yiluphi uhlelo olusekela i-Bluetooth noma i-Wi-Fi. Izinga okulo lincike ku-OS oyihlosile.

---

### Izinga elijwayelekile (Standard tier) — noma yiluphi uhlelo

Android · Windows · Linux · macOS · iOS

I-Aether isebenza kunoma iyiphi idivayisi ene-Bluetooth noma i-hardware ye-Wi-Fi. Lapho i-radio ingekho ngokomzimba, i-transport ngayinye evinjelwe ilinganiswa ngokusetshenziswa kwalokho okutholakalayo. Lokhu kulinganisa manje sekuyi-**khodi yangempela** (kuqinisekisiwe ukuhlanganisa; **i-runtime ayikaqinisekiswa** ilinde isivivinyo se-2-device / se-hardware RF):

- **NearLink (Aether Teal)** — i-SSAP-over-BLE-GATT approximation yangempela (i-Aether SLE UUID `61657468-6572-0003-…`) ku-Android (`android/teal/AetherNetSleService`) nase-Windows (`WinNearLinkBleTransportService`); kuqinisekiswe ukuhlanganisa + isivivinyo seyunithi, i-runtime ayikaqinisekiswa. I-radio ye-NearLink yangempela ikhona ku-HarmonyOS kuphela (`harmonyos/teal/`, ukuqinisekiswa kudivayisi kusalindile).
- **LoRa (Aether Red)** — i-RYLR SX127x/SX126x serial driver yangempela (`LoRaSerialTransport` ku-**zilimi zonke eziyi-8** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; wonke ama-port aqinisekiswe ukuhlanganisa, kufaka i-Swift + C ku-Mac build server; i-runtime ayikaqinisekiswa — idinga imojuli ephathekayo). I-Meshtastic-over-BLE-Coded-PHY bridge (~1.3 km) isala ingumklamo obhaliwe; i-LoRa yangempela yebanga elide idinga inodi ekwazi i-LoRa (i-gateway, i-SBC, noma i-handset eqinile enemojuli ye-LoRa).
- **NFC (Aether White)** — yangempela ku-Android (HCE). I-Windows manje inayo i-BLE-GATT yangempela + i-RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`, iyahlanganisa net9/10; i-runtime ayikaqinisekiswa); i-ACR122U PC/SC uma isifundi sikhona.

Okuyangempela futhi okufanayo yonke indawo: **i-BLE, i-Wi-Fi Direct, i-HTTP/QUIC relay, kanye ne-WebRTC P2P transport (iqinisekiswe nge-loopback kuzo zonke izilimi eziyi-8)**, kanye nezokuphepha ze-Signal Protocol (X3DH + Double Ratchet), i-AODV routing, i-DTN store-and-forward, i-SOS broadcast, izwi, kanye ne-streaming.

**Isimo esiqotho:** i-BLE + Wi-Fi Direct + relay ziyangempela emkhiqizweni; **i-WebRTC P2P iyangempela futhi iqinisekiswe nge-loopback kuzo zonke izilimi eziyi-8** (izinhlaka ezimbili zishintshanisa amabhayithi ku-ICE data channel yangempela — i-Rust iqinisekisiwe ku-`.201` Linux box ene-UDP ICE esebenzayo); ukulinganisa kwe-NearLink / LoRa / NFC-on-Windows manje sekuyikhodi yangempela ehlanganisayo (i-LoRa iqinisekiswe ukuhlanganisa kuzo zonke eziyi-8, kufaka i-Swift + C ku-Mac build server; i-NearLink-Android nayo ivivinywe iyunithi) kodwa **i-runtime ayikaqinisekiswa** — akukho i-hardware / 2-device RF test okwamanje. Ziyahlanganyela ku-mesh ngekhodi; ungathumeli lezi ezintathu ulindele i-RF efakazelwe ensimini.

---

### Izinga lomdabu (Native tier) — CircleOS / OpenHarmony

CircleOS · HarmonyOS · noma iyiphi i-OS esekelwe ku-OpenHarmony

I-CircleOS yakhiwe phezu kwe-OpenHarmony, ethumela i-silicon ye-NearLink (SLE) kanye ne-SDK ye-`@kit.NearLinkKit` njengekhono le-OS lezinga eliphezulu. Kumadivayisi e-CircleOS ne-HarmonyOS ane-hardware ye-NearLink, akudingeki ukulinganisa — i-`harmonyos/teal/` isebenzisa i-radio ye-SLE yangempela ngokuqondile:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Lena akuyona nje inguqulo engcono yezinga elijwayelekile. Kungqimba ye-NearLink iyinethiwekhi ehluke ngokuphelele:

| Ikhono | Izinga elijwayelekile (BLE approx) | Izinga lomdabu (CircleOS / OpenHarmony) |
|---|---|---|
| **Ibanga le-NearLink** | ~100 m (BLE) | **600 m** |
| **I-Bandwidth ye-NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **I-Latency ye-NearLink** | ~10 ms (BLE) | **20 µs** |
| **Amandla e-NearLink** | i-BLE baseline | **60% ngaphansi kwe-BLE 5.0** |
| **Izinhlaka ze-NearLink ezikanye kanye** | ~7 (umkhawulo woxhumano lwe-BLE) | **500+** |
| **Umthombo we-NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | I-radio ye-SLE yangempela (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Umdabu | Umdabu (efanayo) |
| **Ezokuphepha ze-Signal Protocol** | Egcwele | Egcwele (efanayo) |
| **Routing / DTN / SOS** | Egcwele | Egcwele (efanayo) |
| **Ubunikazi be-Aether Tag** | Buyasekelwa | Buyasekelwa (efanayo) |

---

### Ukuhamba phakathi kwamazinga

Akukho ushintsho lwekhodi oludingekayo. Izinga linqunywa ku-runtime yi-`IsAvailable` kuyo insiza yalelo transport ngayinye:

1. Kudivayisi ye-CircleOS noma ye-HarmonyOS ene-silicon ye-NearLink, i-`IsAvailable` ku-transport ye-NearLink ibuyisela i-`true` (i-hardware ihloliwe nge-permission check + umzamo we-passive scan).
2. I-`TransportManager` ikhweza ngokuzenzakalelayo i-NearLink kusikhundla sokubaluleka — izindleko zamandla eziphansi kunazo zonke, i-bandwidth ephezulu kunazo zonke.
3. Ikhodi ye-app, ifomethi yephakethe, i-algorithm yokwedlulisa, ingqimba yezokuphepha, kanye nama-Aether Tags kuyafana kuwo womabili amazinga.

Inodi esezingeni elijwayelekile nenodi esezingeni lomdabu zingakhulumisana ngokukhululekile — zabelana ngefomethi ye-wire efanayo, amaseshini e-Signal Protocol afanayo, kanye nama-Aether Tags afanayo. Umehluko wezinga uthinta kuphela i-radio esetshenziselwa amaphakethe e-NearLink, hhayi iphrothokholi engaphezu kwayo.

---

> **Ngaphakathi lawa mazinga abizwa ngokuthi yi-Asterix variant (elijwayelekile) kanye ne-Obelix variant (elomdabu).** I-Asterix isebenza kahle nalokho okutholakalayo. I-Obelix — esebenza ku-CircleOS ene-NearLink yomdabu — isebenza ngekhono eliphakeme unomphela, ngendlela u-Obelix aphatha ngayo amandla we-magic potion ngaphandle kokuphuza futhi.

---

## Ukwenziwa (Implementations)

I-Aether yakhiwe ngezilimi eziyi-8 ukuze isebenze kumafoni, kuma-laptop, kumathebulethi, nakuma-microcontroller. Zonke izenzo zikhiqiza amaphakethe ahambisana ku-wire — umyalezo obethelwe yinodi ye-Rust ungadluliselwa yinodi ye-Python bese uhlutshwa yinodi ye-Swift.

| Ulimi | Uhla | Ifomethi ye-wire | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Voice/Group | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Zonke izilimi eziyi-8 zikhiqiza amaphakethe e-wire afana ibhayithi, kuqinisekiswa yizifixture zefomethi ye-wire eziyi-14 ezisemthethweni kanye nama-Signal test vectors ama-4 asetshenziswa ku-CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). I-Routing (i-AODV-style RREQ/RREP), i-DTN store-and-forward, i-SOS broadcast, izwi, i-streaming, kanye nezinsiza ze-security-hardening kwenziwe kulo lonke ulimi nge-**~3,000 izivivinyo** kuzo zonke izenzo eziyi-8:

| Ulimi | Izivivinyo | I-CI platform |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Isamba** | **~3,000** | |

I-Cross-language Signal interop iboshwe ku-`fixtures/signal/` ngama-test vectors abiwe e-X3DH (`x3dh_basic`), i-symmetric ratchet (`ratchet_step_basic`, `ratchet_step_three_iterations`), kanye ne-KDF_RK (`kdf_rk_basic`). Sonke isenzo kumele sikhiqize okuphumayo okufana ibhayithi ngebhayithi maqondana nalezo fixtures. Zonke izilimi eziyi-8 manje zithumela i-Signal session egcwele (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Ngaphesheya kwefomethi ye-wire kanye ne-Signal, **isethi yonke yensiza ye-wire-service** — i-presence, i-heartbeat, i-profile sync, i-ephemeral-ID announce, i-pre-key exchange, amashaneli, i-push-to-talk, ukwabelana ngesikrini, ukulawula amakholi, ukuqinisekiswa kwe-SOS, i-space breadcrumbs, i-forge announce, isicelo se-vault shard, kanye nokukala i-bandwidth (bheka **Okutholayo**) — nayo yenziwe kuzo zonke izilimi eziyi-8 futhi iboshelwe ku-fixtures yayo (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, nezinye ezifanayo). Ayikho isici esiku-C# kuphela kungqimba yephrothokholi.

## Iqalisa ngokushesha (Quickstart)

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

I-demo ikuhambisa ezinyathelweni ezi-8: ukukhiqiza okhiye bobunikazi be-Ed25519 bezinhlaka ezintathu (Alice, Bob, Charlie), ukusungula amaseshini e-Signal Protocol, ukuthumela imilayezo ebethelwe, ukudlulisa umyalezo nge-Charlie (ongakwazi ukuwufunda), ukubonisa ifomethi ye-binary wire, kanye nokubonisa i-forward secrecy kuyo yonke imilayezo emi-5 elandelanayo. Okuphumayo kukhonjiswe ngemibala futhi kumisa phakathi kwezinyathelo.

**Thumela umyalezo ku-C#:**

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

I-demo ikhiqiza okhiye bobunikazi bezinhlaka ezimbili, ishintshanisa ama-pre-key bundles, isungula amaseshini abethelwe, ithumela imilayezo ebethelwe kuzo zombili izindlela, idala futhi isayine amaphakethe e-mesh, iqinisekise izisayino, futhi iserialize amaphakethe abe yifomethi ye-binary wire. Ibuye ibonise ingqimba ye-in-process transport.

**Thumela umyalezo ku-Rust:**

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

I-demo idala izinhlaka ezimbili kunethiwekhi elingisiwe, ikhiqiza okhiye be-Ed25519, isungula amaseshini e-Signal Protocol, idala futhi isayine iphakethe, iserialize libe yifomethi ye-binary ehambisana ne-C#, ibethela umyalezo oyimfihlo, iwuhluze kwenye inodi, iwuthumele nge-transport, futhi iqinisekise i-round-trip.

**Thumela umyalezo ku-TypeScript:**

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

I-demo isebenzisa imibukiso emi-8: ukukhiqizwa kokhiye be-Ed25519 kanye nokutholwa kokuphazamiseka, ukudalwa kwenodi enamakhono, ukushintshaniswa kokhiye be-Signal Protocol X3DH, ukubethela kanye nokuhluza kwe-AES-256-GCM, i-packet serialization, ukusayina iphakethe nge-replay detection, i-in-process transport, kanye nokugeleza okuphelele kwe-end-to-end okuhlanganisa zonke izingqimba.

**Thumela umyalezo ku-Python:**

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

I-demo isebenzisa imibukiso emi-5: ama-round-trip e-packet serialization, ukusayina kwe-Ed25519 nokutholwa kokuphazamiseka, ukusungulwa kweseshini ye-Signal Protocol nemilayezo ebethelwe kuzo zombili izindlela, i-in-process transport phakathi kwezinhlaka ezimbili, kanye ne-nonce deduplication yokuvikela i-replay.

**Thumela umyalezo ku-Go:**

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

I-demo ihamba ezinyathelweni eziyi-11: ukukhiqizwa kokhiye, ukudalwa kwenodi enamakhono, ukuqaliswa kwe-Signal Protocol, ukushintshaniswa kwe-pre-key bundle, ukusungulwa kweseshini, ukudalwa kanye nokusayina iphakethe, i-serialization, i-deserialization nokuqinisekiswa kwesisayino, ukubethela kwe-end-to-end nge-key ratcheting, ukutholwa kokuhlasela kwe-replay, kanye ne-in-process transport.

**Thumela umyalezo ku-Kotlin:**

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

I-demo isebenzisa izivivinyo ezi-5: ama-round-trip e-packet serialization, ukusayina kwe-Ed25519 nokunqatshwa kokuphazamiseka, ukusungulwa kweseshini ye-Signal Protocol nokubethela kwe-AES-256-GCM, ukulethwa komyalezo we-in-process transport, kanye nokugeleza okuphelele kwe-end-to-end lapho u-Alice esayina iphakethe bese u-Bob eliqinisekisa emva kwe-transport.

**Thumela umyalezo ku-Swift:**

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

I-demo isebenzisa imibukiso emi-7: ukukhiqizwa kokhiye be-Ed25519, ukudalwa kanye nokusayina iphakethe, i-serialization ibe yifomethi ye-binary wire, i-deserialization nokuhlolwa kobuqotho, ukubethela kanye nokuhluza kwe-AES-256-GCM, ukuqinisekiswa komyalezo we-HMAC-SHA256, kanye nokususelwa kokhiye kwe-HKDF-SHA256.

**Thumela umyalezo ku-C:**

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

## Ohlelo lwesu (Roadmap)

Okwakhiwe kanye nokulandelayo.

**Kwenziwe (kuqinisekiswe phakathi kwezilimi, zonke izenzo eziyi-8):**
- Ifomethi ye-wire: iyafana ibhayithi ngebhayithi kuzo zonke izilimi eziyi-8, iboshwe yizifixture eziyi-14 ezisemthethweni kanye nokuqinisekiswa phakathi kwezilimi ku-CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — i-9-job matrix (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, kanye ne-fixture integrity job) ku-`.github/workflows/ci.yml`.
- Ukusayina kanye nokuqinisekiswa kwephakethe le-Ed25519
- Ukubethela kwe-AES-256-GCM
- Ama-primitive okususelwa kokhiye e-HKDF / HMAC
- I-packet serialization + i-layout yokusayina (LE + 4-byte int32 fields)
- I-in-process transport simulator (yentuthuko nezivivinyo)
- Insiza yokwedlulisa egqugquzelwe yi-AODV nge-RREQ/RREP, izimpendulo zendlela ezisayiniwe, i-dedup, i-TTL forwarding
- Insiza ye-DTN store-and-forward ne-custody transfer, i-geohash-aware replication, i-72h TTL
- Insiza ye-SOS broadcast ne-flood, i-dedup, i-self-origin guard, i-rate-limit (3/hr)
- Ama-seams okwelulwa: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (ama-Noop defaults)
- **~3,000 izivivinyo** kuzo zonke izilimi eziyi-8 (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — zonke ziluhlaza ku-CI
- ✅ **I-X3DH ephemeral key yangempela (izilimi eziyi-8)** — ama-X25519 DHs amane (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) nge-HKDF-SHA256 root derivation. Iboshwe yi-`fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Ukuqondaniswa kwe-Double Ratchet kuwo wonke umndeni** — i-Signal §5 egcwele ne-HMAC-SHA256 + 0x01/0x02 domain separation ku-symmetric ratchet, i-HKDF-SHA256 KDF_RK ku-DH-ratchet step, i-DH-rotation ekwamukeleni. Kuqinisekiswe yi-`ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` fixtures.
- ✅ **I-PROTOCOL_SPEC §2 / §3 / §4 / §9 ibuyisaniswe ne-HEAD** — bheka i-`docs/PROTOCOL_SPEC.md`.

**Kwenziwe (zonke izilimi eziyi-8):**
- ✅ **Amakholi ezwi (1-to-1)** — i-signaling state machine (Offer/Answer/Hangup/Cancel/Timeout) + i-binary frame transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Ukulethwa okuqaphela indlela nge-`IRoutingService`.
- ✅ **Izwi leqembu** — ubulungu obuqhutshwa ngumphathi (invite/kick/leave), inkambu yokukhiqiza okhiye nge-frame ngayinye, i-unicast fan-out kuzo zonke izinhlaka zamanje, ukujikeleziswa kokhiye okulawulwa ngumphathi ekushintsheni kobulungu.
- ✅ **I-Live streaming** — umshicileli usakaza i-`StreamAnnounce`; ababhalisile bathumela i-`StreamSubscribe`; amafreyimu e-binary `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) e-unicast kumbhalisi ngamunye.
- ✅ **Amakholi evidiyo (1-to-1)** — ukuxoxisana kwe-codec/resolution/fps/bitrate ku-signaling, izisignali ze-keyframe-request kanye ne-quality-change, ifomethi ye-binary `VideoFrame` ehambisana ne-layout yezwi.
- ✅ **Ukubukela Ndawonye (Watch Together)** — umphathi ukhipha imiyalo egunyaziwe ye-`WatchSync` (play/pause/seek/speed); abalandeli bayayisebenzisa nge-RTT compensation (`position = positionMs + elapsed × playbackSpeed`); i-`WatchReaction` ye-fire-and-forget.
- ✅ **I-One-time pre-key (OPK) pool** — okuzenzakalelayo yi-100, i-FIFO issue, i-lazy top-up, ukusetshenziswa okuvikelwe nge-lock kuzo zonke izilimi eziyi-8. Ivala ingozi ye-single-OPK concurrency.
- ✅ **C: i-Signal session egcwele** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` ku-`c/src/signal_protocol.c`; izivivinyo ezi-6 ze-two-node E2E ku-`c/tests/test_signal_session.c`. Zonke izilimi eziyi-8 manje zine-Signal Protocol ekwazi iseshini egcwele.

**Kwenziwe (zonke izilimi eziyi-8 — isethi yonke yensiza ye-wire-service):**
- ✅ **Lonke uhlobo lwephakethe olugodliwe manje seluyinsiza yangempela, efana ibhayithi kuzo zonke izilimi eziyi-8.** I-Presence beacon/query (21/22), i-heartbeat (10), i-profile sync (23), i-ephemeral-routing-ID announce (56), i-pre-key exchange (25/26), amashaneli (7), i-push-to-talk (15), ukwabelana ngesikrini (32), ukulawula amakholi (27), ukuqinisekiswa kwe-SOS (6), i-space breadcrumbs (40), i-forge announce (41), isicelo se-vault shard (42), kanye nokukala i-bandwidth / ABMF (53/54/55). Ngayinye iyinsiza encane (produce + handle + event) umphathi ayixhuma ku-Signal session yayo nakuthebula lokwedlulisa; ngayinye iboshelwe ku-fixture esatshalaliswa phakathi kwezilimi (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) futhi ivivinywa ngezivivinyo zeyunithi ngazinye zezilimi, kanti i-Swift ne-C ziqinisekiswa ku-macOS build server. Bheka **Okutholayo**.

**Kwenziwe (i-C# reference kuphela):**
- ✅ **Demo Step 9 — MessagingService + DTN fallback end-to-end** — i-`samples/AetherNet.Demo.Console` ihamba ku-messaging ebethelwe nge-real-Signal ne-DTN store-and-forward uma umamukeli engaxhunyiwe ku-inthanethi.
- ✅ **I-`AetherNet.Messaging` ↔ `AetherNet.Security` bridge** — i-`SignalMessageEnvelopeCipher` yenza ingqimba ye-messaging ibe ne-end-to-end encryption ngokuzenzakalelayo; imilayezo engenayo i-Signal session iyalayishwa emgqeni, ayithunyelwa ngokungaphephile.
- ✅ **I-Adaptive bitrate streaming** — i-`AdaptiveBitrateController` nezikhwelo ze-bitrate ezidingwa yi-spec ze-Profile A (real-time), B (live broadcast), no C (VOD). Umshicileli ukhetha isitebhisi esiphakeme esiqhubekayo (i-20% headroom) bese ekhipha i-`StreamAbandon` (`PacketType.StreamAbandon`) esikhundleni se-segment uma engaphansi kwefloor. I-`IStreamingService` iveza i-`UpdateBandwidthEstimate` kanye ne-`GetCurrentBitrateRung`.
- ✅ **Watch Together: BitTorrent ingest + ChipIn group funding** — amamodeli e-`TorrentInfo` / `TorrentFile`; i-`WatchTogetherService` iphatha i-`PacketType.TorrentMetadata` bese icupha i-`TorrentReceived`. I-`ChipInPool` / `ChipInContribution` state machine (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); i-`StartChipInAsync` / `ContributeAsync` / `GetChipIn` ku-`IWatchTogetherService`.
- ✅ **Amakholi evidiyo eqembu nge-auto SFU relay** — i-`GroupVideoService` / `IGroupVideoService`. I-FullMesh topology ku-≤ 3 ababambiqhaza; ukushintsha okuzenzakalelayo ku-SFU ku-`SfuThresholdParticipants` (4) nge-relay re-assignment nge-`GroupVideoSignaling(SfuAssigned)`. I-fan-out ku-FullMesh, i-relay-only send ku-SFU mode. Uhlobo lwephakethe lokusayina i-`GroupVideoSignaling = 35`.
- ✅ **I-BLE GATT transport simulation** — i-`SimulatedBleGattTransportService` (`IBleTransportService`). I-GATT MTU framing nge-`BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), i-in-process static peer registry, i-advertisement broadcast. Yonke i-`BleMaxPayloadBytes` constraints iyaphoqelelwa.
- ✅ **I-Wi-Fi Direct transport simulation** — i-`SimulatedWifiDirectTransportService` (`IWifiDirectService`). I-`ConnectAsync`/`DisconnectAsync` lifecycle ecacile, ukulethwa okuqondile kwe-payload enkulu (akukho framing), imicimbi ye-`PeerConnected`/`PeerDisconnected` yezinhlangothi zombili.
- ✅ **I-NearLink transport simulation** — i-`SimulatedNearLinkTransportService` (`INearLinkTransportService`). I-4096 B frame MTU, i-500-peer registry, i-`ConnectedPeerCount`, i-`IsAvailable` esethekayo ku-runtime.
- ✅ **Izivivinyo ze-RF bring-up simulation** — izivivinyo ze-two-node interop (`SimulatedTransportTests`): i-BLE + NearLink `MeshPacket` round-trip, i-WiFi Direct 64 KB payload transfer. Ingqimba yesofthiwe iqinisekiswe ngokugcwele; iseshini yelabhu yedivayisi ephathekayo iyadingeka ekuqinisekisweni kwe-hardware.

**Kwenziwe (i-C# transport layer — konke i-fail-fast):**
- ✅ **I-BLE GATT real transport** — i-`WinBleGattTransportService` (Windows WinRT) + i-`android/blue/` (Android GATT server). Isivivinyo esiphelele se-RF bring-up ku-`samples/AetherNet.BleRfTest/`.
- ✅ **I-Wi-Fi Direct real transport** — i-`WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + i-`android/green/` (`WifiP2pManager`). Isivivinyo se-RF ku-`samples/AetherNet.WifiDirectRfTest/`.
- ✅ **I-HTTP relay transport (Aether Purple)** — i-`HttpRelayTransportService` ne-10-second long-poll, i-`PowerCostRelative = 100`, ngaso sonke isikhathi isixazululo sokugcina. I-relay server ku-`samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). Isivivinyo se-RF ku-`samples/AetherNet.RelayRfTest/`.
- ✅ **I-NFC (Aether White)** — i-`android/white/` yenza i-`HostApduService` ne-AID `F061657468657200`. I-`WinNfcStubTransportService` ibhala izindlela ezimbili zokulinganisa ku-Windows: (1) i-NDEF-over-BLE-GATT ne-RSSI gate ≥ −40 dBm (ilingisa i-tap-to-connect ngaphandle kwe-NFC silicon, `IsAvailable = Bluetooth present`); (2) i-ACR122U USB reader nge-`Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). Indlela yokuthuthukisa: yenza i-`ITransportService` uma i-Microsoft ithumela i-first-party P2P NFC API.
- ✅ **I-NearLink (Aether Teal)** — **`harmonyos/teal/`** — i-HarmonyOS 5.0.1 (API 13) ArkTS implementation egcwele esebenzisa i-`@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); i-`isAvailable` ihloliwe ku-runtime. I-`WinNearLinkStubTransportService` + i-`android/teal/` zibhala i-SSAP-over-BLE approximation: i-BLE GATT ne-Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — i-API-analogous ku-SSAP, hhayi i-wire-compatible ne-hardware ye-NearLink yangempela. Indlela yokuthuthukisa: buyisela izingcingo ze-BLE GATT ngezingcingo ze-`ssapc_*`/`ssaps_*` SDK; ama-UUID ne-`TransportManager` slot akushintshi.
- ✅ **I-LoRa / CircleLink (Aether Red)** — i-`LoRaCircleLinkStub` + i-`android/red/` zibhala i-Meshtastic-over-BLE-LR approximation: ifomethi ye-Meshtastic wire egcwele (16-byte header + AES-256-CTR protobuf) nge-BLE 5.0 Coded PHY S=8 (~1.3 km ngaphandle), ne-managed-flood routing kanye ne-RSSI-weighted contention window. I-Bridge-node federation ne-hardware ye-LoRa yangempela isebenza ngokuzenzakalelayo (ifomethi ye-Meshtastic packet efanayo, akukho ukuhumusha). Indlela yokuthuthukisa: buyisela i-BLE LR radio nge-SX1276/SX1278 AT-command noma i-SPI driver; ifomethi yephakethe kanye ne-routing akushintshi.

**Kuvuliwe — kulandelelwa ku-`OPEN_ISSUES.md`:**
- I-RF bring-up ku-hardware yangempela: isivivinyo se-two-node interop se-end-to-end kumadivayisi e-BLE / Wi-Fi Direct ephathekayo (izivivinyo zokulingisa ziyaphumelela; iseshini yelabhu ye-hardware iyadingeka)
- I-NearLink: i-`harmonyos/teal/` iphelele; idinga i-hardware ye-Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (i-silicon ye-NearLink ayikho kumadivayisi angewona awe-Huawei). I-Windows + Android ibuyela ku-SSAP-over-BLE approximation ngokuzenzakalelayo.
- I-LoRa / CircleLink: imojuli ye-radio iyadingeka ukuze kube nebanga le-LoRa langempela. Ngaphandle kwayo, ifomethi ye-Meshtastic wire ithwalwa nge-BLE LR (~1.3 km) futhi i-bridge-node federation ne-hardware ye-LoRa yangempela iyatholakala.
- ✅ **(KUXAZULULIWE v1.2.0)** I-Consumer protocol surface (Wave 16/17) — umcimbi we-`IDtnService.BundleReceived` wamabhandeli angenayo ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), i-application-layer naming/discovery directory ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), i-author-tipping interface ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Zonke ezi-3 zithunyelwe ngokwengeza kuzo zonke izilimi eziyi-8 ngezifixture ezifana ibhayithi phakathi kwezilimi. Bheka i-CHANGELOG.

**Ayikavuleki ekufakeni kwangaphandle:**
- Iphrothokholi isaphansi kwentuthuko esebenzayo. Izifakelo zangaphandle azamukelwa okwamanje.
- Ukwenziwa kwe-NearLink transport, izibonelo zokuxhumana kwe-Android/iOS, ama-transport backend engeziwe, ama-performance benchmark, kanye ne-protocol fuzzing kulandelelwa ngaphakathi futhi kuzovulwa lapho iphrojekthi ifinyelela iphuzu eliqinile lokufaka komphakathi.

## Isakhiwo Sephrojekthi (Project Structure)

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

## Ukwengeza i-Transport Entsha

Yenza i-`ITransportService`:

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

Yibhalise ku-DI bese i-`TransportManager` iyoyifaka ngokuzenzakalelayo ekukhethweni kwe-transport, ihlelwe ngezindleko zamandla.

## Iqhathaniswa Kanjani

| Iphrothokholi | Umkhawulo | Inzuzo ye-Aether |
|----------|-----------|-----------------|
| **Briar** | I-Android kuphela, incike ku-Tor | I-cross-platform, i-mesh emsulwa |
| **Meshtastic** | I-LoRa kuphela (30 kbps ubuningi) | I-multi-transport (BLE + WiFi + NearLink), ikwazi izwi ne-streaming |
| **Reticulum** | I-Python, umphakathi omncane | Izilimi eziyi-8, i-wire-compatible kuzo zonke |
| **libp2p** | Icabanga umgogodla we-inthanethi | I-offline-first, isebenza ngengqalasizinda engekho |
| **Yggdrasil** | I-overlay network, idinga i-inthanethi | I-physical-layer mesh, isebenza ngaphandle kwe-inthanethi |
| **Signal** | Ayina-mesh, idinga i-inthanethi | Isebenza offline, i-P2P, i-mesh relay, i-E2E encryption efanayo |

## Imibuzo evame ukubuzwa

**Ingabe i-AetherNet iyasebenza ngaphandle kwe-inthanethi?**
Yebo — iyi-offline-first. Amadivayisi akhulumisana ngokuqondile nge-Bluetooth, i-Wi-Fi Direct, i-NearLink, noma i-LoRa futhi adlulisela imilayezo ngokuqhasha isinyathelo nesinyathelo edlula kwamanye amadivayisi, ngaphandle koxhumano lwe-inthanethi, umbhoshongo weselula, noma iseva edingekayo. Uma kungekho ndlela ephilayo, imilayezo iyabanjelwa (i-delay-tolerant store-and-forward) kuze kube amahora angama-72 kuze kuvuleke enye.

**Ingabe ibethelwe i-end-to-end?**
Yebo. I-AetherNet isebenzisa i-Signal Protocol (i-X3DH key agreement kanye ne-Double Ratchet nge-X25519) yokubethela kwe-end-to-end, i-AES-256-GCM yama-payload emilayezo, kanye nezisayino ze-Ed25519 kulo lonke iphakethe. Amadivayisi adlulisela umyalezo awakwazi ukuwufunda.

**Isebenzisa ama-transport anjani?**
I-Bluetooth LE, i-Wi-Fi Direct, i-NearLink (SLE), i-LoRa/CircleLink serial radio, i-HTTP/QUIC relay, kanye ne-WebRTC yokuxhumana kwe-inthanethi okuqondile kwe-peer-to-peer. Iphrothokholi ikhetha ngokuzenzakalelayo i-transport etholakalayo enamandla aphansi kunazo zonke ngephakethe ngalinye bese ibuyela kwelandelayo.

**Itholakala ngeziphi izilimi zokuhlela?**
Eziyisishiyagalombili — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, kanye no-C. Sonke isenzo sikhiqiza amaphakethe e-wire afana ibhayithi ngebhayithi, kuqinisekiswa yi-corpus yezifixture ezisatshalaliswa phakathi kwezilimi ku-CI, ngakho iphakethe elakhiwe ngolunye ulimi lihlutshwa lingashintshiwe nganoma yiluphi olunye.

**Ihluke kanjani ku-Meshtastic, Briar, noma Bridgefy?**
I-Meshtastic yi-LoRa-kuphela; i-AetherNet iyi-multi-transport (i-Bluetooth + Wi-Fi + NearLink + LoRa) futhi ithwala izwi, ividiyo, kanye ne-streaming kanye nemilayezo. I-Briar iyi-Android-kuphela futhi idlulisa nge-Tor; i-AetherNet iyi-cross-platform futhi iyi-mesh emsulwa. Ngokungafani nama-SDK avaliwe, i-AetherNet inelayisensi ye-MIT futhi yenziwe ngokuvulekile ngezilimi eziyisishiyagalombili. Ithebula lokuqhathanisa elingenhla linemininingwane.

**Ingabe isilungele ukukhiqizwa (production-ready)?**
Ingqimba yephrothokholi — ifomethi ye-wire, ezokuphepha ze-Signal, i-routing, i-DTN store-and-forward, kanye nesethi yonke yensiza — yenziwe futhi yavivinywa kuzo zonke izilimi eziyisishiyagalombili. Ama-radio transport angempela lapho ikhodi yohlelo ikhona (i-Bluetooth ne-Wi-Fi ku-Windows nase-Android, i-WebRTC yonke indawo) futhi ayikaqinisekiswa ensimini kwenye indawo ilinde i-hardware bring-up, elandelelwa ngokwethembeka ku-`OPEN_ISSUES.md`. Funda amanothi esimo esigabeni ngasinye ngaphambi kokuthumela.

**Inelayisensi enjani?**
I-MIT — yamahhala yokusetshenziswa kwezohwebo nokuvulekile. Bheka i-[LICENSE](LICENSE).

**Ubani owakha i-AetherNet?**
Yenziwa njengephrothokholi evulekile engemuva kwe-mesh ecosystem ye-The Geek Network, yakhelwe eNingizimu Afrika ukuze kube khona ukuxhumana okusebenza noma ngabe kukhona noma akukho idatha yeselula.

## Amaphuzu Okwelula (Extension Points)

Iphrothokholi isebenza yodwa. Lezi zi-interface zikuvumela ukuxhuma i-backend yakho uma uyifuna:

- `IAetherNetIncentiveProvider` — vuza izinhlaka ezidlulisa ithrafikhi (i-no-op default: i-altruistic relaying)
- `IAetherNetBackendClient` — vumelanisa neseva uma i-inthanethi itholakala (i-no-op default: i-offline ngokugcwele)
- `IAetherNetFeatureFlagProvider` — vula/vala izici zephrothokholi ku-runtime (i-no-op default: konke kuvuliwe)

Zontathu zithunyelwa nezenzo ze-no-op. Zisuse futhi akukho okuphukayo.

## Ukufaka isandla (Contributing)

Izifakelo zangaphandle azivuliwe okwamanje. Iphrothokholi isaphansi kwentuthuko esebenzayo. Buya lapho simemezela iwindi lokufaka komphakathi.

## Ezokuphepha (Security)

Bheka i-[SECURITY.md](SECURITY.md) kwinqubomgomo yokudalula ngokwesibopho.

## Ilayisensi (License)

MIT License. Bheka i-[LICENSE](LICENSE).

## Izinguqulo (Translations)

Le README nayo igcinwa kwezinye izilimi ezisohlwini lwebha yolimi engenhla kwaleli fayela, ngaphansi kwe-[`docs/i18n/`](docs/i18n/) — ihlanganisa izilimi zase-Yurophu, zaseMpumalanga ye-Asia, zaseMpumalanga Ephakathi, zaseNingizimu ye-Asia, zaseNingizimu-Mpumalanga ye-Asia, kanye nezase-Afrika, ngoba inethiwekhi eyakhelwe abantu abangenayo idatha akufanele ibe nomnyango wangaphambili ongafundwa yilabo abaxhunywe kahle kuphela. **Inguqulo yesiNgisi iwumthombo weqiniso**: lapho inguqulo nombhalo wesiNgisi kungvumelani, umbhalo wesiNgisi ugunyaziwe, futhi izinguqulo zingase zisalele emuva ngokukhishwa okukodwa noma okubili. Iphrothokholi, ikhodi, izifixture, kanye nokuziphatha okuchaziwe kuyafana kungakhathaliseki ukuthi ufunda luphi ulimi.
