// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;
using WinDav.Core.Configuration;
using WinDav.Providers.Nextcloud;
using WinDav.Providers.WebDav;
using Xunit;

namespace WinDav.Cli.Tests;

public sealed class MountRequestTests
{
    private const string Server = "https://cloud.example.com";

    private static readonly Uri s_server = new(Server);

    [Fact]
    public void AWholeAccountIsNamedAfterTheAccountAndItsServer()
    {
        MountRequest request = Read("--user", "alice");

        Assert.Equal("alice@cloud.example.com", Label(request, "alice"));
        Assert.Equal("\\cloud.example.com\\alice", Prefix(request, "alice"));
        Assert.Equal("/", request.RemotePath);
        Assert.Equal(NextcloudProviderFactory.ProviderName, request.Provider);
        Assert.True(request.NeedsSecret);
        Assert.Null(request.MountPoint);
        Assert.Null(request.Account);
    }

    // Decision 72: what was typed is a login, and the drive is named after the user the store
    // knows, which is the name in the path as well.
    [Fact]
    public void ADriveIsNamedAfterTheUserTheStoreKnows()
    {
        MountRequest request = Read("--user", "alice@example.com");

        Assert.Equal("alice@example.com", request.LoginId);
        Assert.Equal("alice@cloud.example.com", Label(request, "alice"));
        Assert.Equal("\\cloud.example.com\\alice", Prefix(request, "alice"));
    }

    [Fact]
    public void AFolderIsNamedAfterItself()
    {
        MountRequest request = Read("--user", "alice", "--path", "/Documents/Work");

        Assert.Equal("Work", Label(request, "alice"));
        Assert.Equal("\\cloud.example.com\\Work", Prefix(request, "alice"));
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

        Assert.Equal("Work drive", Label(request, "alice"));
        Assert.Equal("X:", request.MountPoint);
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

        Assert.Equal("\\files\\team", Prefix(request, "alice"));
    }

    [Fact]
    public void ANetworkNameNeedsAShare() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--prefix", "\\\\files"));

    [Fact]
    public void ALocalDiskHasNoNetworkName()
    {
        MountRequest request = Read("--user", "alice", "--local");

        Assert.Null(Prefix(request, "alice"));
    }

    [Fact]
    public void ALocalDiskAndANetworkNameAreRefusedTogether() =>
        Assert.Throws<UsageException>(() => Read("--user", "alice", "--local", "--prefix", "\\\\files\\team"));

    [Fact]
    public void AMountWithoutACredentialAsksForNone()
    {
        MountRequest request = Read("--anonymous", "--provider", WebDavProviderFactory.ProviderName);

        Assert.False(request.NeedsSecret);
        Assert.Null(request.LoginId);
        Assert.Equal(WebDavProviderFactory.ProviderName, request.Provider);
        Assert.Equal("cloud.example.com", Label(request, null));
        Assert.Equal($"\\cloud.example.com\\{ProductInfo.Slug}", Prefix(request, null));
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

    // Decision 72: everything about the store is in the account, so nothing about it is typed
    // and nothing about it is asked for.
    [Fact]
    public void AMountIsMadeFromAnAccount()
    {
        MountRequest request = ReadLine("mount", "--account", "home", "--mount", "N:");

        Assert.Equal("home", request.Account);
        Assert.Null(request.Server);
        Assert.Null(request.Provider);
        Assert.Null(request.LoginId);
        Assert.False(request.NeedsSecret);
        Assert.Equal("/", request.RemotePath);
        Assert.Equal("N:", request.MountPoint);
    }

    [Fact]
    public void AnAccountIsNamedByItsUuidJustAsWell()
    {
        MountRequest request = ReadLine("mount", "--account", "46ef72a2-f6ca-4552-a577-ddd9f3afab9a");

        Assert.Equal("46ef72a2-f6ca-4552-a577-ddd9f3afab9a", request.Account);
    }

    [Fact]
    public void AnAccountAndAnAddressAreRefusedTogether() =>
        Assert.Throws<UsageException>(() => ReadLine("mount", Server, "--account", "home"));

    [Theory]
    [InlineData("--provider", "webdav")]
    [InlineData("--user", "alice")]
    public void WhatTheAccountSettlesIsNotTypedNextToIt(string option, string value) =>
        Assert.Throws<UsageException>(() => ReadLine("mount", "--account", "home", option, value));

    [Fact]
    public void AnAccountSaysForItselfWhetherThereIsACredential() =>
        Assert.Throws<UsageException>(() => ReadLine("mount", "--account", "home", "--anonymous"));

    // The mount keeps what is its own: where it appears, what it is called, and how far down
    // the account it reaches.
    [Fact]
    public void AMountOfAnAccountIsNamedAfterWhatItReaches()
    {
        MountRequest whole = ReadLine("mount", "--account", "home");
        MountRequest folder = ReadLine("mount", "--account", "home", "--path", "/Documents");

        Assert.Equal("alice@cloud.example.com", Label(whole, "alice"));
        Assert.Equal("\\cloud.example.com\\alice", Prefix(whole, "alice"));
        Assert.Equal("Documents", Label(folder, "alice"));
        Assert.Equal("/Documents", folder.RemotePath);
    }

    // Decision 73: a first word that carries no scheme is the name of a mount that was written
    // down, and everything that mount is made of stands in the configuration and not here.
    [Fact]
    public void AWordThatIsNotAnAddressIsTheNameOfAMount()
    {
        MountRequest request = ReadLine("mount", "files");

        Assert.Equal("files", request.Stored);
        Assert.Null(request.Account);
        Assert.Null(request.Server);
        Assert.Null(request.Provider);
        Assert.False(request.NeedsSecret);
    }

    [Theory]
    [InlineData("--path", "/Documents")]
    [InlineData("--mount", "N:")]
    [InlineData("--label", "Work drive")]
    [InlineData("--prefix", "\\\\files\\team")]
    [InlineData("--account", "home")]
    public void AMountThatWasWrittenDownTakesNoOptions(string option, string value) =>
        Assert.Throws<UsageException>(() => ReadLine("mount", "files", option, value));

    [Fact]
    public void AMountThatWasWrittenDownTakesNoFlagsEither() =>
        Assert.Throws<UsageException>(() => ReadLine("mount", "files", "--local"));

    // What was written down becomes a request like any other, so that everything past this
    // point treats a stored mount and a typed one alike.
    [Fact]
    public void WhatWasWrittenDownIsWhatIsAskedFor()
    {
        MountRequest request = MountRequest.OfStored(new MountConfiguration
        {
            Id = "files",
            Account = "46ef72a2-f6ca-4552-a577-ddd9f3afab9a",
            RemotePath = "/Documents",
            DriveLetter = "N",
            Label = "Work drive",
            IconPath = @"C:\icons\cloud.ico",
            NetworkPrefix = "\\files\\team",
        });

        Assert.Equal("files", request.Stored);
        Assert.Equal("46ef72a2-f6ca-4552-a577-ddd9f3afab9a", request.Account);
        Assert.Equal("/Documents", request.RemotePath);
        Assert.Equal("N:", request.MountPoint);
        Assert.Equal(@"C:\icons\cloud.ico", request.IconPath);
        Assert.Equal("Work drive", Label(request, "alice"));
        Assert.Equal("\\files\\team", Prefix(request, "alice"));
        Assert.False(request.NeedsSecret);
    }

    [Fact]
    public void AMountThatGoesIntoADirectoryIsAskedForAtThatDirectory()
    {
        MountRequest request = MountRequest.OfStored(new MountConfiguration
        {
            Id = "files",
            Account = "46ef72a2-f6ca-4552-a577-ddd9f3afab9a",
            Directory = @"C:\mnt\cloud",
        });

        Assert.Equal(@"C:\mnt\cloud", request.MountPoint);
    }

    // Nothing written down about where it goes and what it is called is the same as nothing
    // typed: the next free letter, and a name taken from what the mount reaches.
    [Fact]
    public void AStoredMountThatSaysLittleIsNamedAfterWhatItReaches()
    {
        MountRequest request = MountRequest.OfStored(new MountConfiguration
        {
            Id = "files",
            Account = "46ef72a2-f6ca-4552-a577-ddd9f3afab9a",
        });

        Assert.Null(request.MountPoint);
        Assert.Equal("alice@cloud.example.com", Label(request, "alice"));
        Assert.Equal("\\cloud.example.com\\alice", Prefix(request, "alice"));
    }

    [Fact]
    public void AStoredLocalDiskStaysOne()
    {
        MountRequest request = MountRequest.OfStored(new MountConfiguration
        {
            Id = "files",
            Account = "46ef72a2-f6ca-4552-a577-ddd9f3afab9a",
            Local = true,
        });

        Assert.Null(Prefix(request, "alice"));
    }

    private static string Label(MountRequest request, string? userId) => request.LabelFor(s_server, userId);

    private static string? Prefix(MountRequest request, string? userId) => request.PrefixFor(s_server, userId);

    private static MountRequest Read(params string[] options)
    {
        string[] tokens = ["mount", Server, .. options];

        return MountRequest.Parse(CommandLine.Parse(tokens));
    }

    private static MountRequest ReadLine(params string[] tokens) => MountRequest.Parse(CommandLine.Parse(tokens));
}
