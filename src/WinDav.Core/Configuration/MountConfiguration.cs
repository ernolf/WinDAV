// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// One mount: part of one account's file tree, made visible at one place in Windows.
/// </summary>
/// <remarks>
/// What a mount needs to be made is here, and nothing else: where it reaches, where it
/// appears and how it presents itself. A mount that is written down can say everything a
/// mount that is typed out can say; decisions.md 73. Settings that need a project this build
/// does not have — how many connections a mount may use, what is cached — arrive with it, so
/// that no value can be set today and silently ignored.
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
    /// Gets the <see cref="AccountConfiguration.Uuid"/> of the account this mount reaches.
    /// </summary>
    /// <remarks>
    /// The identity, not the name: an account that is renamed, or merged with the same
    /// account reached under another login, must not take its mounts down with it. The
    /// command line takes the name and resolves it here. See decisions.md 71.
    /// </remarks>
    public string Account { get; init; } = string.Empty;

    /// <summary>
    /// Gets the path on the server that becomes the root of the mount.
    /// </summary>
    public string RemotePath { get; init; } = RootPath;

    /// <summary>
    /// Gets the drive letter to use, as a single letter, or <see langword="null"/> to leave
    /// the choice to Windows.
    /// </summary>
    /// <remarks>
    /// Nothing here and no directory either is the next free letter, which is what a mount
    /// that is typed out without a place to go does as well.
    /// </remarks>
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
    /// Gets what the drive is called, or <see langword="null"/> to name it after what it
    /// reaches.
    /// </summary>
    /// <remarks>
    /// Derived when it is not given: the account at its server for a whole account, the name
    /// of the folder for anything below it. See decisions.md 58.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>
    /// Gets the file the drive icon is taken from, as a full path, or <see langword="null"/>
    /// for the icon Windows gives a network drive.
    /// </summary>
    /// <remarks>
    /// A full path, because the registry keeps it and is read again long after the command
    /// that wrote it has ended.
    /// </remarks>
    public string? IconPath { get; init; }

    /// <summary>
    /// Gets the network name the mount is also reached under, in the form
    /// <c>\Server\Share</c>, or <see langword="null"/> to derive one.
    /// </summary>
    public string? NetworkPrefix { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mount appears as a local disk rather than as a
    /// network drive, which leaves it without a network name.
    /// </summary>
    public bool Local { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mount refuses every write, whatever the server
    /// would have allowed.
    /// </summary>
    public bool ReadOnly { get; init; }
}
