// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogAreaTests
{
    [Theory]
    [InlineData("WinDav.Fs.WinDavFileSystem", LogArea.Fs)]
    [InlineData("WinDav.Fs.ProviderMount", LogArea.Fs)]
    [InlineData("WinDav.Dav.DavClient", LogArea.Http)]
    [InlineData("WinDav.Providers.Nextcloud.NextcloudProvider", LogArea.Provider)]
    [InlineData("WinDav.Core.Providers.AccountConnector", LogArea.Provider)]
    public void ANamespaceDecidesTheArea(string category, LogArea expected) =>
        Assert.Equal(expected, LogAreas.Of(category));

    [Theory]
    [InlineData("WinDav.Cli.Program")]
    [InlineData("WinDav.Core.Configuration.ConfigurationStore")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("")]
    public void EverythingElseIsTheCommand(string category) =>
        Assert.Equal(LogArea.Cli, LogAreas.Of(category));

    // A record that belongs to none of the areas belongs to the command, and that has to hold
    // for a value nobody set as much as for one that was worked out from a category.
    [Fact]
    public void TheFallbackIsTheDefaultValue() => Assert.Equal(LogArea.Cli, default);

    [Theory]
    [InlineData(LogArea.Cli, "cli")]
    [InlineData(LogArea.Fs, "fs")]
    [InlineData(LogArea.Http, "http")]
    [InlineData(LogArea.Provider, "provider")]
    public void EachAreaIsWrittenInLowerCase(LogArea area, string expected) =>
        Assert.Equal(expected, LogAreas.Name(area));
}
