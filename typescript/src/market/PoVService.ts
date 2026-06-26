// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory).
// TypeScript port of AetherNet.Market.IPoVService / InMemoryPoVService. Two users
// meet physically; their devices exchange a signed token over a short-range
// transport (BLE/NFC/NearLink). Over time a directed trust graph maps how many
// distinct humans have verified a profile.
//
// Signatures are REAL Ed25519 (Ed25519Service / TweetNaCl) over the canonical
// token body (buildSignableTokenData = "SubjectUhid + TimestampTicks + Transport").
// The single-node service holds one identity key and produces both the witness and
// subject signatures with it; the two-party mesh exchange (each side counter-signs
// with its own key) is PoVTokenExchangeService.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity
// signal — it attaches NO value semantics and never touches any money/reward layer.

import { Ed25519Service } from "../security/Ed25519Service.js";
import {
  PoVToken,
  PoVTransportType,
  buildSignableTokenData,
  unixMsToTicks,
  type PoVScore,
} from "./PoVToken.js";

/** The Proof-of-Vicinity trust service. */
export interface IPoVService {
  issueToken(witnessUhid: string, subjectUhid: string, transport?: PoVTransportType): Promise<PoVToken>;
  acceptToken(token: PoVToken): Promise<void>;
  getScore(uhid: string): Promise<PoVScore>;
  verifyToken(token: PoVToken): Promise<boolean>;
  reportDefection(witnessUhid: string, defectorUhid: string): Promise<void>;
}

/** Single-node, in-memory {@link IPoVService} for testing / single-node scenarios. */
export class InMemoryPoVService implements IPoVService {
  private readonly tokensBySubject = new Map<string, PoVToken[]>();
  private readonly scoreOverrides = new Map<string, number>();
  private readonly privateKey: Uint8Array;
  private readonly publicKey: Uint8Array;

  /** Fires when a token is issued or accepted. */
  onTokenReceived?: (token: PoVToken) => void;

  constructor() {
    const kp = Ed25519Service.generateKeyPair();
    this.privateKey = kp.privateKey;
    this.publicKey = kp.publicKey;
  }

  async issueToken(
    witnessUhid: string,
    subjectUhid: string,
    transport: PoVTransportType = PoVTransportType.Ble,
  ): Promise<PoVToken> {
    const ticks = unixMsToTicks(BigInt(Date.now()));
    const signable = buildSignableTokenData(subjectUhid, ticks, transport);
    // REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node model).
    const sig = Ed25519Service.sign(this.privateKey, signable);

    const token = new PoVToken({
      witnessUhid,
      subjectUhid,
      timestampTicks: ticks,
      transportUsed: transport,
      witnessSignature: sig,
      subjectSignature: sig,
    });
    this.onTokenReceived?.(token);
    return token;
  }

  async acceptToken(token: PoVToken): Promise<void> {
    // Record only a token that cryptographically verifies — both signatures valid + distinct parties.
    if (!(await this.verifyToken(token))) return;
    const list = this.tokensBySubject.get(token.subjectUhid) ?? [];
    list.push(token);
    this.tokensBySubject.set(token.subjectUhid, list);
    this.onTokenReceived?.(token);
  }

  async getScore(uhid: string): Promise<PoVScore> {
    const tokens = this.tokensBySubject.get(uhid) ?? [];
    const override = this.scoreOverrides.get(uhid);

    if (tokens.length === 0) {
      // A UHID with no inbound tokens still surfaces a stored defection override.
      return { uhid, uniqueWitnesses: 0, weightedScore: override ?? 0, lastUpdated: new Date() };
    }

    const unique = new Set(tokens.map((t) => t.witnessUhid)).size;
    // Sigmoid-ish: w / (w + 1).
    let score = unique / (unique + 1);
    if (override !== undefined) score = override;
    return { uhid, uniqueWitnesses: unique, weightedScore: score, lastUpdated: new Date() };
  }

  async verifyToken(token: PoVToken): Promise<boolean> {
    // Structural: both parties signed, both UHIDs present, and distinct.
    if (
      !token.witnessSignature?.length ||
      !token.subjectSignature?.length ||
      !token.witnessUhid ||
      !token.subjectUhid ||
      token.witnessUhid === token.subjectUhid
    ) {
      return false;
    }
    // Cryptographic: BOTH signatures valid over the canonical body.
    const signable = token.signableData();
    return (
      Ed25519Service.verify(this.publicKey, signable, token.witnessSignature) &&
      Ed25519Service.verify(this.publicKey, signable, token.subjectSignature)
    );
  }

  async reportDefection(witnessUhid: string, _defectorUhid: string): Promise<void> {
    const score = await this.getScore(witnessUhid);
    this.scoreOverrides.set(witnessUhid, score.weightedScore * 0.8);
  }
}
