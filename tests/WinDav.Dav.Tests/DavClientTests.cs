// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace WinDav.Dav.Tests;

public sealed class DavClientTests
{
    private static readonly XNamespace s_ownCloud = "http://owncloud.org/ns";

    private static readonly Uri s_folder = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    private static readonly Uri s_file = new("https://cloud.example.com/remote.php/dav/files/ernolf/a%20note.txt");

    private static readonly Uri s_otherFile = new("https://cloud.example.com/remote.php/dav/files/ernolf/renamed.txt");

    private static readonly Uri s_otherFolder = new("https://cloud.example.com/remote.php/dav/files/ernolf/copy/");

    // Plain ASCII, so its length in characters is its length in bytes.
    private const string Note = "the quick brown fox";

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
                <d:getcontentlength>17</d:getcontentlength>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task PropFindAsyncSendsAPropfindWithAnXmlBody()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient).PropFindAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("PROPFIND", handler.Method);
        Assert.Equal("application/xml; charset=utf-8", handler.ContentType);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", handler.Body, StringComparison.Ordinal);
    }

    // Asked for, never relied on, and written on the request itself because
    // HttpClient.DefaultRequestVersion reaches only the requests HttpClient builds for
    // itself. RequestVersionOrLower is what keeps it a wish: a server that does not offer
    // HTTP/2 answers over 1.1 and nothing here notices the difference (#26).
    [Fact]
    public async Task EveryRequestAsksForHttpTwoAndTakesWhateverItGets()
    {
        RecordingHandler asked = new(MultiStatus(Listing));
        using HttpClient forPropFind = new(asked);

        await new DavClient(forPropFind).PropFindAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpVersion.Version20, asked.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, asked.VersionPolicy);

        RecordingHandler fetched = new(Body(HttpStatusCode.PartialContent, "brown"));
        using HttpClient forGet = new(fetched);

        using DavContent content = await new DavClient(forGet)
            .GetRangeAsync(s_file, 10, 5, TestContext.Current.CancellationToken);

        // The read path is the one that sends the most of them, so it is the one that would
        // notice a request going out over 1.1 by accident.
        Assert.Equal(HttpVersion.Version20, fetched.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, fetched.VersionPolicy);
    }

    [Theory]
    [InlineData(DavDepth.Zero, "0")]
    [InlineData(DavDepth.One, "1")]
    [InlineData(DavDepth.Infinity, "infinity")]
    public async Task PropFindAsyncWritesTheDepthHeader(DavDepth depth, string expected)
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient).PropFindAsync(s_folder, depth, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, handler.Depth);
    }

    [Fact]
    public async Task PropFindAsyncAsksForTheNamedPropertiesIncludingVendorOnes()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient)
            .PropFindAsync(
                s_folder,
                DavDepth.One,
                [DavNames.ResourceType, s_ownCloud + "fileid"],
                TestContext.Current.CancellationToken);

        XElement root = XDocument.Parse(handler.Body!).Root!;
        XName[] expected = [DavNames.ResourceType, s_ownCloud + "fileid"];

        Assert.Equal(DavNames.PropFind, root.Name);
        Assert.Equal(expected, root.Element(DavNames.Prop)!.Elements().Select(property => property.Name));
    }

    [Fact]
    public async Task PropFindAsyncAsksForAllPropertiesWhenNoneAreNamed()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient).PropFindAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken);

        XElement root = XDocument.Parse(handler.Body!).Root!;

        Assert.NotNull(root.Element(DavNames.AllProp));
        Assert.Null(root.Element(DavNames.Prop));
    }

    [Fact]
    public async Task PropFindAsyncReadsTheListingIntoResources()
    {
        RecordingHandler handler = new(MultiStatus(Listing));
        using HttpClient httpClient = new(handler);

        IReadOnlyList<DavResource> resources = await new DavClient(httpClient)
            .PropFindAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, resources.Count);
        Assert.True(resources[0].IsCollection);
        Assert.Null(resources[0].ContentLength);
        Assert.False(resources[1].IsCollection);
        Assert.Equal(17L, resources[1].ContentLength);
    }

    [Fact]
    public async Task PropFindAsyncRefusesAnAnswerThatIsNotMultiStatus()
    {
        // 200 with a body is the trap: the server answered, but not the question that was
        // asked, and parsing it as a listing would be worse than failing.
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>", Encoding.UTF8, "text/html"),
        });

        using HttpClient httpClient = new(handler);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new DavClient(httpClient).PropFindAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
    }

    [Fact]
    public async Task GetAsyncFetchesTheWholeResourceWithoutARange()
    {
        RecordingHandler handler = new(Body(HttpStatusCode.OK, Note));
        using HttpClient httpClient = new(handler);

        using DavContent content = await new DavClient(httpClient)
            .GetAsync(s_file, TestContext.Current.CancellationToken);

        using StreamReader reader = new(content.Content);
        string body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal("GET", handler.Method);
        Assert.Null(handler.Range);
        Assert.False(content.IsPartial);
        Assert.Equal(Note, body);
    }

    [Fact]
    public async Task GetRangeAsyncNamesFirstAndLastByte()
    {
        RecordingHandler handler = new(Body(HttpStatusCode.PartialContent, "brown"));
        using HttpClient httpClient = new(handler);

        using DavContent content = await new DavClient(httpClient)
            .GetRangeAsync(s_file, 10, 5, TestContext.Current.CancellationToken);

        Assert.Equal("bytes=10-14", handler.Range);
        Assert.True(content.IsPartial);
    }

    [Fact]
    public async Task GetRangeAsyncAcceptsAServerThatIgnoresTheRange()
    {
        // Answering 200 with the whole resource is allowed. The caller has to notice and
        // skip to the offset itself, which it can only do if it is told.
        RecordingHandler handler = new(Body(HttpStatusCode.OK, Note));
        using HttpClient httpClient = new(handler);

        using DavContent content = await new DavClient(httpClient)
            .GetRangeAsync(s_file, 10, 5, TestContext.Current.CancellationToken);

        Assert.False(content.IsPartial);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    public async Task GetRangeAsyncRefusesARangeThatCannotExist(long offset, long count)
    {
        using HttpClient httpClient = new(new RecordingHandler(Body(HttpStatusCode.OK, Note)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DavClient(httpClient).GetRangeAsync(s_file, offset, count, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsyncKeepsTheHeadersThatDescribeTheBody()
    {
        HttpResponseMessage response = Body(HttpStatusCode.OK, Note);
        response.Headers.ETag = new EntityTagHeaderValue("\"6a9f\"", isWeak: true);
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 11, 9, 31, 0, TimeSpan.Zero);

        using HttpClient httpClient = new(new RecordingHandler(response));

        using DavContent content = await new DavClient(httpClient)
            .GetAsync(s_file, TestContext.Current.CancellationToken);

        Assert.Equal("W/\"6a9f\"", content.ETag);
        Assert.Equal("text/plain; charset=utf-8", content.ContentType);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 9, 31, 0, TimeSpan.Zero), content.LastModified);
        Assert.Equal((long)Note.Length, content.ContentLength);
    }

    [Fact]
    public async Task GetAsyncRefusesAnAnswerThatIsNeitherOkNorPartial()
    {
        using HttpClient httpClient = new(new RecordingHandler(Body(HttpStatusCode.NotFound, "gone")));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new DavClient(httpClient).GetAsync(s_file, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task PutAsyncWritesTheStreamAndReturnsTheNewETag()
    {
        HttpResponseMessage response = new(HttpStatusCode.Created);
        response.Headers.ETag = new EntityTagHeaderValue("\"6a9f\"");

        RecordingHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Note));

        string? etag = await new DavClient(httpClient)
            .PutAsync(s_file, content, "text/plain", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("PUT", handler.Method);
        Assert.Equal("text/plain", handler.ContentType);
        Assert.Equal(Note, handler.Body);
        Assert.Null(handler.IfMatch);
        Assert.Equal("\"6a9f\"", etag);
    }

    [Fact]
    public async Task PutAsyncLeavesTheStreamOpen()
    {
        // The stream is the caller's. StreamContent would have closed it with the request.
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Note));

        await new DavClient(httpClient).PutAsync(s_file, content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task PutAsyncSendsTheEntityTagItWasGiven()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Note));

        string? etag = await new DavClient(httpClient)
            .PutAsync(s_file, content, ifMatch: "\"6a9f\"", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("\"6a9f\"", handler.IfMatch);
        Assert.Null(etag);
    }

    [Fact]
    public async Task PutAsyncReportsAFailedPreconditionAsSuch()
    {
        // 412 is how a lost update announces itself: somebody else wrote first.
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)));
        using MemoryStream content = new(Encoding.UTF8.GetBytes(Note));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new DavClient(httpClient).PutAsync(
                s_file,
                content,
                ifMatch: "\"6a9f\"",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
    }

    [Fact]
    public async Task MkColAsyncCreatesTheCollection()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient).MkColAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("MKCOL", handler.Method);
    }

    [Fact]
    public async Task MkColAsyncRefusesAnAnswerThatIsNotCreated()
    {
        // 405 means something is already there. Reporting that as success would let a
        // caller believe it owns a directory that somebody else made.
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new DavClient(httpClient).MkColAsync(s_folder, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, exception.StatusCode);
    }

    [Fact]
    public async Task DeleteAsyncSendsDelete()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient).DeleteAsync(s_file, TestContext.Current.CancellationToken);

        Assert.Equal("DELETE", handler.Method);
    }

    [Fact]
    public async Task DeleteAsyncTreatsMultiStatusAsAFailure()
    {
        // For a delete, 207 is the server saying that part of the tree is still there.
        using HttpClient httpClient = new(new RecordingHandler(MultiStatus(Listing)));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new DavClient(httpClient).DeleteAsync(s_folder, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.MultiStatus, exception.StatusCode);
    }

    [Theory]
    [InlineData(false, "F")]
    [InlineData(true, "T")]
    public async Task MoveAsyncNamesTheDestinationAndWhetherItMayBeReplaced(bool overwrite, string expected)
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient)
            .MoveAsync(s_file, s_otherFile, overwrite, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("MOVE", handler.Method);
        Assert.Equal(s_otherFile.AbsoluteUri, handler.Destination);
        Assert.Equal(expected, handler.Overwrite);

        // MOVE takes a tree whole; RFC 4918 section 9.9.2 knows no other depth for it.
        Assert.Null(handler.Depth);
    }

    [Theory]
    [InlineData(DavDepth.Infinity, "infinity")]
    [InlineData(DavDepth.Zero, "0")]
    public async Task CopyAsyncStatesHowDeepItCopies(DavDepth depth, string expected)
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Created));
        using HttpClient httpClient = new(handler);

        await new DavClient(httpClient)
            .CopyAsync(s_folder, s_otherFolder, overwrite: false, depth, TestContext.Current.CancellationToken);

        Assert.Equal("COPY", handler.Method);
        Assert.Equal(expected, handler.Depth);
    }

    [Fact]
    public async Task CopyAsyncRefusesADepthOfOne()
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DavClient(httpClient).CopyAsync(
                s_folder,
                s_otherFolder,
                overwrite: false,
                DavDepth.One,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoveAsyncRefusesARelativeDestination()
    {
        using HttpClient httpClient = new(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new DavClient(httpClient).MoveAsync(
                s_file,
                new Uri("a%20note.txt", UriKind.Relative),
                overwrite: false,
                cancellationToken: TestContext.Current.CancellationToken));
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

        public string? Depth { get; private set; }

        public string? Range { get; private set; }

        public string? Destination { get; private set; }

        public string? Overwrite { get; private set; }

        public string? IfMatch { get; private set; }

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        public Version? Version { get; private set; }

        public HttpVersionPolicy? VersionPolicy { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Depth = Header(request, "Depth");
            Range = request.Headers.Range?.ToString();
            Destination = Header(request, "Destination");
            Overwrite = Header(request, "Overwrite");
            IfMatch = request.Headers.IfMatch.Count == 0 ? null : request.Headers.IfMatch.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Version = request.Version;
            VersionPolicy = request.VersionPolicy;

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
}
