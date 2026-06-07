// SPDX-License-Identifier: MIT
/**
 * Default {@link IDirectoryService} implementation — in-process catalogue
 * with broadcast publish, query/response correlation by query_id, and
 * timeout-aware wait via setTimeout race. Persistence is the host's
 * responsibility (rehydrate via {@link DirectoryService.publish} on startup
 * for a non-volatile catalogue).
 *
 * Added in v1.2.0 — closes Issue #60.
 */

import { v4 as uuidv4 } from "uuid";

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import {
  ContentDescriptor,
  ContentDescriptorWire,
  descriptorFromWire,
  descriptorToWire,
} from "./ContentDescriptor.js";
import {
  DirectoryEntryAnnouncedEvent,
  IDirectoryService,
} from "./IDirectoryService.js";

/** Default ResolveAsync timeout when no value is supplied. */
export const DEFAULT_QUERY_TIMEOUT_MS = 5000;

/**
 * Wire payload for {@link PacketType.NamePublish}. snake_case JSON for
 * cross-language interop. Two modes:
 *  - Unsolicited broadcast: in_response_to_query_id is null.
 *  - Query response: in_response_to_query_id carries the query's correlation id.
 */
export interface NamePublishPayloadWire {
  name: string;
  descriptor: ContentDescriptorWire;
  in_response_to_query_id: string | null;
}

/**
 * Wire payload for {@link PacketType.NameQuery}. A broadcast request asking
 * peers to send a NamePublish for the named entry back to the sender,
 * correlated by query_id. snake_case JSON for cross-language interop.
 */
export interface NameQueryPayloadWire {
  name: string;
  query_id: string;
}

export class DirectoryService implements IDirectoryService {
  onEntryAnnounced?: (event: DirectoryEntryAnnouncedEvent) => void;

  // name -> descriptor. Plain Map (ordinal/identity name comparison —
  // application-defined opaque identifiers, not case-insensitive labels).
  private readonly catalogue = new Map<string, ContentDescriptor>();

  // Outstanding queries keyed by query_id. Resolver completes when a matching
  // NamePublish response arrives; the timeout race resolves to null otherwise.
  private readonly pendingQueries = new Map<
    string,
    (descriptor: ContentDescriptor | null) => void
  >();

  constructor(private readonly sender: IMeshSender) {}

  async publish(name: string, descriptor: ContentDescriptor): Promise<void> {
    if (!name) throw new Error("name must not be empty");
    if (!descriptor) throw new Error("descriptor must not be null");

    this.catalogue.set(name, descriptor);

    const wire: NamePublishPayloadWire = {
      name,
      descriptor: descriptorToWire(descriptor),
      in_response_to_query_id: null,
    };
    const packet = new MeshPacket();
    packet.type = PacketType.NamePublish;
    packet.sourceUhid = this.sender.localUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(JSON.stringify(wire));

    await this.sender.broadcast(packet);
  }

  async resolve(
    name: string,
    timeoutMs: number = DEFAULT_QUERY_TIMEOUT_MS,
  ): Promise<ContentDescriptor | null> {
    if (!name) throw new Error("name must not be empty");

    const cached = this.catalogue.get(name);
    if (cached) return cached;

    const queryId = uuidv4();
    const wire: NameQueryPayloadWire = { name, query_id: queryId };

    const packet = new MeshPacket();
    packet.type = PacketType.NameQuery;
    packet.sourceUhid = this.sender.localUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(JSON.stringify(wire));

    // Register the pending resolver BEFORE we broadcast, so a synchronous
    // in-process responder (test transport) can still find us.
    const waitPromise = new Promise<ContentDescriptor | null>((resolve) => {
      this.pendingQueries.set(queryId, resolve);
    });

    await this.sender.broadcast(packet);

    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const timeoutPromise = new Promise<ContentDescriptor | null>((resolve) => {
        timer = setTimeout(() => resolve(null), timeoutMs);
      });
      return await Promise.race([waitPromise, timeoutPromise]);
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      this.pendingQueries.delete(queryId);
    }
  }

  async listNames(): Promise<string[]> {
    return Array.from(this.catalogue.keys());
  }

  async handle(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.NamePublish:
        this.handlePublish(packet);
        break;
      case PacketType.NameQuery:
        await this.handleQuery(packet);
        break;
      default:
        break;
    }
  }

  private handlePublish(packet: MeshPacket): void {
    let wire: NamePublishPayloadWire | null = null;
    try {
      wire = JSON.parse(new TextDecoder().decode(packet.payload)) as NamePublishPayloadWire;
    } catch {
      return;
    }
    if (!wire || !wire.name || !wire.descriptor) return;

    const descriptor = descriptorFromWire(wire.descriptor);
    this.catalogue.set(wire.name, descriptor);

    // Query-response correlation: if this NamePublish carries an
    // in_response_to_query_id that matches one of our outstanding queries,
    // resolve that query's Promise with the descriptor.
    if (wire.in_response_to_query_id) {
      const resolver = this.pendingQueries.get(wire.in_response_to_query_id);
      if (resolver) {
        this.pendingQueries.delete(wire.in_response_to_query_id);
        resolver(descriptor);
      }
    }

    this.onEntryAnnounced?.({
      name: wire.name,
      descriptor,
      sourceUhid: packet.sourceUhid,
      announcedAtUtc: new Date().toISOString(),
    });
  }

  private async handleQuery(packet: MeshPacket): Promise<void> {
    let wire: NameQueryPayloadWire | null = null;
    try {
      wire = JSON.parse(new TextDecoder().decode(packet.payload)) as NameQueryPayloadWire;
    } catch {
      return;
    }
    if (!wire || !wire.name) return;

    const descriptor = this.catalogue.get(wire.name);
    if (!descriptor) {
      // We don't hold this name — silently ignore. Other peers may answer.
      return;
    }

    const responseWire: NamePublishPayloadWire = {
      name: wire.name,
      descriptor: descriptorToWire(descriptor),
      in_response_to_query_id: wire.query_id,
    };
    const response = new MeshPacket();
    response.type = PacketType.NamePublish;
    response.sourceUhid = this.sender.localUhid;
    response.destinationUhid = packet.sourceUhid;
    response.ttl = DEFAULT_TTL;
    response.payload = new TextEncoder().encode(JSON.stringify(responseWire));

    await this.sender.send(response, packet.sourceUhid);
  }
}
