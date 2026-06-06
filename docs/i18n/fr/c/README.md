# Protocole de réseau maillé Aether - Implémentation C

[English](../../../../c/README.md) · [Français](README.md) · [Español](../../es/c/README.md) · [العربية](../../ar/c/README.md) · [中文简体](../../zh-CN/c/README.md) · [日本語](../../ja/c/README.md) · [Deutsch](../../de/c/README.md) · [Português (BR)](../../pt-BR/c/README.md) · [Русский](../../ru/c/README.md) · [فارسی](../../fa/c/README.md) · [한국어](../../ko/c/README.md)

Une implémentation C haute performance et adaptée aux systèmes embarqués du protocole de réseau maillé Aether. Conçue pour les appareils à ressources limitées tels que l'ESP32 et le nRF52, avec une prise en charge complète de la signature Ed25519, du chiffrement AES-256-GCM et du routage basé sur AODV.

## Vue d'ensemble

Aether est un protocole de réseau maillé décentralisé destiné aux environnements disposant d'une connectivité internet intermittente ou absente. Cette implémentation C fournit :

- **Sérialisation/désérialisation de protocole** — format filaire petit-boutiste correspondant à l'implémentation de référence C#
- **Opérations cryptographiques** — signatures Ed25519, chiffrement AES-256-GCM, HMAC-SHA256, HKDF-SHA256 (via libsodium)
- **Signature de paquets** — construction déterministe des données signables selon la spécification du protocole
- **Abstraction de transport** — motif vtable pour les implémentations de transport personnalisées
- **Transport en cours de processus** — transport de test intégré pour les scénarios multi-nœuds
- **Conception embarquée en premier** — tampons de taille fixe dans la mesure du possible, allocation minimale, opérations en temps constant

## Prérequis de compilation

- **CMake** ≥ 3.16
- **Compilateur C11** (gcc, clang, etc.)
- **libsodium** — pour les opérations cryptographiques
- **Threads POSIX** (pthread)

### macOS

```bash
# Install libsodium using Homebrew
brew install libsodium

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### Linux (Ubuntu/Debian)

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF (ESP32)

La bibliothèque est conçue pour être utilisée comme composant ESP-IDF :

```bash
# In your ESP-IDF project components directory
cp -r /Users/admin/Code/Dev/aether-protocol/c/include aether
cp -r /Users/admin/Code/Dev/aether-protocol/c/src aether/

# Create idf_component.yml
cat > aether/idf_component.yml << 'EOF'
version: "1.0.0"
description: "Aether Mesh Networking Protocol"
dependencies:
  libsodium: "*"
EOF

# In your project's CMakeLists.txt
idf_component_register(
    INCLUDE_DIRS "aether/include"
    SRCS "aether/src/protocol.c" "aether/src/security.c" "aether/src/transport_inprocess.c"
    REQUIRES libsodium pthread
)
```

## Structure

```
c/
├── include/aether/
│   ├── constants.h       # Protocol constants and limits
│   ├── protocol.h        # Packet structure and serialization
│   ├── security.h        # Cryptographic operations
│   └── transport.h       # Transport abstraction
├── src/
│   ├── protocol.c        # Serialization implementation
│   ├── security.c        # Cryptography using libsodium
│   ├── transport_inprocess.c  # In-process test transport
│   └── demo.c            # Example usage
├── tests/
│   ├── CMakeLists.txt
│   └── test_protocol.c   # Unit tests
├── CMakeLists.txt
└── README.md
```

## Démarrage rapide

### Compiler et exécuter la démonstration

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

La sortie attendue illustre :
1. Génération de clés Ed25519
2. Création et signature de paquets
3. Sérialisation au format filaire
4. Désérialisation
5. Chiffrement/déchiffrement AES-256-GCM
6. Authentification HMAC-SHA256
7. Dérivation de clé HKDF

### Exécuter les tests unitaires

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Utilisation dans votre code

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethermesh_mesh_packet_t *packet = aethermesh_packet_new();
    if (!packet) return 1;

    // Set fields
    aethermesh_packet_set_source_uhid(packet, "node-alice");
    aethermesh_packet_set_destination_uhid(packet, "node-bob");
    aethermesh_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERMESH_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERMESH_ED25519_PUBLIC_KEY_SIZE];
    aethermesh_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethermesh_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERMESH_ED25519_SIGNATURE_SIZE];
        aethermesh_ed25519_sign(private_key, signable, signable_len, signature);
        aethermesh_packet_set_signature(packet, signature, AETHERMESH_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethermesh_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethermesh_mesh_packet_t *received = aethermesh_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethermesh_packet_free(received);
    }

    aethermesh_packet_free(packet);
    return 0;
}
```

## Référence de l'API

### Protocole

#### Gestion des paquets
- `aethermesh_mesh_packet_t *aethermesh_packet_new(void)` — Créer un nouveau paquet
- `void aethermesh_packet_free(aethermesh_mesh_packet_t *packet)` — Libérer un paquet
- `aethermesh_mesh_packet_t *aethermesh_packet_clone(const aethermesh_mesh_packet_t *packet)` — Cloner un paquet

#### Sérialisation
- `int aethermesh_packet_serialize(const aethermesh_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — Sérialiser au format filaire
- `aethermesh_mesh_packet_t *aethermesh_packet_deserialize(const uint8_t *data, size_t data_len)` — Désérialiser depuis le format filaire
- `size_t aethermesh_packet_estimate_size(const aethermesh_mesh_packet_t *packet)` — Estimer la taille filaire

#### Champs du paquet
- `bool aethermesh_packet_set_source_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — Définir la source
- `bool aethermesh_packet_set_destination_uhid(aethermesh_mesh_packet_t *packet, const char *uhid)` — Définir la destination
- `bool aethermesh_packet_set_payload(aethermesh_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Définir la charge utile
- `bool aethermesh_packet_set_signature(aethermesh_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Définir la signature

#### Validation
- `bool aethermesh_packet_is_expired(const aethermesh_mesh_packet_t *packet, int max_age_seconds)` — Vérifier si le paquet est expiré
- `bool aethermesh_packet_can_forward(const aethermesh_mesh_packet_t *packet)` — Vérifier si TTL > 0

#### Données de signature
- `uint8_t *aethermesh_packet_get_signable_data(const aethermesh_mesh_packet_t *packet, size_t *out_len)` — Obtenir les octets signables déterministes (l'appelant doit libérer la mémoire)

### Sécurité

#### Ed25519
- `bool aethermesh_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — Générer des clés de 32+32 octets
- `bool aethermesh_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Signer (produit 64 octets)
- `bool aethermesh_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Vérifier

#### AES-256-GCM
- `bool aethermesh_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Chiffrer (nonce auto-généré si NULL)
- `bool aethermesh_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Déchiffrer

#### HMAC et hachage
- `bool aethermesh_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 octets)
- `bool aethermesh_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 octets)
- `bool aethermesh_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Utilitaires
- `void aethermesh_zeroize(void *mem, size_t len)` — Effacement mémoire en temps constant
- `bool aethermesh_random_bytes(uint8_t *out, size_t len)` — Octets aléatoires cryptographiquement sûrs

### Transport

#### Fonctions génériques
- `bool aethermesh_transport_send(aethermesh_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Envoyer des données
- `bool aethermesh_transport_is_connected(aethermesh_transport_t *transport, const char *peer_uhid)` — Vérifier la connexion
- `void aethermesh_transport_set_on_data_received(aethermesh_transport_t *transport, aethermesh_transport_on_data_received callback, void *user_data)` — Enregistrer un rappel
- `void aethermesh_transport_destroy(aethermesh_transport_t *transport)` — Nettoyage

#### Transport en cours de processus
- `aethermesh_transport_t *aethermesh_inprocess_transport_new(void)` — Créer un transport en cours de processus partagé
- `bool aethermesh_inprocess_transport_register_node(aethermesh_transport_t *transport, const char *uhid)` — Enregistrer un nœud
- `bool aethermesh_inprocess_transport_unregister_node(aethermesh_transport_t *transport, const char *uhid)` — Désenregistrer un nœud

## Conformité au format filaire

Cette implémentation suit strictement la spécification du protocole avec des entiers multi-octets en **petit-boutiste** :

```
[1] protocol_version
[1] type
[16] packet_id (UUID bytes)
[1] priority
[4] ttl (little-endian int32)
[8] timestamp_ms (little-endian int64)
[2] source_uhid_len (little-endian uint16)
[N] source_uhid (UTF-8)
[2] destination_uhid_len (little-endian uint16)
[N] destination_uhid (UTF-8)
[2] nonce_len (little-endian uint16)
[N] packet_nonce
[4] payload_len (little-endian int32)
[N] payload
[2] signature_len (little-endian uint16)
[N] signature (Ed25519, 64 bytes)
```

Les paquets sérialisés par cette implémentation C sont compatibles à 100 % avec l'implémentation de référence C#.

## Considérations de sécurité

### Bibliothèques cryptographiques
- **libsodium** (libsodium.org) pour toutes les opérations cryptographiques
- Signatures et vérification Ed25519
- Chiffrement authentifié AES-256-GCM
- HMAC-SHA256 et SHA-256
- Dérivation de clé HKDF-SHA256
- Génération de nombres aléatoires cryptographiquement sûrs

### Effacement des clés
Tout le matériel sensible (clés, texte en clair, valeurs intermédiaires) est effacé de la mémoire à l'aide de `sodium_memzero()` immédiatement après utilisation. Cela prévient les fuites accidentelles de clés.

### Validation des paquets
- Déduplication basée sur l'horodatage : les paquets datant de plus de 300 secondes sont rejetés
- Unicité du nonce : nonce aléatoire de 8 octets dans chaque paquet
- Validation du TTL : les paquets avec TTL=0 sont abandonnés
- Vérification des signatures : les signatures Ed25519 sont obligatoires dans le protocole v2

## Notes sur les appareils embarqués

### ESP32
- Nécessite le portage de libsodium pour ESP-IDF (disponible via les composants ESP-IDF)
- L'estimation de taille fixe des paquets simplifie l'allocation mémoire
- Utilise les threads POSIX pour les opérations de mutex
- Pré-allouer les tampons sur la pile dans la mesure du possible

### nRF52
- Similaire à l'ESP32
- La couche de transport BLE GATT peut être implémentée via la vtable de transport
- Envisager l'utilisation d'un RTOS comme FreeRTOS pour la gestion de paquets multiples

### Utilisation mémoire
- Paquet minimal : ~52 octets
- Paquet maximal : 65 Ko (configurable via `AETHERMESH_MAX_PAYLOAD_LEN`)
- Table de pairs à 256 nœuds : ~32 Ko
- Paquet maillé en mémoire : ~8 Ko (cas le plus défavorable avec les champs au maximum)

## Performances

Sur une machine x86-64 moderne (Intel Core i9) :
- **Sérialisation** : ~1-2 µs par paquet
- **Désérialisation** : ~1-2 µs par paquet
- **Signature Ed25519** : ~100 µs
- **Vérification Ed25519** : ~300 µs
- **Chiffrement AES-256-GCM** : ~1 µs par Ko
- **SHA-256** : ~0,5 µs par Ko

## Tests

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Les tests couvrent :
- Création et clonage de paquets
- Allers-retours de sérialisation
- Signature et vérification Ed25519
- Chiffrement/déchiffrement AES-GCM
- Calcul HMAC-SHA256
- Dérivation de clé HKDF
- Validation du TTL et de l'expiration
- Déterminisme des données signables

## Intégration avec l'écosystème Aether

Cette bibliothèque C est conçue pour s'intégrer avec :
- **AetherMeshAPI** (C#) — relais maillé côté serveur et analytique
- **AetherMesh.Core** (C#) — implémentation de référence (format filaire interopérable)
- **Meshtastic** — firmware radio maillé open source
- **esp-idf** — cadre de développement IoT Espressif
- Applications embarquées personnalisées

## Licence

SPDX-License-Identifier: MIT

Voir le fichier LICENSE pour le texte complet.

## Contribution

Les contributions sont les bienvenues ! Veuillez vous assurer que :
- Tous les tests passent (`ctest --output-on-failure`)
- Le code est conforme à C11
- Le format filaire correspond exactement à la référence C#
- Toutes les données sensibles sont effacées
- La documentation est mise à jour

## Références

- Spécification du protocole : `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- Référence C# : `/Users/admin/Code/Dev/aether-protocol/src/AetherMesh.Core/`
- libsodium : https://libsodium.org/
- RFC 5869 (HKDF) : https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV) : https://tools.ietf.org/html/rfc3561
