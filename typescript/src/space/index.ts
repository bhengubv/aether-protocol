/**
 * aether-space geo-pinned noticeboard: in-memory store + WIRE binding
 * (PacketType.SpaceBreadcrumb = 40).
 * SPDX-License-Identifier: MIT
 */

export {
  BreadcrumbType,
  EMERGENCY_TTL_HOURS,
  MIN_TTL_HOURS,
  MAX_TTL_HOURS,
  InMemorySpaceService,
  breadcrumbExpiresAtUtc,
  breadcrumbIsExpired,
} from "./SpaceService.js";
export type { SpaceBreadcrumb, ISpaceService } from "./SpaceService.js";
export {
  SpaceBreadcrumbService,
  serializeSpaceBreadcrumbPayload,
  deserializeSpaceBreadcrumbPayload,
  breadcrumbToPayload,
  payloadToBreadcrumb,
} from "./SpaceBreadcrumbService.js";
export type { SpaceBreadcrumbPayload } from "./SpaceBreadcrumbService.js";
