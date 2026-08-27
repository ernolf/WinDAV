// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;
using WinDav.Providers.Nextcloud;
using WinDav.Providers.WebDav;
using Xunit;

namespace WinDav.Cli.Tests;

public sealed class MountRequestTests
{
    private const string Server = "https://cloud.example.com";

    [Fact]
    public void AWholeAccountIsNamedAfterTheAccountAndItsServer()
    {
        MountRequest request = Read("--user", "alice");

        Assert.Equal("alice@cloud.example.com", request.Label);
        Assert.Equal("\\cloud.example.com\\alice", request.NetworkPrefix);
        Assert.Equal("/", request.RemotePath);
        Assert.Equal(NextcloudProviderFactory.ProviderName, request.Provider);
        Assert.True(request.NeedsSecret);
        Assert.Null(request.MountPoint);
    }

    [Fact]
    public void AFolderIsNamedAfterItself()
    {
        MountRequest request = Read("--user", "alice", "--path", "/Documents/Work");

        Assert.Equal("Work", request.Label);
        Assert.Equal("\\cloud.example.com\\Work", request.NetworkPrefix);
    }

    [Fact]
    public void APathIsReadTheWayItIsTyped()
    {
        MountRequest request = Read("--user", "alice", "--path", "Documents\\Work\\");

        Assert.Equal("/Documents/Work", request.RemotePath);
    }

    [Fact]
    public void ANameThatWasGivenIsTheNameThatIsUsed()
    {
        MountRequest request = Read("--user", "alice", "--label", "Work drive", "--mount", "X:");

        Assert.Equal("Work drive", request.Label);
        Assert.Equal("X:", request.MountPoint);
    }

    // The two names are separate things, and the one Explorer shows follows the one the
    // volume answers with until somebody says otherwise.
    [Fact]
    public void WhatExplorerShowsIsTheLabelUnlessItIsGivenItsOwnName()
    {
        Assert.Equal("alice@cloud.example.com", Read("--user", "alice").ExplorerName);
        Assert.Equal("Work drive", Read("--user", "alice", "--label", "Work drive").ExplorerName);
        Assert.Equal("Work", Read("--user", "alice", "--label", "Work drive", "--name", "Work").ExplorerName);
    }

    [Fact]
    public void WithoutAnIconThereIsNoneToWrite() => Assert.Null(Read("--user", "alice").IconPath);

    // The registry keeps the path and is read again long after the directory the command ran
    // in has stopped mattering.
    [Fact]
    public void AnIconIsKeptAsAFullPath()
    {
        // Written where the command would be run, so that the bare name is one that resolves.
        string name = $"windav-tests-{Guid.NewGuid():N}.ico";

        File.WriteAllBytes(name, []);

        try
        {
            MountRequest request = Read("--user", "alice", "--icon", name);

            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), name), request.IconPath);
        }
        finally
        {
            File.Delete(name);
        }
    }

    [Fact]
    public void AnIconThatIsNotThereIsSaidSoAtOnce() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--icon", "no-such-file.ico"));

    [Fact]
    public void ANetworkNameIsKeptInTheFormAMountCarriesIt()
    {
        MountRequest request = Read("--user", "alice", "--prefix", "\\\\files\\team");

        Assert.Equal("\\files\\team", request.NetworkPrefix);
    }

    [Fact]
    public void ANetworkNameNeedsAShare() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--prefix", "\\\\files"));

    [Fact]
    public void ALocalDiskHasNoNetworkName()
    {
        MountRequest request = Read("--user", "alice", "--local");

        Assert.Null(request.NetworkPrefix);
    }

    [Fact]
    public void ALocalDiskAndANetworkNameAreRefusedTogether() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--local", "--prefix", "\\\\files\\team"));

    [Fact]
    public void AMountWithoutACredentialAsksForNone()
    {
        MountRequest request = Read("--anonymous", "--provider", WebDavProviderFactory.ProviderName);

        Assert.False(request.NeedsSecret);
        Assert.Null(request.UserId);
        Assert.Equal(WebDavProviderFactory.ProviderName, request.Provider);
        Assert.Equal("cloud.example.com", request.Label);
        Assert.Equal($"\\cloud.example.com\\{ProductInfo.Slug}", request.NetworkPrefix);
    }

    [Fact]
    public void AMountIsMadeEitherAsAUserOrAnonymously() =>
        Assert.Throws<UsageException>(() => Read("--anonymous", "--user", "alice"));

    [Fact]
    public void AMountSaysWhoItIsFor() =>
        Assert.Throws<UsageException>(() => Read());

    [Fact]
    public void TheAddressHasToBeOneABrowserWouldTake() =>
        Assert.Throws<UsageException>(() => ReadLine("mount", "ftp://files.example.com", "--anonymous"));

    [Fact]
    public void AnOptionMountHasNoUseForIsRefused() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--colour", "red"));

    private static MountRequest Read(params string[] options)
    {
        string[] tokens = ["mount", Server, .. options];

        return MountRequest.Parse(CommandLine.Parse(tokens));
    }

    private static MountRequest ReadLine(params string[] tokens) => MountRequest.Parse(CommandLine.Parse(tokens));
}
