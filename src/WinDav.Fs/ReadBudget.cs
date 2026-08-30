// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Fs;

/// <summary>
/// How much memory all the windows of one mount may hold between them.
/// </summary>
/// <remarks>
/// A window is taken when a handle is first read from and given back when the handle closes,
/// so what this bounds is a mount with several files open at once. Twenty handles must not
/// become twenty windows. When there is no room left the handle simply gets none and reads
/// the way a mount with no window at all reads: nothing fails, and nothing waits for room.
/// </remarks>
internal sealed class ReadBudget
{
    private readonly Lock _gate = new();
    private readonly long _ceiling;

    private long _taken;

    /// <summary>
    /// Initialises a new instance of the <see cref="ReadBudget"/> class.
    /// </summary>
    /// <param name="ceiling">
    /// The most that may be held at once, in bytes, or anything below one for no ceiling at
    /// all. Without one, what bounds the windows is how many files are open.
    /// </param>
    internal ReadBudget(long ceiling)
    {
        _ceiling = Math.Max(ceiling, 0);
    }

    /// <summary>
    /// Gets how much is held at this moment.
    /// </summary>
    internal long Taken
    {
        get
        {
            lock (_gate)
            {
                return _taken;
            }
        }
    }

    /// <summary>
    /// Asks for room for one window.
    /// </summary>
    /// <param name="bytes">How much the window would hold.</param>
    /// <returns>Whether there was room, in which case it is now taken.</returns>
    internal bool TryTake(long bytes)
    {
        if (bytes <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_ceiling > 0 && _taken + bytes > _ceiling)
            {
                return false;
            }

            _taken += bytes;

            return true;
        }
    }

    /// <summary>
    /// Gives the room back.
    /// </summary>
    /// <param name="bytes">What was taken, exactly as it was taken.</param>
    internal void Return(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _taken -= bytes;
        }
    }
}
