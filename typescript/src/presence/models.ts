/**
 * Presence data models (PacketType.PresenceBeacon = 21 / PresenceQuery = 22).
 *
 * A privacy-preserving "I'm here" broadcast (beacon) and a "who's around here?" solicitation
 * (query). The beacon advertises the node's ROTATING erid (Ephemeral Routing Id, from EridDirectory —
 * never the stable UHID), a COARSE geohash (host-truncated; empty when hidden), its capability bitmask,
 * a presence status, and a send timestamp. TRANSPORT ONLY — the ERID rotation + geohash coarsening
 * are the host's concern (this layer never touches the stable UHID or precise location).
 *
 * The wire payloads are UTF-8 JSON with snake_case keys, no whitespace, bare integers, and a
 * lowercase-dashed UUID for query_id. The encoding is byte-identical across every language port
 * (locked by fixtures/presence/vectors.json).
 *
 * Beacon field order: erid, geohash, capabilities, status, sent_at_ms.
 * Query field order:   query_id, geohash.
 *
 * SPDX-License-Identifier: MIT
 */

/** PresenceStatus value carried in a beacon (mirrors the C# reference enum). */
export enum PresenceStatus {
  Unknown = 0,
  Available = 1,
  Busy = 2,
  Away = 3,
  DoNotDisturb = 4,
  Offline = 5,
}

/**
 * JSON payload for a PresenceBeacon packet — the node's rotating erid, coarse geohash, capability
 * bitmask, status, and send timestamp. The beacon carries the ROTATING erid (never the stable UHID)
 * and a COARSE geohash ("" = hidden).
 */
export interface PresenceBeaconPayload {
  /** The node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID. */
  erid: string;
  /** Coarse geohash of the node (host-truncated per privacy level); empty string = hidden. */
  geohash: string;
  /** NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …). */
  capabilities: number;
  /** PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5). */
  status: number;
  /** Unix timestamp (ms) when the beacon was sent. */
  sentAtMs: number;
}

/**
 * JSON payload for a PresenceQuery packet — "who's around here?". An empty geohash means "anywhere".
 */
export interface PresenceQueryPayload {
  /** Correlation id minted by the querier (lowercase-dashed UUID). */
  queryId: string;
  /** Coarse geohash to scope the query; empty string = anywhere. */
  geohash: string;
}

/** Event surfaced when an inbound presence beacon arrives, plus the peer that sent it. */
export interface PresenceBeaconReceived {
  beacon: PresenceBeaconPayload;
  /** UHID of the peer that sent the beacon (the inbound packet's source). */
  fromUhid: string;
}

/** Event surfaced when an inbound presence query arrives, plus the peer that sent it. */
export interface PresenceQueryReceived {
  query: PresenceQueryPayload;
  /** UHID of the peer that sent the query (the inbound packet's source). */
  fromUhid: string;
}

/**
 * Canonical PresenceBeacon payload serialization — MUST be byte-identical across all language ports
 * (fixtures/presence/vectors.json): snake_case keys in the field order erid, geohash, capabilities,
 * status, sent_at_ms; no whitespace; capabilities/status/sent_at_ms bare integers; geohash may be "".
 */
export function serializePresenceBeaconPayload(p: PresenceBeaconPayload): string {
  return JSON.stringify({
    erid: p.erid,
    geohash: p.geohash,
    capabilities: p.capabilities,
    status: p.status,
    sent_at_ms: p.sentAtMs,
  });
}

/** Parse a canonical PresenceBeacon payload back into camelCase fields. */
export function deserializePresenceBeaconPayload(bytes: Uint8Array): PresenceBeaconPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    erid?: string;
    geohash?: string;
    capabilities?: number;
    status?: number;
    sent_at_ms?: number;
  };
  return {
    erid: data.erid ?? "",
    geohash: data.geohash ?? "",
    capabilities: data.capabilities ?? 0,
    status: data.status ?? 0,
    sentAtMs: data.sent_at_ms ?? 0,
  };
}

/**
 * Canonical PresenceQuery payload serialization — MUST be byte-identical across all language ports
 * (fixtures/presence/vectors.json): snake_case keys in the field order query_id, geohash; no
 * whitespace; lowercase-dashed UUID; geohash may be "".
 */
export function serializePresenceQueryPayload(p: PresenceQueryPayload): string {
  return JSON.stringify({
    query_id: p.queryId,
    geohash: p.geohash,
  });
}

/** Parse a canonical PresenceQuery payload back into camelCase fields. */
export function deserializePresenceQueryPayload(bytes: Uint8Array): PresenceQueryPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    query_id?: string;
    geohash?: string;
  };
  return {
    queryId: data.query_id ?? "",
    geohash: data.geohash ?? "",
  };
}
