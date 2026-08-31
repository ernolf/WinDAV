// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// What is inside one directory, and what the store said about that directory itself.
/// </summary>
/// <remarks>
/// A store is asked about a directory and its contents in one request, and it answers both in
/// one breath. Throwing the first half away means asking for it again, which is a request for
/// something that has already been paid for. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#76-listings-are-kept-an-etag-says-whether-they-still-hold-and-f5-throws-them-away">decision 76</see>.
/// </remarks>
/// <param name="entries">What is directly inside, without the directory itself.</param>
/// <param name="self">What the store said about the directory. See <see cref="Self"/>.</param>
public sealed class DirectoryListing(IReadOnlyList<RemoteEntry> entries, RemoteEntry? self = null)
{
    /// <summary>
    /// Gets what is directly inside, without the directory itself, in no particular order.
    /// </summary>
    public IReadOnlyList<RemoteEntry> Entries { get; } = entries;

    /// <summary>
    /// Gets what the store said about the directory itself, or <see langword="null"/> when
    /// it described only what is inside.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and not a failure: a store is entitled to describe only the
    /// contents, and whoever wants the directory itself then asks for it.
    /// </remarks>
    public RemoteEntry? Self { get; } = self;
}
