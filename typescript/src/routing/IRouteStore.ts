/**
 * Route store abstraction + in-memory default.
 * SPDX-License-Identifier: MIT
 */

import { RouteEntry, isRouteExpired } from "../models/index.js";

export interface IRouteStore {
  get(destinationUhid: string): Promise<RouteEntry | null>;
  getAll(): Promise<RouteEntry[]>;
  save(route: RouteEntry): Promise<void>;
  remove(destinationUhid: string): Promise<void>;
  pruneExpired(): Promise<number>;
}

export class InMemoryRouteStore implements IRouteStore {
  private readonly routes = new Map<string, RouteEntry>();

  async get(destinationUhid: string): Promise<RouteEntry | null> {
    return this.routes.get(destinationUhid) ?? null;
  }

  async getAll(): Promise<RouteEntry[]> {
    return Array.from(this.routes.values());
  }

  async save(route: RouteEntry): Promise<void> {
    this.routes.set(route.destinationUhid, route);
  }

  async remove(destinationUhid: string): Promise<void> {
    this.routes.delete(destinationUhid);
  }

  async pruneExpired(): Promise<number> {
    let pruned = 0;
    const now = new Date();
    for (const [k, v] of Array.from(this.routes.entries())) {
      if (isRouteExpired(v, now)) {
        this.routes.delete(k);
        pruned++;
      }
    }
    return pruned;
  }
}
