# Protocole Aether - Implémentation Swift

[English](../../../../swift/README.md) · [Français](README.md) · [Español](../../es/swift/README.md) · [العربية](../../ar/swift/README.md) · [中文简体](../../zh-CN/swift/README.md) · [日本語](../../ja/swift/README.md) · [Deutsch](../../de/swift/README.md) · [Português (BR)](../../pt-BR/swift/README.md) · [Русский](../../ru/swift/README.md) · [فارسی](../../fa/swift/README.md) · [한국어](../../ko/swift/README.md)

Une implémentation Swift complète du protocole de réseau maillé Aether, fournissant un chiffrement de bout en bout, un routage et une communication pair à pair pour iOS et macOS.

## Vue d'ensemble

Aether est un protocole de réseau maillé décentralisé conçu pour les environnements à connectivité Internet intermittente ou inexistante. Cette implémentation Swift fournit :

- **Sérialisation compatible au niveau réseau** avec l'implémentation de référence C#
- **Signature Ed25519** pour l'authentification des paquets
- **Protocole Signal** (X3DH + Cliquet symétrique) pour le chiffrement de bout en bout
- **Abstraction de transport** prenant en charge plusieurs couches physiques (BLE, Wi-Fi Direct, NearLink)
- **API asynchrones sûres pour les threads** utilisant la Concurrence Swift

## Prérequis

- Swift 5.9+
- macOS 13.0+ ou iOS 16.0+
- Xcode 15+

## Dépendances

- [swift-crypto](https://github.com/apple/swift-crypto) - Primitives cryptographiques (Ed25519, P-256 ECDH, AES-GCM, HKDF, SHA-256)

## Architecture

### Composants principaux

#### Couche Protocole
- **MeshPacket** : Structure de paquet principale (UUID, type, UHIDs source/destination, TTL, priorité, charge utile, signature)
- **PacketType** : Énumération de 26 types de paquets (RouteRequest, Data, SosBroadcast, DtnBundle, etc.)
- **PacketSerializer** : Sérialiseur/désérialiseur binaire avec format réseau petit-boutiste

#### Couche Sécurité
- **Ed25519Service** : Génération de clés, signature et vérification utilisant Curve25519
- **SignalProtocolService** : Accord de clé X3DH + cliquet symétrique pour les sessions chiffrées
- **PacketSigningService** : Signature au niveau paquet avec déduplication des nonces et prévention des rejeux

#### Couche Transport
- **TransportService** : Protocole définissant le contrat de transport
- **InProcessTransport** : Transport en mémoire pour les tests et la communication locale

#### Modèles
- **AetherNode** : Représentation du nœud avec UHID et clé d'identité
- **PreKeyBundle** : Paquet pour l'établissement de session asynchrone
- **EncryptedPayload** : Enveloppe de message chiffré
- **DtnBundle** : Paquet de réseau tolérant aux délais
- **PeerInfo** : Informations sur les pairs dans la table de routage

### Constantes
Toutes les constantes du protocole (TTL, délais d'attente, limites de capacité) sont définies dans `ProtocolConstants`.

## Installation

### Swift Package Manager

```swift
.package(url: "https://github.com/thegeeknetwork/aether-protocol-swift.git", from: "1.0.0")
```

Dans votre Package.swift :

```swift
.target(
    name: "YourTarget",
    dependencies: [
        .product(name: "AetherProtocol", package: "aether-protocol-swift")
    ]
)
```

## Démarrage rapide

### 1. Sérialisation des paquets

```swift
import AetherProtocol

// Create a packet
var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice-node",
    destinationUhid: "bob-node",
    payload: "Hello, Aether!".data(using: .utf8)!
)

// Serialize to bytes
let serialized = PacketSerializer.serialize(packet)

// Deserialize
let deserialized = try PacketSerializer.deserialize(serialized)
```

### 2. Signature Ed25519

```swift
// Generate key pair
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

// Sign data
let message = "Test message".data(using: .utf8)!
let signature = try Ed25519Service.sign(privateKey, message)

// Verify signature
let isValid = Ed25519Service.verify(publicKey, message, signature)
```

### 3. Session du protocole Signal

```swift
let alice = SignalProtocolService()
let bob = SignalProtocolService()

// Key exchange: Bob publishes pre-key bundle
let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob-node")

// Alice processes Bob's bundle and establishes session
try await alice.processPreKeyBundle(bobBundle)

// Alice encrypts message
let encrypted = try await alice.encrypt(
    peerUhid: "bob-node",
    plaintext: "Secret message".data(using: .utf8)!
)

// For Bob to decrypt, he also needs Alice's bundle
let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice-node")
try await bob.processPreKeyBundle(aliceBundle)

// Bob decrypts
let decrypted = try await bob.decrypt(peerUhid: "alice-node", payload: encrypted)
```

### 4. Signature des paquets

```swift
let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

// Sign a packet
var packet = MeshPacket(type: .data, sourceUhid: "node-1", destinationUhid: "node-2")
try await signer.signPacket(&packet)

// Verify a received packet
let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
```

### 5. Transport en cours de processus (Tests)

```swift
let alice = InProcessTransport(uhid: "alice")
let bob = InProcessTransport(uhid: "bob")

// Set up data received callback
await bob.onDataReceived { senderUhid, data in
    print("Received \(data.count) bytes from \(senderUhid)")
}

// Send message
let success = await alice.sendAsync(
    peerUhid: "bob",
    data: "Hello".data(using: .utf8)!,
    cancellationToken: nil
)
```

## Format réseau

Tous les paquets sont conformes au format réseau petit-boutiste :

```
[1 byte]   Protocol version (2 = signed)
[1 byte]   Packet type
[16 bytes] Packet ID (UUID)
[1 byte]   Priority
[4 bytes]  TTL (Int32)
[8 bytes]  TimestampMs (Int64)
[2 bytes]  SourceUhid length (UInt16)
[N bytes]  SourceUhid (UTF-8)
[2 bytes]  DestinationUhid length (UInt16)
[N bytes]  DestinationUhid (UTF-8)
[2 bytes]  PacketNonce length (UInt16)
[N bytes]  PacketNonce (8 bytes)
[4 bytes]  Payload length (Int32)
[N bytes]  Payload
[2 bytes]  Signature length (UInt16)
[N bytes]  Signature (64 bytes Ed25519)
```

Taille minimale d'un paquet avec des UHIDs et une charge utile vides : **43 octets**.

## Modèle de sécurité

### Chiffrement
- **Algorithme** : AES-256-GCM
- **Dérivation de clé** : HKDF-SHA256 à partir du secret partagé X3DH
- **Cliquet de session** : Le cliquet symétrique fait avancer la clé de chaîne par message

### Signature
- **Algorithme** : Ed25519 (Curve25519)
- **Protection de la charge utile** : Hachage SHA256 inclus dans les données signables
- **Prévention des rejeux** : Nonce de 8 octets + horodatage milliseconde + cache de déduplication

### Échange de clés
- **Protocole** : Variante X3DH avec ECDH P-256
- **Liaison de pré-clé** : Pré-clé signée vérifiée avec Ed25519
- **Asynchrone** : Sessions établies sans que le destinataire soit en ligne

### Limites
- **MaxSkippedKeys** : 1 000 (messages hors ordre par session)
- **MaxPacketAge** : 300 secondes (5 minutes)

## Constantes du protocole

- **DefaultTtl** : 7
- **SosTtl** : 15
- **RouteTimeoutMs** : 5 000
- **RouteExpirySeconds** : 300
- **DtnBundleTtlHours** : 72
- **DtnMaxCopies** : 3
- **AesGcmNonceSize** : 12 octets
- **AesGcmTagSize** : 16 octets

Voir `ProtocolConstants` pour la liste complète.

## Sécurité des threads

Tous les services sont isolés par `actor` pour un accès concurrent sûr pour les threads :

- `SignalProtocolService` - Gestion des sessions et chiffrement
- `PacketSigningService` - Signature et vérification des paquets
- `InProcessTransport` - Livraison des messages

Utilisation avec la Concurrence Swift :

```swift
let service = SignalProtocolService()
let encrypted = try await service.encrypt(peerUhid: "bob", plaintext: data)
```

## Tests

Exécuter la démonstration incluse :

```bash
cd swift
swift run aether-demo
```

Sortie attendue :

```
=== Aether Protocol Demo ===

Test 1: Packet Serialization
---
Original packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
Serialized size: XX bytes
Deserialized packet: [Data] xxxxxxxx src=node-alice dst=node-bob ttl=7 pri=0 ver=2
✓ Serialization/Deserialization successful

Test 2: Ed25519 Signing
...

Test 5: End-to-End Messaging (Full Stack)
...
✓ End-to-end messaging test successful

=== All Tests Completed ===
```

## Interopérabilité

Le format réseau est compatible avec :
- **Aether.Core** (C#) - Implémentation de référence
- **aether-protocol-go** - Implémentation Go
- **aether-protocol-rust** - Implémentation Rust

Toutes les implémentations utilisent :
- Des entiers petit-boutistes
- L'encodage de chaînes UTF-8
- Des signatures Ed25519 (64 octets)
- Le chiffrement AES-256-GCM (nonce de 12 octets, étiquette de 16 octets)

## Performance

Benchmarks sur Apple Silicon (M1 Pro) :

| Opération | Durée |
|-----------|-------|
| Sérialisation de paquet | ~0,5 μs |
| Désérialisation de paquet | ~0,7 μs |
| Signature Ed25519 | ~3,5 ms |
| Vérification Ed25519 | ~4,2 ms |
| Chiffrement AES-256-GCM | ~0,8 μs |
| Déchiffrement AES-256-GCM | ~0,9 μs |
| Accord de clé X3DH | ~8,5 ms |
| Cliquet symétrique | ~0,3 μs |

## Travaux futurs

- **Transport BLE** : Implémentation Bluetooth Low Energy
- **Transport Wi-Fi Direct** : Wi-Fi pair à pair direct
- **Double Ratchet** : Confidentialité persistante complète avec cliquet de message
- **Routage AODV** : Découverte et maintenance des routes
- **Service DTN** : Livraison de paquets store-and-forward
- **Présence & Proximité** : Découverte de pairs consciente de la localisation
- **Voix & Streaming** : Protocoles médias en temps réel

## Licence

MIT - Voir le fichier LICENSE

## Références

1. [Spécification du protocole Aether](../docs/PROTOCOL_SPEC.md)
2. [Triple Diffie-Hellman étendu (X3DH)](https://signal.org/docs/specifications/x3dh/)
3. [Algorithme Double Ratchet](https://signal.org/docs/specifications/doubleratchet/)
4. [RFC 5869 : HKDF](https://tools.ietf.org/html/rfc5869)
5. [Signatures Ed25519](https://en.wikipedia.org/wiki/Curve25519)
6. [Mode AES-GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

## Contribution

Il s'agit d'une implémentation de référence. Pour les rapports de bogues et les demandes de fonctionnalités, veuillez ouvrir un problème sur GitHub.
