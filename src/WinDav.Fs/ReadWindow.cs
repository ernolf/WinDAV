// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// What one open handle has already fetched, and the rule by which it fetches more.
/// </summary>
/// <remarks>
/// <para>
/// The window belongs to the handle, not to the file and not to the mount: two programs
/// reading the same file each read at their own place, and closing the handle throws the
/// window away. It holds one run of bytes, and it is filled by a read that continues where
/// the last one ended. A read that lands anywhere else is served by a request of exactly the
/// size it asked for and leaves the window untouched, so a program that seeks about pays for
/// what it reads and never for what it skipped.
/// </para>
/// <para>
/// A read at least as large as the window is served the same way. There is nothing to be won
/// by reading ahead of a read that is already wider than the window, and it is what keeps a
/// window from ever handing back fewer bytes than were asked for except at the end of the
/// file, where fewer bytes is the answer.
/// </para>
/// <para>
/// A file that fits in the window is the exception to all of it: it is fetched whole, from
/// its start, the first time anything is read from it, and every read of that handle is
/// answered out of the window afterwards. A second request costs a quarter of a second
/// before it costs a byte, which is more than the bytes it would save.
/// </para>
/// </remarks>
internal sealed class ReadWindow
{
    private readonly Lock _gate = new();
    private readonly ReadLayer _layer;
    private readonly string _path;
    private readonly long? _size;
    private readonly bool _whole;

    private long _want;
    private byte[]? _buffer;
    private long _taken;
    private long _start;
    private int _length;

    // Where the last read ended, and so the one offset that continues this window. Before
    // the first read there is nothing to continue, which is why it starts at a place no read
    // can begin: the first read of a handle is a request of its own, and a handle that is
    // opened, sniffed at and dropped again never fetches a window it does not use.
    private long _next = -1;

    /// <summary>
    /// Initialises a new instance of the <see cref="ReadWindow"/> class.
    /// </summary>
    /// <param name="layer">The layer that does the fetching and holds the ceiling.</param>
    /// <param name="path">The entry, in the store's own spelling.</param>
    /// <param name="size">How long the store said it is, or <see langword="null"/>.</param>
    /// <param name="window">How far ahead of a read this handle may fetch, in bytes.</param>
    internal ReadWindow(ReadLayer layer, string path, long? size, long window)
    {
        _layer = layer;
        _path = path;
        _size = size;

        // A file shorter than the window is held in the room it needs and no more, which is
        // also what fetches it whole in one request. Nothing larger than an array can hold
        // is asked for, however the number arrived here.
        _want = size is long length ? Math.Min(window, length) : window;
        _want = Math.Clamp(_want, 0, Array.MaxLength);

        // Whether the whole of it fits. Decided after the clamp, so that a file larger than
        // one piece of memory is not mistaken for one that fits.
        _whole = _want > 0 && size is long whole && _want >= whole;
    }

    /// <summary>
    /// Reads a range of the entry, out of the window where it is there and out of a request
    /// of its own where it is not.
    /// </summary>
    /// <param name="offset">The first byte, counted from the start of the entry.</param>
    /// <param name="count">
    /// How many bytes are wanted, already brought down to what the entry holds.
    /// </param>
    /// <param name="destination">The buffer of the read, written from its start.</param>
    /// <returns>How many bytes were written, which is fewer at the end of the file.</returns>
    /// <exception cref="ProviderException">The store refused, or could not be reached.</exception>
    internal int Read(long offset, long count, IntPtr destination)
    {
        lock (_gate)
        {
            bool windowed = Holds(offset, count) || (MayFill(offset, count) && Fill(offset));

            int served = windowed
                ? Serve(offset, Math.Min(count, Available(offset)), destination)
                : _layer.FetchInto(_path, offset, count, destination);

            _next = offset + served;

            return served;
        }
    }

    /// <summary>
    /// Gives the window back to the mount, which is what closing the handle does.
    /// </summary>
    internal void Close()
    {
        lock (_gate)
        {
            _layer.Budget.Return(_taken);

            _taken = 0;
            _buffer = null;
            _length = 0;
            _want = 0;
        }
    }

    // The window is filled by a read that continues where the last one ended, and only by a
    // read that is smaller than the window, because reading ahead of a read that is already
    // as wide as the window wins nothing and could only shorten it. A file that fits is
    // fetched whenever anything is wanted from it, which is once.
    private bool MayFill(long offset, long count) =>
        _want > 0 && (_whole || (count < _want && offset == _next));

    private bool Holds(long offset, long count) =>
        _length > 0 && offset >= _start && offset + count <= _start + _length;

    private long Available(long offset) =>
        _length > 0 && offset >= _start && offset < _start + _length
            ? _start + _length - offset
            : 0;

    // Fetches the window afresh, starting where the read that continues it starts. False is
    // the answer only when there is no window to fill at all.
    private bool Fill(long offset)
    {
        if (_buffer is null)
        {
            if (!_layer.Budget.TryTake(_want))
            {
                // The mount is holding as much as it may. This handle reads without a window
                // rather than waiting for one, and it does not ask again: what filled the
                // ceiling is other handles, and they are not about to close because this one
                // is reading.
                _want = 0;

                return false;
            }

            _taken = _want;
            _buffer = new byte[(int)_want];
        }

        // From the start where the whole of it fits, so that it is fetched whole however far
        // into it the first read reached.
        long from = _whole ? 0 : offset;

        int room = _size is long length
            ? (int)Math.Min(_buffer.Length, length - from)
            : _buffer.Length;

        // Emptied before the request rather than after it, so that a request that fails
        // leaves the handle with no window instead of one that says the wrong thing.
        _length = 0;
        _start = from;

        if (room > 0)
        {
            _length = _layer.FetchInto(_path, from, room, _buffer);
        }

        return true;
    }

    private int Serve(long offset, long count, IntPtr destination)
    {
        if (count <= 0)
        {
            return 0;
        }

        Marshal.Copy(_buffer!, (int)(offset - _start), destination, (int)count);

        return (int)count;
    }
}
