// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// The names this mount has made and the store has not been given yet.
/// </summary>
/// <remarks>
/// <para>
/// A file Windows makes is not sent when it is made but when the handle to it lets go, so
/// that what goes up is one upload with the contents in it rather than an empty one followed
/// by a real one. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#86-a-write-is-finished-when-the-server-has-it">decision 86</see>.
/// </para>
/// <para>
/// Between the two the name exists and the store has never heard of it, and this is where
/// that is written down. Anything asked about such a name is answered from here instead of
/// from the store, which would say there is nothing there — and a second create of the same
/// name is the collision it would have been.
/// </para>
/// </remarks>
internal sealed class HeldNames
{
    // Held while one name is looked at. Nothing waits on a request inside it.
    private readonly Lock _sync = new();

    // Ordinal, for the reason the listings are ordinal: a store that keeps case has two
    // entries where these differ.
    private readonly Dictionary<string, RemoteEntry> _held = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes a name, where nothing holds it yet.
    /// </summary>
    /// <param name="path">The name, as the store would spell it.</param>
    /// <param name="entry">What is answered about it while it is held.</param>
    /// <returns>
    /// <see langword="true"/> where the name was free, and <see langword="false"/> where
    /// another handle already holds it, which is a collision.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal bool Claim(string path, RemoteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            return _held.TryAdd(path, entry);
        }
    }

    /// <summary>
    /// What is held under a name.
    /// </summary>
    /// <param name="path">The name, as the store would spell it.</param>
    /// <returns>The entry, or <see langword="null"/> where the name is not held here.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal RemoteEntry? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            return _held.GetValueOrDefault(path);
        }
    }

    /// <summary>
    /// Lets a name go, because the store now has it or because the handle that made it has
    /// gone. A name that is not held is left alone, so this may be called twice.
    /// </summary>
    /// <param name="path">The name, as the store would spell it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal void Release(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            _held.Remove(path);
        }
    }
}
