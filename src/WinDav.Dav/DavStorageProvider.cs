// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Globalization;
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
/// a described resource becomes as a whole (<see cref="ToEntry"/>), how bytes are written
/// (<see cref="WriteAsync(string, Stream, string?, EntryTimes, bool, CancellationToken)"/>) and
/// which properties carry the times (<see cref="TimeProperties"/>).
/// Everything else is the protocol, which is the same everywhere.
/// </para>
/// </remarks>
public abstract class DavStorageProvider : IStorageProvider
{
    // The two of RFC 4331, asked for by name. They are live properties a server works out on
    // request, so naming them keeps the question to what is being asked and keeps the answer
    // free of a whole listing's worth of properties nobody wants here.
    private static readonly XName[] s_space = [DavNames.QuotaAvailableBytes, DavNames.QuotaUsedBytes];

    // Set once a server has said it will not have its times written, and then the request
    // is not sent again for the life of this provider. A server answers that the same way
    // for every entry, so asking a second time buys the same refusal and one more round
    // trip on every file that is copied.
    private bool _timesRefused;

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
    public async Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToUri(BaseUri, path);
        string self = DavPath.Normalise(path);

        IReadOnlyList<DavResource> resources = await PropFindAsync(uri, DavDepth.One, $"Listing {self}", cancellationToken)
            .ConfigureAwait(false);

        List<RemoteEntry> entries = new(resources.Count);
        RemoteEntry? collection = null;

        foreach (DavResource resource in resources)
        {
            RemoteEntry entry = ToEntry(resource);

            // Depth 1 describes the collection along with what is in it. Which place it takes
            // among the responses is not laid down anywhere, so it is told apart by its path.
            // It is kept rather than dropped: it is the answer to what the directory itself
            // is, and it has already been paid for.
            if (string.Equals(entry.Path, self, StringComparison.Ordinal))
            {
                collection = entry;
            }
            else
            {
                entries.Add(entry);
            }
        }

        return new DirectoryListing(entries, collection);
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
    /// <remarks>
    /// Plain WebDAV has no way of sending the times along with the contents, so they follow
    /// in a PROPPATCH of their own. That second request is what makes the entity tag of the
    /// PUT worthless, which is why none is handed back when one went out: the file changed
    /// after the server named the tag. A vendor whose PUT takes the times overrides this and
    /// keeps its tag.
    /// </remarks>
    public virtual async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        EntryTimes times = default,
        bool mustBeNew = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        Uri uri = DavPath.ToUri(BaseUri, path);
        string? eTag;

        try
        {
            eTag = await Client
                .PutAsync(
                    uri,
                    content,
                    contentType: null,
                    ifMatch,
                    ifNoneMatch: mustBeNew ? "*" : null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Writing {DavPath.Normalise(path)}", exception, Occupied(exception, mustBeNew));
        }

        return await WriteTimesAsync(path, times, cancellationToken).ConfigureAwait(false) ? null : eTag;
    }

    /// <inheritdoc/>
    public Task SetTimesAsync(string path, EntryTimes times, CancellationToken cancellationToken = default) =>
        WriteTimesAsync(path, times, cancellationToken);

    /// <inheritdoc/>
    public async Task<string?> CreateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToUri(BaseUri, path);

        try
        {
            using MemoryStream empty = new();

            return await Client
                .PutAsync(uri, empty, contentType: null, ifMatch: null, ifNoneMatch: "*", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // The condition is the whole point of the request: 412 is the server saying
            // something is already at the path, and that it has written nothing.
            ProviderError? occupied = exception.StatusCode == HttpStatusCode.PreconditionFailed
                ? ProviderError.AlreadyExists
                : null;

            throw Failed($"Creating {DavPath.Normalise(path)}", exception, occupied);
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

    /// <inheritdoc/>
    public async Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri uri = DavPath.ToCollectionUri(BaseUri, path);
        string what = $"Asking after the room in {DavPath.Normalise(path)}";

        IReadOnlyList<DavResource> resources = await PropFindAsync(uri, DavDepth.Zero, s_space, what, cancellationToken)
            .ConfigureAwait(false);

        if (resources.Count == 0)
        {
            throw new ProviderException(ProviderError.Protocol, $"{what} returned a multistatus without a response.");
        }

        return new StorageSpace
        {
            Used = ReadBytes(resources[0], DavNames.QuotaUsedBytes),
            Available = ReadBytes(resources[0], DavNames.QuotaAvailableBytes),
        };
    }

    /// <summary>
    /// Names the properties that carry the times on this kind of server.
    /// </summary>
    /// <param name="times">What is to be set, with either half possibly absent.</param>
    /// <returns>
    /// A property name and the text to write for each time that can be set here, and
    /// nothing for a time this kind of server keeps to itself.
    /// </returns>
    /// <remarks>
    /// Only the creation date is here, because only the creation date is in the protocol:
    /// RFC 4918 section 15.1 defines <c>DAV:creationdate</c> and leaves servers free to let
    /// it be written, while section 15.7 says <c>DAV:getlastmodified</c> SHOULD be
    /// protected, and a server that follows that advice refuses it. A modification time is
    /// therefore a vendor's own business, and a provider that has a way of setting one adds
    /// it here.
    /// </remarks>
    protected virtual IEnumerable<KeyValuePair<XName, string>> TimeProperties(EntryTimes times)
    {
        if (times.Created is DateTimeOffset created)
        {
            // The date-time of RFC 3339, which is the form section 15.1 asks for, written
            // in UTC so that the offset is a Z and not a number some parser has to add up.
            yield return new(
                DavNames.CreationDate,
                created.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
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
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderError.PermissionDenied,
        HttpStatusCode.PreconditionFailed => ProviderError.PreconditionFailed,
        HttpStatusCode.Conflict => ProviderError.Conflict,
        HttpStatusCode.InsufficientStorage => ProviderError.InsufficientStorage,

        // Both are the server saying "not now" rather than "not you": 423 is a resource
        // held, by a lock or by the server's own bookkeeping under load, and 503 is the
        // server as a whole. A caller that reads them as a refusal of the credential would
        // ask the person at the keyboard to fix something that is not wrong.
        HttpStatusCode.Locked or HttpStatusCode.ServiceUnavailable => ProviderError.Busy,

        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => ProviderError.Unreachable,

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
    protected Task<IReadOnlyList<DavResource>> PropFindAsync(
        Uri uri,
        DavDepth depth,
        string what,
        CancellationToken cancellationToken) =>
        PropFindAsync(uri, depth, RequestedProperties, what, cancellationToken);

    /// <summary>
    /// Asks the server for named properties of a resource, for a question that is not the one
    /// <see cref="RequestedProperties"/> answers.
    /// </summary>
    /// <param name="uri">The resource to ask about.</param>
    /// <param name="depth">How far the question reaches.</param>
    /// <param name="properties">
    /// The properties to ask for, or <see langword="null"/> to ask for all of them.
    /// </param>
    /// <param name="what">What was being attempted, for the message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One entry per resource the server described.</returns>
    protected async Task<IReadOnlyList<DavResource>> PropFindAsync(
        Uri uri,
        DavDepth depth,
        IReadOnlyList<XName>? properties,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Client
                .PropFindAsync(uri, depth, properties, cancellationToken)
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

    // Whether the server wrote any of what it was sent. A property it will not have comes
    // back in a propstat of its own with a status outside 2xx, so one accepted property is
    // enough to say the request was worth sending.
    // A 412 means one of two things and the request that got it says which. Where the write
    // asked to be the one that makes the name, the server is saying somebody else made it and
    // that nothing has been written; where it named a version, it is saying the version has
    // moved on. Only the first is a name already taken.
    private static ProviderError? Occupied(HttpRequestException exception, bool mustBeNew) =>
        mustBeNew && exception.StatusCode == HttpStatusCode.PreconditionFailed
            ? ProviderError.AlreadyExists
            : null;

    private static bool Accepted(IReadOnlyList<DavResponse> answered, KeyValuePair<XName, string>[] properties)
    {
        foreach (DavResponse response in answered)
        {
            foreach (DavPropertyStatus status in response.PropertyStatuses)
            {
                if (status.StatusCode is < 200 or > 299)
                {
                    continue;
                }

                foreach (KeyValuePair<XName, string> property in properties)
                {
                    if (status.Properties.ContainsKey(property.Key))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Returns whether a request went out, because whoever has just written a file holds an
    // entity tag that is only good for as long as none did.
    private async Task<bool> WriteTimesAsync(string path, EntryTimes times, CancellationToken cancellationToken)
    {
        if (times.IsEmpty || _timesRefused)
        {
            return false;
        }

        KeyValuePair<XName, string>[] properties = [.. TimeProperties(times)];

        if (properties.Length == 0)
        {
            return false;
        }

        Uri uri = DavPath.ToUri(BaseUri, path);
        IReadOnlyList<DavResponse> answered;

        try
        {
            answered = await Client.PropPatchAsync(uri, properties, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            if (exception.StatusCode == HttpStatusCode.NotFound)
            {
                throw Failed($"Setting the times on {DavPath.Normalise(path)}", exception);
            }

            // Not knowing the method, and not understanding the body, are said about the
            // server and not about this entry. Anything else can be about this entry alone,
            // so it is left to be asked again on the next one.
            _timesRefused = exception.StatusCode
                is HttpStatusCode.BadRequest
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotImplemented;

            return true;
        }

        if (!Accepted(answered, properties))
        {
            // A 207 that refuses every property is the server saying what it will not have
            // written, which is about the server and not about this entry.
            _timesRefused = true;
        }

        return true;
    }

    // RFC 4331 states both figures as a number of bytes, and a number of bytes is never
    // negative. Nextcloud nevertheless answers -1, -2 or -3 for a quota it has not worked
    // out, does not know, or does not impose. None of the three is an amount, so all of them
    // are read as the silence they stand for, along with anything that is no number at all.
    private static long? ReadBytes(DavResource resource, XName name)
    {
        if (!resource.Properties.TryGetValue(name, out XElement? element))
        {
            return null;
        }

        bool read = long.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes);

        return read && bytes >= 0 ? bytes : null;
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
