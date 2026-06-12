// SPDX-License-Identifier: MIT
// Ephemeral Routing Id (ERID) — rotating, key-derived wire address. See aethernet/erid.h.

#include <string.h>

#include "aethernet/erid.h"
#include "aethernet/security.h"   /* aethernet_hkdf_sha256, aethernet_hmac_sha256 */
#include "aethernet/constants.h"  /* AETHERNET_HMAC_SHA256_SIZE */

/* Crockford base-32 alphabet (no I/L/O/U), same as aethernet_tag.c. */
static const char ERID_ALPHABET[32] = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/* HKDF domain-separation label. Must match the C# reference (and every other port). */
static const char ERID_ROUTING_KEY_INFO[] = "aether-erid-routing-key-v1";

/* 'A' 'E' 'R' 'D' — "AetherNet ERID Directory announcement". */
static const uint8_t ERID_ANNOUNCE_MAGIC[4] = { 0x41, 0x45, 0x52, 0x44 };
#define ERID_ANNOUNCE_VERSION 1

/* Encode the first length*5 bits of data as Crockford base-32, MSB first. */
static void erid_base32(const uint8_t *data, size_t data_len, int length, char *out)
{
    int bit_pos = 0;
    for (int i = 0; i < length; i++) {
        int byte_index = bit_pos >> 3;
        int bit_offset = bit_pos & 7;
        int hi = data[byte_index];
        int lo = ((size_t)(byte_index + 1) < data_len) ? data[byte_index + 1] : 0;
        int window = (hi << 8) | lo;
        int val = (window >> (11 - bit_offset)) & 0x1F;
        out[i] = ERID_ALPHABET[val];
        bit_pos += 5;
    }
    out[length] = '\0';
}

bool aethernet_erid_derive_routing_key(const uint8_t *identity_secret,
                                       size_t identity_secret_len,
                                       uint8_t *out_routing_key)
{
    if (!identity_secret || identity_secret_len == 0 || !out_routing_key)
        return false;
    /* salt = NULL → HKDF uses HashLen zero bytes (RFC 5869), matching every other port. */
    return aethernet_hkdf_sha256(NULL, 0,
                                 identity_secret, identity_secret_len,
                                 (const uint8_t *)ERID_ROUTING_KEY_INFO,
                                 strlen(ERID_ROUTING_KEY_INFO),
                                 AETHERNET_ERID_ROUTING_KEY_SIZE, out_routing_key);
}

int64_t aethernet_erid_epoch_for(int64_t unix_seconds, int64_t epoch_seconds)
{
    if (epoch_seconds <= 0)
        return -1;
    if (unix_seconds < 0)
        unix_seconds = 0;
    return unix_seconds / epoch_seconds;
}

bool aethernet_erid_derive_for_epoch(const uint8_t *routing_key, size_t routing_key_len,
                                     int64_t epoch, int length,
                                     char *out, size_t out_size)
{
    if (!routing_key || routing_key_len == 0 || !out)
        return false;
    if (length < 1 || length > AETHERNET_ERID_MAX_LENGTH)
        return false;
    if (out_size < (size_t)length + 1)
        return false;

    /* 8-byte big-endian signed int64 — matches BinaryPrimitives.WriteInt64BigEndian. */
    uint8_t epoch_bytes[8];
    uint64_t e = (uint64_t)epoch;
    for (int i = 7; i >= 0; i--) {
        epoch_bytes[i] = (uint8_t)(e & 0xFF);
        e >>= 8;
    }

    uint8_t mac[AETHERNET_HMAC_SHA256_SIZE];
    if (!aethernet_hmac_sha256(routing_key, routing_key_len, epoch_bytes, sizeof(epoch_bytes), mac))
        return false;

    erid_base32(mac, sizeof(mac), length, out);
    return true;
}

bool aethernet_erid_derive(const uint8_t *routing_key, size_t routing_key_len,
                           int64_t unix_seconds, int64_t epoch_seconds, int length,
                           char *out, size_t out_size)
{
    int64_t epoch = aethernet_erid_epoch_for(unix_seconds, epoch_seconds);
    if (epoch < 0)
        return false;
    return aethernet_erid_derive_for_epoch(routing_key, routing_key_len, epoch, length, out, out_size);
}

/* ─── Announcement codec ─────────────────────────────────────────────────── */

static void write_int32_be(uint8_t *buf, int32_t value)
{
    uint32_t u = (uint32_t)value;
    buf[0] = (uint8_t)((u >> 24) & 0xFF);
    buf[1] = (uint8_t)((u >> 16) & 0xFF);
    buf[2] = (uint8_t)((u >> 8) & 0xFF);
    buf[3] = (uint8_t)(u & 0xFF);
}

static int32_t read_int32_be(const uint8_t *buf)
{
    return (int32_t)(((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
                     ((uint32_t)buf[2] << 8) | (uint32_t)buf[3]);
}

bool aethernet_erid_announcement_encode(const uint8_t *routing_key, size_t routing_key_len,
                                        int32_t epoch_seconds, int32_t erid_length,
                                        uint8_t *out, size_t out_size, size_t *out_len)
{
    if (!routing_key || routing_key_len == 0 || !out)
        return false;
    if (epoch_seconds <= 0)
        return false;
    if (erid_length < 1 || erid_length > AETHERNET_ERID_MAX_LENGTH)
        return false;

    size_t total = (size_t)AETHERNET_ERID_ANNOUNCE_HEADER_LEN + routing_key_len;
    if (out_size < total)
        return false;

    memcpy(out, ERID_ANNOUNCE_MAGIC, 4);
    out[4] = ERID_ANNOUNCE_VERSION;
    write_int32_be(out + 5, epoch_seconds);
    write_int32_be(out + 9, erid_length);
    write_int32_be(out + 13, (int32_t)routing_key_len);
    memcpy(out + AETHERNET_ERID_ANNOUNCE_HEADER_LEN, routing_key, routing_key_len);
    if (out_len)
        *out_len = total;
    return true;
}

bool aethernet_erid_announcement_try_decode(const uint8_t *data, size_t data_len,
                                            uint8_t *out_routing_key, size_t out_key_size,
                                            size_t *out_key_len,
                                            int32_t *out_epoch_seconds,
                                            int32_t *out_erid_length)
{
    if (!data || data_len < AETHERNET_ERID_ANNOUNCE_HEADER_LEN)
        return false;
    if (memcmp(data, ERID_ANNOUNCE_MAGIC, 4) != 0)
        return false;
    if (data[4] != ERID_ANNOUNCE_VERSION)
        return false;

    int32_t epoch_seconds = read_int32_be(data + 5);
    int32_t erid_length = read_int32_be(data + 9);
    int32_t key_len = read_int32_be(data + 13);

    if (epoch_seconds <= 0)
        return false;
    if (erid_length < 1 || erid_length > AETHERNET_ERID_MAX_LENGTH)
        return false;
    if (key_len <= 0)
        return false;
    if ((size_t)AETHERNET_ERID_ANNOUNCE_HEADER_LEN + (size_t)key_len > data_len)
        return false;
    if (out_routing_key && out_key_size < (size_t)key_len)
        return false;

    if (out_routing_key)
        memcpy(out_routing_key, data + AETHERNET_ERID_ANNOUNCE_HEADER_LEN, (size_t)key_len);
    if (out_key_len)
        *out_key_len = (size_t)key_len;
    if (out_epoch_seconds)
        *out_epoch_seconds = epoch_seconds;
    if (out_erid_length)
        *out_erid_length = erid_length;
    return true;
}

/* ─── Directory ──────────────────────────────────────────────────────────── */

bool aethernet_erid_directory_init(aethernet_erid_directory_t *dir,
                                   const uint8_t *my_routing_key,
                                   int64_t epoch_seconds, int erid_length)
{
    if (!dir || !my_routing_key)
        return false;
    memset(dir, 0, sizeof(*dir));
    memcpy(dir->my_routing_key, my_routing_key, AETHERNET_ERID_ROUTING_KEY_SIZE);
    dir->epoch_seconds = (epoch_seconds > 0) ? epoch_seconds : AETHERNET_ERID_DEFAULT_EPOCH_SECONDS;
    dir->erid_length = (erid_length > 0) ? erid_length : AETHERNET_ERID_DEFAULT_LENGTH;
    return true;
}

bool aethernet_erid_directory_my_erid(const aethernet_erid_directory_t *dir,
                                      int64_t unix_seconds, char *out, size_t out_size)
{
    if (!dir)
        return false;
    return aethernet_erid_derive(dir->my_routing_key, AETHERNET_ERID_ROUTING_KEY_SIZE,
                                 unix_seconds, dir->epoch_seconds, dir->erid_length, out, out_size);
}

static aethernet_erid_peer_t *erid_find_peer(aethernet_erid_directory_t *dir, const char *uhid)
{
    for (size_t i = 0; i < AETHERNET_ERID_MAX_PEERS; i++) {
        if (dir->peers[i].used && strcmp(dir->peers[i].uhid, uhid) == 0)
            return &dir->peers[i];
    }
    return NULL;
}

bool aethernet_erid_directory_remember_peer(aethernet_erid_directory_t *dir,
                                            const char *peer_uhid,
                                            const uint8_t *peer_routing_key)
{
    if (!dir || !peer_uhid || peer_uhid[0] == '\0' || !peer_routing_key)
        return false;
    if (strlen(peer_uhid) + 1 > AETHERNET_ERID_MAX_UHID)
        return false;

    aethernet_erid_peer_t *slot = erid_find_peer(dir, peer_uhid);
    if (!slot) {
        for (size_t i = 0; i < AETHERNET_ERID_MAX_PEERS; i++) {
            if (!dir->peers[i].used) { slot = &dir->peers[i]; break; }
        }
        if (!slot)
            return false; /* table full */
    }
    slot->used = true;
    strncpy(slot->uhid, peer_uhid, AETHERNET_ERID_MAX_UHID - 1);
    slot->uhid[AETHERNET_ERID_MAX_UHID - 1] = '\0';
    memcpy(slot->routing_key, peer_routing_key, AETHERNET_ERID_ROUTING_KEY_SIZE);
    return true;
}

bool aethernet_erid_directory_forget_peer(aethernet_erid_directory_t *dir, const char *peer_uhid)
{
    if (!dir || !peer_uhid)
        return false;
    aethernet_erid_peer_t *slot = erid_find_peer(dir, peer_uhid);
    if (!slot)
        return false;
    memset(slot, 0, sizeof(*slot));
    return true;
}

bool aethernet_erid_directory_erid_for_peer(const aethernet_erid_directory_t *dir,
                                            const char *peer_uhid, int64_t unix_seconds,
                                            char *out, size_t out_size)
{
    if (!dir || !peer_uhid)
        return false;
    for (size_t i = 0; i < AETHERNET_ERID_MAX_PEERS; i++) {
        if (dir->peers[i].used && strcmp(dir->peers[i].uhid, peer_uhid) == 0) {
            return aethernet_erid_derive(dir->peers[i].routing_key, AETHERNET_ERID_ROUTING_KEY_SIZE,
                                         unix_seconds, dir->epoch_seconds, dir->erid_length,
                                         out, out_size);
        }
    }
    return false;
}

bool aethernet_erid_directory_resolve_peer(const aethernet_erid_directory_t *dir,
                                           const char *erid, int64_t unix_seconds,
                                           char *out_uhid, size_t out_size)
{
    if (!dir || !erid || erid[0] == '\0' || !out_uhid)
        return false;

    char candidate[AETHERNET_ERID_MAX_LENGTH + 1];
    for (size_t i = 0; i < AETHERNET_ERID_MAX_PEERS; i++) {
        if (!dir->peers[i].used)
            continue;
        if (!aethernet_erid_derive(dir->peers[i].routing_key, AETHERNET_ERID_ROUTING_KEY_SIZE,
                                   unix_seconds, dir->epoch_seconds, dir->erid_length,
                                   candidate, sizeof(candidate)))
            continue;
        if (strcmp(candidate, erid) == 0) {
            if (out_size < strlen(dir->peers[i].uhid) + 1)
                return false;
            strncpy(out_uhid, dir->peers[i].uhid, out_size - 1);
            out_uhid[out_size - 1] = '\0';
            return true;
        }
    }
    return false;
}

size_t aethernet_erid_directory_known_peer_count(const aethernet_erid_directory_t *dir)
{
    if (!dir)
        return 0;
    size_t n = 0;
    for (size_t i = 0; i < AETHERNET_ERID_MAX_PEERS; i++)
        if (dir->peers[i].used)
            n++;
    return n;
}
