// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void AFactoryIsFoundUnderItsName()
    {
        StubFactory webdav = new("webdav");
        ProviderRegistry registry = new([webdav, new StubFactory("nextcloud")]);

        Assert.Same(webdav, registry.Resolve("webdav"));
    }

    [Theory]
    [InlineData("Nextcloud")]
    [InlineData("NEXTCLOUD")]
    public void TheNameIsMatchedWithoutRegardToCase(string name)
    {
        StubFactory nextcloud = new("nextcloud");
        ProviderRegistry registry = new([nextcloud]);

        Assert.Same(nextcloud, registry.Resolve(name));
    }

    [Fact]
    public void TheNamesAreListedInOrderWhateverOrderTheyArrivedIn()
    {
        ProviderRegistry registry = new([new StubFactory("webdav"), new StubFactory("nextcloud")]);
        string[] expected = ["nextcloud", "webdav"];

        Assert.Equal(expected, registry.Names);
    }

    [Fact]
    public void ARegistryWithNothingInItHasNoNames()
    {
        ProviderRegistry registry = new([]);

        Assert.Empty(registry.Names);
    }

    [Fact]
    public void AnUnknownNameIsRefusedAndSaysWhatThereIs()
    {
        ProviderRegistry registry = new([new StubFactory("webdav"), new StubFactory("nextcloud")]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => registry.Resolve("owncloud"));

        Assert.Contains("owncloud", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nextcloud, webdav", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameThatIsBlankIsRefused(string name)
    {
        ProviderRegistry registry = new([new StubFactory("webdav")]);

        Assert.Throws<ArgumentException>(() => registry.Resolve(name));
    }

    // Which of the two would answer is decided by the order they were handed over in, and
    // that is no way to decide where a mount connects to.
    [Fact]
    public void TwoFactoriesUnderTheSameNameAreRefused()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new ProviderRegistry([new StubFactory("webdav"), new StubFactory("WebDav")]));

        Assert.Contains("WebDav", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryThatIsNotThereIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new ProviderRegistry([null!]));

    // Nothing here connects: what a registry does is look up, and what it looks up is never
    // asked to build anything.
    private sealed class StubFactory : IStorageProviderFactory
    {
        public StubFactory(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IStorageConnection Connect(ProviderSettings settings) => throw new NotSupportedException();
    }
}
