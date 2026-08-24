// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Xml.Linq;
using Xunit;

namespace WinDav.Dav.Tests;

public sealed class DavResourceTests
{
    // A collection, and a file with the full set of properties a PROPFIND asks for. The
    // collection reports getcontentlength under 404, which is how servers say "a
    // directory has no size".
    private const string Listing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/></d:resourcetype>
                <d:getlastmodified>Tue, 11 Aug 2026 09:31:00 GMT</d:getlastmodified>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
            <d:propstat>
              <d:prop>
                <d:getcontentlength/>
                <d:getcontenttype/>
              </d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/a%20note.txt</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength>17</d:getcontentlength>
                <d:getlastmodified>Tue, 11 Aug 2026 09:31:00 GMT</d:getlastmodified>
                <d:getetag>&quot;5f2a1b3c4d5e6&quot;</d:getetag>
                <d:getcontenttype>text/plain; charset=utf-8</d:getcontenttype>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public void FromResponseTellsACollectionFromAFile()
    {
        Assert.True(Read(Listing, 0).IsCollection);
        Assert.False(Read(Listing, 1).IsCollection);
    }

    [Fact]
    public void FromResponseReadsTheContentLength() =>
        Assert.Equal(17L, Read(Listing, 1).ContentLength);

    [Fact]
    public void FromResponseIgnoresPropertiesReportedUnderAnotherStatus()
    {
        DavResource collection = Read(Listing, 0);

        // The element is in the body, but under 404 it stands for absence, not for zero.
        Assert.Null(collection.ContentLength);
        Assert.Null(collection.ContentType);
    }

    [Fact]
    public void FromResponseReadsTheLastModifiedTimeAsUtc()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 8, 11, 9, 31, 0, TimeSpan.Zero),
            Read(Listing, 1).LastModified);
    }

    [Fact]
    public void FromResponseKeepsTheQuotesOfTheETag() =>
        Assert.Equal("\"5f2a1b3c4d5e6\"", Read(Listing, 1).ETag);

    [Fact]
    public void FromResponseKeepsTheParametersOfTheContentType() =>
        Assert.Equal("text/plain; charset=utf-8", Read(Listing, 1).ContentType);

    [Fact]
    public void FromResponseReportsAValueItCannotReadAsAbsent()
    {
        string listing = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/ernolf/a%20note.txt</d:href>
                <d:propstat>
                  <d:prop>
                    <d:getcontentlength>seventeen</d:getcontentlength>
                    <d:getlastmodified>2026-08-11T09:31:00Z</d:getlastmodified>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        DavResource resource = Read(listing, 0);

        Assert.Null(resource.ContentLength);
        Assert.Null(resource.LastModified);
    }

    [Fact]
    public void FromResponseKeepsTheDeliveredPropertiesReachable()
    {
        DavResource resource = Read(Listing, 1);

        Assert.Equal("17", resource.Properties[DavNames.GetContentLength].Value);
        Assert.Equal("/remote.php/dav/files/ernolf/a%20note.txt", resource.Href);
    }

    private static DavResource Read(string listing, int index) =>
        DavResource.FromResponse(MultiStatusParser.Parse(XDocument.Parse(listing))[index]);
}
