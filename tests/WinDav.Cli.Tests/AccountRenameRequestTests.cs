// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Xunit;

namespace WinDav.Cli.Tests;

public sealed class AccountRenameRequestTests
{
    [Fact]
    public void TheAccountAndTheNameItIsToHaveAreWhatWasTyped()
    {
        AccountRenameRequest request = Parse("account", "rename", "ernolf@cloud.example.com", "work");

        Assert.Equal("ernolf@cloud.example.com", request.Account);
        Assert.Equal("work", request.Id);
    }

    // Decision 71: the uuid is what a script holds on to, being the one name that outlives a
    // rename, so it is a way of saying which account is to be renamed.
    [Fact]
    public void TheAccountCanBeNamedByItsUuid()
    {
        string uuid = Guid.NewGuid().ToString();

        Assert.Equal(uuid, Parse("account", "rename", uuid, "work").Account);
    }

    [Fact]
    public void ARenameNeedsAnAccountAndAName()
    {
        Assert.Throws<UsageException>(() => Parse("account", "rename"));
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home"));
    }

    [Fact]
    public void OneNameIsGivenAtATime() =>
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home", "work", "spare"));

    [Fact]
    public void ARenameTakesNoOptions() =>
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home", "work", "--provider", "webdav"));

    [Fact]
    public void ANameIsSomethingRatherThanNothing()
    {
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home", string.Empty));
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home", "   "));
    }

    // An account is looked up by name first, so a name that reads as a uuid would stand in
    // front of the identity it spells.
    [Fact]
    public void ANameThatReadsAsAUuidIsRefused()
    {
        Assert.Throws<UsageException>(() => Parse("account", "rename", "home", Guid.NewGuid().ToString()));
        Assert.Throws<UsageException>(
            () => Parse("account", "rename", "home", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)));
    }

    private static AccountRenameRequest Parse(params string[] tokens) =>
        AccountRenameRequest.Parse(CommandLine.Parse(tokens));
}
