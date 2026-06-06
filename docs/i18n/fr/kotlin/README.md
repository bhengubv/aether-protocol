# Protocole Aether - Implémentation Kotlin

[English](../../../../kotlin/README.md) · [Français](README.md) · [Español](../../es/kotlin/README.md) · [العربية](../../ar/kotlin/README.md) · [中文简体](../../zh-CN/kotlin/README.md) · [日本語](../../ja/kotlin/README.md) · [Deutsch](../../de/kotlin/README.md) · [Português (BR)](../../pt-BR/kotlin/README.md) · [Русский](../../ru/kotlin/README.md) · [فارسی](../../fa/kotlin/README.md) · [한국어](../../ko/kotlin/README.md)

Une implémentation Kotlin complète et prête pour la production du protocole de réseau maillé Aether, avec une compatibilité totale du format filaire entre langages vis-à-vis de l'implémentation de référence C#.

## Vue d'ensemble

Aether est un protocole de réseau maillé décentralisé destiné aux environnements disposant d'une connectivité internet intermittente ou absente. Cette implémentation Kotlin fournit :

- **Compatibilité du format filaire** avec C# (la sérialisation binaire des paquets correspond exactement)
- **Signature Ed25519** pour l'authentification et l'intégrité des paquets
- **Protocole Signal** pour le chiffrement de bout en bout (accord de clé X3DH, cliquet symétrique, AES-256-GCM)
- **Accord de clé ECDH P-256** pour l'établissement de session
- **Sérialisation/désérialisation de paquets** avec des entiers multi-octets en petit-boutiste
- **Protection contre les rejeux** par déduplication de nonce
- **Abstraction de transport** pour BLE, Wi-Fi Direct et la messagerie en cours de processus

## Structure du projet

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherMeshNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## Compilation

### Prérequis

- JDK 17 ou supérieur
- Gradle 8.0 ou supérieur

### Compiler

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Exécuter la démonstration

```bash
./gradlew run
```

La démonstration illustre :
1. Génération de paire de clés Ed25519
2. Création et échange de bundles de pré-clés
3. Établissement de session du protocole Signal
4. Signature de paquets avec Ed25519
5. Sérialisation/désérialisation de paquets
6. Chiffrement et déchiffrement de messages
7. Protection contre les rejeux
8. Messagerie par transport en cours de processus

## Composants clés

### 1. Sérialisation de paquets (`PacketSerializer`)

Format filaire (petit-boutiste) :
- Version du protocole (1 octet)
- Type de paquet (1 octet)
- Identifiant de paquet / UUID (16 octets)
- Priorité (1 octet)
- TTL (4 octets, int32)
- TimestampMs (8 octets, int64)
- SourceUhid (préfixe de longueur sur 2 octets + octets UTF-8)
- DestinationUhid (préfixe de longueur sur 2 octets + octets UTF-8)
- PacketNonce (préfixe de longueur sur 2 octets + octets)
- Charge utile (préfixe de longueur sur 4 octets + octets)
- Signature (préfixe de longueur sur 2 octets + octets)

Entièrement compatible avec `PacketSerializer` en C#.

### 2. Signature Ed25519 (`Ed25519Service`, `PacketSigning`)

- **Génération de clé** : graine de clé privée sur 32 octets, clé publique sur 32 octets
- **Signature** : signatures de 64 octets sur des données signables déterministes
- **Vérification** : remplace ECDSA P-256 pendant la période de migration
- **Format des données signables** : correspond exactement à la spécification C# (nonce de paquet, horodatage, type, UHID, hachage de charge utile, TTL, priorité)
- **Protection contre les rejeux** : déduplication de nonce avec TTL de 5 minutes

### 3. Protocole Signal (`SignalProtocol`)

Implémente l'accord de clé X3DH avec cliquet symétrique :

**Établissement de session :**
- Récupère le bundle de pré-clés du pair
- Vérifie la signature du bundle avec Ed25519
- Effectue X3DH : DH(identité locale, pré-clé signée distante) + DH(identité locale, pré-clé distante)
- Dérive la clé racine et les clés de chaîne via HKDF-SHA256

**Chiffrement/Déchiffrement :**
- Cliquet symétrique avec HMAC-SHA256
- AES-256-GCM avec nonce aléatoire de 12 octets
- Clés par message avec confidentialité persistante
- Gestion des messages hors ordre (cache de clés ignorées, max 1000 clés)

**Paramètres :**
- Info de dérivation de clé racine : `"aether-root-v1"`
- Info de dérivation de chaîne d'envoi : `"aether-chain-send-v1"`
- Info de dérivation de chaîne de réception : `"aether-chain-recv-v1"`
- Sel de clé de message : `0x01`, sel de clé de chaîne : `0x02`

### 4. Abstraction de transport (`TransportService`)

Interface pour les transports physiques (BLE, Wi-Fi Direct, etc.) :

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport :** implémentation de référence utilisant un `ConcurrentHashMap` global pour les tests et la démonstration.

### 5. Modèles de domaine (`Models.kt`)

- **AetherMeshNode** : identité de nœud avec UHID, clé publique, capacités, géohachage
- **PeerInfo** : pair connu avec score de fiabilité et horodatage de dernière vue
- **RouteEntry** : entrée de table de routage avec nombre de sauts et score de qualité
- **NodeCapabilities** : champ de bits (BLE, Wi-Fi Direct, Passerelle, Relais, SOS, Diffusion en continu, Voix, DTN)
- **DtnBundle** : bundle de stockage-et-retransmission avec expiration et comptage de copies

## Constantes du protocole

Constantes clés (depuis `Constants.kt`) :

| Catégorie | Constante | Valeur |
|-----------|-----------|--------|
| Paquet | DEFAULT_TTL | 7 |
| Paquet | PACKET_NONCE_SIZE | 8 |
| Sécurité | MAX_SKIPPED_KEYS | 1000 |
| Sécurité | AES_GCM_NONCE_SIZE | 12 |
| Sécurité | AES_GCM_TAG_SIZE | 16 |
| Routage | ROUTE_TIMEOUT_MS | 5000 |
| Routage | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Types de paquets

Les 23 types de paquets correspondent aux valeurs de l'énumération C# (1-23) :

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## Dépendances

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — prise en charge du format de clé
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await, Flow
- **org.slf4j:slf4j-api:2.0.9** — journalisation
- **kotlin-stdlib** — bibliothèque standard Kotlin

## Exemples d'utilisation

### Génération de clé

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Signature de paquet

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### Sérialisation de paquet

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Chiffrement par le protocole Signal

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## Compatibilité entre langages

Cette implémentation maintient une **compatibilité exacte du format filaire** avec l'implémentation de référence C# :

- Format binaire des paquets : disposition petit-boutiste identique
- Énumération des types de paquets : valeurs correspondant exactement à l'énumération C# (1-23)
- Signatures Ed25519 : compatibles avec NSec/libsodium
- ECDH P-256 : courbe standard, compatible entre les langages
- HKDF-SHA256 : implémentation standard RFC 5869
- AES-256-GCM : standard NIST avec nonce de 12 octets, balise de 16 octets

Les paquets sérialisés en Kotlin peuvent être désérialisés en C# et vice versa.

## Tests

L'implémentation comprend une démonstration complète (`Demo.kt`) qui couvre :

1. Génération de clé et export de clé publique
2. Génération et échange de bundles de pré-clés
3. Établissement de session via le protocole Signal
4. Création, signature et sérialisation de paquets
5. Désérialisation de paquets et vérification des signatures
6. Chiffrement et déchiffrement de messages
7. Prévention des attaques par rejeu
8. Messagerie par transport en cours de processus

Exécuter avec :
```bash
./gradlew run
```

## Considérations de sécurité

- **Effacement des clés** : tout le matériel cryptographique intermédiaire est effacé après utilisation via `CryptographicOperations.ZeroMemory` (équivalent Kotlin : `fill(0)`)
- **Protection contre les rejeux** : déduplication de nonce avec TTL de 5 minutes prévenant les attaques par rejeu
- **Confidentialité persistante** : clés par message dérivées du cliquet de chaîne
- **Gestion hors ordre** : cache de clés ignorées avec max 1000 clés pour prévenir l'épuisement mémoire
- **Authentification RREP** : paquets de réponse de route signés par le nœud de destination
- **Confidentialité des messages** : contenu des messages chiffré avec AES-256-GCM

## Extensions futures

L'implémentation fournit des points d'ancrage pour :

- **Transport BLE** (interface `TransportService`)
- **Transport Wi-Fi Direct** (même interface)
- **Routage épidémique DTN** (modèle `DtnBundle` prêt)
- **Diffusion SOS** (type de paquet défini)
- **Balises de présence** (type de paquet défini)
- **Voix et diffusion en continu** (types de paquets définis)
- **Double Ratchet** (lorsque des transports toujours actifs sont disponibles)

## Documentation du protocole

Spécification complète du protocole : `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## Licence

SPDX-License-Identifier: MIT
