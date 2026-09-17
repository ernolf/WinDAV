// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Dav;
using WinDav.Providers.Nextcloud.Ocs;

namespace WinDav.Providers.Nextcloud;

/// <summary>
/// A Nextcloud store, which is a WebDAV store that can also take a large file in pieces and
/// says more about an entry than RFC 4918 provides for.
/// </summary>
/// <remarks>
/// <para>
/// Everything a Nextcloud server does over plain RFC 4918 it inherits. The first thing it
/// adds here is the chunked upload of version 2, documented under
/// <see href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/WebDAV/chunking.html"/>:
/// the pieces are written into an upload directory of their own and the server assembles
/// them when the last one has arrived. A connection that breaks then costs one chunk instead
/// of the whole transfer.
/// </para>
/// <para>
/// The chunked path is taken for every file of known length that is larger than one chunk,
/// in chunks of the size the server states and as many at once as it allows; see
/// <see cref="WriteAsync(string, Stream, string?, EntryTimes, bool, CancellationToken)"/>
/// for the cases that fall back to a single PUT and for where the conditions of a write go.
/// </para>
/// <para>
/// The other thing it adds is the two properties of the vendor namespaces that the seam has
/// a place for, the identifier and the permissions. See <see cref="NextcloudNames"/> for
/// where they are documented and <see cref="RequestedProperties"/> for why they have to be
/// asked for by name.
/// </para>
/// </remarks>
public sealed class NextcloudProvider : DavStorageProvider
{
    // All three from the protocol documentation: a chunk is no smaller than five megabytes
    // and no larger than five gigabytes, and the name of a chunk has to be a number from 1
    // to 10000.
    private const long SmallestChunkSize = 5L * 1024 * 1024;

    private const long LargestChunkSize = 5L * 1024 * 1024 * 1024;

    private const int MaximumChunks = 10000;

    // What the web interface sends with when the server does not state its limits, which a
    // server before Nextcloud 31 does not.
    private const long UnstatedChunkSize = 10L * 1024 * 1024;

    private const int UnstatedChunksInFlight = 5;

    // Where a Nextcloud answers WebDAV, relative to the server's base address.
    private const string DavEndpoint = "remote.php/dav/";

    // The name the server answers under once every chunk is in place. Moving it is what
    // triggers the assembly.
    private const string AssembledName = ".file";

    // Nextcloud wants the target on every request of an upload, not only on the last one,
    // so it can refuse early what it would have to refuse in the end anyway.
    private const string DestinationHeader = "Destination";

    // The size of the whole file, which is what a quota can be checked against. Without it
    // an upload over quota is only refused when the chunks are assembled.
    private const string TotalLengthHeader = "OC-Total-Length";

    // The two times the server takes on the request that writes the file, so that setting
    // them costs no request of its own. Both are unix timestamps in seconds.
    private const string ModifiedHeader = "X-OC-MTime";

    private const string CreatedHeader = "X-OC-CTime";

    // RFC 4918 section 10.4. Its tagged list names the resource a condition is about, which
    // is what lets the entity tag of the target ride on a request sent to the upload.
    private const string IfHeader = "If";

    // The server refuses a timestamp inside the first day after 1970 as a value that cannot
    // be meant, whichever time zone it is read in, and the refusal arrives after the file
    // has been written. Nothing older than this is sent.
    private const long EarliestTimestamp = 24 * 60 * 60;

    // How long an assembly that went unanswered is waited for, per gigabyte of file and at
    // most. Both are the desktop client's, which gives the assembling MOVE this long before
    // it calls the request lost; a server it can take a file from is one this can wait for.
    private static readonly TimeSpan s_assemblyPerGigabyte = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan s_longestAssembly = TimeSpan.FromMinutes(30);

    // How long one chunk sent ahead is meant to take. The desktop client sizes its chunks by
    // how long they take, towards a minute (targetChunkUploadDuration). What goes ahead is kept
    // shorter here, because a program copying a file onto the mount has counted every byte
    // written ahead of the chunks on their way, and waits at its end for as long as they take.
    private static readonly TimeSpan s_chunkDuration = TimeSpan.FromSeconds(10);

    // The properties every PROPFIND asks for: the five of RFC 4918 the seam reads, the
    // creation date, and the two of the vendor namespaces.
    private static readonly XName[] s_properties =
    [
        DavNames.ResourceType,
        DavNames.GetContentLength,
        DavNames.GetLastModified,
        DavNames.CreationDate,
        DavNames.GetETag,
        DavNames.GetContentType,
        NextcloudNames.Id,
        NextcloudNames.Permissions,
    ];

    private readonly Uri _uploads;

    private readonly OcsClient _ocs;

    private readonly Lock _limitsGate = new();

    // What the server allows a chunked upload, asked for with the first file that could go
    // in chunks.
    private Task<ChunkLimits>? _limits;

    /// <summary>
    /// Initialises a new instance of the <see cref="NextcloudProvider"/> class.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="ocs">
    /// Asks the same server how a file may be sent in chunks, which is for its administrator
    /// to set up. It is asked once, with the first file large enough to go in chunks.
    /// </param>
    /// <param name="baseUri">
    /// The collection the seam's root stands for, usually
    /// <c>https://server/remote.php/dav/files/&lt;user&gt;/</c> but any collection below it
    /// works just as well.
    /// </param>
    /// <param name="uploadsUri">
    /// The user's upload area, <c>https://server/remote.php/dav/uploads/&lt;user&gt;/</c>.
    /// It is asked for rather than worked out from <paramref name="baseUri"/>, because
    /// <paramref name="baseUri"/> may point anywhere below the user's files and a server may
    /// live under a path of its own.
    /// </param>
    public NextcloudProvider(DavClient client, OcsClient ocs, Uri baseUri, Uri uploadsUri)
        : base(client, baseUri)
    {
        ArgumentNullException.ThrowIfNull(ocs);
        ArgumentNullException.ThrowIfNull(uploadsUri);

        if (!uploadsUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The upload area has to be an absolute URI.", nameof(uploadsUri));
        }

        _ocs = ocs;
        _uploads = DavPath.AsCollection(uploadsUri);
    }

    // How often the upload directory is looked at while an unanswered assembly is waited
    // for. One PROPFIND at depth 0 every few seconds is nothing next to the assembly the
    // server is busy with.
    internal TimeSpan AssemblyPoll { get; init; } = TimeSpan.FromSeconds(5);

    // The least time an unanswered assembly is waited for, whatever the size of the file:
    // the desktop client's timeout for any request.
    internal TimeSpan ShortestAssemblyWait { get; init; } = TimeSpan.FromMinutes(5);

    internal TimeSpan LongestAssemblyWait { get; init; } = s_longestAssembly;

    /// <summary>
    /// Gets the properties a PROPFIND asks for.
    /// </summary>
    /// <remarks>
    /// The server answers a PROPFIND without a body with a small set of its own choosing, and
    /// <c>DAV:allprop</c> with the properties it considers live, which the vendor namespaces
    /// are not part of. Either way, what is not named does not arrive, so the list is named in
    /// full. Naming it also fixes what the request costs: the server works out only what was
    /// asked for.
    /// </remarks>
    protected override IReadOnlyList<XName> RequestedProperties => s_properties;

    /// <summary>
    /// Builds a provider for a user's file area, with the two URIs a stock Nextcloud uses.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="ocs">Asks the same server how a file may be sent in chunks.</param>
    /// <param name="davRoot">The DAV root, <c>https://server/remote.php/dav/</c>.</param>
    /// <param name="userId">
    /// The user's identifier, which is the one in the path and not the display name.
    /// </param>
    /// <param name="remotePath">
    /// The directory below the user's files that becomes the root, or <c>/</c> for all of
    /// them.
    /// </param>
    /// <returns>A provider rooted there.</returns>
    public static NextcloudProvider ForUser(
        DavClient client,
        OcsClient ocs,
        Uri davRoot,
        string userId,
        string remotePath = "/")
    {
        ArgumentNullException.ThrowIfNull(davRoot);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        Uri root = DavPath.AsCollection(davRoot);
        string segment = Uri.EscapeDataString(userId);
        Uri files = DavPath.ToCollectionUri(new Uri(root, $"files/{segment}/"), remotePath);

        return new NextcloudProvider(client, ocs, files, new Uri(root, $"uploads/{segment}/"));
    }

    /// <summary>
    /// Builds a provider from a server's base address, which is what a configuration holds.
    /// </summary>
    /// <param name="httpClient">The client the requests go out on, WebDAV and OCS alike.</param>
    /// <param name="server">The server, <c>https://server/</c> or an instance below a path.</param>
    /// <param name="userId">
    /// The user's identifier, which is the one in the path and not the display name.
    /// </param>
    /// <param name="remotePath">
    /// The directory below the user's files that becomes the root, or <c>/</c> for all of
    /// them.
    /// </param>
    /// <returns>A provider rooted there.</returns>
    /// <remarks>
    /// This and <see cref="ForUser"/> are the only places that know where a Nextcloud keeps
    /// its DAV endpoint, its files and its upload area.
    /// </remarks>
    public static NextcloudProvider ForServer(HttpClient httpClient, Uri server, string userId, string remotePath = "/")
    {
        ArgumentNullException.ThrowIfNull(server);

        return ForUser(
            new DavClient(httpClient),
            new OcsClient(httpClient, server),
            new Uri(DavPath.AsCollection(server), DavEndpoint),
            userId,
            remotePath);
    }

    /// <summary>
    /// Writes a file, in pieces when that is both possible and worth it.
    /// </summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="ifMatch">See <see cref="DavStorageProvider.WriteAsync"/>.</param>
    /// <param name="times">See <see cref="DavStorageProvider.WriteAsync"/>.</param>
    /// <param name="mustBeNew">See <see cref="DavStorageProvider.WriteAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the upload and clears up after it.</param>
    /// <returns>
    /// The entity tag when the server stated one. On the chunked path it is the one the
    /// assembling MOVE answers with, which the server works out once the file is in place.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three cases go out as a single PUT instead. A stream that cannot be measured has no
    /// total length to declare, and the server needs one. A file no larger than one chunk
    /// would be a PUT with three extra requests around it; one no larger than the smallest
    /// chunk the protocol allows goes without the server being asked anything. And a server
    /// that states a chunk size of zero wants no chunks at all.
    /// </para>
    /// <para>
    /// On the chunked path the conditions ride on the assembling MOVE, which is the request
    /// that writes the file. <paramref name="mustBeNew"/> is <c>Overwrite: F</c>, which the
    /// server checks before it assembles anything. <paramref name="ifMatch"/> cannot be an
    /// <c>If-Match</c> there, because that would be compared with the <c>.file</c> the MOVE
    /// is sent to; it goes in the tagged list of RFC 4918 section 10.4, which names the
    /// target. Where either does not hold, the server answers 412 as it does to the single
    /// PUT. The one difference is a target that has gone away since its tag was read, which
    /// is a 404 on this path.
    /// </para>
    /// <para>
    /// The times ride on the request that writes the file, which is the PUT here and the
    /// assembling MOVE on the chunked path, so they cost nothing. The server sets them
    /// before it works out the entity tag it answers with, so unlike the PROPPATCH the base
    /// class would send, this leaves the tag good.
    /// </para>
    /// <para>
    /// How large a chunk is and how many are out at once is for the server to say. Both are
    /// read from its capabilities, as its administrator set them up, once per provider; a
    /// server that states nothing gets what the web interface sends it then. The server puts
    /// the chunks together in the order of their names and not of their arrival.
    /// </para>
    /// <para>
    /// Every chunk reads its own stretch of <paramref name="content"/> while it travels, so
    /// the chunks out at once cost no memory of their own, and nothing else may use the stream
    /// until the write has finished.
    /// </para>
    /// </remarks>
    public override async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        EntryTimes times = default,
        bool mustBeNew = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        long? length = content.CanSeek ? content.Length - content.Position : null;

        if (length is not long size || size <= SmallestChunkSize)
        {
            return await PutAsync(path, content, ifMatch, times, mustBeNew, cancellationToken).ConfigureAwait(false);
        }

        ChunkLimits limits = await LimitsAsync($"Writing {DavPath.Normalise(path)}", cancellationToken).ConfigureAwait(false);

        if (limits.Size == 0 || size <= limits.Size)
        {
            return await PutAsync(path, content, ifMatch, times, mustBeNew, cancellationToken).ConfigureAwait(false);
        }

        return await UploadInChunksAsync(path, content, size, limits, ifMatch, times, mustBeNew, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Begins an upload in chunks whose front goes to the server before the rest of the file
    /// exists.
    /// </summary>
    /// <param name="path">Where the file goes.</param>
    /// <returns>The upload.</returns>
    /// <remarks>
    /// <para>
    /// The pieces are chunks of version 2, as large as the server states a chunk may be, sent
    /// into an upload directory of their own before anybody knows how long the file will be.
    /// The rest follows when the file is finished, as
    /// <see cref="WriteAsync(string, Stream, string?, EntryTimes, bool, CancellationToken)"/>
    /// would send it, and the same assembling MOVE carries the conditions and the times, so
    /// nothing at the path changes before it. A server that wants no chunks takes no pieces,
    /// and the whole file goes with the finish.
    /// </para>
    /// <para>
    /// The chunks sent ahead cannot declare the total length, so a file over quota is only
    /// refused once the rest of it goes, which does. At most half of the ten thousand names
    /// go ahead; the other half is kept for the rest, whose chunks grow where it would not
    /// fit otherwise.
    /// </para>
    /// </remarks>
    public override IUpload? BeginUpload(string path) => new ChunkedUpload(this, path);

    /// <summary>
    /// Reads <c>oc:id</c>, the file identifier with the identifier of the instance behind it.
    /// </summary>
    /// <param name="resource">What the server said about it.</param>
    /// <returns>The identifier, or <see langword="null"/> when the server sent none.</returns>
    /// <remarks>
    /// The other identifier, <c>oc:fileid</c>, is the same number without the instance behind
    /// it. It is the shorter one and it is the one the web interface puts in a link, but two
    /// servers that share a file hand out the same number for different files, so it is not
    /// what an entry can be recognised by.
    /// </remarks>
    protected override string? ReadId(DavResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!resource.Properties.TryGetValue(NextcloudNames.Id, out XElement? element))
        {
            return null;
        }

        string id = element.Value;

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Reads <c>oc:permissions</c>, which the server writes as one letter per permission.
    /// </summary>
    /// <param name="resource">What the server said about it.</param>
    /// <returns>
    /// What may be done with the entry, or <see langword="null"/> when the server did not
    /// state it. A property that arrived empty becomes <see cref="EntryPermissions.None"/>,
    /// which is the server saying that nothing may be done, and not the same as silence.
    /// </returns>
    /// <remarks>
    /// Two of the letters the server can send are dropped here. <c>S</c> and <c>M</c> say
    /// that an entry is shared or mounted from elsewhere, which is where it comes from and
    /// not what may be done with it. A letter that is not known is dropped as well: the list
    /// belongs to the server, and a later one may lengthen it.
    /// </remarks>
    protected override EntryPermissions? ReadPermissions(DavResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!resource.Properties.TryGetValue(NextcloudNames.Permissions, out XElement? element))
        {
            return null;
        }

        EntryPermissions permissions = EntryPermissions.None;
        foreach (char letter in element.Value)
        {
            permissions |= letter switch
            {
                'G' => EntryPermissions.Read,
                'W' => EntryPermissions.Write,
                'D' => EntryPermissions.Delete,
                'N' => EntryPermissions.Rename,
                'V' => EntryPermissions.Move,
                'C' => EntryPermissions.CreateFile,
                'K' => EntryPermissions.CreateDirectory,
                'R' => EntryPermissions.Share,
                _ => EntryPermissions.None,
            };
        }

        return permissions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The modification time is added to what the base class names. The server takes it
    /// under <c>DAV:lastmodified</c>, which is not the protocol's protected
    /// <c>DAV:getlastmodified</c> but a name of its own; see
    /// <see cref="NextcloudNames.LastModified"/>. This is the way in for a directory and for
    /// an entry whose times are set without its contents being written, where there is no
    /// upload for a header to ride on.
    /// </remarks>
    protected override IEnumerable<KeyValuePair<XName, string>> TimeProperties(EntryTimes times)
    {
        foreach (KeyValuePair<XName, string> property in base.TimeProperties(times))
        {
            yield return property;
        }

        if (Timestamp(times.LastModified) is string modified)
        {
            yield return new(NextcloudNames.LastModified, modified);
        }
    }

    // The seconds since 1970 the server wants, or nothing where the time is absent or lies
    // in the stretch it refuses.
    private static string? Timestamp(DateTimeOffset? time)
    {
        if (time is not DateTimeOffset value)
        {
            return null;
        }

        long seconds = value.ToUnixTimeSeconds();

        return seconds > EarliestTimestamp ? seconds.ToString(CultureInfo.InvariantCulture) : null;
    }

    // The times as the headers the server reads them from, and an empty list where there is
    // nothing it would take.
    private static KeyValuePair<string, string>[] TimeHeaders(EntryTimes times)
    {
        List<KeyValuePair<string, string>> headers = [];

        if (Timestamp(times.LastModified) is string modified)
        {
            headers.Add(new(ModifiedHeader, modified));
        }

        if (Timestamp(times.Created) is string created)
        {
            headers.Add(new(CreatedHeader, created));
        }

        return [.. headers];
    }

    private async Task<string?> PutAsync(
        string path,
        Stream content,
        string? ifMatch,
        EntryTimes times,
        bool mustBeNew,
        CancellationToken cancellationToken)
    {
        Uri uri = DavPath.ToUri(BaseUri, path);
        KeyValuePair<string, string>[] headers = TimeHeaders(times);

        try
        {
            return await Client
                .PutAsync(
                    uri,
                    content,
                    contentType: null,
                    ifMatch,
                    ifNoneMatch: mustBeNew ? "*" : null,
                    headers: headers.Length == 0 ? null : headers,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed($"Writing {DavPath.Normalise(path)}", exception, Occupied(exception, mustBeNew));
        }
    }

    private async Task<string?> UploadInChunksAsync(
        string path,
        Stream content,
        long length,
        ChunkLimits limits,
        string? ifMatch,
        EntryTimes times,
        bool mustBeNew,
        CancellationToken cancellationToken)
    {
        Uri target = DavPath.ToUri(BaseUri, path);
        Uri folder = UploadFolder();

        long chunkSize = ChunkSize(length, limits, sentAhead: 0);

        KeyValuePair<string, string>[] headers =
        [
            new(DestinationHeader, target.AbsoluteUri),
            new(TotalLengthHeader, length.ToString(CultureInfo.InvariantCulture)),
        ];

        try
        {
            await Client.MkColAsync(folder, headers, cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(content, folder, length, chunkSize, limits.InFlight, headers, firstNumber: 1, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            await DiscardAsync(folder).ConfigureAwait(false);

            throw Failed($"Writing {DavPath.Normalise(path)}", exception, Occupied(exception, mustBeNew));
        }
        catch
        {
            // Cancellation and a stream that came up short land here. Whatever it was, the
            // half-written upload directory is ours to take away.
            await DiscardAsync(folder).ConfigureAwait(false);

            throw;
        }

        return await AssembleAsync(path, folder, length, ifMatch, times, mustBeNew, cancellationToken).ConfigureAwait(false);
    }

    // Moves the assembled file into place once every chunk has arrived.
    private async Task<string?> AssembleAsync(
        string path,
        Uri folder,
        long length,
        string? ifMatch,
        EntryTimes times,
        bool mustBeNew,
        CancellationToken cancellationToken)
    {
        Uri target = DavPath.ToUri(BaseUri, path);

        // MOVE carries the destination as a parameter of its own, so only the length goes in
        // here; passing it twice would send the header twice. The times and the guard go here
        // too and nowhere earlier: the server reads them off the request that writes the file,
        // and on this path that is the assembly and not any of the chunks.
        List<KeyValuePair<string, string>> assembly =
        [
            new(TotalLengthHeader, length.ToString(CultureInfo.InvariantCulture)),
            .. TimeHeaders(times),
        ];

        if (ifMatch is not null)
        {
            assembly.Add(new(IfHeader, $"<{target.AbsoluteUri}> ([{ifMatch}])"));
        }

        // From here on the upload directory is only taken away on an answer the server gave
        // itself. The assembly runs on after the client has stopped listening, and it reads
        // the chunks while it writes: taking the directory away under it leaves a broken file
        // where the target was. That includes cancellation, which may have come after the
        // request left, so it leaves the directory to the server's own expiry.
        try
        {
            // Refusing to overwrite is how a name that has to be made is claimed. The server
            // checks it before any of the assembly happens, so a name somebody else has taken
            // in the meantime is left as they wrote it.
            return await Client
                .MoveAsync(new Uri(folder, AssembledName), target, overwrite: !mustBeNew, assembly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (Unanswered(exception.StatusCode))
        {
            return await AwaitAssemblyAsync(path, folder, length, ifMatch, exception, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            await DiscardAsync(folder).ConfigureAwait(false);

            throw Failed($"Writing {DavPath.Normalise(path)}", exception, Occupied(exception, mustBeNew));
        }
    }

    // The assembling MOVE got no answer from the server, only from a gateway that stopped
    // waiting or from a connection that broke. The server removes the upload directory once
    // the file is in place and leaves it where the assembly failed, so the directory is
    // watched until it has gone, and the target is then asked whether it is the file that
    // was sent. Anything short of that is reported as the failure the MOVE got, with the
    // directory left alone.
    private async Task<string?> AwaitAssemblyAsync(
        string path,
        Uri folder,
        long length,
        string? ifMatch,
        HttpRequestException unanswered,
        CancellationToken cancellationToken)
    {
        string what = $"Writing {DavPath.Normalise(path)}";
        TimeSpan wait = AssemblyWait(length);
        long started = Stopwatch.GetTimestamp();

        while (await StillThereAsync(folder, what, unanswered, cancellationToken).ConfigureAwait(false))
        {
            if (Stopwatch.GetElapsedTime(started) >= wait)
            {
                throw Failed(what, unanswered);
            }

            await Task.Delay(AssemblyPoll, cancellationToken).ConfigureAwait(false);
        }

        RemoteEntry written;
        try
        {
            written = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            throw Failed(what, unanswered);
        }

        // The length says the whole file is there, and a tag other than the one the write was
        // guarded with says it is not the file that was there before.
        return written.Length == length && (ifMatch is null || written.ETag != ifMatch)
            ? written.ETag
            : throw Failed(what, unanswered);
    }

    // Whether the upload directory is still there. A gateway that gave up on the MOVE may
    // give up on this as well while the server is busy assembling, so an answer that says
    // nothing about the directory counts as it still being there.
    private async Task<bool> StillThereAsync(
        Uri folder,
        string what,
        HttpRequestException unanswered,
        CancellationToken cancellationToken)
    {
        try
        {
            await Client.PropFindAsync(folder, DavDepth.Zero, [DavNames.ResourceType], cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (HttpRequestException exception) when (
            Unanswered(exception.StatusCode)
            || exception.StatusCode is HttpStatusCode.Locked or HttpStatusCode.ServiceUnavailable)
        {
            return true;
        }
        catch (HttpRequestException)
        {
            throw Failed(what, unanswered);
        }
        catch (FormatException)
        {
            throw Failed(what, unanswered);
        }
    }

    // A gateway's 502 or 504, or no status at all: the request may still be running on the
    // server behind it, so it has neither worked nor failed.
    private static bool Unanswered(HttpStatusCode? status) =>
        status is null or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private TimeSpan AssemblyWait(long length)
    {
        TimeSpan scaled = s_assemblyPerGigabyte * (length / 1e9);

        return scaled < ShortestAssemblyWait ? ShortestAssemblyWait
            : scaled > LongestAssemblyWait ? LongestAssemblyWait
            : scaled;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Every window is handed to the chunk that carries it, which disposes of it once the server has answered, whatever the answer. The rule cannot follow that transfer.")]
    private async Task SendChunksAsync(
        Stream content,
        Uri folder,
        long length,
        long chunkSize,
        int chunksInFlight,
        KeyValuePair<string, string>[] headers,
        int firstNumber,
        CancellationToken cancellationToken)
    {
        // Linked, so that the chunks still out can be called back when one of them fails
        // without cancelling anything that belongs to the caller.
        using CancellationTokenSource abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task> inFlight = new(chunksInFlight);

        // Every chunk reads its own stretch of the content while it travels, so the chunks
        // out at once cost no memory of their own. They share the stream, and with it a lock.
        Lock gate = new();
        long start = content.Position;

        try
        {
            long left = length;
            for (int number = firstNumber; left > 0; number++)
            {
                await SettleAsync(inFlight, chunksInFlight - 1).ConfigureAwait(false);

                long wanted = Math.Min(chunkSize, left);

                Uri chunk = new(folder, ChunkName(number));

                inFlight.Add(SendChunkAsync(
                    chunk,
                    new WindowStream(content, start + (length - left), wanted, gate),
                    headers,
                    abandon.Token));
                left -= wanted;
            }

            await SettleAsync(inFlight, 0).ConfigureAwait(false);
        }
        catch
        {
            // The upload directory is taken away after this, so nothing may still be on its
            // way into it: the rest are called back and waited for before the failure is
            // passed on.
            await abandon.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(inFlight).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            throw;
        }
    }

    // The piece belongs to the chunk from here on and is disposed of once the server has
    // answered, whatever the answer was.
    private async Task SendChunkAsync(
        Uri chunk,
        Stream piece,
        KeyValuePair<string, string>[] headers,
        CancellationToken cancellationToken)
    {
        try
        {
            await Client
                .PutAsync(chunk, piece, contentType: null, ifMatch: null, headers: headers, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await piece.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Takes every answered chunk off the list and passes on the first failure among them,
    // then waits for answers until no more than the given number are still out. A refused
    // chunk is seen here before the next one is started, not only at the end.
    private static async Task SettleAsync(List<Task> inFlight, int stillOut)
    {
        while (true)
        {
            Task? answered = inFlight.Find(task => task.IsCompleted);
            if (answered is null)
            {
                if (inFlight.Count <= stillOut)
                {
                    return;
                }

                answered = await Task.WhenAny(inFlight).ConfigureAwait(false);
            }

            inFlight.Remove(answered);
            await answered.ConfigureAwait(false);
        }
    }

    // Asked once per provider, which is once per mount, and asked again only after an attempt
    // that failed. Every write waits for the same answer, and one that is cancelled stops
    // waiting without cancelling it for the others.
    private async Task<ChunkLimits> LimitsAsync(string what, CancellationToken cancellationToken)
    {
        Task<ChunkLimits> limits;

        lock (_limitsGate)
        {
            if (_limits is null || _limits.IsFaulted || _limits.IsCanceled)
            {
                _limits = FetchLimitsAsync();
            }

            limits = _limits;
        }

        try
        {
            return await limits.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Failed(what, exception);
        }
        catch (FormatException exception)
        {
            throw new ProviderException(
                ProviderError.Protocol,
                $"{what} needed the limits of a chunked upload, and the server did not answer in an OCS envelope.",
                exception);
        }
    }

    // The answer read the way the web interface reads it: a size of zero or less is no chunks
    // at all, and a server that states nothing gets the size and the count the web interface
    // uses then. A size outside what the protocol allows is brought inside it. Not the
    // caller's token, because the answer is shared by every write that follows.
    private async Task<ChunkLimits> FetchLimitsAsync()
    {
        OcsChunkedUpload? stated = await _ocs.GetChunkedUploadAsync(CancellationToken.None).ConfigureAwait(false);

        long size = stated?.MaxSize switch
        {
            null => UnstatedChunkSize,
            <= 0 => 0,
            long max => Math.Clamp(max, SmallestChunkSize, LargestChunkSize),
        };

        // At least one, whatever is stated, or nothing would go out.
        int inFlight = Math.Max(stated?.MaxParallelCount ?? UnstatedChunksInFlight, 1);

        return new ChunkLimits(size, inFlight);
    }

    private static long ChunkSize(long length, ChunkLimits limits, int sentAhead)
    {
        // Only ten thousand chunks are allowed, so past a certain size the pieces have to
        // grow rather than multiply. Chunks sent ahead of the rest have used up names already.
        long names = MaximumChunks - sentAhead;
        long needed = (length + names - 1) / names;
        long size = Math.Max(limits.Size, needed);

        return size <= LargestChunkSize
            ? size
            : throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A file this size would need chunks over {LargestChunkSize} bytes, which is more than the protocol allows.");
    }

    // A directory of its own for every upload, below the user's upload area.
    private Uri UploadFolder() => new(_uploads, $"windav-{Guid.NewGuid()}/");

    // The chunks are assembled in the order of their names, and the names are read as text,
    // so they are padded to the width of the largest one.
    private static string ChunkName(int number) => number.ToString("D5", CultureInfo.InvariantCulture);

    private async Task DiscardAsync(Uri folder)
    {
        try
        {
            // Not the caller's token: the tidy-up matters most when the upload was
            // cancelled, and a cancelled token would skip it.
            await Client.DeleteAsync(folder, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The server expires an upload directory after a day of silence, so a failed
            // tidy-up costs space for a while. Letting it replace the failure that caused
            // it would cost the reason the upload went wrong.
        }
    }

    // An upload begun before the length of the file is known. The pieces go out as chunks
    // numbered from one, as many at once as the server allows; the rest goes the way the
    // chunked path sends a file, numbered on from there, and the assembly is the one every
    // chunked upload ends with.
    private sealed class ChunkedUpload(NextcloudProvider provider, string path) : IUpload
    {
        private readonly List<Task> _inFlight = [];

        private readonly Lock _sizing = new();

        // Made with the first piece, and forgotten once it has been taken away.
        private Uri? _folder;

        // What the pieces are measured by, kept from the first question on, so that every
        // piece of the upload is held to the same.
        private ChunkLimits? _limits;

        // How long the next piece is to be. It starts at the smallest a chunk may be, so that a
        // slow line does not have to send a large one first, and then follows how long the
        // chunks take, up to what the server allows. Set as the chunks are answered.
        private long _pieceSize = SmallestChunkSize;

        private int _pieces;

        // The bytes of all pieces, which the length of the whole file is counted on from.
        private long _piecesLength;

        private bool _finished;

        // Set when a piece failed, after which the directory has a gap in it.
        private bool _broken;

        // Set once the assembling MOVE has gone out. From then on the directory is the
        // server's, for the reason AssembleAsync gives.
        private bool _assembling;

        private Uri Target => DavPath.ToUri(provider.BaseUri, path);

        private string What => $"Writing {DavPath.Normalise(path)}";

        public async Task<long> GetPieceSizeAsync(CancellationToken cancellationToken)
        {
            ChunkLimits limits = await GetLimitsAsync(cancellationToken).ConfigureAwait(false);

            // The pieces may use only half of the names, and on a slow line they stay at the
            // smallest size, which would use those names up after 24 GiB. Once a quarter of the
            // names is left, the smallest piece doubles with every halving of what is left, so
            // that the pieces go on ahead of a larger file rather than leave it to the close.
            long left = Math.Max(1, (MaximumChunks / 2) - _pieces);
            long smallest = Math.Min(Math.Max(SmallestChunkSize, SmallestChunkSize * (MaximumChunks / 4) / left), limits.Size);

            lock (_sizing)
            {
                return limits.Size == 0 ? 0 : Math.Max(_pieceSize, smallest);
            }
        }

        public async Task<int> GetPiecesAtOnceAsync(CancellationToken cancellationToken) =>
            (await GetLimitsAsync(cancellationToken).ConfigureAwait(false)).InFlight;

        public async Task<bool> SendAsync(Stream piece, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(piece);

            // Handed on to the chunk that carries it, which disposes of it once the server has
            // answered. Until then it is disposed of here, whatever stops it.
            bool handedOn = false;

            try
            {
                if (_finished || _broken)
                {
                    throw new InvalidOperationException("The upload has been finished, or a piece of it has failed.");
                }

                ChunkLimits limits = await GetLimitsAsync(cancellationToken).ConfigureAwait(false);

                // None at all where the server wants no chunks, and half of the names at most,
                // so that the rest of a file of any size still fits into the other half.
                if (limits.Size == 0 || _pieces >= MaximumChunks / 2)
                {
                    return false;
                }

                long length = piece.CanSeek ? piece.Length - piece.Position : -1;
                if (length < SmallestChunkSize || length > limits.Size)
                {
                    throw new ArgumentException(
                        $"A piece has to be between {SmallestChunkSize} and {limits.Size} bytes long.",
                        nameof(piece));
                }

                KeyValuePair<string, string>[] headers = [new(DestinationHeader, Target.AbsoluteUri)];
                Uri folder;

                try
                {
                    folder = _folder ?? await OpenFolderAsync(headers, cancellationToken).ConfigureAwait(false);

                    await SettleAsync(_inFlight, limits.InFlight - 1).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    _broken = true;

                    throw Failed(What, exception);
                }
                catch
                {
                    _broken = true;

                    throw;
                }

                _pieces++;
                _piecesLength += length;

                Uri chunk = new(folder, ChunkName(_pieces));
                handedOn = true;
                _inFlight.Add(SendTimedAsync(chunk, piece, length, limits, headers));

                return true;
            }
            finally
            {
                if (!handedOn)
                {
                    await piece.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public async Task<string?> FinishAsync(
            Stream rest,
            string? ifMatch,
            EntryTimes times,
            bool mustBeNew,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rest);

            if (!rest.CanSeek)
            {
                throw new ArgumentException("The rest of the file has to be able to say how long it is.", nameof(rest));
            }

            if (_finished)
            {
                throw new InvalidOperationException("The upload has been finished already.");
            }

            _finished = true;

            // Nothing went ahead, and a file that is all rest is written the way any file is.
            if (_pieces == 0)
            {
                await AbandonAsync().ConfigureAwait(false);

                return await provider.WriteAsync(path, rest, ifMatch, times, mustBeNew, cancellationToken).ConfigureAwait(false);
            }

            if (_broken || _folder is not Uri folder || _limits is not ChunkLimits limits)
            {
                await AbandonAsync().ConfigureAwait(false);

                throw new InvalidOperationException("Part of the upload is missing, so it cannot be finished.");
            }

            long tail = rest.Length - rest.Position;
            long length = _piecesLength + tail;

            KeyValuePair<string, string>[] headers =
            [
                new(DestinationHeader, Target.AbsoluteUri),
                new(TotalLengthHeader, length.ToString(CultureInfo.InvariantCulture)),
            ];

            try
            {
                await SettleAsync(_inFlight, 0).ConfigureAwait(false);
                await provider
                    .SendChunksAsync(
                        rest,
                        folder,
                        tail,
                        ChunkSize(tail, limits, _pieces),
                        limits.InFlight,
                        headers,
                        _pieces + 1,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                await AbandonAsync().ConfigureAwait(false);

                throw Failed(What, exception, Occupied(exception, mustBeNew));
            }
            catch
            {
                // As on the chunked path: whatever it was, the half-written upload directory
                // is ours to take away.
                await AbandonAsync().ConfigureAwait(false);

                throw;
            }

            _assembling = true;

            return await provider.AssembleAsync(path, folder, length, ifMatch, times, mustBeNew, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_assembling)
            {
                await AbandonAsync().ConfigureAwait(false);
            }
        }

        private async Task<ChunkLimits> GetLimitsAsync(CancellationToken cancellationToken)
        {
            ChunkLimits limits = _limits ?? await provider.LimitsAsync(What, cancellationToken).ConfigureAwait(false);
            _limits = limits;

            return limits;
        }

        // Not the caller's token: what is out is waited for rather than called back, so that
        // the directory is only taken away once nothing is on its way into it. Timed from when
        // the chunk goes, not from when it began to wait for its turn.
        private async Task SendTimedAsync(
            Uri chunk,
            Stream piece,
            long length,
            ChunkLimits limits,
            KeyValuePair<string, string>[] headers)
        {
            long started = Stopwatch.GetTimestamp();

            await provider.SendChunkAsync(chunk, piece, headers, CancellationToken.None).ConfigureAwait(false);

            Took(length, Stopwatch.GetElapsedTime(started), limits);
        }

        // Sizes the next pieces the way the desktop client does: the size that would have
        // taken the time meant, averaged with the size so far, so that a chunk slowed or sped
        // by something else moves it only halfway.
        private void Took(long length, TimeSpan elapsed, ChunkLimits limits)
        {
            long milliseconds = Math.Max(1L, (long)elapsed.TotalMilliseconds);
            long fitting = length * (long)s_chunkDuration.TotalMilliseconds / milliseconds;

            lock (_sizing)
            {
                _pieceSize = Math.Clamp((_pieceSize / 2) + (fitting / 2), SmallestChunkSize, limits.Size);
            }
        }

        // The directory the chunks go into. It is noted before it is asked for, so that one
        // the server made before its answer went missing is still taken away.
        private async Task<Uri> OpenFolderAsync(KeyValuePair<string, string>[] headers, CancellationToken cancellationToken)
        {
            Uri folder = provider.UploadFolder();
            _folder = folder;

            await provider.Client.MkColAsync(folder, headers, cancellationToken).ConfigureAwait(false);

            return folder;
        }

        // Waits for what is still on its way into the directory, then takes the directory
        // away, so that nothing arrives in it afterwards.
        private async Task AbandonAsync()
        {
            await Task.WhenAll(_inFlight).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _inFlight.Clear();

            if (_folder is Uri folder)
            {
                _folder = null;

                await provider.DiscardAsync(folder).ConfigureAwait(false);
            }
        }
    }

    // What the server allows a chunked upload: the size of one chunk, zero where it wants no
    // chunks, and how many chunks of one upload may be out at once.
    private readonly record struct ChunkLimits(long Size, int InFlight);
}
