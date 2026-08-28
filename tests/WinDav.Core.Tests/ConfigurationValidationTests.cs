// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

// Checked through SaveAsync rather than against the validator directly: what matters is
// that a configuration nobody can act on never reaches the disk.
public sealed class ConfigurationValidationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-tests-{Guid.NewGuid():N}");

    private readonly ConfigurationStore _store;

    public ConfigurationValidationTests()
    {
        _store = new ConfigurationStore(Path.Combine(_directory, ConfigurationStore.FileName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TwoAccountsCannotShareAnIdEvenSpeltDifferently()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home"), Account("HOME")],
        });

        Assert.Contains("repeats the id", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNeedsAUuid()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts =
            [
                new AccountConfiguration
                {
                    Id = "home",
                    Server = new Uri("https://cloud.example.com/"),
                    Provider = "webdav",
                },
            ],
        });

        Assert.Contains("accounts[0] has no uuid", message, StringComparison.Ordinal);
    }

    // Two accounts a rename cannot tell apart, which is what a hand-copied entry leaves behind.
    [Fact]
    public async Task TwoAccountsCannotShareAUuid()
    {
        AccountConfiguration home = Account("home");
        AccountConfiguration copied = new()
        {
            Uuid = home.Uuid,
            Id = "work",
            Server = home.Server,
            Provider = home.Provider,
        };

        string message = await RejectedAsync(new ClientConfiguration { Accounts = [home, copied] });

        Assert.Contains($"repeats the uuid '{home.Uuid}'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNeedsAnId()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts =
            [
                new AccountConfiguration
                {
                    Uuid = Guid.NewGuid(),
                    Server = new Uri("https://cloud.example.com/"),
                    Provider = "webdav",
                },
            ],
        });

        Assert.Contains("accounts[0] has no id", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNeedsAServer()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [new AccountConfiguration { Uuid = Guid.NewGuid(), Id = "home", Provider = "webdav" }],
        });

        Assert.Contains("has no server", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerAddressHasToBeAbsolute()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts =
            [
                new AccountConfiguration
                {
                    Uuid = Guid.NewGuid(),
                    Id = "home",
                    Server = new Uri("cloud.example.com", UriKind.Relative),
                    Provider = "webdav",
                },
            ],
        });

        Assert.Contains("relative server address", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerAddressHasToSpeakHttp()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts =
            [
                new AccountConfiguration
                {
                    Uuid = Guid.NewGuid(),
                    Id = "home",
                    Server = new Uri("ftp://cloud.example.com/"),
                    Provider = "webdav",
                },
            ],
        });

        Assert.Contains("scheme 'ftp'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNeedsAProvider()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts =
            [
                new AccountConfiguration
                {
                    Uuid = Guid.NewGuid(),
                    Id = "home",
                    Server = new Uri("https://cloud.example.com/"),
                },
            ],
        });

        Assert.Contains("names no provider", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountCannotNameAnAccountThatIsNotThere()
    {
        AccountConfiguration stranger = Account("work");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts = [Mount("files", stranger)],
        });

        Assert.Contains(
            $"names the account '{stranger.Uuid}', which does not exist",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoMountsCannotShareAnId()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts = [Mount("files", home), Mount("files", home)],
        });

        Assert.Contains("repeats the id 'files'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountTakesEitherALetterOrADirectory()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts =
            [
                new MountConfiguration
                {
                    Id = "files",
                    Account = home.Uuid.ToString(),
                    DriveLetter = "N",
                    Directory = @"C:\mnt\cloud",
                },
            ],
        });

        Assert.Contains("it can have one", message, StringComparison.Ordinal);
    }

    // Decision 73: neither of the two is not a mount without a place to go, it is a mount that
    // takes the next free letter, which is what one asked for without --mount does as well.
    [Fact]
    public async Task AMountWithNoPlaceToGoTakesTheNextFreeLetter()
    {
        AccountConfiguration home = Account("home");

        await _store.SaveAsync(
            new ClientConfiguration
            {
                Accounts = [home],
                Mounts = [new MountConfiguration { Id = "files", Account = home.Uuid.ToString() }],
            },
            TestContext.Current.CancellationToken);

        ClientConfiguration written = await _store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(written.Mounts[0].DriveLetter);
        Assert.Null(written.Mounts[0].Directory);
    }

    [Fact]
    public async Task ALocalDiskHasNoNetworkName()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts =
            [
                new MountConfiguration
                {
                    Id = "files",
                    Account = home.Uuid.ToString(),
                    Local = true,
                    NetworkPrefix = @"\files\team",
                },
            ],
        });

        Assert.Contains("appears as a local disk and has a network name", message, StringComparison.Ordinal);
    }

    // The form a mount carries it in, which is the one a person writes with one backslash
    // fewer. A name that is not of that form reaches Windows and is turned down there.
    [Theory]
    [InlineData("files")]
    [InlineData(@"files\team")]
    [InlineData(@"\files")]
    [InlineData(@"\\files\team")]
    public async Task ANetworkNameIsWrittenAsServerAndShare(string prefix)
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts =
            [
                new MountConfiguration { Id = "files", Account = home.Uuid.ToString(), NetworkPrefix = prefix },
            ],
        });

        Assert.Contains($"has the network name '{prefix}'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALabelWithNothingInItIsRefused()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts = [new MountConfiguration { Id = "files", Account = home.Uuid.ToString(), Label = "  " }],
        });

        Assert.Contains("has an empty label", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriveLetterIsOneLetter()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts = [new MountConfiguration { Id = "files", Account = home.Uuid.ToString(), DriveLetter = "N:" }],
        });

        Assert.Contains("not a single letter", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemotePathStartsAtTheRoot()
    {
        AccountConfiguration home = Account("home");

        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [home],
            Mounts =
            [
                new MountConfiguration
                {
                    Id = "files",
                    Account = home.Uuid.ToString(),
                    DriveLetter = "N",
                    RemotePath = "documents",
                },
            ],
        });

        Assert.Contains("does not start with a slash", message, StringComparison.Ordinal);
    }

    // A person editing the file by hand should not have to run the program once per typo.
    [Fact]
    public async Task EveryProblemIsReportedAtOnce()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [new AccountConfiguration { Id = "home" }],
            Mounts = [new MountConfiguration { Id = "files", Account = "work", Label = " ", NetworkPrefix = "team" }],
        });

        Assert.Contains("has no server", message, StringComparison.Ordinal);
        Assert.Contains("names no provider", message, StringComparison.Ordinal);
        Assert.Contains("which does not exist", message, StringComparison.Ordinal);
        Assert.Contains("has an empty label", message, StringComparison.Ordinal);
        Assert.Contains("which is not \\server\\share", message, StringComparison.Ordinal);
    }

    private static AccountConfiguration Account(string id) => new()
    {
        Uuid = Guid.NewGuid(),
        Id = id,
        Server = new Uri("https://cloud.example.com/"),
        Provider = "webdav",
    };

    // Named by what the account is rather than by what it is called: decisions.md 71.
    private static MountConfiguration Mount(string id, AccountConfiguration account) => new()
    {
        Id = id,
        Account = account.Uuid.ToString(),
        DriveLetter = "N",
    };

    private async Task<string> RejectedAsync(ClientConfiguration configuration)
    {
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.SaveAsync(configuration, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        return exception.Message;
    }
}
