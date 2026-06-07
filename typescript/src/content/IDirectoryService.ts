// SPDX-License-Identifier: MIT
/**
 * Application-layer name -> ContentDescriptor resolver. Closes the Wave-16
 * protocol gap: chunk-fetch services are content-addressed (rootHash-keyed) —
 * consumers that want to fetch content by an application-layer name (e.g.
 * "podcast:abc123", "reel:hash", "album:artist/title") cannot do so by hash
 * alone because they do not know the rootHash upfront. That's precisely what
 * they're trying to discover.
 *
 * This service maintains a local name catalogue, broadcasts NamePublish when
 * the local node publishes a binding, emits NameQuery when the local node
 * needs to resolve an unknown name, and unicasts a NamePublish response when
 * a peer's query matches an entry we hold.
 *
 * Added in v1.2.0 — closes Issue #60.
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { ContentDescriptor } from "./ContentDescriptor.js";

/**
 * Event payload for {@link IDirectoryService.onEntryAnnounced} — raised when
 * a NamePublish packet arrives and the local catalogue learns a new (or
 * replaced) name -> descriptor binding.
 */
export interface DirectoryEntryAnnouncedEvent {
  /** The newly-learned application-layer name. */
  name: string;
  /** The descriptor the name resolves to. */
  descriptor: ContentDescriptor;
  /** UHID of the peer that emitted the announcement. */
  sourceUhid: string;
  /** ISO-8601 UTC timestamp at which the announcement arrived locally. */
  announcedAtUtc: string;
}

/**
 * Application-layer name resolver. Hosts pump inbound NamePublish / NameQuery
 * packets in via {@link handle}, then call {@link publish} and {@link resolve}
 * from the app layer.
 */
export interface IDirectoryService {
  /**
   * Raised when a NamePublish packet arrives — either an unsolicited
   * broadcast from a peer or a unicast response to one of our outstanding
   * queries — and updates the local catalogue. Matches the existing
   * DtnService event idiom (assignable callback property).
   */
  onEntryAnnounced?: (event: DirectoryEntryAnnouncedEvent) => void;

  /**
   * Store the binding locally and broadcast a NamePublish to every connected
   * peer. Subsequent {@link resolve} calls on the local node return the
   * descriptor immediately from the catalogue.
   */
  publish(name: string, descriptor: ContentDescriptor): Promise<void>;

  /**
   * Resolve a name to its descriptor. Returns the local-catalogue hit
   * immediately if present. Otherwise broadcasts a NameQuery and awaits a
   * matching NamePublish response up to {@link timeoutMs} (default 5000 ms).
   * Returns null on timeout.
   */
  resolve(name: string, timeoutMs?: number): Promise<ContentDescriptor | null>;

  /** Enumerate every name currently in the local catalogue (snapshot). */
  listNames(): Promise<string[]>;

  /**
   * Pump inbound NamePublish / NameQuery packets into the service. Hosts wire
   * this from their transport's receive pump.
   */
  handle(packet: MeshPacket): Promise<void>;
}
