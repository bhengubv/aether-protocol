# Protocole de réseau maillé Aether - Implémentation Python

[English](../../../../python/README.md) · [Français](README.md) · [Español](../../es/python/README.md) · [العربية](../../ar/python/README.md) · [中文简体](../../zh-CN/python/README.md) · [日本語](../../ja/python/README.md) · [Deutsch](../../de/python/README.md) · [Português (BR)](../../pt-BR/python/README.md) · [Русский](../../ru/python/README.md) · [فارسی](../../fa/python/README.md) · [한국어](../../ko/python/README.md)

Une implémentation Python du protocole de réseau maillé Aether, fournissant des opérations cryptographiques compatibles au niveau du format réseau avec l'implémentation de référence C#.

## Vue d'ensemble

Aether est un protocole de réseau maillé décentralisé conçu pour les environnements à connectivité Internet intermittente ou inexistante. Ce paquet Python fournit :

- **Signature Ed25519** : Génération de clés, signature et vérification avec PyNaCl
- **Protocole Signal X3DH** : Échange de clés asynchrone avec ECDH P-256
- **Chiffrement AES-256-GCM** : Chiffrement symétrique par message avec des nonces de 12 octets
- **Dérivation de clé HKDF-SHA256** : Dérivation de clé conforme à la RFC 5869 avec des chaînes d'information spécifiques au contexte
- **Cliquet symétrique** : Dérivation de clé de message basée sur HMAC-SHA256 avec confidentialité persistante
- **Sérialisation des paquets** : Format binaire réseau petit-boutiste correspondant à l'implémentation C#
- **Protection contre les attaques par rejeu** : Déduplication basée sur les nonces avec une durée de vie de 5 minutes
- **Transport en cours de processus** : Transport simulé pour tester la communication maillée

## Installation

### Depuis PyPI (lors de la publication)
```bash
pip install aether-protocol
```

### Depuis les sources
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Dépendances de développement
```bash
pip install -e ".[dev]"
```

## Démarrage rapide

```python
import asyncio
from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.signal_protocol import SignalProtocolService
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Architecture

### Structure du paquet

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherMeshNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## Fonctionnalités clés

### 1. Service de signature Ed25519

Utilise PyNaCl (libsodium) pour les opérations cryptographiques :

```python
from aethermesh.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Tailles des clés :**
- Clé privée : 32 octets (graine Ed25519)
- Clé publique : 32 octets (point Ed25519)
- Signature : 64 octets

### 2. Protocole Signal

Implémente l'échange de clés X3DH avec un cliquet symétrique pour la confidentialité persistante :

```python
from aethermesh.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**Dérivation de clé :**
- Utilise HKDF-SHA256 avec le sel : `"AetherMeshSignal"`
- Informations de clé racine : `"aether-root-v1"`
- Informations de chaîne d'envoi : `"aether-chain-send-v1"`
- Informations de chaîne de réception : `"aether-chain-recv-v1"`

**Cliquet symétrique :**
- Utilise HMAC-SHA256 avec la clé de chaîne
- Dérive de nouvelles clés de message et fait avancer la chaîne à chaque message
- Prend en charge jusqu'à 1000 clés ignorées pour la livraison hors ordre
- Chiffrement par message : AES-256-GCM avec un nonce aléatoire de 12 octets

### 3. Sérialisation des paquets

Format binaire compatible au niveau réseau correspondant à l'implémentation C# :

```python
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**Format réseau (petit-boutiste) :**
- Version du protocole : 1 octet
- Type de paquet : 1 octet
- Identifiant de paquet : 16 octets (UUID)
- Priorité : 1 octet
- TTL : 4 octets (int32)
- TimestampMs : 8 octets (int64)
- Longueur SourceUhid : 2 octets + données UTF-8
- Longueur DestinationUhid : 2 octets + données UTF-8
- Longueur PacketNonce : 2 octets + données
- Longueur de la charge utile : 4 octets + données
- Longueur de la signature : 2 octets + données

### 4. Signature des paquets

Signe les paquets avec Ed25519 et détecte les attaques par rejeu :

```python
from aethermesh.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Données signables :**
Conformément à la section 2.3 de la spécification du protocole, la signature couvre :
- PacketNonce (8 octets)
- TimestampMs (8 octets, int64 petit-boutiste)
- Type (4 octets, int32 petit-boutiste)
- SourceUhid (longueur + UTF-8)
- DestinationUhid (longueur + UTF-8)
- SHA-256(Payload) (32 octets)
- Ttl (4 octets, int32 petit-boutiste)
- Priority (4 octets, int32 petit-boutiste)

**Protection contre les rejeux :**
- Maintient un cache des paires (sender_uhid, nonce) vues
- Durée de vie de 5 minutes par entrée de cache
- Nettoyage automatique toutes les 60 secondes

### 5. Services de transport

Classe de base abstraite pour les transports physiques (BLE, Wi-Fi Direct, etc.) :

```python
from aethermesh.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**Fonctionnalités d'InProcessTransport :**
- Registre global des nœuds au niveau de la classe
- Sûr pour les threads avec threading.Lock
- Idéal pour les tests et la simulation de maillage local
- Propriétés : name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Référence des constantes

Toutes les constantes du protocole sont définies dans `aether/constants.py` :

### Cryptographie
- `ED25519_PRIVATE_KEY_SIZE` : 32 octets
- `ED25519_PUBLIC_KEY_SIZE` : 32 octets
- `ED25519_SIGNATURE_SIZE` : 64 octets
- `AES_GCM_NONCE_SIZE` : 12 octets
- `AES_GCM_TAG_SIZE` : 16 octets
- `MAX_SKIPPED_KEYS` : 1000

### Routage
- `DEFAULT_TTL` : 7
- `SOS_TTL` : 15
- `ROUTE_TIMEOUT_MS` : 5000
- `ROUTE_EXPIRY_SECONDS` : 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS` : 72
- `DTN_MAX_COPIES` : 3
- `DTN_MAX_BUNDLES_PER_NODE` : 50
- `DTN_SCAN_INTERVAL_SECONDS` : 60

(Voir `constants.py` pour la liste complète)

## Exécution de la démonstration

Démontre toutes les fonctionnalités principales avec une sortie colorée :

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

La démonstration couvre :
1. Génération de clés Ed25519 et signature
2. Création de nœud avec AetherMeshNode
3. Échange de clés X3DH du protocole Signal
4. Chiffrement et déchiffrement des messages
5. Sérialisation/désérialisation des paquets
6. Signature des paquets et détection des attaques par rejeu
7. Communication via transport en cours de processus
8. Flux de travail de chiffrement de bout en bout complet

## Dépendances

### Exécution
- `pynacl>=1.5.0` - Signature Ed25519 via libsodium
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Développement
- `pytest>=7.4.0` - Framework de test
- `pytest-asyncio>=0.21.0` - Prise en charge des tests asynchrones
- `black>=23.0.0` - Formatage du code
- `mypy>=1.5.0` - Vérification statique des types
- `ruff>=0.1.0` - Analyse statique

## Compatibilité

**Version Python :** 3.10+

**Plateforme :** Multiplateforme (Windows, macOS, Linux)

**Backend cryptographique :** Utilise les backends système libsodium et de la bibliothèque cryptography, garantissant un comportement cohérent sur toutes les plateformes.

## Références du protocole

- **Routage AODV :** RFC 3561
- **Accord de clé X3DH :** Signal Foundation, novembre 2016
- **Double Ratchet :** Signal Foundation, novembre 2016
- **HKDF :** RFC 5869 (Extraction et expansion basées sur HMAC)
- **AES-GCM :** NIST SP 800-38D
- **Ed25519 :** DJB et al., 2012

## Considérations de sécurité

### Effacement des clés
Le matériel cryptographique intermédiaire est effacé après utilisation :
- Secrets partagés issus de l'ECDH
- Clés de message du cliquet symétrique
- Matériel de clé dérivé dans le contexte d'établissement

En Python, l'effacement réel en mémoire in-place est limité, mais les données sensibles sont effacées de la portée des variables immédiatement après utilisation.

### Modèle de menace
Aether suppose :
- Une écoute passive sur BLE/Wi-Fi
- Une injection active de paquets et des rejeux
- Des attaques Sybil via la création de faux nœuds
- Un déni de service sélectif

Les protections comprennent :
- **Confidentialité :** Clés par message AES-256-GCM
- **Intégrité :** Signatures de paquets Ed25519
- **Protection contre les rejeux :** Déduplication basée sur les nonces
- **Confidentialité persistante :** Cliquet symétrique avec clés par message
- **Authentification des routes :** Réponses de route signées

### Limitations
- La livraison de messages hors ordre est prise en charge jusqu'à 1000 messages
- Les messages au-delà de l'écart sont rejetés
- Les adresses BLE tournent toutes les 15 minutes (non implémenté en Python)
- La fenêtre de migration de P-256 vers Ed25519 est de 30 jours (solution de repli pas encore implémentée)

## Tests

Exécuter la suite de tests :

```bash
pytest -v
pytest --asyncio-mode=auto
```

## Licence

Licence MIT - Voir le fichier LICENSE pour les détails

## Contribution

Pour contribuer des améliorations :

1. S'assurer que le code respecte le style PEP 8 (utiliser `black` pour le formatage)
2. Ajouter des annotations de type à toutes les fonctions
3. Inclure des docstrings pour les API publiques
4. Exécuter `mypy` pour la vérification des types
5. Ajouter des tests pour les nouvelles fonctionnalités

## Références

- Spécification du protocole Aether : `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Implémentation de référence C# : `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V. : https://thegeeknetwork.dev
