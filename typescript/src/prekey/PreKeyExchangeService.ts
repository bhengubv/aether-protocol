/**
 * Default mesh pre-key exchange service (PacketType.PreKeyRequest = 25 / PreKeyResponse = 26).
 *
 * Directed request/response — never broadcast — so bundle requests do not leak identity-interest
 * to the whole mesh. A node publishes its current bundle via setLocalBundle (the host produces it
 * with SignalProtocol.generatePreKeyBundle); a peer asks for it with requestBundle; the responder
 * replies with its bundle; the requester caches it and fires onBundleReceived.
 *
 * TRANSPORT ONLY: the host performs the actual X3DH by feeding the received bundle to
 * SignalProtocol.processPreKeyBundle (Signal-canonical — no key agreement happens here).
 *
 * Mirrors the C# PreKeyExchangeService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import type { PreKeyBundle } from "../security/SignalProtocol.js";
import {
  PreKeyBundleReceived,
  PreKeyRequestPayload,
  PreKeyResponsePayload,
  responsePayloadFromBundle,
  responsePayloadToBundle,
} from "./models.js";

export class PreKeyExchangeService {
  /** Raised when a peer's pre-key bundle arrives in a PreKeyResponse. */
  onBundleReceived?: (received: PreKeyBundleReceived) => void;

  private local?: PreKeyBundle;
  private readonly received = new Map<string, PreKeyBundle>();

  constructor(private readonly sender: IMeshSender) {}

  /** Set (or replace) this node's published bundle — served in reply to inbound requests. */
  setLocalBundle(bundle: PreKeyBundle): void {
    this.local = bundle;
  }

  /** The currently-published local bundle, or undefined if none has been set. */
  getLocalBundle(): PreKeyBundle | undefined {
    return this.local;
  }

  /** The most recently received bundle for `uhid`, or undefined. */
  getReceivedBundle(uhid: string): PreKeyBundle | undefined {
    return this.received.get(uhid);
  }

  /**
   * Ask `peerUhid` for its pre-key bundle: mint a request id and directed-send a PreKeyRequest.
   * Returns the new request id (lowercase-dashed UUID) — the responder echoes it in the response.
   */
  async requestBundle(peerUhid: string): Promise<string> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");

    const requestId = crypto.randomUUID();
    const payload: PreKeyRequestPayload = {
      requestId,
      requesterUhid: this.sender.localUhid,
    };

    const packet = new MeshPacket();
    packet.type = PacketType.PreKeyRequest;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(serializePreKeyRequestPayload(payload));

    await this.sender.send(packet, peerUhid);
    return requestId;
  }

  /**
   * Process an incoming pre-key packet. On PreKeyRequest, reply with the local bundle (if set). On
   * PreKeyResponse, cache the peer bundle and fire onBundleReceived. Returns false for the wrong
   * packet type, a malformed payload, or a request received when no local bundle is set.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    switch (packet.type) {
      case PacketType.PreKeyRequest:
        return this.handleRequest(packet);
      case PacketType.PreKeyResponse:
        return this.handleResponse(packet);
      default:
        return false;
    }
  }

  private async handleRequest(packet: MeshPacket): Promise<boolean> {
    let body: PreKeyRequestPayload | undefined;
    try {
      body = deserializePreKeyRequestPayload(packet.payload);
    } catch {
      return false;
    }
    if (!body) return false;

    const local = this.local;
    if (!local) return false;

    const replyTo = body.requesterUhid || packet.sourceUhid;
    const payload = responsePayloadFromBundle(body.requestId, local);

    const reply = new MeshPacket();
    reply.type = PacketType.PreKeyResponse;
    reply.sourceUhid = this.sender.localUhid;
    reply.destinationUhid = replyTo;
    reply.ttl = DEFAULT_TTL;
    reply.payload = new TextEncoder().encode(serializePreKeyResponsePayload(payload));

    await this.sender.send(reply, replyTo);
    return true;
  }

  private async handleResponse(packet: MeshPacket): Promise<boolean> {
    let body: PreKeyResponsePayload | undefined;
    try {
      body = deserializePreKeyResponsePayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.uhid) return false;

    const bundle = responsePayloadToBundle(body);
    this.received.set(body.uhid, bundle);
    this.onBundleReceived?.({
      requestId: body.requestId,
      fromUhid: packet.sourceUhid,
      bundle,
    });
    return true;
  }
}

/**
 * Canonical PreKeyRequest payload serialization — MUST be byte-identical across all language
 * ports (fixtures/prekey/vectors.json): snake_case keys, field order request_id, requester_uhid,
 * no whitespace, lowercase-dashed UUID.
 */
export function serializePreKeyRequestPayload(p: PreKeyRequestPayload): string {
  return JSON.stringify({
    request_id: p.requestId,
    requester_uhid: p.requesterUhid,
  });
}

/** Parse a canonical PreKeyRequest payload back into camelCase fields. */
export function deserializePreKeyRequestPayload(bytes: Uint8Array): PreKeyRequestPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    request_id?: string;
    requester_uhid?: string;
  };
  return {
    requestId: data.request_id ?? "",
    requesterUhid: data.requester_uhid ?? "",
  };
}

/**
 * Canonical PreKeyResponse payload serialization — MUST be byte-identical across all language
 * ports (fixtures/prekey/vectors.json): snake_case keys in the field order request_id, uhid,
 * identity_key, identity_key_x25519, pre_key_id, pre_key, signed_pre_key_id, signed_pre_key,
 * signed_pre_key_signature; no whitespace; lowercase-dashed UUID; integer ids bare; every
 * public-key byte field as STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding).
 */
export function serializePreKeyResponsePayload(p: PreKeyResponsePayload): string {
  return JSON.stringify({
    request_id: p.requestId,
    uhid: p.uhid,
    identity_key: toBase64(p.identityKey),
    identity_key_x25519: toBase64(p.identityKeyX25519),
    pre_key_id: p.preKeyId,
    pre_key: toBase64(p.preKey),
    signed_pre_key_id: p.signedPreKeyId,
    signed_pre_key: toBase64(p.signedPreKey),
    signed_pre_key_signature: toBase64(p.signedPreKeySignature),
  });
}

/** Parse a canonical PreKeyResponse payload back into camelCase fields (base64 -> bytes). */
export function deserializePreKeyResponsePayload(bytes: Uint8Array): PreKeyResponsePayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    request_id?: string;
    uhid?: string;
    identity_key?: string;
    identity_key_x25519?: string;
    pre_key_id?: number;
    pre_key?: string;
    signed_pre_key_id?: number;
    signed_pre_key?: string;
    signed_pre_key_signature?: string;
  };
  return {
    requestId: data.request_id ?? "",
    uhid: data.uhid ?? "",
    identityKey: fromBase64(data.identity_key),
    identityKeyX25519: fromBase64(data.identity_key_x25519),
    preKeyId: data.pre_key_id ?? 0,
    preKey: fromBase64(data.pre_key),
    signedPreKeyId: data.signed_pre_key_id ?? 0,
    signedPreKey: fromBase64(data.signed_pre_key),
    signedPreKeySignature: fromBase64(data.signed_pre_key_signature),
  };
}

/** Uint8Array -> STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding). */
function toBase64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

/** STANDARD base64 -> Uint8Array (empty for undefined). */
function fromBase64(b64: string | undefined): Uint8Array {
  return b64 ? new Uint8Array(Buffer.from(b64, "base64")) : new Uint8Array();
}
