// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Fs;

/// <summary>
/// What one mount needs to know about itself, apart from the store it shows.
/// </summary>
/// <remarks>
/// Everything here has an effect the moment it is set. Settings that would be read by code
/// which does not exist yet are not here, so that no value can be given today and silently
/// ignored.
/// </remarks>
public sealed class MountSettings
{
    /// <summary>
    /// Gets the path in the store that becomes the root of the mount.
    /// </summary>
    /// <remarks>
    /// Written the way <see cref="Abstractions.RemoteEntry.Path"/> is: leading slash, no
    /// trailing one, unescaped. Everything above it stays out of sight.
    /// </remarks>
    public string RemotePath { get; init; } = "/";

    /// <summary>
    /// Gets where the mount appears, as a drive letter with a colon or as the path of an
    /// empty directory, or <see langword="null"/> to take the next free letter counting
    /// down from Z:.
    /// </summary>
    public string? MountPoint { get; init; }

    /// <summary>
    /// Gets the UNC name the mount answers to, written with single backslashes as
    /// <c>\Server\Share</c>, or <see langword="null"/> to appear as a local disk instead.
    /// </summary>
    /// <remarks>
    /// A WebDAV mount is a network location, and setting this is what makes Windows treat
    /// it as one. Leaving it out is not a fallback: a mount that presents itself as a local
    /// disk is reached by programs that refuse to touch network drives, which is something
    /// the Windows WebDAV redirector cannot offer at all.
    /// </remarks>
    public string? NetworkPrefix { get; init; }

    /// <summary>
    /// Gets the name the volume reports for itself.
    /// </summary>
    /// <remarks>
    /// This is what a program asking the volume for its label gets. It is not what the
    /// Explorer shows beside the drive letter, which is a separate matter and comes from
    /// the registry.
    /// </remarks>
    public string VolumeLabel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name Explorer shows beside the drive letter, or <see langword="null"/> to
    /// leave whatever Windows made of the network name.
    /// </summary>
    /// <remarks>
    /// This is the other name, the one that comes from the registry rather than from the
    /// volume. Windows builds one of its own out of the network name, and it reads
    /// <c>ernolf (\\global-social.net)</c> where a person would have written
    /// <c>ernolf@global-social.net</c>.
    /// </remarks>
    public string? ExplorerName { get; init; }

    /// <summary>
    /// Gets the file the drive icon is taken from, or <see langword="null"/> for the icon
    /// Windows gives a network drive.
    /// </summary>
    /// <remarks>
    /// An <c>.ico</c>, or anything else that carries icons; the first one in the file is
    /// used. Leaving it out is not merely doing nothing: an icon written for an earlier
    /// choice is taken away again, so that whoever wants the ordinary icon has nothing of
    /// ours left in their registry.
    /// </remarks>
    public string? IconPath { get; init; }
}
