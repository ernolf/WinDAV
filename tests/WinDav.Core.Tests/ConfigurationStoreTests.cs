// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-tests-{Guid.NewGuid():N}");

    private readonly ConfigurationStore _store;

    public ConfigurationStoreTests()
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
    public async Task AMissingFileReadsAsTheDefaults()
    {
        ClientConfiguration configuration = await _store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClientConfiguration.CurrentVersion, configuration.Version);
        Assert.Empty(configuration.Accounts);
        Assert.Empty(configuration.Mounts);
    }

    [Fact]
    public async Task WhatWasSavedComesBack()
    {
        await _store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        ClientConfiguration read = await _store.LoadAsync(TestContext.Current.CancellationToken);

        AccountConfiguration account = Assert.Single(read.Accounts);
        Assert.Equal("home", account.Id);
        Assert.Equal(new Uri("https://cloud.example.com/"), account.Server);
        Assert.Equal("nextcloud", account.Provider);
        Assert.Equal("ernolf", account.UserId);

        MountConfiguration mount = Assert.Single(read.Mounts);
        Assert.Equal("files", mount.Id);
        Assert.Equal("home", mount.Account);
        Assert.Equal("N", mount.DriveLetter);
        Assert.Null(mount.Directory);
        Assert.False(mount.ReadOnly);
    }

    [Fact]
    public async Task TheFileIsWrittenInTheSpellingItIsReadIn()
    {
        await _store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        string text = await File.ReadAllTextAsync(_store.FilePath, TestContext.Current.CancellationToken);

        Assert.Contains("\"driveLetter\"", text, StringComparison.Ordinal);
        Assert.Contains("\"remotePath\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DriveLetter\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingCreatesTheDirectory()
    {
        Assert.False(Directory.Exists(_directory));

        await _store.SaveAsync(new ClientConfiguration(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task NothingIsLeftBehindNextToTheFile()
    {
        await _store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Single(Directory.GetFiles(_directory));
        Assert.True(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task TextThatIsNotJsonIsRefused()
    {
        await WriteRawAsync("{ this is not json");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains(_store.FilePath, exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task NullInsteadOfAConfigurationIsRefused()
    {
        await WriteRawAsync("null");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains(_store.FilePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileFromANewerBuildIsRefusedAndSaysSo()
    {
        await WriteRawAsync("{ \"version\": 99 }");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("99", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            ClientConfiguration.CurrentVersion.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSaveLeavesThePreviousFileAlone()
    {
        await _store.SaveAsync(Sample(), TestContext.Current.CancellationToken);
        string before = await File.ReadAllTextAsync(_store.FilePath, TestContext.Current.CancellationToken);

        ClientConfiguration broken = new() { Mounts = [new MountConfiguration { Id = "orphan", Account = "nobody" }] };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.SaveAsync(broken, TestContext.Current.CancellationToken));

        string after = await File.ReadAllTextAsync(_store.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
    }

    // The promise that a configuration file can be copied or attached to a report is only
    // worth something if there is nowhere for a credential to sit in the first place.
    [Fact]
    public void AnAccountHasNowhereToPutACredential()
    {
        string[] suspicious = ["password", "secret", "token", "credential", "passphrase"];

        foreach (PropertyInfo property in typeof(AccountConfiguration).GetProperties())
        {
            if (string.Equals(property.Name, nameof(AccountConfiguration.SecretRef), StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string word in suspicious)
            {
                Assert.False(
                    property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"AccountConfiguration.{property.Name} looks like somewhere a credential would end up.");
            }
        }
    }

    private static ClientConfiguration Sample() => new()
    {
        Accounts =
        [
            new AccountConfiguration
            {
                Id = "home",
                Server = new Uri("https://cloud.example.com/"),
                Provider = "nextcloud",
                UserId = "ernolf",
                SecretRef = "home",
            },
        ],
        Mounts =
        [
            new MountConfiguration
            {
                Id = "files",
                Account = "home",
                DriveLetter = "N",
            },
        ],
    };

    private async Task WriteRawAsync(string text)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(_store.FilePath, text, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }
}
