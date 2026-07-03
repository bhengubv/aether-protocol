# AetherNet — protokol jaringan mesh yang mengutamakan luring

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet adalah protokol jaringan mesh sumber-terbuka berlisensi MIT** untuk mengirim pesan, file, suara, dan video ke orang-orang di sekitar — dengan **tanpa internet, tanpa server, dan tanpa pendaftaran**. Perangkat terhubung langsung melalui Bluetooth, Wi-Fi Direct, NearLink, dan LoRa; ketika penerima di luar jangkauan, pesan melompat melalui perangkat lain dan menunggu hingga 72 jam untuk sebuah rute. Ia dikirimkan dalam **implementasi yang identik byte-per-byte dalam delapan bahasa pemrograman** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, dan C.

Berbagi file, pesan, dan streaming dengan orang-orang di sekitar. Tanpa WiFi. Tanpa data seluler. Tanpa pendaftaran. Seperti AirDrop, tetapi bekerja dengan semua orang, di setiap platform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](../fr/README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Satu protokol, delapan bahasa, identik di kabel.** Aether diimplementasikan dalam **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, dan C** — dan setiap paket identik byte-per-byte di seluruhnya, ditegakkan oleh korpus fixture lintas-bahasa bersama di CI. Bangun node Anda dalam salah satu dari kedelapan bahasa itu; ia beroperasi bersama dengan semua yang lain. README ini juga tersedia dalam 11 bahasa manusia (tautan di atas).

## Apa yang bisa Anda lakukan dengannya?

**Berbagi catatan kuliah tanpa menghabiskan data.**

Anda sedang di kelompok belajar. Seseorang punya soal-soal ujian lampau di ponselnya. Aether mengirimkannya langsung ke perangkat Anda melalui Bluetooth — tanpa hotspot, tanpa grup WhatsApp, tanpa batas ukuran file. Jika seseorang di kelompok itu di luar jangkauan, file itu melompat melalui perangkat lain sampai mencapainya. Pesan menunggu hingga 72 jam untuk sebuah rute jika diperlukan.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Cari tahu apa yang terjadi di sekitar Anda.**

Anda sedang di acara kampus atau festival. Aether menemukan perangkat lain di sekitar melalui Bluetooth dan WiFi Direct — tanpa umpan aplikasi, tanpa algoritma. Anda melihat apa yang sebenarnya ada di sekitar, bukan apa yang dipromosikan.

**Kirim SOS ketika tidak ada sinyal.**

Ponsel Anda tidak punya jangkauan. Aether menyiarkan pesan darurat ke setiap perangkat dalam jangkauan, dan perangkat-perangkat itu meneruskannya. Tidak perlu menara seluler.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Buat kanal grup privat.**

Sebuah kanal untuk lantai asrama Anda, himpunan Anda, tim proyek Anda. Hanya anggota terverifikasi yang bisa membaca atau mengirim pesan. Tidak ada server yang menyimpan percakapan.

**Jual barang ke orang-orang di sekitar.**

Pasang sebuah buku teks untuk dijual. Orang yang berjalan dalam jangkauan mesh melihatnya. Tanpa akun marketplace, tanpa biaya pemasangan — hanya kedekatan.

**Nonton film bersama, melintasi mesh.**

Grup Anda mengadakan malam nonton film. Seseorang punya filenya. Aether menyinkronkan pemutaran di setiap perangkat — putar, jeda, cari — semuanya seiring langkah. Jika hanya sebagian orang yang punya filenya, mesh mendistribusikannya secara real-time sebagai streaming P2P. Semua orang urunan lewat SDPKT untuk membelinya jika tidak ada yang punya.

## Cara kerjanya

Perangkat berbicara langsung satu sama lain menggunakan Bluetooth, WiFi Direct, atau NearLink. Tanpa koneksi internet, tanpa server, tanpa infrastruktur pusat.

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

Ketika sebuah pesan tidak bisa mencapai tujuannya secara langsung, ia melompat melalui perangkat lain. Perangkat relay itu tidak bisa membaca apa yang mereka bawa — setiap pesan dienkripsi dengan AES-256-GCM. Setiap paket ditandatangani dengan kunci identitas Ed25519, dan paket palsu dibuang oleh jaringan.

> **Catatan kematangan keamanan (baca sebelum merilis):** X3DH sejati (4 X25519 DH), Signal Double Ratchet lengkap (langkah rotasi-DH saat menerima, KDF_RK, ratchet rantai 0x01/0x02), dan pool pre-key sekali pakai (default 100 OPK, FIFO, dilindungi kunci) diimplementasikan dalam **kedelapan bahasa** dan disematkan ke korpus fixture lintas-bahasa bersama di bawah `fixtures/signal/`. Satu-satunya item terbuka yang tersisa adalah bring-up RF fisik pada perangkat keras BLE nyata (dilacak di `OPEN_ISSUES.md`).

Tanpa akun, tanpa nomor telepon, tanpa email. Anda membuat sepasang kunci dan Anda sudah berada di jaringan.

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

**Perutean** — AODV dengan balasan rute yang ditandatangani. Setiap balasan rute ditandatangani oleh kunci Ed25519 milik tujuan, sehingga tidak ada perangkat yang bisa berpura-pura menjadi tujuan yang bukan dirinya.

**Store-and-forward** — Ketika tidak ada rute langsung, paket ditahan hingga 72 jam sampai sebuah jalur terbuka.

**Pemilihan transport** — Protokol memilih transport yang tepat per paket. Pesan kontrol kecil lewat BLE. Transfer besar memakai WiFi Direct. NearLink bila tersedia.

**Suara, video, dan streaming** — Panggilan video dengan negosiasi codec (H.264/H.265/VP8), pemilihan kualitas yang sadar-transport, video grup dengan relay SFU otomatis, nonton-bersama tersinkronisasi dengan kompensasi RTT, dan streaming bitrate adaptif.

**Perlindungan replay** — Deduplikasi nonce dengan jendela kesegaran timestamp 5 menit.

## Apa yang Anda dapatkan — setiap layanan, dalam setiap bahasa

Aether bukan sekadar transport. Setiap tipe paket yang direservasi oleh protokol kini menjadi **layanan nyata yang berfungsi dalam kedelapan bahasa**, dan setiap satunya diserialisasi menjadi **paket kabel yang identik byte** — sebuah paket yang dibangun oleh node Go didekode, tanpa perubahan, oleh node Swift, Rust, C, Python, TypeScript, Kotlin, atau C#. Setiap layanan disematkan ke fixture lintas-bahasa bersama di bawah `fixtures/<service>/` dan diuji oleh unit test per-bahasa, dengan Swift dan C selain itu diverifikasi pada server build macOS.

| Kemampuan | Apa yang dilakukannya | Tipe paket | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Beacon & kueri kehadiran** | Umumkan "Saya di sini" dan tanyakan "siapa yang di sekitar?" — melalui **ID efemeral berputar yang diturunkan dari kunci** (bukan identitas asli Anda) plus geohash kasar | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Keep-alive keaktifan yang ringan antara peer yang tertaut | 10 | `fixtures/heartbeat/` | ✅ |
| **Sinkronisasi profil** | Bertukar kartu profil bertanda tangan dengan peer melalui mesh | 23 | `fixtures/profiles/` | ✅ |
| **Pengumuman ID-efemeral** | Secara privat memberi tahu teman ID perutean berputar Anda saat ini agar mereka tetap bisa menghubungi Anda setelah ID itu berputar | 56 | `fixtures/erid/` | ✅ |
| **Pertukaran pre-key** | Meminta dan mengirim bundel pre-key Signal melalui mesh, untuk mem-bootstrap sesi ujung-ke-ujung dengan seseorang yang belum pernah Anda temui | 25, 26 | `fixtures/prekey/` | ✅ |
| **Kanal** | Pesan bertanda tangan ke kanal grup privat khusus-anggota | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Bingkai suara walkie-talkie (payload audio terenkode buram) | 15 | `fixtures/media/` | ✅ |
| **Berbagi layar** | Bingkai video berbagi layar (payload video terenkode buram) | 32 | `fixtures/media/` | ✅ |
| **Kontrol panggilan** | Sinyal dering / terima / tolak / tutup untuk panggilan suara dan video | 27 | `fixtures/videocall/` | ✅ |
| **Konfirmasi SOS** | Konfirmasi kepada pengirim bahwa siaran daruratnya telah diterima | 6 | `fixtures/sos/` | ✅ |
| **Remah roti ruang** | Remah penemuan bertanda-lokasi untuk lapisan "apa yang di sekitar saya" | 40 | `fixtures/space/` | ✅ |
| **Pengumuman forge** | Mengiklankan artefak konten turunan/tempaan ke mesh | 41 | `fixtures/forge/` | ✅ |
| **Permintaan shard vault** | Mengambil shard penyimpanan berkode-hapus (K dari N shard mana pun membangun ulang file) | 42 | `fixtures/vaultshard/` | ✅ |
| **Pengukuran bandwidth** | Menyelidiki / mengonfirmasi / menggosipkan throughput tautan agar mesh merutekan lewat pipa tergemuk (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Ini semua berada di atas layanan **messaging, suara 1-ke-1 dan grup, panggilan video, live streaming, nonton-bersama, perutean AODV, DTN store-and-forward, dan flood SOS** yang sudah lengkap — juga diimplementasikan dalam kedelapan bahasa.

> **Apa yang "dibangun" berarti di sini, secara tepat.** Setiap layanan memproduksi dan menangani paket kabelnya, memicu event yang tepat, dan disematkan ke fixture tingkat-byte yang harus dicocokkan oleh seluruh keluarga bahasa. Aplikasi Anda menghubungkan layanan itu ke sesi Signal-nya, tabel perutean, dan state lokal. Ini adalah lapisan protokol — terbukti dalam kode, tes, dan fixture-byte lintas-bahasa — dengan pijakan RF yang sama jujurnya seperti segala hal lain: jalur mana pun yang pada akhirnya menaiki radio belum terverifikasi di lapangan sampai bring-up perangkat keras yang dilacak di `OPEN_ISSUES.md`.

## Keamanan & privasi

Di luar rangkaian layanan-kabel, Aether menyertakan **lapisan keamanan & privasi** kecil — manajemen kunci-identitas dan anti-pelacakan di tingkat link-layer. Seperti segala hal lain, masing-masing diimplementasikan dalam **kedelapan bahasa** dan disematkan ke fixture lintas-bahasa bersama di bawah `fixtures/<feature>/` (Swift dan C juga diverifikasi di macOS build server). Ini *bukan* empat tambahan dari 18 layanan kabel: tiga di antaranya sama sekali **tidak mendefinisikan tipe paket kabel baru**, dan yang keempat membawa envelope-nya sendiri **di dalam jalur DTN/mesh yang sudah ada** alih-alih sebagai paket baru yang dicadangkan.

| Kemampuan | Apa yang dilakukannya | Lapisan | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Cadangan recovery-phrase** | Cadangkan sebuah identitas sebagai frasa **24-kata BIP-39** dan pulihkan di perangkat mana pun. BIP-39 standar (diverifikasi terhadap vektor Trezor resmi), ber-checksum SHA-256 sehingga kata yang salah ketik *ditolak*, tidak pernah salah secara diam-diam. Tanpa server, tanpa kustodian — frasa itu **adalah** identitasnya. | lokal | `fixtures/bip39/` | ✅ |
| **Perlindungan pelacakan Bluetooth** | Menurunkan BLE **Service UUID** yang berputar dan diturunkan-dari-kunci (HMAC-SHA256, jendela 15-menit) serta **alamat privat yang dapat diselesaikan** (IRK + fungsi RFC `ah`, AES-128) — materi anti-pelacakan yang dibutuhkan sebuah BLE advertiser agar pemindai pasif tidak dapat menghubungkannya lintas waktu atau tempat. | link-layer | `fixtures/bleprivacy/` | ✅ |
| **Panic-wipe** | Sebuah **duress PIN** (SHA-256, dibandingkan secara constant-time) yang, di bawah paksaan, menghapus dengan aman setiap kunci identitas — timpa-dengan-acak lalu nol — sehingga tidak ada yang tersisa untuk dipulihkan. | lokal | `fixtures/panicwipe/` | ✅ |
| **Sinkronisasi multi-perangkat** | Sinkronisasi **terdesentralisasi, tanpa server** di antara perangkat *milik Anda sendiri*: sebuah **DeviceLink** bertanda-tangan Ed25519 memasangkannya, dan envelope **SyncRecord** last-write-wins merekonsiliasi state — dibawa terenkripsi E2E melalui DTN/mesh yang sudah ada, tanpa akun cloud dan tanpa server sinkronisasi. | menumpang DTN | `fixtures/sync/` | ✅ |

**Satu asimetri yang jujur.** **DeviceLink** multi-perangkat bertanda-tangan Ed25519, dan tanda tangan itu **identik-byte di 7 dari 8 bahasa**. CryptoKit milik Apple sengaja *mengacak* tanda tangan Ed25519, sehingga di Swift ke-64 byte tanda tangan berbeda setiap kali — tetapi **badan yang ditandatangani identik-byte** dan setiap tautan tetap terverifikasi di semua 8 SDK, jadi Swift mencapai paritas **verifikasi** alih-alih paritas byte-tanda-tangan. Itu adalah properti platform-crypto, bukan cacat, dan itulah satu-satunya tempat di keempat fitur ini di mana "identik-byte" membawa tanda bintang (asterisk). Format kabel lengkap ada di [`PROTOCOL_SPEC.md`](../../PROTOCOL_SPEC.md) §12; model ancaman ada di [`THREAT_MODEL.md`](../../THREAT_MODEL.md).

## Transport

Setiap transport punya nama warna yang dipakai di seluruh basis kode. `IsAvailable` menjaga jalur yang terblokir perangkat keras — `TransportManager` melewatinya dan mundur ke transport tersedia berikutnya.

**Kunci status:** ✅ nyata, dibangun & diverifikasi · ⏳ nyata, verifikasi sedang berlangsung · ⚠️ nyata di sebagian platform, stub di platform lain · ❌ stub (belum ada kode transport).

| Warna | Nama | Jangkauan | Bandwidth | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Nyata — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Nyata — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | Relay HTTP / QUIC | Tak terbatas | ~10 Mbps | ✅ Nyata — Windows; server relay di `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Kanal data internet | Tak terbatas | ~100 Mbps | ✅ Nyata dalam kedelapan bahasa — **terverifikasi loopback di kedelapannya** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust masing-masing punya dua peer yang bertukar byte melalui kanal data ICE nyata) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Nyata di Android (`android/white/`); Windows = perkiraan kedekatan BLE-GATT nyata + RSSI −40 dBm (`WinNfcBleTransportService`, mengompilasi net9/10, belum terverifikasi runtime) — `Windows.Networking.Proximity` dihapus di Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Nyata di HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — menunggu verifikasi di perangkat); Android + Windows = perkiraan SSAP-over-BLE nyata (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; terverifikasi kompilasi + unit-test, belum terverifikasi runtime) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Driver serial RYLR SX127x/SX126x nyata (`LoRaSerialTransport` di C#/Go/Rust/C; mengompilasi, belum terverifikasi runtime — perlu modul fisik); jembatan BLE Coded-PHY masih berupa desain terdokumentasi |

Transport radio hanya nyata di tempat kode platform ada (C#/Windows, Kotlin/Android, HarmonyOS). Kedelapan pustaka bahasa selain itu mengirimkan transport **simulasi dalam-proses** untuk pengujian — **WebRTC adalah transport nyata pertama yang umum bagi semuanya** (lengkap; terverifikasi loopback di seluruh bahasa).

Prioritas berdasarkan biaya daya: mesh radio yang diutamakan, lalu WebRTC sebagai jalur internet langsung, dengan relay HTTP/QUIC sebagai upaya terakhir.

## Tingkatan penyebaran

Aether bekerja di platform mana pun yang mendukung Bluetooth atau Wi-Fi. Tingkatan yang Anda gunakan bergantung pada OS yang Anda targetkan.

---

### Tingkatan standar — platform apa pun

Android · Windows · Linux · macOS · iOS

Aether berjalan di perangkat mana pun dengan perangkat keras Bluetooth atau Wi-Fi. Di mana sebuah radio secara fisik tidak ada, setiap transport yang terblokir diperkirakan lewat apa yang tersedia. Perkiraan ini kini merupakan **kode nyata** (terverifikasi kompilasi; **belum terverifikasi runtime** menunggu uji RF 2-perangkat / perangkat keras):

- **NearLink (Aether Teal)** — perkiraan SSAP-over-BLE-GATT nyata (Aether SLE UUID `61657468-6572-0003-…`) di Android (`android/teal/AetherNetSleService`) dan Windows (`WinNearLinkBleTransportService`); terverifikasi kompilasi + unit-test, belum terverifikasi runtime. Radio NearLink nyata hanya ada di HarmonyOS (`harmonyos/teal/`, menunggu verifikasi di perangkat).
- **LoRa (Aether Red)** — driver serial RYLR SX127x/SX126x nyata (`LoRaSerialTransport` di **kedelapan bahasa** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; setiap port terverifikasi kompilasi, termasuk Swift + C di server build Mac; belum terverifikasi runtime — perlu modul fisik). Jembatan Meshtastic-over-BLE-Coded-PHY (~1,3 km) tetap berupa desain terdokumentasi; LoRa jarak-jauh nyata memerlukan node berkemampuan LoRa (gateway, SBC, atau handset tangguh dengan modul LoRa).
- **NFC (Aether White)** — nyata di Android (HCE). Windows kini punya perkiraan kedekatan BLE-GATT + RSSI −40 dBm nyata (`WinNfcBleTransportService`, mengompilasi net9/10; belum terverifikasi runtime); ACR122U PC/SC ketika pembaca hadir.

Apa yang nyata dan identik di mana-mana: **BLE, Wi-Fi Direct, relay HTTP/QUIC, dan transport WebRTC P2P (terverifikasi loopback dalam kedelapan bahasa)**, plus keamanan Signal Protocol (X3DH + Double Ratchet), perutean AODV, DTN store-and-forward, siaran SOS, suara, dan streaming.

**Status jujur:** BLE + Wi-Fi Direct + relay adalah nyata-produksi; **WebRTC P2P nyata dan terverifikasi loopback dalam kedelapan bahasa** (dua peer bertukar byte melalui kanal data ICE nyata — Rust dikonfirmasi di boks Linux `.201` dengan UDP ICE yang berfungsi); perkiraan NearLink / LoRa / NFC-di-Windows kini merupakan kode nyata yang mengompilasi (LoRa terverifikasi kompilasi di kedelapannya, termasuk Swift + C di server build Mac; NearLink-Android juga diuji-unit) tetapi **belum terverifikasi runtime** — belum ada uji RF perangkat keras / 2-perangkat. Mereka berpartisipasi dalam mesh di kode; jangan sebarkan ketiga itu dengan berharap RF yang terbukti di lapangan.

---

### Tingkatan native — CircleOS / OpenHarmony

CircleOS · HarmonyOS · OS berbasis OpenHarmony apa pun

CircleOS dibangun di atas OpenHarmony, yang mengirimkan silikon NearLink (SLE) dan SDK `@kit.NearLinkKit` sebagai kemampuan OS kelas satu. Pada perangkat CircleOS dan HarmonyOS dengan perangkat keras NearLink, tidak diperlukan perkiraan — `harmonyos/teal/` menggunakan radio SLE nyata secara langsung:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Ini bukan sekadar versi yang lebih baik dari tingkatan standar. Pada lapisan NearLink ini adalah jaringan yang secara kategoris berbeda:

| Kemampuan | Tingkatan standar (perkiraan BLE) | Tingkatan native (CircleOS / OpenHarmony) |
|---|---|---|
| **Jangkauan NearLink** | ~100 m (BLE) | **600 m** |
| **Bandwidth NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latensi NearLink** | ~10 ms (BLE) | **20 µs** |
| **Daya NearLink** | Baseline BLE | **60% lebih sedikit dari BLE 5.0** |
| **Peer NearLink bersamaan** | ~7 (batas koneksi BLE) | **500+** |
| **Sumber NearLink** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Radio SLE nyata (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / relay HTTP** | Native | Native (identik) |
| **Keamanan Signal Protocol** | Penuh | Penuh (identik) |
| **Perutean / DTN / SOS** | Penuh | Penuh (identik) |
| **Identitas Aether Tag** | Didukung | Didukung (identik) |

---

### Berpindah antar tingkatan

Tidak diperlukan perubahan kode. Tingkatan ditentukan saat runtime oleh `IsAvailable` pada setiap layanan transport:

1. Pada perangkat CircleOS atau HarmonyOS dengan silikon NearLink, `IsAvailable` pada transport NearLink mengembalikan `true` (diperiksa perangkat keras melalui pemeriksaan izin + upaya pemindaian pasif).
2. `TransportManager` secara otomatis mempromosikan NearLink ke posisi prioritas — biaya daya terendah, bandwidth tertinggi.
3. Kode aplikasi, format paket, algoritma perutean, lapisan keamanan, dan Aether Tag identik di kedua tingkatan.

Sebuah node di tingkatan standar dan sebuah node di tingkatan native dapat berkomunikasi dengan bebas — mereka berbagi format kabel yang sama, sesi Signal Protocol yang sama, dan Aether Tag yang sama. Perbedaan tingkatan hanya memengaruhi radio yang digunakan untuk paket NearLink, bukan protokol di atasnya.

---

> **Secara internal, tingkatan ini disebut sebagai varian Asterix (standar) dan varian Obelix (native).** Asterix bekerja dengan baik memakai apa yang tersedia. Obelix — berjalan di CircleOS dengan NearLink native — beroperasi pada kemampuan yang secara permanen ditinggikan, sebagaimana Obelix membawa kekuatan ramuan ajaib tanpa perlu meminumnya lagi.

---

## Implementasi

Aether dibangun dalam 8 bahasa agar ia berjalan di ponsel, laptop, tablet, dan mikrokontroler. Semua implementasi menghasilkan paket yang kompatibel-kabel — sebuah pesan yang dienkripsi oleh node Rust dapat direlai oleh node Python dan didekripsi oleh node Swift.

| Bahasa | Direktori | Format kabel | Routing/DTN/SOS | X3DH | Double Ratchet | Pool OPK | Suara/Grup | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

Kedelapan bahasa menghasilkan paket kabel yang identik byte, diverifikasi oleh 14 fixture format-kabel kanonis dan 4 vektor uji Signal yang dijalankan di CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Perutean (RREQ/RREP gaya-AODV), DTN store-and-forward, siaran SOS, suara, streaming, dan layanan pengerasan-keamanan diimplementasikan dalam setiap bahasa dengan **~3.000 tes** di seluruh 8 implementasi:

| Bahasa | Tes | Platform CI |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3.000** | |

Interop Signal lintas-bahasa berlabuh ke `fixtures/signal/` dengan vektor uji bersama untuk X3DH (`x3dh_basic`), ratchet simetris (`ratchet_step_basic`, `ratchet_step_three_iterations`), dan KDF_RK (`kdf_rk_basic`). Setiap implementasi harus menghasilkan output yang identik byte terhadap fixture itu. Kedelapan bahasa kini mengirimkan sesi Signal lengkap (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Di luar format kabel dan Signal, **seluruh rangkaian layanan-kabel** — kehadiran, heartbeat, sinkronisasi profil, pengumuman ID-efemeral, pertukaran pre-key, kanal, push-to-talk, berbagi layar, kontrol panggilan, konfirmasi SOS, remah roti ruang, pengumuman forge, permintaan shard vault, dan pengukuran bandwidth (lihat **Apa yang Anda dapatkan**) — juga diimplementasikan dalam kedelapan bahasa dan disematkan ke fixture-nya sendiri (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, dan saudaranya). Tidak ada fitur yang khusus-C# pada lapisan protokol.

## Mulai cepat

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

Demo memandu Anda melalui 8 langkah: membuat kunci identitas Ed25519 untuk tiga node (Alice, Bob, Charlie), membangun sesi Signal Protocol, mengirim pesan terenkripsi, merelai sebuah pesan melalui Charlie (yang tidak bisa membacanya), menampilkan format kabel biner, dan mendemonstrasikan kerahasiaan-maju di 5 pesan berturut-turut. Keluaran diberi kode-warna dan berhenti sejenak di antara langkah.

**Kirim pesan dalam C#:**

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

Demo membuat kunci identitas untuk dua node, bertukar bundel pre-key, membangun sesi terenkripsi, mengirim pesan terenkripsi di kedua arah, membuat dan menandatangani paket mesh, memverifikasi tanda tangan, dan menserialisasi paket ke format kabel biner. Ia juga mendemonstrasikan lapisan transport dalam-proses.

**Kirim pesan dalam Rust:**

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

Demo membuat dua node dalam jaringan tersimulasi, membuat kunci Ed25519, membangun sesi Signal Protocol, membuat dan menandatangani sebuah paket, menserialisasinya ke format biner yang kompatibel-C#, mengenkripsi sebuah pesan rahasia, mendekripsinya di node lain, mengirimnya melalui transport, dan memverifikasi perjalanan pulang-pergi.

**Kirim pesan dalam TypeScript:**

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

Demo menjalankan 8 demonstrasi: pembuatan kunci Ed25519 dan deteksi gangguan, pembuatan node dengan kemampuan, pertukaran kunci X3DH Signal Protocol, enkripsi dan dekripsi AES-256-GCM, serialisasi paket, penandatanganan paket dengan deteksi replay, transport dalam-proses, dan alur ujung-ke-ujung penuh yang menggabungkan semua lapisan.

**Kirim pesan dalam Python:**

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

Demo menjalankan 5 demonstrasi: perjalanan pulang-pergi serialisasi paket, penandatanganan Ed25519 dengan deteksi gangguan, pembangunan sesi Signal Protocol dengan pesan terenkripsi di kedua arah, transport dalam-proses antara dua peer, dan deduplikasi nonce untuk perlindungan replay.

**Kirim pesan dalam Go:**

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

Demo memandu melalui 11 langkah: pembuatan kunci, pembuatan node dengan kemampuan, inisialisasi Signal Protocol, pertukaran bundel pre-key, pembangunan sesi, pembuatan dan penandatanganan paket, serialisasi, deserialisasi dengan verifikasi tanda tangan, enkripsi ujung-ke-ujung dengan ratcheting kunci, deteksi serangan replay, dan transport dalam-proses.

**Kirim pesan dalam Kotlin:**

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

Demo menjalankan 5 tes: perjalanan pulang-pergi serialisasi paket, penandatanganan Ed25519 dengan penolakan gangguan, pembangunan sesi Signal Protocol dengan enkripsi AES-256-GCM, pengiriman pesan transport dalam-proses, dan alur ujung-ke-ujung penuh di mana Alice menandatangani sebuah paket dan Bob memverifikasinya setelah transport.

**Kirim pesan dalam Swift:**

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

Demo menjalankan 7 demonstrasi: pembuatan kunci Ed25519, pembuatan dan penandatanganan paket, serialisasi ke format kabel biner, deserialisasi dengan pemeriksaan integritas, enkripsi dan dekripsi AES-256-GCM, otentikasi pesan HMAC-SHA256, dan penurunan kunci HKDF-SHA256.

**Kirim pesan dalam C:**

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

## Peta jalan

Apa yang dibangun dan apa yang berikutnya.

**Selesai (terverifikasi lintas-bahasa, seluruh 8 implementasi):**
- Format kabel: identik byte di 8 bahasa, berlabuh oleh 14 fixture kanonis dan asersi lintas-bahasa di CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — matriks 9-job (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, plus job integritas fixture) di `.github/workflows/ci.yml`.
- Penandatanganan dan verifikasi paket Ed25519
- Enkripsi AES-256-GCM
- Primitif penurunan kunci HKDF / HMAC
- Serialisasi paket + tata letak penandatanganan (field LE + int32 4-byte)
- Simulator transport dalam-proses (untuk pengembangan dan tes)
- Layanan perutean terinspirasi-AODV dengan RREQ/RREP, balasan rute bertanda tangan, dedup, penerusan TTL
- Layanan DTN store-and-forward dengan transfer kustodi, replikasi sadar-geohash, TTL 72 jam
- Layanan siaran SOS dengan flood, dedup, penjaga asal-sendiri, batas-laju (3/jam)
- Sambungan ekstensibilitas: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (default Noop)
- **~3.000 tes** di seluruh 8 bahasa (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — semuanya hijau di CI
- ✅ **Kunci efemeral X3DH nyata (8 bahasa)** — 4 X25519 DH (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) dengan penurunan akar HKDF-SHA256. Disematkan oleh `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Penyelarasan Double Ratchet seluruh-keluarga** — Signal §5 lengkap dengan HMAC-SHA256 + pemisahan domain 0x01/0x02 di ratchet simetris, HKDF-SHA256 KDF_RK di langkah DH-ratchet, rotasi-DH saat menerima. Diverifikasi oleh fixture `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 direkonsiliasi dengan HEAD** — lihat `docs/PROTOCOL_SPEC.md`.

**Selesai (seluruh 8 bahasa):**
- ✅ **Panggilan suara (1-ke-1)** — mesin state pensinyalan (Offer/Answer/Hangup/Cancel/Timeout) + transport bingkai biner (16B callId · 4B seq · 8B timestamp · 1B isSilence · N byte). Pengiriman sadar-rute melalui `IRoutingService`.
- ✅ **Suara grup** — keanggotaan digerakkan-host (invite/kick/leave), field pembuatan kunci per-bingkai, fan-out unicast ke semua anggota saat ini, rotasi kunci dikendalikan-host saat perubahan keanggotaan.
- ✅ **Live streaming** — penerbit menyiarkan `StreamAnnounce`; pelanggan mengirim `StreamSubscribe`; bingkai biner `StreamSegment` (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N byte) unicast ke setiap pelanggan.
- ✅ **Panggilan video (1-ke-1)** — negosiasi codec/resolusi/fps/bitrate dalam pensinyalan, sinyal permintaan-keyframe dan perubahan-kualitas, format biner `VideoFrame` yang mencocokkan tata letak suara.
- ✅ **Watch Together** — host memancarkan perintah `WatchSync` otoritatif (play/pause/seek/speed); pengikut menerapkannya dengan kompensasi RTT (`position = positionMs + elapsed × playbackSpeed`); `WatchReaction` tembak-dan-lupakan.
- ✅ **Pool pre-key sekali pakai (OPK)** — default 100, penerbitan FIFO, pengisian-ulang malas, konsumsi dilindungi-kunci di seluruh 8 bahasa. Menutup bahaya konkurensi OPK-tunggal.
- ✅ **C: sesi Signal penuh** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` di `c/src/signal_protocol.c`; 6 tes E2E dua-node di `c/tests/test_signal_session.c`. Kedelapan bahasa kini punya Signal Protocol yang mampu-sesi penuh.

**Selesai (seluruh 8 bahasa — rangkaian layanan-kabel penuh):**
- ✅ **Setiap tipe paket yang direservasi kini menjadi layanan nyata yang identik byte di seluruh 8 bahasa.** Beacon/kueri kehadiran (21/22), heartbeat (10), sinkronisasi profil (23), pengumuman ID-perutean-efemeral (56), pertukaran pre-key (25/26), kanal (7), push-to-talk (15), berbagi layar (32), kontrol panggilan (27), konfirmasi SOS (6), remah roti ruang (40), pengumuman forge (41), permintaan shard vault (42), dan pengukuran bandwidth / ABMF (53/54/55). Masing-masing adalah layanan tipis (produksi + tangani + event) yang dihubungkan host ke sesi Signal dan tabel peruteannya; masing-masing disematkan ke fixture lintas-bahasa bersama (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) dan diuji oleh unit test per-bahasa, dengan Swift dan C diverifikasi pada server build macOS. Lihat **Apa yang Anda dapatkan**.

**Selesai (referensi C# saja):**
- ✅ **Demo Langkah 9 — MessagingService + fallback DTN ujung-ke-ujung** — `samples/AetherNet.Demo.Console` memandu melalui messaging terenkripsi-Signal-nyata dengan DTN store-and-forward ketika penerima luring.
- ✅ **Jembatan `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` membuat lapisan messaging terenkripsi ujung-ke-ujung secara default; pesan tanpa sesi Signal diantrekan, tidak pernah dikirim secara tidak aman.
- ✅ **Streaming bitrate adaptif** — `AdaptiveBitrateController` dengan tangga bitrate wajib-spek untuk Profil A (real-time), B (siaran langsung), dan C (VOD). Penerbit memilih anak tangga berkelanjutan tertinggi (headroom 20%) dan memancarkan `StreamAbandon` (`PacketType.StreamAbandon`) alih-alih segmen ketika di bawah lantai. `IStreamingService` mengekspos `UpdateBandwidthEstimate` dan `GetCurrentBitrateRung`.
- ✅ **Watch Together: ingest BitTorrent + pendanaan grup ChipIn** — model `TorrentInfo` / `TorrentFile`; `WatchTogetherService` menangani `PacketType.TorrentMetadata` dan memicu `TorrentReceived`. Mesin state `ChipInPool` / `ChipInContribution` (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` pada `IWatchTogetherService`.
- ✅ **Panggilan video grup dengan relay SFU otomatis** — `GroupVideoService` / `IGroupVideoService`. Topologi FullMesh untuk ≤ 3 peserta; peralihan otomatis ke SFU pada `SfuThresholdParticipants` (4) dengan penugasan-ulang relay via `GroupVideoSignaling(SfuAssigned)`. Fan-out di FullMesh, kirim relay-saja di mode SFU. Tipe paket pensinyalan `GroupVideoSignaling = 35`.
- ✅ **Simulasi transport BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Pembingkaian GATT MTU via `BleGattFramer` (1024 B/bingkai, `[2B count][2B index][payload]`), registri peer statis dalam-proses, siaran iklan. Semua kendala `BleMaxPayloadBytes` ditegakkan.
- ✅ **Simulasi transport Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Siklus hidup `ConnectAsync`/`DisconnectAsync` eksplisit, pengiriman payload-besar langsung (tanpa pembingkaian), event dua-arah `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulasi transport NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU bingkai 4096 B, registri 500-peer, `ConnectedPeerCount`, `IsAvailable` dapat-diatur saat runtime.
- ✅ **Tes simulasi bring-up RF** — Tes interop dua-node (`SimulatedTransportTests`): perjalanan pulang-pergi `MeshPacket` BLE + NearLink, transfer payload 64 KB WiFi Direct. Lapisan perangkat lunak terverifikasi penuh; diperlukan sesi lab perangkat fisik untuk validasi di-perangkat-keras.

**Selesai (lapisan transport C# — semua gagal-cepat):**
- ✅ **Transport nyata BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (server GATT Android). Tes bring-up RF penuh di `samples/AetherNet.BleRfTest/`.
- ✅ **Transport nyata Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Tes RF di `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Transport relay HTTP (Aether Purple)** — `HttpRelayTransportService` dengan long-poll 10-detik, `PowerCostRelative = 100`, selalu upaya terakhir. Server relay di `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). Tes RF di `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` mengimplementasikan `HostApduService` dengan AID `F061657468657200`. `WinNfcStubTransportService` mendokumentasikan dua jalur perkiraan Windows: (1) NDEF-over-BLE-GATT dengan gerbang RSSI ≥ −40 dBm (mensimulasikan tap-untuk-terhubung tanpa silikon NFC, `IsAvailable = Bluetooth hadir`); (2) pembaca USB ACR122U via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = pembaca nirsentuh terenumerasi`). Jalur peningkatan: implementasikan `ITransportService` ketika Microsoft mengirimkan API NFC P2P pihak-pertama.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implementasi ArkTS HarmonyOS 5.0.1 (API 13) penuh menggunakan `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` diperiksa saat runtime. `WinNearLinkStubTransportService` + `android/teal/` mendokumentasikan perkiraan SSAP-over-BLE: BLE GATT dengan Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — analog-API dengan SSAP, tidak kompatibel-kabel dengan perangkat keras NearLink nyata. Jalur peningkatan: ganti panggilan BLE GATT dengan panggilan SDK `ssapc_*`/`ssaps_*`; UUID dan slot `TransportManager` tidak berubah.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` mendokumentasikan perkiraan Meshtastic-over-BLE-LR: format kabel Meshtastic penuh (header 16-byte + protobuf AES-256-CTR) melalui BLE 5.0 Coded PHY S=8 (~1,3 km luar ruang), dengan perutean flood-terkelola dan jendela kontensi berbobot-RSSI. Federasi node-jembatan dengan perangkat keras LoRa nyata bekerja otomatis (format paket Meshtastic yang sama, tanpa penerjemahan). Jalur peningkatan: ganti radio BLE LR dengan driver AT-command atau SPI SX1276/SX1278; format paket dan perutean tidak berubah.

**Terbuka — dilacak di `OPEN_ISSUES.md`:**
- Bring-up RF pada perangkat keras nyata: tes interop dua-node ujung-ke-ujung pada perangkat fisik BLE / Wi-Fi Direct (tes simulasi lulus; diperlukan sesi lab perangkat keras)
- NearLink: `harmonyos/teal/` lengkap; memerlukan perangkat keras Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silikon NearLink tidak hadir pada perangkat non-Huawei). Windows + Android mundur ke perkiraan SSAP-over-BLE secara otomatis.
- LoRa / CircleLink: modul radio diperlukan untuk jangkauan LoRa sejati. Tanpanya, format kabel Meshtastic dibawa melalui BLE LR (~1,3 km) dan federasi node-jembatan dengan perangkat keras LoRa nyata tersedia.
- ✅ **(TERSELESAIKAN v1.2.0)** Permukaan protokol konsumen (Wave 16/17) — event `IDtnService.BundleReceived` untuk bundel masuk ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), direktori penamaan/penemuan lapisan-aplikasi ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), antarmuka tip-penulis ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Ketiganya dikirimkan secara aditif di 8 bahasa dengan fixture lintas-bahasa yang setara-byte. Lihat CHANGELOG.

**Belum terbuka untuk kontribusi eksternal:**
- Protokol masih dalam pengembangan aktif. Kontribusi eksternal tidak diterima saat ini.
- Implementasi transport NearLink, contoh integrasi Android/iOS, backend transport tambahan, tolok ukur kinerja, dan fuzzing protokol dilacak secara internal dan akan dibuka ketika proyek mencapai titik kontribusi publik yang stabil.

## Struktur Proyek

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

## Menambahkan Transport Baru

Implementasikan `ITransportService`:

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

Daftarkan di DI dan `TransportManager` akan secara otomatis menyertakannya dalam pemilihan transport, diurutkan berdasarkan biaya daya.

## Bagaimana Perbandingannya

| Protokol | Keterbatasan | Keunggulan Aether |
|----------|-----------|-----------------|
| **Briar** | Hanya-Android, bergantung-Tor | Lintas-platform, mesh murni |
| **Meshtastic** | Hanya LoRa (maks 30 kbps) | Multi-transport (BLE + WiFi + NearLink), mampu suara dan streaming |
| **Reticulum** | Python, komunitas kecil | 8 bahasa, kompatibel-kabel di seluruhnya |
| **libp2p** | Mengasumsikan tulang punggung internet | Luring-dulu, bekerja tanpa infrastruktur apa pun |
| **Yggdrasil** | Jaringan overlay, perlu internet | Mesh lapisan-fisik, bekerja tanpa internet |
| **Signal** | Tanpa mesh, memerlukan internet | Bekerja luring, P2P, relay mesh, enkripsi E2E yang sama |

## Pertanyaan yang sering diajukan

**Apakah AetherNet bekerja tanpa internet?**
Ya — ia mengutamakan luring. Perangkat berbicara langsung melalui Bluetooth, Wi-Fi Direct, NearLink, atau LoRa dan merelai pesan lompatan-demi-lompatan melalui perangkat lain, tanpa memerlukan koneksi internet, menara seluler, atau server. Ketika tidak ada rute langsung, pesan ditahan (store-and-forward yang toleran-tunda) hingga 72 jam sampai satu rute terbuka.

**Apakah ia terenkripsi ujung-ke-ujung?**
Ya. AetherNet menggunakan Signal Protocol (kesepakatan kunci X3DH plus Double Ratchet di atas X25519) untuk enkripsi ujung-ke-ujung, AES-256-GCM untuk payload pesan, dan tanda tangan Ed25519 pada setiap paket. Perangkat yang merelai sebuah pesan tidak dapat membacanya.

**Transport apa yang digunakannya?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), radio serial LoRa/CircleLink, relay HTTP/QUIC, dan WebRTC untuk peer-to-peer internet langsung. Protokol secara otomatis memilih transport tersedia berdaya-terendah per paket dan mundur ke berikutnya.

**Dalam bahasa pemrograman apa saja ia tersedia?**
Delapan — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, dan C. Setiap implementasi menghasilkan paket kabel yang identik byte, ditegakkan oleh korpus fixture lintas-bahasa bersama di CI, sehingga sebuah paket yang dibangun oleh satu bahasa didekode tanpa perubahan oleh bahasa lain mana pun.

**Apa bedanya dengan Meshtastic, Briar, atau Bridgefy?**
Meshtastic hanya-LoRa; AetherNet adalah multi-transport (Bluetooth + Wi-Fi + NearLink + LoRa) dan membawa suara, video, dan streaming selain pesan. Briar hanya-Android dan merutekan lewat Tor; AetherNet lintas-platform dan mesh murni. Tidak seperti SDK tertutup, AetherNet berlisensi MIT dan diimplementasikan secara terbuka dalam delapan bahasa. Tabel perbandingan di atas memuat detailnya.

**Apakah ia siap-produksi?**
Lapisan protokol — format kabel, keamanan Signal, perutean, DTN store-and-forward, dan rangkaian layanan penuh — diimplementasikan dan diuji di seluruh kedelapan bahasa. Transport radio nyata di tempat kode platform ada (Bluetooth dan Wi-Fi di Windows dan Android, WebRTC di mana-mana) dan belum-terverifikasi-lapangan di tempat lain menunggu bring-up perangkat keras, yang dilacak secara jujur di `OPEN_ISSUES.md`. Baca catatan status di setiap bagian sebelum menyebarkan.

**Di bawah lisensi apa ia berada?**
MIT — gratis untuk penggunaan komersial dan sumber-terbuka. Lihat [LICENSE](LICENSE).

**Siapa yang membangun AetherNet?**
Ia dikembangkan sebagai protokol terbuka di balik ekosistem mesh The Geek Network, dibangun di Afrika Selatan untuk komunikasi yang bekerja dengan atau tanpa data seluler.

## Titik Ekstensi

Protokol bekerja mandiri. Antarmuka ini memungkinkan Anda menyambungkan backend Anda sendiri bila menginginkannya:

- `IAetherNetIncentiveProvider` — beri imbalan node yang merelai lalu lintas (default no-op: relai altruistik)
- `IAetherNetBackendClient` — sinkronisasi dengan server ketika internet tersedia (default no-op: sepenuhnya luring)
- `IAetherNetFeatureFlagProvider` — alihkan fitur protokol saat runtime (default no-op: semuanya diaktifkan)

Ketiganya dikirimkan dengan implementasi no-op. Hapus mereka dan tidak ada yang rusak.

## Berkontribusi

Kontribusi eksternal belum terbuka. Proyek masih dalam pengembangan aktif. Periksa kembali ketika kami mengumumkan jendela kontribusi publik.

## Keamanan

Lihat [SECURITY.md](SECURITY.md) untuk kebijakan pengungkapan yang bertanggung jawab.

## Lisensi

Lisensi MIT. Lihat [LICENSE](LICENSE).

## Terjemahan

README ini juga dipelihara dalam bahasa-bahasa lain yang tercantum di bilah bahasa di bagian atas file ini, di bawah [`docs/i18n/`](docs/i18n/) — mencakup bahasa Eropa, Asia Timur, Timur Tengah, Asia Selatan, Asia Tenggara, dan Afrika, karena sebuah jaringan yang dibangun untuk orang-orang tanpa data seharusnya tidak memiliki pintu depan yang hanya bisa dibaca oleh mereka yang terhubung dengan baik. **Versi bahasa Inggris adalah sumber kebenaran**: di mana sebuah terjemahan dan teks bahasa Inggris tidak sepakat, teks bahasa Inggris yang berwenang, dan terjemahan mungkin tertinggal darinya satu atau dua rilis. Protokol, kode, fixture, dan perilaku yang dijelaskan adalah identik apa pun bahasa yang Anda baca.
