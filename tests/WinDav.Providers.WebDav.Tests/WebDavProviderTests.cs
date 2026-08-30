// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using WinDav.Abstractions;
using WinDav.Dav;
using Xunit;

namespace WinDav.Providers.WebDav.Tests;

public sealed class WebDavProviderTests
{
    private static readonly Uri s_base = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    private const string Bytes = "0123456789";

    private const string Listing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/</d:href>
            <d:propstat>
              <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/a%20note.txt</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength>19</d:getcontentlength>
                <d:getcontenttype>text/plain</d:getcontenttype>
                <d:getetag>"abc123"</d:getetag>
                <d:getlastmodified>Mon, 24 Aug 2026 10:11:12 GMT</d:getlastmodified>
                <d:creationdate>2026-08-24T10:10:00Z</d:creationdate>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/docs/</d:href>
            <d:propstat>
              <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // What RFC 4331 adds, on the collection the two properties belong to.
    private const string Quota = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/</d:href>
            <d:propstat>
              <d:prop>
                <d:quota-used-bytes>3000</d:quota-used-bytes>
                <d:quota-available-bytes>7000</d:quota-available-bytes>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task ListAsyncLeavesOutTheCollectionItself()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        IReadOnlyList<RemoteEntry> entries = await Provider(httpClient)
            .ListAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(["/a note.txt", "/docs"], entries.Select(entry => entry.Path));
    }

    [Fact]
    public async Task ListAsyncReadsWhatTheServerSaidAboutAnEntry()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        IReadOnlyList<RemoteEntry> entries = await Provider(httpClient)
            .ListAsync("/", TestContext.Current.CancellationToken);

        RemoteEntry note = entries[0];

        Assert.Equal("a note.txt", note.Name);
        Assert.False(note.IsDirectory);
        Assert.Equal(19, note.Length);
        Assert.Equal("text/plain", note.ContentType);
        Assert.Equal("\"abc123\"", note.ETag);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.Zero), note.LastModified);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 10, 0, TimeSpan.Zero), note.Created);
        Assert.True(entries[1].IsDirectory);
    }

    [Fact]
    public async Task AnEntryOfAPlainServerHasNoIdentifierAndNoPermissions()
    {
        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(Listing)));

        IReadOnlyList<RemoteEntry> entries = await Provider(httpClient)
            .ListAsync("/", TestContext.Current.CancellationToken);

        // RFC 4918 has no property for either, and this provider invents nothing. Null is
        // the seam's way of saying so; see EntryPermissions on what it is not.
        Assert.Null(entries[0].Id);
        Assert.Null(entries[0].Permissions);
    }

    [Fact]
    public async Task APropFindOfAPlainServerAsksForEveryProperty()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).ListAsync("/", TestContext.Current.CancellationToken);

        // A provider that names no properties of its own asks for all of them, which is what
        // a server without a vendor namespace has anyway.
        XElement propfind = XDocument.Parse(handler.Body!).Root!;

        Assert.Equal(DavNames.PropFind, propfind.Name);
        Assert.NotNull(propfind.Element(DavNames.AllProp));
    }

    [Fact]
    public async Task ListAsyncAsksWithDepthOne()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).ListAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("PROPFIND", handler.Method);
        Assert.Equal("1", handler.Depth);
        Assert.Equal(s_base.AbsoluteUri, handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task APathIsEscapedOnTheWayOut()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).ListAsync("/holiday 2026/#1", TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://cloud.example.com/remote.php/dav/files/ernolf/holiday%202026/%231",
            handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ATrailingSlashOnAPathChangesNothing()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).ListAsync("/docs/", TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://cloud.example.com/remote.php/dav/files/ernolf/docs",
            handler.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("/docs/../../etc")]
    [InlineData("/docs//deep")]
    public async Task APathThatIsNotOfTheAgreedFormIsRefused(string path)
    {
        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(Listing)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(httpClient).ListAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnHrefOutsideTheBaseIsAProtocolFailure()
    {
        const string StrayHref = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/somebody-else/secret.txt</d:href>
                <d:propstat>
                  <d:prop><d:resourcetype/></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(StrayHref)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).ListAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Protocol, exception.Error);
    }

    [Fact]
    public async Task GetAsyncAsksWithDepthZero()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        RemoteEntry entry = await Provider(httpClient).GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("0", handler.Depth);
        Assert.Equal("/", entry.Path);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public async Task GetSpaceAsyncReadsBothFiguresAndAsksForThemByName()
    {
        RecordingHandler handler = new(MultiStatus(Quota));
        using HttpClient httpClient = new(handler);

        StorageSpace space = await Provider(httpClient).GetSpaceAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(3000L, space.Used);
        Assert.Equal(7000L, space.Available);
        Assert.Equal("0", handler.Depth);

        // Named rather than left to allprop: both are worked out on request, and this is the
        // one question that wants them and nothing else.
        XElement prop = XDocument.Parse(handler.Body!).Root!.Element(DavNames.Prop)!;

        Assert.NotNull(prop.Element(DavNames.QuotaUsedBytes));
        Assert.NotNull(prop.Element(DavNames.QuotaAvailableBytes));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-2")]
    [InlineData("-3")]
    [InlineData("plenty")]
    public async Task AnAmountThatIsNoAmountIsReadAsSilence(string written)
    {
        // Nextcloud answers the three negatives for a quota it has not worked out, does not
        // know, or does not impose. None of them is a number of bytes.
        string body = Quota.Replace("7000", written, StringComparison.Ordinal);
        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(body)));

        StorageSpace space = await Provider(httpClient).GetSpaceAsync("/", TestContext.Current.CancellationToken);

        Assert.Null(space.Available);
        Assert.Equal(3000L, space.Used);
    }

    [Fact]
    public async Task AServerThatKeepsNoSuchFigureIsNotAFailure()
    {
        const string NoQuota = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/ernolf/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:quota-used-bytes/>
                    <d:quota-available-bytes/>
                  </d:prop>
                  <d:status>HTTP/1.1 404 Not Found</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(NoQuota)));

        StorageSpace space = await Provider(httpClient).GetSpaceAsync("/", TestContext.Current.CancellationToken);

        Assert.Null(space.Used);
        Assert.Null(space.Available);
    }

    [Fact]
    public async Task OpenReadAsyncAsksForTheRangeItWasGiven()
    {
        RecordingHandler handler = new(Body(HttpStatusCode.PartialContent, "3456"));
        using HttpClient httpClient = new(handler);

        using Stream stream = await Provider(httpClient)
            .OpenReadAsync("/a note.txt", 3, 4, TestContext.Current.CancellationToken);

        Assert.Equal("bytes=3-6", handler.Range);
        Assert.Equal("3456", await Read(stream));
    }

    [Fact]
    public async Task OpenReadAsyncAsksForAnOpenRangeWithoutACount()
    {
        RecordingHandler handler = new(Body(HttpStatusCode.PartialContent, "3456789"));
        using HttpClient httpClient = new(handler);

        using Stream stream = await Provider(httpClient)
            .OpenReadAsync("/a note.txt", 3, count: null, TestContext.Current.CancellationToken);

        Assert.Equal("bytes=3-", handler.Range);
        Assert.Equal("3456789", await Read(stream));
    }

    [Fact]
    public async Task OpenReadAsyncSendsNoRangeForTheWholeResource()
    {
        RecordingHandler handler = new(Body(HttpStatusCode.OK, Bytes));
        using HttpClient httpClient = new(handler);

        using Stream stream = await Provider(httpClient)
            .OpenReadAsync("/a note.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(handler.Range);
        Assert.Equal(Bytes, await Read(stream));
    }

    [Fact]
    public async Task OpenReadAsyncSkipsForwardWhenTheServerIgnoredTheRange()
    {
        using HttpClient httpClient = new(new RecordingHandler(Body(HttpStatusCode.OK, Bytes)));

        using Stream stream = await Provider(httpClient)
            .OpenReadAsync("/a note.txt", 3, 4, TestContext.Current.CancellationToken);

        Assert.Equal("3456", await Read(stream));
    }

    [Fact]
    public async Task OpenReadAsyncEndsWhereTheRangeWouldHaveEnded()
    {
        using HttpClient httpClient = new(new RecordingHandler(Body(HttpStatusCode.OK, Bytes)));

        using Stream stream = await Provider(httpClient)
            .OpenReadAsync("/a note.txt", 0, 4, TestContext.Current.CancellationToken);

        Assert.Equal("0123", await Read(stream));
    }

    [Fact]
    public async Task WriteAsyncSendsTheEntityTagAndHandsBackTheNewOne()
    {
        HttpResponseMessage response = new(HttpStatusCode.NoContent)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"def456\"") },
        };

        RecordingHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Bytes));

        string? etag = await Provider(httpClient)
            .WriteAsync("/a note.txt", content, "\"abc123\"", TestContext.Current.CancellationToken);

        Assert.Equal("PUT", handler.Method);
        Assert.Equal("\"abc123\"", handler.IfMatch);
        Assert.Equal("\"def456\"", etag);
        Assert.Equal(Bytes, handler.Body);
    }

    [Fact]
    public async Task CreateDirectoryAsyncNamesTheCollectionWithATrailingSlash()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).CreateDirectoryAsync("/docs", TestContext.Current.CancellationToken);

        Assert.Equal("MKCOL", handler.Method);
        Assert.Equal(
            "https://cloud.example.com/remote.php/dav/files/ernolf/docs/",
            handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task MoveAsyncSendsAnAbsoluteDestination()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient)
            .MoveAsync("/a note.txt", "/docs/renamed.txt", overwrite: true, TestContext.Current.CancellationToken);

        Assert.Equal("MOVE", handler.Method);
        Assert.Equal(
            "https://cloud.example.com/remote.php/dav/files/ernolf/docs/renamed.txt",
            handler.Destination);
        Assert.Equal("T", handler.Overwrite);
        Assert.Null(handler.Depth);
    }

    [Fact]
    public async Task CopyAsyncTakesTheWholeSubtree()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient)
            .CopyAsync("/docs", "/backup", overwrite: false, TestContext.Current.CancellationToken);

        Assert.Equal("COPY", handler.Method);
        Assert.Equal("infinity", handler.Depth);
        Assert.Equal("F", handler.Overwrite);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ProviderError.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderError.PermissionDenied)]
    [InlineData(HttpStatusCode.Forbidden, ProviderError.PermissionDenied)]
    [InlineData(HttpStatusCode.Locked, ProviderError.Busy)]
    [InlineData(HttpStatusCode.Conflict, ProviderError.Conflict)]
    [InlineData(HttpStatusCode.InsufficientStorage, ProviderError.InsufficientStorage)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderError.Busy)]
    [InlineData(HttpStatusCode.NotImplemented, ProviderError.Unknown)]
    public async Task AStatusBecomesTheCaseItStandsFor(HttpStatusCode status, ProviderError expected)
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(status)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).DeleteAsync("/a note.txt", TestContext.Current.CancellationToken));

        Assert.Equal(expected, exception.Error);
    }

    [Fact]
    public async Task AFailedPreconditionOnAWriteIsALostUpdate()
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)));
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Bytes));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/a note.txt", content, "\"abc123\"", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.PreconditionFailed, exception.Error);
    }

    [Fact]
    public async Task AFailedPreconditionOnAMoveIsATakenDestination()
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).MoveAsync("/a note.txt", "/docs/taken.txt", overwrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.AlreadyExists, exception.Error);
    }

    [Fact]
    public async Task AMethodNotAllowedOnAMkColIsATakenPath()
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).CreateDirectoryAsync("/docs", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.AlreadyExists, exception.Error);
    }

    [Fact]
    public async Task AServerThatNeverAnsweredIsUnreachable()
    {
        using HttpClient httpClient = new(new FailingHandler());

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).ListAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Unreachable, exception.Error);
    }

    [Fact]
    public async Task ABodyThatIsNoMultistatusIsAProtocolFailure()
    {
        using HttpClient httpClient = new(new RecordingHandler(MultiStatus("<html>Sorry.</html>")));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).ListAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Protocol, exception.Error);
    }

    private static WebDavProvider Provider(HttpClient httpClient) => new(new DavClient(httpClient), s_base);

    private static async Task<string> Read(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static HttpResponseMessage Body(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage MultiStatus(string body) =>
        new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };

    // The request is disposed by the client before the assertions run, so everything worth
    // looking at is copied out while it is still alive.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public string? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Depth { get; private set; }

        public string? Range { get; private set; }

        public string? Destination { get; private set; }

        public string? Overwrite { get; private set; }

        public string? IfMatch { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            RequestUri = request.RequestUri;
            Depth = Header(request, "Depth");
            Range = request.Headers.Range?.ToString();
            Destination = Header(request, "Destination");
            Overwrite = Header(request, "Overwrite");
            IfMatch = request.Headers.IfMatch.Count == 0 ? null : request.Headers.IfMatch.ToString();

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return _response;
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // A request that never reaches a server: no status, only the failure itself.
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The name could not be resolved.");
    }
}
