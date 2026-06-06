// SPDX-License-Identifier: MIT
namespace AetherMesh.Space.Models;

/// <summary>Category of a geo-pinned <see cref="SpaceBreadcrumb"/>.</summary>
public enum BreadcrumbType : byte
{
    /// <summary>General community notice (default).</summary>
    Notice = 0,
    /// <summary>Emergency alert — bypasses 3-cell flood-guard; TTL extended to 720 h.</summary>
    Emergency = 1,
    /// <summary>Commercial listing or market offer.</summary>
    Commerce = 2,
    /// <summary>Local event announcement.</summary>
    Event = 3,
    /// <summary>Job posting or opportunity.</summary>
    JobPosting = 4,
}
