# AetherNet — protocol ya marang-rang a mesh a offline-pele

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet ke protocol ya marang-rang a mesh ya open-source, e nang le laesense ya MIT** ya ho romela melaetsa, difaele, lentswe, le video ho batho ba haufi — ka **ho se be le inthanete, ho se be le disebedisi, le ho se be le ho ingodisa**. Disebediswa di hokahana ka kotloloho ka Bluetooth, Wi-Fi Direct, NearLink, le LoRa; ha moamohedi a le ka ntle ho sebaka, melaetsa e tlola ka disebediswa tse ding mme e emela ho fihlela dihora tse 72 bakeng sa tsela. E romela **diimplementeshene tse tshwanang byte-ka-byte ka dipuo tse robedi tsa dinhlaloso** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, le C.

Arolelana difaele, melaetsa, le dikhwele le batho ba haufi le wena. Ha ho WiFi. Ha ho mobile data. Ha ho ho ingodisa. Jwaloka AirDrop, empa e sebetsa le bohle, hoo platform e nngwe le e nngwe.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Protocol e le nngwe, dipuo tse robedi, e tshwana feela mohaleng.** Aether e kentswe tshebetsong ka **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, le C** — mme packet e nngwe le e nngwe e tshwana byte-ka-byte hoo tsohle, e tiisitswe ke corpus ya di-fixture tse arolelanwang pakeng tsa dipuo eo implementeshene e nngwe le e nngwe e tshwanetseng ho e lekana, byte ka byte. Haha node ya hao ka e nngwe ya tse robedi; e sebetsana le tse ding tsohle. README ena e boetse e fumaneha ka dipuo tse 20 tsa batho (dikgokahano ka hodimo).

## Ka mantswe a bonolo

**AetherNet e dumella mehala le dilaptop ho buisana ka kotloloho — ntle le inthanete, ntle le khamphani ya mehala, le ntle le akhaonto.** Haeba batho ba o potolohileng ba na le app, o ka ba romela melaetsa, wa romela dinepe le difaele tse kgolo, wa etsa diletsetso tsa lentswe le video, mme wa arolelana phallo e phelang, o sebedisa feela diseyalemoya tsa sebaka se sekgutshwane tse seng di le ka hare ho mohala o mong le o mong (Bluetooth le Wi-Fi). Haeba motho a le hole haholo hoo o ke keng wa mo fihlela ka kotloloho, molaetsa wa hao o tlola ka setu ho tloha mohaleng o mong ho ya ho o latelang ho fihlela o fihla — mme o emela ho fihlela matsatsi a mararo bakeng sa tsela haeba ho hlokahala. E ka bile ya fihlella marang-rang a maholo a phatlalatsa a ho arolelana difaele a lefatshe (theknoloji e tshwanang e ka mora di-download tse molaong tse jwaloka Linux le di-update tsa dipapadi), ya nka faele, mme ya e isa ka hare ho motswalle ya se nang inthanete ho hang.

Ntho e nngwe le e nngwe e kentswe ka encryption ya end-to-end, kahoo ke motho eo o buang le yena feela ya ka e balang — mehala e e fetisang ha e kgone. Ke ya **mahala mme e bulehile** hore mang kapa mang a e sebedise kapa a e hlahlobe, mme e ngotswe ka makgetlo a robedi, ka dipuo tse robedi tsa dinhlaloso, hore e kgone ho sebetsa hodima sesebediswa se ka bang sefe kapa sefe.

**E phethehile hakae?** "Boko" ba marang-rang — diformat tsa melaetsa, encryption, routing, le ho arolelana difaele — di hahilwe mme di hlahlobilwe ke mochini ho pholletsa le dipuo tsohle tse robedi. Se sa ntseng se hloka teko ya lefatshe la sebele ke diseyalemoya tsa sebele tse buisanang moyeng pakeng tsa mehala e mmedi ya mmele; mohato oo wa hardware ke ona o setseng, mme re o latella phatlalatsa ho `OPEN_ISSUES.md`. Tsohle tse ka tlase ke pale e tshwanang ka dintlha tse eketsehileng.

## O ka etsang ka yona?

**Arolelana dinoutu tsa dithuto ntle le ho sebedisa data.**

O sehlopheng sa boithuto. Motho e mong o na le dipampiri tsa nako e fetileng mohaleng wa hae. Aether e di romela ka kotloloho ho sesebediswa sa hao ka Bluetooth — ha ho hotspot, ha ho sehlopha sa WhatsApp, ha ho moedi wa boholo ba faele. Haeba motho sehlopheng a le ka ntle ho sebaka, faele e tlola ka disebediswa tse ding ho fihlela e mo fihlela. Melaetsa e emela ho fihlela dihora tse 72 bakeng sa tsela haeba ho hlokahala.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Fumana hore na ho etsahalang haufi le wena.**

O ketsahalong ya khampase kapa moketeng. Aether e sibolla disebediswa tse ding tse haufi ka Bluetooth le WiFi Direct — ha ho feed ya app, ha ho algorithm. O bona se hlileng se leng haufi le wena, e seng se phahamisitsweng.

**Romela SOS ha ho se na signal.**

Mohala wa hao ha o na letshwao. Aether e phatlalatsa molaetsa wa tshohanyetso ho sesebediswa se seng le se seng se leng sebakeng, mme disebediswa tseo di o fetisetsa pele. Ha ho hlokahale tora ya selefouno.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Etsa dichanele tsa sehlopha tse poraebete.**

Chanele bakeng sa mokato wa hao wa bodulo, mokgatlo wa hao, sehlopha sa projeke ya hao. Ke ditho tse netefaditsweng feela tse ka balang kapa ho romela melaetsa. Ha ho sebedisi se bolokang moqoqo.

**Rekisetsa batho ba haufi le wena dintho.**

Ngodisa buka ya thuto ho rekiswa. Batho ba tsamayang ka hara sebaka sa mesh ba e bona. Ha ho akhaonto ya mmaraka, ha ho ditefiso tsa ho ngodisa — ke ho ba haufi feela.

**Shebella filimi mmoho, ho pholletsa le mesh.**

Sehlopha sa hao se na le bosiu ba filimi. Motho e mong o na le faele. Aether e ngwahanya ho bapala ho pholletsa le sesebediswa se seng le se seng — bapala, emisa, batla — kaofela ka nako e le nngwe. Haeba ke batho ba bang feela ba nang le faele, mesh e e ajella ka nako ya sebele jwaloka phallo ya P2P. Bohle ba kenya letsoho ka SDPKT ho e reka haeba ho se na ya nang le yona.

**Fumana faele e kgolo ka tsela eo inthanete yohle e seng e di arolelana ka yona.**

BitTorrent ke theknoloji e ka mora karolo e kgolo ya ho arolelana difaele ka molao lefatsheng — ditokollo tsa Linux, di-update tsa dipapadi, Internet Archive. Aether jwale e e bua *ka nnete*: node ya Aether e ka kena swarm e tlwaelehileng ya BitTorrent mme ya nka faele ka kotloloho ho letshwele, ntle le sebedisi se bohareng. Mme mona ke phetoho bakeng sa batho ba se nang data — node e le nngwe ya Aether e *nang* le inthanete e ka lata torrent mme ya **e arolelana hape ho pholletsa le mesh ya offline**, kahoo motswalle ya se nang inthanete ho hang o ntse a amohela faele, ntlha-ka-ntlha, ka Bluetooth le Wi-Fi. Marang-rang a maholo ka ho fetisisa a ho arolelana difaele lefatsheng, a fihlella batho bao inthanete e sa ba fihlelleng.

## E sebetsa jwang

Disebediswa di buisana ka kotloloho ka Bluetooth, WiFi Direct, kapa NearLink. Ha ho kgokahano ya inthanete, ha ho sebedisi, ha ho meaho e bohareng.

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

Ha molaetsa o sitwa ho fihla sebakeng sa ona ka kotloloho, o tlola ka disebediswa tse ding. Disebediswa tseo tsa ho fetisa ha di kgone ho bala seo di se jereng — molaetsa o mong le o mong o kentswe ka AES-256-GCM. Packet e nngwe le e nngwe e saennwe ka dinotlolo tsa boitsebiso tsa Ed25519, mme di-packet tse thetsang di lahlwa ke marang-rang.

> **Lengolo la ho hola ha tshireletso (bala pele o romela):** X3DH ya sebele (di-X25519 DH tse 4), Signal Double Ratchet e feletseng (mohato wa DH-rotation ha o amohela, KDF_RK, 0x01/0x02 chain ratchet), le letamo la one-time pre-key (100 OPK ka tlwaelo, FIFO, e sireleditsweng ka locko) di kentswe tshebetsong ka **dipuo tsohle tse 8** mme di tiiselitswe ho corpus ya di-fixture tse arolelanwang pakeng tsa dipuo ka tlasa `fixtures/signal/`. Ntho e le nngwe e setseng e bulehileng ke ho phahamisa RF ya sebele hardware ya sebele ya BLE (e latellwa ho `OPEN_ISSUES.md`).

Ha ho di-akhaonto, ha ho dinomoro tsa mehala, ha ho di-imeile. O hlahisa keypair mme o marang-rang.

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

**Routing** — AODV e nang le dikarabo tsa tsela tse saennweng. Karabo e nngwe le e nngwe ya tsela e saennwe ka notlolo ya Ed25519 ya sebaka, kahoo ha ho sesebediswa se ka iketsang sebaka seo e seng sona.

**Store-and-forward** — Ha ho se na tsela e phelang, di-packet di bolokwa ho fihlela dihora tse 72 ho fihlela tsela e buleha.

**Kgetho ya transport** — Protocol e kgetha transport e nepahetseng packet e nngwe le e nngwe. Melaetsa e menyenyane ya taolo e ea ka BLE. Diphetisetso tse kgolo di sebedisa WiFi Direct. NearLink ha e fumaneha.

**Lentswe, video, le streaming** — Diletsetso tsa video tse nang le therisano ya codec (H.264/H.265/VP8), kgetho ya boleng bo tsebang transport, video ya sehlopha ka auto SFU relay, watch-together e ngwahanngweng ka phetetso ya RTT, le streaming ya bitrate e ikamahanyang.

**Tshireletso ya replay** — Nonce deduplication ka window ya bocha ba timestamp ya metsotso e 5.

## Seo o se fumanang — tshebeletso e nngwe le e nngwe, ka puo e nngwe le e nngwe

Aether ha se transport feela. Mofuta o mong le o mong wa packet o boloketsweng ke protocol jwale ke **tshebeletso ya sebele, e sebetsang ka dipuo tsohle tse 8**, mme e nngwe le e nngwe e serialize ho **di-packet tsa mohala tse tshwanang byte** — packet e hahilweng ke node ya Go e decode-uwa, e sa fetohe, ke node ya Swift, Rust, C, Python, TypeScript, Kotlin, kapa C#. Tshebeletso e nngwe le e nngwe e tiiselitswe ho fixture e arolelanwang pakeng tsa dipuo ka tlasa `fixtures/<service>/` mme e etswa ka diteko tsa yuniti tsa puo ka nngwe, ka Swift le C tse eketsehileng tse netefaditsweng ho sesebediswa sa kaho sa macOS.

| Bokgoni | Se etsang | Mofuta wa packet | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Presence beacon & query** | Phatlalatsa "Ke teng mona" mme o botse "ke mang ya haufi?" — ka **ID ya nakwana e potolohang, e nkilweng ho notlolo** (e seng boitsebiso ba hao ba sebele) hammoho le geohash e sa nepahalang | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Ho boloka bophelo bo bobebe pakeng tsa dithaka tse hokahaneng | 10 | `fixtures/heartbeat/` | ✅ |
| **Profile sync** | Fapanyetsana karete ya profaele e saennweng le thaka ho pholletsa le mesh | 23 | `fixtures/profiles/` | ✅ |
| **Ephemeral-ID announce** | Bolella motswalle ka lekunutu ID ya hao ya nakwana ya ho fetisa e potolohang hore a ntse a ka o fihlela ka mora hore e potolohe | 56 | `fixtures/erid/` | ✅ |
| **Pre-key exchange** | Kopa mme o fane ka Signal pre-key bundle ho pholletsa le mesh, ho qala moqoqo wa end-to-end le motho eo o esong ho mmone | 25, 26 | `fixtures/prekey/` | ✅ |
| **Channels** | Melaetsa e saennweng ho chanele ya sehlopha e poraebete, ya ditho feela | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Diforeimi tsa lentswe tsa walkie-talkie (payload ya odiyo e kentsweng e sa bonahaleng) | 15 | `fixtures/media/` | ✅ |
| **Screen share** | Diforeimi tsa video tsa ho arolelana skrine (payload ya video e kentsweng e sa bonahaleng) | 32 | `fixtures/media/` | ✅ |
| **Call control** | Letshwao la ho letsa / amohela / hana / ho kwala bakeng sa diletsetso tsa lentswe le video | 27 | `fixtures/videocall/` | ✅ |
| **SOS acknowledgement** | Netefatsa ho moromedi hore phatlalatso ya hae ya tshohanyetso e amohetswe | 6 | `fixtures/sos/` | ✅ |
| **Space breadcrumbs** | Dikoto tsa ho sibolla tse nang le letshwao la sebaka bakeng sa lera la "ho se leng haufi le nna" | 40 | `fixtures/space/` | ✅ |
| **Forge announce** | Phatlalatsa artefact ya diteng e nkilweng/e etselitsweng ho mesh | 41 | `fixtures/forge/` | ✅ |
| **Vault shard request** | Fumana shard ya polokelo e nang le erasure-coding (K e nngwe le e nngwe ya di-shard tsa N e aha faele hape) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandwidth measurement** | Hlahloba / ntsha / gossip ho phalla ha kgokahano hore mesh e kgethe tsela e nang le pipe e nonneng ka ho fetisisa (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Tsena di dula hodima ditshebeletso tse seng di phethehile tsa **messaging, lentswe la 1-to-1 le sehlopha, diletsetso tsa video, streaming e phelang, watch-together, routing ya AODV, DTN store-and-forward, le SOS flood** — tse boetseng di kentswe tshebetsong ka dipuo tsohle tse 8.

> **Se boleletsweng ke "e hahilwe" mona, ka ho nepahala.** Tshebeletso e nngwe le e nngwe e hlahisa mme e sebetsana le packet ya yona ya mohala, e phahamisa diketsahalo tse nepahetseng, mme e tiiselitswe ho fixture ya byte-level eo lelapa lohle la puo le tshwanetseng ho e lekana. Application ya hao e hokahanya tshebeletso le Signal session ya yona, tafole ya routing, le boemo ba lehae. Lena ke lera la protocol — le paketsweng ka khoutu, diteko, le di-byte-fixture tsa dipuo — ka boemo bo tshwanang ba RF bo tshepahalang jwaloka tsohle: tsela e nngwe le e nngwe e qetellang e palame seyalemoya ha e netefatswe tshimong ho fihlela ho phahamisa hardware ho latellwa ho `OPEN_ISSUES.md`.

## BitTorrent — ya sebele, e kopantswe le mesh

Aether jwale e kenyeletsa **implementeshene ya sebele, e sebedisanang ya BitTorrent** — protocol ya sebele eo di-torrent client tsa nnete di e sebedisang, eseng se tshwanang le yona feela. Kahoo node ya Aether e ka kena swarm e tlwaelehileng mme ya fapanyetsana dikotwana tsa faele le basele inthaneteng, ntle le sebedisi bohareng.

Ha re a ka ra bolela feela hore ke ya sebele — re e paketse. Aether e ile ya hlahlojwa kgahlano le **MonoTorrent**, laebrari e hodileng, e ikemetseng ya BitTorrent e hahilweng ke batho ba bang: ha di fuwa faele e tshwanang, ka bobedi di hlahisa fingerprint e *tshwanang hantle*, kahoo torrent client efe kapa efe ya sebele e nka Aether jwaloka e nngwe ya yona. Mang kapa mang a ka supa torrent client ya sebele ho yona mme a iponele.

Ho feta moo, Aether e eketsa **bridge**: node e nang le inthanete e ka nka torrent webong e pharaletseng, ya paka dikotwana tsa yona bocha e le di-chunk tsa mesh tsa Aether tse kentsweng ka encryption, mme ya e arolelana pele — kahoo motho ya se nang inthanete ho hang o ntse a ka amohela faele eo ho pholletsa le mesh ya offline. Ke wona morero: hokela marang-rang a maholo ka ho fetisisa a ho arolelana difaele lefatsheng ho batho bao ka tlwaelo a ke keng a ba fihlela.

**Moo e emeng teng, ka botshepehi.** Di*format* tsa BitTorrent — kamoo torrent e hlaloswang, e nkuwang fingerprint, mme e behwang mohaleng — di hahilwe mme di **tshwana byte-ka-byte ka dipuo tsohle tse 8**, di tiiselitswe ho corpus ya di-fixture tse arolelanwang ho `fixtures/bittorrent/`. Client e felletseng e sebetsang le mesh bridge di felletse mme di netefaditswe ho **C# reference**; dipuo tse ding tse supileng di jere diformat tse tshwanang tsa protocol, tse nang le lera la tsona la marang-rang a phelang e le mohato o latelang.

> **Bakeng sa bahlahisi.** Se akaretswang: bencode + `.torrent`/magnet + SHA-1 info-hash le BEP-3 peer-wire (rarest-first), HTTP + UDP trackers (BEP-3/15/23), Mainline DHT + PEX + ut_metadata (BEP-5/11/9/10), µTP (BEP-29), le BitTorrent v2 SHA-256 merkle (BEP-52), hammoho le **gateway** ya piece↔chunk ho tshebeletso ya diteng le downloader e arotsweng ka dikarolo, e tsamaisanang, e ka tsosolosuwang. C# reference (`src/AetherNet.BitTorrent`, `src/AetherNet.BitTorrent.Gateway`) e romela client ya TCP/µTP e phelang, DHT node, di-tracker, gateway, le downloader, ka teko ya MonoTorrent interop ho `tests/AetherNet.BitTorrent.Interop.Tests`. Corpus ya byte-identity ya dipuo tse 8 (`fixtures/bittorrent/vectors.json`, dikarolo tse 7) e akaretsa bencode, info-hash, peer-wire, µTP, merkle, compact-info, le KRPC; SDK e nngwe le e nngwe e romela teko ya fixture e tshwanang.

## Tshireletso le lekunutu

Ntle le sethi sa ditshebeletso tsa mohala, Aether e tsamaisa **lera le lenyane la tshireletso le lekunutu** — taolo ya dinotlolo tsa boitsebiso le thibelo ya ho latellwa boemong ba link-layer. Jwaloka tsohle tse ding, e nngwe le e nngwe e kentswe tshebetsong **ka dipuo tsohle tse 8** mme e tiiselitswe ho fixture e arolelanwang pakeng tsa dipuo tlasa `fixtures/<feature>/` (Swift le C di boetse di netefaditswe ho macOS build server). Tsena *hase* tse ding tse nne tsa ditshebeletso tse 18 tsa mohala: tse tharo *ha di* hlalose **mofuta o mocha wa packet ya mohala** ho hang, mme ya bone e jere di-envelope tsa yona **ka hare ho tsela e teng ya DTN/mesh** ho e-na le ho ba packet e ncha e behelletsweng.

| Bokgoni | Se se etsang | Lera | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Backup ya recovery-phrase** | Boloka boitsebiso e le poleloana ya **24-word BIP-39** mme o e tsosolose sesebedisweng sefe kapa sefe. BIP-39 e tlwaelehileng (e netefaditswe kgahlanong le di-vector tsa semmuso tsa Trezor), e nang le SHA-256 checksum e le hore lentswe le ngotsweng ka phoso *le hanwe*, le se ke la fosahala ka ho khutsa. Ha ho server, ha ho mohlokomedi — poleloana **ke** boitsebiso. | ka hae | `fixtures/bip39/` | ✅ |
| **Tshireletso ya ho latellwa ka Bluetooth** | E fumana BLE **Service UUID** e potolohang, e tswang ho notlolo (HMAC-SHA256, window ya metsotso e 15) le **di-resolvable private address** (IRK + tshebetso ya RFC `ah`, AES-128) — thepa ya ho thibela ho latellwa eo BLE advertiser e e hlokang e le hore scanner e sa sebetseng e se kgone ho e hokahanya nakong le sebakeng. | link-layer | `fixtures/bleprivacy/` | ✅ |
| **Panic-wipe** | **Duress PIN** (SHA-256, e bapiswang ka nako e sa fetoheng) e reng, tlasa qobello, e hlakola ka polokeho notlolo e nngwe le e nngwe ya boitsebiso — ngola-holima-random ebe zero — ho se sale letho le ka tsosolosuwang. | ka hae | `fixtures/panicwipe/` | ✅ |
| **Sync ya disebediswa tse ngata** | Sync e **sa buseng bohareng, e se nang server** ho pholletsa le disebediswa tsa *hao*: **DeviceLink** e saennweng ka Ed25519 e di kopanya, mme di-envelope tsa **SyncRecord** tsa last-write-wins di boelanya boemo — di jarwa di kwetswe ka E2E holima DTN/mesh e teng, ho se na akhaonto ya leru le server ya sync. | e palame DTN | `fixtures/sync/` | ✅ |

**Ho se lekane ho le hong ho tshepahalang.** **DeviceLink** ya disebediswa tse ngata e saennwe ka Ed25519, mme tekeno eo e **tshwana ka byte ho tse 7 tsa dipuo tse 8**. CryptoKit ya Apple e *etsa random* ka boomo ditekeno tsa Ed25519, kahoo ho Swift di-byte tse 64 tsa tekeno di fapana nako le nako — empa **mmele o saennweng o tshwana ka byte** mme kgokahano e nngwe le e nngwe e ntse e netefatswa ho di-SDK tsohle tse 8, kahoo Swift e fihlela parity ya **netefatso** ho e-na le parity ya byte ya tekeno. Ke thepa ya platform-crypto, eseng bofokodi, mme ke sona sebaka feela ho tsena tse nne moo "e tshwana ka byte" e nang le asterisk. Diformat tse felletseng tsa mohala di ho [`PROTOCOL_SPEC.md`](../../PROTOCOL_SPEC.md) §12; mohlala wa tshokelo o ho [`THREAT_MODEL.md`](../../THREAT_MODEL.md).

## Ditransport

Transport e nngwe le e nngwe e na le lebitso la mmala le sebediswang ho pholletsa le codebase. `IsAvailable` e thiba ditsela tse thibetsweng ke hardware — `TransportManager` e di tlola mme e wela ho transport e latelang e fumanehang.

**Senotlolo sa boemo:** ✅ ya sebele, e hahilwe & e netefaditswe · ⏳ ya sebele, netefatso e ntse e tsamaya · ⚠️ ya sebele ho di-platform tse ding, stub ho tse ding · ❌ stub (ha ho khoutu ya transport hona jwale).

| Mmala | Lebitso | Sebaka | Bandwidth | Boemo |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Real — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Real — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | Unlimited | ~10 Mbps | ✅ Real — Windows; relay server in `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Internet data channel | Unlimited | ~100 Mbps | ✅ Real in all 8 languages — **loopback-verified in all 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust each have two peers exchange bytes over a real ICE data channel) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Real on Android (`android/white/`); Windows = real BLE-GATT + RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`, compiles net9/10, runtime-unverified) — `Windows.Networking.Proximity` removed in Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Real on HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — pending on-device verification); Android + Windows = real SSAP-over-BLE approximation (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; compile + unit-test verified, runtime-unverified) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Real RYLR SX127x/SX126x serial driver (`LoRaSerialTransport` in C#/Go/Rust/C; compiles, runtime-unverified — needs a physical module); BLE Coded-PHY bridge still a documented design |

Di-transport tsa seyalemoya di ya sebele feela moo khoutu ya platform e leng teng (C#/Windows, Kotlin/Android, HarmonyOS). Ho seng jwalo, dilaebrari tse robedi tsa dipuo di romela transport ya **in-process simulation** bakeng sa ho testa — **WebRTC ke transport ya pele ya sebele e tshwanang ho tsohle** (e phethehile; e netefaditswe ka loopback ho pholletsa le dipuo).

Ntlha ya pele e latela tjhelete ya matla: mesh ya seyalemoya e ratwa, ebe WebRTC jwaloka tsela e tobileng ya inthanete, ka HTTP/QUIC relay jwaloka tsela ya ho qetela.

## Maemo a tsamaiso

Aether e sebetsa hoo platform e nngwe le e nngwe e tshehetsang Bluetooth kapa Wi-Fi. Emo eo o ho yona e itshetleha ho OS eo o e lebisang.

---

### Emo e tlwaelehileng — platform e nngwe le e nngwe

Android · Windows · Linux · macOS · iOS

Aether e sebetsa hoo sesebediswa se seng le se seng se nang le hardware ya Bluetooth kapa Wi-Fi. Moo seyalemoya se sieo ka bomamela, transport e nngwe le e nngwe e thibetsweng e etselitswe ho seo se fumanehang. Dihlaho tsena jwale ke **khoutu ya sebele** (e netefaditswe ka compile; **e sa netefatswa ka runtime** e emetse teko ya disebediswa tse 2 / RF ya hardware):

- **NearLink (Aether Teal)** — SSAP-over-BLE-GATT approximation ya sebele (Aether SLE UUID `61657468-6572-0003-…`) ho Android (`android/teal/AetherNetSleService`) le Windows (`WinNearLinkBleTransportService`); compile + unit-test e netefaditswe, e sa netefatswe ka runtime. Seyalemoya sa sebele sa NearLink se teng feela ho HarmonyOS (`harmonyos/teal/`, e emetse netefatso ho sesebediswa).
- **LoRa (Aether Red)** — RYLR SX127x/SX126x serial driver ya sebele (`LoRaSerialTransport` ka **dipuo tsohle tse 8** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; port e nngwe le e nngwe e netefaditswe ka compile, ho kenyeletswa Swift + C ho sesebediswa sa kaho sa Mac; e sa netefatswe ka runtime — e hloka module ya sebele). Meshtastic-over-BLE-Coded-PHY bridge (~1.3 km) e sala e le moralo o ngotsweng; LoRa ya sebele ya sebaka se selelele e hloka node e kgonang LoRa (gateway, SBC, kapa mohala o tiileng o nang le module ya LoRa).
- **NFC (Aether White)** — ya sebele ho Android (HCE). Windows jwale e na le BLE-GATT + RSSI −40 dBm proximity approximation ya sebele (`WinNfcBleTransportService`, e compile-a net9/10; e sa netefatswe ka runtime); ACR122U PC/SC ha sebadi se le teng.

Se leng sa sebele mme se tshwana hohle: **BLE, Wi-Fi Direct, HTTP/QUIC relay, le WebRTC P2P transport (e netefaditswe ka loopback ka dipuo tsohle tse 8)**, hammoho le tshireletso ya Signal Protocol (X3DH + Double Ratchet), routing ya AODV, DTN store-and-forward, phatlalatso ya SOS, lentswe, le streaming.

**Boemo bo tshepahalang:** BLE + Wi-Fi Direct + relay ke tsa sebele tsa tlhahiso; **WebRTC P2P ke ya sebele mme e netefaditswe ka loopback ka dipuo tsohle tse 8** (dithaka tse pedi di fapanyetsana di-byte ho pholletsa le ICE data channel ya sebele — Rust e netefaditswe ho `.201` Linux box ka ICE ya UDP e sebetsang); dihlaho tsa NearLink / LoRa / NFC-on-Windows jwale ke khoutu ya sebele e compile-ang (LoRa e netefaditswe ka compile ho tsohle tse 8, ho kenyeletswa Swift + C ho sesebediswa sa kaho sa Mac; NearLink-Android e boetse e testilwe ka yuniti) empa e **sa netefatswe ka runtime** — ha ho na teko ya hardware / disebediswa tse 2 tsa RF hona jwale. Di nka karolo ho mesh ka khoutu; se ke wa tsamaisa tse tharo tseo o lebeletse RF e netefaditsweng tshimong.

---

### Emo ya native — CircleOS / OpenHarmony

CircleOS · HarmonyOS · OS e nngwe le e nngwe e thehilweng ho OpenHarmony

CircleOS e hahilwe ho OpenHarmony, e romelang NearLink (SLE) silicon le `@kit.NearLinkKit` SDK jwaloka bokgoni ba OS ba maemo a pele. Ho disebediswa tsa CircleOS le HarmonyOS tse nang le hardware ya NearLink, ha ho hlokahale approximation — `harmonyos/teal/` e sebedisa seyalemoya sa sebele sa SLE ka kotloloho:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Ena ha se feela mofuta o motle ho feta wa emo e tlwaelehileng. Ho lera la NearLink ke marang-rang a fapaneng ka ho felletseng:

| Bokgoni | Emo e tlwaelehileng (BLE approx) | Emo ya native (CircleOS / OpenHarmony) |
|---|---|---|
| **Sebaka sa NearLink** | ~100 m (BLE) | **600 m** |
| **Bandwidth ya NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latency ya NearLink** | ~10 ms (BLE) | **20 µs** |
| **Matla a NearLink** | BLE baseline | **60% ka tlase ho BLE 5.0** |
| **Dithaka tsa NearLink tse tsamaisanang** | ~7 (moedi wa kgokahano ya BLE) | **500+** |
| **Mohlodi wa NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Seyalemoya sa sebele sa SLE (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Native | Native (e tshwanang) |
| **Tshireletso ya Signal Protocol** | E felletseng | E felletseng (e tshwanang) |
| **Routing / DTN / SOS** | E felletseng | E felletseng (e tshwanang) |
| **Boitsebiso ba Aether Tag** | E tshehetswa | E tshehetswa (e tshwanang) |

---

### Ho tsamaya pakeng tsa maemo

Ha ho diphetoho tsa khoutu tse hlokahalang. Emo e boloketswe ka runtime ke `IsAvailable` ho tshebeletso e nngwe le e nngwe ya transport:

1. Ho sesebediswa sa CircleOS kapa HarmonyOS se nang le NearLink silicon, `IsAvailable` ho transport ya NearLink e kgutlisa `true` (e hlahlobilwe ka hardware ka tlhahlobo ya tumello + teko ya passive scan).
2. `TransportManager` e phahamisa NearLink ka boiketlo ho boemo ba ntlha ya pele — tjhelete ya matla e tlase ka ho fetisisa, bandwidth e phahameng ka ho fetisisa.
3. Khoutu ya app, sebopeho sa packet, algorithm ya routing, lera la tshireletso, le Aether Tags di tshwana ho pholletsa le maemo ka bobedi.

Node e ho emo e tlwaelehileng le node e ho emo ya native di ka buisana ka bolokolohi — di arolelana sebopeho se le seng sa mohala, di-Signal Protocol session tse tshwanang, le Aether Tags tse tshwanang. Phapang ya emo e ama feela seyalemoya se sebediswang bakeng sa di-packet tsa NearLink, e seng protocol e ka hodima yona.

---

> **Ka hare maemo ana a bitswa mofuta wa Asterix (o tlwaelehileng) le mofuta wa Obelix (native).** Asterix o sebetsa hantle ka seo se fumanehang. Obelix — o sebetsang ho CircleOS ka NearLink ya native — o sebetsa ka bokgoni bo phahamisitsweng ka ho sa feleng, ka mokgwa oo Obelix a jereng matla a moriana wa maselamose ntle le ho hloka ho nwa hape.

---

## Diimplementeshene

Aether e hahilwe ka dipuo tse 8 hore e sebetse ho mehala, dilaptop, ditablete, le di-microcontroller. Diimplementeshene tsohle di hlahisa di-packet tse sebedisanang mohaleng — molaetsa o kentsweng ke node ya Rust o ka fetiswa ke node ya Python mme wa decrypt-uwa ke node ya Swift.

| Puo | Directory | Sebopeho sa mohala | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Voice/Group | Streaming/Video/Watch | BitTorrent |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |

**Kholomo ya BitTorrent:** ✅ = client e felletseng, e sebetsang + mesh gateway (C# reference). ◐ = **diformat tsa mohala** tsa BitTorrent di tshwana byte-ka-byte mona (di tiiselitswe ho `fixtures/bittorrent/`), tse nang le lera la marang-rang a phelang e le mohato o latelang — bona [BitTorrent — ya sebele, e kopantswe le mesh](#bittorrent--real-and-bridged-into-the-mesh). Kholomo e nngwe le e nngwe e sebetsa ka sebele ka dipuo tsohle tse 8.

Dipuo tsohle tse 8 di hlahisa di-packet tsa mohala tse tshwanang byte, tse netefaditsweng kgahlano le di-fixture tse 17 tsa sebopeho sa mohala tse tlwaelehileng le di-Signal test vector tse 6 (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`) — puo e nngwe le e nngwe e hlahlobwa kgahlano le di-byte tse tshwanang. Routing (AODV-style RREQ/RREP), DTN store-and-forward, phatlalatso ya SOS, lentswe, streaming, le ditshebeletso tsa ho tiisa tshireletso di kentswe tshebetsong ka puo e nngwe le e nngwe ka **diteko tse ka bang 3,000** ho pholletsa le diimplementeshene tsohle tse 8:

| Puo | Diteko | Platform ya Teko |
|----------|------:|-------------|
| C# (.NET 10) | 530 | Linux |
| TypeScript / Node 20 | 459 | Linux |
| Kotlin / JVM 21 | 457 | Linux |
| Go 1.22 | 423 | Linux |
| Python 3.12 | 387 | Linux |
| Swift 6 | 295 | macOS |
| C (GCC) | 253 | Linux |
| Rust (stable) | ~195 | Linux |
| **Kakaretso** | **~3,000** | |

Signal interop ya dipuo e tiiselitswe ho `fixtures/signal/` ka di-test vector tse arolelanwang bakeng sa X3DH (`x3dh_basic`), symmetric ratchet (`ratchet_step_basic`, `ratchet_step_three_iterations`), KDF_RK (`kdf_rk_basic`), le potoloho e felletseng ya session ya X3DH (`x3dh_session_msg1`, `x3dh_session_reply`). Implementeshene e nngwe le e nngwe e tshwanetse ho hlahisa di-output tse tshwanang byte kgahlano le di-fixture tseo. Dipuo tsohle tse 8 jwale di romela Signal session e felletseng (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Ho feta sebopeho sa mohala le Signal, **sete e felletseng ya ditshebeletso tsa mohala** — presence, heartbeat, profile sync, ephemeral-ID announce, pre-key exchange, channels, push-to-talk, screen share, call control, SOS acknowledgement, space breadcrumbs, forge announce, vault shard request, le bandwidth measurement (bona **Seo o se fumanang**) — le yona e kentswe tshebetsong ka dipuo tsohle tse 8 mme e tiiselitswe ho di-fixture tsa yona (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, le bo-ausi). Ha ho karolo e leng ya C#-feela ho lera la protocol.

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Demo e o tsamaisa ka mehato e 8: ho hlahisa dinotlolo tsa boitsebiso tsa Ed25519 bakeng sa di-node tse tharo (Alice, Bob, Charlie), ho theha di-Signal Protocol session, ho romela melaetsa e kentsweng, ho fetisa molaetsa ka Charlie (ya sitwang ho o bala), ho bontsha sebopeho sa binary sa mohala, le ho bontsha forward secrecy ho pholletsa le melaetsa e 5 e latellanang. Output e na le mmala mme e emisa pakeng tsa mehato.

**Romela molaetsa ka C#:**

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

Demo e hlahisa dinotlolo tsa boitsebiso bakeng sa di-node tse pedi, e fapanyetsana di-pre-key bundle, e theha di-session tse kentsweng, e romela melaetsa e kentsweng ka ditsela tse pedi, e etsa mme e saena di-mesh packet, e netefatsa di-signature, mme e serialize di-packet ho sebopeho sa binary sa mohala. E boetse e bontsha lera la transport la in-process.

**Romela molaetsa ka Rust:**

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

Demo e etsa di-node tse pedi marang-rang a etselitsweng, e hlahisa dinotlolo tsa Ed25519, e theha di-Signal Protocol session, e etsa mme e saena packet, e e serialize ho sebopeho sa binary se sebedisanang le C#, e kenya molaetsa wa lekunutu, e o decrypt-a ho node e nngwe, e o romela ka transport, mme e netefatsa round-trip.

**Romela molaetsa ka TypeScript:**

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

Demo e etsa dipontsho tse 8: tlhahiso ya notlolo ya Ed25519 le ho lemoha ho ferekanya, tlhahiso ya node e nang le bokgoni, Signal Protocol X3DH key exchange, AES-256-GCM encryption le decryption, packet serialization, ho saena packet ka ho lemoha replay, transport ya in-process, le phallo e felletseng ya end-to-end e kopanyang maqhubu wohle.

**Romela molaetsa ka Python:**

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

Demo e etsa dipontsho tse 5: di-round-trip tsa packet serialization, ho saena ka Ed25519 ka ho lemoha ho ferekanya, ho theha Signal Protocol session ka messaging e kentsweng ka ditsela tse pedi, transport ya in-process pakeng tsa dithaka tse pedi, le nonce deduplication bakeng sa tshireletso ya replay.

**Romela molaetsa ka Go:**

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

Demo e tsamaya ka mehato e 11: tlhahiso ya notlolo, tlhahiso ya node e nang le bokgoni, ho qala Signal Protocol, ho fapanyetsana pre-key bundle, ho theha session, ho etsa le ho saena packet, serialization, deserialization ka ho netefatsa signature, end-to-end encryption ka key ratcheting, ho lemoha replay attack, le transport ya in-process.

**Romela molaetsa ka Kotlin:**

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

Demo e etsa diteko tse 5: di-round-trip tsa packet serialization, ho saena ka Ed25519 ka ho hana ho ferekanya, ho theha Signal Protocol session ka AES-256-GCM encryption, ho fana ka molaetsa ka transport ya in-process, le phallo e felletseng ya end-to-end moo Alice a saenang packet mme Bob a e netefatsa ka mora transport.

**Romela molaetsa ka Swift:**

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

Demo e etsa dipontsho tse 7: tlhahiso ya notlolo ya Ed25519, tlhahiso le ho saena packet, serialization ho sebopeho sa binary sa mohala, deserialization ka ditlhahlobo tsa botshepehi, AES-256-GCM encryption le decryption, HMAC-SHA256 message authentication, le HKDF-SHA256 key derivation.

**Romela molaetsa ka C:**

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

Se hahilweng le se latelang.

**E entswe (e netefaditswe ho pholletsa le dipuo, diimplementeshene tsohle tse 8):**
- Sebopeho sa mohala: se tshwanang byte ho pholletsa le dipuo tse 8, se tiiselitswe ke di-fixture tse 17 tse tlwaelehileng le diassertion tsa dipuo (`fixtures/expected/*.bin`)
- **GitHub Actions workflow (e hlalositswe, eseng heke ya hajwale)** — matrix ya mesebetsi e 9 (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS, Rust stable, C/GCC, hammoho le mosebetsi wa botshepehi ba fixture) e hlalositswe ho `.github/workflows/ci.yml`. Di-commit hajwale di sunngwa ka `[skip ci]`, kahoo tiiso ya sebele ke corpus ya di-fixture e sebetsang **ka lehae, ka puo ka nngwe** (Swift le C ho sesebediswa sa kaho sa macOS); CI e ka bulwa hape ntle le diphetoho tsa khoutu.
- Ed25519 packet signing le netefatso
- AES-256-GCM encryption
- HKDF / HMAC key derivation primitives
- Packet serialization + sebopeho sa ho saena (LE + di-field tsa int32 tsa di-byte tse 4)
- In-process transport simulator (bakeng sa ntshetsopele le diteko)
- Tshebeletso ya routing e susumeditsweng ke AODV ka RREQ/RREP, dikarabo tsa tsela tse saennweng, dedup, TTL forwarding
- Tshebeletso ya DTN store-and-forward ka custody transfer, geohash-aware replication, 72h TTL
- Tshebeletso ya phatlalatso ya SOS ka flood, dedup, self-origin guard, rate-limit (3/hr)
- Diseam tsa ho eketseha: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (Noop defaults)
- **Diteko tse ka bang 3,000** ho pholletsa le dipuo tsohle tse 8 (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — tsohle di tala, di sebetsa ka puo ka nngwe (Swift le C ho sesebediswa sa kaho sa macOS)
- ✅ **X3DH ephemeral key ya sebele (dipuo tse 8)** — di-X25519 DH tse 4 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) ka HKDF-SHA256 root derivation. E tiiselitswe ke `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Double Ratchet alignment lelapa lohle** — Signal §5 e felletseng ka HMAC-SHA256 + 0x01/0x02 domain separation ho symmetric ratchet, HKDF-SHA256 KDF_RK ho mohato wa DH-ratchet, DH-rotation ha o amohela. E netefaditswe ke di-fixture tsa `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 e boelanngwe le HEAD** — bona `docs/PROTOCOL_SPEC.md`.

**E entswe (dipuo tsohle tse 8):**
- ✅ **Diletsetso tsa lentswe (1-to-1)** — signaling state machine (Offer/Answer/Hangup/Cancel/Timeout) + binary frame transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Phano e tsebang tsela ka `IRoutingService`.
- ✅ **Lentswe la sehlopha** — botho bo tsamaiswang ke host (invite/kick/leave), per-frame key generation field, unicast fan-out ho ditho tsohle tsa hona jwale, host-controlled key rotation ha botho bo fetoha.
- ✅ **Streaming e phelang** — publisher e phatlalatsa `StreamAnnounce`; di-subscriber di romela `StreamSubscribe`; di-frame tsa binary tsa `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) di unicast ho subscriber e nngwe le e nngwe.
- ✅ **Diletsetso tsa video (1-to-1)** — codec/resolution/fps/bitrate negotiation ho signaling, keyframe-request le quality-change signals, sebopeho sa binary sa `VideoFrame` se lekanang le sebopeho sa lentswe.
- ✅ **Watch Together** — host e ntsha ditaelo tsa bolaodi tsa `WatchSync` (play/pause/seek/speed); balatedi ba di sebedisa ka phetetso ya RTT (`position = positionMs + elapsed × playbackSpeed`); fire-and-forget `WatchReaction`.
- ✅ **One-time pre-key (OPK) pool** — 100 ka tlwaelo, FIFO issue, lazy top-up, tshebediso e sireleditsweng ka locko ho pholletsa le dipuo tsohle tse 8. E kwala kotsi ya single-OPK concurrency.
- ✅ **C: Signal session e felletseng** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` ho `c/src/signal_protocol.c`; diteko tse 6 tsa two-node E2E ho `c/tests/test_signal_session.c`. Dipuo tsohle tse 8 jwale di na le Signal Protocol e kgonang session e felletseng.

**E entswe (dipuo tsohle tse 8 — sete e felletseng ya ditshebeletso tsa mohala):**
- ✅ **Mofuta o mong le o mong wa packet o boloketsweng jwale ke tshebeletso ya sebele, e tshwanang byte ho dipuo tsohle tse 8.** Presence beacon/query (21/22), heartbeat (10), profile sync (23), ephemeral-routing-ID announce (56), pre-key exchange (25/26), channels (7), push-to-talk (15), screen share (32), call control (27), SOS acknowledgement (6), space breadcrumbs (40), forge announce (41), vault shard request (42), le bandwidth measurement / ABMF (53/54/55). E nngwe le e nngwe ke tshebeletso e tshesane (produce + handle + event) eo host e e hokahanyang le Signal session ya yona le tafole ya routing; e nngwe le e nngwe e tiiselitswe ho fixture e arolelanwang pakeng tsa dipuo (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) mme e etswa ka diteko tsa yuniti tsa puo ka nngwe, ka Swift le C tse netefaditsweng ho sesebediswa sa kaho sa macOS. Bona **Seo o se fumanang**.

**E entswe (C# reference feela):**
- ✅ **Demo Step 9 — MessagingService + DTN fallback end-to-end** — `samples/AetherNet.Demo.Console` e tsamaya ka real-Signal-encrypted messaging ka DTN store-and-forward ha moamohedi a se ithontse.
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` bridge** — `SignalMessageEnvelopeCipher` e etsa hore lera la messaging le be end-to-end encrypted ka tlwaelo; melaetsa e se nang Signal session e beelwa moleng, ha e ke e romelwe ntle le tshireletso.
- ✅ **Adaptive bitrate streaming** — `AdaptiveBitrateController` ka bitrate ladders tse hlokwang ke spec bakeng sa Profile A (real-time), B (live broadcast), le C (VOD). Publisher e kgetha rung e phahameng ka ho fetisisa e ka tshehetswang (20% headroom) mme e ntsha `StreamAbandon` (`PacketType.StreamAbandon`) sebakeng sa segment ha e le ka tlase ho mokato. `IStreamingService` e pepesa `UpdateBandwidthEstimate` le `GetCurrentBitrateRung`.
- ✅ **Watch Together: BitTorrent ingest + ChipIn group funding** — `TorrentInfo` / `TorrentFile` models; `WatchTogetherService` e sebetsana le `PacketType.TorrentMetadata` mme e ntsha `TorrentReceived`. `ChipInPool` / `ChipInContribution` state machine (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` ho `IWatchTogetherService`.
- ✅ **Diletsetso tsa video tsa sehlopha ka auto SFU relay** — `GroupVideoService` / `IGroupVideoService`. FullMesh topology bakeng sa banki-nkakarolo ba ≤ 3; phetoho ya boiketlo ho SFU ho `SfuThresholdParticipants` (4) ka relay re-assignment ka `GroupVideoSignaling(SfuAssigned)`. Fan-out ho FullMesh, relay-only send ho SFU mode. Signaling packet type `GroupVideoSignaling = 35`.
- ✅ **BLE GATT transport simulation** — `SimulatedBleGattTransportService` (`IBleTransportService`). GATT MTU framing ka `BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), in-process static peer registry, phatlalatso ya advertisement. Ditlhoko tsohle tsa `BleMaxPayloadBytes` di tiisitswe.
- ✅ **Wi-Fi Direct transport simulation** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Explicit `ConnectAsync`/`DisconnectAsync` lifecycle, phano e tobileng ya payload e kgolo (ha ho framing), diketsahalo tse pedi tsa `PeerConnected`/`PeerDisconnected`.
- ✅ **NearLink transport simulation** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096 B frame MTU, 500-peer registry, `ConnectedPeerCount`, `IsAvailable` e ka behwang ka runtime.
- ✅ **Diteko tsa RF bring-up simulation** — Diteko tsa two-node interop (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` round-trip, WiFi Direct 64 KB payload transfer. Lera la software le netefaditswe ka botlalo; ho hlokahala tshebetso ya lab ya sesebediswa sa mmele bakeng sa netefatso ho hardware.

**E entswe (C# transport layer — kaofela fail-fast):**
- ✅ **BLE GATT real transport** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server). Teko e felletseng ya RF bring-up ho `samples/AetherNet.BleRfTest/`.
- ✅ **Wi-Fi Direct real transport** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Teko ya RF ho `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **HTTP relay transport (Aether Purple)** — `HttpRelayTransportService` ka 10-second long-poll, `PowerCostRelative = 100`, kamehla tsela ya ho qetela. Relay server ho `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). Teko ya RF ho `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` e sebedisa `HostApduService` ka AID `F061657468657200`. `WinNfcStubTransportService` e ngola ditsela tse pedi tsa Windows approximation: (1) NDEF-over-BLE-GATT ka RSSI gate ≥ −40 dBm (e etsisa tap-to-connect ntle le NFC silicon, `IsAvailable = Bluetooth present`); (2) ACR122U USB reader ka `Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). Tsela ya ho ntlafatsa: sebedisa `ITransportService` ha Microsoft e romela first-party P2P NFC API.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implementeshene e felletseng ya HarmonyOS 5.0.1 (API 13) ArkTS e sebedisang `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` e hlahlobilwe ka runtime. `WinNearLinkStubTransportService` + `android/teal/` di ngola SSAP-over-BLE approximation: BLE GATT ka Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — API-analogous ho SSAP, e sa sebedisane mohaleng le hardware ya sebele ya NearLink. Tsela ya ho ntlafatsa: bea diteseletso tsa BLE GATT ka `ssapc_*`/`ssaps_*` SDK calls; di-UUID le `TransportManager` slot ha di fetohe.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` di ngola Meshtastic-over-BLE-LR approximation: Meshtastic wire format e felletseng (16-byte header + AES-256-CTR protobuf) hodima BLE 5.0 Coded PHY S=8 (~1.3 km outdoor), ka managed-flood routing le RSSI-weighted contention window. Bridge-node federation ka LoRa hardware ya sebele e sebetsa ka boiketlo (Meshtastic packet format e tshwanang, ha ho phetolelo). Tsela ya ho ntlafatsa: bea BLE LR radio ka SX1276/SX1278 AT-command kapa SPI driver; packet format le routing ha di fetohe.

**E bulehileng — e latellwa ho `OPEN_ISSUES.md`:**
- RF bring-up ho hardware ya sebele: teko ya end-to-end ya two-node interop ho disebediswa tsa mmele tsa BLE / Wi-Fi Direct (diteko tsa simulation di feta; ho hlokahala tshebetso ya lab ya hardware)
- NearLink: `harmonyos/teal/` e felletse; e hloka hardware ya Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (NearLink silicon ha e teng ho disebediswa tseo e seng tsa Huawei). Windows + Android di wela ho SSAP-over-BLE approximation ka boiketlo.
- LoRa / CircleLink: module ya seyalemoya e hlokahala bakeng sa sebaka sa sebele sa LoRa. Ntle le yona, Meshtastic wire format e jarwa hodima BLE LR (~1.3 km) mme bridge-node federation ka LoRa hardware ya sebele e a fumaneha.
- ✅ **(RESOLVED v1.2.0)** Consumer protocol surface (Wave 16/17) — `IDtnService.BundleReceived` event bakeng sa di-bundle tse kenang ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), application-layer naming/discovery directory ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), author-tipping interface ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Tsohle tse 3 di romelwe ka ho eketsa ho pholletsa le dipuo tse 8 ka di-fixture tsa dipuo tse lekanang byte. Bona CHANGELOG.

**E esong ho bulehe bakeng sa monehelo wa ka ntle:**
- Protocol e ntse e le tlasa ntshetsopele e matla. Menehelo ya ka ntle ha e amohelwe hona jwale.
- Implementeshene ya transport ya NearLink, dimehlala tsa integration tsa Android/iOS, di-transport backend tse eketsehileng, dibenchmark tsa performance, le protocol fuzzing di latellwa ka hare mme di tla bulwa ha projeke e fihla boemong bo tsitsitseng ba monehelo wa phatlalatsa.

## Sebopeho sa Projeke

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

## Ho Eketsa Transport e Ntjha

Sebedisa `ITransportService`:

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

E ngodise ho DI mme `TransportManager` e tla e kenyeletsa ka boiketlo ho kgetho ya transport, e hlophisitswe ka tjhelete ya matla.

## E Bapiswa Jwang

| Protocol | Moedi | Molemo wa Aether |
|----------|-----------|-----------------|
| **Briar** | Android-feela, e itshetlehileng ho Tor | Cross-platform, mesh e hlwekileng |
| **Meshtastic** | LoRa feela (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), e kgona lentswe le streaming |
| **Reticulum** | Python, sechaba se senyenyane | Dipuo tse 8, e sebedisana mohaleng ho tsohle tsa yona |
| **libp2p** | E nka mokokotlo wa inthanete | Offline-first, e sebetsa ntle le meaho |
| **Yggdrasil** | Overlay network, e hloka inthanete | Physical-layer mesh, e sebetsa ntle le inthanete |
| **Signal** | Ha ho mesh, e hloka inthanete | E sebetsa offline, P2P, mesh relay, encryption e tshwanang ya E2E |

## Dipotso tse botswang khafetsa

**Na AetherNet e sebetsa ntle le inthanete?**
E — ke offline-pele. Disebediswa di buisana ka kotloloho ka Bluetooth, Wi-Fi Direct, NearLink, kapa LoRa mme di fetisa melaetsa ntlha-ka-ntlha ka disebediswa tse ding, ntle le kgokahano ya inthanete, tora ya selefouno, kapa sebedisi. Ha ho se na tsela e phelang, melaetsa e bolokwa (delay-tolerant store-and-forward) ho fihlela dihora tse 72 ho fihlela e nngwe e buleha.

**Na e kentswe ka encryption ya end-to-end?**
E. AetherNet e sebedisa Signal Protocol (X3DH key agreement hammoho le Double Ratchet hodima X25519) bakeng sa encryption ya end-to-end, AES-256-GCM bakeng sa di-payload tsa melaetsa, le di-signature tsa Ed25519 ho packet e nngwe le e nngwe. Disebediswa tse fetisang molaetsa ha di kgone ho o bala.

**E sebedisa di-transport dife?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), LoRa/CircleLink serial radio, HTTP/QUIC relay, le WebRTC bakeng sa direct internet peer-to-peer. Protocol e kgetha ka boiketlo transport e nang le matla a tlase ka ho fetisisa e fumanehang packet e nngwe le e nngwe mme e wela ho e latelang.

**E fumaneha ka dipuo dife tsa dinhlaloso?**
Tse robedi — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, le C. Implementeshene e nngwe le e nngwe e hlahisa di-packet tsa mohala tse tshwanang byte, tse tiisitsweng ke corpus ya di-fixture tse arolelanwang pakeng tsa dipuo eo implementeshene e nngwe le e nngwe e hlahlobwang kgahlano le yona, kahoo packet e hahilweng ke puo e nngwe e decode-uwa e sa fetohe ke e nngwe le e nngwe.

**E fapane jwang le Meshtastic, Briar, kapa Bridgefy?**
Meshtastic ke LoRa-feela; AetherNet ke multi-transport (Bluetooth + Wi-Fi + NearLink + LoRa) mme e jara lentswe, video, le streaming hammoho le melaetsa. Briar ke Android-feela mme e fetisa hodima Tor; AetherNet ke cross-platform mme ke mesh e hlwekileng. Ho fapana le di-SDK tse kwetsweng, AetherNet e na le laesense ya MIT mme e kentswe tshebetsong phatlalatsa ka dipuo tse robedi. Tafole ya papiso ka hodimo e na le dintlha.

**Na e itokiseditse tlhahiso?**
Lera la protocol — sebopeho sa mohala, tshireletso ya Signal, routing, DTN store-and-forward, le sete e felletseng ya ditshebeletso — le kentswe tshebetsong mme le testilwe ho pholletsa le dipuo tsohle tse robedi. Di-transport tsa seyalemoya di ya sebele moo khoutu ya platform e leng teng (Bluetooth le Wi-Fi ho Windows le Android, WebRTC hohle) mme ha di netefatswe tshimong kae kae ho emetse ho phahamisa hardware, ho latellwang ka botshepehi ho `OPEN_ISSUES.md`. Bala di-note tsa boemo karolong e nngwe le e nngwe pele o tsamaisa.

**E na le laesense efe?**
MIT — ya mahala bakeng sa tshebediso ya khwebo le ya open-source. Bona [LICENSE](LICENSE).

**Ke mang ya hahang AetherNet?**
E ntlafatswa e le protocol e bulehileng ka mora mesh ecosystem ya The Geek Network, e hahilwe Afrika Borwa bakeng sa dikgokahano tse sebetsang ka kapa ntle le mobile data.

## Dintlha tsa Katoloso

Protocol e sebetsa e le nngwe. Di-interface tsena di o dumella ho kenya backend ya hao haeba o e batla:

- `IAetherNetIncentiveProvider` — putsa di-node tse fetisang traffic (no-op default: altruistic relaying)
- `IAetherNetBackendClient` — ngwahanya le sebedisi ha inthanete e le teng (no-op default: fully offline)
- `IAetherNetFeatureFlagProvider` — bulela / kwala dikarolo tsa protocol ka runtime (no-op default: everything enabled)

Tsohle tse tharo di romela ka diimplementeshene tsa no-op. Di tlose mme ha ho letho le senyehang.

## Ho Kenya Letsoho

Menehelo ya ka ntle ha e so bulwe. Projeke e ntse e le tlasa ntshetsopele e matla. Kgutlela ha re phatlalatsa lebati la monehelo wa phatlalatsa.

## Tshireletso

Bona [SECURITY.md](SECURITY.md) bakeng sa leano la ho senola ka boikarabelo.

## Laesense

MIT License. Bona [LICENSE](LICENSE).

## Diphetolelo

README ena e boetse e bolokwa ka dipuo tse ding tse thathamisitsweng ho bareng ya dipuo ka hodimo ho faele ena, ka tlasa [`docs/i18n/`](docs/i18n/) — e akaretsa dipuo tsa Europe, Asia Bochabela, Bochabela bo Hare, Asia Boroa, Asia Boroa-Bochabela, le Afrika, hobane marang-rang a hahetsweng batho ba se nang data ha a lokela ho ba le lebati la ka pele leo feela ba hokahaneng hantle ba ka le balang. **Mofuta wa Senyesemane ke mohlodi wa nnete**: moo phetolelo le mongolo wa Senyesemane di sa dumellaneng, mongolo wa Senyesemane ke ona o nang le taolo, mme diphetolelo di ka o setseha morao ka tokollo e le nngwe kapa tse pedi. Protocol, khoutu, di-fixture, le boitshwaro bo hlalositsweng di tshwana ho sa kgathalehe hore o bala puo efe.
