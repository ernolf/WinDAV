// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Dav;

/// <summary>
/// The body of a response, handed out as the plain stream the seam promises.
/// </summary>
/// <remarks>
/// <para>
/// It exists for two reasons. The body belongs to a <see cref="DavContent"/> that also holds
/// the response, and disposing the body alone would leave the connection hanging; disposing
/// this disposes both.
/// </para>
/// <para>
/// And a store that ignored the range answers with the whole resource, so the caller would
/// read past what it asked for. A limit ends the stream where the range would have.
/// </para>
/// </remarks>
internal sealed class DavContentStream : Stream
{
    private readonly DavContent _content;

    private readonly Stream _body;

    private long? _remaining;

    internal DavContentStream(DavContent content, long? length)
    {
        _content = content;
        _body = content.Content;
        _remaining = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int wanted = Allowed(buffer.Length);
        if (wanted == 0)
        {
            return 0;
        }

        return Taken(_body.Read(buffer[..wanted]));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int wanted = Allowed(buffer.Length);
        if (wanted == 0)
        {
            return 0;
        }

        return Taken(await _body.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false));
    }

    public override void Flush()
    {
        // Nothing is written, so there is nothing to flush.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Allowed(int wanted) =>
        _remaining is null ? wanted : (int)Math.Min(wanted, _remaining.Value);

    private int Taken(int read)
    {
        if (_remaining is not null)
        {
            _remaining -= read;
        }

        return read;
    }
}
