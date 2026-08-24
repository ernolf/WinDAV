// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace WinDav.Dav;

/// <summary>
/// The body of a GET together with the headers that describe it.
/// </summary>
/// <remarks>
/// The body is not read into memory. <see cref="Content"/> reads from the connection, so
/// the instance has to be disposed, and it has to stay alive until the last byte is read.
/// </remarks>
public sealed class DavContent : IDisposable
{
    private readonly HttpResponseMessage _response;

    private DavContent(HttpResponseMessage response, Stream content)
    {
        _response = response;
        Content = content;

        IsPartial = response.StatusCode == HttpStatusCode.PartialContent;
        ContentLength = response.Content.Headers.ContentLength;
        ContentType = response.Content.Headers.ContentType?.ToString();
        ETag = response.Headers.ETag?.ToString();
        LastModified = response.Content.Headers.LastModified;
    }

    /// <summary>
    /// Gets the body as it arrives from the server. Reading it a second time is not
    /// possible; it is a forward-only stream over the connection.
    /// </summary>
    public Stream Content { get; }

    /// <summary>
    /// Gets a value indicating whether the server answered with a range rather than the
    /// whole resource. A server may ignore a range request and send everything, in which
    /// case this is <see langword="false"/> and the caller has to skip to the offset itself.
    /// </summary>
    public bool IsPartial { get; }

    /// <summary>
    /// Gets the length of this body in bytes, or <see langword="null"/> when the server did
    /// not state one. For a partial answer this is the length of the part, not of the file.
    /// </summary>
    public long? ContentLength { get; }

    /// <summary>
    /// Gets the media type, parameters such as <c>charset</c> included, or
    /// <see langword="null"/> when the server did not state one.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the entity tag as the server wrote it, quotes and any weakness prefix included,
    /// because that is the form a conditional request has to send back.
    /// </summary>
    public string? ETag { get; }

    /// <summary>
    /// Gets the time of the last modification, or <see langword="null"/> when the server did
    /// not state one.
    /// </summary>
    public DateTimeOffset? LastModified { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }

    internal static async Task<DavContent> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return new DavContent(response, content);
    }
}
