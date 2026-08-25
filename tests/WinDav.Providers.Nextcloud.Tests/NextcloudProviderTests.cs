// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using WinDav.Abstractions;
using WinDav.Dav;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class NextcloudProviderTests
{
    private static readonly Uri s_base = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    private static readonly Uri s_uploads = new("https://cloud.example.com/remote.php/dav/uploads/ernolf/");

    // The smallest chunk the protocol allows, so a file of a few chunks stays small enough
    // to build in a test.
    private const long ChunkSize = 5L * 1024 * 1024;

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

        Assert.Equal(
            expected,
            handler.Exchanges.Where(exchange => exchange.Method == "PUT").Select(exchange => exchange.Uri));
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

        byte[] sent = [.. handler.Exchanges
            .Where(exchange => exchange.Method == "PUT")
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
    public async Task AGuardedWriteGoesOutAsASinglePutSoTheGuardSurvives()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern((int)(ChunkSize * 2)));

        await Provider(httpClient).WriteAsync("/big.bin", content, "\"abc123\"", TestContext.Current.CancellationToken);

        Exchange only = Assert.Single(handler.Exchanges);

        Assert.Equal("PUT", only.Method);
        Assert.Equal("\"abc123\"", only.IfMatch);
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

    [Fact]
    public async Task ForUserBuildsTheTwoPathsAStockServerUses()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using MemoryStream content = new(Pattern(16));

        NextcloudProvider provider = NextcloudProvider.ForUser(
            new DavClient(httpClient),
            new Uri("https://cloud.example.com/remote.php/dav"),
            "erna müller");

        await provider.WriteAsync("/note.txt", content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://cloud.example.com/remote.php/dav/files/erna%20m%C3%BCller/note.txt"),
            Assert.Single(handler.Exchanges).Uri);
    }

    [Fact]
    public void AChunkUnderWhatTheServerAcceptsIsRefused()
    {
        using HttpClient httpClient = new(new RecordingHandler());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NextcloudProvider(new DavClient(httpClient), s_base, s_uploads, chunkSize: 1024));
    }

    [Fact]
    public async Task AFileTooLargeForTenThousandChunksIsRefusedBeforeAnythingIsSent()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using HugeStream content = new(10_000L * 100L * 1024 * 1024 + 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Provider(httpClient).WriteAsync("/huge.bin", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Exchanges);
    }

    private static NextcloudProvider Provider(HttpClient httpClient) =>
        new(new DavClient(httpClient), s_base, s_uploads, ChunkSize);

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

        public ReadOnlyMemory<byte> Body { get; init; }
    }

    // Records every request of an exchange, because a chunked upload is a sequence and what
    // matters is the order and the headers of the whole of it.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpStatusCode> _answer;

        private readonly Action<Exchange>? _onRequest;

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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                Body = body,
            };

            Exchanges.Add(exchange);
            _onRequest?.Invoke(exchange);

            return new HttpResponseMessage(_answer(request));
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;
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
