// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;

namespace WinDav.Core;

/// <summary>
/// A stretch of a stream, handed out as a stream of its own that reads that stretch and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It is made for a file that goes to the server as several requests at once, each of them
/// reading its own part of it. A stream has one position for all of them, so every read puts
/// it where the window needs it and reads under a lock the windows share. Anything else that
/// reads or writes the stream while windows are out has to take the same lock.
/// </para>
/// <para>
/// The stream stays with whoever handed it out. Disposing a window leaves it open.
/// </para>
/// </remarks>
public sealed class WindowStream : Stream
{
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The stream belongs to whoever handed it out, and other windows may still read it. See the remarks above.")]
    private readonly Stream _source;

    private readonly long _start;

    private readonly long _length;

    private readonly Lock _gate;

    private long _position;

    /// <summary>
    /// Initialises a new instance of the <see cref="WindowStream"/> class.
    /// </summary>
    /// <param name="source">The stream the window looks onto.</param>
    /// <param name="start">Where in <paramref name="source"/> the window begins.</param>
    /// <param name="length">How many bytes the window holds.</param>
    /// <param name="gate">The lock every reader of <paramref name="source"/> takes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> or <paramref name="length"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> cannot be read or cannot be sought.
    /// </exception>
    public WindowStream(Stream source, long start, long length, Lock gate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("A window needs a stream it can read and seek.", nameof(source));
        }

        _source = source;
        _start = start;
        _length = length;
        _gate = gate;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);

            _position = value;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    /// <exception cref="EndOfStreamException">
    /// The stream ends inside the window. It was shortened after the window was made, and
    /// what the window would deliver is no longer the stretch it was made for.
    /// </exception>
    public override int Read(Span<byte> buffer)
    {
        long left = _length - _position;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        Span<byte> wanted = buffer[..(int)Math.Min(buffer.Length, left)];
        int read;

        lock (_gate)
        {
            _source.Position = _start + _position;
            read = _source.Read(wanted);
        }

        if (read == 0)
        {
            throw new EndOfStreamException("The stream ended inside the stretch the window was made for.");
        }

        _position += read;

        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        // Read where it is asked for: the lock cannot be held across an await, and the streams
        // a window is made for are local files, whose reads are short next to the requests
        // they feed.
        return ValueTask.FromResult(Read(buffer.Span));
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Nothing is written, so there is nothing to flush.
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Not a place a seek can be counted from."),
        };

        if (position < 0)
        {
            throw new IOException("A window cannot be sought to before its start.");
        }

        _position = position;

        return position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
