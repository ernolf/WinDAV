// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Net;
using System.Xml.Linq;
using WinDav.Abstractions;

namespace WinDav.Dav;

/// <summary>
/// The seam, served over RFC 4918 and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Everything the server says stops here. Statuses become a <see cref="ProviderError"/>,
/// hrefs become paths, and a range the server ignored is made good on before the stream is
/// handed out.
/// </para>
/// <para>
/// A vendor changes little: which properties a PROPFIND asks for
/// (<see cref="RequestedProperties"/>), what two of them mean
/// (<see cref="ReadId(DavResource)"/> and <see cref="ReadPermissions(DavResource)"/>), what
/// a described resource becomes as a whole (<see cref="ToEntry"/>), and how bytes are
/// written (<see cref="WriteAsync(string, Stream, string?, CancellationToken)"/>).
/// Everything else is the protocol, which is the same everywhere.
/// </para>
/// </remarks>
public abstract class DavStorageProvider : IStorageProvider
{
    /// <summary>
    /// Initialises a new instance of the <see cref="DavStorageProvider"/> class.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="baseUri">
    /// The collection the seam's root stands for, as an absolute URI. Everything below it is
    /// reachable, nothing above it is.
    /// </param>
    protected DavStorageProvider(DavClient client, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The base has to be an absolute URI.", nameof(baseUri));
        }

        Client = client;
        BaseUri = DavPath.AsCollection(baseUri);
    }

    /// <summary>
    /// Gets the client the requests go out on.
    /// </summary>
    protected DavClient Client { get; }

    /// <summary>
    /// Gets the collection the seam's root stands for, always ending in a slash.
    /// </summary>
    protected Uri BaseUri { get; }

    /// <summary>
    /// Gets the properties a PROPFIND asks for, or <see langword="null"/> to ask for all of
    /// them. A server answers <c>allprop</c> with the properties it considers live, so a
    /// vendor after its own has to name them.
    /// </summary>
    protected virtual IReadOnlyList<XName>? RequestedProperties => null;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToUri(BaseUri, path);
        string self = DavPath.Normalise(path);

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
        Uri uri = DavPath.ToUri(BaseUri, path);
        string what = $"Describing {DavPath.Normalise(path)}";

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

        Uri uri = DavPath.ToUri(BaseUri, path);
        bool whole = offset == 0 && count is null;

        DavContent content;
        try
        {
            content = whole
                ? await Client.GetAsync(uri, cancellationToken).ConfigureAwait(false)
                : await Client.GetRangeAsync(uri, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Reading {DavPath.Normalise(path)}", exception);
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
    public virtual async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        Uri uri = DavPath.ToUri(BaseUri, path);

        try
        {
            return await Client
                .PutAsync(uri, content, contentType: null, ifMatch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Writing {DavPath.Normalise(path)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToCollectionUri(BaseUri, path);

        try
        {
            await Client.MkColAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // 405 on a MKCOL is the server saying the method does not apply here, which it
            // says because something already occupies the path.
            ProviderError? occupied = exception.StatusCode == HttpStatusCode.MethodNotAllowed
                ? ProviderError.AlreadyExists
                : null;

            throw Failed($"Creating {DavPath.Normalise(path)}", exception, occupied);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToUri(BaseUri, path);

        try
        {
            await Client.DeleteAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Deleting {DavPath.Normalise(path)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        Uri source = DavPath.ToUri(BaseUri, sourcePath);
        Uri destination = DavPath.ToUri(BaseUri, destinationPath);

        try
        {
            await Client.MoveAsync(source, destination, overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Relocation($"Moving {DavPath.Normalise(sourcePath)} to {DavPath.Normalise(destinationPath)}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        Uri source = DavPath.ToUri(BaseUri, sourcePath);
        Uri destination = DavPath.ToUri(BaseUri, destinationPath);

        try
        {
            await Client
                .CopyAsync(source, destination, overwrite, DavDepth.Infinity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Relocation($"Copying {DavPath.Normalise(sourcePath)} to {DavPath.Normalise(destinationPath)}", exception);
        }
    }

    /// <summary>
    /// Turns a failed request into the exception the seam raises.
    /// </summary>
    /// <param name="what">What was being attempted, for the message.</param>
    /// <param name="exception">The failure as the client reported it.</param>
    /// <param name="error">
    /// The case to report, or <see langword="null"/> to read it off the status.
    /// </param>
    /// <returns>The exception to throw.</returns>
    protected static ProviderException Failed(string what, HttpRequestException exception, ProviderError? error = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new ProviderException(error ?? Classify(exception.StatusCode), $"{what} failed.", exception);
    }

    /// <summary>
    /// Reads a status that carries no further meaning from the verb it answered.
    /// </summary>
    /// <param name="status">The status the server sent, or <see langword="null"/> if none.</param>
    /// <returns>The case the seam reports.</returns>
    protected static ProviderError Classify(HttpStatusCode? status) => status switch
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

    /// <summary>
    /// Turns a described resource into an entry of the seam.
    /// </summary>
    /// <param name="resource">What the server said about it.</param>
    /// <returns>The entry a caller sees.</returns>
    protected virtual RemoteEntry ToEntry(DavResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new RemoteEntry(DavPath.FromHref(BaseUri, resource.Href), resource.IsCollection)
        {
            Length = resource.ContentLength,
            LastModified = resource.LastModified,
            Created = resource.CreationDate,
            ETag = resource.ETag,
            ContentType = resource.ContentType,
            Id = ReadId(resource),
            Permissions = ReadPermissions(resource),
        };
    }

    /// <summary>
    /// Reads what the store calls the resource. See <see cref="RemoteEntry.Id"/>.
    /// </summary>
    /// <param name="resource">What the server said about it.</param>
    /// <returns>
    /// The identifier, or <see langword="null"/> when there is none. RFC 4918 has no such
    /// property, so this is where a vendor that does answers with its own.
    /// </returns>
    protected virtual string? ReadId(DavResource resource) => null;

    /// <summary>
    /// Reads what may be done with the resource. See <see cref="RemoteEntry.Permissions"/>.
    /// </summary>
    /// <param name="resource">What the server said about it.</param>
    /// <returns>
    /// The permissions, or <see langword="null"/> when the server did not state them. RFC
    /// 4918 has no property for them either, so the same applies as to
    /// <see cref="ReadId(DavResource)"/>.
    /// </returns>
    protected virtual EntryPermissions? ReadPermissions(DavResource resource) => null;

    /// <summary>
    /// Asks the server about a resource and turns the ways that can fail into the seam's
    /// exception.
    /// </summary>
    /// <param name="uri">The resource to ask about.</param>
    /// <param name="depth">How far the question reaches.</param>
    /// <param name="what">What was being attempted, for the message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One entry per resource the server described.</returns>
    protected async Task<IReadOnlyList<DavResource>> PropFindAsync(
        Uri uri,
        DavDepth depth,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Client
                .PropFindAsync(uri, depth, RequestedProperties, cancellationToken)
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
}
