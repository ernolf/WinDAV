// SPDX-FileCopyrightText: 2026 ernolf
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Xml.Linq;
using Xunit;

namespace WinDav.Dav.Tests;

public sealed class MultiStatusParserTests
{
    private static readonly XNamespace s_ownCloud = "http://owncloud.org/ns";

    // A PROPFIND listing in the shape Nextcloud actually returns: a collection whose
    // getcontentlength is absent, and a file that has one.
    private const string Listing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/></d:resourcetype>
                <oc:fileid>12345</oc:fileid>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
            <d:propstat>
              <d:prop>
                <d:getcontentlength/>
              </d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/a%20note.txt</d:href>
            <d:propstat>
              <d:prop>
                <d:getcontentlength>17</d:getcontentlength>
                <d:resourcetype/>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public void ParseReturnsOneEntryPerResponseInDocumentOrder()
    {
        IReadOnlyList<DavResponse> responses = MultiStatusParser.Parse(XDocument.Parse(Listing));

        Assert.Equal(2, responses.Count);
        Assert.Equal("/remote.php/dav/files/ernolf/", responses[0].Href);
    }

    [Fact]
    public void ParseLeavesTheHrefEncodedAsTheServerWroteIt()
    {
        IReadOnlyList<DavResponse> responses = MultiStatusParser.Parse(XDocument.Parse(Listing));

        Assert.Equal("/remote.php/dav/files/ernolf/a%20note.txt", responses[1].Href);
    }

    [Fact]
    public void ParseKeepsPropertiesOutsideTheDavNamespace()
    {
        IReadOnlyList<DavResponse> responses = MultiStatusParser.Parse(XDocument.Parse(Listing));

        DavPropertyStatus found = responses[0].PropertyStatuses.Single(p => p.StatusCode == 200);

        Assert.Equal("12345", found.Properties[s_ownCloud + "fileid"].Value);
    }

    [Fact]
    public void ParseSeparatesPropertiesByTheirStatus()
    {
        IReadOnlyList<DavResponse> responses = MultiStatusParser.Parse(XDocument.Parse(Listing));

        DavPropertyStatus missing = responses[0].PropertyStatuses.Single(p => p.StatusCode == 404);

        Assert.Contains(DavNames.Namespace + "getcontentlength", missing.Properties.Keys);
        Assert.Null(responses[0].StatusCode);
    }

    [Fact]
    public void ParseRejectsABodyThatIsNotAMultiStatus()
    {
        XDocument document = XDocument.Parse("""<d:error xmlns:d="DAV:" />""");

        Assert.Throws<FormatException>(() => MultiStatusParser.Parse(document));
    }

    [Fact]
    public async Task ParseAsyncReadsTheSameListingFromAStream()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Listing));

        IReadOnlyList<DavResponse> responses = await MultiStatusParser.ParseAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(2, responses.Count);
    }

    [Fact]
    public async Task ParseAsyncRefusesADocumentTypeDeclaration()
    {
        // An entity declaration in a response body is an attack, not a listing.
        string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE multistatus [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]>
            <d:multistatus xmlns:d="DAV:" />
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(hostile));

        await Assert.ThrowsAsync<System.Xml.XmlException>(
            () => MultiStatusParser.ParseAsync(stream, TestContext.Current.CancellationToken));
    }
}
