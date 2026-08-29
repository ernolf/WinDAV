// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Dav.Tests;

public sealed class DavProviderFactoryTests
{
    private static readonly Uri s_server = new("https://cloud.example.com/");

    private const string Listing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/</d:href>
            <d:propstat>
              <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task TheCredentialIsSentWithTheFirstRequest()
    {
        TestFactory factory = new();
        using IStorageConnection connection = factory.Connect(Settings(userId: "ernolf", secret: "open sesame"));

        await factory.Client!.PropFindAsync(s_server, cancellationToken: TestContext.Current.CancellationToken);

        string pair = Convert.ToBase64String(Encoding.UTF8.GetBytes("ernolf:open sesame"));

        Assert.Equal($"Basic {pair}", factory.Handler!.Authorization);
    }

    [Fact]
    public async Task WithoutACredentialNothingIsClaimed()
    {
        TestFactory factory = new();
        using IStorageConnection connection = factory.Connect(Settings(userId: "ernolf"));

        await factory.Client!.PropFindAsync(s_server, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(factory.Handler!.Authorization);
    }

    // The one place a password could still surface, since every request this product sends
    // carries one. What is asserted is both halves of it: the request is written down, and
    // the credential in it is not.
    [Fact]
    public async Task ACredentialNeverReachesWhatIsWrittenDown()
    {
        using RecordingLoggerFactory logging = new();

        TestFactory factory = new(logging);
        using IStorageConnection connection = factory.Connect(Settings(userId: "ernolf", secret: "open sesame"));

        await factory.Client!.PropFindAsync(s_server, cancellationToken: TestContext.Current.CancellationToken);

        string written = logging.Written;

        Assert.Contains("PROPFIND https://cloud.example.com/", written, StringComparison.Ordinal);
        Assert.Contains($"Authorization: {LogRedaction.Marker}", written, StringComparison.Ordinal);

        Assert.DoesNotContain("open sesame", written, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ernolf:open sesame")),
            written,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgramNamesItselfAsItWasTold()
    {
        TestFactory factory = new();
        using IStorageConnection connection = factory.Connect(Settings(userAgent: "WinDAV/1.2.3"));

        await factory.Client!.PropFindAsync(s_server, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("WinDAV/1.2.3", factory.Handler!.UserAgent);
    }

    [Fact]
    public async Task WithoutANameNoneIsSent()
    {
        TestFactory factory = new();
        using IStorageConnection connection = factory.Connect(Settings());

        await factory.Client!.PropFindAsync(s_server, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(factory.Handler!.UserAgent);
    }

    [Fact]
    public void TheSettingsAreHandedToTheProviderUntouched()
    {
        TestFactory factory = new();
        ProviderSettings settings = Settings(userId: "ernolf");

        using IStorageConnection connection = factory.Connect(settings);

        Assert.Same(settings, factory.Settings);
        Assert.Same(factory.Provider, connection.Provider);
    }

    [Fact]
    public void SettingsAreRequired()
    {
        TestFactory factory = new();

        Assert.Throws<ArgumentNullException>(() => factory.Connect(null!));
    }

    // The whole reason a connection exists: the client and the handler under it belong to it,
    // and closing a mount is what closes the sockets it opened.
    [Fact]
    public void ClosingTheConnectionClosesWhatItWasBuiltOn()
    {
        TestFactory factory = new();
        IStorageConnection connection = factory.Connect(Settings());

        Assert.False(factory.Handler!.Disposed);

        connection.Dispose();

        Assert.True(factory.Handler.Disposed);
    }

    // Nothing has taken ownership yet when a provider refuses to be built, so the handler
    // would be left behind with its connections open.
    [Fact]
    public void AProviderThatRefusesToBeBuiltLeavesNothingOpen()
    {
        TestFactory factory = new() { Fail = true };

        Assert.Throws<InvalidOperationException>(() => factory.Connect(Settings()));

        Assert.True(factory.Handler!.Disposed);
    }

    // A redirect that is followed silently sends a MOVE or a COPY to one server while its
    // destination header still names the other.
    [Fact]
    public void TheHandlerItBuildsDoesNotFollowRedirects()
    {
        using HttpMessageHandler handler = new TestFactory().CreateDefaultHandler();

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    private static ProviderSettings Settings(string? userId = null, string? secret = null, string? userAgent = null) =>
        new()
        {
            Server = s_server,
            UserId = userId,
            Secret = secret,
            UserAgent = userAgent,
        };

    private sealed class TestFactory : DavProviderFactory
    {
        public TestFactory(ILoggerFactory? logging = null)
            : base(logging)
        {
        }

        public override string Name => "test";

        public RecordingHandler? Handler { get; private set; }

        public DavClient? Client { get; private set; }

        public ProviderSettings? Settings { get; private set; }

        public IStorageProvider? Provider { get; private set; }

        public bool Fail { get; init; }

        // The one the factory would have built on its own, which is what the test about
        // redirects is looking at.
        public HttpMessageHandler CreateDefaultHandler() => base.CreateMessageHandler();

        protected override HttpMessageHandler CreateMessageHandler()
        {
            Handler = new RecordingHandler();

            return Handler;
        }

        protected override IStorageProvider CreateProvider(DavClient client, ProviderSettings settings)
        {
            Client = client;
            Settings = settings;

            if (Fail)
            {
                throw new InvalidOperationException("This provider refuses to be built.");
            }

            Provider = new StubProvider();

            return Provider;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? UserAgent { get; private set; }

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            UserAgent = request.Headers.UserAgent.Count == 0 ? null : request.Headers.UserAgent.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(Listing, Encoding.UTF8, "application/xml"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    // Everything the handler is willing to write, kept as it was written. The file sink is
    // not in the way, because what a credential must never reach is the record itself.
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _records = [];

        public string Written => string.Join('\n', _records);

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() => GC.SuppressFinalize(this);

        internal void Add(string record) => _records.Add(record);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerFactory _written;

        internal RecordingLogger(RecordingLoggerFactory written)
        {
            _written = written;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // Everything, so that what is asserted is what the loudest recording would hold.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _written.Add(formatter(state, exception));
        }
    }

    // Whether the connection hands out the provider it was given is what these tests are
    // about; what that provider does is the provider's own tests.
    private sealed class StubProvider : IStorageProvider
    {
        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string path, long offset, long? count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> WriteAsync(string path, Stream content, string? ifMatch, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CopyAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
