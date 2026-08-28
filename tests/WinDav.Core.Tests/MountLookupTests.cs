// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

// Decision 73: a mount is named on the command line, so the rule about what that name reaches
// belongs next to the one for accounts and not in the command that reads it.
public sealed class MountLookupTests
{
    private static readonly Guid s_home = new("0f2a6b41-6c1a-4c0e-9a6f-3b8d5e1c7a90");

    [Fact]
    public void AMountIsFoundByItsName() =>
        Assert.Equal(s_home.ToString(), Sample().FindMount("files")!.Account);

    [Fact]
    public void ANameIsComparedWithoutRegardToCase() =>
        Assert.Equal("files", Sample().FindMount("FILES")!.Id);

    [Fact]
    public void AMountThatIsNotThereIsNotFound() => Assert.Null(Sample().FindMount("photos"));

    // Decision 71: the uuid in a mount is the account's, and nothing points at a mount the way
    // a mount points at an account.
    [Fact]
    public void TheAccountAMountReachesIsNotAWayToNameTheMount() =>
        Assert.Null(Sample().FindMount(s_home.ToString()));

    [Fact]
    public void NothingIsAskedForWithNothing() =>
        Assert.Throws<ArgumentNullException>(() => Sample().FindMount(null!));

    private static ClientConfiguration Sample() => new()
    {
        Accounts =
        [
            new AccountConfiguration
            {
                Uuid = s_home,
                Id = "home",
                Server = new Uri("https://cloud.example.com/"),
                Provider = "webdav",
            },
        ],
        Mounts = [new MountConfiguration { Id = "files", Account = s_home.ToString(), DriveLetter = "N" }],
    };
}
