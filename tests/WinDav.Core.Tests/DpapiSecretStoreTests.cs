// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using System.Text;
using WinDav.Core.Security;
using Xunit;

namespace WinDav.Core.Tests;

// The store is the one Windows-only type in the project, and so is what tests it.
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-tests-{Guid.NewGuid():N}");

    private readonly DpapiSecretStore _store;

    public DpapiSecretStoreTests()
    {
        _store = new DpapiSecretStore(_directory);
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
    public async Task WhatWasStoredComesBack()
    {
        await _store.SetAsync("home@cloud.example.com", "an app password", TestContext.Current.CancellationToken);

        Assert.Equal(
            "an app password",
            await _store.GetAsync("home@cloud.example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANameNothingWasStoredUnderReadsAsNothing() =>
        Assert.Null(await _store.GetAsync("nobody", TestContext.Current.CancellationToken));

    [Fact]
    public async Task TheDirectoryIsMadeOnTheFirstWrite()
    {
        Assert.False(Directory.Exists(_directory));

        await _store.SetAsync("home", "secret", TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public async Task WhatIsOnDiskIsNotTheCredential()
    {
        await _store.SetAsync("home", "an app password", TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(
            Path.Combine(_directory, "home.bin"),
            TestContext.Current.CancellationToken);

        // Latin1 rather than UTF-8: it turns every byte into a character, so anything that
        // was left in the clear shows up instead of being dropped as invalid.
        Assert.DoesNotContain("an app password", Encoding.Latin1.GetString(written), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoringTwiceKeepsTheSecond()
    {
        await _store.SetAsync("home", "the first", TestContext.Current.CancellationToken);
        await _store.SetAsync("home", "the second", TestContext.Current.CancellationToken);

        Assert.Equal("the second", await _store.GetAsync("home", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingTakesItAway()
    {
        await _store.SetAsync("home", "secret", TestContext.Current.CancellationToken);
        await _store.RemoveAsync("home", TestContext.Current.CancellationToken);

        Assert.Null(await _store.GetAsync("home", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingWhatIsNotThereIsNotAFailure() =>
        await _store.RemoveAsync("nobody", TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has:colon")]
    [InlineData("")]
    public async Task ANameThatCannotBeAFileNameIsRefused(string reference)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SetAsync(reference, "secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyCredentialIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SetAsync("home", string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFileThatIsNotACredentialIsSaidToBeUnopenable()
    {
        Directory.CreateDirectory(_directory);

        await File.WriteAllBytesAsync(
            Path.Combine(_directory, "home.bin"),
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.GetAsync("home", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheDefaultStoreIsBelowTheLocalDataDirectory()
    {
        Assert.Equal(
            Path.Combine(ProductInfo.LocalDataDirectory, DpapiSecretStore.DirectoryName),
            DpapiSecretStore.Default().DirectoryPath);
    }
}
