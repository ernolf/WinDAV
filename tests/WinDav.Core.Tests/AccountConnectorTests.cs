// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core.Configuration;
using WinDav.Core.Providers;
using WinDav.Core.Security;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class AccountConnectorTests
{
    private const string ProviderName = "stub";

    private const string Reference = "home-credential";

    private const string Credential = "open sesame";

    private static readonly Uri s_server = new("https://cloud.example.com/");

    // What the mount points at, which decisions.md 71 keeps apart from what the account is
    // called. Fixed, so that both halves of the sample can name it.
    private static readonly Guid s_account = new("2c9a7f10-84b3-4a7e-8f21-6d5c4b3a2910");

    [Fact]
    public async Task TheMountsAccountDecidesWhereTheConnectionGoes()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(), "files", TestContext.Current.CancellationToken);

        Assert.Equal(s_server, factory.Settings!.Server);
        Assert.Equal("ernolf", factory.Settings.UserId);
    }

    // Decision 71: the name the credential is presented under is a question of its own, and
    // the provider is told both rather than one standing in for the other.
    [Fact]
    public async Task TheLoginNameReachesTheProviderNextToTheUser()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(loginId: "ernolf@example.com"), "files", TestContext.Current.CancellationToken);

        Assert.Equal("ernolf", factory.Settings!.UserId);
        Assert.Equal("ernolf@example.com", factory.Settings.LoginId);
    }

    // An account reached under the name it is known by leaves the question unanswered rather
    // than answering it twice.
    [Fact]
    public async Task AnAccountWithNoLoginOfItsOwnLeavesItEmpty()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(), "files", TestContext.Current.CancellationToken);

        Assert.Null(factory.Settings!.LoginId);
    }

    [Fact]
    public async Task TheMountIsFoundWithoutRegardToCase()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(), "FILES", TestContext.Current.CancellationToken);

        Assert.NotNull(factory.Settings);
    }

    [Fact]
    public async Task AMountThatIsNotThereIsRefused()
    {
        KeyNotFoundException exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(Sample(), "pictures", TestContext.Current.CancellationToken));

        Assert.Contains("pictures", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountThatNamesAnAccountThatIsNotThereIsRefused()
    {
        ClientConfiguration configuration = new()
        {
            Mounts = [new MountConfiguration { Id = "files", Account = "nobody" }],
        };

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(configuration, "files", TestContext.Current.CancellationToken));

        Assert.Contains("nobody", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountWithoutAServerIsRefused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(Sample(withServer: false), "files", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAccountNamingAProviderThisBuildDoesNotHaveIsRefused()
    {
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(Sample(provider: "owncloud"), "files", TestContext.Current.CancellationToken));

        Assert.Contains("owncloud", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCredentialIsLookedUpByReferenceAndHandedOver()
    {
        RecordingFactory factory = new(ProviderName);
        FakeSecretStore secrets = new(Reference, Credential);

        using IStorageConnection connection = await new AccountConnector(Registry(factory), secrets)
            .ConnectAsync(Sample(), "files", TestContext.Current.CancellationToken);

        Assert.Equal(Reference, Assert.Single(secrets.Asked));
        Assert.Equal(Credential, factory.Settings!.Secret);
    }

    // Plain WebDAV allows a store that is reached without one, and asking a credential store
    // for a name that was never written would be the wrong way to find that out.
    [Fact]
    public async Task AnAccountWithoutAReferenceIsConnectedWithoutACredential()
    {
        RecordingFactory factory = new(ProviderName);
        FakeSecretStore secrets = new(Reference, Credential);

        using IStorageConnection connection = await new AccountConnector(Registry(factory), secrets)
            .ConnectAsync(Sample(secretRef: null), "files", TestContext.Current.CancellationToken);

        Assert.Empty(secrets.Asked);
        Assert.Null(factory.Settings!.Secret);
    }

    // A reference that is there and a store that has nothing under it is a credential that
    // went missing. Connecting anyway would turn that into a 401 much later on.
    [Fact]
    public async Task AReferenceTheStoreCannotSatisfyIsRefused()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AccountConnector(Registry(new RecordingFactory(ProviderName)), new FakeSecretStore())
                .ConnectAsync(Sample(), "files", TestContext.Current.CancellationToken));

        Assert.Contains(Reference, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMountsRemotePathReachesTheProvider()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(remotePath: "/Documents"), "files", TestContext.Current.CancellationToken);

        Assert.Equal("/Documents", factory.Settings!.RemotePath);
    }

    [Fact]
    public async Task TheProgramNamesItselfWithItsNameAndVersion()
    {
        RecordingFactory factory = new(ProviderName);

        using IStorageConnection connection = await Connector(factory)
            .ConnectAsync(Sample(), "files", TestContext.Current.CancellationToken);

        string agent = factory.Settings!.UserAgent!;

        Assert.StartsWith($"{ProductInfo.Name}/", agent, StringComparison.Ordinal);
        Assert.EndsWith(ProductInfo.Version, agent, StringComparison.Ordinal);

        // A product token with a space in it is refused by the transport that sends it, and
        // both halves of this one come out of the assembly rather than a literal.
        Assert.DoesNotContain(" ", agent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfigurationIsRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(null!, "files", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMountHasToBeNamed(string mountId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Connector(new RecordingFactory(ProviderName))
                .ConnectAsync(Sample(), mountId, TestContext.Current.CancellationToken));
    }

    private static ClientConfiguration Sample(
        string provider = ProviderName,
        string? secretRef = Reference,
        string remotePath = MountConfiguration.RootPath,
        bool withServer = true,
        string? loginId = null)
    {
        AccountConfiguration account = new()
        {
            Uuid = s_account,
            Id = "home",
            Server = withServer ? s_server : null,
            Provider = provider,
            UserId = "ernolf",
            LoginId = loginId,
            SecretRef = secretRef,
        };

        MountConfiguration mount = new()
        {
            Id = "files",
            Account = s_account.ToString(),
            RemotePath = remotePath,
        };

        return new() { Accounts = [account], Mounts = [mount] };
    }

    private static ProviderRegistry Registry(IStorageProviderFactory factory) => new([factory]);

    private static AccountConnector Connector(IStorageProviderFactory factory) =>
        new(Registry(factory), new FakeSecretStore(Reference, Credential));

    private sealed class RecordingFactory : IStorageProviderFactory
    {
        public RecordingFactory(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public ProviderSettings? Settings { get; private set; }

        public IStorageConnection Connect(ProviderSettings settings)
        {
            Settings = settings;

            return new FakeConnection();
        }
    }

    private sealed class FakeConnection : IStorageConnection
    {
        // What these tests look at is what arrived at the factory, so nothing ever asks for
        // the provider that would have come back out of it.
        public IStorageProvider Provider => throw new NotSupportedException();

        public void Dispose() => GC.SuppressFinalize(this);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public FakeSecretStore(string? reference = null, string? secret = null)
        {
            if (reference is not null && secret is not null)
            {
                _secrets[reference] = secret;
            }
        }

        public List<string> Asked { get; } = [];

        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
        {
            Asked.Add(reference);

            return Task.FromResult(_secrets.TryGetValue(reference, out string? secret) ? secret : null);
        }

        public Task SetAsync(string reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
