# AetherNet — offline-first mesh networking ፕሮቶኮል

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet ክፍት-ምንጭ፣ በ MIT-license የተፈቀደ mesh networking ፕሮቶኮል ነው** መልእክቶችን፣ ፋይሎችን፣ ድምጽንና ቪዲዮን ለአቅራቢያ ላሉ ሰዎች ለመላክ — **ኢንተርኔት የለም፣ ሰርቨሮች የሉም፣ ምዝገባ የለም**። መሣሪያዎች በ Bluetooth፣ Wi-Fi Direct፣ NearLink፣ እና LoRa በኩል በቀጥታ ይገናኛሉ፤ ተቀባዩ ከክልል ውጭ ሲሆን፣ መልእክቶች በሌሎች መሣሪያዎች በኩል ይዘላሉ እና መንገድ ለማግኘት እስከ 72 ሰዓት ይጠብቃሉ። በ **byte-for-byte ተመሳሳይ implementations በስምንት programming languages** ይላካል — C#፣ Rust፣ TypeScript፣ Python፣ Go፣ Kotlin፣ Swift፣ እና C።

ፋይሎችን፣ መልእክቶችንና ስትሪሞችን ከአቅራቢያዎ ካሉ ሰዎች ጋር ያጋሩ። WiFi የለም። የሞባይል ዳታ የለም። ምዝገባ የለም። እንደ AirDrop ነው፣ ብቻ ከሁሉም ሰው ጋር፣ በሁሉም መድረክ ላይ ይሠራል።

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **አንድ ፕሮቶኮል፣ ስምንት ቋንቋዎች፣ በ wire ላይ ተመሳሳይ።** Aether በ **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, እና C** ተተግብሯል — እያንዳንዱ packet በሁሉም ውስጥ byte-for-byte ተመሳሳይ ነው፣ በ CI ውስጥ በጋራ በሚጋራ cross-language fixture corpus አማካኝነት ተጠብቋል። ኖድዎን ከስምንቱ በማንኛውም ይገንቡ፤ ከሌሎቹ ሁሉ ጋር ይተባበራል። ይህ README በ 11 የሰው ቋንቋዎችም ይገኛል (ማገናኛዎቹ ከላይ)።

## በእሱ ምን ማድረግ ይችላሉ?

**የትምህርት ማስታወሻዎችን ዳታ ሳያወጡ ያጋሩ።**

በጥናት ቡድን ውስጥ ነዎት። አንድ ሰው በስልኩ ላይ የቀድሞ ፈተናዎች አሉት። Aether በ Bluetooth በኩል በቀጥታ ወደ መሣሪያዎ ይልካቸዋል — hotspot የለም፣ የWhatsApp ቡድን የለም፣ የፋይል መጠን ገደብ የለም። በቡድኑ ውስጥ ያለ አንድ ሰው ከክልል ውጭ ከሆነ፣ ፋይሉ እስኪደርሳቸው ድረስ በሌሎች መሣሪያዎች ላይ ይዘላል። አስፈላጊ ከሆነ መልእክቶች መንገድ ለማግኘት እስከ 72 ሰዓት ይጠብቃሉ።

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**በዙሪያዎ ምን እየተከሰተ እንዳለ ይወቁ።**

በካምፓስ ዝግጅት ወይም በፌስቲቫል ላይ ነዎት። Aether በ Bluetooth እና በ WiFi Direct በኩል በአቅራቢያ ያሉ ሌሎች መሣሪያዎችን ያገኛል — የመተግበሪያ feed የለም፣ አልጎሪዝም የለም። እውነተኛ በዙሪያዎ ያለውን ያያሉ፣ የተስፋፋውን ሳይሆን።

**ምንም ምልክት በሌለበት ጊዜ SOS ይላኩ።**

ስልክዎ ምንም አቀባበል የለውም። Aether ለክልል ውስጥ ለሚገኝ ለእያንዳንዱ መሣሪያ የአደጋ መልእክት ያሰራጫል፣ እነዚያ መሣሪያዎችም ያስተላልፉታል። የሞባይል ማማ አያስፈልግም።

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**የግል የቡድን ቻናሎችን ይፍጠሩ።**

ለመኖሪያ ወለልዎ፣ ለማህበርዎ፣ ለፕሮጀክት ቡድንዎ አንድ ቻናል። የተረጋገጡ አባላት ብቻ መልእክቶችን ማንበብ ወይም መላክ ይችላሉ። ምንም ሰርቨር ውይይቱን አያስቀምጥም።

**ለአቅራቢያ ሰዎች ነገሮችን ይሽጡ።**

የመማሪያ መጽሐፍ ለሽያጭ ያስቀምጡ። በ mesh ክልል ውስጥ የሚያልፉ ሰዎች ያዩታል። የገበያ ቦታ መለያ የለም፣ የዝርዝር ክፍያ የለም — ቅርበት ብቻ።

**በ mesh በኩል፣ አብራችሁ ፊልም ተመልከቱ።**

ቡድንዎ የፊልም ምሽት አለው። አንድ ሰው ፋይሉ አለው። Aether ማጫወቱን በእያንዳንዱ መሣሪያ ላይ ያመሳስላል — play፣ pause፣ seek — ሁሉም በአንድነት። አንዳንድ ሰዎች ብቻ ፋይሉ ካላቸው፣ mesh በእውነተኛ ጊዜ እንደ P2P stream ያሰራጫል። ማንም ከሌለው ሁሉም ለመግዛት በ SDPKT በኩል ያዋጣሉ።

## እንዴት እንደሚሠራ

መሣሪያዎች Bluetooth፣ WiFi Direct ወይም NearLink በመጠቀም እርስ በርስ በቀጥታ ይነጋገራሉ። የኢንተርኔት ግንኙነት የለም፣ ሰርቨር የለም፣ ማዕከላዊ መሠረተ ልማት የለም።

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

አንድ መልእክት መድረሻውን በቀጥታ ማድረስ ሳይችል ሲቀር፣ በሌሎች መሣሪያዎች ላይ ይዘላል። እነዚያ የማስተላለፊያ መሣሪያዎች የሚይዙትን ማንበብ አይችሉም — እያንዳንዱ መልእክት በ AES-256-GCM ተመስጥሯል። እያንዳንዱ packet በ Ed25519 identity keys ተፈርሟል፣ የተጭበረበሩ packets በኔትወርኩ ይጣላሉ።

> **የደህንነት ብስለት ማስታወሻ (ከመላክ በፊት ያንብቡ):** እውነተኛ X3DH (4 X25519 DHs)፣ ሙሉ የ Signal Double Ratchet (በተቀበሉ ጊዜ የ DH-rotation ደረጃ፣ KDF_RK፣ 0x01/0x02 chain ratchet)፣ እና the one-time pre-key pool (ነባሪ 100 OPKs፣ FIFO፣ lock-protected) በ **ሁሉም 8 ቋንቋዎች** ተተግብረዋል እና በ `fixtures/signal/` ስር ወዳለ በጋራ በሚጋራ cross-language fixture corpus ተጠብቀዋል። ብቸኛው የቀረው ክፍት ነገር በእውነተኛ BLE hardware ላይ ያለው የፊዚካል RF bring-up ነው (በ `OPEN_ISSUES.md` ውስጥ ተከታትሏል)።

መለያዎች የሉም፣ የስልክ ቁጥሮች የሉም፣ ኢሜይሎች የሉም። keypair ይፈጥራሉ እና በኔትወርኩ ላይ ነዎት።

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

**Routing** — AODV ከተፈረሙ route replies ጋር። እያንዳንዱ route reply በመድረሻው Ed25519 key ተፈርሟል፣ ስለዚህ ማንኛውም መሣሪያ ያልሆነውን መድረሻ መስሎ ማቅረብ አይችልም።

**Store-and-forward** — ቀጥታ route በሌለበት ጊዜ፣ መንገድ እስኪከፈት ድረስ packets እስከ 72 ሰዓት ይያዛሉ።

**Transport selection** — ፕሮቶኮሉ ለእያንዳንዱ packet ትክክለኛውን transport ይመርጣል። ትንንሽ የመቆጣጠሪያ መልእክቶች በ BLE ይሄዳሉ። የጅምላ ማስተላለፊያዎች WiFi Direct ይጠቀማሉ። NearLink ሲገኝ።

**ድምጽ፣ ቪዲዮ እና streaming** — በ codec negotiation (H.264/H.265/VP8) የቪዲዮ ጥሪዎች፣ transport-aware quality selection፣ በራስ-ሰር SFU relay ያለው የቡድን ቪዲዮ፣ በ RTT compensation የተመሳሰለ watch-together፣ እና adaptive bitrate streaming።

**Replay protection** — በ 5 ደቂቃ timestamp freshness window ያለው Nonce deduplication።

## የምታገኘው — እያንዳንዱ አገልግሎት፣ በእያንዳንዱ ቋንቋ

Aether transport ብቻ አይደለም። በፕሮቶኮሉ የተያዘ እያንዳንዱ packet type አሁን በ **ሁሉም 8 ቋንቋዎች እውነተኛ፣ የሚሠራ አገልግሎት** ነው፣ እያንዳንዱም ወደ **byte-identical wire packets** ይሰራሠራል — በ Go ኖድ የተገነባ packet፣ ሳይለወጥ፣ በ Swift፣ Rust፣ C፣ Python፣ TypeScript፣ Kotlin ወይም C# ኖድ ይፈታል። እያንዳንዱ አገልግሎት በ `fixtures/<service>/` ስር ወዳለ በጋራ በሚጋራ cross-language fixture ተጠብቆ በእያንዳንዱ ቋንቋ unit tests ይፈተናል፣ Swift እና C ደግሞ በ macOS build server ላይ በተጨማሪ ተረጋግጠዋል።

| አቅም | ምን እንደሚያደርግ | Packet type(s) | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Presence beacon & query** | "እኔ እዚህ ነኝ" ብሎ ማወጅ እና "ማን በዙሪያ አለ?" ብሎ መጠየቅ — በ **rotating፣ key-derived ephemeral ID** (እውነተኛ ማንነትዎ ሳይሆን) ላይ ከ coarse geohash ጋር | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | በተገናኙ peers መካከል ቀላል የ liveness keep-alive | 10 | `fixtures/heartbeat/` | ✅ |
| **Profile sync** | ከ peer ጋር በ mesh በኩል የተፈረመ profile card መለዋወጥ | 23 | `fixtures/profiles/` | ✅ |
| **Ephemeral-ID announce** | ከተሽከረከረ በኋላም ጓደኛዎ አሁንም ሊደርስዎ እንዲችል የአሁኑን rotating routing ID በግል መንገር | 56 | `fixtures/erid/` | ✅ |
| **Pre-key exchange** | ፈጽሞ ካላገኙት ሰው ጋር end-to-end session ለማስጀመር በ mesh በኩል Signal pre-key bundle መጠየቅና ማድረስ | 25, 26 | `fixtures/prekey/` | ✅ |
| **Channels** | ወደ የግል፣ ለአባላት-ብቻ የቡድን ቻናል የተፈረሙ መልእክቶች | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | የ walkie-talkie ድምጽ frames (opaque encoded audio payload) | 15 | `fixtures/media/` | ✅ |
| **Screen share** | የ Screen-share ቪዲዮ frames (opaque encoded video payload) | 32 | `fixtures/media/` | ✅ |
| **Call control** | ለድምጽና ለቪዲዮ ጥሪዎች Ring / accept / decline / hang-up signalling | 27 | `fixtures/videocall/` | ✅ |
| **SOS acknowledgement** | የአደጋ ስርጭታቸው መደረሱን ለላኪው ማረጋገጥ | 6 | `fixtures/sos/` | ✅ |
| **Space breadcrumbs** | ለ "በዙሪያዬ ምን አለ" ንብርብር Location-tagged discovery crumbs | 40 | `fixtures/space/` | ✅ |
| **Forge announce** | derived/forged የይዘት artefact ለ mesh ማስተዋወቅ | 41 | `fixtures/forge/` | ✅ |
| **Vault shard request** | erasure-coded storage shard ማምጣት (ማንኛውም K ከ N shards ፋይሉን ይገነባል) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandwidth measurement** | mesh በ ወፍራሙ pipe እንዲያሰራጭ የ link throughput ማጣራት / ማረጋገጥ / ማወራት (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

እነዚህ ቀድሞ በተጠናቀቁት **messaging፣ 1-to-1 እና group voice፣ video calls፣ live streaming፣ watch-together፣ AODV routing፣ DTN store-and-forward እና SOS flood** አገልግሎቶች ላይ ይቀመጣሉ — እነዚህም በሁሉም 8 ቋንቋዎች ተተግብረዋል።

> **"የተገነባ" እዚህ ምን ማለት እንደሆነ፣ በትክክል።** እያንዳንዱ አገልግሎት wire packet-ውን ያመነጫል እና ያስተናግዳል፣ ትክክለኛዎቹን events ያስነሳል፣ እና ሙሉ የቋንቋ ቤተሰብ ማዛመድ ወዳለበት byte-level fixture ተጠብቋል። መተግበሪያዎ አገልግሎቱን ወደ Signal session-ው፣ routing table-ው እና local state-ው ያገናኛል። ይህ የፕሮቶኮል ንብርብር ነው — በ code፣ በ tests እና በ cross-language byte-fixtures የተረጋገጠ — እንደ ሁሉም ነገር በተመሳሳይ ታማኝ የ RF መሠረት ላይ: በመጨረሻ radio የሚጋልብ ማንኛውም መንገድ በ `OPEN_ISSUES.md` ውስጥ እስከሚከታተለው hardware bring-up ድረስ field-unverified ነው።

## ደህንነት እና ግላዊነት

ከ wire-service ስብስቡ ባሻገር፣ Aether ትንሽ **የደህንነት እና ግላዊነት ንብርብር** ትሰጣለች — የማንነት ቁልፍ አስተዳደር እና በ link-layer ደረጃ ከመከታተል መከላከል። ልክ እንደ ሁሉም ነገር፣ እያንዳንዱ በ**ሁሉም 8 ቋንቋዎች** ተተግብሮ በ`fixtures/<feature>/` ስር ካለ በቋንቋዎች መካከል ከሚጋራ fixture ጋር ተጣብቋል (Swift እና C በተጨማሪ በ macOS build server ላይ ተረጋግጠዋል)። እነዚህ ከ 18ቱ wire services *ተጨማሪ አራት አይደሉም*: ሦስቱ ምንም **አዲስ የ wire packet ዓይነት አይገልጹም**፣ አራተኛውም እንደ አዲስ የተያዘ packet ሳይሆን የራሱን ፖስታዎች **በነባሩ የ DTN/mesh መንገድ ውስጥ** ይሸከማል።

| ችሎታ | ምን እንደሚያደርግ | ንብርብር | Fixture | 8/8 |
|---|---|---|---|:-:|
| **የ recovery-phrase ምትኬ** | ማንነትን እንደ **24-word BIP-39** ሐረግ ምትኬ አድርገው በማንኛውም መሣሪያ ላይ ወደ ነበረበት ይመልሱ። መደበኛ BIP-39 (ከኦፊሴላዊ Trezor vectors ጋር የተረጋገጠ)፣ በ SHA-256 checksum የተደረገ ስለሆነ በስህተት የተተየበ ቃል *ውድቅ ይደረጋል*፣ በጭራሽ በዝምታ ስህተት አይሆንም። ምንም server የለም፣ ምንም ጠባቂ የለም — ሐረጉ **ራሱ** ማንነቱ ነው። | local | `fixtures/bip39/` | ✅ |
| **የ Bluetooth መከታተል-መከላከል** | የሚሽከረከር፣ ከቁልፍ የተገኘ BLE **Service UUID** (HMAC-SHA256፣ የ15-ደቂቃ መስኮት) እና **ሊፈቱ የሚችሉ የግል አድራሻዎች** (IRK + የ RFC `ah` ተግባር፣ AES-128) ያመነጫል — passive scanner በጊዜ ወይም በቦታ ማገናኘት እንዳይችል BLE advertiser የሚፈልገው የመከታተል-መከላከያ ቁሳቁስ። | link-layer | `fixtures/bleprivacy/` | ✅ |
| **Panic-wipe** | በማስገደድ ስር፣ እያንዳንዱን የማንነት ቁልፍ በአስተማማኝ ሁኔታ የሚያጠፋ **duress PIN** (SHA-256፣ በቋሚ-ጊዜ የሚነጻጸር) — በ random ላይ-መጻፍ ከዚያም zero — ወደ ነበረበት የሚመለስ ምንም ሳይቀር። | local | `fixtures/panicwipe/` | ✅ |
| **የበርካታ-መሣሪያ sync** | በ*የራስዎ* መሣሪያዎች መካከል **ያልተማከለ፣ server-የሌለው** sync: በ Ed25519 የተፈረመ **DeviceLink** ያጣምራቸዋል፣ እና የ last-write-wins **SyncRecord** ፖስታዎች ሁኔታን ያስታርቃሉ — ምንም የ cloud መለያ እና ምንም የ sync server ሳይኖር በነባሩ DTN/mesh ላይ በ E2E ተመስጥሮ ይሸከማሉ። | DTN ላይ ይጋልባል | `fixtures/sync/` | ✅ |

**አንድ ታማኝ አለመመጣጠን።** የበርካታ-መሣሪያ **DeviceLink** በ Ed25519 የተፈረመ ነው፣ እና ያ ፊርማ **በ 8ቱ ቋንቋዎች ውስጥ በ 7ቱ byte-identical ነው**። የ Apple CryptoKit ሆን ብሎ የ Ed25519 ፊርማዎችን *በ random ያደርጋል*፣ ስለዚህ በ Swift ላይ 64ቱ የፊርማ bytes በእያንዳንዱ ጊዜ ይለያያሉ — ነገር ግን **የተፈረመው አካል byte-identical ነው** እና እያንዳንዱ link አሁንም በሁሉም 8 SDKs ላይ ይረጋገጣል፣ ስለዚህ Swift ከ ፊርማ-byte parity ይልቅ የ**ማረጋገጫ** parity ይደርሳል። ይህ የ platform-crypto ባህሪ ነው፣ ጉድለት አይደለም፣ እና በእነዚህ አራት ባህሪያት ውስጥ "byte-identical" asterisk የሚይዝበት ብቸኛው ቦታ ነው። ሙሉ የ wire formats በ [`PROTOCOL_SPEC.md`](../../PROTOCOL_SPEC.md) §12 ውስጥ ናቸው፤ የ threat model በ [`THREAT_MODEL.md`](../../THREAT_MODEL.md) ውስጥ ነው።

## Transports

እያንዳንዱ transport በኮድ ቤዝ ውስጥ ሁሉ የሚጠቀም የቀለም ስም አለው። `IsAvailable` በ hardware የተዘጉ መንገዶችን ይቆጣጠራል — `TransportManager` ይዘላቸዋል እና ወደ ቀጣዩ የሚገኝ transport ይመለሳል።

**የ Status ቁልፍ:** ✅ እውነተኛ፣ የተገነባ እና የተረጋገጠ · ⏳ እውነተኛ፣ ማረጋገጥ በሂደት ላይ · ⚠️ በአንዳንድ መድረኮች እውነተኛ፣ በሌሎች stub · ❌ stub (እስካሁን transport code የለም)።

| ቀለም | ስም | ክልል | Bandwidth | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ እውነተኛ — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ እውነተኛ — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | ያልተገደበ | ~10 Mbps | ✅ እውነተኛ — Windows; relay server በ `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Internet data channel | ያልተገደበ | ~100 Mbps | ✅ በሁሉም 8 ቋንቋዎች እውነተኛ — **በሁሉም 8 loopback-verified** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust እያንዳንዱ ሁለት peers በእውነተኛ ICE data channel ላይ bytes ይለዋወጣሉ) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ በ Android እውነተኛ (`android/white/`); Windows = እውነተኛ BLE-GATT + RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`፣ net9/10 compiles፣ runtime-unverified) — `Windows.Networking.Proximity` በ Win 11 ተወግዷል |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ በ HarmonyOS እውነተኛ (`harmonyos/teal/`፣ `@kit.NearLinkKit` — on-device verification በመጠባበቅ ላይ); Android + Windows = እውነተኛ SSAP-over-BLE approximation (`android/teal/AetherNetSleService`፣ `WinNearLinkBleTransportService`; compile + unit-test verified፣ runtime-unverified) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ እውነተኛ RYLR SX127x/SX126x serial driver (`LoRaSerialTransport` በ C#/Go/Rust/C; compiles፣ runtime-unverified — physical module ያስፈልገዋል); BLE Coded-PHY bridge አሁንም የተመዘገበ design ነው |

radio transports እውነተኛ የሚሆኑት platform code ባለበት ብቻ ነው (C#/Windows፣ Kotlin/Android፣ HarmonyOS)። ስምንቱ የቋንቋ ቤተ-መጻሕፍት አለበለዚያ ለ testing **in-process simulation** transport ያቀርባሉ — **WebRTC ለሁሉም የተለመደ የመጀመሪያው እውነተኛ transport ነው** (የተጠናቀቀ; በቋንቋዎቹ ሁሉ loopback-verified)።

ቅድሚያ በ power cost ነው: radio mesh ይመረጣል፣ ከዚያ WebRTC እንደ ቀጥታ የ internet መንገድ፣ HTTP/QUIC relay እንደ የመጨረሻ አማራጭ።

## የ Deployment ደረጃዎች

Aether Bluetooth ወይም Wi-Fi የሚደግፍ በማንኛውም መድረክ ላይ ይሠራል። ያለዎት ደረጃ የሚያነጣጥሩበት OS ላይ ይመሠረታል።

---

### Standard tier — ማንኛውም መድረክ

Android · Windows · Linux · macOS · iOS

Aether Bluetooth ወይም Wi-Fi hardware ባለው በማንኛውም መሣሪያ ላይ ይሠራል። radio በፊዚካል በሌለበት ቦታ፣ እያንዳንዱ የተዘጋ transport ባለው ላይ ይገመታል። እነዚህ approximations አሁን **እውነተኛ code** ናቸው (compile-verified; **runtime-unverified** የ 2-device / hardware RF test በመጠባበቅ ላይ):

- **NearLink (Aether Teal)** — እውነተኛ SSAP-over-BLE-GATT approximation (Aether SLE UUID `61657468-6572-0003-…`) በ Android (`android/teal/AetherNetSleService`) እና በ Windows (`WinNearLinkBleTransportService`); compile + unit-test verified፣ runtime-unverified። እውነተኛው NearLink radio ያለው በ HarmonyOS ላይ ብቻ ነው (`harmonyos/teal/`፣ on-device verification በመጠባበቅ ላይ)።
- **LoRa (Aether Red)** — እውነተኛ RYLR SX127x/SX126x serial driver (`LoRaSerialTransport` በ **ሁሉም 8 ቋንቋዎች** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; እያንዳንዱ port compile-verified፣ በ Mac build server ላይ Swift + C ን ጨምሮ; runtime-unverified — physical module ያስፈልገዋል)። the Meshtastic-over-BLE-Coded-PHY bridge (~1.3 km) የተመዘገበ design ሆኖ ይቆያል; እውነተኛ long-range LoRa LoRa-capable ኖድ ያስፈልገዋል (gateway፣ SBC፣ ወይም LoRa module ያለው rugged handset)።
- **NFC (Aether White)** — በ Android እውነተኛ (HCE)። Windows አሁን እውነተኛ BLE-GATT + RSSI −40 dBm proximity approximation አለው (`WinNfcBleTransportService`፣ net9/10 compiles; runtime-unverified); reader ሲኖር ACR122U PC/SC።

በሁሉም ቦታ እውነተኛ እና ተመሳሳይ የሆነው: **BLE፣ Wi-Fi Direct፣ HTTP/QUIC relay፣ እና WebRTC P2P transport (በሁሉም 8 ቋንቋዎች loopback-verified)**፣ ከዚያ Signal Protocol security (X3DH + Double Ratchet)፣ AODV routing፣ DTN store-and-forward፣ SOS broadcast፣ ድምጽ እና streaming።

**ታማኝ ሁኔታ:** BLE + Wi-Fi Direct + relay production-real ናቸው; **WebRTC P2P እውነተኛ እና በሁሉም 8 ቋንቋዎች loopback-verified ነው** (ሁለት peers በእውነተኛ ICE data channel ላይ bytes ይለዋወጣሉ — Rust በ `.201` Linux box ላይ በሚሠራ UDP ICE ተረጋግጧል); the NearLink / LoRa / NFC-on-Windows approximations አሁን የሚcompile እውነተኛ code ናቸው (LoRa በሁሉም 8 compile-verified፣ በ Mac build server ላይ Swift + C ን ጨምሮ; NearLink-Android እንዲሁ unit-tested) ግን **runtime-unverified** ነው — እስካሁን hardware / 2-device RF test የለም። በ code ውስጥ በ mesh ይሳተፋሉ; እነዚያን ሦስት field-proven RF እየጠበቁ አታሰማሩ።

---

### Native tier — CircleOS / OpenHarmony

CircleOS · HarmonyOS · በ OpenHarmony-ላይ የተመሠረተ ማንኛውም OS

CircleOS በ OpenHarmony ላይ የተገነባ ነው፣ እሱም NearLink (SLE) silicon እና `@kit.NearLinkKit` SDK ን እንደ first-class OS capability ያቀርባል። NearLink hardware ባላቸው CircleOS እና HarmonyOS መሣሪያዎች ላይ፣ ምንም approximation አያስፈልግም — `harmonyos/teal/` እውነተኛውን SLE radio በቀጥታ ይጠቀማል:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

ይህ የ standard tier የተሻለ ስሪት ብቻ አይደለም። በ NearLink ንብርብር በምድብ ደረጃ የተለየ ኔትወርክ ነው:

| አቅም | Standard tier (BLE approx) | Native tier (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink range** | ~100 m (BLE) | **600 m** |
| **NearLink bandwidth** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink latency** | ~10 ms (BLE) | **20 µs** |
| **NearLink power** | BLE baseline | **ከ BLE 5.0 60% ያነሰ** |
| **Concurrent NearLink peers** | ~7 (BLE connection limit) | **500+** |
| **NearLink source** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | እውነተኛ SLE radio (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Native | Native (ተመሳሳይ) |
| **Signal Protocol security** | ሙሉ | ሙሉ (ተመሳሳይ) |
| **Routing / DTN / SOS** | ሙሉ | ሙሉ (ተመሳሳይ) |
| **Aether Tag identity** | ይደገፋል | ይደገፋል (ተመሳሳይ) |

---

### በደረጃዎች መካከል መንቀሳቀስ

ምንም የ code ለውጦች አያስፈልጉም። ደረጃው በ runtime በእያንዳንዱ transport service ላይ ባለ `IsAvailable` ይወሰናል:

1. NearLink silicon ባለው CircleOS ወይም HarmonyOS መሣሪያ ላይ፣ በ NearLink transport ላይ ያለ `IsAvailable` `true` ይመልሳል (በ permission check + passive scan attempt hardware-probed)።
2. `TransportManager` NearLink ን በራስ-ሰር ወደ priority position ያሳድጋል — ዝቅተኛ power cost፣ ከፍተኛ bandwidth።
3. የ App code፣ packet format፣ routing algorithm፣ security layer፣ እና Aether Tags በሁለቱም ደረጃዎች ተመሳሳይ ናቸው።

በ standard tier ላይ ያለ ኖድ እና በ native tier ላይ ያለ ኖድ በነጻነት ሊነጋገሩ ይችላሉ — ተመሳሳይ wire format፣ ተመሳሳይ Signal Protocol sessions፣ እና ተመሳሳይ Aether Tags ይጋራሉ። የደረጃ ልዩነቱ የሚነካው ለ NearLink packets የሚጠቀመውን radio ብቻ ነው፣ ከዚያ በላይ ያለውን ፕሮቶኮል አይደለም።

---

> **በውስጥ እነዚህ ደረጃዎች Asterix variant (standard) እና Obelix variant (native) ተብለው ይጠቀሳሉ።** Asterix ባለው ነገር በጥሩ ሁኔታ ይሠራል። Obelix — በ CircleOS ላይ በnative NearLink እየሮጠ — Obelix እንደገና ሳይጠጣ የአስማት potion-ውን ጥንካሬ እንደሚይዝ ሁሉ፣ በዘላቂነት ከፍ ባለ አቅም ላይ ይሠራል።

---

## Implementations

Aether በ 8 ቋንቋዎች የተገነባ ነው፣ ስለዚህ በስልኮች፣ በላፕቶፖች፣ በታብሌቶች እና በ microcontrollers ላይ ይሠራል። ሁሉም implementations wire-compatible packets ያመነጫሉ — በ Rust ኖድ የተመሰጠረ መልእክት በ Python ኖድ ተስተላልፎ በ Swift ኖድ ሊፈታ ይችላል።

| ቋንቋ | Directory | Wire format | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Voice/Group | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

ሁሉም 8 ቋንቋዎች byte-identical wire packets ያመነጫሉ፣ በ CI ውስጥ በሚሮጡ 17 canonical wire-format fixtures እና 6 Signal test vectors ተረጋግጠዋል (`fixtures/expected/*.bin`፣ `fixtures/signal/expected/*.json`)። Routing (AODV-style RREQ/RREP)፣ DTN store-and-forward፣ SOS broadcast፣ ድምጽ፣ streaming፣ እና security-hardening services በእያንዳንዱ ቋንቋ ተተግብረዋል፣ በሁሉም 8 implementations ላይ **~3,000 tests** አሉ:

| ቋንቋ | Tests | CI platform |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3,000** | |

Cross-language Signal interop በ `fixtures/signal/` ላይ ተስተካክሏል፣ ለ X3DH (`x3dh_basic`)፣ ለ symmetric ratchet (`ratchet_step_basic`፣ `ratchet_step_three_iterations`)፣ ለ KDF_RK (`kdf_rk_basic`)፣ እና ለ ሙሉ የ X3DH session round-trip (`x3dh_session_msg1`፣ `x3dh_session_reply`) በሚጋሩ test vectors። እያንዳንዱ implementation በእነዚያ fixtures ላይ byte-identical outputs ማምረት አለበት። ሁሉም 8 ቋንቋዎች አሁን ሙሉ Signal session ያቀርባሉ (`generate_pre_key_bundle`፣ `process_pre_key_bundle`፣ `encrypt`፣ `decrypt`)።

ከ wire format እና ከ Signal በላይ፣ **ሙሉ የ wire-service suite** — presence፣ heartbeat፣ profile sync፣ ephemeral-ID announce፣ pre-key exchange፣ channels፣ push-to-talk፣ screen share፣ call control፣ SOS acknowledgement፣ space breadcrumbs፣ forge announce፣ vault shard request፣ እና bandwidth measurement (**የምታገኘው** ን ይመልከቱ) — በተመሳሳይ በሁሉም 8 ቋንቋዎች ተተግብሮ ወደ ራሱ fixtures ተጠብቋል (`fixtures/presence/`፣ `fixtures/media/`፣ `fixtures/bandwidth/`፣ `fixtures/prekey/`፣ `fixtures/videocall/`፣ `fixtures/vaultshard/`፣ እና ተመሳሳዮች)። በፕሮቶኮል ንብርብር ላይ ምንም ባህሪ C#-only አይደለም።

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

demo በ 8 ደረጃዎች ውስጥ ይመራዎታል: ለሦስት ኖዶች (Alice፣ Bob፣ Charlie) Ed25519 identity keys ማመንጨት፣ Signal Protocol sessions ማቋቋም፣ የተመሰጠሩ መልእክቶች መላክ፣ (ማንበብ በማይችለው) Charlie በኩል መልእክት ማስተላለፍ፣ የ binary wire format ማሳየት፣ እና በ 5 ተከታታይ መልእክቶች ላይ forward secrecy ማሳየት። ውጤቱ በቀለም-የተመደበ ሲሆን በደረጃዎች መካከል ይቆማል።

**በ C# ውስጥ መልእክት ላክ:**

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

demo ለሁለት ኖዶች identity keys ያመነጫል፣ pre-key bundles ይለዋወጣል፣ የተመሰጠሩ sessions ያቋቁማል፣ በሁለቱም አቅጣጫ የተመሰጠሩ መልእክቶች ይልካል፣ mesh packets ይፈጥርና ይፈርማል፣ signatures ያረጋግጣል፣ እና packets ወደ binary wire format ይሰራሠራል። in-process transport layer-ንም ያሳያል።

**በ Rust ውስጥ መልእክት ላክ:**

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

demo በ simulated network ውስጥ ሁለት ኖዶች ይፈጥራል፣ Ed25519 keys ያመነጫል፣ Signal Protocol sessions ያቋቁማል፣ packet ይፈጥርና ይፈርማል፣ ወደ C#-compatible binary format ይሰራሠራል፣ ሚስጥራዊ መልእክት ይመሰጥራል፣ በሌላው ኖድ ላይ ይፈታዋል፣ በ transport በኩል ይልከዋል፣ እና round-trip-ውን ያረጋግጣል።

**በ TypeScript ውስጥ መልእክት ላክ:**

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

demo 8 ማሳያዎችን ያሮጣል: Ed25519 key generation እና tamper detection፣ ከ capabilities ጋር node creation፣ Signal Protocol X3DH key exchange፣ AES-256-GCM encryption እና decryption፣ packet serialization፣ ከ replay detection ጋር packet signing፣ in-process transport፣ እና ሁሉንም ንብርብሮች የሚያዋህድ ሙሉ end-to-end flow።

**በ Python ውስጥ መልእክት ላክ:**

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

demo 5 ማሳያዎችን ያሮጣል: packet serialization round-trips፣ ከ tamper detection ጋር Ed25519 signing፣ በሁለቱም አቅጣጫ ከተመሰጠረ messaging ጋር Signal Protocol session establishment፣ በሁለት peers መካከል in-process transport፣ እና ለ replay protection nonce deduplication።

**በ Go ውስጥ መልእክት ላክ:**

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

demo በ 11 ደረጃዎች ውስጥ ይመራል: key generation፣ ከ capabilities ጋር node creation፣ Signal Protocol initialization፣ pre-key bundle exchange፣ session establishment፣ packet creation እና signing፣ serialization፣ ከ signature verification ጋር deserialization፣ ከ key ratcheting ጋር end-to-end encryption፣ replay attack detection፣ እና in-process transport።

**በ Kotlin ውስጥ መልእክት ላክ:**

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

demo 5 ፈተናዎችን ያሮጣል: packet serialization round-trips፣ ከ tamper rejection ጋር Ed25519 signing፣ ከ AES-256-GCM encryption ጋር Signal Protocol session establishment፣ in-process transport message delivery፣ እና Alice packet ፈርማ Bob ከ transport በኋላ የሚያረጋግጥበት ሙሉ end-to-end flow።

**በ Swift ውስጥ መልእክት ላክ:**

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

demo 7 ማሳያዎችን ያሮጣል: Ed25519 key generation፣ packet creation እና signing፣ ወደ binary wire format serialization፣ ከ integrity checks ጋር deserialization፣ AES-256-GCM encryption እና decryption፣ HMAC-SHA256 message authentication፣ እና HKDF-SHA256 key derivation።

**በ C ውስጥ መልእክት ላክ:**

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

የተገነባው እና ቀጥሎ ያለው።

**የተጠናቀቀ (cross-language የተረጋገጠ፣ ሁሉም 8 implementations):**
- Wire format: በ 8 ቋንቋዎች byte-identical፣ በ 17 canonical fixtures እና በ CI ውስጥ cross-language assertions የተስተካከለ (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9-job matrix (C#/.NET 10፣ Go 1.22፣ TypeScript/Node 20፣ Python 3.12፣ Kotlin/JVM 21፣ Swift/macOS-14፣ Rust stable፣ C/GCC፣ ከ fixture integrity job ጋር) በ `.github/workflows/ci.yml`።
- Ed25519 packet signing እና verification
- AES-256-GCM encryption
- HKDF / HMAC key derivation primitives
- Packet serialization + signing layout (LE + 4-byte int32 fields)
- In-process transport simulator (ለ development እና tests)
- ከ RREQ/RREP ጋር AODV-inspired routing service፣ የተፈረሙ route replies፣ dedup፣ TTL forwarding
- ከ custody transfer ጋር DTN store-and-forward service፣ geohash-aware replication፣ 72h TTL
- ከ flood ጋር SOS broadcast service፣ dedup፣ self-origin guard፣ rate-limit (3/hr)
- Extensibility seams: `IncentiveProvider`፣ `BackendClient`፣ `FeatureFlagProvider` (Noop defaults)
- በሁሉም 8 ቋንቋዎች **~3,000 tests** (C# 530፣ TypeScript 459፣ Kotlin 457፣ Go 423፣ Python 387፣ Swift 295፣ C 253፣ Rust ~195) — ሁሉም በ CI ውስጥ green
- ✅ **እውነተኛ X3DH ephemeral key (8 ቋንቋዎች)** — ከ HKDF-SHA256 root derivation ጋር 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`)። በ `fixtures/signal/expected/x3dh_basic.json` ተጠብቋል።
- ✅ **Double Ratchet alignment ለቤተሰቡ ሁሉ** — ሙሉ Signal §5 ከ HMAC-SHA256 + 0x01/0x02 domain separation ጋር በ symmetric ratchet ውስጥ፣ HKDF-SHA256 KDF_RK በ DH-ratchet step ውስጥ፣ በተቀበሉ ጊዜ DH-rotation። በ `ratchet_step_basic`፣ `ratchet_step_three_iterations`፣ `kdf_rk_basic` fixtures ተረጋግጧል።
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 ከ HEAD ጋር ተስማምቷል** — `docs/PROTOCOL_SPEC.md` ን ይመልከቱ።

**የተጠናቀቀ (ሁሉም 8 ቋንቋዎች):**
- ✅ **Voice calls (1-to-1)** — signaling state machine (Offer/Answer/Hangup/Cancel/Timeout) + binary frame transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes)። በ `IRoutingService` በኩል Route-aware delivery።
- ✅ **Group voice** — host-driven membership (invite/kick/leave)፣ per-frame key generation field፣ ለሁሉም አሁን ላሉ አባላት unicast fan-out፣ በ membership change ላይ host-controlled key rotation።
- ✅ **Live streaming** — publisher `StreamAnnounce` ያሰራጫል; subscribers `StreamSubscribe` ይልካሉ; binary `StreamSegment` frames (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) ለእያንዳንዱ subscriber unicast።
- ✅ **Video calls (1-to-1)** — በ signaling ውስጥ codec/resolution/fps/bitrate negotiation፣ keyframe-request እና quality-change signals፣ ከ voice layout ጋር የሚዛመድ binary `VideoFrame` format።
- ✅ **Watch Together** — host authoritative `WatchSync` (play/pause/seek/speed) commands ያወጣል; followers በ RTT compensation ይተገብራሉ (`position = positionMs + elapsed × playbackSpeed`); fire-and-forget `WatchReaction`።
- ✅ **One-time pre-key (OPK) pool** — ነባሪ 100፣ FIFO issue፣ lazy top-up፣ በሁሉም 8 ቋንቋዎች lock-protected consumption። the single-OPK concurrency hazard ን ይዘጋል።
- ✅ **C: ሙሉ Signal session** — `aethernet_signal_service_init`፣ `generate_pre_key_bundle`፣ `process_pre_key_bundle`፣ `encrypt`፣ `decrypt` በ `c/src/signal_protocol.c`; 6 two-node E2E tests በ `c/tests/test_signal_session.c`። ሁሉም 8 ቋንቋዎች አሁን ሙሉ session-capable Signal Protocol አላቸው።

**የተጠናቀቀ (ሁሉም 8 ቋንቋዎች — ሙሉ የ wire-service suite):**
- ✅ **እያንዳንዱ የተያዘ packet type አሁን በሁሉም 8 ቋንቋዎች እውነተኛ፣ byte-identical service ነው።** Presence beacon/query (21/22)፣ heartbeat (10)፣ profile sync (23)፣ ephemeral-routing-ID announce (56)፣ pre-key exchange (25/26)፣ channels (7)፣ push-to-talk (15)፣ screen share (32)፣ call control (27)፣ SOS acknowledgement (6)፣ space breadcrumbs (40)፣ forge announce (41)፣ vault shard request (42)፣ እና bandwidth measurement / ABMF (53/54/55)። እያንዳንዱ host ወደ Signal session-ው እና routing table-ው የሚያገናኘው ቀጭን service ነው (produce + handle + event); እያንዳንዱ ወደ በጋራ በሚጋራ cross-language fixture ተጠብቋል (`fixtures/presence/`፣ `fixtures/media/`፣ `fixtures/bandwidth/`፣ `fixtures/prekey/`፣ `fixtures/videocall/`፣ `fixtures/vaultshard/`፣ `fixtures/channels/`፣ `fixtures/profiles/`፣ `fixtures/heartbeat/`፣ `fixtures/erid/`፣ `fixtures/space/`፣ `fixtures/forge/`፣ `fixtures/sos/`) እና በእያንዳንዱ ቋንቋ unit tests ይፈተናል፣ Swift እና C በ macOS build server ላይ ተረጋግጠዋል። **የምታገኘው** ን ይመልከቱ።

**የተጠናቀቀ (C# reference ብቻ):**
- ✅ **Demo Step 9 — MessagingService + DTN fallback end-to-end** — `samples/AetherNet.Demo.Console` ተቀባዩ offline ሲሆን ከ DTN store-and-forward ጋር real-Signal-encrypted messaging ውስጥ ይመራል።
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` bridge** — `SignalMessageEnvelopeCipher` የ messaging layer-ን በነባሪ end-to-end encrypted ያደርገዋል; Signal session የሌላቸው መልእክቶች ይሰለፋሉ፣ ፈጽሞ በ insecure መንገድ አይላኩም።
- ✅ **Adaptive bitrate streaming** — `AdaptiveBitrateController` ለ Profile A (real-time)፣ B (live broadcast)፣ እና C (VOD) spec-mandated bitrate ladders ጋር። publisher ከፍተኛውን sustainable rung ይመርጣል (20% headroom) እና ከ floor በታች ሲሆን ከ segment ይልቅ `StreamAbandon` (`PacketType.StreamAbandon`) ያወጣል። `IStreamingService` `UpdateBandwidthEstimate` እና `GetCurrentBitrateRung` ያሳያል።
- ✅ **Watch Together: BitTorrent ingest + ChipIn group funding** — `TorrentInfo` / `TorrentFile` models; `WatchTogetherService` `PacketType.TorrentMetadata` ያስተናግዳል እና `TorrentReceived` ያስነሳል። `ChipInPool` / `ChipInContribution` state machine (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` በ `IWatchTogetherService`።
- ✅ **Group video calls with auto SFU relay** — `GroupVideoService` / `IGroupVideoService`። ለ ≤ 3 participants FullMesh topology; ከ relay re-assignment ጋር በ `SfuThresholdParticipants` (4) ወደ SFU በራስ-ሰር መቀየር በ `GroupVideoSignaling(SfuAssigned)` በኩል። በ FullMesh ውስጥ Fan-out፣ በ SFU mode ውስጥ relay-only send። Signaling packet type `GroupVideoSignaling = 35`።
- ✅ **BLE GATT transport simulation** — `SimulatedBleGattTransportService` (`IBleTransportService`)። በ `BleGattFramer` በኩል GATT MTU framing (1024 B/frame፣ `[2B count][2B index][payload]`)፣ in-process static peer registry፣ advertisement broadcast። ሁሉም `BleMaxPayloadBytes` constraints ተፈጻሚ ናቸው።
- ✅ **Wi-Fi Direct transport simulation** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`)። ግልጽ `ConnectAsync`/`DisconnectAsync` lifecycle፣ ቀጥታ large-payload delivery (framing የለም)፣ bidirectional `PeerConnected`/`PeerDisconnected` events።
- ✅ **NearLink transport simulation** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`)። 4096 B frame MTU፣ 500-peer registry፣ `ConnectedPeerCount`፣ በ runtime settable `IsAvailable`።
- ✅ **RF bring-up simulation tests** — Two-node interop tests (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` round-trip፣ WiFi Direct 64 KB payload transfer። Software layer ሙሉ በሙሉ ተረጋግጧል; ለ on-hardware validation የፊዚካል device lab session ያስፈልጋል።

**የተጠናቀቀ (C# transport layer — ሁሉም fail-fast):**
- ✅ **BLE GATT real transport** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server)። ሙሉ RF bring-up test በ `samples/AetherNet.BleRfTest/`።
- ✅ **Wi-Fi Direct real transport** — `WinWifiDirectTransportService` (WinRT፣ `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`)። RF test በ `samples/AetherNet.WifiDirectRfTest/`።
- ✅ **HTTP relay transport (Aether Purple)** — ከ 10-second long-poll ጋር `HttpRelayTransportService`፣ `PowerCostRelative = 100`፣ ሁልጊዜ የመጨረሻ አማራጭ። Relay server በ `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API፣ port 5200)። RF test በ `samples/AetherNet.RelayRfTest/`።
- ✅ **NFC (Aether White)** — `android/white/` `HostApduService` ን ከ AID `F061657468657200` ጋር ይተገብራል። `WinNfcStubTransportService` ሁለት የ Windows approximation paths ይመዘግባል: (1) ከ RSSI gate ≥ −40 dBm ጋር NDEF-over-BLE-GATT (NFC silicon ሳይኖር tap-to-connect ያስመስላል፣ `IsAvailable = Bluetooth present`); (2) በ `Windows.Devices.SmartCards` PC/SC በኩል ACR122U USB reader (`IsAvailable = contactless reader enumerated`)። Upgrade path: Microsoft first-party P2P NFC API ሲያወጣ `ITransportService` ተግብር።
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`) በመጠቀም ሙሉ HarmonyOS 5.0.1 (API 13) ArkTS implementation; `isAvailable` በ runtime probed። `WinNearLinkStubTransportService` + `android/teal/` the SSAP-over-BLE approximation ይመዘግባሉ: BLE GATT ከ Aether SLE service UUID `61657468-6572-0003-0000-000000000000` ጋር — ለ SSAP API-analogous፣ ከእውነተኛ NearLink hardware ጋር wire-compatible አይደለም። Upgrade path: BLE GATT calls በ `ssapc_*`/`ssaps_*` SDK calls ተካ; UUIDs እና `TransportManager` slot ሳይለወጡ።
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` the Meshtastic-over-BLE-LR approximation ይመዘግባሉ: ሙሉ Meshtastic wire format (16-byte header + AES-256-CTR protobuf) በ BLE 5.0 Coded PHY S=8 ላይ (~1.3 km outdoor)፣ ከ managed-flood routing እና RSSI-weighted contention window ጋር። ከእውነተኛ LoRa hardware ጋር Bridge-node federation በራስ-ሰር ይሠራል (ተመሳሳይ Meshtastic packet format፣ translation የለም)። Upgrade path: BLE LR radio በ SX1276/SX1278 AT-command ወይም SPI driver ተካ; packet format እና routing ሳይለወጡ።

**ክፍት — በ `OPEN_ISSUES.md` ተከታትሏል:**
- RF bring-up በእውነተኛ hardware ላይ: በፊዚካል BLE / Wi-Fi Direct devices ላይ end-to-end two-node interop test (simulation tests ያልፋሉ; hardware lab session ያስፈልጋል)
- NearLink: `harmonyos/teal/` የተጠናቀቀ; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 hardware ያስፈልገዋል (NearLink silicon Huawei-ባልሆኑ devices ላይ የለም)። Windows + Android በራስ-ሰር ወደ SSAP-over-BLE approximation ይመለሳሉ።
- LoRa / CircleLink: ለእውነተኛ LoRa range radio module ያስፈልጋል። ያለ እሱ፣ the Meshtastic wire format በ BLE LR (~1.3 km) ላይ ይሸከማል እና ከእውነተኛ LoRa hardware ጋር bridge-node federation ይገኛል።
- ✅ **(RESOLVED v1.2.0)** Consumer protocol surface (Wave 16/17) — ለ inbound bundles `IDtnService.BundleReceived` event ([#59](https://github.com/bhengubv/aether-protocol/issues/59))፣ application-layer naming/discovery directory ([#60](https://github.com/bhengubv/aether-protocol/issues/60))፣ author-tipping interface ([#61](https://github.com/bhengubv/aether-protocol/issues/61))። ሦስቱም በ 8 ቋንቋዎች ላይ ከ byte-equal cross-language fixtures ጋር additively ተልከዋል። CHANGELOG ን ይመልከቱ።

**ለውጭ contribution ገና ያልተከፈተ:**
- ፕሮቶኮሉ አሁንም በንቁ development ላይ ነው። የውጭ contributions በዚህ ጊዜ አይቀበሉም።
- NearLink transport implementation፣ Android/iOS integration examples፣ ተጨማሪ transport backends፣ performance benchmarks፣ እና protocol fuzzing በውስጥ ይከታተላሉ እና ፕሮጀክቱ stable public contribution point ሲደርስ ይከፈታሉ።

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

## አዲስ Transport ማከል

`ITransportService` ተግብር:

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

በ DI ውስጥ መዝግበው `TransportManager` በራስ-ሰር በ transport selection ውስጥ ያካትተዋል፣ በ power cost የተደረደረ።

## እንዴት እንደሚነጻጸር

| Protocol | ገደብ | የ Aether ጥቅም |
|----------|-----------|-----------------|
| **Briar** | Android-only፣ Tor-dependent | Cross-platform፣ pure mesh |
| **Meshtastic** | LoRa only (30 kbps max) | Multi-transport (BLE + WiFi + NearLink)፣ voice እና streaming ችሎታ ያለው |
| **Reticulum** | Python፣ ትንሽ ማህበረሰብ | 8 ቋንቋዎች፣ በሁሉም መካከል wire-compatible |
| **libp2p** | internet backbone ያስባል | Offline-first፣ ያለ ምንም infrastructure ይሠራል |
| **Yggdrasil** | Overlay network፣ internet ያስፈልገዋል | Physical-layer mesh፣ ያለ internet ይሠራል |
| **Signal** | mesh የለም፣ internet ያስፈልገዋል | Offline ይሠራል፣ P2P፣ mesh relay፣ ተመሳሳይ E2E encryption |

## በተደጋጋሚ የሚጠየቁ ጥያቄዎች

**AetherNet ያለ ኢንተርኔት ይሠራል?**
አዎ — offline-first ነው። መሣሪያዎች በ Bluetooth፣ Wi-Fi Direct፣ NearLink፣ ወይም LoRa በኩል በቀጥታ ይነጋገራሉ እና መልእክቶችን በሌሎች መሣሪያዎች በኩል hop-by-hop ያስተላልፋሉ፣ ምንም የኢንተርኔት ግንኙነት፣ የሞባይል ማማ፣ ወይም ሰርቨር ሳያስፈልግ። ቀጥታ route በሌለበት ጊዜ፣ መልእክቶች እስኪከፈት ድረስ እስከ 72 ሰዓት ይያዛሉ (delay-tolerant store-and-forward)።

**End-to-end encrypted ነው?**
አዎ። AetherNet ለ end-to-end encryption የ Signal Protocol (X3DH key agreement ከ Double Ratchet በ X25519 ላይ ጋር)፣ ለመልእክት payloads AES-256-GCM፣ እና በእያንዳንዱ packet ላይ Ed25519 signatures ይጠቀማል። መልእክት የሚያስተላልፉ መሣሪያዎች ማንበብ አይችሉም።

**ምን transports ይጠቀማል?**
Bluetooth LE፣ Wi-Fi Direct፣ NearLink (SLE)፣ LoRa/CircleLink serial radio፣ HTTP/QUIC relay፣ እና ለቀጥታ የኢንተርኔት peer-to-peer WebRTC። ፕሮቶኮሉ ለእያንዳንዱ packet ዝቅተኛ-power ያለውን የሚገኝ transport በራስ-ሰር ይመርጣል እና ወደ ቀጣዩ ይመለሳል።

**በምን programming languages ይገኛል?**
ስምንት — C#፣ Rust፣ TypeScript፣ Python፣ Go፣ Kotlin፣ Swift፣ እና C። እያንዳንዱ implementation byte-identical wire packets ያመነጫል፣ በ CI ውስጥ በጋራ በሚጋራ cross-language fixture corpus ተጠብቆ፣ ስለዚህ በአንድ ቋንቋ የተገነባ packet በማንኛውም ሌላ ሳይለወጥ ይፈታል።

**ከ Meshtastic፣ Briar፣ ወይም Bridgefy እንዴት ይለያል?**
Meshtastic LoRa-only ነው; AetherNet multi-transport ነው (Bluetooth + Wi-Fi + NearLink + LoRa) እና ከመልእክቶች በተጨማሪ ድምጽ፣ ቪዲዮ፣ እና streaming ይሸከማል። Briar Android-only ነው እና በ Tor ላይ ይመራል; AetherNet cross-platform እና pure mesh ነው። ከተዘጉ SDKs በተለየ፣ AetherNet በ MIT-license የተፈቀደ ነው እና በስምንት ቋንቋዎች በግልጽ ተተግብሯል። ከላይ ያለው የንጽጽር ሰንጠረዥ ዝርዝሮቹ አሉት።

**production-ready ነው?**
የፕሮቶኮል ንብርብር — wire format፣ Signal security፣ routing፣ DTN store-and-forward፣ እና ሙሉ የ service suite — በሁሉም ስምንት ቋንቋዎች ተተግብሮ ተፈትኗል። Radio transports platform code ባለበት እውነተኛ ናቸው (Bluetooth እና Wi-Fi በ Windows እና Android ላይ፣ WebRTC በሁሉም ቦታ) እና በሌላ ቦታ hardware bring-up እየተጠባበቁ field-unverified ናቸው፣ ይህም በ `OPEN_ISSUES.md` ውስጥ በታማኝነት ይከታተላል። ከማሰማራትዎ በፊት በእያንዳንዱ ክፍል ውስጥ ያሉትን የ status ማስታወሻዎች ያንብቡ።

**በምን license ስር ነው?**
MIT — ለ commercial እና ለ open-source ጥቅም ነጻ። [LICENSE](LICENSE) ን ይመልከቱ።

**AetherNet ን ማን ይገነባል?**
ከሞባይል ዳታ ጋር ወይም ያለ ሞባይል ዳታ ለሚሠራ ግንኙነት በደቡብ አፍሪካ ተገንብቶ፣ ከ The Geek Network mesh ecosystem ጀርባ እንደ ክፍት ፕሮቶኮል ተሠርቷል።

## Extension Points

ፕሮቶኮሉ በራሱ ይሠራል። እነዚህ interfaces ከፈለጉ የራስዎን backend እንዲያገናኙ ያስችልዎታል:

- `IAetherNetIncentiveProvider` — traffic የሚያስተላልፉ nodes ይሸልማል (no-op default: altruistic relaying)
- `IAetherNetBackendClient` — internet ሲገኝ ከ server ጋር ያመሳስላል (no-op default: fully offline)
- `IAetherNetFeatureFlagProvider` — በ runtime protocol features ያስተካክላል (no-op default: everything enabled)

ሦስቱም ከ no-op implementations ጋር ይመጣሉ። ያስወግዷቸው እና ምንም አይሰበርም።

## Contributing

የውጭ contributions ገና አልተከፈቱም። ፕሮጀክቱ አሁንም በንቁ development ላይ ነው። public contribution window ስናስታውቅ ተመልሰው ይመልከቱ።

## Security

ለ responsible disclosure policy [SECURITY.md](SECURITY.md) ን ይመልከቱ።

## License

MIT License። [LICENSE](LICENSE) ን ይመልከቱ።

## Translations

ይህ README ከዚህ ፋይል አናት ላይ ባለው የቋንቋ አሞሌ ውስጥ በተዘረዘሩት ሌሎች ቋንቋዎችም በ [`docs/i18n/`](docs/i18n/) ስር ይጠበቃል — በአውሮፓ፣ በምስራቅ እስያ፣ በመካከለኛው ምስራቅ፣ በደቡብ እስያ፣ በደቡብ ምስራቅ እስያ፣ እና በአፍሪካ ቋንቋዎች ላይ የሚዘረጋ፣ ምክንያቱም ዳታ ለሌላቸው ሰዎች የተገነባ ኔትወርክ በደንብ የተገናኙት ብቻ ሊያነቡት የሚችሉት የፊት በር ሊኖረው አይገባም። **የእንግሊዝኛ ስሪት የእውነት ምንጭ ነው**: አንድ ትርጉም እና የእንግሊዝኛው ጽሑፍ ሲለያዩ፣ የእንግሊዝኛው ጽሑፍ ስልጣን ያለው ነው፣ ትርጉሞችም በአንድ ወይም በሁለት release ሊዘገዩበት ይችላሉ። የተገለጹት ፕሮቶኮል፣ code፣ fixtures፣ እና ባህሪ በማንኛውም ቋንቋ ቢያነቡ ተመሳሳይ ናቸው።
