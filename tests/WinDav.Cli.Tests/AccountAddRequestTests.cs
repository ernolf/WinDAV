// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace WinDav.Cli.Tests;

public sealed class AccountAddRequestTests
{
    [Fact]
    public void TheServerIsWhatWasTypedAndTheProviderIsNextcloud()
    {
        AccountAddRequest request = Parse("account", "add", "https://cloud.example.com/");

        Assert.Equal(new Uri("https://cloud.example.com/"), request.Server);
        Assert.Equal("nextcloud", request.Provider);
        Assert.Null(request.Id);
        Assert.Null(request.LoginId);
        Assert.False(request.Anonymous);
    }

    [Fact]
    public void AUserThatWasGivenIsKept()
    {
        AccountAddRequest request = Parse("account", "add", "https://cloud.example.com/", "--user", "ernolf");

        Assert.Equal("ernolf", request.LoginId);
    }

    // Decision 71: what is typed here is the name a password was issued for, and a server that
    // lets one user in under several names takes it under that one only.
    [Fact]
    public void AUserCanBeAnAddressTheServerLetsThatUserInUnder()
    {
        AccountAddRequest request = Parse(
            "account", "add", "https://cloud.example.com/", "--user", "ernolf@example.com");

        Assert.Equal("ernolf@example.com", request.LoginId);
    }

    [Fact]
    public void AnIdThatWasGivenIsKept()
    {
        AccountAddRequest request = Parse("account", "add", "https://cloud.example.com/", "--id", "home");

        Assert.Equal("home", request.Id);
    }

    [Fact]
    public void AnAnonymousAccountNeedsAProviderThatHasNoUsers()
    {
        AccountAddRequest request = Parse(
            "account", "add", "https://dav.example.com/", "--provider", "webdav", "--anonymous");

        Assert.True(request.Anonymous);
        Assert.Equal("webdav", request.Provider);
    }

    [Fact]
    public void AWebDavStoreIsReachedAsAUser() =>
        Assert.Throws<UsageException>(
            () => Parse("account", "add", "https://dav.example.com/", "--provider", "webdav"));

    [Fact]
    public void ANextcloudAccountIsNotAnonymous() =>
        Assert.Throws<UsageException>(
            () => Parse("account", "add", "https://cloud.example.com/", "--anonymous"));

    [Fact]
    public void AUserAndAnonymousTogetherAreRefused()
    {
        Assert.Throws<UsageException>(
            () => Parse("account", "add", "https://dav.example.com/", "--provider", "webdav", "--user", "ernolf", "--anonymous"));
    }

    [Fact]
    public void AnAddressIsNeeded() =>
        Assert.Throws<UsageException>(() => Parse("account", "add"));

    [Fact]
    public void OneAddressAndNoMore()
    {
        Assert.Throws<UsageException>(
            () => Parse("account", "add", "https://cloud.example.com/", "https://other.example.com/"));
    }

    [Theory]
    [InlineData("cloud.example.com")]
    [InlineData("ftp://cloud.example.com/")]
    [InlineData("/srv/dav")]
    public void WhatIsNotAnHttpAddressIsRefused(string address) =>
        Assert.Throws<UsageException>(() => Parse("account", "add", address));

    [Fact]
    public void AnOptionThisCommandHasNoUseForIsRefused() =>
        Assert.Throws<UsageException>(
            () => Parse("account", "add", "https://cloud.example.com/", "--mount", "N:"));

    [Fact]
    public void AnAccountIsCalledAfterItsLoginAndItsServer()
    {
        Assert.Equal(
            "ernolf@cloud.example.com",
            AccountAddRequest.DeriveId(new Uri("https://cloud.example.com/"), "ernolf"));
    }

    // Decision 71: the login is what tells one door into an account from the other, so it is
    // the login that the name is built from, address and all.
    [Fact]
    public void AnAccountReachedUnderAnAddressIsCalledAfterThatAddress()
    {
        Assert.Equal(
            "ernolf@example.com@cloud.example.com",
            AccountAddRequest.DeriveId(new Uri("https://cloud.example.com/"), "ernolf@example.com"));
    }

    [Fact]
    public void AnAccountWithoutAUserIsCalledAfterItsServer() =>
        Assert.Equal("dav.example.com", AccountAddRequest.DeriveId(new Uri("https://dav.example.com/"), null));

    private static AccountAddRequest Parse(params string[] tokens) =>
        AccountAddRequest.Parse(CommandLine.Parse(tokens));
}
