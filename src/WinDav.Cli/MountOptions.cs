// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Configuration;

namespace WinDav.Cli;

/// <summary>
/// The options that describe a mount, read the way a person writes them.
/// </summary>
/// <remarks>
/// In one place because a mount is asked for on two lines — the one that makes it and the one
/// that writes it down — and a rule about how something is typed that stands in two places is
/// two rules as soon as one of them is changed. The same reason <see cref="ServerAddress"/>
/// exists. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#73-a-mount-that-stays">decision 73</see>.
/// </remarks>
internal static class MountOptions
{
    /// <summary>
    /// Reads the path that becomes the root of a mount.
    /// </summary>
    /// <param name="path">What was typed, or <see langword="null"/> for the whole store.</param>
    /// <returns>The path in the form the rest of the program works in.</returns>
    internal static string ReadPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return MountConfiguration.RootPath;
        }

        // Written by a person, so both kinds of slash arrive and a trailing one is common.
        // The form the rest of the program works in has neither.
        string written = path.Trim().Replace('\\', '/').TrimEnd('/');

        return written.StartsWith('/') ? written : MountConfiguration.RootPath + written;
    }

    /// <summary>
    /// Reads what the drive is to be called.
    /// </summary>
    /// <param name="label">What was typed, or <see langword="null"/> to derive one.</param>
    /// <returns>The name, or <see langword="null"/>.</returns>
    /// <exception cref="UsageException">Nothing was written in it.</exception>
    internal static string? ReadLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        string written = label.Trim();

        if (written.Length == 0)
        {
            throw new UsageException("--label needs a name for the drive. Leave --label out to name it after what it reaches.");
        }

        return written;
    }

    /// <summary>
    /// Reads the file a drive icon is taken from.
    /// </summary>
    /// <param name="icon">What was typed, or <see langword="null"/> for no icon.</param>
    /// <returns>The full path of the file, or <see langword="null"/>.</returns>
    /// <exception cref="UsageException">There is no file there.</exception>
    internal static string? ReadIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        // Written as a full path, because the registry keeps it and is read again long after
        // whatever directory the command ran in has stopped mattering.
        string path = Path.GetFullPath(icon.Trim());

        if (!File.Exists(path))
        {
            throw new UsageException($"There is no file at '{path}'.");
        }

        return path;
    }

    /// <summary>
    /// Reads the network name a mount is also reached under.
    /// </summary>
    /// <param name="prefix">What was typed, or <see langword="null"/> to derive one.</param>
    /// <returns>The name in the form a mount carries it, or <see langword="null"/>.</returns>
    /// <exception cref="UsageException">It is not a name of the form <c>\\server\share</c>.</exception>
    internal static string? ReadPrefix(string? prefix)
    {
        if (prefix is null)
        {
            return null;
        }

        // A person writes the name Windows shows, with two leading backslashes. WinFsp is
        // given the same name with one, which is the form a mount carries it in.
        string written = prefix.Trim().Replace('/', '\\').TrimStart('\\');

        if (written.IndexOf('\\', StringComparison.Ordinal) <= 0)
        {
            throw new UsageException("A network name is written as \\\\server\\share.");
        }

        return "\\" + written;
    }

    /// <summary>
    /// Reads the place a mount appears at, and tells a drive letter from a directory.
    /// </summary>
    /// <param name="mountPoint">
    /// What was typed, or <see langword="null"/> for the next free letter.
    /// </param>
    /// <returns>The letter, or the directory, or neither.</returns>
    /// <remarks>
    /// A single letter, with or without the colon and the backslash a person writes after it,
    /// is a drive letter; anything else is a directory, which is kept as a full path for the
    /// same reason an icon is.
    /// </remarks>
    internal static (string? DriveLetter, string? Directory) ReadMountPoint(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return (null, null);
        }

        string written = mountPoint.Trim();
        string bare = written.TrimEnd('\\', '/');

        if (bare.Length == 2 && bare[1] == ':' && char.IsAsciiLetter(bare[0]))
        {
            return (bare[..1], null);
        }

        return bare.Length == 1 && char.IsAsciiLetter(bare[0])
            ? (bare, null)
            : (null, Path.GetFullPath(written));
    }
}
