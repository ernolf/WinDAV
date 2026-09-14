// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Runtime.InteropServices;
using WinDav.Core;

namespace WinDav.Fs;

/// <summary>
/// Where the contents of one open handle are kept until the store has them.
/// </summary>
/// <remarks>
/// <para>
/// A store like this takes a file whole, and Windows hands it over in blocks, at any offset
/// and in any order. So the blocks need somewhere local to land before there is anything to
/// send, and that is what this is. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#86-a-write-is-finished-when-the-server-has-it">decision 86</see>.
/// </para>
/// <para>
/// Somewhere local is a file of its own in the temporary directory, opened so that Windows
/// takes it away the moment the handle to it closes. Nothing is left behind by a mount that
/// ends, however it ends, and nothing of a write survives a restart, which is the line
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#66-a-store-that-feels-like-a-sync-client-without-being-one">decision 66</see>
/// draws.
/// </para>
/// <para>
/// A store that takes a file in pieces is sent the front of it from here while the rest is
/// still being written, so this also keeps track of how far the file has been written without
/// a gap and of how much of it has been read out to go ahead. A write that goes back over
/// what went ahead is noted, and the file then goes whole.
/// </para>
/// </remarks>
internal sealed class WriteStage : IDisposable
{
    // One trip between the memory WinFsp handed over and the staged file. The same size the
    // read path copies with, and for the same reason: it is a buffer, not a window.
    private const int TransferBufferSize = 64 * 1024;

    private const string StagedSuffix = ".part";

    // How many runs written beyond the front are followed. A program that writes all over a
    // file has it sent whole rather than kept track of without end.
    private const int ScatteredLimit = 256;

    private readonly FileStream _file;

    // Pieces are read out of the file while writes go into it, and a FileStream has one
    // position between them.
    private readonly Lock _sync = new();

    // Runs written beyond the front, from where each starts to where it ends, until the front
    // reaches them.
    private readonly SortedList<long, long> _ahead = [];

    // The front: everything before it has been written, in whatever order it came.
    private long _written;

    // How far pieces have been read out to go ahead. What lies before it has left.
    private long _claimed;

    // Set where following the writes is not worth it, or no longer is: nothing is read out
    // ahead from then on.
    private bool _whole;

    // Set once a write went back over what had been read out ahead.
    private bool _disturbed;

    /// <summary>
    /// Initialises a new instance of the <see cref="WriteStage"/> class.
    /// </summary>
    /// <param name="eTag">
    /// What the entry carried when the handle was opened, or <see langword="null"/> when the
    /// store named none. It travels with the upload as its condition.
    /// </param>
    internal WriteStage(string? eTag)
    {
        ETag = eTag;

        _file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-{Guid.NewGuid():N}{StagedSuffix}"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose,
            });
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="WriteStage"/> class for a name the store
    /// has not been given yet.
    /// </summary>
    /// <remarks>
    /// It stands pending with nothing in it, so that a file made and closed again without a
    /// byte ever going into it still reaches the store, as one upload rather than none. There
    /// is no version to make it conditional on, because there is nothing there yet to have
    /// one; what stands in its place is the store being asked to refuse the write if the name
    /// has been taken in the meantime.
    /// </remarks>
    internal WriteStage()
        : this(eTag: null)
    {
        MustBeNew = true;
        Pending = true;
    }

    /// <summary>
    /// Gets the version the next upload is made conditional on, or <see langword="null"/>
    /// when there is none to name.
    /// </summary>
    internal string? ETag { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the store still has to be told this name at all, so
    /// that the next upload is the one that makes it and has to refuse to overwrite.
    /// </summary>
    internal bool MustBeNew { get; private set; }

    /// <summary>
    /// Gets a value indicating whether there is something here the store has not got.
    /// </summary>
    internal bool Pending { get; private set; }

    /// <summary>
    /// Gets how long the file is as it stands here.
    /// </summary>
    internal long Length
    {
        get
        {
            lock (_sync)
            {
                return _file.Length;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a write went back over what had been read out ahead,
    /// so that what went ahead is not the front of the file any more.
    /// </summary>
    internal bool Disturbed
    {
        get
        {
            lock (_sync)
            {
                return _disturbed;
            }
        }
    }

    /// <summary>
    /// Takes what was handed over at an offset, lengthening the file where it has to.
    /// </summary>
    /// <param name="offset">Where the bytes go, counted from the start of the file.</param>
    /// <param name="buffer">The memory WinFsp handed over.</param>
    /// <param name="count">How many bytes of it are the write.</param>
    internal void Write(long offset, IntPtr buffer, int count)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

        try
        {
            lock (_sync)
            {
                // Before the bytes go in, so that a write that fails half way still counts as
                // one that went back over what had left.
                _disturbed |= count > 0 && offset < _claimed;

                _file.Position = offset;

                for (int written = 0; written < count;)
                {
                    int piece = Math.Min(TransferBufferSize, count - written);

                    Marshal.Copy(buffer + written, chunk, 0, piece);
                    _file.Write(chunk, 0, piece);

                    written += piece;
                }

                Track(offset, count);

                Pending = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Reads back what is here, which is what a program that has just written gets.
    /// </summary>
    /// <param name="offset">The first byte, counted from the start of the file.</param>
    /// <param name="count">How many bytes are wanted.</param>
    /// <param name="destination">The memory WinFsp handed over.</param>
    /// <returns>How many bytes were put there, which is none at the end of the file.</returns>
    internal int Read(long offset, long count, IntPtr destination)
    {
        lock (_sync)
        {
            long left = _file.Length - offset;

            if (left <= 0)
            {
                return 0;
            }

            int wanted = (int)Math.Min(count, left);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

            try
            {
                _file.Position = offset;

                int taken = 0;

                while (taken < wanted)
                {
                    int piece = _file.Read(chunk, 0, Math.Min(TransferBufferSize, wanted - taken));

                    if (piece == 0)
                    {
                        break;
                    }

                    Marshal.Copy(chunk, 0, destination + taken, piece);

                    taken += piece;
                }

                return taken;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
    }

    /// <summary>
    /// Makes the file exactly this long, cutting it short or padding it with zeroes.
    /// </summary>
    /// <param name="length">What the length becomes.</param>
    internal void Truncate(long length)
    {
        lock (_sync)
        {
            _file.SetLength(length);

            Cut(length);

            Pending = true;
        }
    }

    /// <summary>
    /// Puts what the store already holds here, so that a write to part of a file sends the
    /// whole of it back unchanged elsewhere.
    /// </summary>
    /// <param name="source">The contents as the store handed them over.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    internal void Fill(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_sync)
        {
            _file.Position = 0;

            source.CopyTo(_file);

            // What was there before is not a front anybody wrote, so none of it goes ahead.
            _whole = true;
        }
    }

    /// <summary>
    /// Gives the whole file back to be read from its start, which is how it is uploaded.
    /// </summary>
    /// <returns>The staged file, positioned at its first byte.</returns>
    internal Stream Rewound()
    {
        lock (_sync)
        {
            _file.Flush();
            _file.Position = 0;

            return _file;
        }
    }

    /// <summary>
    /// Tells whether a piece of this size has been written and not read out to go ahead yet.
    /// </summary>
    /// <param name="size">How long a piece is.</param>
    /// <returns><see langword="true"/> when one can be read out.</returns>
    internal bool CanClaim(long size)
    {
        lock (_sync)
        {
            return Claimable(size);
        }
    }

    /// <summary>
    /// Hands out the next piece to go ahead, where there is one.
    /// </summary>
    /// <param name="size">How long a piece is.</param>
    /// <returns>The piece, or <see langword="null"/> when there is none to hand out.</returns>
    /// <remarks>
    /// Nothing is copied: the piece reads its stretch of the staged file while it travels,
    /// so a piece on its way costs no memory. It reads under the lock the writes take,
    /// because the file has one position between them.
    /// </remarks>
    internal Stream? Claim(long size)
    {
        lock (_sync)
        {
            if (!Claimable(size))
            {
                return null;
            }

            WindowStream piece = new(_file, _claimed, size, _sync);
            _claimed += size;

            return piece;
        }
    }

    /// <summary>
    /// Gives back the last piece that was handed out, which did not go ahead after all.
    /// </summary>
    /// <param name="size">How long a piece is.</param>
    internal void Unclaim(long size)
    {
        lock (_sync)
        {
            _claimed -= size;
        }
    }

    /// <summary>
    /// Ends reading out ahead for good, and leaves what went ahead as it is.
    /// </summary>
    internal void StopClaims()
    {
        lock (_sync)
        {
            _whole = true;
            _ahead.Clear();
        }
    }

    /// <summary>
    /// Gives what follows the pieces that went ahead, which is what is left to upload.
    /// </summary>
    /// <returns>
    /// The staged file from the first byte that did not go ahead to its end, as a stream of
    /// its own. Pieces can still be reading the file when it is handed over, and the file has
    /// one position between them.
    /// </returns>
    internal Stream Rest()
    {
        lock (_sync)
        {
            _file.Flush();

            return new WindowStream(_file, _claimed, _file.Length - _claimed, _sync);
        }
    }

    /// <summary>
    /// Notes that the store has what is here, and under which version it has it.
    /// </summary>
    /// <param name="eTag">
    /// What the store answered with, or <see langword="null"/> when it named nothing. A
    /// store that names nothing leaves the next upload with no condition to make.
    /// </param>
    internal void Delivered(string? eTag)
    {
        ETag = eTag;
        Pending = false;
        MustBeNew = false;

        lock (_sync)
        {
            // The front has gone, and is not sent ahead a second time. What is written into
            // the file from here on goes whole.
            _whole = true;
            _ahead.Clear();
            _claimed = 0;
            _disturbed = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _file.Dispose();

    // Whether a piece can go ahead: the writes are more than a piece past what has gone, so
    // the piece is written through and something is still left behind it for the rest.
    private bool Claimable(long size) => !_disturbed && !_whole && _written - _claimed > size;

    // Moves the front along with a write that reached it, or notes one that landed beyond it.
    private void Track(long offset, int count)
    {
        long end = offset + count;

        if (offset <= _written)
        {
            _written = Math.Max(_written, end);

            // Runs written ahead that the front has now reached are part of it.
            while (_ahead.Count > 0 && _ahead.Keys[0] <= _written)
            {
                _written = Math.Max(_written, _ahead.Values[0]);
                _ahead.RemoveAt(0);
            }

            return;
        }

        if (_whole || count == 0)
        {
            return;
        }

        long start = offset;

        // Runs that overlap or touch become one.
        for (int index = _ahead.Count - 1; index >= 0; index--)
        {
            if (_ahead.Keys[index] <= end && _ahead.Values[index] >= start)
            {
                start = Math.Min(start, _ahead.Keys[index]);
                end = Math.Max(end, _ahead.Values[index]);
                _ahead.RemoveAt(index);
            }
        }

        _ahead[start] = end;

        if (_ahead.Count > ScatteredLimit)
        {
            _whole = true;
            _ahead.Clear();
        }
    }

    // Keeps what is known about the writes true of a file cut to this length.
    private void Cut(long length)
    {
        _disturbed |= length < _claimed;
        _written = Math.Min(_written, length);

        for (int index = _ahead.Count - 1; index >= 0; index--)
        {
            if (_ahead.Keys[index] >= length)
            {
                _ahead.RemoveAt(index);
            }
            else if (_ahead.Values[index] > length)
            {
                _ahead[_ahead.Keys[index]] = length;
            }
        }
    }
}
