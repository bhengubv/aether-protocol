/**
 * AODV-inspired reactive routing service.
 *
 * Lifecycle:
 *  - Callers invoke findRoute(destinationUhid) to get a route.
 *    Cached routes return immediately; otherwise an RREQ is broadcast and
 *    the call awaits the matching RREP (subject to ROUTE_TIMEOUT_MS).
 *  - Hosts pump received RREQ / RREP packets through handleRouteRequest /
 *    handleRouteReply respectively.
 *  - Hosts call prune() periodically.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL, ROUTE_EXPIRY_SECONDS, ROUTE_TIMEOUT_MS, RREQ_RATE_LIMIT_MAX, RREQ_RATE_LIMIT_WINDOW_SECONDS } from "../constants.js";
import { NodeReputationService } from "../reputation.js";
import { IncentiveProvider, NoopIncentiveProvider } from "../extensibility.js";
import { RouteEntry, isRouteExpired } from "../models/index.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "./IMeshSender.js";
import { InMemoryRouteStore, IRouteStore } from "./IRouteStore.js";
import { AcceptAllRouteReplyVerifier, IRouteReplyVerifier } from "./IRouteReplyVerifier.js";

interface PendingDiscovery {
  resolve: (route: RouteEntry | null) => void;
  reject: (reason?: unknown) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class RoutingService {
  private readonly cache = new Map<string, RouteEntry>();
  private readonly pending = new Map<string, PendingDiscovery>();
  private readonly seenRreqs = new Set<string>();
  private readonly rreqSources = new Map<string, number[]>(); // per-source epoch-seconds timestamps
  private reputation: NodeReputationService | null = null;
  private loaded = false;

  constructor(
    private readonly sender: IMeshSender,
    private readonly store: IRouteStore = new InMemoryRouteStore(),
    private readonly verifier: IRouteReplyVerifier = new AcceptAllRouteReplyVerifier(),
    private readonly incentives: IncentiveProvider = new NoopIncentiveProvider(),
  ) {}

  /** Attach an optional NodeReputationService. Pass null to disable. */
  setReputation(reputation: NodeReputationService | null): void {
    this.reputation = reputation;
  }

  async findRoute(destinationUhid: string): Promise<RouteEntry | null> {
    if (!destinationUhid) throw new Error("destinationUhid must not be empty");
    await this.ensureLoaded();

    const cached = this.cache.get(destinationUhid);
    if (cached && !isRouteExpired(cached)) return cached;

    const stored = await this.store.get(destinationUhid);
    if (stored && !isRouteExpired(stored)) {
      this.cache.set(destinationUhid, stored);
      return stored;
    }

    return this.discover(destinationUhid);
  }

  getCachedRoute(destinationUhid: string): RouteEntry | null {
    if (!destinationUhid) return null;
    const cached = this.cache.get(destinationUhid);
    if (!cached || isRouteExpired(cached)) return null;
    return cached;
  }

  getAllRoutes(): RouteEntry[] {
    return Array.from(this.cache.values()).filter((r) => !isRouteExpired(r));
  }

  async handleRouteRequest(rreq: MeshPacket): Promise<void> {
    if (rreq.type !== PacketType.RouteRequest) {
      throw new Error("expected PacketType.RouteRequest");
    }
    if (this.seenRreqs.has(rreq.id)) return;
    // Per-source RREQ rate limiting — mirrors Go/Rust RoutingService.
    const nowSec = Date.now() / 1000;
    const windowStart = nowSec - RREQ_RATE_LIMIT_WINDOW_SECONDS;
    const existing = this.rreqSources.get(rreq.sourceUhid) ?? [];
    const recent = existing.filter((ts) => ts > windowStart);
    if (recent.length >= RREQ_RATE_LIMIT_MAX) {
      this.rreqSources.set(rreq.sourceUhid, recent);
      this.reputation?.recordRreqFloodAttempt(rreq.sourceUhid);
      return; // silently drop: source is flooding unique RREQs
    }
    recent.push(nowSec);
    this.rreqSources.set(rreq.sourceUhid, recent);
    this.seenRreqs.add(rreq.id);

    const local = this.sender.localUhid;
    if (!rreq.sourceUhid || rreq.sourceUhid === local) return;

    const hopCount = Math.max(1, DEFAULT_TTL - rreq.ttl + 1);
    const reverse: RouteEntry = {
      destinationUhid: rreq.sourceUhid,
      nextHopUhid: rreq.sourceUhid,
      hopCount,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + ROUTE_EXPIRY_SECONDS * 1000),
    };
    this.cache.set(reverse.destinationUhid, reverse);
    await this.store.save(reverse);

    if (rreq.destinationUhid === local) {
      await this.sendRouteReply(local, rreq);
      return;
    }

    const known = this.cache.get(rreq.destinationUhid);
    if (known && !isRouteExpired(known)) {
      await this.sendRouteReply(rreq.destinationUhid, rreq);
      return;
    }

    if (rreq.ttl > 1) {
      rreq.ttl -= 1;
      await this.sender.broadcast(rreq);
      await this.incentives.recordRelay(local, rreq);
    }
  }

  async handleRouteReply(rrep: MeshPacket): Promise<void> {
    if (rrep.type !== PacketType.RouteReply) {
      throw new Error("expected PacketType.RouteReply");
    }
    if (!(await this.verifier.verify(rrep))) return;

    const local = this.sender.localUhid;
    if (!rrep.sourceUhid || rrep.sourceUhid === local) return;

    const hopCount = Math.max(1, DEFAULT_TTL - rrep.ttl + 1);
    const forward: RouteEntry = {
      destinationUhid: rrep.sourceUhid,
      nextHopUhid: rrep.sourceUhid,
      hopCount,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + ROUTE_EXPIRY_SECONDS * 1000),
    };
    this.cache.set(forward.destinationUhid, forward);
    await this.store.save(forward);

    if (rrep.destinationUhid === local) {
      const pending = this.pending.get(forward.destinationUhid);
      if (pending) {
        clearTimeout(pending.timer);
        this.pending.delete(forward.destinationUhid);
        pending.resolve(forward);
      }
      return;
    }

    if (rrep.ttl <= 1) return;
    const next = this.cache.get(rrep.destinationUhid);
    if (next && !isRouteExpired(next)) {
      rrep.ttl -= 1;
      const delivered = await this.sender.send(rrep, next.nextHopUhid);
      if (delivered) await this.incentives.recordRelay(local, rrep);
    }
  }

  async prune(): Promise<void> {
    for (const [k, v] of Array.from(this.cache.entries())) {
      if (isRouteExpired(v)) this.cache.delete(k);
    }
    if (this.seenRreqs.size > 10_000) this.seenRreqs.clear();
    await this.store.pruneExpired();
  }

  private async sendRouteReply(repliedSource: string, rreq: MeshPacket): Promise<void> {
    const rrep = new MeshPacket();
    rrep.type = PacketType.RouteReply;
    rrep.sourceUhid = repliedSource;
    rrep.destinationUhid = rreq.sourceUhid;
    rrep.ttl = DEFAULT_TTL;
    rrep.payload = rreq.payload;

    const reverse = this.cache.get(rreq.sourceUhid);
    if (reverse && !isRouteExpired(reverse)) {
      await this.sender.send(rrep, reverse.nextHopUhid);
    } else {
      await this.sender.broadcast(rrep);
    }
  }

  private async discover(destinationUhid: string): Promise<RouteEntry | null> {
    const rreq = new MeshPacket();
    rreq.type = PacketType.RouteRequest;
    rreq.sourceUhid = this.sender.localUhid;
    rreq.destinationUhid = destinationUhid;
    rreq.ttl = DEFAULT_TTL;

    const fanout = await this.sender.broadcast(rreq);
    if (fanout === 0) return null;

    return new Promise<RouteEntry | null>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(destinationUhid);
        resolve(null);
      }, ROUTE_TIMEOUT_MS);
      this.pending.set(destinationUhid, { resolve, reject, timer });
    });
  }

  private async ensureLoaded(): Promise<void> {
    if (this.loaded) return;
    this.loaded = true;
    try {
      for (const r of await this.store.getAll()) {
        if (!isRouteExpired(r)) this.cache.set(r.destinationUhid, r);
      }
    } catch {
      this.loaded = false;
    }
  }
}
