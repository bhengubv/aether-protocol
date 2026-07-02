// SPDX-License-Identifier: MIT
// Aether named-channel pub/sub — flood-broadcast channel messages (PacketType 7).

#ifndef AETHERNET_CHANNELS_H
#define AETHERNET_CHANNELS_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A channel message surfaced to a subscribed node. Mirrors the C# ChannelMessageReceived. The owned
 * string fields are managed by the channel service; callbacks borrow them for their duration and must
 * copy anything they wish to retain.
 */
typedef struct {
    char   *channel_id;                          // owned; channel the message was published to
    uint8_t message_id[AETHERNET_PACKET_ID_SIZE]; // 16-byte UUID (flood de-dup key)
    char   *sender_uhid;                         // owned; UHID of the original author
    char   *content;                             // owned; message body
    int64_t sent_at_ms;                          // author-stamped Unix-ms publish time
} aethernet_channel_message_t;

/**
 * Serialize a ChannelMessagePayload (PacketType 7) to canonical UTF-8 JSON:
 *   {"channel_id":"<s>","message_id":"<uuid>","sender_uhid":"<s>","content":"<s>","sent_at_ms":<int>}
 * snake_case keys, field order channel_id, message_id, sender_uhid, content, sent_at_ms, no
 * whitespace, lowercase-dashed 36-char UUID, sent_at_ms a bare integer. This is the cross-language
 * byte-identity gate (fixtures/channels/vectors.json) — every SDK must emit exactly these bytes.
 * String fields are ASCII in the vectors; the reference encoders interpolate them verbatim (matching
 * sos.c / heartbeat.c), so callers must not pass content containing characters that require JSON
 * escaping if byte-identity with other SDKs is required.
 *
 * `message_id` is a 16-byte UUID. On success, writes a heap-allocated buffer to *out_json
 * (null-terminated just past *out_len; the caller may treat [0, *out_len) as the JSON bytes) and its
 * length to *out_len, and returns true. The caller owns *out_json and frees it with free(). Returns
 * false on allocation failure or if any required pointer is NULL.
 */
bool aethernet_channel_message_payload_serialize(const char *channel_id,
                                                 const uint8_t message_id[AETHERNET_PACKET_ID_SIZE],
                                                 const char *sender_uhid,
                                                 const char *content,
                                                 int64_t sent_at_ms,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len);

/**
 * Opaque channel-message service handle. Tracks the set of subscribed channels and a flood-dedup set
 * keyed by message id. The service borrows `sender` — caller keeps it alive for the service lifetime.
 *
 * Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
 * in their own mutex (matches sos.c / heartbeat.c).
 */
typedef struct aethernet_channel_service aethernet_channel_service_t;

aethernet_channel_service_t *aethernet_channel_service_new(aethernet_mesh_sender_t *sender);
void aethernet_channel_service_free(aethernet_channel_service_t *service);

/**
 * Subscribe to a channel — messages on it will fire the message-received callback. Idempotent
 * (subscribing to an already-subscribed channel is a no-op). No-op if `service`/`channel_id` is NULL
 * or `channel_id` is empty.
 */
void aethernet_channel_subscribe(aethernet_channel_service_t *service, const char *channel_id);

/**
 * Stop surfacing messages for a channel. No-op if not subscribed.
 */
void aethernet_channel_unsubscribe(aethernet_channel_service_t *service, const char *channel_id);

/**
 * Snapshot of the channels this node is currently subscribed to. Writes a heap-allocated array of
 * owned, null-terminated strings to *out_channels and the count to *out_count, returning the count
 * (0 on empty, -1 on error). The caller owns the array and frees it with
 * aethernet_channel_subscriptions_free().
 */
int aethernet_channel_get_subscriptions(const aethernet_channel_service_t *service,
                                        char ***out_channels,
                                        int *out_count);

/**
 * Free an array returned by aethernet_channel_get_subscriptions, including each owned string. Safe to
 * call with NULL / count 0.
 */
void aethernet_channel_subscriptions_free(char **channels, int count);

/**
 * Publish `content` to `channel_id`: mints a fresh message id, records it in the dedup set (so our
 * own message is never re-handled when it floods back), and floods a ChannelMessage packet
 * (dest "*", TTL AETHERNET_DEFAULT_TTL) via sender->broadcast. Returns the number of peers the flood
 * reached directly (the sender's broadcast fan-out), or -1 if `service`/`channel_id`/`content` is
 * NULL or `channel_id` is empty.
 */
int aethernet_channel_publish(aethernet_channel_service_t *service,
                             const char *channel_id,
                             const char *content);

/**
 * Process an inbound ChannelMessage packet: de-dup by message id, fire the message-received callback
 * if we are subscribed to its channel AND it is not our own message, and re-flood while TTL allows
 * (even if we are not subscribed — pure relay). `packet` is mutated in place (TTL decrement) when
 * re-flooded — callers should not reuse it. Returns false for the wrong packet type, a malformed
 * payload, a NULL argument, or a duplicate message id; true otherwise.
 */
bool aethernet_channel_handle_packet(aethernet_channel_service_t *service,
                                     aethernet_mesh_packet_t *packet);

/**
 * Message-received callback. Fired once per new message arriving on a subscribed channel (never for
 * this node's own messages). `message` is borrowed for the callback duration — copy any fields to
 * retain. Mirrors the C# MessageReceived event.
 */
typedef void (*aethernet_channel_message_received_cb)(const aethernet_channel_message_t *message,
                                                      void *user_data);

void aethernet_channel_set_message_received_cb(aethernet_channel_service_t *service,
                                               aethernet_channel_message_received_cb cb,
                                               void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_CHANNELS_H
