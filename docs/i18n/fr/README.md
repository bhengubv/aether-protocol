# AetherNet — protocole de réseau maillé « hors-ligne d'abord »

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet est un protocole de réseau maillé open source, sous licence MIT** pour envoyer des messages, des fichiers, de la voix et de la vidéo à des personnes à proximité — avec **aucun internet, aucun serveur et aucune inscription**. Les appareils se connectent directement via Bluetooth, Wi-Fi Direct, NearLink et LoRa ; quand le destinataire est hors de portée, les messages transitent par d'autres appareils et attendent jusqu'à 72 heures qu'une route se libère. Il livre des **implémentations identiques octet par octet dans huit langages de programmation** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift et C.

Partagez des fichiers, des messages et des flux avec des personnes à proximité. Sans Wi-Fi. Sans données mobiles. Sans inscription. Comme AirDrop, sauf que ça fonctionne avec tout le monde, sur toutes les plateformes.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](../../../README.md) · [Français](README.md) · [Español](../es/README.md) · [العربية](../ar/README.md) · [中文简体](../zh-CN/README.md) · [日本語](../ja/README.md) · [Deutsch](../de/README.md) · [Português (BR)](../pt-BR/README.md) · [Русский](../ru/README.md) · [فارسی](../fa/README.md) · [한국어](../ko/README.md) · [isiZulu](../zu/README.md) · [Afrikaans](../af/README.md) · [Sesotho](../st/README.md) · [Kiswahili](../sw/README.md) · [Hausa](../ha/README.md) · [አማርኛ](../am/README.md) · [हिन्दी](../hi/README.md) · [Bahasa Indonesia](../id/README.md) · [বাংলা](../bn/README.md) · [اردو](../ur/README.md)

> **Un protocole, huit langages, identiques sur le fil.** Aether est implémenté en **C#, Rust, TypeScript, Python, Go, Kotlin, Swift et C** — et chaque paquet est identique octet par octet entre tous, garanti par un corpus de fixtures cross-langages partagé auquel chaque implémentation doit correspondre, octet par octet. Construisez votre nœud dans n'importe lequel des huit ; il interopère avec tous les autres. Ce README est également disponible en 20 langues humaines (liens ci-dessus).

## En termes simples

**AetherNet permet aux téléphones et aux ordinateurs portables de communiquer directement entre eux — sans internet, sans opérateur téléphonique et sans compte.** Si les personnes autour de vous ont l'application, vous pouvez leur envoyer des messages, des photos et des fichiers volumineux, passer des appels voix et vidéo, et partager un flux en direct, en utilisant uniquement les radios à courte portée déjà présentes dans chaque téléphone (Bluetooth et Wi-Fi). Si quelqu'un est trop loin pour être joint directement, votre message passe discrètement d'un téléphone au suivant jusqu'à ce qu'il arrive — et attend jusqu'à trois jours qu'un chemin se libère s'il le faut. Il peut même atteindre les grands réseaux publics de partage de fichiers du monde (la même technologie derrière les téléchargements légaux comme Linux et les mises à jour de jeux), récupérer un fichier, et l'acheminer vers un ami qui n'a aucun accès internet.

Tout est chiffré de bout en bout, de sorte que seule la personne à qui vous parlez peut le lire — les téléphones qui le transmettent ne le peuvent pas. C'est **libre et ouvert**, pour que quiconque puisse l'utiliser ou l'inspecter, et c'est écrit huit fois, dans huit langages de programmation, pour pouvoir fonctionner sur presque n'importe quel appareil.

**Quel est le degré d'avancement ?** Les « cerveaux » du réseau — les formats de message, le chiffrement, le routage et le partage de fichiers — sont construits et vérifiés par machine dans les huit langages. Ce qui nécessite encore des tests en conditions réelles, ce sont les radios elles-mêmes qui communiquent entre elles par les ondes entre deux téléphones physiques ; cette étape matérielle est ce qui reste, et nous la suivons ouvertement dans `OPEN_ISSUES.md`. Tout ce qui suit raconte la même histoire plus en détail.

## Qu'est-ce que vous pouvez faire avec ?

**Partager des notes de cours sans consommer de données.**

Vous êtes dans un groupe d'étude. Quelqu'un a des annales sur son téléphone. Aether les envoie directement sur votre appareil via Bluetooth — pas de point d'accès, pas de groupe WhatsApp, pas de limite de taille de fichier. Si quelqu'un dans le groupe est hors de portée, le fichier transite par d'autres appareils jusqu'à l'atteindre. Les messages peuvent attendre jusqu'à 72 heures qu'une route se libère si nécessaire.

```
  [Vous] ──BLE──▶ [Ami] ──WiFi──▶ [Ami d'un ami]
    notes.pdf           relayé, chiffré
```

**Découvrez ce qui se passe autour de vous.**

Vous êtes à un événement sur un campus ou à un festival. Aether découvre les autres appareils à proximité via Bluetooth et Wi-Fi Direct — pas de fil d'actualité, pas d'algorithme. Vous voyez ce qui se trouve réellement autour de vous, pas ce qui est promu.

**Envoyez un SOS quand il n'y a pas de signal.**

Votre téléphone n'a pas de réception. Aether diffuse un message d'urgence à tous les appareils à portée, et ces appareils le transmettent à leur tour. Aucune tour cellulaire n'est nécessaire.

```
          ╭── [Téléphone B]
         ╱
  [SOS!] ───── [Téléphone C] ──── [Téléphone E]
         ╲
          ╰── [Téléphone D]

  Diffusion : atteint tous les appareils à portée
```

**Créez des canaux de groupe privés.**

Un canal pour votre étage de résidence, votre association, votre équipe de projet. Seuls les membres vérifiés peuvent lire ou envoyer des messages. Aucun serveur ne stocke la conversation.

**Vendez des choses aux personnes à proximité.**

Mettez un manuel en vente. Les personnes passant à portée du maillage le voient. Pas de compte sur une marketplace, pas de frais d'annonce — juste la proximité.

**Regardez un film ensemble, à travers le maillage.**

Votre groupe organise une soirée cinéma. Quelqu'un a le fichier. Aether synchronise la lecture sur chaque appareil — play, pause, avance — tous en parfaite synchronisation. Si seules certaines personnes ont le fichier, le maillage le distribue en temps réel sous forme de flux P2P. Tout le monde participe via SDPKT pour l'acheter si personne ne l'a.

**Obtenez un gros fichier de la même manière que tout l'internet les partage déjà.**

BitTorrent est la technologie derrière une énorme partie du partage de fichiers légal dans le monde — les versions de Linux, les mises à jour de jeux, l'Internet Archive. Aether le parle désormais *pour de vrai* : un nœud Aether peut rejoindre un essaim BitTorrent ordinaire et récupérer un fichier directement auprès de la foule, sans serveur central. Et voici l'astuce pour les personnes sans données — un nœud Aether qui, lui, *a* internet peut récupérer un torrent et **le repartager à travers le maillage hors-ligne**, de sorte qu'un ami complètement hors-ligne reçoit tout de même le fichier, de proche en proche, via Bluetooth et Wi-Fi. Le plus grand réseau de partage de fichiers au monde, atteignant les personnes que l'internet n'atteint pas.

## Comment ça fonctionne

Les appareils communiquent directement entre eux via Bluetooth, Wi-Fi Direct ou NearLink. Pas de connexion internet, pas de serveur, pas d'infrastructure centralisée.

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ Chiffré de bout en bout (Signal Protocol) ======>|
       |                                                             |
       |  Pas d'internet. Pas de serveurs. Pas de FAI. Juste des appareils qui communiquent.     |
```

Quand un message ne peut pas atteindre sa destination directement, il transite par d'autres appareils. Ces appareils relais ne peuvent pas lire ce qu'ils transportent — chaque message est chiffré avec AES-256-GCM. Chaque paquet est signé avec des clés d'identité Ed25519, et les paquets falsifiés sont rejetés par le réseau.

> **Note sur la maturité sécuritaire (à lire avant de déployer) :** Le vrai X3DH (4 DH X25519), le Double Ratchet Signal complet (étape de rotation DH à la réception, KDF_RK, ratchet de chaîne 0x01/0x02), et le pool de clés pré-partagées à usage unique (100 OPK par défaut, FIFO, protégé par verrou) sont implémentés dans **les 8 langages** et ancrés à un corpus de fixtures cross-langages partagé sous `fixtures/signal/`. Le seul point ouvert restant est la mise en service RF physique sur du matériel BLE réel (suivi dans `OPEN_ISSUES.md`).

Pas de comptes, pas de numéros de téléphone, pas d'e-mails. Vous générez une paire de clés et vous êtes sur le réseau.

```
  ┌─────────────────────────────────┐
  │         Votre Application       │
  ├─────────────────────────────────┤
  │ Messagerie · Streaming · Voix   │
  │ Vidéo · Regarder Ensemble       │
  ├─────────────────────────────────┤
  │  Sécurité : AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
  ├─────────────────────────────────┤
  │  Routage : AODV + DTN           │
  ├─────────────────────────────────┤
  │  Transport : BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**Routage** — AODV avec réponses de route signées. Chaque réponse de route est signée par la clé Ed25519 de la destination, de sorte qu'aucun appareil ne peut se faire passer pour une destination qu'il n'est pas.

**Stockage et transmission différée** — Quand il n'y a pas de route active, les paquets sont conservés jusqu'à 72 heures jusqu'à ce qu'un chemin s'ouvre.

**Sélection du transport** — Le protocole choisit le transport approprié par paquet. Les petits messages de contrôle passent par BLE. Les transferts volumineux utilisent Wi-Fi Direct. NearLink quand disponible.

**Voix, vidéo et streaming** — Appels vidéo avec négociation de codec (H.264/H.265/VP8), sélection de qualité selon le transport, vidéo de groupe avec relais SFU automatique, visionnage synchronisé avec compensation RTT, et streaming à débit adaptatif.

**Protection contre la relecture** — Déduplication des nonces avec une fenêtre de fraîcheur d'horodatage de 5 minutes.

<a name="what-you-get"></a>

## Ce que vous obtenez — chaque service, dans chaque langage

Aether n'est pas seulement un transport. Chaque type de paquet réservé par le protocole est désormais un **service réel et fonctionnel dans les 8 langages**, et chacun se sérialise en **paquets fil identiques octet par octet** — un paquet construit par le nœud Go est décodé, sans modification, par le nœud Swift, Rust, C, Python, TypeScript, Kotlin ou C#. Chaque service est ancré à une fixture cross-langages partagée sous `fixtures/<service>/` et exercé par des tests unitaires par langage, Swift et C étant en plus vérifiés sur le serveur de build macOS.

| Capacité | Ce que ça fait | Type(s) de paquet | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Balise de présence & requête** | Annoncer « je suis là » et demander « qui est aux alentours ? » — via un **ID éphémère rotatif dérivé d'une clé** (pas votre véritable identité) plus un geohash grossier | 21, 22 | `fixtures/presence/` | ✅ |
| **Battement de cœur** | Maintien de connexion léger entre pairs liés | 10 | `fixtures/heartbeat/` | ✅ |
| **Synchronisation de profil** | Échanger une carte de profil signée avec un pair via le maillage | 23 | `fixtures/profiles/` | ✅ |
| **Annonce d'ID éphémère** | Communiquer en privé à un ami votre ID de routage rotatif actuel pour qu'il puisse toujours vous joindre après sa rotation | 56 | `fixtures/erid/` | ✅ |
| **Échange de pré-clés** | Demander et livrer un bundle de pré-clés Signal via le maillage, pour amorcer une session de bout en bout avec quelqu'un que vous n'avez jamais rencontré | 25, 26 | `fixtures/prekey/` | ✅ |
| **Canaux** | Messages signés vers un canal de groupe privé réservé aux membres | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Trames vocales talkie-walkie (charge audio encodée opaque) | 15 | `fixtures/media/` | ✅ |
| **Partage d'écran** | Trames vidéo de partage d'écran (charge vidéo encodée opaque) | 32 | `fixtures/media/` | ✅ |
| **Contrôle d'appel** | Signalisation sonnerie / accepter / refuser / raccrocher pour les appels voix et vidéo | 27 | `fixtures/videocall/` | ✅ |
| **Accusé de réception SOS** | Confirmer à l'expéditeur que sa diffusion d'urgence a été reçue | 6 | `fixtures/sos/` | ✅ |
| **Miettes de proximité** | Miettes de découverte étiquetées par localisation pour la couche « ce qui m'entoure » | 40 | `fixtures/space/` | ✅ |
| **Annonce de forge** | Annoncer un artefact de contenu dérivé/forgé au maillage | 41 | `fixtures/forge/` | ✅ |
| **Requête de fragment de coffre** | Récupérer un fragment de stockage à code d'effacement (K fragments quelconques parmi N reconstruisent le fichier) | 42 | `fixtures/vaultshard/` | ✅ |
| **Mesure de bande passante** | Sonder / accuser / propager le débit d'un lien pour que le maillage route par le tuyau le plus large (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

Ceux-ci reposent sur les services déjà complets de **messagerie, voix 1-à-1 et de groupe, appels vidéo, streaming en direct, regarder ensemble, routage AODV, stockage-et-transmission différée DTN et diffusion SOS** — également implémentés dans les 8 langages.

> **Ce que « construit » signifie ici, précisément.** Chaque service produit et gère son paquet fil, lève les bons événements, et est ancré à une fixture au niveau octet à laquelle toute la famille de langages doit correspondre. Votre application relie le service à sa session Signal, sa table de routage et son état local. C'est la couche protocole — prouvée en code, en tests et en fixtures octet cross-langages — sur la même base RF honnête que tout le reste : tout chemin qui finit par emprunter une radio reste non vérifié sur le terrain jusqu'à la mise en service matérielle suivie dans `OPEN_ISSUES.md`.

<a name="bittorrent-bridge"></a>

## BitTorrent — réel, et relié au maillage

Aether inclut désormais une **véritable implémentation BitTorrent interopérable** — le vrai protocole qu'utilisent les vrais clients torrent, pas un sosie. Ainsi, un nœud Aether peut rejoindre un essaim normal et échanger des morceaux d'un fichier avec des inconnus sur internet, sans serveur au milieu.

Nous n'avons pas seulement affirmé que c'est réel — nous l'avons prouvé. Aether a été vérifié par rapport à **MonoTorrent**, une bibliothèque BitTorrent mature et indépendante construite par d'autres personnes : pour un même fichier, les deux produisent l'empreinte *identique*, de sorte que n'importe quel vrai client torrent traite Aether comme l'un des siens. Quiconque peut pointer un vrai client BitTorrent dessus et le constater par lui-même.

En plus de cela, Aether ajoute un **pont** : un nœud disposant d'internet peut récupérer un torrent sur le web au sens large, ré-empaqueter ses morceaux sous forme de fragments de maillage chiffrés propres à Aether, et le partager plus loin — de sorte que quelqu'un **sans aucun accès internet** peut tout de même recevoir ce fichier à travers le maillage hors-ligne. C'est tout l'intérêt : brancher le plus grand réseau de partage de fichiers au monde sur les personnes qu'il ne peut normalement pas atteindre.

**Où en est-on, honnêtement.** Les *formats* BitTorrent — comment un torrent est décrit, doté d'une empreinte et mis en trame sur le fil — sont construits et **identiques octet par octet dans les 8 langages**, ancrés à un corpus de fixtures partagé dans `fixtures/bittorrent/`. Le client complet fonctionnel et le pont vers le maillage sont achevés et vérifiés dans la **référence C#** ; les sept autres langages portent les mêmes formats de protocole, leur couche réseau active constituant l'étape suivante.

> **Pour les développeurs.** Couverture : bencode + `.torrent`/magnet + info-hash SHA-1 et peer-wire BEP-3 (le plus rare d'abord), trackers HTTP + UDP (BEP-3/15/23), Mainline DHT + PEX + ut_metadata (BEP-5/11/9/10), µTP (BEP-29), et merkle SHA-256 BitTorrent v2 (BEP-52), plus une **passerelle** morceau↔fragment vers le service de contenu et un téléchargeur segmenté concurrent et reprenable. La référence C# (`src/AetherNet.BitTorrent`, `src/AetherNet.BitTorrent.Gateway`) livre le client TCP/µTP actif, le nœud DHT, les trackers, la passerelle et le téléchargeur, avec le test d'interopérabilité MonoTorrent dans `tests/AetherNet.BitTorrent.Interop.Tests`. Le corpus d'identité-octet à 8 langages (`fixtures/bittorrent/vectors.json`, 7 catégories) couvre bencode, info-hash, peer-wire, µTP, merkle, compact-info, et KRPC ; chaque SDK livre un test de fixture correspondant.

## Sécurité & confidentialité

Au-delà de la suite de services fil, Aether livre une petite **couche de sécurité & confidentialité** — gestion des clés d'identité et anti-pistage au niveau liaison. Comme tout le reste, chacune est implémentée dans **les 8 langages** et ancrée à une fixture cross-langages partagée sous `fixtures/<feature>/` (Swift et C en plus vérifiés sur le serveur de build macOS). Ce ne sont *pas* quatre services fil de plus parmi les 18 : trois ne définissent **aucun nouveau type de paquet fil**, et le quatrième transporte ses propres enveloppes **à l'intérieur du chemin DTN/maillage existant** plutôt que comme un nouveau paquet réservé.

| Capacité | Ce que ça fait | Couche | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Sauvegarde par phrase de récupération** | Sauvegarder une identité sous forme de phrase **BIP-39 de 24 mots** et la restaurer sur n'importe quel appareil. BIP-39 standard (vérifié par rapport aux vecteurs Trezor officiels), avec somme de contrôle SHA-256 de sorte qu'un mot mal saisi est *rejeté*, jamais silencieusement erroné. Pas de serveur, pas de dépositaire — la phrase **est** l'identité. | locale | `fixtures/bip39/` | ✅ |
| **Protection anti-pistage Bluetooth** | Dérive un **UUID de service** BLE rotatif dérivé d'une clé (HMAC-SHA256, fenêtre de 15 minutes) et des **adresses privées résolvables** (IRK + la fonction RFC `ah`, AES-128) — le matériel anti-pistage dont un émetteur BLE a besoin pour qu'un scanner passif ne puisse pas le relier à travers le temps ou l'espace. | liaison | `fixtures/bleprivacy/` | ✅ |
| **Effacement de panique** | Un **PIN de contrainte** (SHA-256, comparé en temps constant) qui, sous la contrainte, efface de façon sécurisée chaque clé d'identité — écrasement par de l'aléatoire puis mise à zéro — ne laissant rien à récupérer. | locale | `fixtures/panicwipe/` | ✅ |
| **Synchronisation multi-appareils** | Synchronisation **décentralisée, sans serveur** entre vos *propres* appareils : un **DeviceLink** signé Ed25519 les appaire, et des enveloppes **SyncRecord** en dernier-écrit-gagne réconcilient l'état — transportées chiffrées de bout en bout sur le DTN/maillage existant, sans compte cloud ni serveur de synchronisation. | sur DTN | `fixtures/sync/` | ✅ |

**Une asymétrie honnête.** Le `DeviceLink` multi-appareils est signé Ed25519, et cette signature est **identique octet par octet dans 7 des 8 langages**. La CryptoKit d'Apple *randomise* délibérément les signatures Ed25519, donc sur Swift les 64 octets de signature diffèrent à chaque fois — mais le **corps signé est identique octet par octet** et chaque lien se vérifie toujours sur les 8 SDK, de sorte que Swift atteint la parité de **vérification** plutôt que la parité octet de signature. C'est une propriété de la cryptographie de la plateforme, pas un défaut, et c'est le seul endroit parmi ces quatre fonctionnalités où « identique octet par octet » porte un astérisque. Les formats fil complets sont dans [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §12 ; le modèle de menace est dans [`THREAT_MODEL.md`](THREAT_MODEL.md).

## Transports

Chaque transport a un nom de couleur utilisé dans tout le code source. `IsAvailable` bloque les chemins matériellement indisponibles — le `TransportManager` les ignore automatiquement et bascule sur le transport disponible suivant.

**Clé de statut :** ✅ réel, construit & vérifié · ⏳ réel, vérification en cours · ⚠️ réel sur certaines plateformes, stub sur d'autres · ❌ stub (pas encore de code de transport).

| Couleur | Nom | Portée | Bande passante | Statut |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Réel — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Réel — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | Relais HTTP / QUIC | Illimité | ~10 Mbps | ✅ Réel — Windows ; serveur relais dans `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Canal de données internet | Illimité | ~100 Mbps | ✅ Réel dans les 8 langages — **vérifié en loopback dans les 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust ont chacun deux pairs qui échangent des octets sur un vrai canal de données ICE) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Réel sur Android (`android/white/`) ; Windows = vrai BLE-GATT + approximation de proximité RSSI −40 dBm (`WinNfcBleTransportService`, compile net9/10, non vérifié à l'exécution) — `Windows.Networking.Proximity` supprimé dans Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Réel sur HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — en attente de vérification sur appareil) ; Android + Windows = vraie approximation SSAP-sur-BLE (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService` ; compilation + test unitaire vérifiés, non vérifié à l'exécution) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Vrai pilote série RYLR SX127x/SX126x (`LoRaSerialTransport` en C#/Go/Rust/C ; compile, non vérifié à l'exécution — nécessite un module physique) ; le pont BLE Coded-PHY reste une conception documentée |

Les transports radio ne sont réels que là où du code de plateforme existe (C#/Windows, Kotlin/Android, HarmonyOS). Les huit bibliothèques de langages livrent sinon un transport de **simulation en cours de processus** pour les tests — **WebRTC est le premier vrai transport commun à toutes** (complet ; vérifié en loopback dans tous les langages).

La priorité est fonction du coût énergétique : le maillage radio est privilégié, puis WebRTC comme chemin internet direct, avec le relais HTTP/QUIC en dernier recours.

## Niveaux de déploiement

Aether fonctionne sur toute plateforme supportant Bluetooth ou Wi-Fi. Le niveau auquel vous vous trouvez dépend du système d'exploitation ciblé.

---

### Niveau standard — toute plateforme

Android · Windows · Linux · macOS · iOS

Aether s'exécute sur tout appareil disposant de matériel Bluetooth ou Wi-Fi. Là où une radio est physiquement absente, chaque transport bloqué est approximé à l'aide de ce qui est disponible. Ces approximations sont désormais du **vrai code** (compilation vérifiée ; **non vérifié à l'exécution** en attente d'un test RF sur 2 appareils / matériel) :

- **NearLink (Aether Teal)** — vraie approximation SSAP-sur-BLE-GATT (UUID SLE Aether `61657468-6572-0003-…`) sur Android (`android/teal/AetherNetSleService`) et Windows (`WinNearLinkBleTransportService`) ; compilation + test unitaire vérifiés, non vérifié à l'exécution. La vraie radio NearLink n'existe que sur HarmonyOS (`harmonyos/teal/`, en attente de vérification sur appareil).
- **LoRa (Aether Red)** — vrai pilote série RYLR SX127x/SX126x (`LoRaSerialTransport` dans **les 8 langages** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin ; chaque portage compilation-vérifié, y compris Swift + C sur le serveur de build Mac ; non vérifié à l'exécution — nécessite un module physique). Le pont Meshtastic-sur-BLE-Coded-PHY (~1,3 km) reste une conception documentée ; le vrai LoRa longue portée nécessite un nœud compatible LoRa (passerelle, SBC, ou terminal durci avec module LoRa).
- **NFC (Aether White)** — réel sur Android (HCE). Windows a désormais une vraie approximation de proximité BLE-GATT + RSSI −40 dBm (`WinNfcBleTransportService`, compile net9/10 ; non vérifié à l'exécution) ; ACR122U PC/SC quand un lecteur est présent.

Ce qui est réel et identique partout : **BLE, Wi-Fi Direct, le relais HTTP/QUIC, et le transport WebRTC P2P (vérifié en loopback dans les 8 langages)**, plus la sécurité Signal Protocol (X3DH + Double Ratchet), le routage AODV, le stockage-et-transmission différée DTN, la diffusion SOS, la voix et le streaming.

**Statut honnête :** BLE + Wi-Fi Direct + relais sont réels et de qualité production ; **WebRTC P2P est réel et vérifié en loopback dans les 8 langages** (deux pairs échangent des octets sur un vrai canal de données ICE — Rust confirmé sur la machine Linux `.201` avec ICE UDP fonctionnel) ; les approximations NearLink / LoRa / NFC-sur-Windows sont désormais du vrai code qui compile (LoRa compilation-vérifié dans les 8, y compris Swift + C sur le serveur de build Mac ; NearLink-Android également testé unitairement) mais est **non vérifié à l'exécution** — pas encore de test RF matériel / 2 appareils. Ils participent au maillage en code ; ne déployez pas ces trois-là en attendant une RF éprouvée sur le terrain.

---

### Niveau natif — CircleOS / OpenHarmony

CircleOS · HarmonyOS · tout OS basé sur OpenHarmony

CircleOS est construit sur OpenHarmony, qui embarque du silicium NearLink (SLE) et le SDK `@kit.NearLinkKit` comme capacité OS de première classe. Sur les appareils CircleOS et HarmonyOS dotés de matériel NearLink, aucune approximation n'est nécessaire — `harmonyos/teal/` utilise directement la vraie radio SLE :

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

Ce n'est pas simplement une meilleure version du niveau standard. Au niveau NearLink, c'est un réseau catégoriquement différent :

| Capacité | Niveau standard (approximation BLE) | Niveau natif (CircleOS / OpenHarmony) |
|---|---|---|
| **Portée NearLink** | ~100 m (BLE) | **600 m** |
| **Bande passante NearLink** | ~1 Mbps (BLE) | **12 Mbps** |
| **Latence NearLink** | ~10 ms (BLE) | **20 µs** |
| **Consommation NearLink** | Référence BLE | **60% de moins que BLE 5.0** |
| **Pairs NearLink simultanés** | ~7 (limite de connexion BLE) | **500+** |
| **Source NearLink** | SSAP-sur-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Vraie radio SLE (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / Relais HTTP** | Natif | Natif (identique) |
| **Sécurité Signal Protocol** | Complète | Complète (identique) |
| **Routage / DTN / SOS** | Complet | Complet (identique) |
| **Identité Aether Tag** | Supportée | Supportée (identique) |

---

### Passage entre niveaux

Aucun changement de code n'est nécessaire. Le niveau est déterminé à l'exécution par `IsAvailable` sur chaque service de transport :

1. Sur un appareil CircleOS ou HarmonyOS avec du silicium NearLink, `IsAvailable` sur le transport NearLink retourne `true` (sondage matériel via vérification des permissions + tentative de scan passif).
2. `TransportManager` promeut automatiquement NearLink en position prioritaire — coût énergétique le plus bas, bande passante la plus haute.
3. Le code applicatif, le format fil, l'algorithme de routage, la couche de sécurité et les Aether Tags sont identiques sur les deux niveaux.

Un nœud du niveau standard et un nœud du niveau natif peuvent communiquer librement — ils partagent le même format fil, les mêmes sessions Signal Protocol, et les mêmes Aether Tags. La différence de niveau affecte uniquement la radio utilisée pour les paquets NearLink, pas le protocole au-dessus.

---

> **En interne, ces niveaux sont appelés la variante Asterix (standard) et la variante Obelix (natif).** Asterix fonctionne bien avec ce qui est disponible. Obelix — tournant sur CircleOS avec NearLink natif — opère à une capacité durablement élevée, à la manière dont Obelix porte la force de la potion magique sans avoir besoin d'en boire à nouveau.

---

## Implémentations

Aether est construit en 8 langages pour fonctionner sur les téléphones, ordinateurs portables, tablettes et microcontrôleurs. Toutes les implémentations produisent des paquets compatibles fil — un message chiffré par le nœud Rust peut être relayé par le nœud Python et déchiffré par le nœud Swift.

| Langage | Répertoire | Format fil | Routage/DTN/SOS | X3DH | Double Ratchet | Pool OPK | Voix/Groupe | Streaming/Vidéo/Regarder | BitTorrent |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ | ◐ |

**Colonne BitTorrent :** ✅ = client complet et fonctionnel + passerelle vers le maillage (la référence C#). ◐ = les **formats fil** BitTorrent sont ici identiques octet par octet (ancrés à `fixtures/bittorrent/`), leur couche réseau active constituant l'étape suivante — voir [BitTorrent — réel, et relié au maillage](#bittorrent-bridge). Toutes les autres colonnes sont réelles et fonctionnelles dans les 8 langages.

Les 8 langages produisent des paquets fil identiques octet par octet, vérifiés par rapport à 17 fixtures canoniques de format fil et 6 vecteurs de test Signal (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`) — chaque langage est vérifié par rapport aux mêmes octets. Le routage (RREQ/RREP de style AODV), le stockage-et-transmission différée DTN, la diffusion SOS, la voix, le streaming, et les services de renforcement de la sécurité sont implémentés dans chaque langage avec **~3 000 tests** sur les 8 implémentations :

| Langage | Tests | Plateforme de test |
|----------|------:|-------------|
| C# (.NET 10) | 530 | Linux |
| TypeScript / Node 20 | 459 | Linux |
| Kotlin / JVM 21 | 457 | Linux |
| Go 1.22 | 423 | Linux |
| Python 3.12 | 387 | Linux |
| Swift 6 | 295 | macOS |
| C (GCC) | 253 | Linux |
| Rust (stable) | ~195 | Linux |
| **Total** | **~3 000** | |

L'interopérabilité Signal cross-langages est ancrée à `fixtures/signal/` avec des vecteurs de test partagés pour X3DH (`x3dh_basic`), le ratchet symétrique (`ratchet_step_basic`, `ratchet_step_three_iterations`), KDF_RK (`kdf_rk_basic`), et l'aller-retour complet de session X3DH (`x3dh_session_msg1`, `x3dh_session_reply`). Chaque implémentation doit produire des sorties identiques octet par octet par rapport à ces fixtures. Les 8 langages embarquent désormais une session Signal complète (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

Au-delà du format fil et de Signal, la **suite complète des services fil** — présence, battement de cœur, synchronisation de profil, annonce d'ID éphémère, échange de pré-clés, canaux, push-to-talk, partage d'écran, contrôle d'appel, accusé de réception SOS, miettes de proximité, annonce de forge, requête de fragment de coffre, et mesure de bande passante (voir [**Ce que vous obtenez**](#what-you-get)) — est de même implémentée dans les 8 langages et ancrée à ses propres fixtures (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, et consorts). Aucune fonctionnalité n'est réservée à C# au niveau protocole.

## Démarrage Rapide

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
```

La démo vous guide à travers 8 étapes : génération de clés d'identité Ed25519 pour trois nœuds (Alice, Bob, Charlie), établissement de sessions Signal Protocol, envoi de messages chiffrés, relayage d'un message via Charlie (qui ne peut pas le lire), affichage du format fil binaire, et démonstration de la confidentialité persistante sur 5 messages consécutifs. La sortie est colorée et s'arrête entre les étapes.

**Envoyer un message en C# :**

```csharp
// Établir une session Signal Protocol
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Chiffrer et envoyer
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Créer un paquet signé
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

La démo génère des clés d'identité pour deux nœuds, échange des bundles de pré-clés, établit des sessions chiffrées, envoie des messages chiffrés dans les deux sens, crée et signe des paquets maillage, vérifie les signatures, et sérialise les paquets au format fil binaire. Elle démontre également la couche de transport en cours de processus.

**Envoyer un message en Rust :**

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

La démo crée deux nœuds dans un réseau simulé, génère des clés Ed25519, établit des sessions Signal Protocol, crée et signe un paquet, le sérialise au format binaire compatible C#, chiffre un message secret, le déchiffre sur l'autre nœud, l'envoie via le transport, et vérifie le voyage aller-retour.

**Envoyer un message en TypeScript :**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Échanger le bundle avec le pair
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

La démo exécute 8 démonstrations : génération de clés Ed25519 et détection de falsification, création de nœuds avec capacités, échange de clés X3DH Signal Protocol, chiffrement et déchiffrement AES-256-GCM, sérialisation de paquets, signature de paquets avec détection de relecture, transport en cours de processus, et un flux complet de bout en bout combinant toutes les couches.

**Envoyer un message en Python :**

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

La démo exécute 5 démonstrations : aller-retours de sérialisation de paquets, signature Ed25519 avec détection de falsification, établissement de session Signal Protocol avec messagerie chiffrée dans les deux sens, transport en cours de processus entre deux pairs, et déduplication de nonces pour la protection contre la relecture.

**Envoyer un message en Go :**

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

La démo parcourt 11 étapes : génération de clés, création de nœuds avec capacités, initialisation Signal Protocol, échange de bundles de pré-clés, établissement de session, création et signature de paquets, sérialisation, désérialisation avec vérification de signature, chiffrement de bout en bout avec rotation de clés, détection d'attaque par relecture, et transport en cours de processus.

**Envoyer un message en Kotlin :**

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

La démo exécute 5 tests : aller-retours de sérialisation de paquets, signature Ed25519 avec rejet de falsification, établissement de session Signal Protocol avec chiffrement AES-256-GCM, livraison de messages via transport en cours de processus, et un flux complet de bout en bout où Alice signe un paquet et Bob le vérifie après transport.

**Envoyer un message en Swift :**

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

La démo exécute 7 démonstrations : génération de clés Ed25519, création et signature de paquets, sérialisation au format fil binaire, désérialisation avec vérifications d'intégrité, chiffrement et déchiffrement AES-256-GCM, authentification de message HMAC-SHA256, et dérivation de clés HKDF-SHA256.

**Envoyer un message en C :**

```c
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Signer
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// Sérialiser et envoyer
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// envoyer buffer[0..size-1] via le transport

aethernet_packet_free(packet);
```

## Feuille de Route

Ce qui est construit et ce qui vient ensuite.

**Terminé (vérifié cross-langages, les 8 implémentations) :**
- Format fil : identique octet par octet sur 8 langages, ancré par 17 fixtures canoniques et assertions cross-langages (`fixtures/expected/*.bin`)
- **Workflow GitHub Actions (défini, mais pas la barrière actuelle)** — une matrice à 9 tâches (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS, Rust stable, C/GCC, plus une tâche d'intégrité des fixtures) est définie dans `.github/workflows/ci.yml`. Les commits sont actuellement poussés avec `[skip ci]`, de sorte que la véritable garantie est le corpus de fixtures exécuté **localement, par langage** (Swift et C sur le serveur de build macOS) ; la CI peut être réactivée sans changement de code.
- Signature et vérification de paquets Ed25519
- Chiffrement AES-256-GCM
- Primitives de dérivation de clés HKDF / HMAC
- Sérialisation de paquets + disposition de signature (LE + champs int32 4 octets)
- Simulateur de transport en cours de processus (pour le développement et les tests)
- Service de routage inspiré AODV avec RREQ/RREP, réponses de route signées, déduplication, transfert TTL
- Service de stockage-et-transmission différée DTN avec transfert de garde, réplication géohash, TTL 72h
- Service de diffusion SOS avec inondation, déduplication, garde d'auto-origine, limite de débit (3/h)
- Points d'extensibilité : `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (implémentations Noop par défaut)
- **~3 000 tests** sur les 8 langages (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — tous verts, exécutés par langage (Swift et C sur le serveur de build macOS)
- ✅ **Vraie clé éphémère X3DH (8 langages)** — 4 DH X25519 (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) avec dérivation de racine HKDF-SHA256. Ancré par `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Alignement Double Ratchet à l'échelle de la famille** — Signal §5 complet avec HMAC-SHA256 + séparation de domaine 0x01/0x02 dans le ratchet symétrique, HKDF-SHA256 KDF_RK dans l'étape DH-ratchet, rotation DH à la réception. Vérifié par les fixtures `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic`.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 réconcilié avec HEAD** — voir `docs/PROTOCOL_SPEC.md`.

**Terminé (les 8 langages) :**
- ✅ **Appels vocaux (1-à-1)** — machine d'état de signalisation (Offer/Answer/Hangup/Cancel/Timeout) + transport de trames binaires (16 octets callId · 4 octets seq · 8 octets timestamp · 1 octet isSilence · N octets). Livraison consciente de la route via `IRoutingService`.
- ✅ **Voix de groupe** — adhésion pilotée par l'hôte (inviter/expulser/quitter), champ de génération de clé par trame, distribution unicast à tous les membres actuels, rotation de clé contrôlée par l'hôte lors d'un changement d'adhésion.
- ✅ **Streaming en direct** — l'éditeur diffuse `StreamAnnounce` ; les abonnés envoient `StreamSubscribe` ; trames binaires `StreamSegment` (16 octets streamId · 4 octets seq · 8 octets ts · 1 octet isKeyframe · N octets) en unicast vers chaque abonné.
- ✅ **Appels vidéo (1-à-1)** — négociation de codec/résolution/fps/débit dans la signalisation, signaux de demande d'image clé et de changement de qualité, format binaire `VideoFrame` correspondant à la disposition vocale.
- ✅ **Regarder Ensemble** — l'hôte émet des commandes `WatchSync` autoritatives (lecture/pause/avance/vitesse) ; les suiveurs les appliquent avec compensation RTT (`position = positionMs + elapsed × playbackSpeed`) ; `WatchReaction` en mode fire-and-forget.
- ✅ **Pool de clés pré-partagées à usage unique (OPK)** — 100 par défaut, émission FIFO, rechargement paresseux, consommation protégée par verrou dans les 8 langages. Résout le risque de concurrence sur l'OPK unique.
- ✅ **C : session Signal complète** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` dans `c/src/signal_protocol.c` ; 6 tests E2E à deux nœuds dans `c/tests/test_signal_session.c`. Les 8 langages disposent désormais d'un Signal Protocol complet capable de session.

**Terminé (les 8 langages — la suite complète des services fil) :**
- ✅ **Chaque type de paquet réservé est désormais un service réel et identique octet par octet dans les 8 langages.** Balise/requête de présence (21/22), battement de cœur (10), synchronisation de profil (23), annonce d'ID-de-routage-éphémère (56), échange de pré-clés (25/26), canaux (7), push-to-talk (15), partage d'écran (32), contrôle d'appel (27), accusé de réception SOS (6), miettes de proximité (40), annonce de forge (41), requête de fragment de coffre (42), et mesure de bande passante / ABMF (53/54/55). Chacun est un service léger (produire + gérer + événement) que l'hôte relie à sa session Signal et sa table de routage ; chacun est ancré à une fixture cross-langages partagée (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) et exercé par des tests unitaires par langage, Swift et C étant vérifiés sur le serveur de build macOS. Voir [**Ce que vous obtenez**](#what-you-get).

**Terminé (référence C# uniquement) :**
- ✅ **Démo Étape 9 — MessagingService + DTN fallback de bout en bout** — `samples/AetherNet.Demo.Console` présente la messagerie chiffrée par Signal réel avec stockage-et-transmission différée DTN quand le destinataire est hors ligne.
- ✅ **Pont `AetherNet.Messaging` ↔ `AetherNet.Security`** — `SignalMessageEnvelopeCipher` rend la couche de messagerie chiffrée de bout en bout par défaut ; les messages sans session Signal sont mis en file d'attente, jamais envoyés de façon non sécurisée.
- ✅ **Streaming à débit adaptatif** — `AdaptiveBitrateController` avec échelles de débit mandatées par la spec pour le Profil A (temps réel), B (diffusion en direct), et C (VOD). L'éditeur sélectionne le barreau le plus élevé soutenable (marge de 20%) et émet `StreamAbandon` (`PacketType.StreamAbandon`) au lieu d'un segment quand en dessous du plancher. `IStreamingService` expose `UpdateBandwidthEstimate` et `GetCurrentBitrateRung`.
- ✅ **Regarder Ensemble : ingestion BitTorrent + financement de groupe ChipIn** — modèles `TorrentInfo` / `TorrentFile` ; `WatchTogetherService` gère `PacketType.TorrentMetadata` et déclenche `TorrentReceived`. Machine d'état `ChipInPool` / `ChipInContribution` (Collecte → Financé → Achat → Acquis / Échoué / Remboursé) ; `StartChipInAsync` / `ContributeAsync` / `GetChipIn` sur `IWatchTogetherService`.
- ✅ **Appels vidéo de groupe avec relais SFU automatique** — `GroupVideoService` / `IGroupVideoService`. Topologie FullMesh pour ≤ 3 participants ; basculement automatique vers SFU à `SfuThresholdParticipants` (4) avec réassignation de relais via `GroupVideoSignaling(SfuAssigned)`. Distribution en FullMesh, envoi relais uniquement en mode SFU. Type de paquet de signalisation `GroupVideoSignaling = 35`.
- ✅ **Simulation de transport BLE GATT** — `SimulatedBleGattTransportService` (`IBleTransportService`). Tramage MTU GATT via `BleGattFramer` (1024 o/trame, `[2o count][2o index][payload]`), registre de pairs statique en cours de processus, diffusion publicitaire. Toutes les contraintes `BleMaxPayloadBytes` appliquées.
- ✅ **Simulation de transport Wi-Fi Direct** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Cycle de vie explicite `ConnectAsync`/`DisconnectAsync`, livraison directe de grande charge utile (sans tramage), événements bidirectionnels `PeerConnected`/`PeerDisconnected`.
- ✅ **Simulation de transport NearLink** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). MTU de trame 4096 o, registre de 500 pairs, `ConnectedPeerCount`, `IsAvailable` configurable à l'exécution.
- ✅ **Tests de simulation de mise en service RF** — Tests d'interopérabilité à deux nœuds (`SimulatedTransportTests`) : aller-retour `MeshPacket` BLE + NearLink, transfert de charge utile 64 Ko Wi-Fi Direct. Couche logicielle entièrement vérifiée ; session de laboratoire sur appareil physique nécessaire pour la validation sur matériel.

**Terminé (couche transport C# — tous fail-fast) :**
- ✅ **Vrai transport BLE GATT** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (serveur GATT Android). Test complet de mise en service RF dans `samples/AetherNet.BleRfTest/`.
- ✅ **Vrai transport Wi-Fi Direct** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). Test RF dans `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **Transport relais HTTP (Aether Purple)** — `HttpRelayTransportService` avec long-poll de 10 secondes, `PowerCostRelative = 100`, toujours en dernier recours. Serveur relais dans `samples/AetherNet.RelayServer/` (API minimale ASP.NET Core, port 5200). Test RF dans `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implémente `HostApduService` avec AID `F061657468657200`. `WinNfcStubTransportService` documente deux chemins d'approximation Windows : (1) NDEF-sur-BLE-GATT avec barrière RSSI ≥ −40 dBm (simule la connexion par effleurement sans silicium NFC, `IsAvailable = Bluetooth présent`) ; (2) lecteur USB ACR122U via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = lecteur sans contact énuméré`). Chemin de mise à niveau : implémenter `ITransportService` quand Microsoft livrera une API NFC P2P first-party.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — implémentation ArkTS HarmonyOS 5.0.1 (API 13) complète utilisant `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`) ; `isAvailable` sondé à l'exécution. `WinNearLinkStubTransportService` + `android/teal/` documentent l'approximation SSAP-sur-BLE : BLE GATT avec UUID de service SLE Aether `61657468-6572-0003-0000-000000000000` — compatible API avec SSAP, non compatible fil avec le vrai matériel NearLink. Chemin de mise à niveau : remplacer les appels BLE GATT par des appels SDK `ssapc_*`/`ssaps_*` ; UUIDs et slot `TransportManager` inchangés.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` documentent l'approximation Meshtastic-sur-BLE-LR : format fil Meshtastic complet (en-tête 16 octets + protobuf AES-256-CTR) sur BLE 5.0 Coded PHY S=8 (~1,3 km en extérieur), avec routage à inondation gérée et fenêtre de contention pondérée RSSI. La fédération de nœuds-pont avec du vrai matériel LoRa fonctionne automatiquement (même format de paquet Meshtastic, sans traduction). Chemin de mise à niveau : remplacer la radio BLE LR par un pilote SX1276/SX1278 AT-command ou SPI ; format de paquet et routage inchangés.

**Ouvert — suivi dans `OPEN_ISSUES.md` :**
- Mise en service RF sur matériel réel : test d'interopérabilité à deux nœuds de bout en bout sur des appareils BLE / Wi-Fi Direct physiques (les tests de simulation passent ; session de laboratoire matériel nécessaire)
- NearLink : `harmonyos/teal/` complet ; nécessite du matériel Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 (silicium NearLink absent sur les appareils non-Huawei). Windows + Android basculent automatiquement sur l'approximation SSAP-sur-BLE.
- LoRa / CircleLink : module radio requis pour la vraie portée LoRa. Sans module, le format fil Meshtastic est transporté sur BLE LR (~1,3 km) et la fédération de nœuds-pont avec du vrai matériel LoRa est disponible.
- ✅ **(RÉSOLU v1.2.0)** Surface de protocole consommateur (Wave 16/17) — événement `IDtnService.BundleReceived` pour les bundles entrants ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), annuaire de nommage/découverte au niveau applicatif ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), interface de pourboire à l'auteur ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). Les 3 livrés de façon additive sur 8 langages avec des fixtures cross-langages octet-égales. Voir CHANGELOG.

**Pas encore ouvert aux contributions externes :**
- Le protocole est encore en développement actif. Les contributions externes ne sont pas acceptées pour l'instant.
- L'implémentation du transport NearLink, les exemples d'intégration Android/iOS, les backends de transport additionnels, les benchmarks de performance et le fuzzing de protocole sont suivis en interne et seront ouverts quand le projet atteindra un point de contribution publique stable.

## Structure du Projet

```
aether-protocol/
  src/
    AetherNet.Core/          Modèles de protocole, constantes, sérialisation de paquets
    AetherNet.Security/      Signal Protocol, Ed25519, signature de paquets
    AetherNet.Transport/     Abstractions de transport, NearLink, simulateur en cours de processus
    AetherNet.Messaging/     Gestion des messages et relayage
    AetherNet.Storage/       Persistance du stockage-et-transmission différée DTN
    AetherNet.Streaming/     Streaming à débit adaptatif, modèles et interfaces vidéo
    AetherNet.Voice/         Appels vocaux et voix de groupe
    AetherNet.Content/       Vérification de contenu et transfert fragmenté
  samples/
    AetherNet.Demo.Console/  Démo interactive
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
  rust/                   Implémentation Rust
  typescript/             Implémentation TypeScript
  python/                 Implémentation Python
  go/                     Implémentation Go
  kotlin/                 Implémentation Kotlin/JVM
  swift/                  Implémentation Swift
  c/                      Implémentation C
  docs/
    PROTOCOL_SPEC.md      Spécification de protocole au format RFC
```

## Ajouter un Nouveau Transport

Implémenter `ITransportService` :

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implémenter SendAsync, IsConnected, DataReceived
}
```

Enregistrez-le dans l'injection de dépendances et `TransportManager` l'inclura automatiquement dans la sélection du transport, trié par coût énergétique.

## Comparaison

| Protocole | Limitation | Avantage Aether |
|----------|-----------|-----------------|
| **Briar** | Android uniquement, dépend de Tor | Multi-plateforme, maillage pur |
| **Meshtastic** | LoRa uniquement (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), capable voix et streaming |
| **Reticulum** | Python, petite communauté | 8 langages, compatibles fil entre tous |
| **libp2p** | Suppose une dorsale internet | Hors-ligne en premier, fonctionne sans infrastructure |
| **Yggdrasil** | Réseau superposé, nécessite internet | Maillage de couche physique, fonctionne sans internet |
| **Signal** | Pas de maillage, nécessite internet | Fonctionne hors ligne, P2P, relais maillage, même chiffrement E2E |

## Foire aux questions

**AetherNet fonctionne-t-il sans internet ?**
Oui — il est « hors-ligne d'abord ». Les appareils communiquent directement via Bluetooth, Wi-Fi Direct, NearLink ou LoRa et relaient les messages de proche en proche à travers d'autres appareils, sans aucune connexion internet, tour cellulaire ou serveur requis. Quand aucune route active n'existe, les messages sont conservés (stockage-et-transmission différée tolérant aux délais) jusqu'à 72 heures qu'une route se libère.

**Est-il chiffré de bout en bout ?**
Oui. AetherNet utilise le Signal Protocol (accord de clés X3DH plus le Double Ratchet sur X25519) pour le chiffrement de bout en bout, AES-256-GCM pour les charges utiles des messages, et des signatures Ed25519 sur chaque paquet. Les appareils qui relaient un message ne peuvent pas le lire.

**Quels transports utilise-t-il ?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), une radio série LoRa/CircleLink, un relais HTTP/QUIC, et WebRTC pour le pair-à-pair internet direct. Le protocole sélectionne automatiquement le transport disponible le moins énergivore par paquet et bascule sur le suivant.

**Dans quels langages de programmation est-il disponible ?**
Huit — C#, Rust, TypeScript, Python, Go, Kotlin, Swift et C. Chaque implémentation produit des paquets fil identiques octet par octet, garantis par un corpus de fixtures cross-langages partagé par rapport auquel chaque implémentation est vérifiée, de sorte qu'un paquet construit par un langage est décodé sans modification par n'importe quel autre.

**En quoi diffère-t-il de Meshtastic, Briar ou Bridgefy ?**
Meshtastic est uniquement LoRa ; AetherNet est multi-transport (Bluetooth + Wi-Fi + NearLink + LoRa) et transporte la voix, la vidéo et le streaming en plus des messages. Briar est uniquement Android et route via Tor ; AetherNet est multi-plateforme et maillage pur. Contrairement aux SDK fermés, AetherNet est sous licence MIT et implémenté ouvertement dans huit langages. Le tableau de comparaison ci-dessus donne les détails.

**Est-il prêt pour la production ?**
La couche protocole — format fil, sécurité Signal, routage, stockage-et-transmission différée DTN, et la suite complète des services — est implémentée et testée sur les huit langages. Les transports radio sont réels là où du code de plateforme existe (Bluetooth et Wi-Fi sur Windows et Android, WebRTC partout) et non vérifiés sur le terrain ailleurs en attente de la mise en service matérielle, ce qui est suivi honnêtement dans `OPEN_ISSUES.md`. Lisez les notes de statut de chaque section avant de déployer.

**Sous quelle licence est-il ?**
MIT — libre pour un usage commercial et open source. Voir [LICENSE](LICENSE).

**Qui développe AetherNet ?**
Il est développé comme le protocole ouvert derrière l'écosystème maillé de The Geek Network, construit en Afrique du Sud pour une communication qui fonctionne avec ou sans données mobiles.

## Points d'Extension

Le protocole fonctionne de manière autonome. Ces interfaces vous permettent de brancher votre propre backend si vous en souhaitez un :

- `IAetherNetIncentiveProvider` — récompenser les nœuds qui relayent le trafic (noop par défaut : relayage altruiste)
- `IAetherNetBackendClient` — synchroniser avec un serveur quand internet est disponible (noop par défaut : entièrement hors ligne)
- `IAetherNetFeatureFlagProvider` — activer/désactiver les fonctionnalités du protocole à l'exécution (noop par défaut : tout activé)

Les trois sont livrés avec des implémentations noop. Retirez-les et rien ne se casse.

## Contribution

Les contributions externes ne sont pas encore ouvertes. Le projet est encore en développement actif. Revenez quand nous annoncerons une fenêtre de contribution publique.

## Sécurité

Voir [SECURITY.md](SECURITY.md) pour la politique de divulgation responsable.

## Licence

Licence MIT. Voir [LICENSE](LICENSE).

## Traductions

Ce README est également maintenu dans les autres langues listées dans la barre de langues en haut de ce fichier, sous [`docs/i18n/`](docs/i18n/) — couvrant des langues européennes, est-asiatiques, moyen-orientales, sud-asiatiques, d'Asie du Sud-Est et africaines, parce qu'un réseau conçu pour les personnes sans données ne devrait pas avoir une porte d'entrée que seuls les bien connectés peuvent lire. La **version anglaise est la source de vérité** : lorsqu'une traduction et le texte anglais divergent, le texte anglais fait autorité, et les traductions peuvent accuser un retard d'une version ou deux. Le protocole, le code, les fixtures et le comportement décrits sont identiques quelle que soit la langue que vous lisez.
