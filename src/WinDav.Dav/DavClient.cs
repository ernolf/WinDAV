// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

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

    // HttpMethod knows the methods of RFC 9110; PROPFIND is not among them.
    private static readonly HttpMethod s_propFind = new("PROPFIND");

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
    /// <param name="count">How many bytes to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The body and the headers describing it. A server may answer with the whole resource
    /// instead of the requested range; see <see cref="DavContent.IsPartial"/>.
    /// </returns>
    /// <exception cref="HttpRequestException">The server did not answer with 200 or 206.</exception>
    public Task<DavContent> GetRangeAsync(
        Uri uri,
        long offset,
        long count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // A byte range names first and last byte, both inclusive, which is why the last one
        // is one below offset + count.
        return SendGetAsync(uri, new RangeHeaderValue(offset, offset + count - 1), cancellationToken);
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
