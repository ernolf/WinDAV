// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// Why an operation on a store failed, in the terms a file system can act on.
/// </summary>
/// <remarks>
/// These are the cases a caller has to tell apart, not a translation of any protocol. A
/// provider maps whatever its server said onto one of them; what it said stays inside the
/// provider.
/// </remarks>
public enum ProviderError
{
    /// <summary>The reason is not one of the others.</summary>
    Unknown,

    /// <summary>There is nothing at that path.</summary>
    NotFound,

    /// <summary>There is already something at that path.</summary>
    AlreadyExists,

    /// <summary>The caller is not allowed to do this, or is not who it claimed to be.</summary>
    PermissionDenied,

    /// <summary>
    /// The resource changed since the caller last saw it, so the write was refused rather
    /// than overwriting somebody else's version.
    /// </summary>
    PreconditionFailed,

    /// <summary>
    /// The path cannot be used as asked, typically because the directory above it does not
    /// exist or because a directory is not empty.
    /// </summary>
    Conflict,

    /// <summary>The store has no room left.</summary>
    InsufficientStorage,

    /// <summary>The store could not be reached at all.</summary>
    Unreachable,

    /// <summary>The store answered, but not in a way that can be made sense of.</summary>
    Protocol,
}
