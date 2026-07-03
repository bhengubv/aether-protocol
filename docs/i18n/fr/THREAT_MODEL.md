# Protocole Aether — Modèle de menace

**Révisé contre HEAD `b8b3d22` (2026-05-06).** Ce document décrit ce contre quoi la
couche de protocole cryptographique d'`aether-protocol` défend, ce qui est
explicitement hors périmètre, et les hypothèses sur lesquelles reposent les
affirmations de sécurité. Il est intentionnellement honnête : un attaquant qui lit ce
document doit être en mesure d'énumérer chaque attaque que le protocole **n'arrête
pas**, et ne doit pas être induit en erreur par le marketing du README.

Le document complémentaire est [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7
(Modèle de sécurité). En cas de divergence entre les deux, l'implémentation dans
`src/AetherNet.Security/` fait autorité.

---

## 1. Périmètre

### Ce qu'`aether-protocol` EST

Une bibliothèque de messagerie chiffrée de bout en bout dans le style du protocole
Signal, plus une primitive de mise en réseau maillé (routage de style AODV + stockage
et retransmission DTN + inondation SOS). Les garanties de sécurité fondamentales sont :

1. **Confidentialité** — les corps de messages sont chiffrés AES-256-GCM sous des
   clés par message dérivées d'un Double Ratchet (Signal §5).
2. **Authenticité** — chaque `MeshPacket` porte une signature Ed25519 sur un tampon
   de données signables canoniques (PROTOCOL_SPEC §2.4).
3. **Protection contre le rejeu** — les paquets sont rejetés en cas de doublon
   `(SourceUhid, PacketNonce)` dans une fenêtre de fraîcheur de 5 minutes.
4. **Secret de transmission et post-compromission** — le Double Ratchet régénère les
   clés à chaque changement de clé publique DH lors d'un aller-retour ; un attaquant
   qui compromet une clé de session ne peut récupérer ni les messages passés ni futurs.

### Ce qu'`aether-protocol` n'EST PAS

- **Pas un remplacement de la sécurité de la couche transport.** Utilisez TLS pour
  le trafic client→serveur. Le chiffrement bout-en-bout d'Aether est destiné au trafic
  maillé pair-à-pair ; dès qu'un paquet quitte le maillage vers un backend centralisé,
  la sécurité du transport de ce backend est de la responsabilité de l'hôte.
- **Pas un système de gestion de clés.** L'hôte fournit un stockage durable pour
  les clés d'identité et de pré-clé via `IPreKeyStore` (ou tout adaptateur appuyé
  sur `IKeyValueStore`). L'intégration du trousseau matériel, l'attestation TPM, la
  récupération par entiercement de clés et le chiffrement au repos sont du ressort de
  l'hôte.
- **Pas un système d'authentification.** Aether authentifie que « le détenteur de la
  clé d'identité X a dit ce paquet ». L'association de la clé X à « l'humain Alice »
  est de la responsabilité UX de l'hôte (comparaison du numéro de sécurité, échange
  d'empreinte hors bande, chaîne de confiance préalable).
- **Pas un réseau de confidentialité.** Le fil révèle le type de message, la longueur
  du paquet, le UHID source, le UHID destination, le nombre de sauts et l'horodatage.
  Ce n'est pas Tor.

---

## 2. Attaques défendues

### 2.1. Écoute en transit

Chaque charge utile est chiffrée avec AES-256-GCM sous une clé par message dérivée
de la chaîne symétrique du Double Ratchet (Signal §5.1, HMAC-SHA256 avec séparation
de domaine `0x01`/`0x02`). Un attaquant qui capture tous les paquets entre Alice et
Bob ne récupère rien sans l'une de leurs clés de session.

Vérifié par `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs` et les
vecteurs cross-langage `fixtures/signal/expected/ratchet_step_basic.json`.

### 2.2. Falsification de messages

Chaque paquet Wave-2 porte une signature Ed25519 sur le tampon canonique
`BuildSignableData(packet)` (`src/AetherNet.Security/Services/PacketSigningService.cs`,
PROTOCOL_SPEC §2.4). Les paquets falsifiés échouent à la vérification et sont rejetés
à chaque saut connaissant la clé publique d'identité de la source. Les paquets Route
Reply (RREP) sont signés par la destination revendiquée — les nœuds intermédiaires ne
peuvent pas usurper l'identité des destinations car ils ne détiennent pas la clé
privée Ed25519 de la destination.

### 2.3. Attaques par rejeu

`PacketSigningService.VerifyPacketAsync` :

- Rejette les paquets dont le `TimestampMs` s'écarte de plus de 5 minutes de l'UTC
  local (`FreshnessWindowMs = 5 * 60 * 1000`).
- Maintient une table de déduplication en mémoire indexée par `(SourceUhid, PacketNonce)`
  avec un TTL de 5 minutes. La clé de déduplication a été changée de `nonce` seul à
  `(source, nonce)` dans le commit `5bd52a9` pour corriger deux modes d'échec :
  les collisions de nonce entre expéditeurs qui faisaient tomber du trafic légitime, et
  les attaques de pré-enregistrement où un adversaire plante un nonce chez un destinataire
  pour bloquer le premier paquet de l'expéditeur légitime.

Compteurs : `aethernet.nonces.replayed`, `aethernet.timestamps.stale`.

### 2.4. Secret de transmission (compromission de clés passées)

Le Double Ratchet dérive une nouvelle clé de chaîne d'envoi à chaque étape de rotation
DH (KDF_RK, HKDF-SHA256 sur `salt = current_root_key`,
`info = "aether-ratchet-rk-v1"`, bloc 64 octets divisé 32+32 en nouvelle clé racine
et clé de chaîne — `src/AetherNet.Security/Services/SignalProtocolService.cs`).
Un attaquant qui compromet l'état de session courant ne peut déchiffrer aucun message
antérieur : chaque clé de message précédente a été dérivée et mise à zéro
(`CryptographicOperations.ZeroMemory`) avant l'étape de ratchet suivante.

### 2.5. Sécurité post-compromission (récupération de clés futures)

Quand le côté récepteur observe un nouveau `SenderEphemeralKeyX25519` sur un message
entrant, il effectue une étape de ratchet DH à la réception (Signal §5.2). L'état de
session mis en cache par l'attaquant devient obsolète dès le prochain aller-retour ; un
attaquant qui prend un instantané d'une session et s'éloigne ne peut plus déchiffrer
les messages une fois que les parties légitimes ont échangé un tour.

L'étape de rotation DH à la réception a été déployée dans les 8 langages — voir
`OPEN_ISSUES.md` item 2 pour la liste des commits à l'échelle de la famille.

### 2.6. Rejeu de pré-clé à usage unique

Chaque clé pré-key à usage unique (OPK) est consommée exactement une fois. La
référence C# embarque un pool de 100 OPK avec émission FIFO, rechargement paresseux
à chaque génération de bundle et consommation atomique protégée par verrou
(`SignalProtocolService.TopUpOpkPoolNoLock`, vérifié par
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`). Une OPK est supprimée et remise à zéro
dès que le répondeur la consomme lors du X3DH, de sorte qu'un message PreKey rejoué
qui réutilise le même identifiant d'OPK ne peut établir une session.

Les 7 autres langages n'émettent encore qu'une seule OPK par session — fonctionnellement
correct pour des charges de travail séquentielles, mais expose un risque de concurrence
lors de récupérations de bundle simultanées. Suivi sous `OPEN_ISSUES.md` §9.

### 2.7. Dérive de format fil entre langages

Chaque implémentation doit produire des sorties byte-identiques par rapport au corpus
de fixtures sous `fixtures/` :

- `fixtures/expected/*.bin` — 10 fixtures de sérialisation de paquets, 122
  assertions d'égalité d'octets cross-langage en CI.
- `fixtures/signal/expected/x3dh_basic.json` — calculs X3DH (4 DH X25519,
  HKDF-SHA256 racine avec `info = "aether-x3dh-root-v1"`).
- `fixtures/signal/expected/ratchet_step_basic.json`,
  `ratchet_step_three_iterations.json` — KDFs de ratchet symétrique.
- `fixtures/signal/expected/kdf_rk_basic.json` — étape de ratchet DH.

Une dérive dans la chaîne info HKDF, l'ordre des octets ou le rembourrage d'un
langage fait échouer son build `SignalFixtureTests`. L'interopérabilité fil est donc
un invariant au moment de la compilation, pas un espoir à l'exécution.

### 2.8. Compromission DH statique-statique (l'ancien X3DH cassé)

Avant le 2026-05-05, l'implémentation C# de `KEY_EXCHANGE` utilisait la clé d'identité
locale pour les deux opérations DH — un effondrement statique-statique qui brisait la
propriété de secret de transmission par clé éphémère du X3DH. Corrigé par le commit
`07a93f5` : le vrai X3DH effectue désormais les 4 DH canoniques
`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`
avec une clé éphémère fraîche par session. Voir `OPEN_ISSUES.md` §1.

### 2.9. Boucles de routage et tempêtes de diffusion

`RoutingService` déduplique les paquets RREQ par `(originUhid, broadcastId)`
dans un cache borné (10 000 entrées par défaut ;
`ProtocolConstants.RouteRequestDedupCacheSize`). Le TTL est décrémenté à chaque saut
et les paquets avec `Ttl == 0` sont rejetés. Les diffusions SOS sont limitées à
3/heure par origine et la suppression d'auto-origine empêche un nœud de rediffuser son
propre SOS.

### 2.10. DoS par épuisement du pool OPK

Le pool OPK est borné (`OpkPoolSize`, 100 par défaut) et le contrôle de santé Signal
signale `Unhealthy` quand les OPK disponibles descendent sous
`SignalOptionsBag.MinAvailableOpks` (10 par défaut). Les hôtes câblent des alertes sur
le statut de santé `aether-signal`. Un attaquant qui épuise les OPK en récupérant des
bundles ne peut pas dépasser la taille du pool configuré ; le X3DH du répondeur
continue de fonctionner pour les bundles déjà émis et se rétablit lors du prochain
rechargement à la génération de bundle suivante.

### 2.11. Traçage passif d'appareil par BLE

Un scanner passif qui journalise une adresse MAC BLE stable ou un Service UUID
stable peut suivre un appareil à travers le temps et l'espace. `BlePrivacy`
(`src/AetherNet.Security/Privacy/BlePrivacy.cs`) ferme le vecteur de liaison
d'identifiant : le Service UUID annoncé est redérivé toutes les 15 minutes comme
`HMAC-SHA256(rotation_key, window)` (PROTOCOL_SPEC §12.3), et les pairs sont
adressés par des adresses privées résolvables (IRK + `ah`) plutôt que par une MAC
fixe. Sans la clé de rotation ni l'IRK, deux annonces ne peuvent être liées.
Épinglé à `fixtures/bleprivacy/`.

**Risque résiduel.** Ceci ne ferme que le vecteur d'identifiant BLE — cela ne fait
**pas** d'Aether un réseau de confidentialité (§1). Une fois qu'un paquet est sur le
maillage, l'en-tête `MeshPacket` en clair expose toujours le UHID source/destination,
le type, la longueur et le timing (l'analyse de trafic reste hors périmètre, §3.3),
et l'empreinte de couche RF n'est pas adressée. L'émission des identifiants tournants
sur l'air est le rôle de la pile BLE de l'hôte — la bibliothèque ne fait que les
dériver.

### 2.12. Divulgation de clé sous contrainte (duress)

Un adversaire en possession physique qui contraint l'utilisateur à déverrouiller.
`PanicWipe` (`src/AetherNet.Security/Privacy/PanicWipe.cs`) accepte un **PIN de
contrainte** — comparé à un `SHA-256(pin)` stocké en temps constant (pas de fuite de
timing par sortie anticipée) — qui efface de façon sécurisée chaque clé d'identité
(écrasement par de l'aléatoire, puis mise à zéro) sur l'ensemble du manifeste de noms
de clés, de sorte que l'appareil livré ne détient aucune identité utilisable. Épinglé
à `fixtures/panicwipe/`.

**Risque résiduel.** Au mieux-effort et explicitement borné : cela ne défend **pas**
contre une image forensique capturée *avant* l'effacement, un nivellement d'usure de
la flash qui préserve une copie antérieure des octets de clé, un adversaire qui
contraint à révéler le PIN *authentique*, ou une coercition après que des messages ont
déjà été lus. La comparaison en temps constant atténue le timing de devinette du PIN,
pas un adversaire à canal auxiliaire complet (§3.2).

### 2.13. Perte de l'unique appareil (récupération)

Pas un attaquant, mais la défaillance de disponibilité que constitue la perte de
l'unique copie d'une identité. La sauvegarde par phrase de récupération
(`src/AetherNet.Security/Backup/`) encode la graine d'identité Ed25519 de 32 octets
comme une phrase BIP-39 de 24 mots avec somme de contrôle (PROTOCOL_SPEC §12.4) qui
restaure l'identité sur n'importe quel appareil — aucun serveur ni dépositaire ne la
détient.

**Risque résiduel — une nouvelle surface de vol.** La phrase **est** l'identité :
quiconque lit les 24 mots peut usurper pleinement l'identité de l'utilisateur, sans
révocation possible. Elle échange un risque de perte d'appareil contre un risque de
secret sur papier. La bibliothèque encode/décode et vérifie la somme de contrôle de la
phrase ; l'affichage sécurisé, le stockage et la phrase de passe BIP-39 optionnelle
sont de la responsabilité de l'hôte.

### 2.14. Injection d'un appareil malveillant dans la synchro multi-appareils

Un attaquant qui tente d'insérer un appareil qu'il contrôle dans l'ensemble de synchro
d'une victime, ou de forger des enregistrements de synchro. Un `DeviceLink`
(`src/AetherNet.Security/Sync/`) est **signé en Ed25519 par la clé d'identité**
(PROTOCOL_SPEC §12.1), de sorte que seul le détenteur de l'identité peut autoriser un
nouvel appareil — une liaison non signée ou avec une mauvaise clé échoue à la
vérification. Les charges utiles `SyncRecord` circulent chiffrées de bout en bout dans
le chemin DTN/maillage, si bien que les relais les transportent sans pouvoir les lire.
Épinglé à `fixtures/sync/`.

**Risque résiduel.** Ceci authentifie la *liaison*, pas le comportement ultérieur de
l'appareil lié : un appareil légitimement lié *puis* compromis voit tout l'état
synchronisé — la synchro n'a pas de secret de transmission par enregistrement. La
réconciliation est du dernier-écrit-gagne sur
`(created_at_ms, logical_clock, device_id, record_id)`, de sorte qu'un appareil lié
avec une horloge décalée peut biaiser quel enregistrement l'emporte ; l'intégrité de
l'horloge est l'affaire de l'hôte. La parité octet-à-octet des signatures comporte
l'exception Swift/CryptoKit notée dans PROTOCOL_SPEC §12.1.

---

## 3. Hors périmètre

Ce sont de vraies attaques que le protocole **n'arrête pas**. Certaines sont
théoriquement atténuables dans une future version ; d'autres sont fondamentalement une
préoccupation de l'hôte.

### 3.1. Compromission du terminal

Si un attaquant a les droits root sur l'appareil d'Alice, il peut lire les octets
privés de sa clé d'identité depuis la mémoire et déchiffrer chaque session qu'elle
détient. Le protocole suppose que la mémoire de processus de l'appareil est fiable.
Les atténuations (trousseau de la plateforme, SGX, keystores matériels) sont
explicitement de la responsabilité de l'hôte — voir Section 4.

### 3.2. Attaques par canal auxiliaire

L'implémentation de référence utilise `CryptographicOperations.FixedTimeEquals` pour
la comparaison de clé publique de ratchet (`SignalProtocolService.ConstantTimeEquals`)
mais n'est pas spécifiquement renforcée contre :

- Les canaux auxiliaires de timing dans AES-GCM (le BCL .NET `AesGcm` est accéléré
  matériellement sur les CPU AES-NI ; le timing du repli logiciel n'est pas audité).
- Les canaux auxiliaires d'analyse de puissance (purement logiciel — pas de
  contre-mesures matérielles).
- Le timing de cache sur les chemins de dérivation de clés (HKDF-SHA256 via le BCL).

Une attaque de laboratoire de niveau État-nation sur un appareil déverrouillé volé
est plausible.

### 3.3. Analyse de trafic

Le format fil révèle :

- Le **type** de paquet (1 octet à l'offset 1 — RREQ vs Data vs SOS est en clair).
- La **longueur** du paquet (les charges utiles ne sont pas rembournées).
- Les **UHIDs source et destination** (UTF-8, en clair).
- Les **horodatages**, **TTL** et **priorité**.

Le rembourrage, le trafic de couverture et le routage en oignon ne sont pas
implémentés. Un adversaire qui peut observer passivement le trafic BLE / Wi-Fi peut
construire un graphe de contacts et un profil temporel de chaque conversation, même
s'il ne peut pas lire le contenu. Il s'agit d'une limitation connue ; l'atténuation
nécessiterait une rupture du format fil et n'est pas à l'ordre du jour.

### 3.4. Attaques quantiques

X25519 (RFC 7748) et Ed25519 (RFC 8032) cèdent tous deux face à un ordinateur
quantique suffisamment grand exécutant l'algorithme de Shor. Le protocole **n'est pas
post-quantique**. Une migration future vers un schéma hybride Kyber + X25519 /
Dilithium + Ed25519 est une préoccupation connue mais n'est pas planifiée. Le
chiffretexte existant enregistré aujourd'hui par un adversaire misant sur l'approche
« récolter maintenant, déchiffrer plus tard » est en danger si un CRQC arrive dans
l'horizon temporel pertinent.

### 3.5. Messagerie de groupe à grande échelle

`AetherNet.Security` embarque une interface `IGroupKeyProvider`, mais le protocole
complet Signal Sender Keys (la construction de messagerie de groupe asynchrone utilisée
par Signal) **n'est pas** implémenté dans HEAD. Les hôtes qui ont besoin de messagerie
de groupe aujourd'hui se rabattent sur N sessions par paires — ce qui fonctionne mais
a un coût O(N) par envoi de groupe. PROTOCOL_SPEC §7 ne couvre que les menaces à
destinataire unique.

### 3.6. Vérification d'identité au premier contact (TOFU)

Aether authentifie que « le pair détenant la clé d'identité X a signé ceci ». Il
**n'authentifie pas** que « la clé X appartient réellement à l'humain Alice que
l'utilisateur s'attend à avoir en face de lui ». Au premier contact, un homme du
milieu actif qui contrôle le réseau lors du tout premier échange de bundle peut
substituer sa propre clé d'identité, signer son propre bundle et transmettre le trafic
dans les deux sens de façon transparente.

C'est la faiblesse classique de Signal « Trust On First Use ». L'atténuation canonique
est la comparaison hors bande du numéro de sécurité / de l'empreinte (en personne, via
un canal séparé, sur un écran de vérification pré-partagé). Le protocole n'expose pas
encore de surface d'API publique pour la dérivation du numéro de sécurité ; suivi
comme lacune (pas encore dans `OPEN_ISSUES.md`) — l'UX hôte ne doit pas prétendre que
la vérification est faite par défaut.

### 3.7. Attaques de couche réseau sur le transport sous-jacent

Le brouillage du signal (BLE, Wi-Fi, NearLink), le déni de service de couche RF et les
attaques contre les flux d'appairage/liaison du transport sont hors périmètre. Le
transport (`ITransportService`) est traité comme un tube d'octets opaque. Un brouilleur
qui maîtrise le spectre empêche Aether de livrer quoi que ce soit.

### 3.8. Attaques de routage au-delà de la fenêtre de déduplication

L'inondation Sybil par des nœuds de courte durée qui n'ont pas encore accumulé un score
de fiabilité, les abandons de relais opportunistes qui ne déclenchent pas l'heuristique
de fiabilité, et les attaques d'épuisement de ressources qui restent sous les limites de
débit ne sont pas spécifiquement atténués. Le score de fiabilité (PROTOCOL_SPEC §3.5)
déprioritise les nœuds prouvés mauvais mais n'est pas un protocole de routage
entièrement résistant aux Byzantins.

---

## 4. Hypothèses pour que les affirmations de sécurité tiennent

Les défenses de la Section 2 sont prédicatées sur les invariants suivants. Si l'un
d'eux se brise, la propriété de sécurité correspondante est perdue.

1. **Durabilité de la clé d'identité.** L'hôte stocke les paires de clés d'identité
   Ed25519 + X25519 à long terme de façon durable et sécurisée (p. ex. via
   `IPreKeyStore` contre un `FileSystemKeyValueStore` enveloppé dans
   `EncryptedKeyValueStore`, ou contre le trousseau de la plateforme). La perte d'une
   clé d'identité = compromission totale du compte ; le détenteur de la clé privée peut
   signer n'importe quoi en tant que le pair d'origine.

2. **Correctitude du CSPRNG.** `RandomNumberGenerator.GetBytes` et
   `RandomNumberGenerator.GetInt32` sur la plateforme cible produisent une sortie
   cryptographiquement sûre. L'ensemble du protocole — clés éphémères, nonces
   AES-GCM, nonces de paquets, identifiants OPK — en dépend. Sur les plateformes où la
   source aléatoire du BCL est dégradée (certaines cibles embarquées, pools d'entropie
   Linux cassés), l'ensemble de l'arbre de confiance s'effondre.

3. **Horloge système dans ±5 minutes UTC.** La protection contre le rejeu est à
   fenêtre temporelle. Un appareil dont l'horloge est très décalée rejette chaque paquet
   (horloge trop ancienne) ou accepte les rejeux indéfiniment (horloge trop avancée).
   Les hôtes DEVRAIENT embarquer une vérification de cohérence contre une source
   d'heure fiable au démarrage de l'application.

4. **Consommation atomique des OPK.** Quand un `ConsumeOneTimePreKeyAsync(id)` appuyé
   sur `IPreKeyStore` s'exécute en concurrence avec une opération X3DH de répondeur
   contre le même identifiant, la consommation DOIT réussir ou échouer atomiquement.
   Le pool C# de référence sérialise la consommation sous `_preKeyLock` ; un store
   fourni par l'hôte sur un backend non transactionnel (p. ex. un store de fichiers
   naïf avec lecture-modification-écriture) peut permettre que la même OPK soit
   consommée deux fois, brisant la propriété 2.6. `KeyValuePreKeyStore` utilise
   `IKeyValueStore.RemoveAsync` directement pour la consommation — atomique si le
   remove du KV sous-jacent l'est.

5. **Vérification d'identité au premier contact.** La clé publique d'identité du pair a
   été vérifiée hors bande (numéro de sécurité, empreinte, annuaire de confiance) avant
   le premier message échangé — ou l'hôte accepte le risque TOFU et se contente de
   détecter un changement de clé au prochain contact. Sans cela, §3.6 est une fenêtre
   MitM ouverte.

6. **La mémoire de processus de l'hôte n'est pas lisible par l'adversaire.** §3.1.

---

## 5. Faiblesses connues et atténuations

### 5.1. MitM au premier contact (TOFU)

**Faiblesse :** un attaquant actif qui contrôle le lien pair-à-pair lors du tout premier
échange de bundle peut substituer son propre bundle et transmettre le trafic.
**Atténuation :** l'UX hôte doit exposer un flux de comparaison du numéro de sécurité /
de l'empreinte de clé publique avant de traiter un contact comme vérifié. Une surface
d'API publique pour la dérivation du numéro de sécurité n'est pas encore embarquée dans
`AetherNet.Security` ; suivi comme lacune.

### 5.2. Retard de rotation de la clé pré-signée

**Faiblesse :** tant que l'hôte n'appelle pas `RotateSignedPreKeyAsync`, la même SPK
est servie dans chaque bundle. Un adversaire qui apprend la clé privée SPK (p. ex. via
§3.1 compromission du terminal) peut exécuter X3DH sur tout bundle capturé depuis la
dernière rotation.
**Atténuation :** planifier des appels `RotateSignedPreKeyAsync` quotidiens. Les
`SignedPreKeyRotationOptions` par défaut conservent 3 SPK précédentes afin que les
messages en transit signés sous une clé récemment tournée se déchiffrent encore pendant
la fenêtre de rotation. L'intervalle de rotation par défaut est de 7 jours — les
adopteurs ciblant des utilisateurs activement menacés devraient le raccourcir.

### 5.3. État de session en mémoire sans persistance

**Faiblesse :** si `SignalProtocolService` est construit sans `sessionStore`, un crash
ou un redémarrage de processus perd chaque session active. Le secret de transmission
est intact (les clés perdues ne peuvent être récupérées) mais le prochain message du
pair échouera à se déchiffrer car la chaîne de réception a disparu.
**Atténuation :** câbler `KeyValueSignalSessionStore` contre un `IKeyValueStore` durable
pour tout déploiement en production. Le démo console d'exemple utilise
`InMemoryDtnBundleStore` etc. pour la clarté ; les hôtes en production ne doivent pas
faire de même.

### 5.4. Fenêtre de transition de l'octet flag de compression

**Faiblesse :** `MessagingService` a une interface optionnelle de compression Brotli qui
préfixe un octet flag inconditionnel à l'enveloppe en clair. Un pair exécutant du code
pré-compression mal interprétera l'octet flag comme le premier octet de la charge utile
applicative.
**Atténuation :** les adopteurs positionnent `MessagingOptions.Compression.Enabled =
false` jusqu'à ce que chaque pair ait les nouveaux bits. L'octet flag sera conditionné
par une future négociation de capacité par handshake. Voir la note de migration sur
`CompressionOptions`.

### 5.5. Lacune du langage C

**Faiblesse :** l'implémentation C n'embarque que les primitives X25519 + KDF_RK plus
le vérificateur de fixtures. Elle **n'implémente pas** l'API complète
`SignalProtocolService` (établissement de session X3DH, cycle de vie OPK/SPK,
intégration du ratchet DH). Les hôtes déployant Aether sur des microcontrôleurs en C
ne peuvent pas utiliser la surface C actuelle pour le trafic chiffré bout en bout.
Suivi sous `OPEN_ISSUES.md` §11.

### 5.6. Le pool OPK est C# uniquement

**Faiblesse :** le pool de 100 OPK avec émission FIFO et consommation atomique (défense
2.6) est une fonctionnalité de référence C#. Les implémentations Go, Python, TypeScript,
Rust, Swift, Kotlin n'émettent encore qu'une seule OPK par session. Sous charge
d'initiateurs simultanés, deux répondeurs qui se disputent la même source de bundle
peuvent tous deux observer la même OPK et X3DH peut produire une divergence d'état de
session.
**Atténuation :** pour les langages affectés, sérialiser la consommation du bundle côté
hôte (un initiateur à la fois par pair). Suivi sous `OPEN_ISSUES.md` §9.

### 5.7. Signature de démo dans les langages non-C#

**Faiblesse :** les programmes de démo par langage (Go, Python, TS, Rust, Swift, Kotlin,
C) signent les octets fil complets sérialisés pour visualisation plutôt que le tampon
canonique `BuildSignableData`. Le code de bibliothèque dans ces langages est correct —
seuls les démos prennent le raccourci, mais c'est déroutant pour les porteurs.
**Atténuation :** suivi sous `OPEN_ISSUES.md` §10. Traiter l'étape 3 du démo C# comme
le flux canonique.

---

## 6. Signalement des problèmes de sécurité

Voir [`SECURITY.md`](../SECURITY.md) pour la politique de divulgation responsable.
Envoyer un e-mail à `security@thegeeknetwork.co.za` avec des étapes de reproduction ;
s'attendre à un accusé de réception dans les 48 heures et à une évaluation initiale
dans les 7 jours.

Les problèmes hors périmètre selon la Section 3 sont toujours les bienvenus —
nous préférons savoir ce contre quoi nous ne nous défendons pas plutôt qu'un
utilisateur découvre la lacune en production.
