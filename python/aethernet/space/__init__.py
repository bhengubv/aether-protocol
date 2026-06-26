# SPDX-License-Identifier: MIT
"""aether-space: geo-pinned community noticeboards (Phase-2 extension)."""

from .service import (
    BreadcrumbType,
    SpaceBreadcrumb,
    ISpaceService,
    InMemorySpaceService,
    EMERGENCY_TTL_HOURS,
    MIN_TTL_HOURS,
    MAX_TTL_HOURS,
)

__all__ = [
    "BreadcrumbType",
    "SpaceBreadcrumb",
    "ISpaceService",
    "InMemorySpaceService",
    "EMERGENCY_TTL_HOURS",
    "MIN_TTL_HOURS",
    "MAX_TTL_HOURS",
]
