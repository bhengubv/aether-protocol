// SPDX-License-Identifier: MIT
//
// Offline-capable P2P marketplace (aether-market Phase-2 extension). TypeScript
// port of AetherNet.Market.IMarketService / InMemoryMarketService and the
// listing/escrow models. Listings are geo-pinned (distributed via aether-space)
// and may carry a VaultManifest escrow for document-backed sales; trades run a
// two-party confirm state machine. Requires aether-space and aether-vault.

import { randomUUID } from "node:crypto";
import type { VaultManifest } from "../vault/VaultService.js";
import type { PoVScore } from "./PoVToken.js";

/** Category of a {@link MarketListing}. */
export enum MarketCategory {
  Goods = 0,
  Services = 1,
  Labour = 2,
  Land = 3,
  Documents = 4,
}

/** Role of the node confirming a trade step. */
export enum TradeRole {
  Buyer = 0,
  Seller = 1,
}

/** State machine for a {@link TradeEscrow}. */
export enum TradeState {
  Initiated = 0,
  BuyerConfirmed = 1,
  SellerConfirmed = 2,
  Complete = 3,
  Disputed = 4,
}

/**
 * A geo-pinned market listing dropped by a verified seller. Distributed via
 * aether-space; may include a VaultManifest escrow for document-backed sales.
 */
export interface MarketListing {
  listingId: string;
  sellerUhid: string;
  /** Seller's PoV trust score at the time of listing. */
  sellerPoVScore?: PoVScore;
  title: string;
  description: string;
  /** Price in South African Rand. */
  priceZAR: number;
  /** 6-character geohash of the listing location. */
  geoHash: string;
  category: MarketCategory;
  /** Optional Vault escrow for document-backed transactions. */
  escrowManifest?: VaultManifest;
  createdAtUtc: Date;
  expiresAtUtc: Date;
}

/** Whether the listing has reached its expiry. */
export function isListingExpired(listing: MarketListing): boolean {
  return Date.now() >= listing.expiresAtUtc.getTime();
}

/** Tracks the lifecycle of a marketplace trade. */
export interface TradeEscrow {
  escrowId: string;
  listingId: string;
  buyerUhid: string;
  sellerUhid: string;
  state: TradeState;
  vaultManifest?: VaultManifest;
  createdAtUtc: Date;
}

/** The offline-capable P2P marketplace. */
export interface IMarketService {
  createListing(
    sellerUhid: string,
    title: string,
    description: string,
    priceZAR: number,
    geoHash: string,
    category: MarketCategory,
  ): Promise<MarketListing>;
  browseNearby(centerGeoHash: string, radiusCells?: number): Promise<MarketListing[]>;
  search(query: string, category?: MarketCategory): Promise<MarketListing[]>;
  initiateTrade(listing: MarketListing, buyerUhid: string): Promise<TradeEscrow>;
  confirmTrade(escrow: TradeEscrow, role: TradeRole): Promise<TradeEscrow>;
  dispute(escrow: TradeEscrow, reason: string): Promise<void>;
}

const THIRTY_DAYS_MS = 30 * 24 * 60 * 60 * 1000;

/** In-memory {@link IMarketService} for testing / single-node use. */
export class InMemoryMarketService implements IMarketService {
  private readonly listings = new Map<string, MarketListing>();
  private readonly escrows = new Map<string, TradeEscrow>();

  /** Fired when a new listing is received from the mesh or created locally. */
  onListingReceived?: (listing: MarketListing) => void;

  async createListing(
    sellerUhid: string,
    title: string,
    description: string,
    priceZAR: number,
    geoHash: string,
    category: MarketCategory,
  ): Promise<MarketListing> {
    const now = new Date();
    const listing: MarketListing = {
      listingId: randomUUID(),
      sellerUhid,
      title,
      description,
      priceZAR,
      geoHash,
      category,
      createdAtUtc: now,
      expiresAtUtc: new Date(now.getTime() + THIRTY_DAYS_MS),
    };
    this.listings.set(listing.listingId, listing);
    this.onListingReceived?.(listing);
    return listing;
  }

  async browseNearby(centerGeoHash: string, radiusCells = 2): Promise<MarketListing[]> {
    const prefixLen = Math.min(
      centerGeoHash.length,
      Math.max(1, centerGeoHash.length - radiusCells + 1),
    );
    const prefix = centerGeoHash.slice(0, prefixLen).toLowerCase();
    return [...this.listings.values()].filter(
      (l) => !isListingExpired(l) && l.geoHash.toLowerCase().startsWith(prefix),
    );
  }

  async search(query: string, category?: MarketCategory): Promise<MarketListing[]> {
    const q = query.toLowerCase();
    return [...this.listings.values()].filter(
      (l) =>
        !isListingExpired(l) &&
        (category === undefined || l.category === category) &&
        (l.title.toLowerCase().includes(q) || l.description.toLowerCase().includes(q)),
    );
  }

  async initiateTrade(listing: MarketListing, buyerUhid: string): Promise<TradeEscrow> {
    const escrow: TradeEscrow = {
      escrowId: randomUUID(),
      listingId: listing.listingId,
      buyerUhid,
      sellerUhid: listing.sellerUhid,
      state: TradeState.Initiated,
      vaultManifest: listing.escrowManifest,
      createdAtUtc: new Date(),
    };
    this.escrows.set(escrow.escrowId, escrow);
    return escrow;
  }

  async confirmTrade(escrow: TradeEscrow, role: TradeRole): Promise<TradeEscrow> {
    if (role === TradeRole.Buyer) {
      escrow.state = TradeState.BuyerConfirmed;
    } else {
      escrow.state =
        escrow.state === TradeState.BuyerConfirmed ? TradeState.Complete : TradeState.SellerConfirmed;
    }
    this.escrows.set(escrow.escrowId, escrow);
    return escrow;
  }

  async dispute(escrow: TradeEscrow, _reason: string): Promise<void> {
    escrow.state = TradeState.Disputed;
    this.escrows.set(escrow.escrowId, escrow);
  }
}
