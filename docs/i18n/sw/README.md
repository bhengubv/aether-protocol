# AetherNet — protokoli ya mtandao-mesh inayoanza-nje-ya-mtandao

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet ni protokoli ya mtandao-mesh ya chanzo-huria, yenye leseni ya MIT** kwa ajili ya kutuma ujumbe, faili, sauti, na video kwa watu walio karibu — bila **intaneti, bila seva, na bila kujisajili**. Vifaa vinaunganishwa moja kwa moja kupitia Bluetooth, Wi-Fi Direct, NearLink, na LoRa; wakati mpokeaji yuko nje ya masafa, ujumbe unaruka kupitia vifaa vingine na kusubiri hadi saa 72 kupata njia. Inasafirisha **utekelezaji unaofanana baiti kwa baiti katika lugha nane za programu** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, na C.

Shiriki faili, ujumbe, na mitiririko na watu walio karibu nawe. Hakuna WiFi. Hakuna data ya simu. Hakuna kujisajili. Kama AirDrop, isipokuwa inafanya kazi na kila mtu, kwenye kila jukwaa.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Protokoli moja, lugha nane, zinazofanana kwenye waya.** Aether imetekelezwa katika **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, na C** — na kila pakiti inafanana baiti kwa baiti katika zote, ikisimamiwa na mkusanyiko wa fixtures unaoshirikiwa kati ya lugha katika CI. Jenga nodi yako katika lugha yoyote kati ya nane; itafanya kazi pamoja na zingine zote. README hii pia inapatikana katika lugha 11 za binadamu (viungo hapo juu).

## Unaweza kufanya nini nayo?

**Shiriki maelezo ya darasa bila kutumia data.**

Uko katika kikundi cha masomo. Mtu ana mitihani ya zamani kwenye simu yake. Aether inaituma moja kwa moja hadi kwenye kifaa chako kupitia Bluetooth — hakuna hotspot, hakuna kikundi cha WhatsApp, hakuna kikomo cha ukubwa wa faili. Iwapo mtu katika kikundi yuko nje ya masafa, faili inaruka kupitia vifaa vingine hadi inamfikia. Ujumbe unasubiri hadi saa 72 kupata njia iwapo inahitajika.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Gundua kinachoendelea karibu nawe.**

Uko kwenye tukio la chuo au tamasha. Aether inagundua vifaa vingine vilivyo karibu kupitia Bluetooth na WiFi Direct — hakuna feed ya programu, hakuna algoriti. Unaona kilichopo karibu nawe hasa, si kile kinachotangazwa.

**Tuma SOS wakati hakuna mtandao.**

Simu yako haina mtandao. Aether inatangaza ujumbe wa dharura kwa kila kifaa kilicho ndani ya masafa, na vifaa hivyo vinaupitisha. Hakuna mnara wa simu unaohitajika.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Unda chaneli za faragha za vikundi.**

Chaneli ya sakafu ya bweni lako, chama chako, au timu yako ya mradi. Ni wanachama waliothibitishwa tu wanaoweza kusoma au kutuma ujumbe. Hakuna seva inayohifadhi mazungumzo.

**Uza vitu kwa watu walio karibu.**

Orodhesha kitabu cha kiada kwa kuuza. Watu wanaotembea ndani ya masafa ya mesh wanakiona. Hakuna akaunti ya soko, hakuna ada za kuorodhesha — ni ukaribu tu.

**Angalia filamu pamoja, kupitia mesh.**

Kikundi chako kina usiku wa filamu. Mtu ana faili. Aether inasawazisha uchezaji katika kila kifaa — cheza, simamisha, ruka — vyote kwa pamoja. Iwapo baadhi ya watu tu wana faili, mesh inaigawanya kwa wakati halisi kama mtiririko wa P2P. Kila mtu anachangia kupitia SDPKT kuinunua iwapo hakuna aliye nayo.

## Jinsi inavyofanya kazi

Vifaa vinazungumza moja kwa moja kati yao kwa kutumia Bluetooth, WiFi Direct, au NearLink. Hakuna muunganisho wa intaneti, hakuna seva, hakuna miundombinu ya kati.

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

Ujumbe unaposhindwa kufikia lengo lake moja kwa moja, unaruka kupitia vifaa vingine. Vifaa hivyo vya kurushia haviwezi kusoma kile vinavyobeba — kila ujumbe umefichwa kwa AES-256-GCM. Kila pakiti imetiwa saini kwa funguo za utambulisho za Ed25519, na pakiti bandia zinatupwa na mtandao.

> **Dokezo la ukomavu wa usalama (soma kabla ya kusafirisha):** X3DH halisi (X25519 DHs 4), Double Ratchet kamili ya Signal (hatua ya kuzungusha DH wakati wa kupokea, KDF_RK, chain ratchet ya 0x01/0x02), na dimbwi la pre-key za mara moja (default OPKs 100, FIFO, zinazolindwa kwa kufuli) zimetekelezwa katika **lugha zote 8** na zimefungwa kwa mkusanyiko wa fixtures unaoshirikiwa kati ya lugha chini ya `fixtures/signal/`. Kitu pekee kilichobaki wazi ni uanzishaji halisi wa RF kwenye vifaa halisi vya BLE (kinachofuatiliwa katika `OPEN_ISSUES.md`).

Hakuna akaunti, hakuna nambari za simu, hakuna barua pepe. Unatengeneza jozi ya funguo na uko kwenye mtandao.

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

**Routing** — AODV na majibu ya njia yaliyotiwa saini. Kila jibu la njia limetiwa saini na funguo ya Ed25519 ya lengo, hivyo hakuna kifaa kinachoweza kujifanya kuwa lengo ambalo si.

**Store-and-forward** — Wakati hakuna njia hai, pakiti zinashikiliwa hadi saa 72 hadi njia inafunguka.

**Uchaguzi wa usafirishaji** — Protokoli inachagua usafirishaji sahihi kwa kila pakiti. Ujumbe mdogo wa udhibiti unapita kupitia BLE. Uhamishaji mkubwa unatumia WiFi Direct. NearLink inapopatikana.

**Sauti, video, na mitiririko** — Simu za video zenye ujadilishaji wa codec (H.264/H.265/VP8), uchaguzi wa ubora unaozingatia usafirishaji, video ya kikundi yenye SFU relay ya kiotomatiki, watch-together iliyosawazishwa na fidia ya RTT, na mitiririko yenye bitrate inayojirekebisha.

**Ulinzi dhidi ya replay** — Kuondoa nakala za nonce na dirisha la uhalali la muhuri wa muda wa dakika 5.

## Unachopata — kila huduma, katika kila lugha

Aether si usafirishaji tu. Kila aina ya pakiti iliyohifadhiwa na protokoli sasa ni **huduma halisi, inayofanya kazi katika lugha zote 8**, na kila moja inasomeshwa kuwa **pakiti za waya zinazofanana baiti kwa baiti** — pakiti iliyojengwa na nodi ya Go inasomeka, bila kubadilika, na nodi ya Swift, Rust, C, Python, TypeScript, Kotlin, au C#. Kila huduma imefungwa kwa fixture inayoshirikiwa kati ya lugha chini ya `fixtures/<service>/` na inajaribiwa na majaribio ya kitengo ya kila lugha, huku Swift na C zikithibitishwa zaidi kwenye seva ya kujenga ya macOS.

| Uwezo | Inafanya nini | Aina ya pakiti | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Presence beacon & query** | Tangaza "Nipo hapa" na uliza "nani yuko karibu?" — kupitia **kitambulisho cha muda kinachozunguka, kinachotokana na funguo** (si utambulisho wako halisi) pamoja na geohash isiyo dhahiri | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Keep-alive nyepesi ya uhai kati ya peers waliounganishwa | 10 | `fixtures/heartbeat/` | ✅ |
| **Profile sync** | Badilishana kadi ya wasifu iliyotiwa saini na peer kupitia mesh | 23 | `fixtures/profiles/` | ✅ |
| **Ephemeral-ID announce** | Mwambie rafiki kwa faragha kitambulisho chako cha sasa cha routing kinachozunguka ili aweze bado kukufikia baada ya kuzunguka | 56 | `fixtures/erid/` | ✅ |
| **Pre-key exchange** | Omba na tuma kifurushi cha pre-key cha Signal kupitia mesh, ili kuanzisha kikao cha mwisho-hadi-mwisho na mtu ambaye hujawahi kukutana naye | 25, 26 | `fixtures/prekey/` | ✅ |
| **Channels** | Ujumbe uliotiwa saini kwa chaneli ya kikundi ya faragha, ya wanachama tu | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Fremu za sauti za walkie-talkie (mzigo wa sauti uliosimbwa usioonekana) | 15 | `fixtures/media/` | ✅ |
| **Screen share** | Fremu za video za kushiriki skrini (mzigo wa video uliosimbwa usioonekana) | 32 | `fixtures/media/` | ✅ |
| **Call control** | Ishara za kupiga / kukubali / kukataa / kukata kwa simu za sauti na video | 27 | `fixtures/videocall/` | ✅ |
| **SOS acknowledgement** | Thibitisha kwa mtumaji kwamba tangazo lake la dharura lilipokelewa | 6 | `fixtures/sos/` | ✅ |
| **Space breadcrumbs** | Vidokezo vya ugunduzi vilivyowekwa alama za mahali kwa safu ya "kilichopo karibu nami" | 40 | `fixtures/space/` | ✅ |
| **Forge announce** | Tangaza artefakti ya maudhui iliyotokana/iliyoundwa kwa mesh | 41 | `fixtures/forge/` | ✅ |
| **Vault shard request** | Chukua shard ya hifadhi iliyofungwa kwa erasure-coding (K yoyote kati ya N shards huunda upya faili) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandwidth measurement** | Probe / ack / gossip kuhusu kasi ya kiungo ili mesh iongoze kupitia bomba pana zaidi (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Hizi zinakaa juu ya huduma zilizokamilika tayari za **messaging, sauti ya mtu-mmoja-hadi-mmoja na ya kikundi, simu za video, mitiririko ya moja kwa moja, watch-together, AODV routing, DTN store-and-forward, na SOS flood** — pia zimetekelezwa katika lugha zote 8.

> **Maana kamili ya "iliyojengwa" hapa.** Kila huduma inazalisha na kushughulikia pakiti yake ya waya, inainua matukio sahihi, na imefungwa kwa fixture ya kiwango cha baiti ambayo familia nzima ya lugha lazima ilingane nayo. Programu yako inaunganisha huduma na kikao chake cha Signal, jedwali la routing, na hali ya ndani. Hii ni safu ya protokoli — iliyothibitishwa katika msimbo, majaribio, na fixtures za baiti kati ya lugha — kwenye msingi ule ule wa RF wa uaminifu kama kila kitu kingine: njia yoyote ambayo hatimaye inapanda redio haijathibitishwa uwandani hadi uanzishaji wa vifaa unaofuatiliwa katika `OPEN_ISSUES.md`.

## Transports

Kila usafirishaji una jina la rangi linalotumika katika msingi wote wa msimbo. `IsAvailable` inadhibiti njia zilizozuiliwa na vifaa — `TransportManager` inaziruka na kurudi kwenye usafirishaji unaofuata unaopatikana.

**Ufunguo wa hali:** ✅ halisi, iliyojengwa & iliyothibitishwa · ⏳ halisi, uthibitisho unaendelea · ⚠️ halisi kwenye baadhi ya majukwaa, stub kwenye mengine · ❌ stub (hakuna msimbo wa usafirishaji bado).

| Rangi | Jina | Masafa | Bandwidth | Hali |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Halisi — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Halisi — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | Isiyo na kikomo | ~10 Mbps | ✅ Halisi — Windows; seva ya relay katika `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Njia ya data ya intaneti | Isiyo na kikomo | ~100 Mbps | ✅ Halisi katika lugha zote 8 — **iliyothibitishwa kwa loopback katika zote 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust kila moja ina peers wawili wanaobadilishana baiti kupitia njia halisi ya data ya ICE) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Halisi kwenye Android (`android/white/`); Windows = BLE-GATT halisi + makadirio ya ukaribu wa RSSI −40 dBm (`WinNfcBleTransportService`, inakusanya net9/10, haijathibitishwa wakati wa utekelezaji) — `Windows.Networking.Proximity` iliondolewa katika Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Halisi kwenye HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — inasubiri uthibitisho wa kifaani); Android + Windows = makadirio halisi ya SSAP-juu-ya-BLE (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; imethibitishwa kwa kukusanya + jaribio la kitengo, haijathibitishwa wakati wa utekelezaji) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Kiendeshi halisi cha serial cha RYLR SX127x/SX126x (`LoRaSerialTransport` katika C#/Go/Rust/C; inakusanya, haijathibitishwa wakati wa utekelezaji — inahitaji moduli halisi); daraja la BLE Coded-PHY bado ni muundo uliondikwa |

Usafirishaji wa redio ni halisi tu pale ambapo msimbo wa jukwaa upo (C#/Windows, Kotlin/Android, HarmonyOS). Maktaba nane za lugha vinginevyo zinasafirisha usafirishaji wa **uigaji ndani-ya-mchakato** kwa majaribio — **WebRTC ni usafirishaji wa kwanza halisi wa pamoja kwa zote** (imekamilika; imethibitishwa kwa loopback kati ya lugha).

Kipaumbele ni kwa gharama ya nishati: mesh ya redio inapendelewa, kisha WebRTC kama njia ya moja kwa moja ya intaneti, huku HTTP/QUIC relay ikiwa suluhisho la mwisho.

## Ngazi za usambazaji

Aether inafanya kazi kwenye jukwaa lolote linaloauni Bluetooth au Wi-Fi. Ngazi uliyo nayo inategemea OS unayolenga.

---

### Ngazi ya kawaida — jukwaa lolote

Android · Windows · Linux · macOS · iOS

Aether inaendesha kwenye kifaa chochote chenye vifaa vya Bluetooth au Wi-Fi. Pale ambapo redio haipo kimwili, kila usafirishaji uliozuiliwa unakadiriwa juu ya kile kinachopatikana. Makadirio haya sasa ni **msimbo halisi** (uliothibitishwa kwa kukusanya; **haujathibitishwa wakati wa utekelezaji** ukisubiri jaribio la RF la vifaa 2 / vifaa halisi):

- **NearLink (Aether Teal)** — makadirio halisi ya SSAP-juu-ya-BLE-GATT (Aether SLE UUID `61657468-6572-0003-…`) kwenye Android (`android/teal/AetherNetSleService`) na Windows (`WinNearLinkBleTransportService`); imethibitishwa kwa kukusanya + jaribio la kitengo, haijathibitishwa wakati wa utekelezaji. Redio halisi ya NearLink ipo tu kwenye HarmonyOS (`harmonyos/teal/`, inasubiri uthibitisho wa kifaani).
- **LoRa (Aether Red)** — kiendeshi halisi cha serial cha RYLR SX127x/SX126x (`LoRaSerialTransport` katika **lugha zote 8** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; kila port imethibitishwa kwa kukusanya, ikiwa ni pamoja na Swift + C kwenye seva ya kujenga ya Mac; haijathibitishwa wakati wa utekelezaji — inahitaji moduli halisi). Daraja la Meshtastic-juu-ya-BLE-Coded-PHY (~1.3 km) linabaki muundo uliondikwa; LoRa halisi ya masafa marefu inahitaji nodi yenye uwezo wa LoRa (gateway, SBC, au kifaa imara chenye moduli ya LoRa).
- **NFC (Aether White)** — halisi kwenye Android (HCE). Windows sasa ina makadirio halisi ya BLE-GATT + ukaribu wa RSSI −40 dBm (`WinNfcBleTransportService`, inakusanya net9/10; haijathibitishwa wakati wa utekelezaji); ACR122U PC/SC msomaji anapokuwapo.

Kile kilicho halisi na kinachofanana kila mahali: **BLE, Wi-Fi Direct, HTTP/QUIC relay, na usafirishaji wa WebRTC P2P (uliothibitishwa kwa loopback katika lugha zote 8)**, pamoja na usalama wa Signal Protocol (X3DH + Double Ratchet), AODV routing, DTN store-and-forward, tangazo la SOS, sauti, na mitiririko.

**Hali ya uaminifu:** BLE + Wi-Fi Direct + relay ni halisi za uzalishaji; **WebRTC P2P ni halisi na imethibitishwa kwa loopback katika lugha zote 8** (peers wawili wanabadilishana baiti kupitia njia halisi ya data ya ICE — Rust imethibitishwa kwenye kisanduku cha Linux cha `.201` chenye UDP ICE inayofanya kazi); makadirio ya NearLink / LoRa / NFC-kwenye-Windows sasa ni msimbo halisi unaokusanya (LoRa imethibitishwa kwa kukusanya katika zote 8, ikiwa ni pamoja na Swift + C kwenye seva ya kujenga ya Mac; NearLink-Android pia imejaribiwa kwa kitengo) lakini **haijathibitishwa wakati wa utekelezaji** — hakuna jaribio la RF la vifaa / vifaa 2 bado. Zinashiriki katika mesh kwenye msimbo; usisambaze hizo tatu ukitarajia RF iliyothibitishwa uwandani.

---

### Ngazi ya asili — CircleOS / OpenHarmony

CircleOS · HarmonyOS · OS yoyote inayotokana na OpenHarmony

CircleOS imejengwa juu ya OpenHarmony, ambayo inasafirisha silikoni ya NearLink (SLE) na SDK ya `@kit.NearLinkKit` kama uwezo wa daraja la kwanza wa OS. Kwenye vifaa vya CircleOS na HarmonyOS vyenye vifaa vya NearLink, hakuna makadirio yanayohitajika — `harmonyos/teal/` inatumia redio halisi ya SLE moja kwa moja:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Hii si tu toleo bora la ngazi ya kawaida. Katika safu ya NearLink ni mtandao tofauti kimsingi:

| Uwezo | Ngazi ya kawaida (makadirio ya BLE) | Ngazi ya asili (CircleOS / OpenHarmony) |
|---|---|---|
| **Masafa ya NearLink** | ~100 m (BLE) | **600 m** |
| **Bandwidth ya NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latency ya NearLink** | ~10 ms (BLE) | **20 µs** |
| **Nishati ya NearLink** | msingi wa BLE | **60% chini ya BLE 5.0** |
| **Peers wa NearLink kwa wakati mmoja** | ~7 (kikomo cha muunganisho wa BLE) | **500+** |
| **Chanzo cha NearLink** | SSAP-juu-ya-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Redio halisi ya SLE (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Asili | Asili (inayofanana) |
| **Usalama wa Signal Protocol** | Kamili | Kamili (inayofanana) |
| **Routing / DTN / SOS** | Kamili | Kamili (inayofanana) |
| **Utambulisho wa Aether Tag** | Inaauniwa | Inaauniwa (inayofanana) |

---

### Kuhama kati ya ngazi

Hakuna mabadiliko ya msimbo yanayohitajika. Ngazi inaamuliwa wakati wa utekelezaji na `IsAvailable` kwenye kila huduma ya usafirishaji:

1. Kwenye kifaa cha CircleOS au HarmonyOS chenye silikoni ya NearLink, `IsAvailable` kwenye usafirishaji wa NearLink inarudisha `true` (imepimwa kwa vifaa kupitia ukaguzi wa ruhusa + jaribio la skani tulivu).
2. `TransportManager` inapandisha NearLink kiotomatiki hadi nafasi ya kipaumbele — gharama ya nishati ya chini kabisa, bandwidth ya juu kabisa.
3. Msimbo wa programu, muundo wa pakiti, algoriti ya routing, safu ya usalama, na Aether Tags zinafanana katika ngazi zote mbili.

Nodi katika ngazi ya kawaida na nodi katika ngazi ya asili zinaweza kuwasiliana kwa uhuru — zinashiriki muundo ule ule wa waya, vikao vile vile vya Signal Protocol, na Aether Tags zile zile. Tofauti ya ngazi inaathiri tu redio inayotumika kwa pakiti za NearLink, si protokoli iliyo juu yake.

---

> **Kwa ndani ngazi hizi zinaitwa varianti ya Asterix (kawaida) na varianti ya Obelix (asili).** Asterix inafanya kazi vizuri na kile kinachopatikana. Obelix — inayoendesha kwenye CircleOS na NearLink asili — inafanya kazi kwa uwezo ulioinuliwa kudumu, kama vile Obelix anavyobeba nguvu ya potion ya kichawi bila kuhitaji kunywa tena.

---

## Implementations

Aether imejengwa katika lugha 8 ili iendeshe kwenye simu, laptop, tableti, na microcontrollers. Utekelezaji wote unazalisha pakiti zinazolingana na waya — ujumbe uliofichwa na nodi ya Rust unaweza kurushwa na nodi ya Python na kufunguliwa na nodi ya Swift.

| Lugha | Directory | Muundo wa waya | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Sauti/Kikundi | Mitiririko/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Lugha zote 8 zinazalisha pakiti za waya zinazofanana baiti kwa baiti, zilizothibitishwa na fixtures 14 za kawaida za muundo wa waya na vekta 4 za majaribio za Signal zinazoendeshwa katika CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Routing (RREQ/RREP ya mtindo wa AODV), DTN store-and-forward, tangazo la SOS, sauti, mitiririko, na huduma za kuimarisha usalama zimetekelezwa katika kila lugha zenye **majaribio ~3,000** katika utekelezaji wote 8:

| Lugha | Majaribio | Jukwaa la CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Jumla** | **~3,000** | |

Ushirikiano wa Signal kati ya lugha umetiwa nanga kwa `fixtures/signal/` na vekta za majaribio zinazoshirikiwa kwa X3DH (`x3dh_basic`), ratchet ya ulinganifu (`ratchet_step_basic`, `ratchet_step_three_iterations`), na KDF_RK (`kdf_rk_basic`). Kila utekelezaji lazima uzalishe matokeo yanayofanana baiti kwa baiti dhidi ya fixtures hizo. Lugha zote 8 sasa zinasafirisha kikao kamili cha Signal (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Zaidi ya muundo wa waya na Signal, **mkusanyiko mzima wa huduma za waya** — presence, heartbeat, profile sync, ephemeral-ID announce, pre-key exchange, channels, push-to-talk, screen share, call control, SOS acknowledgement, space breadcrumbs, forge announce, vault shard request, na bandwidth measurement (ona **Unachopata**) — vivyo hivyo zimetekelezwa katika lugha zote 8 na zimefungwa kwa fixtures zao wenyewe (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, na ndugu zao). Hakuna kipengele kilicho cha C#-pekee katika safu ya protokoli.

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Onyesho linakuongoza kupitia hatua 8: kutengeneza funguo za utambulisho za Ed25519 kwa nodi tatu (Alice, Bob, Charlie), kuanzisha vikao vya Signal Protocol, kutuma ujumbe uliofichwa, kurusha ujumbe kupitia Charlie (asiyeweza kuusoma), kuonyesha muundo wa waya wa binary, na kuonyesha forward secrecy katika ujumbe 5 mfululizo. Matokeo yana rangi na yanasimama kati ya hatua.

**Tuma ujumbe katika C#:**

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

Onyesho linatengeneza funguo za utambulisho kwa nodi mbili, linabadilishana vifurushi vya pre-key, linaanzisha vikao vilivyofichwa, linatuma ujumbe uliofichwa katika pande zote mbili, linatengeneza na kutia saini pakiti za mesh, linathibitisha saini, na linasomesha pakiti kuwa muundo wa waya wa binary. Pia linaonyesha safu ya usafirishaji ya ndani-ya-mchakato.

**Tuma ujumbe katika Rust:**

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

Onyesho linatengeneza nodi mbili katika mtandao ulioigwa, linatengeneza funguo za Ed25519, linaanzisha vikao vya Signal Protocol, linatengeneza na kutia saini pakiti, linaisomesha kuwa muundo wa binary unaolingana na C#, linaficha ujumbe wa siri, linaufungua kwenye nodi nyingine, linautuma kupitia usafirishaji, na linathibitisha safari ya kwenda-na-kurudi.

**Tuma ujumbe katika TypeScript:**

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

Onyesho linaendesha maonyesho 8: kutengeneza funguo za Ed25519 na kugundua uharibifu, kutengeneza nodi zenye uwezo, kubadilishana funguo za X3DH za Signal Protocol, kuficha na kufungua kwa AES-256-GCM, kusomesha pakiti, kutia saini pakiti na kugundua replay, usafirishaji wa ndani-ya-mchakato, na mtiririko kamili wa mwisho-hadi-mwisho unaounganisha safu zote.

**Tuma ujumbe katika Python:**

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

Onyesho linaendesha maonyesho 5: safari za kwenda-na-kurudi za kusomesha pakiti, kutia saini kwa Ed25519 na kugundua uharibifu, kuanzisha kikao cha Signal Protocol na messaging iliyofichwa katika pande zote mbili, usafirishaji wa ndani-ya-mchakato kati ya peers wawili, na kuondoa nakala za nonce kwa ulinzi dhidi ya replay.

**Tuma ujumbe katika Go:**

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

Onyesho linakuongoza kupitia hatua 11: kutengeneza funguo, kutengeneza nodi zenye uwezo, kuanzisha Signal Protocol, kubadilishana vifurushi vya pre-key, kuanzisha kikao, kutengeneza na kutia saini pakiti, kusomesha, kusomesha-nyuma na uthibitisho wa saini, uficho wa mwisho-hadi-mwisho na kuzungusha funguo, kugundua shambulio la replay, na usafirishaji wa ndani-ya-mchakato.

**Tuma ujumbe katika Kotlin:**

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

Onyesho linaendesha majaribio 5: safari za kwenda-na-kurudi za kusomesha pakiti, kutia saini kwa Ed25519 na kukataa uharibifu, kuanzisha kikao cha Signal Protocol na uficho wa AES-256-GCM, uwasilishaji wa ujumbe wa usafirishaji wa ndani-ya-mchakato, na mtiririko kamili wa mwisho-hadi-mwisho ambapo Alice anatia saini pakiti na Bob anaithibitisha baada ya usafirishaji.

**Tuma ujumbe katika Swift:**

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

Onyesho linaendesha maonyesho 7: kutengeneza funguo za Ed25519, kutengeneza na kutia saini pakiti, kusomesha kuwa muundo wa waya wa binary, kusomesha-nyuma na ukaguzi wa uadilifu, kuficha na kufungua kwa AES-256-GCM, uthibitisho wa ujumbe wa HMAC-SHA256, na kutokeza funguo za HKDF-SHA256.

**Tuma ujumbe katika C:**

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

Kilichojengwa na kinachofuata.

**Kimefanyika (kimethibitishwa kati ya lugha, utekelezaji wote 8):**
- Muundo wa waya: unaofanana baiti kwa baiti katika lugha 8, uliotiwa nanga na fixtures 14 za kawaida na uhakiki kati ya lugha katika CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — matriki ya kazi 9 (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, pamoja na kazi ya uadilifu wa fixtures) katika `.github/workflows/ci.yml`.
- Kutia saini na kuthibitisha pakiti za Ed25519
- Uficho wa AES-256-GCM
- Primitives za kutokeza funguo za HKDF / HMAC
- Mpangilio wa kusomesha pakiti + kutia saini (LE + sehemu za int32 za baiti 4)
- Kiigaji cha usafirishaji cha ndani-ya-mchakato (kwa maendeleo na majaribio)
- Huduma ya routing iliyochochewa na AODV yenye RREQ/RREP, majibu ya njia yaliyotiwa saini, kuondoa nakala, uelekezaji wa TTL
- Huduma ya DTN store-and-forward yenye uhamishaji wa uangalizi, urudufishaji unaozingatia geohash, TTL ya saa 72
- Huduma ya tangazo la SOS yenye flood, kuondoa nakala, ulinzi wa asili-binafsi, kikomo cha kasi (3/saa)
- Sehemu za upanuzi: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (default za Noop)
- **majaribio ~3,000** katika lugha zote 8 (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — zote za kijani katika CI
- ✅ **Funguo halisi ya ephemeral ya X3DH (lugha 8)** — X25519 DHs 4 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) na utokezaji wa mzizi wa HKDF-SHA256. Imefungwa na `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Ulinganifu wa Double Ratchet katika familia nzima** — Signal §5 kamili na HMAC-SHA256 + utenganishaji wa kikoa wa 0x01/0x02 katika ratchet ya ulinganifu, HKDF-SHA256 KDF_RK katika hatua ya DH-ratchet, kuzungusha DH wakati wa kupokea. Imethibitishwa na fixtures za `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 zimepatanishwa na HEAD** — ona `docs/PROTOCOL_SPEC.md`.

**Kimefanyika (lugha zote 8):**
- ✅ **Simu za sauti (mmoja-hadi-mmoja)** — mashine ya hali ya ishara (Offer/Answer/Hangup/Cancel/Timeout) + usafirishaji wa fremu za binary (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Uwasilishaji unaozingatia njia kupitia `IRoutingService`.
- ✅ **Sauti ya kikundi** — uanachama unaoendeshwa na mwenyeji (invite/kick/leave), sehemu ya kutengeneza funguo kwa kila fremu, unicast fan-out kwa wanachama wote wa sasa, kuzungusha funguo kunakodhibitiwa na mwenyeji wakati wa mabadiliko ya uanachama.
- ✅ **Mitiririko ya moja kwa moja** — mchapishaji anatangaza `StreamAnnounce`; wajiandikishaji wanatuma `StreamSubscribe`; fremu za binary za `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast kwa kila mjiandikishaji.
- ✅ **Simu za video (mmoja-hadi-mmoja)** — ujadilishaji wa codec/resolution/fps/bitrate katika ishara, ishara za ombi-la-keyframe na mabadiliko-ya-ubora, muundo wa binary wa `VideoFrame` unaolingana na mpangilio wa sauti.
- ✅ **Watch Together** — mwenyeji anatoa amri za mamlaka za `WatchSync` (play/pause/seek/speed); wafuasi wanazitumia na fidia ya RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` ya tuma-na-usahau.
- ✅ **Dimbwi la pre-key za mara moja (OPK)** — default 100, utoaji wa FIFO, kujaza kwa uvivu, matumizi yanayolindwa kwa kufuli katika lugha zote 8. Inafunga hatari ya ushindani wa OPK-moja.
- ✅ **C: kikao kamili cha Signal** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` katika `c/src/signal_protocol.c`; majaribio 6 ya E2E ya nodi-mbili katika `c/tests/test_signal_session.c`. Lugha zote 8 sasa zina Signal Protocol yenye uwezo kamili wa kikao.

**Kimefanyika (lugha zote 8 — mkusanyiko kamili wa huduma za waya):**
- ✅ **Kila aina ya pakiti iliyohifadhiwa sasa ni huduma halisi, inayofanana baiti kwa baiti katika lugha zote 8.** Presence beacon/query (21/22), heartbeat (10), profile sync (23), ephemeral-routing-ID announce (56), pre-key exchange (25/26), channels (7), push-to-talk (15), screen share (32), call control (27), SOS acknowledgement (6), space breadcrumbs (40), forge announce (41), vault shard request (42), na bandwidth measurement / ABMF (53/54/55). Kila moja ni huduma nyembamba (produce + handle + event) ambayo mwenyeji anaiunganisha na kikao chake cha Signal na jedwali la routing; kila moja imefungwa kwa fixture inayoshirikiwa kati ya lugha (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) na inajaribiwa na majaribio ya kitengo ya kila lugha, huku Swift na C zikithibitishwa kwenye seva ya kujenga ya macOS. Ona **Unachopata**.

**Kimefanyika (rejeleo la C# tu):**
- ✅ **Onyesho Hatua ya 9 — MessagingService + DTN fallback mwisho-hadi-mwisho** — `samples/AetherNet.Demo.Console` inakuongoza kupitia messaging iliyofichwa kwa Signal halisi na DTN store-and-forward wakati mpokeaji hayupo mtandaoni.
- ✅ **Daraja la `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` inafanya safu ya messaging kuwa iliyofichwa mwisho-hadi-mwisho kwa default; ujumbe usio na kikao cha Signal unawekwa kwenye foleni, kamwe hautumwi kwa njia isiyo salama.
- ✅ **Mitiririko yenye bitrate inayojirekebisha** — `AdaptiveBitrateController` na ngazi za bitrate zinazotakiwa na spec kwa Profile A (wakati halisi), B (utangazaji wa moja kwa moja), na C (VOD). Mchapishaji anachagua ngazi ya juu kabisa inayodumu (nafasi ya 20%) na anatoa `StreamAbandon` (`PacketType.StreamAbandon`) badala ya sehemu ikiwa iko chini ya sakafu. `IStreamingService` inaonyesha `UpdateBandwidthEstimate` na `GetCurrentBitrateRung`.
- ✅ **Watch Together: uingizaji wa BitTorrent + ufadhili wa kikundi wa ChipIn** — mifano ya `TorrentInfo` / `TorrentFile`; `WatchTogetherService` inashughulikia `PacketType.TorrentMetadata` na inainua `TorrentReceived`. Mashine ya hali ya `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` kwenye `IWatchTogetherService`.
- ✅ **Simu za video za kikundi na SFU relay ya kiotomatiki** — `GroupVideoService` / `IGroupVideoService`. Topolojia ya FullMesh kwa washiriki ≤ 3; kubadili kiotomatiki kwenda SFU kwenye `SfuThresholdParticipants` (4) na kupanga upya relay kupitia `GroupVideoSignaling(SfuAssigned)`. Fan-out katika FullMesh, kutuma-kwa-relay-tu katika hali ya SFU. Aina ya pakiti ya ishara `GroupVideoSignaling = 35`.
- ✅ **Uigaji wa usafirishaji wa BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Uwekaji fremu wa GATT MTU kupitia `BleGattFramer` (1024 B/fremu, `[2B count][2B index][payload]`), rejista ya peer tuli ya ndani-ya-mchakato, tangazo la utangazaji. Vikwazo vyote vya `BleMaxPayloadBytes` vinatekelezwa.
- ✅ **Uigaji wa usafirishaji wa Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Mzunguko wa maisha wa wazi wa `ConnectAsync`/`DisconnectAsync`, uwasilishaji wa moja kwa moja wa mzigo mkubwa (bila kuwekwa fremu), matukio ya pande mbili ya `PeerConnected`/`PeerDisconnected`.
- ✅ **Uigaji wa usafirishaji wa NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU ya fremu ya 4096 B, rejista ya peer 500, `ConnectedPeerCount`, `IsAvailable` inayowekwa wakati wa utekelezaji.
- ✅ **Majaribio ya uigaji ya uanzishaji wa RF** — majaribio ya ushirikiano ya nodi-mbili (`SimulatedTransportTests`): safari za kwenda-na-kurudi za `MeshPacket` za BLE + NearLink, uhamishaji wa mzigo wa 64 KB wa WiFi Direct. Safu ya programu imethibitishwa kikamilifu; kikao cha maabara ya kifaa halisi kinahitajika kwa uthibitisho kwenye vifaa.

**Kimefanyika (safu ya usafirishaji ya C# — zote fail-fast):**
- ✅ **Usafirishaji halisi wa BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (seva ya GATT ya Android). Jaribio kamili la uanzishaji wa RF katika `samples/AetherNet.BleRfTest/`.
- ✅ **Usafirishaji halisi wa Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Jaribio la RF katika `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Usafirishaji wa HTTP relay (Aether Purple)** — `HttpRelayTransportService` na long-poll ya sekunde 10, `PowerCostRelative = 100`, daima suluhisho la mwisho. Seva ya relay katika `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). Jaribio la RF katika `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` inatekeleza `HostApduService` na AID `F061657468657200`. `WinNfcStubTransportService` inaandika njia mbili za makadirio za Windows: (1) NDEF-juu-ya-BLE-GATT na lango la RSSI ≥ −40 dBm (inaiga tap-to-connect bila silikoni ya NFC, `IsAvailable = Bluetooth ipo`); (2) msomaji wa USB wa ACR122U kupitia `Windows.Devices.SmartCards` PC/SC (`IsAvailable = msomaji wa contactless umeorodheshwa`). Njia ya kuboresha: tekeleza `ITransportService` wakati Microsoft inasafirisha API ya P2P NFC ya daraja la kwanza.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — utekelezaji kamili wa HarmonyOS 5.0.1 (API 13) wa ArkTS kwa kutumia `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` imepimwa wakati wa utekelezaji. `WinNearLinkStubTransportService` + `android/teal/` zinaandika makadirio ya SSAP-juu-ya-BLE: BLE GATT na Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — inayolingana na SSAP kiAPI, isiyolingana na waya na vifaa halisi vya NearLink. Njia ya kuboresha: badilisha miito ya BLE GATT na miito ya SDK ya `ssapc_*`/`ssaps_*`; UUIDs na nafasi ya `TransportManager` hazibadiliki.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` zinaandika makadirio ya Meshtastic-juu-ya-BLE-LR: muundo kamili wa waya wa Meshtastic (kichwa cha baiti 16 + AES-256-CTR protobuf) juu ya BLE 5.0 Coded PHY S=8 (~1.3 km nje), na routing ya managed-flood na dirisha la ushindani lenye uzito wa RSSI. Ushirikiano wa nodi-daraja na vifaa halisi vya LoRa unafanya kazi kiotomatiki (muundo ule ule wa pakiti wa Meshtastic, hakuna tafsiri). Njia ya kuboresha: badilisha redio ya BLE LR na kiendeshi cha SX1276/SX1278 cha AT-command au SPI; muundo wa pakiti na routing hazibadiliki.

**Wazi — zinafuatiliwa katika `OPEN_ISSUES.md`:**
- Uanzishaji wa RF kwenye vifaa halisi: jaribio la ushirikiano la nodi-mbili la mwisho-hadi-mwisho kwenye vifaa halisi vya BLE / Wi-Fi Direct (majaribio ya uigaji yanapita; kikao cha maabara ya vifaa kinahitajika)
- NearLink: `harmonyos/teal/` imekamilika; inahitaji vifaa vya Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silikoni ya NearLink haipo kwenye vifaa visivyo vya Huawei). Windows + Android zinarudi kwenye makadirio ya SSAP-juu-ya-BLE kiotomatiki.
- LoRa / CircleLink: moduli ya redio inahitajika kwa masafa halisi ya LoRa. Bila moduli, muundo wa waya wa Meshtastic unabebwa juu ya BLE LR (~1.3 km) na ushirikiano wa nodi-daraja na vifaa halisi vya LoRa unapatikana.
- ✅ **(IMETATULIWA v1.2.0)** Uso wa protokoli ya watumiaji (Wave 16/17) — tukio la `IDtnService.BundleReceived` kwa bundles zinazoingia ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), directory ya kutaja/kugundua ya safu ya programu ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), interface ya kumtipu mwandishi ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Zote 3 zilisafirishwa kwa nyongeza katika lugha 8 na fixtures zinazolingana baiti kati ya lugha. Ona CHANGELOG.

**Bado haziko wazi kwa mchango wa nje:**
- Protokoli bado iko chini ya maendeleo hai. Michango ya nje haipokelewi kwa sasa.
- Utekelezaji wa usafirishaji wa NearLink, mifano ya ushirikiano wa Android/iOS, backends za usafirishaji za ziada, vipimo vya utendaji, na fuzzing ya protokoli zinafuatiliwa kwa ndani na zitafunguliwa wakati mradi unafikia hatua thabiti ya mchango wa umma.

## Project Structure

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

## Adding a New Transport

Tekeleza `ITransportService`:

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

Isajili katika DI na `TransportManager` itaijumuisha kiotomatiki katika uchaguzi wa usafirishaji, ikipangwa kwa gharama ya nishati.

## How It Compares

| Protokoli | Kikwazo | Faida ya Aether |
|----------|-----------|-----------------|
| **Briar** | Android-tu, inategemea Tor | Mtambuka wa majukwaa, mesh safi |
| **Meshtastic** | LoRa tu (30 kbps kiwango cha juu) | Usafirishaji wa aina nyingi (BLE + WiFi + NearLink), yenye uwezo wa sauti na mitiririko |
| **Reticulum** | Python, jamii ndogo | Lugha 8, zinazolingana na waya katika zote |
| **libp2p** | Inadhania uti wa mgongo wa intaneti | Isiyohitaji-mtandao-kwanza, inafanya kazi na miundombinu sifuri |
| **Yggdrasil** | Mtandao wa overlay, inahitaji intaneti | Mesh ya safu ya kimwili, inafanya kazi bila intaneti |
| **Signal** | Hakuna mesh, inahitaji intaneti | Inafanya kazi bila mtandao, P2P, mesh relay, uficho ule ule wa E2E |

## Maswali yanayoulizwa mara kwa mara

**Je, AetherNet inafanya kazi bila intaneti?**
Ndiyo — inaanza-nje-ya-mtandao. Vifaa vinazungumza moja kwa moja kupitia Bluetooth, Wi-Fi Direct, NearLink, au LoRa na vinarusha ujumbe hatua-kwa-hatua kupitia vifaa vingine, bila kuhitaji muunganisho wa intaneti, mnara wa simu, au seva. Wakati hakuna njia hai, ujumbe unashikiliwa (store-and-forward inayostahimili ucheleweshaji) hadi saa 72 hadi njia inafunguka.

**Je, ni iliyofichwa mwisho-hadi-mwisho?**
Ndiyo. AetherNet inatumia Signal Protocol (makubaliano ya funguo ya X3DH pamoja na Double Ratchet juu ya X25519) kwa uficho wa mwisho-hadi-mwisho, AES-256-GCM kwa mizigo ya ujumbe, na saini za Ed25519 kwenye kila pakiti. Vifaa vinavyorusha ujumbe haviwezi kuusoma.

**Inatumia usafirishaji gani?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), redio ya serial ya LoRa/CircleLink, HTTP/QUIC relay, na WebRTC kwa peer-to-peer ya moja kwa moja ya intaneti. Protokoli inachagua kiotomatiki usafirishaji wenye nishati ya chini kabisa unaopatikana kwa kila pakiti na inarudi kwa unaofuata.

**Inapatikana katika lugha zipi za programu?**
Nane — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, na C. Kila utekelezaji unazalisha pakiti za waya zinazofanana baiti kwa baiti, ukisimamiwa na mkusanyiko wa fixtures unaoshirikiwa kati ya lugha katika CI, hivyo pakiti iliyojengwa na lugha moja inasomeka bila kubadilika na lugha yoyote nyingine.

**Inatofautianaje na Meshtastic, Briar, au Bridgefy?**
Meshtastic ni LoRa-tu; AetherNet ni ya usafirishaji-nyingi (Bluetooth + Wi-Fi + NearLink + LoRa) na inabeba sauti, video, na mitiririko pamoja na ujumbe. Briar ni Android-tu na inaelekeza juu ya Tor; AetherNet ni mtambuka wa majukwaa na mesh safi. Tofauti na SDK zilizofungwa, AetherNet ina leseni ya MIT na imetekelezwa kwa uwazi katika lugha nane. Jedwali la ulinganisho hapo juu lina maelezo.

**Je, iko tayari kwa uzalishaji?**
Safu ya protokoli — muundo wa waya, usalama wa Signal, routing, DTN store-and-forward, na mkusanyiko kamili wa huduma — imetekelezwa na kujaribiwa katika lugha zote nane. Usafirishaji wa redio ni halisi pale ambapo msimbo wa jukwaa upo (Bluetooth na Wi-Fi kwenye Windows na Android, WebRTC kila mahali) na haujathibitishwa uwandani pengine ukisubiri uanzishaji wa vifaa, ambao unafuatiliwa kwa uaminifu katika `OPEN_ISSUES.md`. Soma dokezo za hali katika kila sehemu kabla ya kusambaza.

**Iko chini ya leseni gani?**
MIT — bila malipo kwa matumizi ya kibiashara na ya chanzo-huria. Ona [LICENSE](LICENSE).

**Nani anajenga AetherNet?**
Inaendelezwa kama protokoli huria nyuma ya mfumo wa mesh wa The Geek Network, iliyojengwa nchini Afrika Kusini kwa mawasiliano yanayofanya kazi na au bila data ya simu.

## Extension Points

Protokoli inafanya kazi peke yake. Interfaces hizi zinakuwezesha kuunganisha backend yako mwenyewe iwapo unaitaka:

- `IAetherNetIncentiveProvider` — zawadi nodi zinazorusha trafiki (default ya no-op: kurusha kwa ukarimu)
- `IAetherNetBackendClient` — sawazisha na seva wakati intaneti inapatikana (default ya no-op: bila mtandao kabisa)
- `IAetherNetFeatureFlagProvider` — washa/zima vipengele vya protokoli wakati wa utekelezaji (default ya no-op: kila kitu kimewashwa)

Zote tatu zinasafirisha na utekelezaji wa no-op. Ziondoe na hakuna kinachovunjika.

## Contributing

Michango ya nje bado haiko wazi. Mradi bado uko chini ya maendeleo hai. Rudi tena tunapotangaza dirisha la mchango wa umma.

## Security

Ona [SECURITY.md](SECURITY.md) kwa sera ya ufichuzi wenye uwajibikaji.

## License

Leseni ya MIT. Ona [LICENSE](LICENSE).

## Translations

README hii pia inatunzwa katika lugha nyingine zilizoorodheshwa katika upau wa lugha ulio juu ya faili hii, chini ya [`docs/i18n/`](docs/i18n/) — zikienea lugha za Ulaya, Asia Mashariki, Mashariki ya Kati, Asia Kusini, Asia ya Kusini-Mashariki, na Afrika, kwa sababu mtandao uliojengwa kwa ajili ya watu wasio na data haupaswi kuwa na mlango wa mbele ambao ni waliounganishwa vizuri tu wanaoweza kuusoma. **Toleo la Kiingereza ndilo chanzo cha ukweli**: pale ambapo tafsiri na maandishi ya Kiingereza yanatofautiana, maandishi ya Kiingereza ndiyo yenye mamlaka, na tafsiri zinaweza kuchelewa nyuma yake kwa toleo moja au mawili. Protokoli, msimbo, fixtures, na tabia zilizoelezwa zinafanana bila kujali lugha unayosoma.
