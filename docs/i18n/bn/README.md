```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

কাছাকাছি থাকা মানুষদের সঙ্গে ফাইল, বার্তা এবং স্ট্রিম ভাগ করুন। WiFi নেই। মোবাইল ডেটা নেই। সাইন-আপ নেই। AirDrop-এর মতো, তবে এটি সবার সঙ্গে, প্রতিটি প্ল্যাটফর্মে কাজ করে।

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](README.md) · [Français](docs/i18n/fr/README.md) · [Español](docs/i18n/es/README.md) · [العربية](docs/i18n/ar/README.md) · [中文简体](docs/i18n/zh-CN/README.md) · [日本語](docs/i18n/ja/README.md) · [Deutsch](docs/i18n/de/README.md) · [Português (BR)](docs/i18n/pt-BR/README.md) · [Русский](docs/i18n/ru/README.md) · [فارسی](docs/i18n/fa/README.md) · [한국어](docs/i18n/ko/README.md) · [isiZulu](docs/i18n/zu/README.md) · [Afrikaans](docs/i18n/af/README.md) · [Sesotho](docs/i18n/st/README.md) · [Kiswahili](docs/i18n/sw/README.md) · [Hausa](docs/i18n/ha/README.md) · [አማርኛ](docs/i18n/am/README.md) · [हिन्दी](docs/i18n/hi/README.md) · [Bahasa Indonesia](docs/i18n/id/README.md) · [বাংলা](docs/i18n/bn/README.md) · [اردو](docs/i18n/ur/README.md)

> **একটি প্রোটোকল, আটটি ভাষা, তারে হুবহু অভিন্ন।** Aether বাস্তবায়িত হয়েছে **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, এবং C**-তে — এবং প্রতিটি প্যাকেট এই সবগুলোর মধ্যে বাইট-বাই-বাইট অভিন্ন, যা CI-তে একটি ভাগ করা ক্রস-ল্যাঙ্গুয়েজ ফিক্সচার কর্পাস দিয়ে বলবৎ করা হয়। এই আটটির যেকোনো একটিতে আপনার নোড তৈরি করুন; এটি বাকি সবগুলোর সঙ্গে আন্তঃপরিচালনযোগ্য। এই README ১১টি মানব ভাষাতেও উপলব্ধ (উপরের লিংকগুলো দেখুন)।

## এটি দিয়ে আপনি কী করতে পারেন?

**ডেটা খরচ না করেই লেকচার নোট ভাগ করুন।**

আপনি একটি স্টাডি গ্রুপে আছেন। কারও ফোনে পুরনো প্রশ্নপত্র আছে। Aether সেগুলো Bluetooth-এর মাধ্যমে সরাসরি আপনার ডিভাইসে পাঠায় — কোনো হটস্পট নেই, কোনো WhatsApp গ্রুপ নেই, কোনো ফাইল সাইজের সীমা নেই। গ্রুপের কেউ যদি নাগালের বাইরে থাকে, ফাইলটি অন্য ডিভাইসগুলোর মধ্য দিয়ে লাফিয়ে তাদের কাছে পৌঁছায়। প্রয়োজন হলে বার্তাগুলো একটি রুটের জন্য ৭২ ঘণ্টা পর্যন্ত অপেক্ষা করে।

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**আপনার চারপাশে কী ঘটছে তা জানুন।**

আপনি একটি ক্যাম্পাস ইভেন্ট বা উৎসবে আছেন। Aether Bluetooth এবং WiFi Direct-এর মাধ্যমে কাছাকাছি অন্য ডিভাইসগুলো খুঁজে পায় — কোনো অ্যাপ ফিড নেই, কোনো অ্যালগরিদম নেই। আপনি যা প্রচার করা হচ্ছে তা নয়, বরং আপনার চারপাশে আসলে যা আছে তা দেখেন।

**সিগন্যাল না থাকলে একটি SOS পাঠান।**

আপনার ফোনে কোনো রিসেপশন নেই। Aether নাগালের মধ্যে থাকা প্রতিটি ডিভাইসে একটি জরুরি বার্তা সম্প্রচার করে, এবং সেই ডিভাইসগুলো তা এগিয়ে দেয়। কোনো সেল টাওয়ার লাগে না।

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**ব্যক্তিগত গ্রুপ চ্যানেল তৈরি করুন।**

আপনার রেসিডেন্স ফ্লোরের জন্য, আপনার সোসাইটির জন্য, আপনার প্রজেক্ট টিমের জন্য একটি চ্যানেল। শুধুমাত্র যাচাইকৃত সদস্যরাই বার্তা পড়তে বা পাঠাতে পারে। কোনো সার্ভার কথোপকথন সংরক্ষণ করে না।

**কাছাকাছি মানুষদের কাছে জিনিস বিক্রি করুন।**

বিক্রির জন্য একটি পাঠ্যবই তালিকাভুক্ত করুন। মেশের নাগালের মধ্যে হেঁটে যাওয়া মানুষজন তা দেখতে পায়। কোনো মার্কেটপ্লেস অ্যাকাউন্ট নেই, কোনো লিস্টিং ফি নেই — শুধু নৈকট্য।

**মেশ জুড়ে একসঙ্গে একটি সিনেমা দেখুন।**

আপনার গ্রুপের একটি মুভি নাইট আছে। কারও কাছে ফাইলটি আছে। Aether প্রতিটি ডিভাইসে প্লেব্যাক সিঙ্ক করে — প্লে, পজ, সিক — সবই একই তালে। যদি শুধু কিছু মানুষের কাছে ফাইলটি থাকে, মেশ এটি একটি P2P স্ট্রিম হিসেবে রিয়েল-টাইমে বিতরণ করে। কারও কাছে না থাকলে, সবাই এটি কিনতে SDPKT-এর মাধ্যমে অবদান রাখে।

## এটি কীভাবে কাজ করে

ডিভাইসগুলো Bluetooth, WiFi Direct, বা NearLink ব্যবহার করে একে অপরের সঙ্গে সরাসরি কথা বলে। কোনো ইন্টারনেট সংযোগ নেই, কোনো সার্ভার নেই, কোনো কেন্দ্রীয় অবকাঠামো নেই।

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

যখন একটি বার্তা সরাসরি তার গন্তব্যে পৌঁছাতে পারে না, এটি অন্য ডিভাইসগুলোর মধ্য দিয়ে লাফিয়ে যায়। সেই রিলে ডিভাইসগুলো তারা যা বহন করছে তা পড়তে পারে না — প্রতিটি বার্তা AES-256-GCM দিয়ে এনক্রিপ্ট করা। প্রতিটি প্যাকেট Ed25519 আইডেন্টিটি কি দিয়ে স্বাক্ষরিত, এবং জাল প্যাকেটগুলো নেটওয়ার্ক দ্বারা বাদ দেওয়া হয়।

> **নিরাপত্তা পরিপক্বতা নোট (শিপ করার আগে পড়ুন):** প্রকৃত X3DH (৪টি X25519 DH), সম্পূর্ণ Signal Double Ratchet (রিসিভে DH-রোটেশন ধাপ, KDF_RK, 0x01/0x02 চেইন র‍্যাচেট), এবং one-time pre-key পুল (ডিফল্ট ১০০ OPK, FIFO, লক-সুরক্ষিত) **সব ৮টি ভাষায়** বাস্তবায়িত এবং `fixtures/signal/`-এর অধীনে একটি ভাগ করা ক্রস-ল্যাঙ্গুয়েজ ফিক্সচার কর্পাসে পিন করা। একমাত্র বাকি থাকা খোলা আইটেমটি হলো প্রকৃত BLE হার্ডওয়্যারে ভৌত RF ব্রিং-আপ (`OPEN_ISSUES.md`-এ ট্র্যাক করা)।

কোনো অ্যাকাউন্ট নেই, কোনো ফোন নম্বর নেই, কোনো ইমেইল নেই। আপনি একটি কি-পেয়ার জেনারেট করেন এবং আপনি নেটওয়ার্কে যুক্ত হয়ে যান।

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

**রাউটিং** — স্বাক্ষরিত রুট রিপ্লাই সহ AODV। প্রতিটি রুট রিপ্লাই গন্তব্যের Ed25519 কি দিয়ে স্বাক্ষরিত, তাই কোনো ডিভাইস এমন একটি গন্তব্য হওয়ার ভান করতে পারে না যা সে নয়।

**স্টোর-অ্যান্ড-ফরোয়ার্ড** — যখন কোনো লাইভ রুট নেই, একটি পথ খোলা না হওয়া পর্যন্ত প্যাকেটগুলো ৭২ ঘণ্টা পর্যন্ত ধরে রাখা হয়।

**ট্রান্সপোর্ট নির্বাচন** — প্রোটোকল প্রতিটি প্যাকেটের জন্য সঠিক ট্রান্সপোর্ট বেছে নেয়। ছোট কন্ট্রোল বার্তাগুলো BLE-এর ওপর দিয়ে যায়। বাল্ক ট্রান্সফার WiFi Direct ব্যবহার করে। উপলব্ধ থাকলে NearLink।

**ভয়েস, ভিডিও এবং স্ট্রিমিং** — কোডেক নেগোসিয়েশন (H.264/H.265/VP8) সহ ভিডিও কল, ট্রান্সপোর্ট-সচেতন কোয়ালিটি নির্বাচন, অটো SFU রিলে সহ গ্রুপ ভিডিও, RTT ক্ষতিপূরণ সহ সিঙ্ক্রোনাইজড ওয়াচ-টুগেদার, এবং অ্যাডাপ্টিভ বিটরেট স্ট্রিমিং।

**রিপ্লে সুরক্ষা** — ৫-মিনিটের টাইমস্ট্যাম্প ফ্রেশনেস উইন্ডো সহ নন্স ডিডুপ্লিকেশন।

## আপনি যা পান — প্রতিটি সার্ভিস, প্রতিটি ভাষায়

Aether শুধু একটি ট্রান্সপোর্ট নয়। প্রোটোকল দ্বারা সংরক্ষিত প্রতিটি প্যাকেট টাইপ এখন **সব ৮টি ভাষায় একটি প্রকৃত, কার্যকর সার্ভিস**, এবং প্রতিটি **বাইট-অভিন্ন তার প্যাকেটে** সিরিয়ালাইজ হয় — Go নোড দ্বারা তৈরি একটি প্যাকেট Swift, Rust, C, Python, TypeScript, Kotlin, বা C# নোড দ্বারা অপরিবর্তিতভাবে ডিকোড করা হয়। প্রতিটি সার্ভিস `fixtures/<service>/`-এর অধীনে একটি ভাগ করা ক্রস-ল্যাঙ্গুয়েজ ফিক্সচারে পিন করা এবং প্রতি-ভাষা ইউনিট টেস্ট দিয়ে যাচাই করা, Swift এবং C অতিরিক্তভাবে macOS বিল্ড সার্ভারে যাচাই করা।

| সক্ষমতা | এটি যা করে | প্যাকেট টাইপ | ফিক্সচার | 8/8 |
|---|---|:-:|---|:-:|
| **প্রেজেন্স বীকন ও কোয়েরি** | "আমি এখানে আছি" ঘোষণা করুন এবং জিজ্ঞাসা করুন "কে কাছাকাছি আছে?" — একটি **ঘূর্ণায়মান, কি-উদ্ভূত ক্ষণস্থায়ী ID**-এর ওপর (আপনার প্রকৃত পরিচয় নয়) এবং একটি স্থূল geohash সহ | 21, 22 | `fixtures/presence/` | ✅ |
| **হার্টবিট** | সংযুক্ত পিয়ারদের মধ্যে হালকা লাইভনেস কিপ-অ্যালাইভ | 10 | `fixtures/heartbeat/` | ✅ |
| **প্রোফাইল সিঙ্ক** | মেশের ওপর দিয়ে একটি পিয়ারের সঙ্গে একটি স্বাক্ষরিত প্রোফাইল কার্ড বিনিময় করুন | 23 | `fixtures/profiles/` | ✅ |
| **ক্ষণস্থায়ী-ID ঘোষণা** | একজন বন্ধুকে ব্যক্তিগতভাবে আপনার বর্তমান ঘূর্ণায়মান রাউটিং ID জানান যাতে এটি ঘোরার পরেও তারা আপনার কাছে পৌঁছাতে পারে | 56 | `fixtures/erid/` | ✅ |
| **প্রি-কি এক্সচেঞ্জ** | মেশের ওপর দিয়ে একটি Signal প্রি-কি বান্ডেল অনুরোধ ও সরবরাহ করুন, এমন কারও সঙ্গে একটি এন্ড-টু-এন্ড সেশন বুটস্ট্র্যাপ করতে যাকে আপনি কখনও দেখেননি | 25, 26 | `fixtures/prekey/` | ✅ |
| **চ্যানেল** | একটি ব্যক্তিগত, শুধুমাত্র-সদস্যদের গ্রুপ চ্যানেলে স্বাক্ষরিত বার্তা | 7 | `fixtures/channels/` | ✅ |
| **পুশ-টু-টক** | ওয়াকি-টকি ভয়েস ফ্রেম (অস্বচ্ছ এনকোডেড অডিও পেলোড) | 15 | `fixtures/media/` | ✅ |
| **স্ক্রিন শেয়ার** | স্ক্রিন-শেয়ার ভিডিও ফ্রেম (অস্বচ্ছ এনকোডেড ভিডিও পেলোড) | 32 | `fixtures/media/` | ✅ |
| **কল কন্ট্রোল** | ভয়েস ও ভিডিও কলের জন্য রিং / অ্যাক্সেপ্ট / ডিক্লাইন / হ্যাং-আপ সিগন্যালিং | 27 | `fixtures/videocall/` | ✅ |
| **SOS স্বীকৃতি** | প্রেরককে নিশ্চিত করুন যে তাদের জরুরি সম্প্রচার গৃহীত হয়েছে | 6 | `fixtures/sos/` | ✅ |
| **স্পেস ব্রেডক্রাম্ব** | "আমার চারপাশে কী আছে" লেয়ারের জন্য অবস্থান-ট্যাগড ডিসকভারি ক্রাম্ব | 40 | `fixtures/space/` | ✅ |
| **ফোর্জ ঘোষণা** | মেশে একটি উদ্ভূত/জাল কনটেন্ট আর্টিফ্যাক্ট বিজ্ঞাপন করুন | 41 | `fixtures/forge/` | ✅ |
| **ভল্ট শার্ড অনুরোধ** | একটি ইরেজার-কোডেড স্টোরেজ শার্ড আনুন (N শার্ডের যেকোনো K শার্ড ফাইলটি পুনর্গঠন করে) | 42 | `fixtures/vaultshard/` | ✅ |
| **ব্যান্ডউইথ পরিমাপ** | লিংক থ্রুপুট প্রোব / অ্যাক / গসিপ করুন যাতে মেশ সবচেয়ে মোটা পাইপ দিয়ে রুট করে (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

এগুলো ইতিমধ্যে সম্পূর্ণ **মেসেজিং, ১-থেকে-১ ও গ্রুপ ভয়েস, ভিডিও কল, লাইভ স্ট্রিমিং, ওয়াচ-টুগেদার, AODV রাউটিং, DTN স্টোর-অ্যান্ড-ফরোয়ার্ড, এবং SOS ফ্লাড** সার্ভিসগুলোর ওপরে বসে — যেগুলোও সব ৮টি ভাষায় বাস্তবায়িত।

> **এখানে "নির্মিত" মানে কী, সঠিকভাবে।** প্রতিটি সার্ভিস তার তার প্যাকেট তৈরি ও পরিচালনা করে, সঠিক ইভেন্ট উত্থাপন করে, এবং একটি বাইট-লেভেল ফিক্সচারে পিন করা যা পুরো ভাষা পরিবারকে মিলতে হবে। আপনার অ্যাপ্লিকেশন সার্ভিসটিকে তার Signal সেশন, রাউটিং টেবিল এবং স্থানীয় স্টেটের সঙ্গে যুক্ত করে। এটি প্রোটোকল লেয়ার — কোড, টেস্ট এবং ক্রস-ল্যাঙ্গুয়েজ বাইট-ফিক্সচারে প্রমাণিত — বাকি সবকিছুর মতো একই সৎ RF ভিত্তিতে: যেকোনো পথ যা শেষ পর্যন্ত একটি রেডিওতে চলে তা `OPEN_ISSUES.md`-এ ট্র্যাক করা হার্ডওয়্যার ব্রিং-আপ পর্যন্ত ফিল্ড-অযাচাইকৃত।

## ট্রান্সপোর্ট

প্রতিটি ট্রান্সপোর্টের একটি রঙের নাম আছে যা কোডবেস জুড়ে ব্যবহৃত হয়। `IsAvailable` হার্ডওয়্যার-অবরুদ্ধ পথগুলোকে গেট করে — `TransportManager` সেগুলো এড়িয়ে যায় এবং পরবর্তী উপলব্ধ ট্রান্সপোর্টে ফিরে আসে।

**স্ট্যাটাস কি:** ✅ প্রকৃত, নির্মিত ও যাচাইকৃত · ⏳ প্রকৃত, যাচাই চলমান · ⚠️ কিছু প্ল্যাটফর্মে প্রকৃত, অন্যগুলোতে স্টাব · ❌ স্টাব (এখনও কোনো ট্রান্সপোর্ট কোড নেই)।

| রঙ | নাম | পাল্লা | ব্যান্ডউইথ | স্ট্যাটাস |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ প্রকৃত — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ প্রকৃত — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | সীমাহীন | ~10 Mbps | ✅ প্রকৃত — Windows; রিলে সার্ভার `samples/AetherNet.RelayServer/`-এ |
| 🟪 WebRTC P2P | Internet data channel | সীমাহীন | ~100 Mbps | ✅ সব ৮টি ভাষায় প্রকৃত — **সব ৮টিতে লুপব্যাক-যাচাইকৃত** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust প্রত্যেকের দুটি পিয়ার একটি প্রকৃত ICE ডেটা চ্যানেলের ওপর দিয়ে বাইট বিনিময় করে) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android-এ প্রকৃত (`android/white/`); Windows = প্রকৃত BLE-GATT + RSSI −40 dBm নৈকট্য আনুমানিকতা (`WinNfcBleTransportService`, net9/10 কম্পাইল হয়, রানটাইম-অযাচাইকৃত) — `Windows.Networking.Proximity` Win 11-এ সরানো হয়েছে |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ HarmonyOS-এ প্রকৃত (`harmonyos/teal/`, `@kit.NearLinkKit` — অন-ডিভাইস যাচাই বাকি); Android + Windows = প্রকৃত SSAP-over-BLE আনুমানিকতা (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; কম্পাইল + ইউনিট-টেস্ট যাচাইকৃত, রানটাইম-অযাচাইকৃত) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ প্রকৃত RYLR SX127x/SX126x সিরিয়াল ড্রাইভার (`LoRaSerialTransport` C#/Go/Rust/C-তে; কম্পাইল হয়, রানটাইম-অযাচাইকৃত — একটি ভৌত মডিউল প্রয়োজন); BLE Coded-PHY ব্রিজ এখনও একটি নথিভুক্ত ডিজাইন |

রেডিও ট্রান্সপোর্টগুলো শুধুমাত্র সেখানেই প্রকৃত যেখানে প্ল্যাটফর্ম কোড বিদ্যমান (C#/Windows, Kotlin/Android, HarmonyOS)। আটটি ভাষা লাইব্রেরি অন্যথায় টেস্টিংয়ের জন্য একটি **ইন-প্রসেস সিমুলেশন** ট্রান্সপোর্ট শিপ করে — **WebRTC হলো তাদের সবার জন্য সাধারণ প্রথম প্রকৃত ট্রান্সপোর্ট** (সম্পূর্ণ; ভাষাগুলো জুড়ে লুপব্যাক-যাচাইকৃত)।

অগ্রাধিকার পাওয়ার খরচ অনুযায়ী: রেডিও মেশ পছন্দনীয়, তারপর একটি সরাসরি ইন্টারনেট পথ হিসেবে WebRTC, শেষ অবলম্বন হিসেবে HTTP/QUIC রিলে সহ।

## ডিপ্লয়মেন্ট টিয়ার

Aether যেকোনো প্ল্যাটফর্মে কাজ করে যা Bluetooth বা Wi-Fi সমর্থন করে। আপনি যে টিয়ারে আছেন তা নির্ভর করে আপনি যে OS লক্ষ্য করছেন তার ওপর।

---

### স্ট্যান্ডার্ড টিয়ার — যেকোনো প্ল্যাটফর্ম

Android · Windows · Linux · macOS · iOS

Aether Bluetooth বা Wi-Fi হার্ডওয়্যার সহ যেকোনো ডিভাইসে চলে। যেখানে একটি রেডিও ভৌতভাবে অনুপস্থিত, প্রতিটি অবরুদ্ধ ট্রান্সপোর্ট যা উপলব্ধ তার ওপর আনুমানিক করা হয়। এই আনুমানিকতাগুলো এখন **প্রকৃত কোড** (কম্পাইল-যাচাইকৃত; একটি ২-ডিভাইস / হার্ডওয়্যার RF টেস্ট বাকি থাকায় **রানটাইম-অযাচাইকৃত**):

- **NearLink (Aether Teal)** — Android (`android/teal/AetherNetSleService`) এবং Windows (`WinNearLinkBleTransportService`)-এ প্রকৃত SSAP-over-BLE-GATT আনুমানিকতা (Aether SLE UUID `61657468-6572-0003-…`); কম্পাইল + ইউনিট-টেস্ট যাচাইকৃত, রানটাইম-অযাচাইকৃত। প্রকৃত NearLink রেডিও শুধুমাত্র HarmonyOS-এ বিদ্যমান (`harmonyos/teal/`, অন-ডিভাইস যাচাই বাকি)।
- **LoRa (Aether Red)** — প্রকৃত RYLR SX127x/SX126x সিরিয়াল ড্রাইভার (`LoRaSerialTransport` **সব ৮টি ভাষায়** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; প্রতিটি পোর্ট কম্পাইল-যাচাইকৃত, Mac বিল্ড সার্ভারে Swift + C সহ; রানটাইম-অযাচাইকৃত — একটি ভৌত মডিউল প্রয়োজন)। Meshtastic-over-BLE-Coded-PHY ব্রিজ (~1.3 km) একটি নথিভুক্ত ডিজাইন থেকে যায়; প্রকৃত দীর্ঘ-পাল্লার LoRa-এর জন্য একটি LoRa-সক্ষম নোড প্রয়োজন (গেটওয়ে, SBC, বা একটি LoRa মডিউল সহ রাগেড হ্যান্ডসেট)।
- **NFC (Aether White)** — Android-এ প্রকৃত (HCE)। Windows-এ এখন একটি প্রকৃত BLE-GATT + RSSI −40 dBm নৈকট্য আনুমানিকতা আছে (`WinNfcBleTransportService`, net9/10 কম্পাইল হয়; রানটাইম-অযাচাইকৃত); একটি রিডার উপস্থিত থাকলে ACR122U PC/SC।

সর্বত্র যা প্রকৃত ও অভিন্ন: **BLE, Wi-Fi Direct, HTTP/QUIC রিলে, এবং WebRTC P2P ট্রান্সপোর্ট (সব ৮টি ভাষায় লুপব্যাক-যাচাইকৃত)**, এবং সঙ্গে Signal Protocol নিরাপত্তা (X3DH + Double Ratchet), AODV রাউটিং, DTN স্টোর-অ্যান্ড-ফরোয়ার্ড, SOS সম্প্রচার, ভয়েস, এবং স্ট্রিমিং।

**সৎ স্ট্যাটাস:** BLE + Wi-Fi Direct + রিলে প্রোডাকশন-প্রকৃত; **WebRTC P2P সব ৮টি ভাষায় প্রকৃত ও লুপব্যাক-যাচাইকৃত** (দুটি পিয়ার একটি প্রকৃত ICE ডেটা চ্যানেলের ওপর দিয়ে বাইট বিনিময় করে — কার্যকর UDP ICE সহ `.201` Linux বক্সে Rust নিশ্চিত করা); NearLink / LoRa / NFC-on-Windows আনুমানিকতাগুলো এখন প্রকৃত কোড যা কম্পাইল হয় (LoRa সব ৮টিতে কম্পাইল-যাচাইকৃত, Mac বিল্ড সার্ভারে Swift + C সহ; NearLink-Android ইউনিট-টেস্টেডও) কিন্তু **রানটাইম-অযাচাইকৃত** — এখনও কোনো হার্ডওয়্যার / ২-ডিভাইস RF টেস্ট নেই। তারা কোডে মেশে অংশগ্রহণ করে; ফিল্ড-প্রমাণিত RF আশা করে এই তিনটি ডিপ্লয় করবেন না।

---

### নেটিভ টিয়ার — CircleOS / OpenHarmony

CircleOS · HarmonyOS · যেকোনো OpenHarmony-ভিত্তিক OS

CircleOS OpenHarmony-এর ওপর নির্মিত, যা একটি প্রথম-শ্রেণীর OS সক্ষমতা হিসেবে NearLink (SLE) সিলিকন এবং `@kit.NearLinkKit` SDK শিপ করে। NearLink হার্ডওয়্যার সহ CircleOS এবং HarmonyOS ডিভাইসে, কোনো আনুমানিকতা প্রয়োজন নেই — `harmonyos/teal/` সরাসরি প্রকৃত SLE রেডিও ব্যবহার করে:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

এটি শুধু স্ট্যান্ডার্ড টিয়ারের একটি ভালো সংস্করণ নয়। NearLink লেয়ারে এটি একটি শ্রেণীগতভাবে ভিন্ন নেটওয়ার্ক:

| সক্ষমতা | স্ট্যান্ডার্ড টিয়ার (BLE approx) | নেটিভ টিয়ার (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink পাল্লা** | ~100 m (BLE) | **600 m** |
| **NearLink ব্যান্ডউইথ** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink লেটেন্সি** | ~10 ms (BLE) | **20 µs** |
| **NearLink পাওয়ার** | BLE বেসলাইন | **BLE 5.0-এর চেয়ে 60% কম** |
| **সমসাময়িক NearLink পিয়ার** | ~7 (BLE সংযোগ সীমা) | **500+** |
| **NearLink উৎস** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | প্রকৃত SLE রেডিও (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | নেটিভ | নেটিভ (অভিন্ন) |
| **Signal Protocol নিরাপত্তা** | সম্পূর্ণ | সম্পূর্ণ (অভিন্ন) |
| **রাউটিং / DTN / SOS** | সম্পূর্ণ | সম্পূর্ণ (অভিন্ন) |
| **Aether Tag পরিচয়** | সমর্থিত | সমর্থিত (অভিন্ন) |

---

### টিয়ারের মধ্যে স্থানান্তর

কোনো কোড পরিবর্তন প্রয়োজন নেই। টিয়ার প্রতিটি ট্রান্সপোর্ট সার্ভিসে `IsAvailable` দ্বারা রানটাইমে নির্ধারিত হয়:

1. NearLink সিলিকন সহ একটি CircleOS বা HarmonyOS ডিভাইসে, NearLink ট্রান্সপোর্টে `IsAvailable` `true` রিটার্ন করে (পারমিশন চেক + প্যাসিভ স্ক্যান প্রচেষ্টার মাধ্যমে হার্ডওয়্যার-প্রোবড)।
2. `TransportManager` স্বয়ংক্রিয়ভাবে NearLink-কে অগ্রাধিকার অবস্থানে উন্নীত করে — সর্বনিম্ন পাওয়ার খরচ, সর্বোচ্চ ব্যান্ডউইথ।
3. অ্যাপ কোড, প্যাকেট ফরম্যাট, রাউটিং অ্যালগরিদম, নিরাপত্তা লেয়ার, এবং Aether Tag উভয় টিয়ারে অভিন্ন।

স্ট্যান্ডার্ড টিয়ারের একটি নোড এবং নেটিভ টিয়ারের একটি নোড অবাধে যোগাযোগ করতে পারে — তারা একই তার ফরম্যাট, একই Signal Protocol সেশন, এবং একই Aether Tag ভাগ করে। টিয়ারের পার্থক্য শুধুমাত্র NearLink প্যাকেটের জন্য ব্যবহৃত রেডিওকে প্রভাবিত করে, তার ওপরের প্রোটোকলকে নয়।

---

> **অভ্যন্তরীণভাবে এই টিয়ারগুলোকে Asterix ভ্যারিয়েন্ট (স্ট্যান্ডার্ড) এবং Obelix ভ্যারিয়েন্ট (নেটিভ) হিসেবে উল্লেখ করা হয়।** Asterix যা উপলব্ধ তা দিয়ে ভালোভাবে কাজ করে। Obelix — নেটিভ NearLink সহ CircleOS-এ চলমান — স্থায়ীভাবে উন্নত সক্ষমতায় পরিচালিত হয়, যেভাবে Obelix ম্যাজিক পোশনের শক্তি বহন করে আবার পান করার প্রয়োজন ছাড়াই।

---

## বাস্তবায়ন

Aether ৮টি ভাষায় নির্মিত যাতে এটি ফোন, ল্যাপটপ, ট্যাবলেট এবং মাইক্রোকন্ট্রোলারে চলে। সব বাস্তবায়ন তার-সামঞ্জস্যপূর্ণ প্যাকেট তৈরি করে — Rust নোড দ্বারা এনক্রিপ্ট করা একটি বার্তা Python নোড দ্বারা রিলে করা এবং Swift নোড দ্বারা ডিক্রিপ্ট করা যায়।

| ভাষা | ডিরেক্টরি | তার ফরম্যাট | রাউটিং/DTN/SOS | X3DH | Double Ratchet | OPK পুল | ভয়েস/গ্রুপ | স্ট্রিমিং/ভিডিও/ওয়াচ |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

সব ৮টি ভাষা বাইট-অভিন্ন তার প্যাকেট তৈরি করে, CI-তে চালানো ১৪টি ক্যানোনিক্যাল তার-ফরম্যাট ফিক্সচার এবং ৪টি Signal টেস্ট ভেক্টর দিয়ে যাচাইকৃত (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`)। রাউটিং (AODV-স্টাইল RREQ/RREP), DTN স্টোর-অ্যান্ড-ফরোয়ার্ড, SOS সম্প্রচার, ভয়েস, স্ট্রিমিং, এবং নিরাপত্তা-শক্তিশালীকরণ সার্ভিস প্রতিটি ভাষায় বাস্তবায়িত সব ৮টি বাস্তবায়ন জুড়ে **~3,000 টেস্ট** সহ:

| ভাষা | টেস্ট | CI প্ল্যাটফর্ম |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **মোট** | **~3,000** | |

ক্রস-ল্যাঙ্গুয়েজ Signal ইন্টারঅপ `fixtures/signal/`-এ নোঙর করা, X3DH (`x3dh_basic`), সিমেট্রিক র‍্যাচেট (`ratchet_step_basic`, `ratchet_step_three_iterations`), এবং KDF_RK (`kdf_rk_basic`)-এর জন্য ভাগ করা টেস্ট ভেক্টর সহ। প্রতিটি বাস্তবায়নকে সেই ফিক্সচারগুলোর বিপরীতে বাইট-অভিন্ন আউটপুট তৈরি করতে হবে। সব ৮টি ভাষা এখন একটি সম্পূর্ণ Signal সেশন শিপ করে (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`)।

তার ফরম্যাট এবং Signal-এর বাইরে, **সম্পূর্ণ তার-সার্ভিস স্যুট** — প্রেজেন্স, হার্টবিট, প্রোফাইল সিঙ্ক, ক্ষণস্থায়ী-ID ঘোষণা, প্রি-কি এক্সচেঞ্জ, চ্যানেল, পুশ-টু-টক, স্ক্রিন শেয়ার, কল কন্ট্রোল, SOS স্বীকৃতি, স্পেস ব্রেডক্রাম্ব, ফোর্জ ঘোষণা, ভল্ট শার্ড অনুরোধ, এবং ব্যান্ডউইথ পরিমাপ (দেখুন **আপনি যা পান**) — একইভাবে সব ৮টি ভাষায় বাস্তবায়িত এবং নিজস্ব ফিক্সচারে পিন করা (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, এবং সহোদর)। প্রোটোকল লেয়ারে কোনো ফিচার C#-শুধুমাত্র নয়।

## কুইকস্টার্ট

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

ডেমোটি আপনাকে ৮টি ধাপের মধ্য দিয়ে নিয়ে যায়: তিনটি নোডের (Alice, Bob, Charlie) জন্য Ed25519 আইডেন্টিটি কি জেনারেট করা, Signal Protocol সেশন প্রতিষ্ঠা করা, এনক্রিপ্ট করা বার্তা পাঠানো, Charlie-এর মাধ্যমে একটি বার্তা রিলে করা (যে এটি পড়তে পারে না), বাইনারি তার ফরম্যাট দেখানো, এবং ৫টি পরপর বার্তা জুড়ে ফরোয়ার্ড সিক্রেসি প্রদর্শন করা। আউটপুট রঙ-কোডেড এবং ধাপগুলোর মধ্যে বিরতি দেয়।

**C#-এ একটি বার্তা পাঠান:**

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

ডেমোটি দুটি নোডের জন্য আইডেন্টিটি কি জেনারেট করে, প্রি-কি বান্ডেল বিনিময় করে, এনক্রিপ্ট করা সেশন প্রতিষ্ঠা করে, উভয় দিকে এনক্রিপ্ট করা বার্তা পাঠায়, মেশ প্যাকেট তৈরি ও স্বাক্ষর করে, স্বাক্ষর যাচাই করে, এবং প্যাকেটগুলোকে বাইনারি তার ফরম্যাটে সিরিয়ালাইজ করে। এটি ইন-প্রসেস ট্রান্সপোর্ট লেয়ারও প্রদর্শন করে।

**Rust-এ একটি বার্তা পাঠান:**

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

ডেমোটি একটি সিমুলেটেড নেটওয়ার্কে দুটি নোড তৈরি করে, Ed25519 কি জেনারেট করে, Signal Protocol সেশন প্রতিষ্ঠা করে, একটি প্যাকেট তৈরি ও স্বাক্ষর করে, এটিকে C#-সামঞ্জস্যপূর্ণ বাইনারি ফরম্যাটে সিরিয়ালাইজ করে, একটি গোপন বার্তা এনক্রিপ্ট করে, অন্য নোডে এটি ডিক্রিপ্ট করে, ট্রান্সপোর্টের মাধ্যমে এটি পাঠায়, এবং রাউন্ড-ট্রিপ যাচাই করে।

**TypeScript-এ একটি বার্তা পাঠান:**

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

ডেমোটি ৮টি প্রদর্শন চালায়: Ed25519 কি জেনারেশন ও টেম্পার সনাক্তকরণ, সক্ষমতা সহ নোড তৈরি, Signal Protocol X3DH কি এক্সচেঞ্জ, AES-256-GCM এনক্রিপশন ও ডিক্রিপশন, প্যাকেট সিরিয়ালাইজেশন, রিপ্লে সনাক্তকরণ সহ প্যাকেট স্বাক্ষর, ইন-প্রসেস ট্রান্সপোর্ট, এবং সব লেয়ার একত্রিত করে একটি সম্পূর্ণ এন্ড-টু-এন্ড ফ্লো।

**Python-এ একটি বার্তা পাঠান:**

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

ডেমোটি ৫টি প্রদর্শন চালায়: প্যাকেট সিরিয়ালাইজেশন রাউন্ড-ট্রিপ, টেম্পার সনাক্তকরণ সহ Ed25519 স্বাক্ষর, উভয় দিকে এনক্রিপ্ট করা মেসেজিং সহ Signal Protocol সেশন প্রতিষ্ঠা, দুটি পিয়ারের মধ্যে ইন-প্রসেস ট্রান্সপোর্ট, এবং রিপ্লে সুরক্ষার জন্য নন্স ডিডুপ্লিকেশন।

**Go-তে একটি বার্তা পাঠান:**

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

ডেমোটি ১১টি ধাপের মধ্য দিয়ে যায়: কি জেনারেশন, সক্ষমতা সহ নোড তৈরি, Signal Protocol ইনিশিয়ালাইজেশন, প্রি-কি বান্ডেল এক্সচেঞ্জ, সেশন প্রতিষ্ঠা, প্যাকেট তৈরি ও স্বাক্ষর, সিরিয়ালাইজেশন, স্বাক্ষর যাচাই সহ ডিসিরিয়ালাইজেশন, কি র‍্যাচেটিং সহ এন্ড-টু-এন্ড এনক্রিপশন, রিপ্লে অ্যাটাক সনাক্তকরণ, এবং ইন-প্রসেস ট্রান্সপোর্ট।

**Kotlin-এ একটি বার্তা পাঠান:**

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

ডেমোটি ৫টি টেস্ট চালায়: প্যাকেট সিরিয়ালাইজেশন রাউন্ড-ট্রিপ, টেম্পার প্রত্যাখ্যান সহ Ed25519 স্বাক্ষর, AES-256-GCM এনক্রিপশন সহ Signal Protocol সেশন প্রতিষ্ঠা, ইন-প্রসেস ট্রান্সপোর্ট বার্তা সরবরাহ, এবং একটি সম্পূর্ণ এন্ড-টু-এন্ড ফ্লো যেখানে Alice একটি প্যাকেট স্বাক্ষর করে এবং Bob ট্রান্সপোর্টের পরে এটি যাচাই করে।

**Swift-এ একটি বার্তা পাঠান:**

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

ডেমোটি ৭টি প্রদর্শন চালায়: Ed25519 কি জেনারেশন, প্যাকেট তৈরি ও স্বাক্ষর, বাইনারি তার ফরম্যাটে সিরিয়ালাইজেশন, ইন্টিগ্রিটি চেক সহ ডিসিরিয়ালাইজেশন, AES-256-GCM এনক্রিপশন ও ডিক্রিপশন, HMAC-SHA256 বার্তা প্রমাণীকরণ, এবং HKDF-SHA256 কি উদ্ভব।

**C-তে একটি বার্তা পাঠান:**

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

## রোডম্যাপ

কী নির্মিত হয়েছে এবং পরবর্তীতে কী আসছে।

**সম্পন্ন (ক্রস-ল্যাঙ্গুয়েজ যাচাইকৃত, সব ৮টি বাস্তবায়ন):**
- তার ফরম্যাট: ৮টি ভাষা জুড়ে বাইট-অভিন্ন, ১৪টি ক্যানোনিক্যাল ফিক্সচার এবং CI-তে ক্রস-ল্যাঙ্গুয়েজ অ্যাসারশন দিয়ে নোঙর করা (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — ৯-জব ম্যাট্রিক্স (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, এবং ফিক্সচার ইন্টিগ্রিটি জব) `.github/workflows/ci.yml`-এ।
- Ed25519 প্যাকেট স্বাক্ষর ও যাচাই
- AES-256-GCM এনক্রিপশন
- HKDF / HMAC কি উদ্ভব প্রিমিটিভ
- প্যাকেট সিরিয়ালাইজেশন + স্বাক্ষর লেআউট (LE + 4-বাইট int32 ফিল্ড)
- ইন-প্রসেস ট্রান্সপোর্ট সিমুলেটর (ডেভেলপমেন্ট ও টেস্টের জন্য)
- RREQ/RREP, স্বাক্ষরিত রুট রিপ্লাই, ডিডুপ, TTL ফরোয়ার্ডিং সহ AODV-অনুপ্রাণিত রাউটিং সার্ভিস
- কাস্টডি ট্রান্সফার, geohash-সচেতন রেপ্লিকেশন, 72h TTL সহ DTN স্টোর-অ্যান্ড-ফরোয়ার্ড সার্ভিস
- ফ্লাড, ডিডুপ, সেল্ফ-অরিজিন গার্ড, রেট-লিমিট (3/hr) সহ SOS সম্প্রচার সার্ভিস
- এক্সটেনসিবিলিটি সিম: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (Noop ডিফল্ট)
- সব ৮টি ভাষা জুড়ে **~3,000 টেস্ট** (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — CI-তে সব সবুজ
- ✅ **প্রকৃত X3DH ক্ষণস্থায়ী কি (৮ ভাষা)** — HKDF-SHA256 রুট উদ্ভব সহ ৪টি X25519 DH (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`)। `fixtures/signal/expected/x3dh_basic.json` দিয়ে পিন করা।
- ✅ **Double Ratchet পরিবার-ব্যাপী সারিবদ্ধকরণ** — সিমেট্রিক র‍্যাচেটে HMAC-SHA256 + 0x01/0x02 ডোমেইন সেপারেশন সহ সম্পূর্ণ Signal §5, DH-র‍্যাচেট ধাপে HKDF-SHA256 KDF_RK, রিসিভে DH-রোটেশন। `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` ফিক্সচার দিয়ে যাচাইকৃত।
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 HEAD-এর সঙ্গে সমন্বিত** — দেখুন `docs/PROTOCOL_SPEC.md`।

**সম্পন্ন (সব ৮টি ভাষা):**
- ✅ **ভয়েস কল (১-থেকে-১)** — সিগন্যালিং স্টেট মেশিন (Offer/Answer/Hangup/Cancel/Timeout) + বাইনারি ফ্রেম ট্রান্সপোর্ট (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes)। `IRoutingService`-এর মাধ্যমে রুট-সচেতন সরবরাহ।
- ✅ **গ্রুপ ভয়েস** — হোস্ট-চালিত সদস্যপদ (invite/kick/leave), প্রতি-ফ্রেম কি জেনারেশন ফিল্ড, সব বর্তমান সদস্যের কাছে ইউনিকাস্ট ফ্যান-আউট, সদস্যপদ পরিবর্তনে হোস্ট-নিয়ন্ত্রিত কি রোটেশন।
- ✅ **লাইভ স্ট্রিমিং** — প্রকাশক `StreamAnnounce` সম্প্রচার করে; সাবস্ক্রাইবাররা `StreamSubscribe` পাঠায়; বাইনারি `StreamSegment` ফ্রেম (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) প্রতিটি সাবস্ক্রাইবারের কাছে ইউনিকাস্ট।
- ✅ **ভিডিও কল (১-থেকে-১)** — সিগন্যালিংয়ে কোডেক/রেজোলিউশন/fps/বিটরেট নেগোসিয়েশন, কিফ্রেম-অনুরোধ ও কোয়ালিটি-পরিবর্তন সিগন্যাল, ভয়েস লেআউটের সঙ্গে মিলিত বাইনারি `VideoFrame` ফরম্যাট।
- ✅ **ওয়াচ টুগেদার** — হোস্ট প্রামাণিক `WatchSync` (play/pause/seek/speed) কমান্ড নির্গত করে; অনুসারীরা RTT ক্ষতিপূরণ সহ প্রয়োগ করে (`position = positionMs + elapsed × playbackSpeed`); ফায়ার-অ্যান্ড-ফরগেট `WatchReaction`।
- ✅ **One-time pre-key (OPK) পুল** — সব ৮টি ভাষা জুড়ে ডিফল্ট 100, FIFO ইস্যু, লেজি টপ-আপ, লক-সুরক্ষিত ব্যবহার। single-OPK কনকারেন্সি হ্যাজার্ড বন্ধ করে।
- ✅ **C: সম্পূর্ণ Signal সেশন** — `c/src/signal_protocol.c`-এ `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`; `c/tests/test_signal_session.c`-এ ৬টি two-node E2E টেস্ট। সব ৮টি ভাষার এখন সম্পূর্ণ সেশন-সক্ষম Signal Protocol আছে।

**সম্পন্ন (সব ৮টি ভাষা — সম্পূর্ণ তার-সার্ভিস স্যুট):**
- ✅ **প্রতিটি সংরক্ষিত প্যাকেট টাইপ এখন সব ৮টি ভাষায় একটি প্রকৃত, বাইট-অভিন্ন সার্ভিস।** প্রেজেন্স বীকন/কোয়েরি (21/22), হার্টবিট (10), প্রোফাইল সিঙ্ক (23), ephemeral-routing-ID ঘোষণা (56), প্রি-কি এক্সচেঞ্জ (25/26), চ্যানেল (7), পুশ-টু-টক (15), স্ক্রিন শেয়ার (32), কল কন্ট্রোল (27), SOS স্বীকৃতি (6), স্পেস ব্রেডক্রাম্ব (40), ফোর্জ ঘোষণা (41), ভল্ট শার্ড অনুরোধ (42), এবং ব্যান্ডউইথ পরিমাপ / ABMF (53/54/55)। প্রতিটি একটি পাতলা সার্ভিস (produce + handle + event) যা হোস্ট তার Signal সেশন এবং রাউটিং টেবিলের সঙ্গে যুক্ত করে; প্রতিটি একটি ভাগ করা ক্রস-ল্যাঙ্গুয়েজ ফিক্সচারে পিন করা (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) এবং প্রতি-ভাষা ইউনিট টেস্ট দিয়ে যাচাইকৃত, Swift এবং C macOS বিল্ড সার্ভারে যাচাইকৃত। দেখুন **আপনি যা পান**।

**সম্পন্ন (শুধুমাত্র C# রেফারেন্স):**
- ✅ **ডেমো ধাপ 9 — MessagingService + DTN ফলব্যাক এন্ড-টু-এন্ড** — `samples/AetherNet.Demo.Console` প্রাপক অফলাইন থাকলে DTN স্টোর-অ্যান্ড-ফরোয়ার্ড সহ প্রকৃত-Signal-এনক্রিপ্ট করা মেসেজিংয়ের মধ্য দিয়ে যায়।
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` ব্রিজ** — `SignalMessageEnvelopeCipher` মেসেজিং লেয়ারকে ডিফল্টভাবে এন্ড-টু-এন্ড এনক্রিপ্ট করে তোলে; Signal সেশন ছাড়া বার্তা সারিবদ্ধ হয়, কখনও অনিরাপদভাবে পাঠানো হয় না।
- ✅ **অ্যাডাপ্টিভ বিটরেট স্ট্রিমিং** — Profile A (রিয়েল-টাইম), B (লাইভ সম্প্রচার), এবং C (VOD)-এর জন্য স্পেক-বাধ্যতামূলক বিটরেট ল্যাডার সহ `AdaptiveBitrateController`। প্রকাশক সর্বোচ্চ টেকসই ধাপ নির্বাচন করে (20% হেডরুম) এবং ফ্লোরের নিচে থাকলে একটি সেগমেন্টের পরিবর্তে `StreamAbandon` (`PacketType.StreamAbandon`) নির্গত করে। `IStreamingService` `UpdateBandwidthEstimate` এবং `GetCurrentBitrateRung` প্রকাশ করে।
- ✅ **ওয়াচ টুগেদার: BitTorrent ইনজেস্ট + ChipIn গ্রুপ ফান্ডিং** — `TorrentInfo` / `TorrentFile` মডেল; `WatchTogetherService` `PacketType.TorrentMetadata` পরিচালনা করে এবং `TorrentReceived` ফায়ার করে। `ChipInPool` / `ChipInContribution` স্টেট মেশিন (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `IWatchTogetherService`-এ `StartChipInAsync` / `ContributeAsync` / `GetChipIn`।
- ✅ **অটো SFU রিলে সহ গ্রুপ ভিডিও কল** — `GroupVideoService` / `IGroupVideoService`। ≤ 3 অংশগ্রহণকারীর জন্য FullMesh টপোলজি; `GroupVideoSignaling(SfuAssigned)`-এর মাধ্যমে রিলে পুনঃবরাদ্দ সহ `SfuThresholdParticipants` (4)-এ SFU-তে স্বয়ংক্রিয় সুইচ। FullMesh-এ ফ্যান-আউট, SFU মোডে রিলে-শুধুমাত্র পাঠানো। সিগন্যালিং প্যাকেট টাইপ `GroupVideoSignaling = 35`।
- ✅ **BLE GATT ট্রান্সপোর্ট সিমুলেশন** — `SimulatedBleGattTransportService` (`IBleTransportService`)। `BleGattFramer`-এর মাধ্যমে GATT MTU ফ্রেমিং (1024 B/frame, `[2B count][2B index][payload]`), ইন-প্রসেস স্ট্যাটিক পিয়ার রেজিস্ট্রি, বিজ্ঞাপন সম্প্রচার। সব `BleMaxPayloadBytes` সীমাবদ্ধতা বলবৎ।
- ✅ **Wi-Fi Direct ট্রান্সপোর্ট সিমুলেশন** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`)। স্পষ্ট `ConnectAsync`/`DisconnectAsync` লাইফসাইকেল, সরাসরি বড়-পেলোড সরবরাহ (কোনো ফ্রেমিং নেই), দ্বিমুখী `PeerConnected`/`PeerDisconnected` ইভেন্ট।
- ✅ **NearLink ট্রান্সপোর্ট সিমুলেশন** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`)। 4096 B ফ্রেম MTU, 500-পিয়ার রেজিস্ট্রি, `ConnectedPeerCount`, রানটাইমে `IsAvailable` সেটযোগ্য।
- ✅ **RF ব্রিং-আপ সিমুলেশন টেস্ট** — Two-node ইন্টারঅপ টেস্ট (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` রাউন্ড-ট্রিপ, WiFi Direct 64 KB পেলোড ট্রান্সফার। সফটওয়্যার লেয়ার সম্পূর্ণ যাচাইকৃত; অন-হার্ডওয়্যার বৈধতার জন্য ভৌত ডিভাইস ল্যাব সেশন প্রয়োজন।

**সম্পন্ন (C# ট্রান্সপোর্ট লেয়ার — সব ফেইল-ফাস্ট):**
- ✅ **BLE GATT প্রকৃত ট্রান্সপোর্ট** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT সার্ভার)। `samples/AetherNet.BleRfTest/`-এ সম্পূর্ণ RF ব্রিং-আপ টেস্ট।
- ✅ **Wi-Fi Direct প্রকৃত ট্রান্সপোর্ট** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket পোর্ট 8888) + `android/green/` (`WifiP2pManager`)। `samples/AetherNet.WifiDirectRfTest/`-এ RF টেস্ট।
- ✅ **HTTP রিলে ট্রান্সপোর্ট (Aether Purple)** — 10-সেকেন্ড লং-পোল সহ `HttpRelayTransportService`, `PowerCostRelative = 100`, সর্বদা শেষ অবলম্বন। রিলে সার্ভার `samples/AetherNet.RelayServer/`-এ (ASP.NET Core minimal API, পোর্ট 5200)। `samples/AetherNet.RelayRfTest/`-এ RF টেস্ট।
- ✅ **NFC (Aether White)** — `android/white/` AID `F061657468657200` সহ `HostApduService` বাস্তবায়ন করে। `WinNfcStubTransportService` দুটি Windows আনুমানিকতা পথ নথিভুক্ত করে: (1) RSSI গেট ≥ −40 dBm সহ NDEF-over-BLE-GATT (NFC সিলিকন ছাড়া tap-to-connect সিমুলেট করে, `IsAvailable = Bluetooth present`); (2) `Windows.Devices.SmartCards` PC/SC-এর মাধ্যমে ACR122U USB রিডার (`IsAvailable = contactless reader enumerated`)। আপগ্রেড পথ: Microsoft একটি প্রথম-পক্ষ P2P NFC API শিপ করলে `ITransportService` বাস্তবায়ন করুন।
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`) ব্যবহার করে সম্পূর্ণ HarmonyOS 5.0.1 (API 13) ArkTS বাস্তবায়ন; `isAvailable` রানটাইমে প্রোবড। `WinNearLinkStubTransportService` + `android/teal/` SSAP-over-BLE আনুমানিকতা নথিভুক্ত করে: Aether SLE সার্ভিস UUID `61657468-6572-0003-0000-000000000000` সহ BLE GATT — SSAP-এর সঙ্গে API-অনুরূপ, প্রকৃত NearLink হার্ডওয়্যারের সঙ্গে তার-সামঞ্জস্যপূর্ণ নয়। আপগ্রেড পথ: BLE GATT কলগুলো `ssapc_*`/`ssaps_*` SDK কল দিয়ে প্রতিস্থাপন করুন; UUID এবং `TransportManager` স্লট অপরিবর্তিত।
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` Meshtastic-over-BLE-LR আনুমানিকতা নথিভুক্ত করে: BLE 5.0 Coded PHY S=8 (~1.3 km আউটডোর)-এর ওপর সম্পূর্ণ Meshtastic তার ফরম্যাট (16-বাইট হেডার + AES-256-CTR protobuf), ম্যানেজড-ফ্লাড রাউটিং এবং RSSI-ওয়েটেড কনটেনশন উইন্ডো সহ। প্রকৃত LoRa হার্ডওয়্যারের সঙ্গে ব্রিজ-নোড ফেডারেশন স্বয়ংক্রিয়ভাবে কাজ করে (একই Meshtastic প্যাকেট ফরম্যাট, কোনো অনুবাদ নেই)। আপগ্রেড পথ: BLE LR রেডিওকে SX1276/SX1278 AT-কমান্ড বা SPI ড্রাইভার দিয়ে প্রতিস্থাপন করুন; প্যাকেট ফরম্যাট এবং রাউটিং অপরিবর্তিত।

**খোলা — `OPEN_ISSUES.md`-এ ট্র্যাক করা:**
- প্রকৃত হার্ডওয়্যারে RF ব্রিং-আপ: ভৌত BLE / Wi-Fi Direct ডিভাইসে এন্ড-টু-এন্ড two-node ইন্টারঅপ টেস্ট (সিমুলেশন টেস্ট পাস করে; হার্ডওয়্যার ল্যাব সেশন প্রয়োজন)
- NearLink: `harmonyos/teal/` সম্পূর্ণ; Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 হার্ডওয়্যার প্রয়োজন (নন-Huawei ডিভাইসে NearLink সিলিকন উপস্থিত নেই)। Windows + Android স্বয়ংক্রিয়ভাবে SSAP-over-BLE আনুমানিকতায় ফিরে আসে।
- LoRa / CircleLink: প্রকৃত LoRa পাল্লার জন্য রেডিও মডিউল প্রয়োজন। একটি ছাড়া, Meshtastic তার ফরম্যাট BLE LR (~1.3 km)-এর ওপর বহন করা হয় এবং প্রকৃত LoRa হার্ডওয়্যারের সঙ্গে ব্রিজ-নোড ফেডারেশন উপলব্ধ।
- ✅ **(সমাধানকৃত v1.2.0)** কনজিউমার প্রোটোকল সারফেস (Wave 16/17) — ইনবাউন্ড বান্ডেলের জন্য `IDtnService.BundleReceived` ইভেন্ট ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), অ্যাপ্লিকেশন-লেয়ার নেমিং/ডিসকভারি ডিরেক্টরি ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), অথর-টিপিং ইন্টারফেস ([#61](https://github.com/bhengubv/aether-protocol/issues/61))। ৩টিই বাইট-সমান ক্রস-ল্যাঙ্গুয়েজ ফিক্সচার সহ ৮টি ভাষা জুড়ে সংযোজনমূলকভাবে শিপ করা। দেখুন CHANGELOG।

**এখনও বাহ্যিক অবদানের জন্য খোলা নয়:**
- প্রোটোকলটি এখনও সক্রিয় বিকাশের অধীনে। এই মুহূর্তে বাহ্যিক অবদান গৃহীত হচ্ছে না।
- NearLink ট্রান্সপোর্ট বাস্তবায়ন, Android/iOS ইন্টিগ্রেশন উদাহরণ, অতিরিক্ত ট্রান্সপোর্ট ব্যাকএন্ড, পারফরম্যান্স বেঞ্চমার্ক, এবং প্রোটোকল ফাজিং অভ্যন্তরীণভাবে ট্র্যাক করা হয় এবং প্রকল্পটি একটি স্থিতিশীল সর্বজনীন অবদান পয়েন্টে পৌঁছালে খোলা হবে।

## প্রকল্প কাঠামো

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

## একটি নতুন ট্রান্সপোর্ট যোগ করা

`ITransportService` বাস্তবায়ন করুন:

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

এটি DI-তে নিবন্ধন করুন এবং `TransportManager` স্বয়ংক্রিয়ভাবে এটিকে ট্রান্সপোর্ট নির্বাচনে অন্তর্ভুক্ত করবে, পাওয়ার খরচ অনুযায়ী সাজানো।

## এটি কীভাবে তুলনা করে

| প্রোটোকল | সীমাবদ্ধতা | Aether সুবিধা |
|----------|-----------|-----------------|
| **Briar** | শুধুমাত্র-Android, Tor-নির্ভর | ক্রস-প্ল্যাটফর্ম, বিশুদ্ধ মেশ |
| **Meshtastic** | শুধুমাত্র LoRa (সর্বোচ্চ 30 kbps) | মাল্টি-ট্রান্সপোর্ট (BLE + WiFi + NearLink), ভয়েস ও স্ট্রিমিং সক্ষম |
| **Reticulum** | Python, ছোট কমিউনিটি | ৮টি ভাষা, সবগুলো জুড়ে তার-সামঞ্জস্যপূর্ণ |
| **libp2p** | ইন্টারনেট ব্যাকবোন ধরে নেয় | অফলাইন-প্রথম, শূন্য অবকাঠামো নিয়ে কাজ করে |
| **Yggdrasil** | ওভারলে নেটওয়ার্ক, ইন্টারনেট প্রয়োজন | ভৌত-স্তর মেশ, ইন্টারনেট ছাড়া কাজ করে |
| **Signal** | কোনো মেশ নেই, ইন্টারনেট প্রয়োজন | অফলাইনে কাজ করে, P2P, মেশ রিলে, একই E2E এনক্রিপশন |

## এক্সটেনশন পয়েন্ট

প্রোটোকলটি স্বতন্ত্রভাবে কাজ করে। এই ইন্টারফেসগুলো আপনাকে চাইলে আপনার নিজস্ব ব্যাকএন্ড প্লাগ ইন করতে দেয়:

- `IAetherNetIncentiveProvider` — যেসব নোড ট্রাফিক রিলে করে তাদের পুরস্কৃত করুন (no-op ডিফল্ট: পরার্থপর রিলেয়িং)
- `IAetherNetBackendClient` — ইন্টারনেট উপলব্ধ থাকলে একটি সার্ভারের সঙ্গে সিঙ্ক করুন (no-op ডিফল্ট: সম্পূর্ণ অফলাইন)
- `IAetherNetFeatureFlagProvider` — রানটাইমে প্রোটোকল ফিচার টগল করুন (no-op ডিফল্ট: সবকিছু সক্ষম)

তিনটিই no-op বাস্তবায়ন সহ শিপ করে। এগুলো সরিয়ে ফেলুন এবং কিছুই ভাঙবে না।

## অবদান

বাহ্যিক অবদান এখনও খোলা নয়। প্রকল্পটি এখনও সক্রিয় বিকাশের অধীনে। যখন আমরা একটি সর্বজনীন অবদান উইন্ডো ঘোষণা করি তখন আবার দেখুন।

## নিরাপত্তা

দায়িত্বশীল প্রকাশ নীতির জন্য [SECURITY.md](SECURITY.md) দেখুন।

## লাইসেন্স

MIT License। [LICENSE](LICENSE) দেখুন।

## অনুবাদ

এই README এই ফাইলের উপরের ভাষা বারে তালিকাভুক্ত অন্যান্য ভাষাগুলোতেও [`docs/i18n/`](docs/i18n/)-এর অধীনে রক্ষণাবেক্ষণ করা হয় — ইউরোপীয়, পূর্ব এশীয়, মধ্যপ্রাচ্যীয়, দক্ষিণ এশীয়, দক্ষিণ-পূর্ব এশীয়, এবং আফ্রিকান ভাষা জুড়ে, কারণ যাদের কোনো ডেটা নেই তাদের জন্য নির্মিত একটি নেটওয়ার্কের সদর দরজা এমন হওয়া উচিত নয় যা শুধুমাত্র সুসংযুক্তরাই পড়তে পারে। **ইংরেজি সংস্করণটি সত্যের উৎস**: যেখানে একটি অনুবাদ এবং ইংরেজি টেক্সট অমিল হয়, ইংরেজি টেক্সট আধিকারিক, এবং অনুবাদগুলো একটি বা দুটি রিলিজ পিছিয়ে থাকতে পারে। বর্ণিত প্রোটোকল, কোড, ফিক্সচার এবং আচরণ আপনি যে ভাষাতেই পড়ুন না কেন অভিন্ন।
