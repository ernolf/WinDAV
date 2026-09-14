// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WinDav.Abstractions;
using WinDav.Dav;
using WinDav.Providers.Nextcloud.Ocs;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class NextcloudProviderTests
{
    private static readonly Uri s_server = new("https://cloud.example.com/");

    private static readonly Uri s_base = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    private static readonly Uri s_uploads = new("https://cloud.example.com/remote.php/dav/uploads/ernolf/");

    // The chunk size the test server states unless a test says otherwise. It is the smallest
    // the protocol allows, so a file of a few chunks stays small enough to build in a test.
    private const long ChunkSize = 5L * 1024 * 1024;

    // What the test server states unless a test says otherwise: chunks of that size, and two
    // of them out at once, so that the overlap shows.
    private const string SmallChunks = """{"max_size":5242880,"max_parallel_count":2}""";

    // Two dates far enough apart to tell which of them ended up where, with the seconds
    // since the epoch the server is handed written out rather than worked out, so that the
    // test does not agree with the code by doing the same sum.
    private static readonly DateTimeOffset s_created = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private const string CreatedSeconds = "1714979289";

    private static readonly DateTimeOffset s_modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private const string ModifiedSeconds = "1767323045";

    [Fact]
    public async Task AFileThatFitsInOneChunkGoesOutAsASinglePut()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(1024));

        await Provider(httpClient).WriteAsync("/small.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal("PUT", only.Method);
        Assert.Equal(new Uri(s_base, "small.bin"), only.Uri);
        Assert.Null(only.Destination);
    }

    [Fact]
    public async Task TheTimesRideAlongOnTheSinglePut()
    {
        RecordingHandler handler = new() { ETag = _ => "\"def456\"" };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(1024));

        string? etag = await Provider(httpClient).WriteAsync(
            "/small.bin",
            content,
            times: new EntryTimes(s_created, s_modified),
            cancellationToken: TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal("PUT", only.Method);
        Assert.Equal(ModifiedSeconds, only.Modified);
        Assert.Equal(CreatedSeconds, only.Created);

        // The server sets the times while it writes the file and works the tag out after,
        // so the tag it answers with is the tag of what is now there.
        Assert.Equal("\"def456\"", etag);
    }

    [Fact]
    public async Task ATimeInsideTheFirstDayIsNotSent()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(1024));

        // The server refuses anything that close to the epoch as a value nobody can have
        // meant, and it refuses it after the file has been written.
        await Provider(httpClient).WriteAsync(
            "/small.bin",
            content,
            times: new EntryTimes(DateTimeOffset.UnixEpoch.AddHours(3), s_modified),
            cancellationToken: TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal(ModifiedSeconds, only.Modified);
        Assert.Null(only.Created);
    }

    [Fact]
    public async Task TheTimesRideOnTheAssemblingMoveAndOnNothingElse()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2) + 7));

        await Provider(httpClient).WriteAsync(
            "/big.bin",
            content,
            times: new EntryTimes(s_created, s_modified),
            cancellationToken: TestContext.Current.CancellationToken);

        // The chunks are not the file, and the request that makes the file out of them is
        // the one the server reads the times off.
        Exchange move = handler.Exchanges[^1];
        Assert.Equal("MOVE", move.Method);
        Assert.Equal(ModifiedSeconds, move.Modified);
        Assert.Equal(CreatedSeconds, move.Created);

        Assert.All(
            handler.Exchanges.SkipLast(1),
            exchange =>
            {
                Assert.Null(exchange.Modified);
                Assert.Null(exchange.Created);
            });
    }

    [Fact]
    public async Task SetTimesAsyncAsksForBothNamesAtOnce()
    {
        RecordingHandler handler = new(_ => HttpStatusCode.MultiStatus);
        using HttpClient httpClient = new(handler);

        await Provider(httpClient).SetTimesAsync(
            "/small.bin",
            new EntryTimes(s_created, s_modified),
            TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal("PROPPATCH", only.Method);

        string document = Encoding.UTF8.GetString(only.Body.Span);

        // The date-time of the protocol for the one, and the seconds the server inherited
        // from ownCloud for the other, in the one request.
        Assert.Contains("<creationdate>2024-05-06T07:08:09.0000000Z</creationdate>", document, StringComparison.Ordinal);
        Assert.Contains($"<lastmodified>{ModifiedSeconds}</lastmodified>", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALargeFileIsCreatedAssembledAndNothingElse()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2) + 7));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        string[] expected = ["MKCOL", "PUT", "PUT", "PUT", "MOVE"];

        Assert.Equal(expected, handler.Exchanges.Select(exchange => exchange.Method));
    }

    [Fact]
    public async Task TheChunksAreNamedAsNumbersPaddedToTheSameWidth()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2) + 7));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Uri folder = handler.Exchanges[0].Uri;
        Uri[] expected = [new(folder, "00001"), new(folder, "00002"), new(folder, "00003")];

        // The server puts the chunks together by name, and with more than one out at a time
        // the order they arrive in need not be the order they were started in.
        Assert.Equal(
            expected,
            handler.Exchanges
                .Where(exchange => exchange.Method == "PUT")
                .Select(exchange => exchange.Uri)
                .OrderBy(uri => uri.AbsoluteUri, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheUploadDirectoryIsBelowTheUploadAreaAndNotBelowTheFiles()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)ChunkSize + 1));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Uri folder = handler.Exchanges[0].Uri;

        Assert.StartsWith(s_uploads.AbsoluteUri, folder.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/", folder.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestOfTheUploadNamesTheTargetAndTheTotalLength()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        int length = (int)ChunkSize + 1;
        using MemoryStream content = new(Pattern(length));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        string target = new Uri(s_base, "big.bin").AbsoluteUri;
        string total = length.ToString(CultureInfo.InvariantCulture);

        foreach (Exchange exchange in handler.Exchanges)
        {
            Assert.Equal(target, exchange.Destination);
            Assert.Equal(total, exchange.TotalLength);
        }
    }

    [Fact]
    public async Task TheAssemblingMoveTakesTheDotFileToTheTarget()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)ChunkSize + 1));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Exchange move = handler.Exchanges[^1];

        Assert.Equal("MOVE", move.Method);
        Assert.Equal(new Uri(handler.Exchanges[0].Uri, ".file"), move.Uri);
        Assert.Equal(new Uri(s_base, "big.bin").AbsoluteUri, move.Destination);
        Assert.Equal("T", move.Overwrite);
    }

    [Fact]
    public async Task TheChunksAreTheFileInOrder()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        byte[] bytes = Pattern((int)(ChunkSize * 2) + 7);
        using MemoryStream content = new(bytes);

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        // In the order of their names, which is the order the server assembles them in.
        byte[] sent = [.. handler.Exchanges
            .Where(exchange => exchange.Method == "PUT")
            .OrderBy(exchange => exchange.Uri.AbsoluteUri, StringComparer.Ordinal)
            .SelectMany(exchange => exchange.Body.ToArray())];

        Assert.Equal(bytes.Length, sent.Length);
        Assert.True(bytes.AsSpan().SequenceEqual(sent));
    }

    [Fact]
    public async Task AStreamThatCannotBeMeasuredGoesOutAsASinglePut()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using UnmeasurableStream content = new(Pattern((int)(ChunkSize * 2)));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("PUT", Assert.Single(handler.Exchanges).Method);
    }

    [Fact]
    public async Task AGuardedWriteCarriesTheGuardOnTheAssemblingMoveAndNamesTheTarget()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        await Provider(httpClient).WriteAsync("/big.bin", content, "\"abc123\"", cancellationToken: TestContext.Current.CancellationToken);

        Exchange move = handler.Exchanges[^1];
        Assert.Equal("MOVE", move.Method);

        // An If-Match there would be compared with the .file the MOVE is sent to. The tagged
        // list names the target, which is what the tag was read from.
        Assert.Equal($"<{new Uri(s_base, "big.bin").AbsoluteUri}> ([\"abc123\"])", move.If);

        Assert.All(handler.Exchanges, exchange => Assert.Null(exchange.IfMatch));
        Assert.All(handler.Exchanges.SkipLast(1), exchange => Assert.Null(exchange.If));
    }

    [Fact]
    public async Task AWriteThatHasToMakeTheNameAssemblesWithoutOverwriting()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        await Provider(httpClient).WriteAsync("/big.bin", content, mustBeNew: true, cancellationToken: TestContext.Current.CancellationToken);

        Exchange move = handler.Exchanges[^1];
        Assert.Equal("MOVE", move.Method);
        Assert.Equal("F", move.Overwrite);

        // If-None-Match: * on the MOVE would be about the .file, which is there by then, and
        // would refuse every upload.
        Assert.All(handler.Exchanges, exchange => Assert.Null(exchange.IfNoneMatch));
    }

    [Fact]
    public async Task ANameTakenBeforeTheAssemblyIsAlreadyExistsAndTheUploadDirectoryGoes()
    {
        RecordingHandler handler = new(request =>
            request.Method.Method == "MOVE" ? HttpStatusCode.PreconditionFailed : RecordingHandler.Success(request));

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, mustBeNew: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.AlreadyExists, exception.Error);

        Exchange last = handler.Exchanges[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal(handler.Exchanges[0].Uri, last.Uri);
    }

    [Fact]
    public async Task ATargetChangedBeforeTheAssemblyIsAFailedPreconditionAndTheUploadDirectoryGoes()
    {
        RecordingHandler handler = new(request =>
            request.Method.Method == "MOVE" ? HttpStatusCode.PreconditionFailed : RecordingHandler.Success(request));

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, "\"abc123\"", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.PreconditionFailed, exception.Error);

        Exchange last = handler.Exchanges[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal(handler.Exchanges[0].Uri, last.Uri);
    }

    [Fact]
    public async Task AChunkedUploadReturnsTheEntityTagTheAssemblyAnswersWith()
    {
        RecordingHandler handler = new()
        {
            ETag = request => request.Method.Method == "MOVE" ? "\"def456\"" : "\"chunk\"",
        };

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        string? etag = await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("\"def456\"", etag);
    }

    [Fact]
    public async Task TheChunksOverlapAndNoMoreAreOutThanTheServerAllows()
    {
        OverlapHandler handler = new(chunks: 5, atOnce: 3, CapabilitiesStating("""{"max_size":5242880,"max_parallel_count":3}"""));
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 5)));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.MostAtOnce);
    }

    [Fact]
    public async Task AServerThatStatesNoCountGetsFiveChunksAtOnce()
    {
        OverlapHandler handler = new(chunks: 6, atOnce: 5, CapabilitiesStating("""{"max_size":5242880}"""));
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 6)));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, handler.MostAtOnce);
    }

    [Fact]
    public async Task TheChunksAreAsLargeAsTheServerStates()
    {
        const int Stated = 6 * 1024 * 1024;
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating("""{"max_size":6291456,"max_parallel_count":2}""") };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((Stated * 2) + 7));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        int[] expected = [Stated, Stated, 7];

        Assert.Equal(expected, ChunkLengths(handler));
    }

    // The protocol allows no chunk under five mebibytes but the last, whatever a server states.
    [Fact]
    public async Task AChunkSizeUnderTheSmallestTheProtocolAllowsIsRaisedToIt()
    {
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating("""{"max_size":1024,"max_parallel_count":2}""") };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2) + 7));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        int[] expected = [(int)ChunkSize, (int)ChunkSize, 7];

        Assert.Equal(expected, ChunkLengths(handler));
    }

    [Theory]
    [InlineData("""{"max_size":0,"max_parallel_count":5}""")]
    [InlineData("""{"max_size":-1,"max_parallel_count":5}""")]
    public async Task AServerThatStatesNoChunkSizeGetsASinglePut(string chunkedUpload)
    {
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating(chunkedUpload) };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal("PUT", only.Method);
        Assert.Equal(new Uri(s_base, "big.bin"), only.Uri);
    }

    // A server before Nextcloud 31 states no limits, and gets the chunks the web interface
    // sends it: ten mebibytes each.
    [Fact]
    public async Task AServerThatStatesNoLimitsGetsChunksOfTenMebibytes()
    {
        const int Unstated = 10 * 1024 * 1024;
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating(null) };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(Unstated + 7));

        await Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        int[] expected = [Unstated, 7];

        Assert.Equal(expected, ChunkLengths(handler));
    }

    [Fact]
    public async Task TheServerIsAskedForItsLimitsOnceAndNotForEveryFile()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream first = new(Pattern((int)(ChunkSize * 2)));
        using MemoryStream second = new(Pattern((int)(ChunkSize * 2)));
        NextcloudProvider provider = Provider(httpClient);

        await provider.WriteAsync("/first.bin", first, cancellationToken: TestContext.Current.CancellationToken);
        await provider.WriteAsync("/second.bin", second, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CapabilitiesAsked);
    }

    [Fact]
    public async Task AFileNoLargerThanTheSmallestChunkGoesWithoutAskingTheServer()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)ChunkSize));

        await Provider(httpClient).WriteAsync("/small.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("PUT", Assert.Single(handler.Exchanges).Method);
        Assert.Equal(0, handler.CapabilitiesAsked);
    }

    [Fact]
    public async Task LimitsTheServerCouldNotStateAreAskedForAgainWithTheNextFile()
    {
        RecordingHandler handler = new() { CapabilitiesStatus = HttpStatusCode.ServiceUnavailable };
        using HttpClient httpClient = new(handler);
        using MemoryStream first = new(Pattern((int)(ChunkSize * 2)));
        using MemoryStream second = new(Pattern((int)(ChunkSize * 2)));
        NextcloudProvider provider = Provider(httpClient);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.WriteAsync("/big.bin", first, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Busy, exception.Error);
        Assert.Empty(handler.Exchanges);

        handler.CapabilitiesStatus = HttpStatusCode.OK;

        await provider.WriteAsync("/big.bin", second, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.CapabilitiesAsked);
        Assert.Equal("MOVE", handler.Exchanges[^1].Method);
    }

    [Fact]
    public async Task CapabilitiesThatAreNoEnvelopeAreAProtocolError()
    {
        RecordingHandler handler = new() { Capabilities = "<html><body>Down for maintenance</body></html>" };
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Protocol, exception.Error);
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task AFailedChunkTakesTheUploadDirectoryWithIt()
    {
        RecordingHandler handler = new(request =>
            request.Method.Method == "PUT" && request.RequestUri!.AbsoluteUri.EndsWith("00002", StringComparison.Ordinal)
                ? HttpStatusCode.InsufficientStorage
                : RecordingHandler.Success(request));

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.InsufficientStorage, exception.Error);

        Exchange last = handler.Exchanges[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal(handler.Exchanges[0].Uri, last.Uri);
    }

    [Fact]
    public async Task ARefusedChunkStopsTheChunksAfterIt()
    {
        RecordingHandler handler = new(request =>
            request.Method.Method == "PUT" && request.RequestUri!.AbsoluteUri.EndsWith("00001", StringComparison.Ordinal)
                ? HttpStatusCode.InsufficientStorage
                : RecordingHandler.Success(request));

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 4)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.InsufficientStorage, exception.Error);

        // The second may be out already, since two go at once. Nothing after it is started.
        Assert.DoesNotContain(
            handler.Exchanges,
            exchange => exchange.Uri.AbsoluteUri.EndsWith("00003", StringComparison.Ordinal)
                || exchange.Uri.AbsoluteUri.EndsWith("00004", StringComparison.Ordinal));

        Assert.Equal("DELETE", handler.Exchanges[^1].Method);
    }

    [Fact]
    public async Task ACancelledUploadTakesTheUploadDirectoryWithIt()
    {
        using CancellationTokenSource source = new();
        RecordingHandler handler = new(onRequest: exchange =>
        {
            if (exchange.Method == "PUT")
            {
                source.Cancel();
            }
        });

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider(httpClient).WriteAsync("/big.bin", content, cancellationToken: source.Token));

        Exchange last = handler.Exchanges[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal(handler.Exchanges[0].Uri, last.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task AnAssemblyTheGatewayGaveUpOnIsWaitedForAndCheckedInsteadOfTakenAway(HttpStatusCode gateway)
    {
        // The server is still assembling for the first two looks and has removed the upload
        // directory by the third.
        int looks = 0;
        RecordingHandler handler = new(request => request.Method.Method switch
        {
            "MOVE" => gateway,
            "PROPFIND" when IsUpload(request) => Interlocked.Increment(ref looks) <= 2
                ? HttpStatusCode.MultiStatus
                : HttpStatusCode.NotFound,
            "PROPFIND" => HttpStatusCode.MultiStatus,
            _ => RecordingHandler.Success(request),
        })
        {
            Body = request => IsUpload(request) ? null : Described("/big.bin", ChunkSize * 2, "\"def456\""),
        };

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        string? etag = await Patient(httpClient)
            .WriteAsync("/big.bin", content, "\"abc123\"", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("\"def456\"", etag);
        Assert.Equal(3, looks);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == "DELETE");
    }

    [Fact]
    public async Task AnAssemblyThatOutlastsTheWaitIsReportedAndLeftToTheServer()
    {
        RecordingHandler handler = new(request => request.Method.Method switch
        {
            "MOVE" => HttpStatusCode.GatewayTimeout,
            "PROPFIND" => HttpStatusCode.MultiStatus,
            _ => RecordingHandler.Success(request),
        });

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Impatient(httpClient).WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Unreachable, exception.Error);
        Assert.Contains(handler.Exchanges, exchange => exchange.Method == "PROPFIND");
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == "DELETE");
    }

    [Fact]
    public async Task AnAssemblyThatLeftTheOldFileInPlaceIsReportedAndNotTakenAway()
    {
        RecordingHandler handler = new(request => request.Method.Method switch
        {
            "MOVE" => HttpStatusCode.GatewayTimeout,
            "PROPFIND" when IsUpload(request) => HttpStatusCode.NotFound,
            "PROPFIND" => HttpStatusCode.MultiStatus,
            _ => RecordingHandler.Success(request),
        })
        {
            Body = _ => Described("/big.bin", ChunkSize * 2, "\"abc123\""),
        };

        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => Patient(httpClient).WriteAsync("/big.bin", content, "\"abc123\"", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.Unreachable, exception.Error);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == "DELETE");
    }

    [Fact]
    public async Task ForUserBuildsTheTwoPathsAStockServerUses()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(16));

        NextcloudProvider provider = NextcloudProvider.ForUser(
            new DavClient(httpClient),
            new OcsClient(httpClient, s_server),
            new Uri("https://cloud.example.com/remote.php/dav"),
            "erna müller");

        await provider.WriteAsync("/note.txt", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://cloud.example.com/remote.php/dav/files/erna%20m%C3%BCller/note.txt"),
            Assert.Single(handler.Exchanges).Uri);
    }

    [Fact]
    public async Task ForServerFindsTheEndpointBelowTheAddressAPersonWouldType()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(16));

        NextcloudProvider provider = NextcloudProvider.ForServer(
            httpClient,
            new Uri("https://cloud.example.com"),
            "ernolf");

        await provider.WriteAsync("/note.txt", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://cloud.example.com/remote.php/dav/files/ernolf/note.txt"),
            Assert.Single(handler.Exchanges).Uri);
    }

    // A server under a path of its own is what a reverse proxy in front of two applications
    // produces, and the endpoint sits below that path rather than at the host's root.
    [Fact]
    public async Task ForServerKeepsThePathTheServerLivesUnder()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(16));

        NextcloudProvider provider = NextcloudProvider.ForServer(
            httpClient,
            new Uri("https://example.com/cloud"),
            "ernolf");

        await provider.WriteAsync("/note.txt", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://example.com/cloud/remote.php/dav/files/ernolf/note.txt"),
            Assert.Single(handler.Exchanges).Uri);
    }

    [Fact]
    public async Task ForServerAsksForTheLimitsBelowThePathTheServerLivesUnder()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        NextcloudProvider provider = NextcloudProvider.ForServer(
            httpClient,
            new Uri("https://example.com/cloud"),
            "ernolf");

        await provider.WriteAsync("/big.bin", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://example.com/cloud/ocs/v2.php/cloud/capabilities"), handler.CapabilitiesUri);
    }

    // What turns one account into several mounts: everything above the remote path is out of
    // reach of the provider that was built for it.
    [Fact]
    public async Task ARemotePathBecomesTheRootOfWhatIsOffered()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(16));

        NextcloudProvider provider = NextcloudProvider.ForServer(
            httpClient,
            new Uri("https://cloud.example.com"),
            "ernolf",
            "/Documents");

        await provider.WriteAsync("/note.txt", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://cloud.example.com/remote.php/dav/files/ernolf/Documents/note.txt"),
            Assert.Single(handler.Exchanges).Uri);
    }

    [Fact]
    public async Task AFileTooLargeForTenThousandChunksIsRefusedBeforeAnythingIsSent()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using HugeStream content = new(10_000L * 5L * 1024 * 1024 * 1024 + 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Provider(httpClient).WriteAsync("/huge.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        // Only the limits were asked for, and they are not part of the record.
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task AnUploadBegunAheadNumbersTheRestOnFromThePieces()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        byte[] bytes = Pattern((int)(ChunkSize * 2) + 7);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);
        Assert.True(await upload.SendAsync(new MemoryStream(bytes, 0, (int)ChunkSize), cancellationToken));

        using MemoryStream rest = new(bytes, (int)ChunkSize, bytes.Length - (int)ChunkSize);

        await upload.FinishAsync(rest, cancellationToken: cancellationToken);

        Assert.Equal(["MKCOL", "PUT", "PUT", "PUT", "MOVE"], handler.Exchanges.Select(exchange => exchange.Method));

        Exchange[] chunks = [.. handler.Exchanges
            .Where(exchange => exchange.Method == "PUT")
            .OrderBy(exchange => exchange.Uri.AbsoluteUri, StringComparer.Ordinal)];

        Assert.Equal(["00001", "00002", "00003"], chunks.Select(chunk => chunk.Uri.Segments[^1]));

        byte[] sent = [.. chunks.SelectMany(chunk => chunk.Body.ToArray())];
        Assert.True(bytes.AsSpan().SequenceEqual(sent));

        // Nobody knew the length when the directory was made and the piece went, and the
        // assembly is told it.
        Assert.Null(handler.Exchanges[0].TotalLength);
        Assert.Null(chunks[0].TotalLength);

        Exchange move = handler.Exchanges[^1];
        Assert.Equal(bytes.Length.ToString(CultureInfo.InvariantCulture), move.TotalLength);
        Assert.Equal(new Uri(s_base, "big.bin").AbsoluteUri, move.Destination);
    }

    [Fact]
    public async Task AnUploadBegunAheadAndLetGoTakesItsChunksAway()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);

        IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);
        Assert.True(await upload.SendAsync(new MemoryStream(Pattern((int)ChunkSize)), TestContext.Current.CancellationToken));

        await upload.DisposeAsync();

        Exchange last = handler.Exchanges[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal(handler.Exchanges[0].Uri, last.Uri);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == "MOVE");
    }

    [Fact]
    public async Task AnUploadWithNothingSentAheadIsAnOrdinaryWrite()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream rest = new(Pattern(100));

        await using IUpload? upload = Provider(httpClient).BeginUpload("/small.bin");

        Assert.NotNull(upload);

        await upload.FinishAsync(rest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("PUT", Assert.Single(handler.Exchanges).Method);
    }

    [Fact]
    public async Task APieceTheServerRefusedFailsTheFinishAndTakesTheChunksAway()
    {
        RecordingHandler handler = new(request =>
            request.Method.Method == "PUT" && request.RequestUri!.AbsoluteUri.EndsWith("00001", StringComparison.Ordinal)
                ? HttpStatusCode.InsufficientStorage
                : RecordingHandler.Success(request));

        using HttpClient httpClient = new(handler);
        byte[] bytes = Pattern((int)ChunkSize + 7);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);
        Assert.True(await upload.SendAsync(new MemoryStream(bytes, 0, (int)ChunkSize), cancellationToken));

        using MemoryStream rest = new(bytes, (int)ChunkSize, 7);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => upload.FinishAsync(rest, cancellationToken: cancellationToken));

        Assert.Equal(ProviderError.InsufficientStorage, exception.Error);
        Assert.Equal("DELETE", handler.Exchanges[^1].Method);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == "MOVE");
    }

    [Fact]
    public async Task AnUploadBegunAheadAsksForPiecesAsLargeAsTheServerStates()
    {
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating("""{"max_size":6291456,"max_parallel_count":2}""") };
        using HttpClient httpClient = new(handler);

        await using IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);
        Assert.Equal(6L * 1024 * 1024, await upload.GetPieceSizeAsync(TestContext.Current.CancellationToken));

        // Asking is not sending: nothing has been made on the server.
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task AnUploadBegunAheadForAServerThatWantsNoChunksTakesNoPieces()
    {
        RecordingHandler handler = new() { Capabilities = CapabilitiesStating("""{"max_size":0,"max_parallel_count":5}""") };
        using HttpClient httpClient = new(handler);
        byte[] bytes = Pattern((int)(ChunkSize * 2));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);
        Assert.Equal(0L, await upload.GetPieceSizeAsync(cancellationToken));
        Assert.False(await upload.SendAsync(new MemoryStream(bytes, 0, (int)ChunkSize), cancellationToken));

        using MemoryStream rest = new(bytes);

        await upload.FinishAsync(rest, cancellationToken: cancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);
        Assert.Equal("PUT", only.Method);
        Assert.Equal(new Uri(s_base, "big.bin"), only.Uri);
    }

    [Fact]
    public async Task APieceOfAnotherLengthIsRefusedAndDisposedOfAllTheSame()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        MemoryStream piece = new(Pattern(1024));

        await using IUpload? upload = Provider(httpClient).BeginUpload("/big.bin");

        Assert.NotNull(upload);

        await Assert.ThrowsAsync<ArgumentException>(() => upload.SendAsync(piece, TestContext.Current.CancellationToken));

        // The stream is the upload's once handed over, whatever becomes of the piece.
        Assert.False(piece.CanRead);
        Assert.Empty(handler.Exchanges);
    }

    private static NextcloudProvider Provider(HttpClient httpClient) =>
        new(new DavClient(httpClient), new OcsClient(httpClient, s_server), s_base, s_uploads);

    // Looks at the upload directory without pausing, so waiting for an assembly costs a test
    // no time, and waits as long as a real server gets.
    private static NextcloudProvider Patient(HttpClient httpClient) =>
        new(new DavClient(httpClient), new OcsClient(httpClient, s_server), s_base, s_uploads)
        {
            AssemblyPoll = TimeSpan.FromMilliseconds(1),
        };

    // Gives up on an assembly almost at once, so that running out of time can be tested.
    private static NextcloudProvider Impatient(HttpClient httpClient) =>
        new(new DavClient(httpClient), new OcsClient(httpClient, s_server), s_base, s_uploads)
        {
            AssemblyPoll = TimeSpan.FromMilliseconds(1),
            ShortestAssemblyWait = TimeSpan.FromMilliseconds(50),
            LongestAssemblyWait = TimeSpan.FromMilliseconds(50),
        };

    private static bool IsUpload(HttpRequestMessage request) =>
        request.RequestUri!.AbsoluteUri.StartsWith(s_uploads.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsCapabilities(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/ocs/v2.php/cloud/capabilities", StringComparison.Ordinal);

    // What the server answers when it is asked for its capabilities, with the limits of a
    // chunked upload it states, or with none, as a server before Nextcloud 31 does.
    private static string CapabilitiesStating(string? chunkedUpload)
    {
        string limits = chunkedUpload is null ? string.Empty : $""", "chunked_upload": {chunkedUpload}""";

        return $$"""
            {"ocs": {"meta": {"status": "ok", "statuscode": 200, "message": "OK"}, "data": {"capabilities": {"files": {"bigfilechunking": true{{limits}} } } } } }
            """;
    }

    // The lengths of the chunks in the order of their names, which is the order the server
    // assembles them in.
    private static int[] ChunkLengths(RecordingHandler handler) =>
        [.. handler.Exchanges
            .Where(exchange => exchange.Method == "PUT")
            .OrderBy(exchange => exchange.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(exchange => exchange.Body.Length)];

    // What the server says about a file below the user's files when it is asked.
    private static string Described(string path, long length, string eTag) => $"""
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/files/ernolf{path}</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength>{length.ToString(CultureInfo.InvariantCulture)}</d:getcontentlength>
                <d:getetag>{eTag}</d:getetag>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private sealed class Exchange
    {
        public required string Method { get; init; }

        public required Uri Uri { get; init; }

        public string? Destination { get; init; }

        public string? TotalLength { get; init; }

        public string? Overwrite { get; init; }

        public string? IfMatch { get; init; }

        public string? IfNoneMatch { get; init; }

        public string? If { get; init; }

        public string? Modified { get; init; }

        public string? Created { get; init; }

        public ReadOnlyMemory<byte> Body { get; init; }
    }

    // Records every request of an exchange, because a chunked upload is a sequence and what
    // matters is the order and the headers of the whole of it. The chunks go out side by
    // side, so the record is kept under a lock.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        // What the server answers a PROPPATCH it carried out with: each property named back
        // under the status it was given.
        private const string Written = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/ernolf/small.bin</d:href>
                <d:propstat>
                  <d:prop><d:creationdate/><d:lastmodified/></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        private readonly Func<HttpRequestMessage, HttpStatusCode> _answer;

        private readonly Action<Exchange>? _onRequest;

        private readonly Lock _gate = new();

        public RecordingHandler(
            Func<HttpRequestMessage, HttpStatusCode>? answer = null,
            Action<Exchange>? onRequest = null)
        {
            _answer = answer ?? Success;
            _onRequest = onRequest;
        }

        // What each method answers when it works. DELETE has no 201 among the codes the
        // client accepts, so one blanket answer for everything would not do.
        public static HttpStatusCode Success(HttpRequestMessage request) =>
            request.Method.Method == "DELETE" ? HttpStatusCode.NoContent : HttpStatusCode.Created;

        public List<Exchange> Exchanges { get; } = [];

        // Put on the answer to every request it names one for, because a write is worth its
        // entity tag and only a handler that names one can be asked what became of it.
        public Func<HttpRequestMessage, string?>? ETag { get; init; }

        // The document a 207 carries where the test names one; the answer to a PROPPATCH
        // otherwise.
        public Func<HttpRequestMessage, string?>? Body { get; init; }

        // The server's capabilities and the status they come with. Asking for them is counted
        // and kept out of the record, which is about the file.
        public string Capabilities { get; init; } = CapabilitiesStating(SmallChunks);

        public HttpStatusCode CapabilitiesStatus { get; set; } = HttpStatusCode.OK;

        public int CapabilitiesAsked { get; private set; }

        public Uri? CapabilitiesUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsCapabilities(request))
            {
                lock (_gate)
                {
                    CapabilitiesAsked++;
                    CapabilitiesUri = request.RequestUri;
                }

                return new HttpResponseMessage(CapabilitiesStatus)
                {
                    Content = new StringContent(Capabilities, Encoding.UTF8, "application/json"),
                };
            }

            ReadOnlyMemory<byte> body = request.Content is null
                ? ReadOnlyMemory<byte>.Empty
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            Exchange exchange = new()
            {
                Method = request.Method.Method,
                Uri = request.RequestUri!,
                Destination = Header(request, "Destination"),
                TotalLength = Header(request, "OC-Total-Length"),
                Overwrite = Header(request, "Overwrite"),
                IfMatch = request.Headers.IfMatch.Count == 0 ? null : request.Headers.IfMatch.ToString(),
                IfNoneMatch = request.Headers.IfNoneMatch.Count == 0 ? null : request.Headers.IfNoneMatch.ToString(),
                If = Header(request, "If"),
                Modified = Header(request, "X-OC-MTime"),
                Created = Header(request, "X-OC-CTime"),
                Body = body,
            };

            lock (_gate)
            {
                Exchanges.Add(exchange);
            }

            _onRequest?.Invoke(exchange);

            HttpResponseMessage answer = new(_answer(request));

            // A 207 is only an answer once it carries the document that says what became of
            // each property, which is the part the client reads.
            if (answer.StatusCode == HttpStatusCode.MultiStatus)
            {
                answer.Content = new StringContent(Body?.Invoke(request) ?? Written, Encoding.UTF8, "application/xml");
            }

            if (ETag?.Invoke(request) is string tag)
            {
                answer.Headers.ETag = new EntityTagHeaderValue(tag);
            }

            return answer;
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;
    }

    // Holds the chunks until as many as the test expects are out at once, or until the last
    // one has arrived, so that the count is reached whenever the provider allows it and not
    // only when the timing happens to. It counts how many were out at once, and a chunk that
    // waits in vain fails with a timeout rather than hanging the test.
    private sealed class OverlapHandler(int chunks, int atOnce, string capabilities) : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;

        private int _out;

        public int MostAtOnce { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsCapabilities(request))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(capabilities, Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method != HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            Task released;
            lock (_gate)
            {
                _arrived++;
                _out++;
                MostAtOnce = Math.Max(MostAtOnce, _out);
                released = _release.Task;

                if (_out >= atOnce || _arrived == chunks)
                {
                    _release.SetResult();
                    _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            try
            {
                await released.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _out--;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    // A stream that reads but cannot state a length, which is what a pipe looks like.
    private sealed class UnmeasurableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
            // Nothing is written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // States a length no machine has the memory for. It is never read: the size alone is
    // enough to be turned down.
    private sealed class HugeStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
            // Nothing is written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
