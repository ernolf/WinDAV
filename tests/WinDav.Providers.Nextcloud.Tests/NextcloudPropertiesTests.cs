// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Xml.Linq;
using WinDav.Abstractions;
using WinDav.Dav;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class NextcloudPropertiesTests
{
    private static readonly Uri s_base = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    private static readonly Uri s_uploads = new("https://cloud.example.com/remote.php/dav/uploads/ernolf/");

    // The form the server uses: the file's number, padded, with the identifier of the
    // instance behind it.
    private const string FileId = "00000042ocsi1v3ku2bm";

    [Fact]
    public async Task APropFindNamesThePropertiesInsteadOfAskingForAll()
    {
        ListingHandler handler = new(Listing(Vendor("RGDNVW")));
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).GetAsync("/a note.txt", TestContext.Current.CancellationToken);

        XDocument request = XDocument.Parse(handler.Body!);
        XName[] asked = [.. request.Descendants(DavNames.Prop).Elements().Select(element => element.Name)];

        // allprop would leave out exactly the two properties this provider exists for.
        Assert.Empty(request.Descendants(DavNames.AllProp));
        Assert.Contains(NextcloudNames.Id, asked);
        Assert.Contains(NextcloudNames.Permissions, asked);
        Assert.Contains(DavNames.CreationDate, asked);
    }

    [Fact]
    public async Task AnEntryCarriesWhatOnlyThisServerSays()
    {
        RemoteEntry entry = await EntryAsync(Listing(Vendor("RGDNVW")));

        Assert.Equal(FileId, entry.Id);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 10, 0, TimeSpan.Zero), entry.Created);
        Assert.Equal(
            EntryPermissions.Share
                | EntryPermissions.Read
                | EntryPermissions.Delete
                | EntryPermissions.Rename
                | EntryPermissions.Move
                | EntryPermissions.Write,
            entry.Permissions);
    }

    [Theory]
    [InlineData("G", EntryPermissions.Read)]
    [InlineData("W", EntryPermissions.Write)]
    [InlineData("D", EntryPermissions.Delete)]
    [InlineData("N", EntryPermissions.Rename)]
    [InlineData("V", EntryPermissions.Move)]
    [InlineData("C", EntryPermissions.CreateFile)]
    [InlineData("K", EntryPermissions.CreateDirectory)]
    [InlineData("R", EntryPermissions.Share)]
    public async Task ALetterBecomesThePermissionItStandsFor(string letters, EntryPermissions expected)
    {
        RemoteEntry entry = await EntryAsync(Listing(Vendor(letters)));

        Assert.Equal(expected, entry.Permissions);
    }

    // S says the entry is shared and M that it is mounted from elsewhere, which is where it
    // comes from and not what may be done with it. Z stands for whatever a later server adds.
    [Theory]
    [InlineData("S")]
    [InlineData("M")]
    [InlineData("Z")]
    public async Task ALetterThatIsNotAPermissionIsDropped(string letters)
    {
        RemoteEntry entry = await EntryAsync(Listing(Vendor(letters)));

        Assert.Equal(EntryPermissions.None, entry.Permissions);
    }

    [Fact]
    public async Task ALetterThatIsNotAPermissionLeavesTheOthersAlone()
    {
        RemoteEntry entry = await EntryAsync(Listing(Vendor("SMGZ")));

        Assert.Equal(EntryPermissions.Read, entry.Permissions);
    }

    [Fact]
    public async Task PermissionsWithoutALetterMeanThatNothingMayBeDone()
    {
        RemoteEntry entry = await EntryAsync(Listing(Vendor(string.Empty)));

        // The server answered, and the answer was no to everything. Silence would be null.
        Assert.Equal(EntryPermissions.None, entry.Permissions);
    }

    [Fact]
    public async Task APropertyTheServerDidNotSendStaysUnanswered()
    {
        RemoteEntry entry = await EntryAsync(Listing(string.Empty));

        Assert.Null(entry.Id);
        Assert.Null(entry.Permissions);
    }

    [Fact]
    public async Task AnIdentifierThatArrivedEmptyIsNoIdentifier()
    {
        RemoteEntry entry = await EntryAsync(Listing("<oc:id></oc:id>"));

        Assert.Null(entry.Id);
    }

    [Fact]
    public async Task APropertyTheSeamHasNoPlaceForChangesNothing()
    {
        RemoteEntry entry = await EntryAsync(Listing($"{Vendor("G")}<nc:has-preview>true</nc:has-preview>"));

        // The seam has no place for it, and a property it has no place for costs nothing:
        // the entry is built from the rest as if it had not been there.
        Assert.Equal("/a note.txt", entry.Path);
        Assert.Equal(FileId, entry.Id);
    }

    private static NextcloudProvider Provider(HttpClient httpClient) =>
        new(new DavClient(httpClient), s_base, s_uploads);

    private static async Task<RemoteEntry> EntryAsync(string listing)
    {
        using HttpClient httpClient = new(new ListingHandler(listing));

        return await Provider(httpClient)
            .GetAsync("/a note.txt", TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    private static string Vendor(string permissions) =>
        $"<oc:id>{FileId}</oc:id><oc:permissions>{permissions}</oc:permissions>";

    private static string Listing(string vendorProperties) => $$"""
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf/a%20note.txt</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength>19</d:getcontentlength>
                <d:getlastmodified>Mon, 24 Aug 2026 10:11:12 GMT</d:getlastmodified>
                <d:creationdate>2026-08-24T10:10:00Z</d:creationdate>
                {{vendorProperties}}
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // Answers every request with the same listing and keeps the request body, which is where
    // the properties that were asked for stand.
    private sealed class ListingHandler(string listing) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(listing, Encoding.UTF8, "application/xml"),
            };
        }
    }
}
