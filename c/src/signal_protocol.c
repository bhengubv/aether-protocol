// SPDX-License-Identifier: MIT
//
// Aether Signal Protocol — X3DH + Double Ratchet session service (C)
//
// Algorithm references:
//   Signal §3  — X3DH key agreement
//   Signal §5  — Double Ratchet
//   Rust reference: rust/src/security/signal_protocol.rs

#define _POSIX_C_SOURCE 200809L

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include "aether/signal_protocol.h"
#include "aether/security.h"
#include "aether/constants.h"

// ─── Internal HKDF info strings (must match Rust / C# exactly) ───────────

static const uint8_t HKDF_X3DH_ROOT_INFO[]    = "aether-x3dh-root-v1";
#define HKDF_X3DH_ROOT_INFO_LEN  20u   /* strlen("aether-x3dh-root-v1") */

// ─── Utility helpers ──────────────────────────────────────────────────────

/** Constant-time byte-array comparison (same length). */
static bool ct_eq(const uint8_t *a, const uint8_t *b, size_t n)
{
    uint8_t acc = 0;
    for (size_t i = 0; i < n; i++) acc |= (a[i] ^ b[i]);
    return acc == 0;
}

/** Locate an existing active session by peer UHID.  Returns NULL if absent. */
static aether_signal_session_t *find_session(aether_signal_service_t *svc,
                                             const char *peer_uhid)
{
    for (int i = 0; i < AETHER_SIGNAL_MAX_SESSIONS; i++) {
        if (svc->sessions[i].active &&
            strncmp(svc->sessions[i].peer_uhid, peer_uhid, AETHER_MAX_UHID_LEN) == 0) {
            return &svc->sessions[i];
        }
    }
    return NULL;
}

/** Allocate (or reuse) a session slot for peer_uhid.  Returns NULL if full. */
static aether_signal_session_t *alloc_session(aether_signal_service_t *svc,
                                              const char *peer_uhid)
{
    /* Reuse existing slot for the same peer (re-key). */
    aether_signal_session_t *existing = find_session(svc, peer_uhid);
    if (existing) {
        aether_zeroize(existing, sizeof(*existing));
        existing->active = false; /* reset before re-init below */
    }

    /* Find a free slot. */
    for (int i = 0; i < AETHER_SIGNAL_MAX_SESSIONS; i++) {
        if (!svc->sessions[i].active) {
            memset(&svc->sessions[i], 0, sizeof(svc->sessions[i]));
            strncpy(svc->sessions[i].peer_uhid, peer_uhid, AETHER_MAX_UHID_LEN);
            svc->sessions[i].peer_uhid[AETHER_MAX_UHID_LEN] = '\0';
            svc->sessions[i].active = true;
            return &svc->sessions[i];
        }
    }
    return NULL; /* table full */
}

// ─── Symmetric ratchet helpers ────────────────────────────────────────────

/**
 * ratchet_chain_key — Signal §5.1 symmetric ratchet step.
 *
 *   message_key  = HMAC-SHA256(chain_key, [0x01])
 *   next_chain   = HMAC-SHA256(chain_key, [0x02])
 *
 * Both outputs are 32 bytes.  Returns false on HMAC failure.
 */
static bool ratchet_chain_key(const uint8_t *ck,
                              uint8_t out_mk[32],
                              uint8_t out_next_ck[32])
{
    uint8_t d0 = 0x01, d1 = 0x02;
    if (!aether_hmac_sha256(ck, 32, &d0, 1, out_mk))      return false;
    if (!aether_hmac_sha256(ck, 32, &d1, 1, out_next_ck)) return false;
    return true;
}

// ─── Skipped-key cache ────────────────────────────────────────────────────

/**
 * Find a cached skipped message key for (ratchet_pub, counter).
 * Returns pointer to the key bytes, or NULL if not found.
 * The slot is marked invalid (consumed) on return — use once.
 */
static uint8_t *skipped_key_pop(aether_signal_session_t *s,
                                const uint8_t ratchet_pub[32],
                                uint32_t counter,
                                uint8_t out_key[32])
{
    for (int i = 0; i < AETHER_SIGNAL_MAX_SKIPPED; i++) {
        aether_skipped_key_entry_t *e = &s->skipped[i];
        if (e->valid && e->counter == counter &&
            ct_eq(e->ratchet_pub, ratchet_pub, 32)) {
            memcpy(out_key, e->key, 32);
            aether_zeroize(e->key, 32);
            e->valid = false;
            s->skipped_count--;
            return out_key;
        }
    }
    return NULL;
}

/**
 * Cache a skipped message key.  Evicts the oldest valid entry when full.
 * Returns false only on an impossible internal error (no slots at all).
 */
static bool skipped_key_push(aether_signal_session_t *s,
                             const uint8_t ratchet_pub[32],
                             uint32_t counter,
                             const uint8_t key[32])
{
    /* Prefer an empty slot. */
    for (int i = 0; i < AETHER_SIGNAL_MAX_SKIPPED; i++) {
        if (!s->skipped[i].valid) {
            memcpy(s->skipped[i].ratchet_pub, ratchet_pub, 32);
            s->skipped[i].counter = counter;
            memcpy(s->skipped[i].key, key, 32);
            s->skipped[i].valid   = true;
            s->skipped_count++;
            return true;
        }
    }
    /* Evict slot 0 (oldest by index). */
    aether_zeroize(s->skipped[0].key, 32);
    memcpy(s->skipped[0].ratchet_pub, ratchet_pub, 32);
    s->skipped[0].counter = counter;
    memcpy(s->skipped[0].key, key, 32);
    s->skipped[0].valid   = true;
    return true;
}

// ─── DH-ratchet helpers ───────────────────────────────────────────────────

/**
 * skip_message_keys — save any unread recv-chain keys up to `until`.
 *
 * Bounded by AETHER_SIGNAL_MAX_SKIPPED.  Returns false if the gap is too
 * large (session re-establishment required).
 */
static bool skip_message_keys(aether_signal_session_t *s, uint32_t until)
{
    if (!s->has_dhr)          return true; /* no recv chain yet, nothing to skip */
    if (!s->has_recv_chain)   return true;
    if (until <= s->nr)       return true;
    if ((until - s->nr) > AETHER_SIGNAL_MAX_SKIPPED) return false;

    while (s->nr < until) {
        uint8_t mk[32], next_ck[32];
        if (!ratchet_chain_key(s->ckr, mk, next_ck)) return false;
        memcpy(s->ckr, next_ck, 32);
        aether_zeroize(next_ck, 32);
        if (!skipped_key_push(s, s->dhr, s->nr, mk)) return false;
        aether_zeroize(mk, 32);
        s->nr++;
    }
    return true;
}

/**
 * dh_ratchet_receive — Signal §5.2 full DH-ratchet step on receive.
 *
 * Steps:
 *  1. pn = ns; ns = nr = 0; dhr = new_dhr
 *  2. dh1 = X25519(dhs_priv, new_dhr); (rk, ckr) = KDF_RK(rk, dh1)
 *  3. rotate dhs to fresh keypair
 *  4. dh2 = X25519(new_dhs_priv, new_dhr); (rk, cks) = KDF_RK(rk, dh2)
 */
static bool dh_ratchet_receive(aether_signal_session_t *s,
                               const uint8_t new_dhr[32])
{
    /* Signal §5.2 — step pn/ns/nr. */
    s->pn = s->ns;
    s->ns = 0;
    s->nr = 0;

    /* Update DHr. */
    memcpy(s->dhr, new_dhr, 32);
    s->has_dhr = true;

    /* Step 2: derive new receiving chain. */
    uint8_t dh1[32];
    if (!aether_x25519_agree(s->dhs_priv, new_dhr, dh1)) return false;

    uint8_t new_rk[32], new_ckr[32];
    if (!aether_signal_kdf_rk(s->root_key, dh1, new_rk, new_ckr)) {
        aether_zeroize(dh1, 32);
        return false;
    }
    aether_zeroize(dh1, 32);
    memcpy(s->root_key, new_rk, 32);
    memcpy(s->ckr, new_ckr, 32);
    s->has_recv_chain = true;
    aether_zeroize(new_rk, 32);
    aether_zeroize(new_ckr, 32);

    /* Step 3: rotate DHs to a fresh keypair. */
    aether_zeroize(s->dhs_priv, 32);
    if (!aether_x25519_generate_keypair(s->dhs_priv, s->dhs_pub)) return false;

    /* Step 4: derive new sending chain from new DHs · new DHr. */
    uint8_t dh2[32];
    if (!aether_x25519_agree(s->dhs_priv, new_dhr, dh2)) return false;

    uint8_t new_rk2[32], new_cks[32];
    if (!aether_signal_kdf_rk(s->root_key, dh2, new_rk2, new_cks)) {
        aether_zeroize(dh2, 32);
        return false;
    }
    aether_zeroize(dh2, 32);
    memcpy(s->root_key, new_rk2, 32);
    memcpy(s->cks, new_cks, 32);
    s->has_send_chain = true;
    aether_zeroize(new_rk2, 32);
    aether_zeroize(new_cks, 32);

    return true;
}

/**
 * dh_ratchet_send_only — lazy first-send init for the initiator.
 *
 * The initiator's DHs (ek) and DHr (spk_pub) are already set after X3DH.
 * Derive the initial sending chain without rotating DHs (only a full
 * dh_ratchet_receive rotates DHs — Signal-canonical integration).
 *
 *   dh = X25519(dhs_priv, remote_pub)
 *   (rk, cks) = KDF_RK(rk, dh)
 */
static bool dh_ratchet_send_only(aether_signal_session_t *s,
                                 const uint8_t remote_pub[32])
{
    uint8_t dh[32];
    if (!aether_x25519_agree(s->dhs_priv, remote_pub, dh)) return false;

    uint8_t new_rk[32], new_cks[32];
    if (!aether_signal_kdf_rk(s->root_key, dh, new_rk, new_cks)) {
        aether_zeroize(dh, 32);
        return false;
    }
    aether_zeroize(dh, 32);
    memcpy(s->root_key, new_rk, 32);
    memcpy(s->cks, new_cks, 32);
    s->has_send_chain = true;
    aether_zeroize(new_rk, 32);
    aether_zeroize(new_cks, 32);
    return true;
}

// ─── X3DH root-key derivation ─────────────────────────────────────────────

/**
 * Derive the X3DH initial root key from 4 concatenated DH outputs (128 bytes).
 *
 * HKDF-SHA256:
 *   salt = zeros32 (32 zero bytes)
 *   ikm  = dh1||dh2||dh3||dh4 (128 bytes)
 *   info = "aether-x3dh-root-v1"
 *   L    = 32
 *
 * Matches the Rust hkdf32() call in process_pre_key_bundle.
 */
static bool x3dh_derive_root_key(const uint8_t dh_concat[128],
                                 uint8_t out_root_key[32])
{
    static const uint8_t zeros32[32] = {0};
    return aether_hkdf_sha256(
        zeros32, 32,
        dh_concat, 128,
        HKDF_X3DH_ROOT_INFO, HKDF_X3DH_ROOT_INFO_LEN,
        32,
        out_root_key
    );
}

// ─── Responder session establishment ─────────────────────────────────────

/**
 * establish_responder_session — mirror initiator's X3DH from the responder's side.
 *
 * X3DH responder DHs (Signal §3, "Bob computes"):
 *   DH1 = X25519(SPK_B_priv, IK_A)
 *   DH2 = X25519(IK_B_priv,  EK_A)
 *   DH3 = X25519(SPK_B_priv, EK_A)
 *   DH4 = X25519(OPK_B_priv, EK_A)
 *   master = DH1||DH2||DH3||DH4
 *   root_key = HKDF-SHA256(zeros32, master, "aether-x3dh-root-v1", 32)
 *
 * The consumed SPK becomes the initial DHs; DHr is left unset so the
 * subsequent dh_ratchet_receive (triggered by the first decrypt) re-keys
 * both chains.
 */
static bool establish_responder_session(aether_signal_service_t *svc,
                                        const char *peer_uhid,
                                        const uint8_t initiator_ik[32],
                                        const uint8_t initiator_ek[32],
                                        int32_t used_spk_id,
                                        int32_t used_opk_id)
{
    /* Validate SPK id — we only hold the active SPK. */
    if (used_spk_id != svc->spk_id) return false;

    /* Find and consume the OPK by id. */
    uint8_t opk_priv[32];
    bool found_opk = false;
    for (int i = 0; i < AETHER_SIGNAL_OPK_POOL_SIZE; i++) {
        if (!svc->opks[i].consumed &&
            (int32_t)svc->opks[i].opk_id == used_opk_id) {
            memcpy(opk_priv, svc->opks[i].priv_key, 32);
            aether_zeroize(svc->opks[i].priv_key, 32);
            svc->opks[i].consumed = true;
            found_opk = true;
            break;
        }
    }
    if (!found_opk) return false;

    /* X3DH step 1 (responder): DH1 = X25519(SPK_B_priv, IK_A) */
    uint8_t dh1[32], dh2[32], dh3[32], dh4[32];
    if (!aether_x25519_agree(svc->spk_priv, initiator_ik, dh1)) {
        aether_zeroize(opk_priv, 32);
        return false;
    }
    /* X3DH step 2 (responder): DH2 = X25519(IK_B_priv, EK_A) */
    if (!aether_x25519_agree(svc->ik_x25519_priv, initiator_ek, dh2)) {
        aether_zeroize(opk_priv, 32);
        aether_zeroize(dh1, 32);
        return false;
    }
    /* X3DH step 3 (responder): DH3 = X25519(SPK_B_priv, EK_A) */
    if (!aether_x25519_agree(svc->spk_priv, initiator_ek, dh3)) {
        aether_zeroize(opk_priv, 32);
        aether_zeroize(dh1, 32); aether_zeroize(dh2, 32);
        return false;
    }
    /* X3DH step 4 (responder): DH4 = X25519(OPK_B_priv, EK_A) */
    if (!aether_x25519_agree(opk_priv, initiator_ek, dh4)) {
        aether_zeroize(opk_priv, 32);
        aether_zeroize(dh1, 32); aether_zeroize(dh2, 32); aether_zeroize(dh3, 32);
        return false;
    }
    aether_zeroize(opk_priv, 32);

    /* Concatenate: master = DH1||DH2||DH3||DH4 */
    uint8_t master[128];
    memcpy(master,       dh1, 32);
    memcpy(master + 32,  dh2, 32);
    memcpy(master + 64,  dh3, 32);
    memcpy(master + 96,  dh4, 32);
    aether_zeroize(dh1, 32); aether_zeroize(dh2, 32);
    aether_zeroize(dh3, 32); aether_zeroize(dh4, 32);

    /* Derive root key via HKDF-SHA256. */
    uint8_t root_key[32];
    if (!x3dh_derive_root_key(master, root_key)) {
        aether_zeroize(master, sizeof(master));
        return false;
    }
    aether_zeroize(master, sizeof(master));

    /* Allocate/reuse session slot. */
    aether_signal_session_t *sess = alloc_session(svc, peer_uhid);
    if (!sess) { aether_zeroize(root_key, 32); return false; }

    /* Adopt SPK as initial DHs (responder), DHr left unset. */
    memcpy(sess->root_key,  root_key,      32);
    memcpy(sess->dhs_priv,  svc->spk_priv, 32);
    memcpy(sess->dhs_pub,   svc->spk_pub,  32);
    sess->has_dhr       = false;
    sess->has_send_chain = false;
    sess->has_recv_chain = false;
    sess->pending_pre_key = false;
    /* ns/nr/pn already 0 from alloc_session memset. */
    aether_zeroize(root_key, 32);
    return true;
}

// ─── Public API implementation ────────────────────────────────────────────

bool aether_signal_service_init(aether_signal_service_t *svc, const char *uhid)
{
    if (!svc || !uhid) return false;

    /* Zero entire struct first (safe padding, no uninitialised bytes). */
    memset(svc, 0, sizeof(*svc));

    strncpy(svc->local_uhid, uhid, AETHER_MAX_UHID_LEN);
    svc->local_uhid[AETHER_MAX_UHID_LEN] = '\0';

    /* Generate Ed25519 identity keypair. */
    if (!aether_ed25519_generate_keypair(svc->ed_priv, svc->ed_pub)) return false;

    /* Generate X25519 identity keypair (for X3DH DH1/DH2). */
    if (!aether_x25519_generate_keypair(svc->ik_x25519_priv, svc->ik_x25519_pub)) return false;

    /* Generate signed pre-key (SPK): X25519 keypair + Ed25519 signature. */
    if (!aether_x25519_generate_keypair(svc->spk_priv, svc->spk_pub)) return false;
    if (!aether_ed25519_sign(svc->ed_priv, svc->spk_pub, 32, svc->spk_sig)) return false;
    svc->spk_id = 1;

    /* Generate OPK pool: IDs 1..AETHER_SIGNAL_OPK_POOL_SIZE. */
    svc->opk_next_id = 1;
    for (int i = 0; i < AETHER_SIGNAL_OPK_POOL_SIZE; i++) {
        svc->opks[i].opk_id   = (uint32_t)(svc->opk_next_id++);
        svc->opks[i].consumed = false;
        if (!aether_x25519_generate_keypair(svc->opks[i].priv_key,
                                            svc->opks[i].pub_key)) {
            return false;
        }
    }

    svc->session_count = 0;
    return true;
}

void aether_signal_service_destroy(aether_signal_service_t *svc)
{
    if (!svc) return;
    aether_zeroize(svc, sizeof(*svc));
}

bool aether_signal_generate_pre_key_bundle(aether_signal_service_t *svc,
                                           aether_pre_key_bundle_t *out)
{
    if (!svc || !out) return false;
    memset(out, 0, sizeof(*out));

    strncpy(out->uhid, svc->local_uhid, AETHER_MAX_UHID_LEN);
    out->uhid[AETHER_MAX_UHID_LEN] = '\0';

    memcpy(out->identity_key_ed25519,      svc->ed_pub,       32);
    memcpy(out->identity_key_x25519,       svc->ik_x25519_pub, 32);
    memcpy(out->signed_pre_key,            svc->spk_pub,       32);
    memcpy(out->signed_pre_key_signature,  svc->spk_sig,       64);
    out->signed_pre_key_id = svc->spk_id;

    /* Pick the first unconsumed OPK. */
    out->has_pre_key = false;
    for (int i = 0; i < AETHER_SIGNAL_OPK_POOL_SIZE; i++) {
        if (!svc->opks[i].consumed) {
            memcpy(out->pre_key, svc->opks[i].pub_key, 32);
            out->pre_key_id  = (int32_t)svc->opks[i].opk_id;
            out->has_pre_key = true;
            break;
        }
    }
    return true;
}

bool aether_signal_process_pre_key_bundle(aether_signal_service_t *svc,
                                          const aether_pre_key_bundle_t *bundle)
{
    if (!svc || !bundle) return false;
    if (!bundle->has_pre_key) return false; /* OPK required for this implementation */

    /* Verify SPK signature: Ed25519.Verify(identity_key_ed25519, spk_pub, sig). */
    if (!aether_ed25519_verify(bundle->identity_key_ed25519,
                               bundle->signed_pre_key, 32,
                               bundle->signed_pre_key_signature)) {
        return false;
    }

    /* Generate ephemeral X25519 keypair (EK_A). */
    uint8_t ek_priv[32], ek_pub[32];
    if (!aether_x25519_generate_keypair(ek_priv, ek_pub)) return false;

    /* X3DH step 1 (initiator): DH1 = X25519(IK_A_priv, SPK_B_pub) */
    uint8_t dh1[32], dh2[32], dh3[32], dh4[32];
    if (!aether_x25519_agree(svc->ik_x25519_priv, bundle->signed_pre_key, dh1)) {
        aether_zeroize(ek_priv, 32);
        return false;
    }
    /* X3DH step 2 (initiator): DH2 = X25519(EK_A_priv, IK_B_pub) */
    if (!aether_x25519_agree(ek_priv, bundle->identity_key_x25519, dh2)) {
        aether_zeroize(ek_priv, 32); aether_zeroize(dh1, 32);
        return false;
    }
    /* X3DH step 3 (initiator): DH3 = X25519(EK_A_priv, SPK_B_pub) */
    if (!aether_x25519_agree(ek_priv, bundle->signed_pre_key, dh3)) {
        aether_zeroize(ek_priv, 32); aether_zeroize(dh1, 32); aether_zeroize(dh2, 32);
        return false;
    }
    /* X3DH step 4 (initiator): DH4 = X25519(EK_A_priv, OPK_B_pub) */
    if (!aether_x25519_agree(ek_priv, bundle->pre_key, dh4)) {
        aether_zeroize(ek_priv, 32);
        aether_zeroize(dh1, 32); aether_zeroize(dh2, 32); aether_zeroize(dh3, 32);
        return false;
    }

    /* Concatenate: master = DH1||DH2||DH3||DH4 */
    uint8_t master[128];
    memcpy(master,       dh1, 32);
    memcpy(master + 32,  dh2, 32);
    memcpy(master + 64,  dh3, 32);
    memcpy(master + 96,  dh4, 32);
    aether_zeroize(dh1, 32); aether_zeroize(dh2, 32);
    aether_zeroize(dh3, 32); aether_zeroize(dh4, 32);

    /* Derive root key via HKDF-SHA256 with zeros32 salt. */
    uint8_t root_key[32];
    if (!x3dh_derive_root_key(master, root_key)) {
        aether_zeroize(master, sizeof(master));
        aether_zeroize(ek_priv, 32);
        return false;
    }
    aether_zeroize(master, sizeof(master));

    /* Allocate session slot. */
    aether_signal_session_t *sess = alloc_session(svc, bundle->uhid);
    if (!sess) {
        aether_zeroize(root_key, 32);
        aether_zeroize(ek_priv, 32);
        return false;
    }

    /* Initiator: DHs = EK_A; DHr = SPK_B_pub (first known remote ratchet key).
     * CKs is derived lazily on first encrypt (dh_ratchet_send_only). */
    memcpy(sess->root_key,          root_key,              32);
    memcpy(sess->dhs_priv,          ek_priv,               32);
    memcpy(sess->dhs_pub,           ek_pub,                32);
    memcpy(sess->dhr,               bundle->signed_pre_key, 32);
    sess->has_dhr       = true;
    sess->has_send_chain = false; /* derived lazily */
    sess->has_recv_chain = false;
    sess->pending_pre_key     = true;
    memcpy(sess->initiator_ik_x25519, svc->ik_x25519_pub, 32);
    memcpy(sess->initiator_ek_x25519, ek_pub,             32);
    sess->used_spk_id         = bundle->signed_pre_key_id;
    sess->used_opk_id         = bundle->pre_key_id;

    aether_zeroize(root_key, 32);
    aether_zeroize(ek_priv, 32);
    return true;
}

bool aether_signal_encrypt(aether_signal_service_t *svc,
                           const char *peer_uhid,
                           const uint8_t *plaintext,
                           size_t plen,
                           aether_signal_message_t *out_msg)
{
    if (!svc || !peer_uhid || !plaintext || !out_msg) return false;

    aether_signal_session_t *sess = find_session(svc, peer_uhid);
    if (!sess) return false;

    /* Lazy CKs init for the initiator's first send. */
    if (!sess->has_send_chain) {
        if (!sess->has_dhr) return false; /* no DHr yet — cannot derive CKs */
        if (!dh_ratchet_send_only(sess, sess->dhr)) return false;
    }

    /* Symmetric ratchet step: get message key and advance CKs. */
    uint8_t mk[32], next_cks[32];
    if (!ratchet_chain_key(sess->cks, mk, next_cks)) return false;
    memcpy(sess->cks, next_cks, 32);
    aether_zeroize(next_cks, 32);

    /* Generate random 12-byte nonce. */
    uint8_t nonce[12];
    if (!aether_random_bytes(nonce, 12)) {
        aether_zeroize(mk, 32);
        return false;
    }

    /* AES-256-GCM encrypt: ciphertext_len = plen; tag is separate. */
    uint8_t *ct = (uint8_t *)malloc(plen);
    if (!ct) { aether_zeroize(mk, 32); return false; }

    uint8_t tag[16];
    uint8_t nonce_out[12]; /* aether_aes256_gcm_encrypt requires non-NULL out_nonce */
    /* aether_aes256_gcm_encrypt: out_ciphertext size == plaintext_len; tag separate. */
    if (!aether_aes256_gcm_encrypt(plaintext, plen, mk, nonce, NULL, 0, ct, tag, nonce_out)) {
        free(ct);
        aether_zeroize(mk, 32);
        return false;
    }
    aether_zeroize(mk, 32);

    /* Fill output message. */
    memset(out_msg, 0, sizeof(*out_msg));
    out_msg->message_type     = AETHER_SIGNAL_MSG_TYPE_NORMAL;
    memcpy(out_msg->sender_ratchet_pub, sess->dhs_pub, 32);
    memcpy(out_msg->nonce,              nonce,         12);
    memcpy(out_msg->tag,                tag,           16);
    out_msg->counter          = sess->ns;
    out_msg->prev_chain_count = sess->pn;
    out_msg->ciphertext       = ct;
    out_msg->ciphertext_len   = plen;
    sess->ns++;

    /* PreKey header on first message from initiator. */
    if (sess->pending_pre_key) {
        out_msg->message_type = AETHER_SIGNAL_MSG_TYPE_PRE_KEY;
        memcpy(out_msg->initiator_ik_x25519, sess->initiator_ik_x25519, 32);
        memcpy(out_msg->initiator_ek_x25519, sess->initiator_ek_x25519, 32);
        out_msg->used_spk_id  = sess->used_spk_id;
        out_msg->used_opk_id  = sess->used_opk_id;
        sess->pending_pre_key = false;
    }

    return true;
}

bool aether_signal_decrypt(aether_signal_service_t *svc,
                           const char *sender_uhid,
                           const aether_signal_message_t *msg,
                           uint8_t **out_plaintext,
                           size_t *out_len)
{
    if (!svc || !sender_uhid || !msg || !out_plaintext || !out_len) return false;

    /* Establish responder session from a PreKey message if needed. */
    if (msg->message_type == AETHER_SIGNAL_MSG_TYPE_PRE_KEY) {
        /* Allow re-keying: destroy existing session first. */
        aether_signal_session_t *existing = find_session(svc, sender_uhid);
        if (existing) {
            aether_zeroize(existing, sizeof(*existing));
        }
        if (!establish_responder_session(svc, sender_uhid,
                                         msg->initiator_ik_x25519,
                                         msg->initiator_ek_x25519,
                                         msg->used_spk_id,
                                         msg->used_opk_id)) {
            return false;
        }
    }

    aether_signal_session_t *sess = find_session(svc, sender_uhid);
    if (!sess) return false;

    const uint8_t *sender_rp = msg->sender_ratchet_pub;

    /* Check if a DH-ratchet step is needed. */
    bool needs_dh_ratchet = !sess->has_dhr ||
                            !ct_eq(sess->dhr, sender_rp, 32);

    if (needs_dh_ratchet) {
        /* Save any unread keys on the OLD recv chain up to pn. */
        if (!skip_message_keys(sess, msg->prev_chain_count)) return false;
        if (!dh_ratchet_receive(sess, sender_rp)) return false;
    }

    /* Check skipped-key cache first. */
    uint8_t cached_mk[32];
    bool from_cache = skipped_key_pop(sess, sender_rp, msg->counter, cached_mk) != NULL;

    uint8_t mk[32];
    if (from_cache) {
        memcpy(mk, cached_mk, 32);
        aether_zeroize(cached_mk, 32);
    } else {
        if (!sess->has_recv_chain) return false;

        /* Gap check. */
        if (msg->counter < sess->nr) return false; /* replay / already consumed */
        if ((msg->counter - sess->nr) > AETHER_SIGNAL_MAX_SKIPPED) return false;

        /* Skip ahead, caching intermediate keys. */
        while (sess->nr < msg->counter) {
            uint8_t skip_mk[32], next_ck[32];
            if (!ratchet_chain_key(sess->ckr, skip_mk, next_ck)) return false;
            memcpy(sess->ckr, next_ck, 32);
            aether_zeroize(next_ck, 32);
            if (!skipped_key_push(sess, sender_rp, sess->nr, skip_mk)) {
                aether_zeroize(skip_mk, 32);
                return false;
            }
            aether_zeroize(skip_mk, 32);
            sess->nr++;
        }

        /* Advance one more step to get the message key. */
        uint8_t next_ck[32];
        if (!ratchet_chain_key(sess->ckr, mk, next_ck)) return false;
        memcpy(sess->ckr, next_ck, 32);
        aether_zeroize(next_ck, 32);
        sess->nr++;
    }

    /* AES-256-GCM decrypt. */
    uint8_t *pt = (uint8_t *)malloc(msg->ciphertext_len);
    if (!pt) { aether_zeroize(mk, 32); return false; }

    if (!aether_aes256_gcm_decrypt(msg->ciphertext, msg->ciphertext_len,
                                   mk, msg->nonce, msg->tag,
                                   NULL, 0, pt)) {
        aether_zeroize(mk, 32);
        free(pt);
        return false;
    }
    aether_zeroize(mk, 32);

    *out_plaintext = pt;
    *out_len       = msg->ciphertext_len;
    return true;
}

bool aether_signal_has_session(const aether_signal_service_t *svc,
                               const char *peer_uhid)
{
    if (!svc || !peer_uhid) return false;
    for (int i = 0; i < AETHER_SIGNAL_MAX_SESSIONS; i++) {
        if (svc->sessions[i].active &&
            strncmp(svc->sessions[i].peer_uhid, peer_uhid, AETHER_MAX_UHID_LEN) == 0) {
            return true;
        }
    }
    return false;
}

void aether_signal_message_free(aether_signal_message_t *msg)
{
    if (!msg) return;
    if (msg->ciphertext) {
        aether_zeroize(msg->ciphertext, msg->ciphertext_len);
        free(msg->ciphertext);
        msg->ciphertext     = NULL;
        msg->ciphertext_len = 0;
    }
}
