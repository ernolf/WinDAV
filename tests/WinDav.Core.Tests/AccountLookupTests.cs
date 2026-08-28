// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

// Decision 71: an account answers to the name it is called and to the identity it has. The
// rule is here rather than in the command, because a mount asks the same question.
public sealed class AccountLookupTests
{
    private static readonly Guid s_home = new("0f2a6b41-6c1a-4c0e-9a6f-3b8d5e1c7a90");

    [Fact]
    public void AnAccountIsFoundByItsName() =>
        Assert.Equal(s_home, Sample().FindAccount("home")!.Uuid);

    [Fact]
    public void ANameIsComparedWithoutRegardToCase() =>
        Assert.Equal(s_home, Sample().FindAccount("HOME")!.Uuid);

    // A script holds the uuid because it outlives a renaming, and it writes it in whatever
    // spelling the thing that gave it out used.
    [Theory]
    [InlineData("0f2a6b41-6c1a-4c0e-9a6f-3b8d5e1c7a90")]
    [InlineData("0F2A6B41-6C1A-4C0E-9A6F-3B8D5E1C7A90")]
    [InlineData("0f2a6b416c1a4c0e9a6f3b8d5e1c7a90")]
    [InlineData("{0f2a6b41-6c1a-4c0e-9a6f-3b8d5e1c7a90}")]
    public void AnAccountIsFoundByItsUuidHoweverItIsWritten(string asked) =>
        Assert.Equal("home", Sample().FindAccount(asked)!.Id);

    // Which is why an account may not be named after another one's uuid: the name would win,
    // and the uuid would reach the wrong account.
    [Fact]
    public void TheNameIsLookedAtFirst()
    {
        ClientConfiguration configuration = new()
        {
            Accounts = [Account("home", s_home), Account(s_home.ToString(), Guid.NewGuid())],
        };

        Assert.Equal(s_home.ToString(), configuration.FindAccount(s_home.ToString())!.Id);
    }

    [Fact]
    public void AnAccountThatIsNotThereIsNotFound() => Assert.Null(Sample().FindAccount("work"));

    [Fact]
    public void AUuidNoAccountHasIsNotFound() =>
        Assert.Null(Sample().FindAccount("6b1f0a3d-5c2e-4a91-8d7b-2f4e6a8c0b13"));

    [Fact]
    public void NothingIsAskedForWithNothing() =>
        Assert.Throws<ArgumentNullException>(() => Sample().FindAccount(null!));

    private static ClientConfiguration Sample() => new() { Accounts = [Account("home", s_home)] };

    private static AccountConfiguration Account(string id, Guid uuid) => new()
    {
        Uuid = uuid,
        Id = id,
        Server = new Uri("https://cloud.example.com/"),
        Provider = "webdav",
    };
}
