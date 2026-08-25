// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// One mount: part of one account's file tree, made visible at one place in Windows.
/// </summary>
/// <remarks>
/// Only what already has an effect is here. Settings that need the file system to exist
/// before they mean anything — how the drive presents itself, how many connections it may
/// use — arrive with the project that reads them, so that no value can be set today and
/// silently ignored.
/// </remarks>
public sealed class MountConfiguration
{
    /// <summary>
    /// The remote path of a mount that exposes the whole of an account.
    /// </summary>
    public const string RootPath = "/";

    /// <summary>
    /// Gets the name this mount is referred to by, unique within the configuration.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the <see cref="AccountConfiguration.Id"/> of the account this mount reaches.
    /// </summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>
    /// Gets the path on the server that becomes the root of the mount.
    /// </summary>
    public string RemotePath { get; init; } = RootPath;

    /// <summary>
    /// Gets the drive letter to use, as a single letter, or <see langword="null"/> when the
    /// mount goes into a directory instead.
    /// </summary>
    public string? DriveLetter { get; init; }

    /// <summary>
    /// Gets the empty NTFS directory to mount into, or <see langword="null"/> when the mount
    /// takes a drive letter instead.
    /// </summary>
    /// <remarks>
    /// Windows has twenty-six letters and shares them with everything else on the machine,
    /// so mounting into a directory is not a fallback but the way out of that limit.
    /// </remarks>
    public string? Directory { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mount refuses every write, whatever the server
    /// would have allowed.
    /// </summary>
    public bool ReadOnly { get; init; }
}
