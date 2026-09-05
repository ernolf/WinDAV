// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Providers;

/// <summary>
/// How many handles are open on a directory at this moment.
/// </summary>
/// <remarks>
/// <para>
/// What this separates is a window somebody is reading from a program walking the tree.
/// A window keeps the directory it shows open and opens it again while it is on screen; a
/// walker opens a directory, lists it, closes it and does not come back. Listing ahead is
/// worth a request for the first and worth nothing for the second, and nothing else the file
/// system is told says which of the two is asking.
/// </para>
/// <para>
/// Open at the same time, never opens added up: a walker that passes the same directory twice
/// reaches a total of two without ever having held two at once. That is why what is counted
/// here is entered and left rather than noted.
/// </para>
/// </remarks>
public sealed class OpenDirectories
{
    // Held while the count of one directory is read and written. Nothing waits on a request
    // inside it: an open has its answer by the time the lock is taken.
    private readonly Lock _sync = new();

    // Ordinal, for the reason the listings are ordinal: a store that keeps case has two
    // directories where these differ.
    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts one handle more on a directory.
    /// </summary>
    /// <param name="path">The directory, as the store spells it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public void Enter(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            _open[path] = _open.TryGetValue(path, out int count) ? count + 1 : 1;
        }
    }

    /// <summary>
    /// Counts one handle fewer on a directory. A directory that was never entered is left
    /// alone, so a close without an open is not a negative count.
    /// </summary>
    /// <param name="path">The directory, as the store spells it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public void Leave(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            if (!_open.TryGetValue(path, out int count))
            {
                return;
            }

            if (count > 1)
            {
                _open[path] = count - 1;

                return;
            }

            // Taken out rather than left at nothing: a mount lives for days and a walk
            // touches every directory in it.
            _open.Remove(path);
        }
    }

    /// <summary>
    /// Says how many handles are open on a directory.
    /// </summary>
    /// <param name="path">The directory, as the store spells it.</param>
    /// <returns>The count, or nothing where nobody holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public int Count(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            return _open.TryGetValue(path, out int count) ? count : 0;
        }
    }
}
