// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// What may be done with an entry, as far as the store is willing to say.
/// </summary>
/// <remarks>
/// <para>
/// These are the store's rules, not the ones of the machine the mount is on. A store that
/// says nothing leaves <see cref="RemoteEntry.Permissions"/> at <see langword="null"/>,
/// which is not the same as <see cref="None"/>: the first means it was never asked or never
/// answered, the second that the answer was no to everything.
/// </para>
/// <para>
/// A store that grants something the seam has no name for grants it silently. Nothing here
/// stands for what an entry is rather than what may be done with it: that a store calls an
/// entry shared, or mounted from elsewhere, has no place among permissions.
/// </para>
/// </remarks>
[Flags]
public enum EntryPermissions
{
    /// <summary>Nothing at all.</summary>
    None = 0,

    /// <summary>The entry can be read, and a directory can be listed.</summary>
    Read = 1,

    /// <summary>The contents of a file can be replaced.</summary>
    Write = 2,

    /// <summary>The entry can be deleted.</summary>
    Delete = 4,

    /// <summary>The entry can be given another name where it is.</summary>
    Rename = 8,

    /// <summary>The entry can be moved somewhere else.</summary>
    Move = 16,

    /// <summary>Files can be created in the directory.</summary>
    CreateFile = 32,

    /// <summary>Directories can be created in the directory.</summary>
    CreateDirectory = 64,

    /// <summary>The entry can be shared with somebody else.</summary>
    Share = 128,
}
