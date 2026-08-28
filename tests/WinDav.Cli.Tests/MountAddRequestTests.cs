// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace WinDav.Cli.Tests;

// Decision 73: what a mount is given when it is written down is what a mount is given when it
// is typed out, read by the same code and refused in the same places.
public sealed class MountAddRequestTests
{
    [Fact]
    public void AMountKeepsEverythingItWasGiven()
    {
        MountAddRequest request = Read(
            "files",
            "--account",
            "home",
            "--path",
            "/Documents/Work",
            "--mount",
            "N:",
            "--label",
            "Work drive",
            "--prefix",
            "\\\\files\\team");

        Assert.Equal("files", request.Id);
        Assert.Equal("home", request.Account);
        Assert.Equal("/Documents/Work", request.RemotePath);
        Assert.Equal("N", request.DriveLetter);
        Assert.Null(request.Directory);
        Assert.Equal("Work drive", request.Label);
        Assert.Equal("\\files\\team", request.NetworkPrefix);
        Assert.Null(request.IconPath);
        Assert.False(request.Local);
    }

    [Fact]
    public void AMountThatIsWrittenDownNeedsAName() =>
        Assert.Throws<UsageException>(() => Read("--account", "home"));

    [Fact]
    public void AMountThatIsWrittenDownNeedsAnAccount() =>
        Assert.Throws<UsageException>(() => Read("files"));

    [Fact]
    public void OneNameIsWrittenDownAtATime() =>
        Assert.Throws<UsageException>(() => Read("files", "photos", "--account", "home"));

    // The first word after "mount" is read as a verb before anything else, so a mount called
    // after one of them could never be run.
    [Theory]
    [InlineData("add")]
    [InlineData("list")]
    [InlineData("remove")]
    [InlineData("REMOVE")]
    public void ANameThatIsAVerbIsRefused(string id) =>
        Assert.Throws<UsageException>(() => Read(id, "--account", "home"));

    [Fact]
    public void AnAddressIsNotTheNameOfAMount() =>
        Assert.Throws<UsageException>(() => Read("https://cloud.example.com", "--account", "home"));

    // A person writes the place the drive appears at as they would type it, and a letter is
    // told from a directory by what it looks like rather than by an option of its own.
    [Theory]
    [InlineData("N")]
    [InlineData("N:")]
    [InlineData("N:\\")]
    public void ADriveLetterIsToldFromADirectory(string written)
    {
        MountAddRequest request = Read("files", "--account", "home", "--mount", written);

        Assert.Equal("N", request.DriveLetter);
        Assert.Null(request.Directory);
    }

    [Fact]
    public void ADirectoryIsKeptAsAFullPath()
    {
        MountAddRequest request = Read("files", "--account", "home", "--mount", @"C:\mnt\cloud");

        Assert.Equal(@"C:\mnt\cloud", request.Directory);
        Assert.Null(request.DriveLetter);
    }

    // Decision 73: no place at all is the next free letter, which is what the mount takes when
    // it is run.
    [Fact]
    public void AMountWithoutAPlaceTakesNeitherOfTheTwo()
    {
        MountAddRequest request = Read("files", "--account", "home");

        Assert.Null(request.DriveLetter);
        Assert.Null(request.Directory);
        Assert.Equal("/", request.RemotePath);
        Assert.Null(request.Label);
        Assert.Null(request.NetworkPrefix);
    }

    [Fact]
    public void APathIsReadTheWayItIsTyped()
    {
        MountAddRequest request = Read("files", "--account", "home", "--path", "Documents\\Work\\");

        Assert.Equal("/Documents/Work", request.RemotePath);
    }

    [Fact]
    public void ALocalDiskHasNoNetworkName()
    {
        MountAddRequest request = Read("files", "--account", "home", "--local");

        Assert.True(request.Local);
        Assert.Null(request.NetworkPrefix);
    }

    [Fact]
    public void ALocalDiskAndANetworkNameAreRefusedTogether() =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", "--local", "--prefix", "\\\\files\\team"));

    [Fact]
    public void ANetworkNameNeedsAShare() =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", "--prefix", "\\\\files"));

    [Fact]
    public void ALabelWithNothingInItIsRefused() =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", "--label="));

    [Fact]
    public void AnIconThatIsNotThereIsSaidSoAtOnce() =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", "--icon", "no-such-file.ico"));

    // What a mount made from an address is asked, an account has answered already, and there
    // is no mount to write down without one.
    [Theory]
    [InlineData("--user", "alice")]
    [InlineData("--provider", "webdav")]
    public void WhatBelongsToAMountMadeFromAnAddressIsRefused(string option, string value) =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", option, value));

    [Fact]
    public void AnOptionThisCommandHasNoUseForIsRefused() =>
        Assert.Throws<UsageException>(() => Read("files", "--account", "home", "--anonymous"));

    private static MountAddRequest Read(params string[] tokens) =>
        MountAddRequest.Parse(CommandLine.Parse(["mount", "add", .. tokens]));
}
