// SPDX-License-Identifier: MIT
//
// Aether Signal Protocol — X3DH + Double Ratchet session service (C)
//
// This header exposes the high-level Signal Protocol session service.  The
// underlying crypto primitives (Ed25519, AES-256-GCM, HMAC-SHA256,
// HKDF-SHA256, X25519, KDF_RK) live in aether/security.h.
//
// Wire-format and algorithm constants match the Rust and C# reference
// implementations; cross-language interop is verified by the fixtures in
// fixtures/signal/expected/*.json.

#ifndef AETHERMESH_SIGNAL_PROTOCOL_H
#define AETHERMESH_SIGNAL_PROTOCOL_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "constants.h"
#include "security.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── Pool / limit constants ───────────────────────────────────────────────

/** Maximum number of concurrent peer sessions held in memory. */
#define AETHERMESH_SIGNAL_MAX_SESSIONS    64

/** Default one-time pre-key pool size (mirrors Rust DEFAULT_OPK_POOL_SIZE). */
#define AETHERMESH_SIGNAL_OPK_POOL_SIZE   100

/** Maximum skipped message keys stored per ratchet epoch (embedded-friendly). */
#define AETHERMESH_SIGNAL_MAX_SKIPPED     100

// ─── Message type flags ───────────────────────────────────────────────────

/** Normal Double-Ratchet message (no X3DH header). */
#define AETHERMESH_SIGNAL_MSG_TYPE_NORMAL   0

/** First message from initiator; carries full X3DH header so the responder
 *  can establish its session on receipt. */
#define AETHERMESH_SIGNAL_MSG_TYPE_PRE_KEY  1

// ─── One-time pre-key entry ───────────────────────────────────────────────

/**
 * A single entry in the one-time pre-key pool.
 *
 * consumed = true means the key has been used and must not be reissued.
 */
typedef struct {
    uint32_t opk_id;
    uint8_t  priv_key[32];
    uint8_t  pub_key[32];
    bool     consumed;
} aethermesh_opk_entry_t;

// ─── Skipped-message-key cache entry (flat array, no hash map) ───────────

/**
 * One cached skipped message key, keyed by (ratchet_pub, counter).
 *
 * Stored in a flat array bounded by AETHERMESH_SIGNAL_MAX_SKIPPED; the oldest
 * entry is evicted when the array is full.
 */
typedef struct {
    uint8_t  ratchet_pub[32];   /* DHr public key at the time of skipping */
    uint32_t counter;           /* Message counter within that chain */
    uint8_t  key[32];           /* The cached message key */
    bool     valid;             /* true = slot occupied */
} aethermesh_skipped_key_entry_t;

// ─── Per-peer Double-Ratchet session ─────────────────────────────────────

/**
 * Complete state for one Signal Protocol session with a remote peer.
 *
 * Signal §5: each direction has an independent chain key (cks / ckr).
 * DHs is the *sending* ratchet keypair; DHr is the remote's current ratchet
 * public key.
 */
typedef struct {
    /** UHID of the remote peer. */
    char peer_uhid[AETHERMESH_MAX_UHID_LEN + 1];

    /** true = slot occupied; false = free to reuse. */
    bool active;

    // ── Shared root key ──────────────────────────────────────────────────
    uint8_t root_key[32];

    // ── Sending chain (CKs) ──────────────────────────────────────────────
    /** false until the first dh_ratchet_send_only or dh_ratchet_receive. */
    bool    has_send_chain;
    uint8_t cks[32];

    // ── Receiving chain (CKr) ────────────────────────────────────────────
    /** false until the first dh_ratchet_receive. */
    bool    has_recv_chain;
    uint8_t ckr[32];

    // ── DH ratchet keypairs ──────────────────────────────────────────────
    /** Our current ratchet private key (DHs). */
    uint8_t dhs_priv[32];
    /** Our current ratchet public key (DHs_pub). */
    uint8_t dhs_pub[32];

    /** true = we know the remote's current ratchet public key (DHr). */
    bool    has_dhr;
    /** Remote's current ratchet public key (DHr). */
    uint8_t dhr[32];

    // ── Counters (ns / nr / pn) ──────────────────────────────────────────
    /** Sending counter (ns): number of messages sent in the current send chain. */
    uint32_t ns;
    /** Receiving counter (nr): number of messages received in the current recv chain. */
    uint32_t nr;
    /** Previous send-chain length (pn): saved when the DH ratchet steps. */
    uint32_t pn;

    // ── X3DH pending-PreKey state (initiator only) ───────────────────────
    /** true = next encrypt should carry the full X3DH PreKey header. */
    bool    pending_pre_key;
    /** Initiator's X25519 identity public key, forwarded to the responder. */
    uint8_t initiator_ik_x25519[32];
    /** Initiator's X25519 ephemeral public key, forwarded to the responder. */
    uint8_t initiator_ek_x25519[32];
    /** SPK id that was used during X3DH (so the responder can look it up). */
    int32_t used_spk_id;
    /** OPK id that was used during X3DH (so the responder can consume it). */
    int32_t used_opk_id;

    // ── Skipped message keys ─────────────────────────────────────────────
    aethermesh_skipped_key_entry_t skipped[AETHERMESH_SIGNAL_MAX_SKIPPED];
    int skipped_count;
} aethermesh_signal_session_t;

// ─── Signal service (one per local identity) ─────────────────────────────

/**
 * Full Signal Protocol service state.
 *
 * Holds the local identity keys, the active SPK, the OPK pool, and up to
 * AETHERMESH_SIGNAL_MAX_SESSIONS concurrent peer sessions.
 *
 * LIMITATION: this implementation retains only the *active* SPK.  Responder
 * X3DH for a bundle that referenced a recently-rotated SPK will fail.  SPK
 * rotation history is deferred to a future revision.
 */
typedef struct {
    /** Local UHID (null-terminated). */
    char local_uhid[AETHERMESH_MAX_UHID_LEN + 1];

    // ── Local long-term identity (Ed25519) ───────────────────────────────
    uint8_t ed_priv[32];
    uint8_t ed_pub[32];

    // ── Local long-term identity (X25519, for X3DH) ──────────────────────
    uint8_t ik_x25519_priv[32];
    uint8_t ik_x25519_pub[32];

    // ── Active signed pre-key (SPK) ──────────────────────────────────────
    uint8_t spk_priv[32];
    uint8_t spk_pub[32];
    /** Ed25519 signature over spk_pub (64 bytes). */
    uint8_t spk_sig[64];
    /** Monotonically increasing SPK identifier. */
    int32_t spk_id;

    // ── One-time pre-key pool ────────────────────────────────────────────
    aethermesh_opk_entry_t opks[AETHERMESH_SIGNAL_OPK_POOL_SIZE];
    /** Next OPK id to assign (auto-incremented). */
    int32_t opk_next_id;

    // ── Peer sessions ────────────────────────────────────────────────────
    aethermesh_signal_session_t sessions[AETHERMESH_SIGNAL_MAX_SESSIONS];
    int session_count;
} aethermesh_signal_service_t;

// ─── Pre-key bundle (published by every device) ──────────────────────────

/**
 * The public data bundle a device publishes to enable X3DH key agreement.
 *
 * Mirrors the Rust PreKeyBundle struct and the C# SignalPreKeyBundle DTO.
 */
typedef struct {
    /** UHID of the bundle owner. */
    char uhid[AETHERMESH_MAX_UHID_LEN + 1];

    /** Long-term Ed25519 identity public key (used to verify spk signature). */
    uint8_t identity_key_ed25519[32];

    /** Long-term X25519 identity public key (used in X3DH DH2). */
    uint8_t identity_key_x25519[32];

    /** Signed pre-key public key (X25519; used in X3DH DH1 / DH3). */
    uint8_t signed_pre_key[32];

    /** Ed25519 signature over signed_pre_key made with identity_key_ed25519. */
    uint8_t signed_pre_key_signature[64];

    /** Identifier for the signed pre-key. */
    int32_t signed_pre_key_id;

    /** One-time pre-key public key (X25519; used in X3DH DH4). */
    uint8_t pre_key[32];

    /** Identifier for the one-time pre-key. */
    int32_t pre_key_id;

    /** true if pre_key / pre_key_id are valid (false = no OPK in bundle). */
    bool has_pre_key;
} aethermesh_pre_key_bundle_t;

// ─── Signal message ───────────────────────────────────────────────────────

/**
 * An encrypted Signal message, ready for wire transmission.
 *
 * ciphertext is heap-allocated; call aethermesh_signal_message_free() when done.
 */
typedef struct {
    /** AETHERMESH_SIGNAL_MSG_TYPE_NORMAL or AETHERMESH_SIGNAL_MSG_TYPE_PRE_KEY. */
    int32_t message_type;

    /** Sender's current DH-ratchet public key (DHs_pub). */
    uint8_t sender_ratchet_pub[32];

    /** AES-256-GCM 12-byte nonce. */
    uint8_t nonce[12];

    /** AES-256-GCM 16-byte authentication tag. */
    uint8_t tag[16];

    /** Number of messages sent in the current chain before this one (ns). */
    uint32_t counter;

    /** Length of the previous send-chain when the DH ratchet last stepped (pn). */
    uint32_t prev_chain_count;

    // ── PreKey header (message_type == AETHERMESH_SIGNAL_MSG_TYPE_PRE_KEY) ───
    /** Initiator's X25519 identity public key. */
    uint8_t initiator_ik_x25519[32];
    /** Initiator's X25519 ephemeral public key (= first DHs_pub). */
    uint8_t initiator_ek_x25519[32];
    /** SPK id the initiator used in X3DH. */
    int32_t used_spk_id;
    /** OPK id the initiator used in X3DH (-1 if none). */
    int32_t used_opk_id;

    /** Heap-allocated ciphertext (plaintext_len bytes). */
    uint8_t *ciphertext;
    /** Length of ciphertext in bytes. */
    size_t   ciphertext_len;
} aethermesh_signal_message_t;

// ─── Public API ───────────────────────────────────────────────────────────

/**
 * Initialise a Signal service for a local identity.
 *
 * Generates Ed25519 identity keys, X25519 identity keys, an SPK (signed with
 * the Ed25519 key), and fills the OPK pool with AETHERMESH_SIGNAL_OPK_POOL_SIZE
 * fresh one-time pre-keys with IDs 1..AETHERMESH_SIGNAL_OPK_POOL_SIZE.
 *
 * @param svc   Caller-allocated service struct (contents overwritten).
 * @param uhid  Null-terminated local UHID string.
 * @returns     true on success; false on crypto failure or NULL input.
 */
bool aethermesh_signal_service_init(aethermesh_signal_service_t *svc, const char *uhid);

/**
 * Destroy a Signal service and zero all key material.
 *
 * Does NOT free @p svc itself (caller owns the allocation).
 */
void aethermesh_signal_service_destroy(aethermesh_signal_service_t *svc);

/**
 * Build a pre-key bundle from the service's current keys.
 *
 * Selects the first unconsumed OPK; if all OPKs are consumed, has_pre_key
 * will be false in the output bundle (X3DH without OPK is still valid but
 * provides reduced forward secrecy).
 *
 * @param svc  Initialised service.
 * @param out  Caller-allocated bundle struct (overwritten).
 * @returns    true on success.
 */
bool aethermesh_signal_generate_pre_key_bundle(aethermesh_signal_service_t *svc,
                                           aethermesh_pre_key_bundle_t *out);

/**
 * Process a remote peer's pre-key bundle (initiator-side X3DH).
 *
 * Verifies the SPK signature, runs the 4-DH X3DH key agreement, and
 * establishes an initiator session ready for the first encrypt().
 *
 * @param svc     Local service.
 * @param bundle  The remote peer's pre-key bundle.
 * @returns       true on success; false if signature verification fails or
 *                any crypto operation fails.
 */
bool aethermesh_signal_process_pre_key_bundle(aethermesh_signal_service_t *svc,
                                          const aethermesh_pre_key_bundle_t *bundle);

/**
 * Encrypt a plaintext message to a peer.
 *
 * The peer session must already exist (created by process_pre_key_bundle or
 * by receiving a PRE_KEY message from the peer).
 *
 * On the initiator's very first call the sending chain is derived lazily via
 * dh_ratchet_send_only (Signal-canonical X3DH integration).
 *
 * @param svc        Local service.
 * @param peer_uhid  Null-terminated UHID of the destination.
 * @param plaintext  Plaintext bytes.
 * @param plen       Length of plaintext.
 * @param out_msg    Output message struct (ciphertext heap-allocated; call
 *                   aethermesh_signal_message_free when done).
 * @returns          true on success.
 */
bool aethermesh_signal_encrypt(aethermesh_signal_service_t *svc,
                           const char *peer_uhid,
                           const uint8_t *plaintext,
                           size_t plen,
                           aethermesh_signal_message_t *out_msg);

/**
 * Decrypt a Signal message from a peer.
 *
 * If @p msg is a PRE_KEY message and no session exists yet, the responder
 * session is established automatically via establish_responder_session.
 *
 * @param svc           Local service.
 * @param sender_uhid   Null-terminated UHID of the sender.
 * @param msg           The received encrypted message.
 * @param out_plaintext Heap-allocated plaintext on success (caller frees).
 * @param out_len       Plaintext length.
 * @returns             true on success; false on auth failure or missing session.
 */
bool aethermesh_signal_decrypt(aethermesh_signal_service_t *svc,
                           const char *sender_uhid,
                           const aethermesh_signal_message_t *msg,
                           uint8_t **out_plaintext,
                           size_t *out_len);

/**
 * Returns true if an active session with peer_uhid already exists.
 */
bool aethermesh_signal_has_session(const aethermesh_signal_service_t *svc,
                               const char *peer_uhid);

/**
 * Free heap-allocated fields inside a message struct.
 *
 * After this call msg->ciphertext is NULL and msg->ciphertext_len is 0.
 * Does NOT free the struct itself (caller owns it).
 */
void aethermesh_signal_message_free(aethermesh_signal_message_t *msg);

#ifdef __cplusplus
}
#endif

#endif /* AETHERMESH_SIGNAL_PROTOCOL_H */
