// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// One file or directory in a store.
/// </summary>
/// <param name="path">Where it is. See <see cref="Path"/> for the form.</param>
/// <param name="isDirectory">Whether it holds other entries.</param>
public sealed class RemoteEntry(string path, bool isDirectory)
{
    /// <summary>
    /// Gets the path, starting with a slash, separated by slashes, without a trailing one,
    /// and not escaped in any way. The root is <c>"/"</c>. Turning this into whatever the
    /// store expects, escapes included, is the provider's job.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// Gets a value indicating whether this holds other entries.
    /// </summary>
    public bool IsDirectory { get; } = isDirectory;

    /// <summary>
    /// Gets the last segment of <see cref="Path"/>, which is empty for the root.
    /// </summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Gets the size in bytes, or <see langword="null"/> when it is not known. Directories
    /// have no size.
    /// </summary>
    public long? Length { get; init; }

    /// <summary>
    /// Gets when it was last written, or <see langword="null"/> when the store did not say.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Gets the token that stands for this version of the entry, or <see langword="null"/>
    /// when the store has none. It is opaque: only the store it came from can read it, and
    /// it is passed back unchanged.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Gets the media type, or <see langword="null"/> when the store did not say.
    /// </summary>
    public string? ContentType { get; init; }
}
