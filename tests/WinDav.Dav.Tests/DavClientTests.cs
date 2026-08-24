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

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Depth = request.Headers.TryGetValues("Depth", out IEnumerable<string>? values) ? values.Single() : null;
            Range = request.Headers.Range?.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return _response;
        }

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
