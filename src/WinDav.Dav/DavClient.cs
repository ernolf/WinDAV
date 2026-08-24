// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace WinDav.Dav;

/// <summary>
/// Sends the requests a WebDAV server understands over an <see cref="HttpClient"/> and
/// hands back what it answered, already read into the types the rest of the program uses.
/// </summary>
/// <remarks>
/// The client does not own the <see cref="HttpClient"/> it is handed. Base address,
/// authentication, timeouts and the lifetime of the handler belong to whoever built it.
/// </remarks>
public sealed class DavClient
{
    // Written out rather than produced by XDocument.Save, which would take its encoding
    // from the writer and could end up declaring an encoding the body is not sent in.
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

    // HttpMethod knows the methods of RFC 9110; these four are not among them.
    private static readonly HttpMethod s_propFind = new("PROPFIND");

    private static readonly HttpMethod s_mkCol = new("MKCOL");

    private static readonly HttpMethod s_copy = new("COPY");

    private static readonly HttpMethod s_move = new("MOVE");

    // 201 for a resource that did not exist, 204 for one that did, 200 for a server that
    // answers with a body.
    private static readonly HttpStatusCode[] s_putAccepts =
        [HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent];

    private static readonly HttpStatusCode[] s_mkColAccepts = [HttpStatusCode.Created];

    private static readonly HttpStatusCode[] s_deleteAccepts =
        [HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.NoContent];

    // 201 when the destination was free, 204 when it was overwritten.
    private static readonly HttpStatusCode[] s_relocationAccepts =
        [HttpStatusCode.Created, HttpStatusCode.NoContent];

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initialises a new instance of the <see cref="DavClient"/> class.
    /// </summary>
    /// <param name="httpClient">The client the requests are sent with.</param>
    public DavClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    /// <summary>
    /// Asks a server about a resource and, depending on <paramref name="depth"/>, about
    /// what lies below it.
    /// </summary>
    /// <param name="uri">The resource to ask about.</param>
    /// <param name="depth">How far the request reaches.</param>
    /// <param name="properties">
    /// The properties to ask for, vendor properties included. When this is
    /// <see langword="null"/> or empty, the request asks for all of them instead.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// One entry per resource the server described, in the order it wrote them. With
    /// <see cref="DavDepth.One"/> the collection itself is the first entry.
    /// </returns>
    /// <exception cref="HttpRequestException">The server did not answer with 207.</exception>
    /// <exception cref="FormatException">The body is not a well formed multistatus.</exception>
    public async Task<IReadOnlyList<DavResource>> PropFindAsync(
        Uri uri,
        DavDepth depth = DavDepth.One,
        IEnumerable<XName>? properties = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using HttpRequestMessage request = new(s_propFind, uri)
        {
            Content = new StringContent(BuildRequestBody(properties), Encoding.UTF8, "application/xml"),
        };

        request.Headers.Add("Depth", DepthHeader(depth));

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // A PROPFIND that worked answers 207 and nothing else. Anything in the 2xx range
        // means the server did not do what was asked, so it is as much of a failure as a
        // 404 and must not be parsed as a listing.
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            throw new HttpRequestException(
                $"PROPFIND {uri} expected 207 Multi-Status but the server answered {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DavResponse> responses = await MultiStatusParser
            .ParseAsync(body, cancellationToken)
            .ConfigureAwait(false);

        List<DavResource> resources = new(responses.Count);
        foreach (DavResponse item in responses)
        {
            resources.Add(DavResource.FromResponse(item));
        }

        return resources;
    }

    /// <summary>
    /// Fetches a resource whole.
    /// </summary>
    /// <param name="uri">The resource to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The body and the headers describing it. The caller disposes it.</returns>
    /// <exception cref="HttpRequestException">The server did not answer with 200 or 206.</exception>
    public Task<DavContent> GetAsync(Uri uri, CancellationToken cancellationToken = default) =>
        SendGetAsync(uri, range: null, cancellationToken);

    /// <summary>
    /// Fetches a part of a resource.
    /// </summary>
    /// <param name="uri">The resource to fetch.</param>
    /// <param name="offset">The first byte to fetch, counted from zero.</param>
    /// <param name="count">
    /// How many bytes to fetch, or <see langword="null"/> for everything from
    /// <paramref name="offset"/> on.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The body and the headers describing it. A server may answer with the whole resource
    /// instead of the requested range; see <see cref="DavContent.IsPartial"/>.
    /// </returns>
    /// <exception cref="HttpRequestException">The server did not answer with 200 or 206.</exception>
    public Task<DavContent> GetRangeAsync(
        Uri uri,
        long offset,
        long? count = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (count is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count.Value);
        }

        // A byte range names first and last byte, both inclusive, which is why the last one
        // is one below offset + count. Left open it asks for the rest of the resource.
        long? last = count is null ? null : offset + count.Value - 1;

        return SendGetAsync(uri, new RangeHeaderValue(offset, last), cancellationToken);
    }

    private async Task<DavContent> SendGetAsync(Uri uri, RangeHeaderValue? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Range = range;

        HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Serving a range is optional: RFC 9110 section 14.2 lets a server answer 200
            // with the whole resource instead. That is not an error, so both statuses pass
            // and the caller is told which one it got.
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                throw new HttpRequestException(
                    $"GET {uri} expected 200 OK or 206 Partial Content but the server answered {(int)response.StatusCode}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            return await DavContent.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The content owns the response once it exists. Until then this method does.
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a resource, creating it or replacing what is there.
    /// </summary>
    /// <param name="uri">The resource to write.</param>
    /// <param name="content">
    /// The bytes to write. The stream is read to its end but not disposed; it belongs to
    /// the caller.
    /// </param>
    /// <param name="contentType">The media type to declare, or <see langword="null"/>.</param>
    /// <param name="ifMatch">
    /// An entity tag the resource must still carry for the write to happen, in the form the
    /// server wrote it. Without one the write overwrites whatever is there.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The entity tag of the written resource when the server stated one, otherwise
    /// <see langword="null"/>. Servers are not obliged to answer with it.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// The server refused the write. A precondition that failed shows as
    /// <see cref="HttpStatusCode.PreconditionFailed"/>, which is how a lost update is told
    /// apart from an error.
    /// </exception>
    public async Task<string?> PutAsync(
        Uri uri,
        Stream content,
        string? contentType = null,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(content);

        StreamPayload payload = new(content);
        using HttpRequestMessage request = new(HttpMethod.Put, uri) { Content = payload };

        if (contentType is not null)
        {
            payload.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        using HttpResponseMessage response = await SendExpectingAsync(request, s_putAccepts, cancellationToken)
            .ConfigureAwait(false);

        return response.Headers.ETag?.ToString();
    }

    /// <summary>
    /// Creates a collection, that is a directory.
    /// </summary>
    /// <param name="uri">The collection to create, named with a trailing slash.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the collection exists.</returns>
    /// <exception cref="HttpRequestException">
    /// The server refused. <see cref="HttpStatusCode.MethodNotAllowed"/> means something is
    /// already there, <see cref="HttpStatusCode.Conflict"/> that the parent is missing.
    /// </exception>
    public async Task MkColAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using HttpRequestMessage request = new(s_mkCol, uri);

        using HttpResponseMessage response = await SendExpectingAsync(request, s_mkColAccepts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a resource, with everything below it when it is a collection.
    /// </summary>
    /// <param name="uri">The resource to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the resource is gone.</returns>
    /// <exception cref="HttpRequestException">
    /// The server refused, or answered 207, which for a delete means that part of the tree
    /// survived.
    /// </exception>
    public async Task DeleteAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using HttpRequestMessage request = new(HttpMethod.Delete, uri);

        // A delete that worked is 204. RFC 4918 section 9.6.1 lets a server answer 207 when
        // it could not delete everything, so here 207 is a failure, not a listing.
        using HttpResponseMessage response = await SendExpectingAsync(request, s_deleteAccepts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a resource, which is also how it is renamed.
    /// </summary>
    /// <param name="source">The resource to move.</param>
    /// <param name="destination">Where it goes, as an absolute URI.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the resource has moved.</returns>
    /// <exception cref="HttpRequestException">
    /// The server refused. <see cref="HttpStatusCode.PreconditionFailed"/> means the
    /// destination exists and <paramref name="overwrite"/> was <see langword="false"/>.
    /// </exception>
    public Task MoveAsync(
        Uri source,
        Uri destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        SendRelocationAsync(s_move, source, destination, overwrite, depth: null, cancellationToken);

    /// <summary>
    /// Copies a resource.
    /// </summary>
    /// <param name="source">The resource to copy.</param>
    /// <param name="destination">Where the copy goes, as an absolute URI.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <param name="depth">
    /// <see cref="DavDepth.Infinity"/> copies a collection with everything in it,
    /// <see cref="DavDepth.Zero"/> copies the collection itself and leaves it empty.
    /// <see cref="DavDepth.One"/> is not a depth COPY accepts.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the copy exists.</returns>
    /// <exception cref="HttpRequestException">The server refused.</exception>
    public Task CopyAsync(
        Uri source,
        Uri destination,
        bool overwrite = false,
        DavDepth depth = DavDepth.Infinity,
        CancellationToken cancellationToken = default)
    {
        if (depth == DavDepth.One)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                "COPY takes 0 or infinity; RFC 4918 section 9.8.3 has no meaning for 1.");
        }

        return SendRelocationAsync(s_copy, source, destination, overwrite, depth, cancellationToken);
    }

    private async Task SendRelocationAsync(
        HttpMethod method,
        Uri source,
        Uri destination,
        bool overwrite,
        DavDepth? depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        // RFC 4918 section 10.3 asks for an absolute URI, and a relative one would in any
        // case be read against the server's idea of the base, not ours.
        if (!destination.IsAbsoluteUri)
        {
            throw new ArgumentException("The destination has to be an absolute URI.", nameof(destination));
        }

        using HttpRequestMessage request = new(method, source);
        request.Headers.Add("Destination", destination.AbsoluteUri);
        request.Headers.Add("Overwrite", overwrite ? "T" : "F");

        if (depth is not null)
        {
            request.Headers.Add("Depth", DepthHeader(depth.Value));
        }

        using HttpResponseMessage response = await SendExpectingAsync(request, s_relocationAccepts, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendExpectingAsync(
        HttpRequestMessage request,
        HttpStatusCode[] expected,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (Array.IndexOf(expected, response.StatusCode) < 0)
        {
            string wanted = string.Join(" or ", expected.Select(status => ((int)status).ToString(CultureInfo.InvariantCulture)));
            HttpStatusCode received = response.StatusCode;
            response.Dispose();

            throw new HttpRequestException(
                $"{request.Method} {request.RequestUri} expected {wanted} but the server answered {(int)received}.",
                inner: null,
                statusCode: received);
        }

        return response;
    }

    private static string BuildRequestBody(IEnumerable<XName>? properties)
    {
        XElement[] requested = properties?.Select(name => new XElement(name)).ToArray() ?? [];

        // allprop is the wider question and the one to fall back on. It is not the better
        // one: a server answers it with the properties it considers live, which is why a
        // caller after vendor properties has to name them.
        XElement selection = requested.Length == 0
            ? new XElement(DavNames.AllProp)
            : new XElement(DavNames.Prop, requested);

        return XmlDeclaration + new XElement(DavNames.PropFind, selection).ToString(SaveOptions.DisableFormatting);
    }

    private static string DepthHeader(DavDepth depth) => depth switch
    {
        DavDepth.Zero => "0",
        DavDepth.One => "1",
        DavDepth.Infinity => "infinity",
        _ => throw new ArgumentOutOfRangeException(nameof(depth)),
    };
}
