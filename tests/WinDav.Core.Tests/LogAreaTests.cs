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

    // The names a person types on the command line are the names they read in the file. One
    // spelling, learnt once, and not one to get right twice.
    [Theory]
    [InlineData("fs", LogArea.Fs)]
    [InlineData("FS", LogArea.Fs)]
    [InlineData("Http", LogArea.Http)]
    [InlineData("provider", LogArea.Provider)]
    [InlineData("cli", LogArea.Cli)]
    public void AnAreaIsReadFromItsNameInAnyCase(string name, LogArea expected)
    {
        Assert.True(LogAreas.TryParse(name, out LogArea area));
        Assert.Equal(expected, area);
    }

    // "all" among them: what it stands for is decided where the command line is read, and an
    // area is one of the four or nothing.
    [Theory]
    [InlineData("dav")]
    [InlineData("all")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameThatIsNoAreaIsNone(string? name) => Assert.False(LogAreas.TryParse(name, out _));

    // What a recording that names no area covers, and what the message for a name that is no
    // area lists.
    [Fact]
    public void EveryAreaIsInTheListAndOnlyOnce()
    {
        Assert.Equal(Enum.GetValues<LogArea>().Length, LogAreas.All.Count);
        Assert.Equal(LogAreas.All.Count, LogAreas.All.Distinct().Count());
    }
}
