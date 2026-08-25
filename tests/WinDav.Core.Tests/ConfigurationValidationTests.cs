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
    public async Task AnAccountNeedsAnId()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [new AccountConfiguration { Server = new Uri("https://cloud.example.com/"), Provider = "webdav" }],
        });

        Assert.Contains("accounts[0] has no id", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNeedsAServer()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [new AccountConfiguration { Id = "home", Provider = "webdav" }],
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
            Accounts = [new AccountConfiguration { Id = "home", Server = new Uri("https://cloud.example.com/") }],
        });

        Assert.Contains("names no provider", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountCannotNameAnAccountThatIsNotThere()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts = [Mount("files", "work")],
        });

        Assert.Contains("names the account 'work', which does not exist", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoMountsCannotShareAnId()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts = [Mount("files", "home"), Mount("files", "home")],
        });

        Assert.Contains("repeats the id 'files'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountTakesEitherALetterOrADirectory()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts =
            [
                new MountConfiguration
                {
                    Id = "files",
                    Account = "home",
                    DriveLetter = "N",
                    Directory = @"C:\mnt\cloud",
                },
            ],
        });

        Assert.Contains("it can have one", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountWithNoPlaceToGoIsRefused()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts = [new MountConfiguration { Id = "files", Account = "home" }],
        });

        Assert.Contains("neither a drive letter nor a directory", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriveLetterIsOneLetter()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts = [new MountConfiguration { Id = "files", Account = "home", DriveLetter = "N:" }],
        });

        Assert.Contains("not a single letter", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemotePathStartsAtTheRoot()
    {
        string message = await RejectedAsync(new ClientConfiguration
        {
            Accounts = [Account("home")],
            Mounts =
            [
                new MountConfiguration
                {
                    Id = "files",
                    Account = "home",
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
            Mounts = [new MountConfiguration { Id = "files", Account = "work" }],
        });

        Assert.Contains("has no server", message, StringComparison.Ordinal);
        Assert.Contains("names no provider", message, StringComparison.Ordinal);
        Assert.Contains("which does not exist", message, StringComparison.Ordinal);
        Assert.Contains("neither a drive letter nor a directory", message, StringComparison.Ordinal);
    }

    private static AccountConfiguration Account(string id) => new()
    {
        Id = id,
        Server = new Uri("https://cloud.example.com/"),
        Provider = "webdav",
    };

    private static MountConfiguration Mount(string id, string account) => new()
    {
        Id = id,
        Account = account,
        DriveLetter = "N",
    };

    private async Task<string> RejectedAsync(ClientConfiguration configuration)
    {
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.SaveAsync(configuration, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        return exception.Message;
    }
}
