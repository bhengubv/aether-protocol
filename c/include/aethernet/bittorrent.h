// SPDX-License-Identifier: MIT
// AetherNet BitTorrent codec + logic core (BEP-3 and friends).
//
// C port of the C# reference in src/AetherNet.BitTorrent and the Go reference in
// go/bittorrent/*.go. Encoded bytes and hashes are BYTE-IDENTICAL to every other
// AetherNet language SDK, proven against fixtures/bittorrent/vectors.json (the same
// corpus Go and C# already pass).
//
// Surface (mirrors the Go package file-for-file):
//   bencode.go     → strict decode + canonical encode of the bencode value tree
//   metainfo.go    → single-file torrent builder + parse + SHA-1 info-hash
//   magnet.go      → magnet: URI parse (hex / base32 info-hash)
//   peerwire.go    → 68-byte handshake, peer-wire messages (4-byte BE framing),
//                    MSB-first bitfield
//   utp.go         → µTP packet (20-byte header, byte-exact, BEP-29)
//   picker.go      → rarest-first piece picker
//   piecestore.go  → in-memory verified piece store (SHA-1 per piece)
//   merkle.go      → BEP-52 SHA-256 merkle root + v2 info-hash
//   dht.go         → Kademlia NodeID (XOR), compact node(26B)/peer(6B), routing table
//   krpc.go        → KRPC message encode/decode (bencode, BEP-5)
//   extensions.go  → extension protocol (BEP-10) + ut_metadata (BEP-9) + PEX (BEP-11)
//
// SHA-256 reuses the SDK's libsodium-backed aethernet_sha256() (security.h). SHA-1 is
// not provided by libsodium and no other module needed it, so a compact self-contained
// SHA-1 lives inside src/bittorrent.c (no new external dependency).

#ifndef AETHERNET_BITTORRENT_H
#define AETHERNET_BITTORRENT_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ═══════════════════════════════════════════════════════════════════════════
 * Bencode (BEP-3): strict decode + canonical encode.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef enum {
    AETHERNET_BENC_INT = 0,  /**< i<decimal>e, 64-bit signed          */
    AETHERNET_BENC_STR = 1,  /**< <length>:<bytes> — RAW bytes         */
    AETHERNET_BENC_LIST = 2, /**< l<values…>e                          */
    AETHERNET_BENC_DICT = 3  /**< d<key><value>…e, keys byte-sorted    */
} aethernet_benc_type_t;

typedef struct aethernet_benc_value aethernet_benc_value_t;

/** A decoded / to-be-encoded bencode value. Byte strings hold raw bytes. */
struct aethernet_benc_value {
    aethernet_benc_type_t type;
    /* INT */
    int64_t i;
    /* STR */
    uint8_t *s;
    size_t   s_len;
    /* LIST + DICT share the item vector; DICT also carries the key vector. */
    aethernet_benc_value_t **items;  /**< list items / dict values (parallel to keys) */
    uint8_t                **keys;    /**< dict: raw key bytes                         */
    size_t                  *key_lens;/**< dict: key lengths                           */
    size_t                   count;   /**< number of items / dict entries              */
    size_t                   cap;     /**< internal capacity                           */
};

/** Allocate an integer value. */
aethernet_benc_value_t *aethernet_benc_int(int64_t v);
/** Allocate a byte-string value (copies len bytes; NULL data allowed iff len==0). */
aethernet_benc_value_t *aethernet_benc_str(const uint8_t *data, size_t len);
/** Convenience: byte-string from a NUL-terminated C string. */
aethernet_benc_value_t *aethernet_benc_str_c(const char *s);
/** Allocate an empty list. */
aethernet_benc_value_t *aethernet_benc_list(void);
/** Allocate an empty dictionary. */
aethernet_benc_value_t *aethernet_benc_dict(void);

/** Append value to a list (takes ownership of value). Returns false on OOM. */
bool aethernet_benc_list_append(aethernet_benc_value_t *list, aethernet_benc_value_t *value);
/** Insert key→value into a dict (copies key, takes ownership of value). Rejects
 *  duplicate keys. Returns false on duplicate/OOM. */
bool aethernet_benc_dict_add(aethernet_benc_value_t *dict, const char *key, aethernet_benc_value_t *value);
/** Look up a dict key (NUL-terminated). Returns a borrowed pointer or NULL. */
const aethernet_benc_value_t *aethernet_benc_dict_get(const aethernet_benc_value_t *dict, const char *key);

/** Free a value tree (recursively). NULL-safe. */
void aethernet_benc_free(aethernet_benc_value_t *v);

/** Canonical encode (dict keys sorted by raw byte order). malloc'd; caller frees.
 *  Writes length to *out_len. Returns NULL on OOM. */
uint8_t *aethernet_benc_encode(const aethernet_benc_value_t *v, size_t *out_len);

/** Strict decode of exactly one value; rejects trailing bytes. Returns NULL on any
 *  BEP-3 violation (leading zeros, negative zero, unsorted/duplicate keys, overflow,
 *  trailing data…). Caller frees with aethernet_benc_free. */
aethernet_benc_value_t *aethernet_benc_decode(const uint8_t *data, size_t len);

/** Decode one value; sets *consumed to the number of bytes read. NULL on error. */
aethernet_benc_value_t *aethernet_benc_decode_n(const uint8_t *data, size_t len, size_t *consumed);

/* ═══════════════════════════════════════════════════════════════════════════
 * SHA-1 (self-contained; info-hash + piece hashes).
 * ═══════════════════════════════════════════════════════════════════════════ */

#define AETHERNET_BT_SHA1_SIZE 20

/** One-shot SHA-1. out must hold 20 bytes. */
void aethernet_bt_sha1(const uint8_t *data, size_t len, uint8_t out[AETHERNET_BT_SHA1_SIZE]);

/* ═══════════════════════════════════════════════════════════════════════════
 * Metainfo / info-hash (BEP-3).
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct {
    char   **path;      /**< path components                 */
    size_t   path_count;
    int64_t  length;
} aethernet_bt_file_entry_t;

typedef struct {
    aethernet_benc_value_t *root;   /**< owned parsed root dict           */
    const aethernet_benc_value_t *info; /**< borrowed pointer into root    */
    uint8_t  info_hash_v1[AETHERNET_BT_SHA1_SIZE]; /**< SHA-1 of RAW info dict */
    char    *name;                  /**< owned                            */
    int64_t  piece_length;
    uint8_t *piece_hashes;          /**< owned; piece_count * 20 bytes     */
    size_t   piece_count;
    aethernet_bt_file_entry_t *files; /**< owned                          */
    size_t   file_count;
    int64_t  total_length;
    char   **announce;              /**< owned; de-duplicated tracker URLs */
    size_t   announce_count;
    bool     is_single_file;
} aethernet_bt_metainfo_t;

/** Build single-file .torrent bytes (byte-identical to the C#/Go builders).
 *  announce may be NULL/empty to omit the tracker. malloc'd; caller frees.
 *  Returns NULL on invalid args / OOM. */
uint8_t *aethernet_bt_build_single_file_torrent(const char *name,
                                                 const uint8_t *data, size_t data_len,
                                                 int64_t piece_length,
                                                 const char *announce,
                                                 size_t *out_len);

/** Parse .torrent bytes. The SHA-1 info-hash is taken over the RAW bencoded info
 *  dict (byte-offset extraction), matching real clients. Caller frees with
 *  aethernet_bt_metainfo_free. Returns NULL on malformed input. */
aethernet_bt_metainfo_t *aethernet_bt_parse_torrent(const uint8_t *data, size_t len);

/** Free a parsed metainfo. NULL-safe. */
void aethernet_bt_metainfo_free(aethernet_bt_metainfo_t *m);

/** Write the 40-char lowercase hex v1 info-hash + NUL into out (>= 41 bytes). */
void aethernet_bt_metainfo_info_hash_v1_hex(const aethernet_bt_metainfo_t *m, char out[41]);

/* ═══════════════════════════════════════════════════════════════════════════
 * Magnet (BEP-9 xt=urn:btih:).
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct {
    uint8_t  info_hash[AETHERNET_BT_SHA1_SIZE];
    char    *display_name;  /**< owned; may be NULL      */
    char   **trackers;      /**< owned                    */
    size_t   tracker_count;
} aethernet_bt_magnet_t;

/** Parse a magnet: URI (40-hex or 32-base32 btih). Caller frees with
 *  aethernet_bt_magnet_free. Returns NULL on malformed input. */
aethernet_bt_magnet_t *aethernet_bt_parse_magnet(const char *uri);
/** Free a parsed magnet. NULL-safe. */
void aethernet_bt_magnet_free(aethernet_bt_magnet_t *m);

/* ═══════════════════════════════════════════════════════════════════════════
 * Peer-wire (BEP-3): handshake, messages, bitfield.
 * ═══════════════════════════════════════════════════════════════════════════ */

#define AETHERNET_BT_HANDSHAKE_SIZE 68

typedef enum {
    AETHERNET_BT_MSG_CHOKE = 0,
    AETHERNET_BT_MSG_UNCHOKE = 1,
    AETHERNET_BT_MSG_INTERESTED = 2,
    AETHERNET_BT_MSG_NOT_INTERESTED = 3,
    AETHERNET_BT_MSG_HAVE = 4,
    AETHERNET_BT_MSG_BITFIELD = 5,
    AETHERNET_BT_MSG_REQUEST = 6,
    AETHERNET_BT_MSG_PIECE = 7,
    AETHERNET_BT_MSG_CANCEL = 8,
    AETHERNET_BT_MSG_PORT = 9,
    AETHERNET_BT_MSG_EXTENDED = 20
} aethernet_bt_message_type_t;

/** A peer-wire message. has_id == false ⇒ keep-alive (zero-length frame). */
typedef struct {
    bool     has_id;
    uint8_t  id;
    uint8_t *payload;   /**< owned; may be NULL when payload_len == 0 */
    size_t   payload_len;
} aethernet_bt_message_t;

typedef struct {
    uint8_t reserved[8];
    uint8_t info_hash[20];
    uint8_t peer_id[20];
} aethernet_bt_handshake_t;

/** Reserved bits advertising the extension protocol (BEP-10) + DHT (BEP-5). */
void aethernet_bt_default_reserved(uint8_t out[8]);
/** Serialize a 68-byte handshake into out (>= 68 bytes). */
void aethernet_bt_handshake_to_bytes(const aethernet_bt_handshake_t *h, uint8_t out[AETHERNET_BT_HANDSHAKE_SIZE]);
/** Parse a 68-byte handshake. Returns false on malformed input. */
bool aethernet_bt_handshake_parse(const uint8_t *data, size_t len, aethernet_bt_handshake_t *out);
bool aethernet_bt_handshake_supports_extended(const aethernet_bt_handshake_t *h);
bool aethernet_bt_handshake_supports_dht(const aethernet_bt_handshake_t *h);

/* Message factories. Each fills *out (payload malloc'd); free with
 * aethernet_bt_message_free. Return false only on OOM. */
bool aethernet_bt_keepalive(aethernet_bt_message_t *out);
bool aethernet_bt_choke(aethernet_bt_message_t *out);
bool aethernet_bt_unchoke(aethernet_bt_message_t *out);
bool aethernet_bt_interested(aethernet_bt_message_t *out);
bool aethernet_bt_not_interested(aethernet_bt_message_t *out);
bool aethernet_bt_have(uint32_t piece_index, aethernet_bt_message_t *out);
bool aethernet_bt_bitfield_msg(const uint8_t *bits, size_t bits_len, aethernet_bt_message_t *out);
bool aethernet_bt_request(uint32_t index, uint32_t begin, uint32_t length, aethernet_bt_message_t *out);
bool aethernet_bt_cancel(uint32_t index, uint32_t begin, uint32_t length, aethernet_bt_message_t *out);
bool aethernet_bt_piece(uint32_t index, uint32_t begin, const uint8_t *block, size_t block_len, aethernet_bt_message_t *out);
bool aethernet_bt_port(uint16_t port, aethernet_bt_message_t *out);
bool aethernet_bt_extended(uint8_t sub_id, const uint8_t *body, size_t body_len, aethernet_bt_message_t *out);

/** Free a message's payload and reset it. NULL-safe. */
void aethernet_bt_message_free(aethernet_bt_message_t *m);

/** Serialize a message with its 4-byte big-endian length prefix. malloc'd; caller
 *  frees. Writes length to *out_len. */
uint8_t *aethernet_bt_message_to_bytes(const aethernet_bt_message_t *m, size_t *out_len);

/** Parse a message BODY (id + payload, no length prefix). Empty body = keep-alive. */
bool aethernet_bt_message_parse_body(const uint8_t *body, size_t len, aethernet_bt_message_t *out);
/** Parse a full length-prefixed frame; sets *consumed. Returns false on short/invalid. */
bool aethernet_bt_message_parse_frame(const uint8_t *data, size_t len, aethernet_bt_message_t *out, size_t *consumed);

/* Bitfield: MSB-first (piece 0 is 0x80 of byte 0). */
typedef struct {
    uint8_t *bits;   /**< owned; (count+7)/8 bytes */
    size_t   nbytes;
    int      count;  /**< number of pieces          */
} aethernet_bt_bitfield_t;

bool aethernet_bt_bitfield_init(aethernet_bt_bitfield_t *bf, int piece_count);
bool aethernet_bt_bitfield_from_bytes(aethernet_bt_bitfield_t *bf, const uint8_t *data, size_t len, int piece_count);
void aethernet_bt_bitfield_free(aethernet_bt_bitfield_t *bf);
bool aethernet_bt_bitfield_get(const aethernet_bt_bitfield_t *bf, int i);
void aethernet_bt_bitfield_set(aethernet_bt_bitfield_t *bf, int i);
int  aethernet_bt_bitfield_popcount(const aethernet_bt_bitfield_t *bf);
bool aethernet_bt_bitfield_has_all(const aethernet_bt_bitfield_t *bf);

/* ═══════════════════════════════════════════════════════════════════════════
 * µTP packet (BEP-29, version 1). 20-byte header, all big-endian.
 * ═══════════════════════════════════════════════════════════════════════════ */

#define AETHERNET_BT_UTP_HEADER_SIZE 20
#define AETHERNET_BT_UTP_VERSION 1

typedef enum {
    AETHERNET_BT_UTP_DATA = 0,
    AETHERNET_BT_UTP_FIN = 1,
    AETHERNET_BT_UTP_STATE = 2,
    AETHERNET_BT_UTP_RESET = 3,
    AETHERNET_BT_UTP_SYN = 4
} aethernet_bt_utp_type_t;

typedef struct {
    aethernet_bt_utp_type_t type;
    uint16_t connection_id;
    uint32_t timestamp_micros;
    uint32_t timestamp_diff;
    uint32_t window_size;
    uint16_t seq_nr;
    uint16_t ack_nr;
    const uint8_t *payload;   /**< borrowed for to_bytes; owned after parse */
    size_t   payload_len;
} aethernet_bt_utp_packet_t;

/** Serialize (no extensions). malloc'd; caller frees. Writes *out_len. */
uint8_t *aethernet_bt_utp_to_bytes(const aethernet_bt_utp_packet_t *p, size_t *out_len);
/** Parse a µTP packet, walking any extension chain to the payload. The payload
 *  pointer aliases into data (not copied). Returns false on malformed input. */
bool aethernet_bt_utp_parse(const uint8_t *data, size_t len, aethernet_bt_utp_packet_t *out);

/* ═══════════════════════════════════════════════════════════════════════════
 * BEP-52 merkle root (SHA-256) + v2 info-hash.
 * ═══════════════════════════════════════════════════════════════════════════ */

#define AETHERNET_BT_MERKLE_BLOCK_SIZE 16384
#define AETHERNET_BT_SHA256_SIZE 32

/** SHA-256 merkle root over 16 KiB leaf blocks (zero-padded to a power of two).
 *  out must hold 32 bytes. */
void aethernet_bt_merkle_root(const uint8_t *data, size_t len, uint8_t out[AETHERNET_BT_SHA256_SIZE]);
/** Merkle root with an explicit block size (block_size must be > 0). */
void aethernet_bt_merkle_root_block(const uint8_t *data, size_t len, size_t block_size,
                                    uint8_t out[AETHERNET_BT_SHA256_SIZE]);
/** Full 32-byte v2 info-hash: SHA-256 of the bencoded info dict. */
void aethernet_bt_v2_info_hash(const uint8_t *info_dict, size_t len, uint8_t out[AETHERNET_BT_SHA256_SIZE]);

/* ═══════════════════════════════════════════════════════════════════════════
 * DHT (BEP-5): NodeID, compact node/peer info, routing table.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct { uint8_t bytes[20]; } aethernet_bt_node_id_t;

typedef struct {
    aethernet_bt_node_id_t id;
    uint8_t  ip[4];
    uint16_t port;
} aethernet_bt_dht_contact_t;

typedef struct {
    uint8_t  ip[4];
    uint16_t port;
} aethernet_bt_peer_addr_t;

/** XOR distance a^b → out. */
void aethernet_bt_node_id_distance(const aethernet_bt_node_id_t *a, const aethernet_bt_node_id_t *b, aethernet_bt_node_id_t *out);
/** memcmp-style unsigned big-endian compare. */
int  aethernet_bt_node_id_compare(const aethernet_bt_node_id_t *a, const aethernet_bt_node_id_t *b);
/** Count leading zero bits (0..160). */
int  aethernet_bt_node_id_leading_zeros(const aethernet_bt_node_id_t *a);

/** Encode 26-byte compact node records. malloc'd; caller frees. */
uint8_t *aethernet_bt_encode_compact_nodes(const aethernet_bt_dht_contact_t *nodes, size_t n, size_t *out_len);
/** Decode 26-byte compact node records. malloc'd array; caller frees. NULL if
 *  len % 26 != 0. Sets *out_count. */
aethernet_bt_dht_contact_t *aethernet_bt_decode_compact_nodes(const uint8_t *data, size_t len, size_t *out_count);
/** Encode 6-byte compact peer records. malloc'd; caller frees. */
uint8_t *aethernet_bt_encode_compact_peers(const aethernet_bt_peer_addr_t *peers, size_t n, size_t *out_len);
/** Decode 6-byte compact peer records. malloc'd array; caller frees. NULL if
 *  len % 6 != 0. Sets *out_count. */
aethernet_bt_peer_addr_t *aethernet_bt_decode_compact_peers(const uint8_t *data, size_t len, size_t *out_count);

#define AETHERNET_BT_DHT_K 8

/** Kademlia routing table: 160 k-buckets indexed by shared-prefix length. */
typedef struct {
    aethernet_bt_node_id_t self;
    aethernet_bt_dht_contact_t buckets[160][AETHERNET_BT_DHT_K];
    int bucket_len[160];
} aethernet_bt_routing_table_t;

void aethernet_bt_routing_table_init(aethernet_bt_routing_table_t *t, const aethernet_bt_node_id_t *self);
/** Insert/refresh a contact; false if it is us or the bucket is full. */
bool aethernet_bt_routing_table_try_add(aethernet_bt_routing_table_t *t, const aethernet_bt_dht_contact_t *c);
/** Up to count contacts nearest target by XOR distance, written to out
 *  (caller-allocated, out_cap entries). Returns the number written. */
size_t aethernet_bt_routing_table_closest(const aethernet_bt_routing_table_t *t,
                                           const aethernet_bt_node_id_t *target,
                                           aethernet_bt_dht_contact_t *out, size_t out_cap, size_t count);
size_t aethernet_bt_routing_table_count(const aethernet_bt_routing_table_t *t);

/* ═══════════════════════════════════════════════════════════════════════════
 * KRPC (BEP-5): bencode query/response/error messages.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef enum {
    AETHERNET_BT_KRPC_QUERY = 0,
    AETHERNET_BT_KRPC_RESPONSE = 1,
    AETHERNET_BT_KRPC_ERROR = 2
} aethernet_bt_krpc_type_t;

/** A KRPC message. For encode: arguments/response are BORROWED bencode dicts the
 *  caller owns and frees. For decode: transaction_id/method/error_message are owned
 *  copies and arguments/response are owned; free with aethernet_bt_krpc_free. */
typedef struct {
    uint8_t                 *transaction_id;
    size_t                   transaction_id_len;
    aethernet_bt_krpc_type_t type;
    char                    *method;        /**< query "q"        */
    aethernet_benc_value_t  *arguments;     /**< query "a" (dict)  */
    aethernet_benc_value_t  *response;      /**< response "r"(dict)*/
    int64_t                  error_code;    /**< error e[0]        */
    char                    *error_message; /**< error e[1]        */
    bool                     owns;          /**< true after decode (free owns strings/dicts) */
} aethernet_bt_krpc_message_t;

/** Encode a KRPC message to canonical bencode. malloc'd; caller frees. Writes
 *  *out_len. Returns NULL on OOM. */
uint8_t *aethernet_bt_krpc_encode(const aethernet_bt_krpc_message_t *m, size_t *out_len);
/** Decode a KRPC message. Caller frees with aethernet_bt_krpc_free. */
bool aethernet_bt_krpc_decode(const uint8_t *data, size_t len, aethernet_bt_krpc_message_t *out);
/** Free an owned (decoded) KRPC message's fields. NULL-safe. */
void aethernet_bt_krpc_free(aethernet_bt_krpc_message_t *m);

/* ═══════════════════════════════════════════════════════════════════════════
 * Extension protocol (BEP-10) + ut_metadata (BEP-9) + PEX (BEP-11).
 * ═══════════════════════════════════════════════════════════════════════════ */

#define AETHERNET_BT_EXTENDED_MESSAGE_ID 20
#define AETHERNET_BT_EXTENSION_HANDSHAKE_ID 0
#define AETHERNET_BT_METADATA_PIECE_SIZE 16384

/** [subID][body] extended-message payload. malloc'd; caller frees. Sets *out_len. */
uint8_t *aethernet_bt_wrap_extended(uint8_t sub_id, const uint8_t *body, size_t body_len, size_t *out_len);
/** Split an extended payload into sub_id + body pointer (body aliases payload). */
bool aethernet_bt_split_extended(const uint8_t *payload, size_t len, uint8_t *out_sub_id, const uint8_t **out_body, size_t *out_body_len);

typedef enum {
    AETHERNET_BT_METADATA_REQUEST = 0,
    AETHERNET_BT_METADATA_DATA = 1,
    AETHERNET_BT_METADATA_REJECT = 2
} aethernet_bt_metadata_type_t;

/** Build a ut_metadata request / data / reject message. malloc'd; caller frees. */
uint8_t *aethernet_bt_build_metadata_request(int piece, size_t *out_len);
uint8_t *aethernet_bt_build_metadata_data(int piece, int total_size, const uint8_t *data, size_t data_len, size_t *out_len);
uint8_t *aethernet_bt_build_metadata_reject(int piece, size_t *out_len);

typedef struct {
    aethernet_bt_metadata_type_t type;
    int      piece;
    int      total_size;
    uint8_t *data;      /**< owned trailing raw bytes */
    size_t   data_len;
} aethernet_bt_metadata_message_t;

/** Parse a ut_metadata message (bencode header + trailing raw piece bytes). */
bool aethernet_bt_parse_metadata(const uint8_t *body, size_t len, aethernet_bt_metadata_message_t *out);
void aethernet_bt_metadata_message_free(aethernet_bt_metadata_message_t *m);

/** Build a BEP-10 extension handshake advertising name→id extensions. names/ids are
 *  parallel arrays of length n. metadata_size <= 0 omits the field. malloc'd. */
uint8_t *aethernet_bt_build_extension_handshake(const char *const *names, const int *ids, size_t n,
                                                int metadata_size, size_t *out_len);

/** Build a ut_pex message advertising added peers (compact). malloc'd; caller frees. */
uint8_t *aethernet_bt_build_pex_added(const aethernet_bt_peer_addr_t *added, size_t n, size_t *out_len);
/** Parse the "added" peers from a ut_pex message. malloc'd array; caller frees. */
aethernet_bt_peer_addr_t *aethernet_bt_parse_pex_added(const uint8_t *body, size_t len, size_t *out_count);

/* ═══════════════════════════════════════════════════════════════════════════
 * Rarest-first piece picker.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct aethernet_bt_picker aethernet_bt_picker_t;

aethernet_bt_picker_t *aethernet_bt_picker_new(int piece_count);
void aethernet_bt_picker_free(aethernet_bt_picker_t *p);
void aethernet_bt_picker_set_have(aethernet_bt_picker_t *p, int index);
void aethernet_bt_picker_add_peer(aethernet_bt_picker_t *p, const char *peer);
void aethernet_bt_picker_peer_has(aethernet_bt_picker_t *p, const char *peer, int index);
/** Rarest pickable piece the peer has (marks it in-flight), or -1. */
int  aethernet_bt_picker_pick_for(aethernet_bt_picker_t *p, const char *peer);
void aethernet_bt_picker_release(aethernet_bt_picker_t *p, int index);
bool aethernet_bt_picker_is_complete(const aethernet_bt_picker_t *p);

/* ═══════════════════════════════════════════════════════════════════════════
 * In-memory verified piece store (SHA-1 per piece).
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct aethernet_bt_piece_store aethernet_bt_piece_store_t;

/** piece_hashes is piece_count * 20 bytes (copied). */
aethernet_bt_piece_store_t *aethernet_bt_piece_store_new(int piece_length, int64_t total_length,
                                                         const uint8_t *piece_hashes, size_t piece_count);
/** Build a complete store from raw content (a seeder's side). */
aethernet_bt_piece_store_t *aethernet_bt_piece_store_from_content(const uint8_t *data, size_t len, int piece_length);
void aethernet_bt_piece_store_free(aethernet_bt_piece_store_t *s);
size_t aethernet_bt_piece_store_piece_count(const aethernet_bt_piece_store_t *s);
int  aethernet_bt_piece_store_length_of_piece(const aethernet_bt_piece_store_t *s, int i);
bool aethernet_bt_piece_store_has(const aethernet_bt_piece_store_t *s, int i);
/** Verify data against the piece's SHA-1 and store it on success. */
bool aethernet_bt_piece_store_try_complete(aethernet_bt_piece_store_t *s, int i, const uint8_t *data, size_t len);
bool aethernet_bt_piece_store_is_complete(const aethernet_bt_piece_store_t *s);
/** Assemble the full content if complete. malloc'd; caller frees. NULL if incomplete. */
uint8_t *aethernet_bt_piece_store_assemble(const aethernet_bt_piece_store_t *s, size_t *out_len);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_BITTORRENT_H */
