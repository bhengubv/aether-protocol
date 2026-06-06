# Protocole maillé Aether - Implémentation TypeScript

[English](../../../../typescript/README.md) · [Français](README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](../../pt-BR/typescript/README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Une implémentation TypeScript/Node.js complète du protocole de réseau maillé Aether, entièrement compatible au niveau du format réseau avec l'implémentation de référence C#.

## Fonctionnalités

- **Sérialisation MeshPacket** : Format binaire réseau correspondant exactement à C# (entiers petit-boutistes, chaînes/tableaux préfixés par la longueur)
- **Signature Ed25519** : Utilisation de TweetNaCl pour la génération et la vérification de signatures
- **Protocole Signal** : Échange de clés X3DH avec dérivation de clé HKDF-SHA256 et chiffrement AES-256-GCM
- **Signature des paquets** : Construction complète des données signables selon la spécification du protocole (Section 2.3)
- **Transport en cours de processus** : Réseau simulé pour les tests et les démonstrations
- **Cliquet symétrique** : Avancement de la clé de chaîne HMAC-SHA256 avec prise en charge des messages hors ordre
- **Constantes du protocole** : Plus de 60 constantes de la Section A de PROTOCOL_SPEC

## Installation

```bash
npm install
```

## Utilisation

### Compilation

```bash
npm run build
```

### Exécuter la démonstration

```bash
npm run dev
```

La démonstration :
1. Crée 2 nœuds dans un réseau simulé en cours de processus
2. Génère des paires de clés Ed25519
3. Établit des sessions du protocole Signal
4. Crée, signe et vérifie un paquet
5. Sérialise et désérialise des paquets
6. Chiffre et déchiffre des messages
7. Envoie des paquets via la couche transport

### Exemples d'API

#### Création et signature de paquets

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Chiffrement avec le protocole Signal

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### Sérialisation des paquets

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### Transport en cours de processus

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## Conformité au protocole

### Format réseau

Tous les entiers multi-octets sont en **petit-boutiste** :
- Identifiant de paquet : UUID de 16 octets
- TTL, TimestampMs : int32/int64 LE
- Longueurs de chaînes : uint16 LE (pas uint32)
- Longueur de la charge utile : int32 LE

### Signature des paquets (Section 2.3)

Format des données signables :
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Protocole Signal (Section 4)

- **Échange de clés** : X3DH avec ECDH P-256
- **HKDF** : SHA256 avec salt="AetherMeshSignal"
- **Chaînes d'information** : "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Chiffrement** : AES-256-GCM avec nonce de 12 octets, étiquette de 16 octets
- **Cliquet de chaîne** : HMAC-SHA256 avec avancement de compteur

## Types de paquets

Les 23 types de paquets sont définis :
- RouteRequest (1) - Demande de route AODV
- RouteReply (2) - Réponse de route AODV
- Data (3) - Données applicatives
- Ack (4) - Accusé de réception de livraison
- SosBroadcast (5) - Diffusion d'urgence
- ... et 18 autres (voir la spécification du protocole)

## Fonctionnalités de sécurité

- **Signatures Ed25519** : Tous les paquets signés selon le protocole v2
- **AES-256-GCM** : Clés par message avec nonces uniques
- **Prévention des rejeux** : Nonce aléatoire de 8 octets + validation d'horodatage
- **Confidentialité persistante** : Le cliquet symétrique fait avancer les clés de chaîne
- **Déchiffrement hors ordre** : Mise en cache des clés de messages ignorées (jusqu'à 1000)

## Structure du projet

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## Tests

La démonstration (`npm run dev`) couvre toutes les fonctionnalités principales :
- Création et sérialisation des paquets (aller-retour)
- Génération de clés Ed25519 et vérification des signatures
- Établissement de session du protocole Signal
- Chiffrement et déchiffrement des messages
- Livraison via le transport en cours de processus

Pour les tests unitaires, étendre avec Jest ou un exécuteur de tests similaire.

## Notes de compatibilité

- **Format réseau C#** : 100 % compatible avec C# PacketSerializer
- **Paquets signés** : Version de protocole 2 avec signatures Ed25519
- **Dérivation HKDF** : Utilisation de @noble/hashes (implémentation JavaScript pure)
- **ECDH** : Module crypto Node.js intégré (courbe P-256)

## Dépendances

- **tweetnacl** : Signatures Ed25519 via TweetNaCl
- **@noble/hashes** : Dérivation de clé HKDF-SHA256
- **uuid** : Génération et analyse d'UUID
- **node crypto** : AES-256-GCM, HMAC-SHA256, ECDH

## Licence

MIT - Voir le fichier LICENSE

## Références

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [Implémentation C#](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
