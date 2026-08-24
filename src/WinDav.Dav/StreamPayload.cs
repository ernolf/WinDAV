// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace WinDav.Dav;

/// <summary>
/// A request body read from a stream that belongs to somebody else.
/// </summary>
/// <remarks>
/// <see cref="StreamContent"/> closes the stream it was given as soon as the request is
/// disposed. That is the wrong behaviour here: the stream is the caller's, and a caller
/// that wants to write the same source twice, or read on after the write, would find it
/// closed underneath.
/// </remarks>
internal sealed class StreamPayload : HttpContent
{
    private readonly Stream _stream;

    internal StreamPayload(Stream stream)
    {
        _stream = stream;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        _stream.CopyToAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        // Only a seekable stream can state its length. Without one the request goes out
        // chunked, which is correct but not every server likes it.
        if (!_stream.CanSeek)
        {
            length = 0;
            return false;
        }

        length = _stream.Length - _stream.Position;
        return true;
    }
}
