# Spécification du protocole de réseau maillé Aether

**Version :** 2.0
**Statut :** Réconcilié avec HEAD (2026-05-05)
**Date :** 2026-03-15 (brouillon initial) ; 2026-05-05 (§2, §4, §10, §11 réconciliés, §3/§9 vérifiés)
**Auteurs :** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **Avis au lecteur.** Les brouillons antérieurs de ce document sont antérieurs
> à l'alignement du format de fil pour 8 langages et au portage familial vers X25519 +
> Signal Double Ratchet. À compter du 2026-05-05, §2 (Format des paquets), §3
> (Routage), §4 (Échange de clés), §9 (DTN) décrivent le protocole implémenté ;
> §10 (Diffusion vidéo) et §11 (Regarder ensemble) décrivent le protocole cible —
> ils sont définis sur le fil et testés par fixture, mais les pipelines codec /
> BitTorrent / ChipIn ne sont pas encore liés à l'échafaudage. La référence C#
> fait autorité partout où ce document et l'implémentation divergent.
>
> - Octets canoniques sur le fil : `fixtures/expected/*.bin` (10 cas nommés)
> - Sérialiseur de référence : `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - Pile Signal de référence : `src/AetherNet.Security/Services/SignalProtocolService.cs`
> - Routage de référence : `src/AetherNet.Core/Routing/RoutingService.cs`
> - DTN de référence : `src/AetherNet.Core/Dtn/DtnService.cs`
> - Preuve d'interopérabilité de fil multi-langage : `fixtures/README.md`
> - Preuve d'interopérabilité Signal multi-langage : `fixtures/signal/README.md`

---

## Table des matières

1. [Résumé](#1-résumé)
2. [Format des paquets](#2-format-des-paquets)
3. [Algorithme de routage](#3-algorithme-de-routage)
4. [Échange de clés](#4-échange-de-clés)
5. [Exigences de la couche de transport](#5-exigences-de-la-couche-de-transport)
6. [Protocole de découverte](#6-protocole-de-découverte)
7. [Modèle de sécurité](#7-modèle-de-sécurité)
8. [Diffusion SOS](#8-diffusion-sos)
9. [Stockage et retransmission DTN](#9-stockage-et-retransmission-dtn)
10. [Diffusion vidéo](#10-diffusion-vidéo)
11. [Regarder ensemble](#11-regarder-ensemble)
12. [Couche de sécurité et de confidentialité](#12-security--privacy-layer)

---

## 1. Résumé

Aether est un protocole de réseau maillé décentralisé conçu pour les environnements où la connectivité internet est intermittente ou absente. Il assure le routage de paquets multi-sauts sur des transports à courte portée hétérogènes (Bluetooth Low Energy, Wi-Fi Direct, NearLink), le chiffrement de bout en bout à l'aide d'un accord de clé dérivé de X3DH avec un cliquet symétrique, une livraison différée par stockage et retransmission, et un mécanisme d'inondation d'urgence SOS. Le protocole est agnostique au transport : toute couche physique capable d'envoyer et de recevoir des tableaux d'octets entre pairs est un transport Aether valide. Les nœuds sont identifiés par des identifiants matériels universels (UHID) et authentifiés via des clés d'identité Ed25519. Aether est conçu comme une couche réseau universelle — chaque application de l'écosystème enregistre des services Aether, et les nœuds sans connectivité internet atteignent le réseau étendu via des pairs passerelles qui relient le trafic maillé à internet.

---

## 2. Format des paquets

> Réconcilié le 2026-05-05 avec `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> et les 10 cas de fixture sous `fixtures/expected/`.

### 2.1. Disposition sur le fil du MeshPacket

Chaque message Aether est encapsulé dans un `MeshPacket`. Les champs apparaissent sur
le fil dans **exactement** cet ordre :

| Déc | Champ            | Type                            | Taille     | Remarques |
|-----|------------------|---------------------------------|------------|-----------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = non signé (héritage), `2` = signé (actuel) |
| 1   | Type             | uint8                           | 1          | Énumération du type de paquet (voir §2.4) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | Identifiant de paquet pour la déduplication. Ordre d'octets **big-endian**, PAS l'ordre mixte Guid par défaut de .NET. |
| 18  | Priority         | uint8                           | 1          | Niveau de priorité (0 = normal, 255 = SOS). **Le champ fil est 1 octet ; les valeurs >255 doivent être écrêtées.** |
| 19  | Ttl              | int32, little-endian            | 4          | Durée de vie, décrémentée à chaque saut. **int32 de 4 octets**, PAS uint8 de 1 octet — les valeurs jusqu'à ~2³¹-1 sont valides. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Millisecondes depuis l'époque Unix (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | Longueur de `SourceUhid` en octets UTF-8. Max 65535. |
| 33  | SourceUhid       | octets UTF-8                    | N          | UHID de l'expéditeur ; vide autorisé mais inhabituel. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | Longueur de `DestinationUhid` en octets UTF-8. |
| ... | DestinationUhid  | octets UTF-8                    | M          | UHID du destinataire ; chaîne vide pour la diffusion. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | Longueur de `PacketNonce` en octets. Valeur standard : 8. |
| ... | PacketNonce      | octets                          | P          | Nonce aléatoire cryptographique pour la prévention des rejeux. |
| ... | Payload Len      | int32, little-endian            | 4          | Longueur de `Payload` en octets. Les valeurs négatives constituent une erreur. |
| ... | Payload          | octets                          | Q          | Données applicatives. L'interprétation dépend de `Type`. |
| ... | Signature Len    | uint16, little-endian           | 2          | Longueur de `Signature` en octets. 0 (non signé) ou 64 (Ed25519). |
| ... | Signature        | octets                          | R          | Signature Ed25519 sur les données signables (voir §2.3). |

**Les largeurs de préfixe de longueur** varient selon le champ — `SourceUhid`, `DestinationUhid`,
`PacketNonce` et `Signature` utilisent des préfixes de longueur de **2 octets (uint16)** ;
`Payload` utilise un préfixe de longueur de **4 octets (int32)** car les charges utiles peuvent dépasser
64 Kio.

### 2.2. Taille minimale de paquet

Avec chaque champ de longueur variable vide (UHID de longueur nulle, nonce de longueur nulle,
charge utile de longueur nulle, signature de longueur nulle), la taille sur le fil est :

```
1 (version) + 1 (type) + 16 (id) + 1 (priorité) + 4 (ttl)
  + 8 (horodatage) + 2 (longueur src) + 2 (longueur dst)
  + 2 (longueur nonce) + 4 (longueur charge utile) + 2 (longueur sig)
= 43 octets
```

Les chiffres de 50 et 52 octets dans les brouillons antérieurs de cette spécification étaient incorrects.

### 2.3. Diagramme du format de fil

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

Pour un exemple concret, voir `fixtures/expected/basic_data.bin` (83 octets,
entrée canonique dans `fixtures/inputs.json`). Les implémentations sont validées
contre l'ensemble du corpus de fixtures — toute divergence fait échouer le test de
vérification de fixture multi-langage.

### 2.4. Construction des données signables

La signature (champ `Signature` sur le fil) est calculée sur une séquence d'octets
canonique séparée — **pas** sur les octets du fil eux-mêmes. Cela
permet à la disposition sur le fil d'évoluer sans invalider les signatures, et laisse
les nœuds intermédiaires vérifier l'intégrité sans voir la charge utile en clair
(seul son hachage SHA-256 est signé).

La séquence d'octets signable est la concaténation :

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> Remarquez la divergence délibérée par rapport à la disposition sur le fil en §2.1 : les données
> signables utilisent **int32 de 4 octets** pour `Type`, `Length`, `Ttl` et `Priority`,
> tandis que le fil utilise respectivement 1 octet / 2 octets / 4 octets / 1 octet.
> C'est intentionnel — la forme signable est portable entre les langages et
> utilise des champs de largeur fixe ; la forme fil est compacte pour l'économie des PDU BLE.
> Les implémentations doivent écrêter `Priority` à `[0,255]` avant l'encodage dans
> les octets signables, sinon le récepteur (qui voit l'octet fil 0..255)
> dérive un tampon signable différent et la vérification échoue.

L'implémentation de référence se trouve à `src/AetherNet.Security/Services/
PacketSigningService.cs::BuildSignableData` et constitue une lecture obligatoire pour
le portage.

### 2.5. Types de paquets

| Valeur | Nom               | Direction     | Description |
|--------|-------------------|---------------|-------------|
| 1      | RouteRequest      | Diffusion     | Demande de route AODV |
| 2      | RouteReply        | Unicast       | Réponse de route AODV (DOIT être signée par la destination) |
| 3      | Data              | Unicast       | Données applicatives |
| 4      | Ack               | Unicast       | Accusé de réception de livraison |
| 5      | SosBroadcast      | Inondation    | Diffusion d'urgence (voir section 8) |
| 6      | SosAck            | Unicast       | Accusé de réception SOS |
| 7      | ChannelMessage    | Multicast     | Message de canal de groupe |
| 8      | ChunkRequest      | Unicast       | Demande de fragment de contenu P2P |
| 9      | ChunkData         | Unicast       | Réponse de fragment de contenu P2P |
| 10     | Heartbeat         | Diffusion     | Signal de vivacité périodique |
| 11     | StreamAnnounce    | Diffusion     | Annonce de flux en direct |
| 12     | StreamSegment     | Unicast/Arbre | Segment média de flux en direct |
| 13     | StreamSubscribe   | Unicast       | Demande d'adhésion à l'arbre de relais du flux |
| 14     | StreamUnsubscribe | Unicast       | Quitter l'arbre de relais du flux |
| 15     | VoicePtt          | Unicast       | Trame vocale push-to-talk |
| 16     | VoiceCall         | Unicast       | Trame d'appel vocal en temps réel |
| 17     | VoiceSignaling    | Unicast       | Établissement/clôture d'appel vocal |
| 18     | DtnBundle         | Unicast       | Paquet DTN de stockage et retransmission (voir section 9) |
| 19     | DtnCustodyAck     | Unicast       | Accusé de réception de transfert de garde DTN |
| 20     | DtnDeliveryReceipt| Unicast       | Confirmation de livraison de bout en bout DTN |
| 21     | PresenceBeacon    | Diffusion     | Annonce de présence et de disponibilité |
| 22     | PresenceQuery     | Unicast       | Demande de statut de présence |
| 23     | ProfileSync       | Unicast       | Synchronisation de métadonnées de profil |
| 24     | TipPacket         | Unicast       | Pourboire de nœud (réglé via LedgerAPI) |
| 25     | PreKeyRequest     | Unicast       | Demande du paquet de pré-clés du pair |
| 26     | PreKeyResponse    | Unicast       | Livraison du paquet de pré-clés |
| 27     | VideoCall         | Unicast       | Trame vidéo chiffrée (unité NAL H.264/H.265/VP8) |
| 28     | VideoSignaling    | Unicast       | Configuration d'appel vidéo : offre, réponse, rejet, fin, négociation de codec |
| 29     | WatchSync         | Unicast       | Commande de lecture synchronisée : lecture, pause, saut, vitesse |
| 30     | WatchReaction     | Multicast     | Réaction emoji ou vocale horodatée lors d'un visionnage en groupe |
| 31     | VideoFrame        | Unicast/SFU   | Trame vidéo de groupe (le relais SFU distribue aux participants) |
| 32     | ScreenShare       | Unicast       | Trame de partage d'écran (même pipeline que la vidéo, signalé séparément) |
| 33     | WatchChunkRequest | Unicast       | Demande de fragment prioritaire pondérée par la position de lecture |
| 34     | TorrentMetadata   | Multicast     | Échange de métadonnées de fichier .torrent BitTorrent ou de lien magnet |

### 2.6. Capacités des nœuds

Les nœuds annoncent leurs capacités sous forme de champ de bits :

| Bit | Valeur | Capacité    | Description |
|-----|--------|-------------|-------------|
| 0   | 1      | Ble         | Transport Bluetooth Low Energy disponible |
| 1   | 2      | WifiDirect  | Transport Wi-Fi Direct disponible |
| 2   | 4      | Gateway     | Passerelle internet (relie le réseau maillé au réseau IP) |
| 3   | 8      | Relay       | Disposé à relayer des paquets pour d'autres |
| 4   | 16     | Sos         | Capable de diffusion SOS |
| 5   | 32     | Streaming   | Capable de relais de diffusion en direct |
| 6   | 64     | Voice       | Capable de relais d'appel vocal |
| 7   | 128    | DtnCarrier  | Transporteur DTN de stockage et retransmission |
| 8   | 256    | NearLink    | Transport NearLink disponible |
| 9   | 512    | Video       | Capable d'encodage/décodage vidéo |

---

## 3. Algorithme de routage

Aether utilise un protocole de routage réactif basé sur le routage par vecteur de distance à la demande (AODV), étendu avec l'authentification cryptographique des routes et la sélection de routes pondérée par QoS.

### 3.1. Demande de route (RREQ)

Lorsqu'un nœud a besoin d'envoyer un paquet vers une destination pour laquelle il n'a pas de route, il initie une demande de route :

1. L'initiateur crée un `MeshPacket` avec `Type = RouteRequest`, définit `SourceUhid` sur lui-même, `DestinationUhid` sur la cible, et `TTL = 7` (valeur par défaut).
2. Le paquet est diffusé à tous les pairs directement connectés.
3. Chaque nœud intermédiaire qui reçoit une RREQ :
   a. Vérifie s'il a déjà vu cette RREQ par `Id` de paquet. Si c'est le cas, il abandonne silencieusement le paquet (déduplication). Le cache de déduplication contient jusqu'à `DeduplicationCacheSize` entrées (défaut 10 000) et est entièrement effacé une fois la limite atteinte.
   b. Installe une **route inverse** vers l'initiateur de la RREQ. La route inverse enregistre l'UHID du pair depuis lequel la RREQ a été reçue comme saut suivant. Le nombre de sauts est dérivé de `DefaultTtl - packet.Ttl + 1`.
   c. S'il EST la destination, il génère une RREP (voir section 3.2).
   d. S'il dispose d'une route valide existante vers la destination, il PEUT générer une RREP au nom de la destination.
   e. Sinon, il décrémente TTL et rediffuse la RREQ.
4. L'initiateur attend une RREP avec un délai d'expiration de **5 000 ms** (`RouteTimeoutMs`). Si aucune RREP n'arrive, la découverte de route échoue.

### 3.2. Réponse de route (RREP)

Lorsque la destination (ou un nœud intermédiaire disposant d'une route valide) génère une réponse de route :

1. Un `MeshPacket` avec `Type = RouteReply` est créé, avec `SourceUhid` défini sur le nœud de destination et `DestinationUhid` défini sur l'initiateur de la RREQ.
2. **EXIGENCE DE SÉCURITÉ :** La RREP DOIT être signée par la clé d'identité Ed25519 du nœud de destination. La signature couvre les données signables standard (section 2.3). Cela empêche l'empoisonnement de route par des nœuds intermédiaires malveillants.
3. La RREP est envoyée en unicast en remontant la route inverse installée lors de la propagation de la RREQ.
4. Chaque nœud intermédiaire qui transmet la RREP :
   a. Vérifie la signature de la RREP par rapport à la clé publique de la source déclarée (si connue). Si la vérification échoue, la RREP est abandonnée et un avertissement est enregistré.
   b. Installe une **route directe** vers la source de la RREP (le nœud de destination) avec l'expéditeur de la RREP comme saut suivant.
   c. Décrémente TTL et transmet vers l'initiateur de la RREQ.
5. Lorsque la RREP atteint l'initiateur, la demande de route en attente (suivie via `TaskCompletionSource`) est résolue avec la route installée.

### 3.3. Maintenance des routes

- **Expiration basée sur TTL :** Chaque entrée de route porte un horodatage `ExpiresAt` défini à `maintenant + 300 secondes` (`RouteExpirySeconds`). Les routes ne sont pas actualisées implicitement ; elles doivent être rétablies via un nouveau cycle RREQ/RREP après expiration.
- **Élagage périodique :** Le service de protocole exécute un battement de cœur périodique (par défaut toutes les 300 secondes). À chaque cycle, il supprime les routes expirées du `ConcurrentDictionary` en mémoire et du magasin de sauvegarde SQLite.
- **Élagage de déduplication RREQ :** L'ensemble des identifiants RREQ vus est effacé lorsqu'il dépasse `DeduplicationCacheSize` (défaut 10 000) entrées.

### 3.4. Qualité de route et QoS

Chaque `RouteEntry` porte un `QualityScore` dans la plage [0, 100], initialisé à 50 pour les routes nouvellement découvertes. Le score prend en compte :

- **Nombre de sauts :** Moins de sauts indique généralement une route plus rapide.
- **Latence :** Temps de trajet aller-retour mesuré lorsque disponible.
- **Fiabilité du pair :** Le score de fiabilité du pair de saut suivant (voir section 3.5).

Les nœuds qui participent au système d'incitation par pourboire reçoivent un boost QoS sur leur score de qualité de route. Il s'agit d'une préférence douce : les non-donneurs de pourboire bénéficient toujours du service, mais les donneurs réguliers peuvent bénéficier d'une sélection de route marginalement meilleure. Les niveaux de boost sont :

| Niveau  | Seuil de régularité | Boost QoS |
|---------|---------------------|-----------|
| Bronze  | 25                  | +5        |
| Silver  | 50                  | +10       |
| Gold    | 75                  | +20       |

### 3.5. Notation de fiabilité des pairs

Chaque pair connu se voit attribuer un score de fiabilité dans la plage [0, 100], initialisé à 50 (`DefaultReliabilityScore`). Le score est ajusté en fonction du comportement observé :

| Événement             | Delta |
|-----------------------|-------|
| Relais réussi         | +2    |
| Relais échoué         | -5    |
| Relais SOS            | +5    |
| Fragment servi        | +1    |
| Échec de service de fragment | -10 |

Les scores de fiabilité sont persistés dans SQLite et chargés en mémoire au démarrage. Le score influence la sélection de route : les routes passant par des pairs plus fiables sont préférées.

---

## 4. Échange de clés

> Réconcilié le 2026-05-05 avec l'implémentation de référence C# à
> `src/AetherNet.Security/Services/SignalProtocolService.cs` et le corpus de
> fixtures multi-langage sous `fixtures/signal/`. La référence C# embarque X3DH
> complet + Double Ratchet (Signal §3 + §5) sur X25519. Go,
> Python, TypeScript, Rust, Swift, Kotlin et C ont tous été portés vers la même
> enveloppe et sont équivalents octet par octet au niveau des fixtures X3DH et KDF_RK.
> C embarque désormais aussi la machinerie de session complète (X3DH + cycle de vie
> OPK/SPK + Double Ratchet dans `c/src/signal_protocol.c`, avec des tests E2E à deux
> nœuds dans `c/tests/test_signal_session.c`), pas seulement les primitives.
> Lorsque cette section est en désaccord avec le code, le code fait autorité ;
> ouvrir un ticket dans `OPEN_ISSUES.md`.

Aether implémente **X3DH** (Extended Triple Diffie-Hellman, Signal §3) pour
l'établissement de session asynchrone, immédiatement suivi du **Signal
Double Ratchet** (Signal §5) pour la confidentialité persistante et la
sécurité post-compromission. Tout le chiffrement de session s'exécute sur Curve25519 :
**X25519** (RFC 7748) pour ECDH et **Ed25519** (RFC 8032) pour la signature.

### 4.1. Clés d'identité

Chaque nœud génère **deux** paires de clés à long terme au premier lancement (pas de XEdDSA ;
l'arrangement à double clé plus simple est ce que chaque implémentation embarque) :

- **Paire de clés Ed25519** — graine de 32 octets (privée), clé publique de 32 octets.
  Utilisée pour la signature de paquets (§2.4), `SignedPreKeySignature` (§4.3),
  authentification RREP (§3.2) et signatures de pourboires.
- **Paire de clés X25519** — clés privée et publique brutes de 32 octets. Utilisées pour
  les quatre opérations DH X3DH (§4.4).

Référence : `SignalProtocolService.InitializeIdentityKeys`. Les clés privées
restent sur l'appareil uniquement ; les clés publiques sont publiées dans `PreKeyBundle`.

Une fenêtre de migration P-256 → Ed25519 de 30 jours est honorée pour la
*vérification de signature* sur les paquets entrants uniquement — voir §7.5. Les paquets
de pré-clés eux-mêmes sont en X25519 uniquement sur le fil.

### 4.2. Choix de courbe

X3DH et le Double Ratchet utilisent **X25519** exclusivement. P-256 n'est *pas*
utilisé dans l'établissement de session par aucune implémentation actuelle. Un brouillon
antérieur de cette spécification décrivait P-256 ECDH ; ce texte est antérieur au
portage familial vers X25519 du 2026-05-05 et n'est plus exact.

### 4.3. Paquet de pré-clés

Un paquet de pré-clés est publié afin qu'un initiateur puisse établir une
session sans que le répondant soit en ligne (Signal §3.4) :

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

Référence : `AetherNet.Security.Models.PreKeyBundle`. La forme de contrat de fil est
la même dans les 8 langages.

**Réserve de pré-clés à usage unique (OPK).** Chaque répondant maintient une réserve de
`OpkPoolSize` (défaut 100, reflétant les recommandations publiées de Signal) OPK X25519.
La génération de paquet extrait le prochain identifiant inutilisé d'une file FIFO, puis
réapprovisionne la réserve jusqu'à sa taille cible. Chaque OPK est consommée exactement
une fois : le répondant supprime et efface la moitié privée lors du premier message
PreKey qui référence son identifiant. Les initiateurs concurrents en compétition pour
le même identifiant OPK verront exactement un `EstablishResponderSession` réussir
sous `_preKeyLock` ; le perdant lève `CryptographicException`.

Référence : `SignalProtocolService.TopUpOpkPoolNoLock` (lignes 494–518),
`SignalProtocolService.EstablishResponderSession` (lignes 636–718). La sémantique
de la réserve est exercée par `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`.

**Rotation de la pré-clé signée (SPK).** La SPK est générée paresseusement lors du
premier appel de paquet et réutilisée entre les appels suivants afin que les initiateurs
concurrents récupérant des paquets avant l'exécution de X3DH ne s'invalident pas
mutuellement. La rotation périodique de la SPK (Signal §3.3 recommande chaque semaine)
est une opération explicite, pas un effet secondaire de la génération de paquet.

Les identifiants de pré-clés sont tirés de `RandomNumberGenerator.GetInt32(1, int.MaxValue)`
avec une nouvelle tentative explicite en cas de collision (jusqu'à 64 tentatives avant de lever une exception).

### 4.4. Établissement de session (X3DH)

Le X3DH complet (Signal §3.3) s'exécute côté initiateur. Quatre opérations
DH sont calculées sur X25519 :

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

où `IK_A` / `IK_B` sont les clés d'identité X25519, `EK_A` est une clé
éphémère X25519 fraîche générée uniquement pour cette session, `SPK_B` est la
pré-clé signée du répondant, et `OPK_B` est la pré-clé à usage unique du répondant.
La clé racine initiale est :

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

La constante `info` `aether-x3dh-root-v1` est identique dans chaque
implémentation et est épinglée par `fixtures/signal/expected/x3dh_basic.json`
(champ `root_key_hex`).

Référence : `SignalProtocolService.ProcessPreKeyBundleAsync` (lignes
554–626). Chemin de vérification :
cas `x3dh_basic` de `fixtures/signal/inputs.json` →
`fixtures/signal/expected/x3dh_basic.json`.

**Vérification du paquet.** Avant tout calcul DH, l'initiateur vérifie
`SignedPreKeySignature` par rapport à `IdentityKey` en utilisant Ed25519. Un échec
de vérification lève `CryptographicException` et le paquet est abandonné.
Les tailles de clé publique sont validées par rapport à `X25519Service.PublicKeySize` (32) ;
les paquets malformés sont rejetés.

**Amorçage de session.** À la fin de `ProcessPreKeyBundleAsync`, un
`SignalSession` est créé avec :

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — intégration X3DH ↔
  Double-Ratchet canonique Signal : la clé éphémère X3DH de l'initiateur devient
  sa première paire de clés de ratchet DH (`DHs`).
- `RemoteEphemeralPub = SPK_B` — la pré-clé signée du répondant est
  traitée comme la clé de ratchet pair initiale (`DHr`).
- `SendChainKey = null`, `RecvChainKey = null` — les deux clés de chaîne sont
  dérivées paresseusement lors du premier envoi / premier réception de ratchet DH.
- `PendingPreKeyMessage = true` — signale que le prochain appel `EncryptAsync`
  sortant DOIT émettre un message PreKey (`MessageType=1`).

Toutes les sorties DH et le secret partagé concaténé sont effacés dans le
bloc `finally` via `CryptographicOperations.ZeroMemory`.

**Refus d'envoyer de façon non sécurisée.** Si `EncryptAsync` est appelé pour un pair
sans session, l'appel lève `InvalidOperationException`. Il n'existe pas de
chemin de repli dérivé de l'UHID. Les hôtes sont censés mettre le message en file
(voir `MessagingService` + `SignalMessageEnvelopeCipher`) et réessayer une fois
l'établissement de session terminé.

### 4.5. Double Ratchet (Signal §5)

Chaque côté maintient une paire de clés de ratchet X25519 rotative (`DHs`) et une copie
de la dernière clé publique de ratchet vue du pair (`DHr`). À chaque message,
l'expéditeur publie son `DHs` public actuel ; chaque fois que le récepteur
observe un nouveau `DHr`, il exécute une **étape de ratchet DH** qui rekeye la
chaîne via `KDF_RK(RK, DH(myDHs, newDHr))` — re-dérivant à la fois la clé racine
et une nouvelle clé de chaîne fraîche.

#### 4.5.1. KDF_RK

`KDF_RK` est HKDF-SHA256 sur un bloc de 64 octets, divisé 32+32 en la nouvelle
clé racine et la nouvelle clé de chaîne :

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

Référence : `SignalProtocolService.KdfRk` (lignes 857–868). Épinglé par
le cas `kdf_rk_basic` de `fixtures/signal/inputs.json` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Ratchet symétrique

Selon Signal §5.1, les clés de message et les clés de chaîne sont dérivées d'une clé
de chaîne à l'aide de HMAC-SHA256 avec une séparation de domaine à un seul octet :

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Référence : `SignalProtocolService.RatchetChainKey` (lignes 876–881).
Épinglé par les cas `ratchet_step_basic` et
`ratchet_step_three_iterations` de `fixtures/signal/inputs.json`.

Le brouillon antérieur de cette spécification décrivait `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` et une avancée de `chain_key`
séparée via `HMAC(chain_key, 0x01)`. C'était non-Signal et jamais
implémenté ; cela a été remplacé par la séparation canonique 0x01/0x02.

#### 4.5.3. Étape de ratchet DH à la réception

Déclenchée lorsque le `SenderEphemeralKeyX25519` du message entrant diffère
de `RemoteEphemeralPub` mis en cache (comparaison en temps constant).

1. Sauvegarder le compteur sortant comme `PreviousChainCount` (Signal §5 : PN) afin que le
   pair puisse calculer les clés sautées à travers la limite.
2. Réinitialiser `SendCounter` et `RecvCounter` à 0 ; installer le nouveau
   `RemoteEphemeralPub`.
3. Dériver la nouvelle chaîne de réception : `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Effacer l'ancienne clé privée `myDHs` ; générer une nouvelle paire de clés X25519.
5. Dériver la nouvelle chaîne d'envoi : `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Référence : `SignalProtocolService.DhRatchetReceive` (lignes 726–772).

#### 4.5.4. Dérivation paresseuse de la chaîne d'envoi

Le premier envoi de l'initiateur exécute une **demi-étape** plutôt qu'un
ratchet DH complet — le X3DH a déjà placé `DHs` et `DHr`, donc seule la
chaîne d'envoi doit être dérivée :

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` n'est *pas* roté ici. Il n'est roté que lors d'une véritable étape de
ratchet DH côté réception.

Référence : `SignalProtocolService.DhRatchetSendOnly` (lignes 780–796).

#### 4.5.5. Clés de message sautées

Lorsque des messages arrivent dans le désordre, la clé de message de chaque compteur
sauté est mise en cache dans `SkippedMessageKeys`, indexée par `(Hex(remoteEphPub):counter)`.
La liaison à la clé publique distante est essentielle — les messages hors ordre d'une chaîne
précédente (autre `DHr`) peuvent encore arriver après une étape de ratchet DH et
ont besoin de leur propre ensemble de clés par chaîne.

Limites :

- Sauter plus de `MaxSkippedKeys` (1000) entrées dans un seul gap
  lève `CryptographicException` et force le rétablissement de session.
- En franchissant une limite de ratchet DH, le récepteur saute d'abord jusqu'à
  `PreviousChainCount` clés sur l'*ancienne* chaîne, puis exécute l'étape
  de ratchet DH avant de dériver des clés sur la nouvelle chaîne.

Référence : `SignalProtocolService.SkipMessageKeys` (lignes 804–830) et
la boucle de saut dans le déchiffrement (lignes 366–388).

### 4.6. Format de charge utile chiffrée

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

Référence : `AetherNet.Security.Models.EncryptedPayload` (lignes 55–66 de
`SecurityModels.cs`). Le champ `InitiatorEphemeralKeyX25519` est un alias
de compatibilité ascendante pour l'enveloppe de fil pré-Double-Ratchet et
est égal à `SenderEphemeralKeyX25519` sur les messages PreKey ; les nouveaux
consommateurs doivent l'ignorer.

Paramètres AES-GCM : clé de 256 bits, nonce de 96 bits (`AesNonceSize = 12`),
tag de 128 bits (`AesTagSize = 16`), tag concaténé au texte chiffré.
Les clés de message sont effacées dans des blocs `finally` immédiatement après le
chiffrement/déchiffrement AES-GCM.

### 4.7. Statut par langage

| Langage     | X3DH (4 DH) | Double Ratchet | Réserve OPK       | Vérifié par fixture |
|-------------|-------------|----------------|-------------------|---------------------|
| C# (.NET)   | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | complet     | complet (§5)   | réserve, défaut 100 | x3dh_basic, ratchet_*, kdf_rk_basic |

Les 8 langages (C# + Go + TypeScript + Python + Kotlin + Swift + Rust + C) embarquent le service de session complet X3DH + Double Ratchet et la réserve OPK FIFO de 100 clés avec réapprovisionnement paresseux et consommation protégée par verrou, correspondant au contrat de référence C#. Le service de session C vit dans `c/src/signal_protocol.c` avec des tests E2E à deux nœuds dans `c/tests/test_signal_session.c`.

---

## 5. Exigences de la couche de transport

Aether est agnostique au transport. Tout canal de communication physique qui satisfait le contrat `ITransportService` peut participer au réseau maillé.

### 5.1. Contrat d'interface ITransportService

Chaque implémentation de transport DOIT exposer les éléments suivants :

**Propriétés :**

| Propriété          | Type   | Description |
|--------------------|--------|-------------|
| `Name`             | string | Identifiant lisible par l'humain (ex. : "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Indique si le transport est actuellement utilisable sur cet appareil |
| `MaxBandwidthBps`  | int64  | Débit maximum en octets par seconde |
| `MaxRangeMeters`   | int32  | Portée de communication maximale en mètres |
| `PowerCostRelative`| int32  | Consommation d'énergie relative (1 = faible, 10 = élevée) |
| `MaxConcurrentPeers` | int32 | Nombre maximum de connexions de pairs simultanées |

**Méthodes :**

| Méthode        | Signature | Description |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Envoie un tableau d'octets à un pair spécifique. Retourne true en cas de succès. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Envoie un flux à un pair (pour les grands transferts, la voix, la vidéo). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Vérifie si une connexion est active avec un pair. |

**Événements :**

| Événement      | Signature | Description |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Déclenché à la réception de données d'un pair. |

### 5.2. Algorithme de sélection de transport

Le `TransportManager` sélectionne le transport optimal pour chaque paquet selon :

1. **Disponibilité :** Seuls les transports où `IsAvailable == true` sont considérés.
2. **Taille de la charge utile :** Si la taille de la charge utile est inférieure ou égale à `BleMaxPayloadBytes` (1 024 octets), BLE est préféré pour l'efficacité énergétique. Les charges utiles plus grandes préfèrent Wi-Fi Direct.
3. **Pondération du coût énergétique :** Parmi les transports disponibles, les valeurs `PowerCostRelative` plus faibles sont préférées pour le trafic courant. Les paquets haute priorité (SOS, voix) peuvent contourner cette préférence.
4. **Connectivité du pair :** Si un transport a déjà une connexion active avec le pair cible (`IsConnected` retourne true), il est préféré pour éviter la surcharge d'établissement de connexion.
5. **Repli :** Si aucun transport local ne peut atteindre la cible, le paquet est mis en file d'attente pour relais serveur via AetherNetAPI.

### 5.3. Transports de référence

| Transport    | Bande passante max | Portée max | Coût énergétique | Pairs max | Remarques |
|-------------|-------------------|------------|------------------|-----------|-----------|
| BLE 5.0     | ~2 Mbps           | 100m       | 1                | 7         | Découverte principale + petits paquets |
| Wi-Fi Direct| ~250 Mbps         | 200m       | 5                | 8         | Grands transferts, diffusion, voix |
| NearLink    | ~900 Mbps         | 200m       | 3                | 16        | Huawei/HiSilicon, haut débit |

**Limite de charge utile BLE :** Les paquets dépassant 1 024 octets (`BleMaxPayloadBytes`) sont automatiquement routés vers Wi-Fi Direct ou NearLink. BLE est utilisé pour les annonces de découverte, les petits paquets de contrôle (RREQ/RREP, balises de présence), et la messagerie à faible bande passante.

**Wi-Fi Direct** : le délai d'expiration de connexion est de 10 000 ms (`WifiDirectTimeoutMs`) avec un maximum de 8 pairs simultanés (`MaxWifiDirectPeers`).

---

## 6. Protocole de découverte

### 6.1. Publicité BLE

Les nœuds Aether se découvrent principalement via la publicité BLE. Pour empêcher le suivi persistant via des identifiants statiques, le protocole emploie deux mécanismes de confidentialité : la rotation des UUID de service et les clés de résolution d'identité.

**Cycle de publicité :** 2 secondes de scan activé, 8 secondes de scan désactivé (`BleScanOnMs`/`BleScanOffMs`). L'intervalle de publicité est de 1 000 ms (`BleAdvertiseIntervalMs`). Une gigue aléatoire de 0 à 2 000 ms (`BleScanJitterMaxMs`) est ajoutée à l'intervalle de scan pour empêcher la détection de motifs temporels.

**Délai d'expiration de pair :** Un pair non redécouvert dans les 30 secondes est considéré perdu (événement `PeerLost`).

### 6.2. UUID de service rotatif

Pour empêcher l'empreinte digitale BLE à long terme, l'UUID de service utilisé dans les publicités pivote toutes les 15 minutes (`BleUuidRotationSeconds = 900`) :

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

La `rotation_key` est une clé de 32 octets générée une fois par nœud et stockée en lieu sûr. Tous les nœuds Aether partageant la même clé de rotation dériveront le même UUID pour une fenêtre temporelle donnée, permettant la découverte mutuelle sans révéler un identifiant permanent.

Un UUID de repli statique (`A3E7-1001-0001-0000-000000000000`) est maintenu pendant 90 jours lors de la transition depuis le schéma non rotatif.

### 6.3. Clé de résolution d'identité (IRK)

Chaque nœud génère une clé de résolution d'identité (IRK) de 128 bits stockée en lieu sûr. L'IRK est partagée avec les pairs de confiance lors de l'échange de clés.

**Génération d'adresse privée résolvable (RPA) :**

1. Calculer `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 octets).
2. Définir les deux bits les plus significatifs de `prand[0]` à `01` (indicateur RPA selon la spécification BLE).
3. Calculer `hash = AES-128-ECB(IRK, pad(prand))` où `prand` occupe les octets 13 à 15 d'une entrée de 16 octets complétée par des zéros.
4. Construire RPA : `hash[0..2] || prand[0..2]` (6 octets au total).

**Résolution RPA :** Un nœud possédant l'IRK d'un pair peut vérifier si une RPA observée appartient à ce pair en recalculant le hachage à partir du composant `prand` de la RPA. Le temps de résolution est approximativement O(N) où N est le nombre d'IRK connus, évalué à ~0,1 ms pour 100 pairs.

La RPA pivote selon le même cycle de 15 minutes que l'UUID de service.

### 6.4. Proximité basée sur le géohash

Les nœuds encodent optionnellement leur emplacement sous forme de géohash. Pour des raisons de confidentialité, le géohash est tronqué à 4 caractères, offrant une résolution d'environ 39 km x 20 km. Cette granularité est suffisante pour :

- La découverte de canal basée sur la proximité
- Le routage épidémique DTN (réplication vers la dernière zone de géohash connue du destinataire)
- Le contexte géographique des alertes SOS

Le géohash pleine précision n'est jamais transmis sur le réseau maillé. Seule la forme tronquée est partagée, et seulement lorsque le niveau de confidentialité du nœud le permet (`PrivacyLevel.Full` ou `PrivacyLevel.Partial`).

---

## 7. Modèle de sécurité

### 7.1. Modèle de menace

Aether suppose que l'adversaire dispose des capacités suivantes :

- **Écoute passive :** L'adversaire peut observer toutes les publicités BLE et le trafic maillé dans la portée radio.
- **Injection active :** L'adversaire peut injecter, modifier ou rejouer des paquets.
- **Attaque Sybil :** L'adversaire peut créer plusieurs fausses identités de nœud.
- **Déni de service sélectif :** L'adversaire peut sélectivement abandonner des paquets en tant que nœud relais.

### 7.2. Ce qui est protégé

| Propriété | Niveau de protection | Mécanisme |
|-----------|---------------------|-----------|
| Contenu du message | Confidentialité totale | AES-256-GCM avec des clés par message (section 4.5) |
| Identité de l'expéditeur | Partielle | UHID visible dans les en-têtes de paquet ; adresse BLE rotative (section 6) |
| Identité du récepteur | Partielle | UHID de destination visible dans les paquets routés ; les paquets de diffusion ont une destination vide |
| Métadonnées de routage | Minimale | Les nœuds intermédiaires voient les UHID source/destination et TTL |
| Ordre des messages | Protégé | Les compteurs dans le ratchet symétrique empêchent le réordonnancement |
| Intégrité des messages | Totale | Signature Ed25519 sur chaque paquet (v2) |

### 7.3. Résistance aux attaques

**Attaques par rejeu :**
Chaque paquet porte un nonce aléatoire cryptographique de 8 octets et un horodatage à précision milliseconde. Les nœuds relais maintiennent un cache de déduplication de paires `(SenderUhid, NonceValue)` avec une TTL de 5 minutes (`MaxPacketAgeSeconds = 300`). Un paquet avec un nonce en double du même expéditeur est abandonné. Les paquets avec des horodatages de plus de 5 minutes sont rejetés indépendamment du nonce.

Le cache de déduplication de nonces est nettoyé toutes les 60 secondes. Les entrées expirées (de plus de 5 minutes) sont supprimées.

**Homme du milieu (MITM) :**
- Les paquets de réponse de route DOIVENT porter une signature Ed25519 valide du nœud de destination déclaré. Les nœuds intermédiaires ne peuvent pas forger des RREP car ils ne possèdent pas la clé privée de la destination.
- Les paquets de pré-clés incluent un `SignedPreKeySignature` (Ed25519) sur la `SignedPreKey`, liant la clé ECDH éphémère à l'identité à long terme.
- L'établissement de session (section 4.4) lie cryptographiquement la session aux identités des deux parties via l'étape de vérification de pré-clé.

**Attaques Sybil :**
- Le score de fiabilité de chaque nœud commence à 50 et est ajusté en fonction du comportement observé (section 3.5). Les nœuds Sybil nouvellement créés n'ont aucune réputation accumulée.
- Les nœuds avec des scores de fiabilité faibles (proches de 0) sont déprioritisés dans la sélection de route.
- L'algorithme de routage épidémique DTN utilise la proximité géohash et l'historique de succès de relais pour sélectionner les cibles de réplication, rendant plus difficile pour les nœuds Sybil d'attirer du trafic sans contributions de relais authentiques.

**Attaques par inondation :**
- TTL est décrémenté à chaque saut et les paquets avec TTL = 0 sont abandonnés. La valeur TTL par défaut de 7 limite le rayon d'action de toute diffusion.
- La déduplication RREQ par identifiant de paquet empêche l'amplification par des tempêtes de diffusion. Le cache de déduplication est vidé lorsqu'il dépasse `DeduplicationCacheSize` (défaut 10 000) entrées.
- Les diffusions SOS sont limitées à 3 par heure par nœud (section 8).

### 7.4. Effacement des clés

Tout le matériel cryptographique intermédiaire est effacé immédiatement après utilisation :

- `sharedSecret` issu de l'accord de clé ECDH : effacé après dérivation HKDF.
- `messageKey` issu du ratchet de chaîne : effacé après chiffrement/déchiffrement AES-GCM.
- `skippedKey` issu du déchiffrement hors ordre : effacé après utilisation et supprimé de la carte.
- `RootKey`, `SendChainKey`, `RecvChainKey` dérivés : effacés du contexte d'établissement (la session conserve ses propres copies).

L'effacement utilise `CryptographicOperations.ZeroMemory` qui est garanti de ne pas être optimisé par le compilateur.

### 7.5. Migration P-256 vers Ed25519

Le protocole supporte une fenêtre de transition de 30 jours des clés d'identité ECDSA P-256 (version de protocole 1) vers Ed25519 (version de protocole 2) :

1. Les paquets de version de protocole 1 (non signés) sont acceptés pendant la période de transition.
2. La vérification de signature tente d'abord Ed25519. Si la clé publique est plus longue que 32 octets (indiquant une clé P-256 encodée DER), elle se replie sur la vérification ECDSA P-256.
3. Après la fenêtre de 30 jours, les paquets de version de protocole 1 sont rejetés.
4. Les nœuds qui n'ont pas migré doivent se réinitialiser avec une nouvelle identité Ed25519.

### 7.6. Conscience juridictionnelle

Le protocole définit des niveaux de juridiction pour gérer les exigences légales variables autour du chiffrement et du réseau maillé :

| Niveau | Comportement | Exemples de juridictions |
|--------|-------------|--------------------------|
| 1      | Fonctionnement libre | Afrique du Sud, Kenya, Ghana |
| 2      | Fonctionnement modifié | Nigeria, Inde, UE, États-Unis, Royaume-Uni |
| 3      | Réseau maillé uniquement (risque élevé) | Chine, Russie, Iran, EAU, Myanmar |
| 4      | Inconnu (réseau maillé uniquement par défaut) | Tous les autres |

La sélection de niveau affecte la disponibilité des fonctionnalités (ex. : les fonctionnalités de pourboire/financières peuvent être désactivées au niveau 3) mais n'affaiblit pas le chiffrement. Le chiffrement de bout en bout est toujours appliqué indépendamment de la juridiction.

---

## 8. Diffusion SOS

Le mécanisme SOS est une inondation d'urgence à double chemin conçue pour les situations où un utilisateur est en danger et a besoin d'atteindre des pairs maillés proches et/ou internet simultanément.

### 8.1. Paramètres de diffusion

| Paramètre  | Valeur  | Description |
|------------|---------|-------------|
| TTL        | 15      | Le double de la valeur par défaut normale (7), assurant une propagation plus large |
| Priority   | 999     | Priorité maximale ; préempte tout autre trafic dans les files de relais |
| Limite de débit | 3/heure | Limite par nœud pour éviter les abus |
| Destination | vide   | Diffusion à tous les pairs (aucune destination spécifique) |

### 8.2. Algorithme d'inondation

1. L'initiateur construit un paquet SOS avec `Type = SosBroadcast`, `TTL = 15`, `Priority = 999`, et un `DestinationUhid` vide.
2. La charge utile est encodée en JSON et contient :
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **Envoi en double chemin :** Le SOS est envoyé simultanément via :
   - **Inondation maillée :** Diffusion à tous les pairs connectés via tous les transports disponibles.
   - **Appel API :** Envoyé à AetherNetAPI pour la distribution côté serveur et la liaison à PanikAPI (envoi de SMS/e-mail).
4. Les deux chemins sont de type « fire-and-forget » l'un par rapport à l'autre. Si l'appel API échoue, l'inondation maillée se poursuit indépendamment.

### 8.3. Comportement de relais

Lorsqu'un nœud reçoit un paquet SOS :

1. Vérifier la déduplication par `Id` de paquet. Si déjà vu, abandonner silencieusement.
2. Désérialiser la charge utile et déclencher l'événement `SosReceived` pour l'interface utilisateur locale.
3. Ajouter l'alerte à la liste des alertes actives.
4. Si `TTL > 1`, décrémenter TTL et **rediffuser à TOUS les pairs** indépendamment de l'état de la table de routage. Les paquets SOS contournent le routage normal — ils inondent inconditionnellement.

### 8.4. Limitation de débit

Chaque nœud maintient une fenêtre glissante d'horodatages de diffusion récents. Avant d'initier un nouveau SOS :

1. Supprimer les entrées de plus d'une heure de la file.
2. Si la file contient 3 entrées ou plus (`MaxSosBroadcastsPerHour`), la diffusion est rejetée.
3. En cas d'envoi réussi, l'horodatage actuel est enfilé.

La limitation de débit s'applique uniquement aux diffusions SOS originales, pas au relais.

### 8.5. Pont SOS-PanikAPI

Les diffusions SOS reçues via le réseau maillé peuvent être transmises à PanikAPI pour une réponse d'urgence traditionnelle (SMS aux contacts, alertes e-mail). Inversement, les sessions d'urgence PanikAPI peuvent être diffusées sur le réseau maillé pour la sensibilisation communautaire. La prévention des boucles est obtenue en marquant la source (`direct` vs `mesh_forward`) et un indicateur `internet_forwarded` sur les diffusions maillées.

---

## 9. Stockage et retransmission DTN

Le sous-système DTN (réseau tolérant aux délais) permet la livraison de messages lorsqu'aucun chemin de bout en bout n'existe entre l'expéditeur et le destinataire. Les paquets sont stockés sur les nœuds intermédiaires et transmis de manière opportuniste à mesure que la connectivité change.

### 9.1. Format de paquet

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. Cycle de vie d'un paquet

1. **Création :** L'expéditeur crée un paquet avec une charge utile chiffrée (chiffrée via la session Signal avec le destinataire). `Status = Pending`, `CopyCount = 1`.
2. **Tentative de livraison immédiate :** L'expéditeur tente d'abord le routage maillé direct (RREQ/RREP). Si une route existe, le paquet est livré immédiatement et `Status` passe à `Delivered`.
3. **Tentative de relais serveur :** Si le routage maillé échoue, l'expéditeur tente de relayer via AetherNetAPI. Si le serveur peut atteindre le destinataire (ou mettre le message en file), la livraison réussit.
4. **Stockage et retransmission :** Si le routage maillé et le relais serveur échouent tous les deux, le paquet reste dans le stockage local (statut `Pending`) en attente du prochain scan de livraison.

### 9.3. Scan de livraison

Un scan périodique s'exécute toutes les 60 secondes (`DtnScanIntervalSeconds`) :

1. Charger tous les paquets en attente depuis SQLite (source de vérité).
2. Pour chaque paquet en attente :
   a. Tenter la route maillée vers le destinataire.
   b. Tenter le relais serveur.
   c. Si les deux échouent et `CopyCount < MaxCopies`, tenter la réplication épidémique (section 9.4).
3. Supprimer les paquets expirés (`ExpiresAt <= now`).

### 9.4. Routage épidémique

Lorsque la livraison directe et le relais serveur échouent tous les deux, les paquets sont répliqués vers les pairs proches en utilisant le routage épidémique :

1. L'`EpidemicRoutingService` sélectionne les cibles de réplication dans la liste de pairs actuelle.
2. La sélection de cible prend en compte :
   - **Proximité géohash :** Les pairs dont le géohash est plus proche du dernier géohash connu du destinataire sont préférés.
   - **Historique de relais :** Les pairs avec des scores de fiabilité plus élevés sont préférés.
   - **Budget de copies :** La réplication s'arrête lorsque `CopyCount >= MaxCopies` (défaut : 3).
3. Chaque réplication envoie un paquet `DtnBundle` au pair sélectionné.
4. À la réception, le service DTN du pair invoque `AcceptCustodyAsync`.

### 9.5. Transfert de garde

Lorsqu'un nœud reçoit un paquet DTN destiné à un autre nœud :

1. **Vérification de capacité :** Le nœud vérifie son nombre actuel de paquets par rapport à `DtnMaxBundlesPerNode` (50). Si à capacité, la garde est rejetée.
2. **Acceptation :** Le statut du paquet est défini à `InCustody`, le nombre de sauts est incrémenté, et le paquet est persisté dans SQLite.
3. **Enregistrement de garde :** Un `CustodyRecord` est créé documentant le transfert (de, à, horodatage).
4. **Incrément du nombre de copies :** Le `CopyCount` du paquet est incrémenté dans le stockage persistant.
5. **Accusé de réception :** Un paquet `DtnCustodyAck` est renvoyé au nœud transférant avec `Accepted = true`.
6. Le nœud acceptant devient responsable de la tentative de livraison lors des scans suivants.

### 9.6. Reçu de livraison

Lorsque le destinataire prévu reçoit un paquet DTN :

1. Le statut du paquet est mis à jour vers `Delivered`.
2. Un `DtnDeliveryReceipt` est renvoyé à l'expéditeur original via le routage maillé (avec repli par relais serveur) :
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. À la réception du reçu, l'expéditeur supprime le paquet de son magasin et déclenche l'événement `BundleDelivered`.
4. Le reçu est également synchronisé vers AetherNetAPI à des fins d'analyse.

### 9.7. Expiration des paquets

- La durée de vie par défaut d'un paquet est de 72 heures (`DtnBundleTtlHours`).
- Les paquets expirés sont nettoyés lors du scan de livraison périodique.
- Les paquets avec un statut `Expired` ou `Delivered` sont supprimés du cache en mémoire et de SQLite.

### 9.8. Limites de capacité

| Paramètre               | Défaut | Description |
|-------------------------|--------|-------------|
| `DtnBundleTtlHours`    | 72     | Durée de vie maximale d'un paquet |
| `DtnMaxCopies`          | 3      | Nombre maximum de copies par paquet dans le réseau |
| `DtnMaxBundlesPerNode`  | 50     | Nombre maximum de paquets qu'un seul nœud peut porter |
| `DtnScanIntervalSeconds`| 60     | Fréquence du scan de livraison |

---

## 10. Diffusion vidéo

> **Statut au 2026-05-05 — conception + échafaudage C#, pas de pipeline de codec opérationnel.**
> Les types de paquets `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) sont
> définis sur le fil et font l'aller-retour via le corpus de fixtures multi-langage.
> Le module C# `AetherNet.Streaming` embarque des interfaces, des modèles et des services
> squelettes (`StreamingService`, `VideoCallService`, `WatchTogetherService`)
> qui câblent les coutures de routage/DI et la distribution de segments en unicast — mais
> aucun encodage/décodage vidéo réel ne leur est lié. Les 7 autres langages ont
> uniquement des types de fil. Le document de conception prospective à
> `docs/adaptive-secure-streaming-spec.md` est l'architecture cible.
> Traiter la prose ci-dessous comme la spécification de ce que ces services VONT
> implémenter ; consulter `OPEN_ISSUES.md` pour les lacunes de préparation à la production.

Aether supporte trois modes vidéo : les appels vidéo pair à pair, la vidéo de groupe (participants illimités avec topologie dynamique), et la diffusion en direct. Toutes les trames vidéo sont chiffrées avec Signal Protocol et signées avec Ed25519.

### 10.1. Matrice de capacité de transport

Avant d'initier un appel vidéo, l'initiateur interroge la couche de transport pour déterminer la meilleure connexion disponible vers le pair. Le transport détermine la qualité vidéo possible :

| Transport | Support vidéo | Résolution max | Codec recommandé | Débit max | Regarder ensemble |
|-----------|--------------|----------------|------------------|-----------|-------------------|
| BLE | Non (audio uniquement) | — | — | 64 Kbps | Paquets de sync uniquement |
| NearLink | Léger | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Complet | 1080p | H.264 | 3000 Kbps | Tous les modes |
| Internet | Complet | 720p | H.264 | 1500 Kbps | Tous les modes |
| CircleLink | Non (audio uniquement) | — | — | 64 Kbps | Paquets de sync uniquement |

Si le seul transport disponible est BLE ou CircleLink, le service d'appel vidéo se dégrade automatiquement en appel vocal.

### 10.2. Codecs vidéo

| Valeur d'énumération | Codec | Cas d'usage |
|---------------------|-------|-------------|
| 0 | H.264 | Par défaut. Largement supporté, bonne compression. |
| 1 | H.265 | Meilleure compression. Utilisé sur NearLink (à bande passante limitée). |
| 2 | VP8 | Alternative libre de redevances. |

### 10.3. Résolutions vidéo

| Valeur d'énumération | Résolution | Débit typique |
|---------------------|-----------|---------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. Flux d'appel vidéo P2P

1. **Vérification des capacités** : L'initiateur interroge `GetVideoCapabilityAsync(peerUhid)` pour déterminer le meilleur transport, la résolution maximale et le codec recommandé.
2. **Offre** : L'initiateur envoie un paquet `VideoSignaling` (type 28) avec `SignalType = Offer`, incluant le codec préféré, la résolution maximale et le débit maximal.
3. **Réponse/Rejet** : L'appelé répond avec `SignalType = Answer` (négociant le codec au plus petit dénominateur commun) ou `SignalType = Reject`.
4. **Appel actif** : Les deux nœuds échangent des paquets `VideoCall` (type 27) contenant des unités NAL H.264/H.265/VP8. Chaque trame inclut un numéro de séquence pour l'ordonnancement du tampon de gigue et un indicateur d'image clé.
5. **Partage d'écran** : L'une ou l'autre des parties peut activer le partage d'écran. `VideoSignaling` avec `SignalType = ScreenShareStart/Stop` notifie le pair. Les trames de partage d'écran utilisent `PacketType.ScreenShare` (type 32) mais le même pipeline de traitement.
6. **Fin d'appel** : L'une ou l'autre des parties envoie `VideoSignaling` avec `SignalType = Bye`.

Toutes les charges utiles de signalisation et de trames sont chiffrées avec Signal Protocol (session X3DH). La charge utile chiffrée est sérialisée en JSON-encodé `EncryptedPayload` dans le champ `MeshPacket.Payload`.

### 10.5. Machine à états d'appel vidéo

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

États : `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. Vidéo de groupe

Les sessions vidéo de groupe supportent un nombre illimité de participants. La topologie est sélectionnée dynamiquement en fonction du nombre de participants :

- **FullMesh** (2-3 participants) : Chaque participant envoie un flux à chaque autre participant. Simple, faible latence.
- **SFU** (4+ participants, seuil : `SfuThresholdParticipants = 4`) : Un nœud est élu comme relais SFU. Chaque participant envoie un flux au relais, qui le distribue à tous les autres. Le nœud relais reçoit des pourboires via la couche d'incitation.

Les changements de topologie sont automatiques : lorsque le 4e participant rejoint, la session passe de FullMesh à SFU. Lorsque les participants quittent et que le nombre tombe en dessous de 4, elle repasse en FullMesh.

Les trames vidéo de groupe utilisent `PacketType.VideoFrame` (type 31). En mode SFU, les trames sont envoyées à l'UHID du nœud relais, qui les rediffuse.

### 10.7. Tampon de gigue

Le tampon de gigue vidéo fonctionne indépendamment du tampon de gigue vocal (qui gère les trames Opus de 20 ms) :

- **Plage** : 60 ms minimum, 500 ms maximum.
- **Profondeur adaptative** : Suit la gigue inter-trames via une moyenne mobile exponentielle (EMA). La profondeur du tampon = 2 × l'estimation de la gigue, écrêtée à [60, 500] ms.
- **Abandon tenant compte des images clés** : Lorsque le tampon déborde, les trames non-clés (P/B) sont abandonnées en premier. Les trames I (images clés) ne sont jamais abandonnées — elles sont nécessaires pour la récupération du décodeur.
- **Gestion des lacunes** : Lorsqu'un gap de séquence est détecté, le tampon passe à la prochaine image clé disponible plutôt que d'attendre indéfiniment.

### 10.8. Types de signalisation vidéo

| Valeur d'énumération | Type | Description |
|---------------------|------|-------------|
| 0 | Offer | Initiation d'appel vidéo avec préférence de codec/résolution |
| 1 | Answer | Acceptation d'appel avec paramètres négociés |
| 2 | Reject | Rejet d'appel |
| 3 | Bye | Terminaison d'appel |
| 4 | Upgrade | Demande de qualité supérieure (ex. : transport amélioré) |
| 5 | Downgrade | Demande de qualité inférieure (ex. : chute de bande passante) |
| 6 | ScreenShareStart | Le pair a commencé à partager son écran |
| 7 | ScreenShareStop | Le pair a arrêté de partager son écran |

### 10.9. Modèle de chiffrement

| Mode | Chiffrement | Distribution des clés |
|------|------------|----------------------|
| Appel vidéo P2P | Signal Protocol par trame | Accord de clé X3DH |
| Vidéo de groupe | Clé de canal de groupe (AES-GCM) | Distribuée via Signal Protocol lors de la création de session |
| Partage d'écran | Identique au mode d'appel parent | Hérité de la session d'appel vidéo |

---

## 11. Regarder ensemble

> **Statut au 2026-05-05 — conception + échafaudage C#, même maturité que
> § 10.** Les types de paquets `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) sont définis sur le fil et
> testés par fixture. `AetherNet.Streaming.WatchTogetherService` fournit le
> squelette de coordination (état de session, propagation de commande de sync via
> `IMeshSender`, assistants de compensation RTT) ; l'ingestion BitTorrent, le
> règlement ChipIn SDPKT, et la récupération de fragments depuis les pairs ne sont
> implémentés dans aucun langage. Traiter la prose ci-dessous comme le protocole
> cible ; le document de conception prospective à
> `docs/adaptive-secure-streaming-spec.md` couvre le même terrain avec plus de
> détails.

Regarder ensemble permet la lecture multimédia synchronisée à travers un groupe de pairs maillés. L'hôte a le contrôle exclusif de la lecture (lecture, pause, saut, vitesse). Les commandes de sync incluent des horodatages d'horloge murale pour la compensation RTT.

### 11.1. Modes de visionnage

| Valeur d'énumération | Mode | Flux de données | Exigence de transport |
|---------------------|------|-----------------|-----------------------|
| 0 | SharedFile | Paquets de sync uniquement (< 100 octets chacun) | Tout (fonctionne sur BLE) |
| 1 | StreamFromHost | Transfert de fragments P2P (réutilise P2pContentService) | WiFi Direct ou Internet |
| 2 | BitTorrent | Essaim maillé + externe via nœuds passerelles | WiFi Direct ou Internet |

### 11.2. Mode SharedFile

Les deux participants ont le même fichier (correspondance par hachage de contenu SHA-256). Seuls les paquets `WatchSync` sont échangés. C'est le mode le plus économe en bande passante et fonctionne sur BLE.

1. L'hôte crée une session de visionnage avec `contentHash` (SHA-256 du fichier).
2. Les participants rejoignent et signalent `IsReady = true` lorsque leur lecteur est chargé.
3. La session commence lorsque TOUS les participants signalent être prêts.
4. L'hôte envoie des commandes lecture/pause/saut/vitesse sous forme de paquets `WatchSync` (type 29).
5. Les récepteurs appliquent la compensation RTT : `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. Mode StreamFromHost

Seul l'hôte a le fichier. L'hôte génère un `ContentManifest` (réutilisant le système de contenu P2P) et les participants téléchargent les fragments via le réseau maillé.

- La sélection de fragments utilise la stratégie `SequentialFromPosition` (pas `RarestFirst`) : priorise les fragments en avance sur la position de lecture actuelle, puis remplit les trous pour la diffusion en essaim.
- Cible de tampon : 30 secondes en avance (`WatchTogetherBufferAheadSeconds`).
- Pause automatique : si le tampon d'UN participant tombe en dessous de 10 secondes (`WatchTogetherMinBufferSeconds`), la session met automatiquement en pause tous les participants avec une commande de sync `BufferUnderrun`. La lecture reprend lorsque tous les participants ont un tampon suffisant (`BufferReady`).
- Au fur et à mesure que les spectateurs téléchargent des fragments, ils deviennent des sources pour les autres spectateurs (essaim de type BitTorrent au sein du réseau maillé).

### 11.4. Mode BitTorrent

Un participant partage un fichier `.torrent` ou un lien magnet dans le chat de groupe. Le paquet `TorrentMetadata` (type 34) distribue les informations du torrent à tous les participants de la session.

**Pont réseau maillé-essaim :**
- Les nœuds passerelles (nœuds avec internet) téléchargent des pièces depuis l'essaim BitTorrent externe.
- Les nœuds passerelles re-chiffrent les pièces téléchargées pour la distribution maillée et les diffusent aux pairs maillés.
- Les pairs maillés sans internet reçoivent des pièces des nœuds passerelles et les uns des autres.
- Le moteur de contenu P2P traduit entre le modèle de pièces BitTorrent et le modèle de fragments Aether.

Une fois suffisamment de contenu mis en tampon, la lecture en mode « regarder ensemble » commence en utilisant le même protocole de sync que le mode SharedFile.

### 11.5. Machine à états de session de visionnage

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

États : `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. Types de commandes de sync

| Valeur d'énumération | Type | Description |
|---------------------|------|-------------|
| 0 | Play | Reprendre la lecture à la position spécifiée |
| 1 | Pause | Mettre en pause à la position spécifiée |
| 2 | Seek | Sauter à la position spécifiée |
| 3 | Speed | Changer la vitesse de lecture |
| 4 | BufferUnderrun | Pause automatique — le tampon d'un participant est à un niveau critique |
| 5 | BufferReady | Reprise — tous les participants ont un tampon suffisant |

### 11.7. Compensation RTT

Les commandes de sync incluent un champ `WallClockMs` (millisecondes depuis l'époque Unix). Lorsqu'un récepteur traite une commande de sync :

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. Pour les commandes Play et BufferReady : `adjustedPosition = commandPosition + networkDelay`
4. Pour les commandes Pause et Seek : la position est appliquée exactement (aucun ajustement nécessaire car la lecture s'arrête/saute).

Cela garantit que tous les participants sont synchronisés à moins de la moitié du RTT réseau.

### 11.8. Réactions

Les participants peuvent réagir au contenu pendant la lecture :

- **Réactions emoji** : Paquet `WatchReaction` (type 30) avec `Type = Emoji`, portant la chaîne emoji et la position média au moment de la réaction.
- **Commentaires vocaux** : Paquet `WatchReaction` avec `Type = VoiceComment`, portant des données audio encodées en Opus (maximum 10 secondes). Les données vocales sont incluses dans le champ `VoiceData` de la réaction.

Les réactions sont diffusées à tous les participants de la session. Elles sont horodatées à la position média, permettant un affichage synchronisé à la lecture.

### 11.9. ChipIn — Acquisition de contenu en groupe

ChipIn permet aux membres d'un groupe de mettre en commun des fonds (en ZAR, réglés via des portefeuilles SDPKT à travers LedgerAPI) pour acquérir collectivement du contenu destiné à un visionnage en groupe.

**Machine à états :**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

États : `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**Flux :**
1. L'initiateur crée un `ChipInPool` avec le montant cible et la description du contenu.
2. Les participants contribuent des montants via des transactions de portefeuille SDPKT.
3. Lorsque `CollectedAmount >= TargetAmount`, l'état passe à `Funded`.
4. Le système acquiert le contenu (ex. : lance un téléchargement BitTorrent).
5. Une fois le contenu disponible, l'état passe à `Acquired` et le visionnage en groupe peut commencer.

Chaque contribution est enregistrée avec un identifiant de transaction SDPKT pour la piste d'audit.

### 11.10. Modèle de chiffrement

| Mode | Chiffrement | Distribution des clés |
|------|------------|----------------------|
| Commandes de sync de visionnage | Clé de canal/conversation | Session Signal Protocol existante |
| Fragments de contenu (StreamFromHost) | Clé de contenu par manifeste | Distribuée via Signal Protocol |
| Pièces BitTorrent | Re-chiffré à l'ingestion | La passerelle télécharge le texte clair depuis l'essaim, chiffre pour le réseau maillé |
| Réactions de visionnage | Clé de session | Dérivée de la clé de conversation |

### 11.11. Indicateurs de fonctionnalité

Toutes les fonctionnalités vidéo et de visionnage en groupe sont contrôlées par des indicateurs de fonctionnalité (tous désactivés par défaut) :

| Indicateur | Parent | Description |
|------------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | Appels vidéo P2P et de groupe |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | Sessions vidéo multi-parties |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | Partage d'écran dans les appels vidéo |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | Lecture multimédia synchronisée |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | Réactions emoji et vocales |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | Acceptation de fichiers BitTorrent pour la distribution maillée |

Les indicateurs de fonctionnalité ont des dépendances parentales : un indicateur enfant ne peut être activé que si son parent est également activé. Cela permet un déploiement progressif.

---

## 12. Couche de sécurité et de confidentialité

> Ajoutée dans la 2.3.0. Implémentation de référence : `src/AetherNet.Security/Backup/` (phrase de récupération), `src/AetherNet.Security/Privacy/` (protection contre le pistage BLE, effacement de panique) et `src/AetherNet.Security/Sync/` (synchronisation multi-appareils). Vecteurs d'octets multi-langages : `fixtures/bip39/`, `fixtures/bleprivacy/`, `fixtures/panicwipe/`, `fixtures/sync/`.

Cette couche est additive et indépendante de la suite de paquets du §2. Seules la **synchronisation multi-appareils** (§12.1–12.2) et le **schéma d'adresse de protection contre le pistage BLE** (§12.3) possèdent des formats d'octets / sur les ondes ; la **sauvegarde par phrase de récupération** (§12.4) et l'**effacement de panique** (§12.5) sont purement locaux et sont spécifiés ici par souci d'exhaustivité. Tous sont implémentés de manière identique octet par octet dans les huit langages, avec l'unique exception de la signature Ed25519 notée au §12.1.

### 12.1. DeviceLink (appairage d'appareils)

Un `DeviceLink` est une assertion signée Ed25519 attestant que la clé publique d'un appareil appartient à une identité, utilisée pour appairer les propres appareils d'un utilisateur pour la synchronisation multi-appareils. Le **corps signé** est :

| Déc | Champ | Type | Taille | Remarques |
|-----|-------|------|--------|-----------|
| 0 | format_version | uint8 | 1 | `0x01` ; rejeter toute autre valeur à la lecture |
| 1 | device_id_len | uint16, little-endian | 2 | Longueur en octets UTF-8 de `device_id` |
| 3 | device_id | octets UTF-8 | N | l'identifiant de l'appareil lié |
| 3+N | device_public_key | octets | 32 | la clé publique Ed25519 de l'appareil lié |
| 35+N | issued_at_ms | int64, little-endian | 8 | Millisecondes depuis l'époque Unix |

Le `DeviceLink` sérialisé est le corps signé suivi d'une **signature Ed25519 de 64 octets** sur ce corps, calculée avec la clé privée d'*identité*. La vérification recalcule le corps et contrôle la signature par rapport à la clé publique d'identité.

> **Exception de parité d'octets de la signature.** Le corps signé et le résultat de la vérification sont identiques dans les huit langages, et les 64 **octets** de signature sont identiques octet par octet dans sept d'entre eux. CryptoKit d'Apple randomise les signatures Ed25519 (signature « hedged » de la RFC 8032 §8), de sorte que la signature Swift diffère à chaque appel tout en restant valide et vérifiable de manière croisée. L'interopérabilité DOIT reposer sur la *vérification*, jamais sur la comparaison des octets de signature.

### 12.2. SyncRecord (enveloppe de synchronisation « dernier écrit gagnant »)

Un `SyncRecord` est une modification répliquée de l'état multi-appareils propre à un utilisateur, réconciliée selon le principe « dernier écrit gagnant ». Les enregistrements circulent chiffrés de bout en bout à l'intérieur du chemin DTN/maillé existant (`encrypted_payload` est un texte chiffré opaque) — ils ne constituent **pas** un nouveau type de `MeshPacket`.

| Déc | Champ | Type | Taille | Remarques |
|-----|-------|------|--------|-----------|
| 0 | format_version | uint8 | 1 | `0x01` |
| 1 | record_id | UUID, RFC 4122 big-endian | 16 | même convention big-endian qu'au §2.1 |
| 17 | op | uint8 | 1 | `0`=Upsert, `1`=Delete, `2`=Read ; rejeter > 2 |
| 18 | logical_clock | int64, little-endian | 8 | compteur monotone par appareil |
| 26 | created_at_ms | int64, little-endian | 8 | Millisecondes depuis l'époque Unix |
| 34 | device_id_len | uint16, little-endian | 2 | Longueur en octets UTF-8 |
| 36 | device_id | octets UTF-8 | N | appareil d'origine |
| 36+N | item_id_len | uint16, little-endian | 2 | Longueur en octets UTF-8 |
| 38+N | item_id | octets UTF-8 | M | clé logique en cours de synchronisation |
| 38+N+M | payload_len | int32, little-endian | 4 | longueur du texte chiffré ; rejeter les valeurs négatives |
| 42+N+M | encrypted_payload | octets | payload_len | texte chiffré opaque de bout en bout |

**Réconciliation (dernier écrit gagnant).** Entre deux enregistrements pour le même `item_id`, le gagnant est choisi en comparant, dans l'ordre jusqu'à ce que l'un diffère : `created_at_ms`, puis `logical_clock`, puis `device_id` (comparaison ordinale d'octets), puis `record_id` (comparaison d'octets big-endian). L'ordre est total et déterministe, de sorte que chaque appareil converge vers le même gagnant quel que soit l'ordre d'arrivée.

### 12.3. Protection contre le pistage BLE

Deux dérivations permettent à un appareil d'émettre des annonces sans être traçable par un scanner passif. Les deux sont des fonctions pures épinglées à `fixtures/bleprivacy/` ; leur émission sur les ondes relève de la pile BLE hôte.

- **UUID de service rotatif.** `window = floor(unix_time_seconds / 900)` (une époque de 15 minutes). L'UUID de service 128 bits annoncé correspond aux 16 premiers octets de `HMAC-SHA256(ble_rotation_key, LE_int64(window))`. Un scanner qui journalise l'UUID ne peut pas relier deux fenêtres sans la clé de rotation.
- **Adresse privée résoluble (RPA).** Selon la fonction Bluetooth `ah` : `hash = ah(IRK, prand)`, où `ah` est un AES-128 sur le `prand` de 24 bits (complété à 128 bits) et où les 24 bits de poids faible sont retenus. L'adresse de 48 bits est `hash(24) || prand(24)`, avec les deux bits de poids fort de `prand` fixés à `0b01` pour la marquer comme résoluble. Un pair détenant l'IRK résout l'adresse en recalculant `ah` et en comparant le hachage.

### 12.4. Sauvegarde par phrase de récupération (locale)

Une identité est une paire de clés Ed25519 dont la graine privée de 32 octets (256 bits) est encodée en une mnémonique **BIP-39 de 24 mots** sur la liste de mots anglaise officielle, avec la somme de contrôle SHA-256 standard (un mot mal saisi échoue à la somme de contrôle et est rejeté plutôt que de produire silencieusement une identité différente). Il s'agit de BIP-39 standard — vérifié par rapport aux vecteurs de test officiels de Trezor et reproduit octet par octet dans les huit langages — de sorte que la phrase restaure l'identité sur n'importe quel appareil sans serveur ni dépositaire. Il n'y a pas de format de fil ; la phrase ne touche jamais le réseau.

### 12.5. Effacement de panique (local)

Sous la contrainte, un **PIN de contrainte** — comparé à un `SHA-256(pin)` stocké en temps constant — déclenche un effacement sécurisé de tout le matériel de clé d'identité : chaque tampon est écrasé avec des octets aléatoires puis mis à zéro, sur un manifeste fixe de noms de clés d'identité (paire de clés d'identité, sel d'appareil, DRK, ainsi que la clé de rotation BLE / l'IRK du §12.3). Il n'y a pas de format de fil ; l'opération est entièrement locale à l'appareil.

---

## Annexe A : Référence des constantes

Toutes les constantes de protocole sont définies dans `ProtocolConstants` et sont reproduites ici pour référence :

### Routage
| Constante             | Valeur |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### Découverte BLE
| Constante                 | Valeur |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherNetBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Sécurité
| Constante                 | Valeur |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constante                  | Valeur |
|----------------------------|--------|
| SosTtl                     | 15     |
| SosPriority                | 255    |
| MaxSosBroadcastsPerHour    | 3      |

### DTN
| Constante                 | Valeur |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transport
| Constante                 | Valeur  |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 8192    |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Battement de cœur
| Constante                     | Valeur |
|-------------------------------|--------|
| HeartbeatIntervalSeconds      | 300    |
| NodeOfflineThresholdSeconds   | 900    |

### Présence
| Constante                         | Valeur |
|-----------------------------------|--------|
| PresenceBeaconIntervalMs          | 15000  |
| PresenceTimeoutSeconds            | 60     |
| EphemeralIdRotationMinutes        | 15     |
| ProximityEventDebounceSeconds     | 30     |

### Voix
| Constante                 | Valeur |
|---------------------------|--------|
| VoiceFrameDurationMs      | 20     |
| PttMaxDurationSeconds     | 60     |
| JitterBufferMinMs         | 20     |
| JitterBufferMaxMs         | 200    |
| OpusDefaultBitrateKbps    | 64     |
| MaxGroupVoiceMembers      | 8      |

### Diffusion en continu
| Constante                   | Valeur |
|-----------------------------|--------|
| DefaultSegmentDurationMs    | 3000   |
| MaxStreamTreeFanout         | 4      |
| MaxStreamRelayHops          | 3      |
| StreamSegmentBufferSize     | 10     |
| BleAudioBitrateKbps        | 64     |
| WifiDirectVideoBitrateKbps  | 500    |

### Vidéo
| Constante                      | Valeur |
|--------------------------------|--------|
| VideoFrameDurationMs           | 33     |
| VideoJitterBufferMinMs         | 60     |
| VideoJitterBufferMaxMs         | 500    |
| WatchTogetherBufferAheadSeconds| 30     |
| WatchTogetherMinBufferSeconds  | 10     |
| NearLink360pBitrateKbps       | 800    |
| Internet1080pBitrateKbps      | 3000   |
| SfuThresholdParticipants       | 4      |
| ScreenShareFrameDurationMs     | 100    |

---

## Annexe B : Glossaire

| Terme | Définition |
|-------|------------|
| **UHID** | Identifiant matériel universel. Chaîne unique identifiant un nœud maillé, dérivée de l'identité de l'appareil et des clés cryptographiques. |
| **RREQ** | Demande de route. Paquet de diffusion utilisé pour découvrir un chemin vers un nœud de destination. |
| **RREP** | Réponse de route. Paquet unicast envoyé en retour le long de la route inverse établie par une RREQ. |
| **IRK** | Clé de résolution d'identité. Clé de 128 bits utilisée pour générer et résoudre les adresses privées résolvables BLE. |
| **RPA** | Adresse privée résolvable. Adresse BLE de 6 octets qui pivote périodiquement mais peut être résolue par les pairs détenant l'IRK de l'expéditeur. |
| **X3DH** | Extended Triple Diffie-Hellman. Protocole d'accord de clé permettant l'établissement de session asynchrone. |
| **DTN** | Réseau tolérant aux délais. Paradigme de stockage et retransmission pour les environnements avec une connectivité intermittente. |
| **Gateway** | Nœud maillé disposant d'une connectivité internet qui fait le pont entre le trafic maillé et les services basés sur IP. |
| **HKDF** | Fonction de dérivation de clés basée sur HMAC. Utilisée pour dériver plusieurs clés à partir d'un seul secret partagé. |
| **Paquet de pré-clés** | Ensemble de clés publié permettant à un expéditeur d'établir une session chiffrée sans que le destinataire soit en ligne. |
| **SFU** | Unité de transfert sélectif. Nœud relais qui reçoit un flux vidéo de chaque expéditeur et le distribue à tous les autres participants, réduisant la bande passante de téléversement par nœud. |
| **ChipIn** | Mécanisme de financement de groupe où les participants mettent en commun des fonds SDPKT pour acquérir collectivement du contenu destiné à un visionnage en groupe. |
| **NAL** | Couche d'abstraction réseau. Format d'encapsulation utilisé par les codecs H.264 et H.265 pour paquetiser les trames vidéo. |

---

## Annexe C : Références

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, juillet 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, novembre 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, novembre 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, mai 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," décembre 2016 (Resolvable Private Address, section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, novembre 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
