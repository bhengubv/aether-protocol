/**
 * On-mesh Proof-of-Vicinity token exchange — the directed, two-key
 * witness→subject co-presence proof, carried over PacketType.PoVTokenExchange
 * (43). TypeScript port of AetherNet.Market.PoVTokenExchangeService. Mirrors the
 * AetherNet handler idiom established by MeshTipService (sign payload with the
 * identity key → wrap in a signed MeshPacket → send) and ReputationGossipService
 * (verify the enclosing packet against the supplied sender public key, which also
 * enforces freshness + nonce replay-dedup).
 *
 * CRYPTO: signatures are real Ed25519 over the canonical token body
 * (buildSignableTokenData = "SubjectUhid + TimestampTicks + Transport"),
 * byte-identical to every other language implementation, so a token exchanged
 * here interoperates on one mesh.
 *
 * SEPARATION: the resulting PoVScore is a purely local anti-Sybil
 * routing/identity signal. It attaches NO value semantics and never touches any
 * money/reward layer.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import {
  PoVScore,
  PoVToken,
  PoVTransportType,
  buildSignableTokenData,
  isShortRange,
  transportToString,
  unixMsToTicks,
} from "./PoVToken.js";

/** The minimal mesh transport surface needed by PoVTokenExchangeService. */
export interface PoVMeshSender {
  /** UHID of the local node. */
  readonly localUhid: string;
  /**
   * Delivers `packet` toward `subjectUhid` (directed — one short-range hop).
   * Returns true on success.
   */
  send(packet: MeshPacket, subjectUhid: string): Promise<boolean>;
}

/**
 * Signs and verifies the enclosing MeshPacket envelope. `verifyPacket` MUST also
 * enforce freshness and nonce replay-dedup (mirroring the C#
 * IPacketSigningService), so a replayed or stale PoV exchange is rejected here
 * before any crypto on the body.
 */
export interface PoVPacketSigner {
  /** Returns `packet` with the signature/nonce/timestamp fields populated. */
  signPacket(packet: MeshPacket): MeshPacket;
  /**
   * Verifies `packet`'s envelope signature against `senderPublicKey` AND enforces
   * freshness + replay-dedup. Returns true only for a fresh, correctly-signed,
   * non-replayed packet.
   */
  verifyPacket(packet: MeshPacket, senderPublicKey: Uint8Array): boolean;
}

/** Signs/verifies canonical token bodies with Ed25519 identity keys. */
export interface PoVIdentitySigner {
  /** Produces a 64-byte Ed25519 signature over `data` using the local identity key. */
  signData(data: Uint8Array): Uint8Array;
  /** Verifies `sig` over `data` against `publicKey`. */
  verifySignature(
    publicKey: Uint8Array,
    data: Uint8Array,
    sig: Uint8Array,
  ): boolean;
}

/** Optional diagnostic sink. */
export interface PoVLogger {
  log(message: string): void;
}

/** A clock seam so issuance timestamps are testable; defaults to Date.now(). */
export type NowMsProvider = () => bigint;

/** Issues and accepts on-mesh PoV tokens over packet type 43. */
export class PoVTokenExchangeService {
  private readonly sender: PoVMeshSender;
  private readonly signer: PoVPacketSigner;
  private readonly identity: PoVIdentitySigner;
  private readonly logger: PoVLogger | null;
  private readonly nowMs: NowMsProvider;

  private readonly tokensBySubject = new Map<string, PoVToken[]>();

  /** Fires once a counter-signed token has been recorded locally. */
  onTokenReceived: ((token: PoVToken) => void) | null = null;

  constructor(
    sender: PoVMeshSender,
    signer: PoVPacketSigner,
    identity: PoVIdentitySigner,
    logger: PoVLogger | null = null,
    nowMs: NowMsProvider | null = null,
  ) {
    this.sender = sender;
    this.signer = signer;
    this.identity = identity;
    this.logger = logger;
    this.nowMs = nowMs ?? (() => BigInt(Date.now()));
  }

  private logMsg(message: string): void {
    this.logger?.log(message);
  }

  /**
   * Mints a witness-signed PoV token for `subjectUhid` and sends it directed
   * (TTL 1) over packet 43. Refuses to mint over a non-short-range transport or
   * to vouch for itself. Returns the token that was issued (with an empty subject
   * signature — the subject fills it on receipt), or null when issuance was
   * refused.
   */
  async issueToken(
    subjectUhid: string,
    transport: PoVTransportType,
  ): Promise<PoVToken | null> {
    if (!subjectUhid) {
      this.logMsg("PoV issue skipped — empty subject UHID");
      return null;
    }

    // ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range
    // channel.
    if (!isShortRange(transport)) {
      this.logMsg(
        `PoV issue refused — transport ${transportToString(transport)} is not short-range`,
      );
      return null;
    }

    const localUhid = this.sender.localUhid;
    if (!localUhid) {
      this.logMsg("PoV issue skipped — local node not initialized");
      return null;
    }

    // A node cannot vouch for itself — that would be a free, unbounded
    // self-attestation.
    if (localUhid === subjectUhid) {
      this.logMsg("PoV issue refused — witness and subject are the same node");
      return null;
    }

    const timestampTicks = unixMsToTicks(this.nowMs());

    // Witness signs the canonical token body with the node's REAL Ed25519
    // identity key.
    const witnessSig = this.identity.signData(
      buildSignableTokenData(subjectUhid, timestampTicks, transport),
    );

    const token = new PoVToken({
      witnessUhid: localUhid,
      subjectUhid,
      timestampTicks,
      transportUsed: transport,
      witnessSignature: witnessSig,
      subjectSignature: null, // filled by the subject when it counter-signs on receipt.
    });

    const body = new TextEncoder().encode(token.toJSON());

    const packet = new MeshPacket();
    packet.type = PacketType.PoVTokenExchange;
    packet.sourceUhid = localUhid;
    packet.destinationUhid = subjectUhid; // directed — NOT a broadcast.
    packet.ttl = 1; // co-present: the subject is one short-range hop away.
    packet.payload = body;

    const signed = this.signer.signPacket(packet);

    const sent = await this.sender.send(signed, subjectUhid);

    this.logMsg(
      `PoV token issued: witness=${localUhid} subject=${subjectUhid} transport=${transportToString(transport)} sent=${sent}`,
    );
    return token;
  }

  /**
   * Processes an inbound PoV exchange packet (type 43).
   *
   * Returns `true` when the token was accepted, counter-signed, and recorded.
   * Returns `false` when the packet should be silently discarded (wrong type,
   * bad/stale/replayed envelope, malformed payload, self-echo, not addressed to
   * us, missing/invalid witness signature, witness == subject).
   */
  handleTokenExchange(
    packet: MeshPacket | null,
    senderPublicKey: Uint8Array | null,
  ): boolean {
    if (!packet || !senderPublicKey) {
      return false;
    }
    if (packet.type !== PacketType.PoVTokenExchange) {
      this.logMsg(
        `PoV exchange: unexpected packet type ${packet.type} — ignored`,
      );
      return false;
    }

    // 1. Verify the enclosing MeshPacket signature (also enforces freshness +
    //    nonce replay-dedup).
    if (!this.signer.verifyPacket(packet, senderPublicKey)) {
      this.logMsg(
        `PoV exchange from ${packet.sourceUhid}: packet signature invalid/stale/replayed — dropped`,
      );
      return false;
    }

    // 2. Deserialise the token body.
    let token: PoVToken;
    try {
      token = PoVToken.parse(packet.payload);
    } catch (err) {
      this.logMsg(
        `PoV exchange from ${packet.sourceUhid}: JSON deserialization failed — dropped: ${err}`,
      );
      return false;
    }
    if (!token.witnessUhid || !token.subjectUhid) {
      this.logMsg(
        `PoV exchange from ${packet.sourceUhid}: payload missing required fields — dropped`,
      );
      return false;
    }

    // 3. The incoming token must already carry the witness's signature.
    if (!token.witnessSignature || token.witnessSignature.length === 0) {
      this.logMsg(
        `PoV exchange from ${token.witnessUhid}: token has no witness signature — dropped`,
      );
      return false;
    }

    const localUhid = this.sender.localUhid;

    // 4. Ignore our own token echoed back to us (witness == us).
    if (localUhid && token.witnessUhid === localUhid) {
      return false;
    }

    // 5. The token must be addressed to us — we are the subject being vouched for.
    if (localUhid && token.subjectUhid !== localUhid) {
      this.logMsg(
        `PoV exchange: token subject ${token.subjectUhid} is not us — ignored`,
      );
      return false;
    }

    // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against
    //    the verified sender key (the witness is the packet source, so the
    //    envelope and the body share a signing key).
    const signable = token.signableData();
    if (
      !this.identity.verifySignature(
        senderPublicKey,
        signable,
        token.witnessSignature,
      )
    ) {
      this.logMsg(
        `PoV exchange from ${token.witnessUhid}: witness Ed25519 signature invalid — dropped`,
      );
      return false;
    }

    // 6b. A witness must not be vouching for itself — distinct parties is a hard
    //     PoV invariant.
    if (token.witnessUhid === token.subjectUhid) {
      this.logMsg(
        `PoV exchange from ${token.witnessUhid}: witness == subject — dropped`,
      );
      return false;
    }

    // 7. Counter-sign the SAME canonical body as the subject, with our REAL
    //    Ed25519 identity key.
    token.subjectSignature = this.identity.signData(signable);

    // 8. Record it (increments the witness's contribution to OUR score) and
    //    notify.
    this.recordToken(token);
    this.onTokenReceived?.(token);

    this.logMsg(
      `PoV token accepted: witness=${token.witnessUhid} subject=${token.subjectUhid} transport=${transportToString(token.transportUsed)}`,
    );
    return true;
  }

  /** Returns the local PoV trust score for `uhid`, derived from recorded tokens. */
  getScore(uhid: string): PoVScore {
    const tokens = this.tokensBySubject.get(uhid) ?? [];

    const witnesses = new Set<string>();
    for (const t of tokens) {
      witnesses.add(t.witnessUhid);
    }
    const unique = witnesses.size;

    const weighted = unique > 0 ? unique / (unique + 1.0) : 0.0;

    return {
      uhid,
      uniqueWitnesses: unique,
      weightedScore: weighted,
      lastUpdated: new Date(),
    };
  }

  private recordToken(token: PoVToken): void {
    const list = this.tokensBySubject.get(token.subjectUhid);
    if (list) {
      list.push(token);
    } else {
      this.tokensBySubject.set(token.subjectUhid, [token]);
    }
  }

  /**
   * Sorted list of subject UHIDs with at least one recorded token. Mainly useful
   * for tests and diagnostics.
   */
  acceptedSubjects(): string[] {
    return [...this.tokensBySubject.keys()].sort();
  }
}
