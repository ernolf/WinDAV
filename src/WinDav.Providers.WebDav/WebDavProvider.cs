// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Net;
using WinDav.Abstractions;
using WinDav.Dav;

namespace WinDav.Providers.WebDav;

/// <summary>
/// A store reached over plain RFC 4918, with nothing of any vendor in it.
/// </summary>
/// <remarks>
/// Everything the server says stops here. Statuses become a <see cref="ProviderError"/>,
/// hrefs become paths, and a range the server ignored is made good on before the stream is
/// handed out.
/// </remarks>
public sealed class WebDavProvider : IStorageProvider
{
    private readonly DavClient _client;

    private readonly Uri _baseUri;

    /// <summary>
    /// Initialises a new instance of the <see cref="WebDavProvider"/> class.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="baseUri">
    /// The collection the seam's root stands for, as an absolute URI. Everything below it is
    /// reachable, nothing above it is.
    /// </param>
    public WebDavProvider(DavClient client, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The base has to be an absolute URI.", nameof(baseUri));
        }

        _client = client;
        _baseUri = WebDavPath.AsCollection(baseUri);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = WebDavPath.ToUri(_baseUri, path);
        string self = WebDavPath.Normalise(path);

        IReadOnlyList<DavResource> resources = await PropFindAsync(uri, DavDepth.One, $"Listing {self}", cancellationToken)
            .ConfigureAwait(false);

        List<RemoteEntry> entries = new(resources.Count);
        foreach (DavResource resource in resources)
        {
            RemoteEntry entry = ToEntry(resource);

            // Depth 1 describes the collection along with what is in it. Which place it takes
            // among the responses is not laid down anywhere, so it is told apart by its path.
            if (!string.Equals(entry.Path, self, StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = WebDavPath.ToUri(_baseUri, path);
        string what = $"Describing {WebDavPath.Normalise(path)}";

        IReadOnlyList<DavResource> resources = await PropFindAsync(uri, DavDepth.Zero, what, cancellationToken)
            .ConfigureAwait(false);

        if (resources.Count == 0)
        {
            throw new ProviderException(ProviderError.Protocol, $"{what} returned a multistatus without a response.");
        }

        return ToEntry(resources[0]);
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(
        string path,
        long offset = 0,
        long? count = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (count is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count.Value);
        }

        Uri uri = WebDavPath.ToUri(_baseUri, path);
        bool whole = offset == 0 && count is null;

        DavContent content;
        try
        {
            content = whole
                ? await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false)
                : await _client.GetRangeAsync(uri, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Reading {WebDavPath.Normalise(path)}", exception);
        }

        try
        {
            if (content.IsPartial)
            {
                // The server served the range, so the body is what was asked for and ends
                // where it should.
                return new DavContentStream(content, length: null);
            }

            // It answered with the whole resource instead, which RFC 9110 section 14.2 lets
            // it do. Making the promise of the seam come true is this provider's work, not
            // the caller's.
            if (offset > 0)
            {
                await SkipAsync(content.Content, offset, cancellationToken).ConfigureAwait(false);
            }

            return new DavContentStream(content, count);
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        Uri uri = WebDavPath.ToUri(_baseUri, path);

        try
        {
            return await _client
                .PutAsync(uri, content, contentType: null, ifMatch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Writing {WebDavPath.Normalise(path)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = WebDavPath.ToCollectionUri(_baseUri, path);

        try
        {
            await _client.MkColAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // 405 on a MKCOL is the server saying the method does not apply here, which it
            // says because something already occupies the path.
            ProviderError? occupied = exception.StatusCode == HttpStatusCode.MethodNotAllowed
                ? ProviderError.AlreadyExists
                : null;

            throw Failed($"Creating {WebDavPath.Normalise(path)}", exception, occupied);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = WebDavPath.ToUri(_baseUri, path);

        try
        {
            await _client.DeleteAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Deleting {WebDavPath.Normalise(path)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        Uri source = WebDavPath.ToUri(_baseUri, sourcePath);
        Uri destination = WebDavPath.ToUri(_baseUri, destinationPath);

        try
        {
            await _client.MoveAsync(source, destination, overwrite, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Relocation($"Moving {WebDavPath.Normalise(sourcePath)} to {WebDavPath.Normalise(destinationPath)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        Uri source = WebDavPath.ToUri(_baseUri, sourcePath);
        Uri destination = WebDavPath.ToUri(_baseUri, destinationPath);

        try
        {
            await _client
                .CopyAsync(source, destination, overwrite, DavDepth.Infinity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Relocation($"Copying {WebDavPath.Normalise(sourcePath)} to {WebDavPath.Normalise(destinationPath)}", exception);
        }
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            while (count > 0)
            {
                int wanted = (int)Math.Min(count, buffer.Length);
                int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    throw new ProviderException(
                        ProviderError.Protocol,
                        "The resource ended before the offset that was asked for was reached.");
                }

                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A destination that is taken while overwrite was not given comes back as 412. On the
    // seam that case is AlreadyExists; PreconditionFailed there means a lost update.
    private static ProviderException Relocation(string what, HttpRequestException exception)
    {
        ProviderError? taken = exception.StatusCode == HttpStatusCode.PreconditionFailed
            ? ProviderError.AlreadyExists
            : null;

        return Failed(what, exception, taken);
    }

    private static ProviderException Failed(string what, HttpRequestException exception, ProviderError? error = null) =>
        new(error ?? Classify(exception.StatusCode), $"{what} failed.", exception);

    private static ProviderError Classify(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone => ProviderError.NotFound,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Locked => ProviderError.PermissionDenied,
        HttpStatusCode.PreconditionFailed => ProviderError.PreconditionFailed,
        HttpStatusCode.Conflict => ProviderError.Conflict,
        HttpStatusCode.InsufficientStorage => ProviderError.InsufficientStorage,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            ProviderError.Unreachable,

        // No status at all means the request never got an answer: name resolution, the
        // connection or the handshake.
        null => ProviderError.Unreachable,
        _ => ProviderError.Unknown,
    };

    private async Task<IReadOnlyList<DavResource>> PropFindAsync(
        Uri uri,
        DavDepth depth,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client
                .PropFindAsync(uri, depth, properties: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed(what, exception);
        }
        catch (FormatException exception)
        {
            throw new ProviderException(ProviderError.Protocol, $"{what} returned a body that is not a multistatus.", exception);
        }
    }

    private RemoteEntry ToEntry(DavResource resource) =>
        new(WebDavPath.FromHref(_baseUri, resource.Href), resource.IsCollection)
        {
            Length = resource.ContentLength,
            LastModified = resource.LastModified,
            ETag = resource.ETag,
            ContentType = resource.ContentType,
        };
}
