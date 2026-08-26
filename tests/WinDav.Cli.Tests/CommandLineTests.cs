// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace WinDav.Cli.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void TheFirstWordIsTheCommand()
    {
        CommandLine line = Parse("mount", "https://cloud.example.com");

        Assert.Equal("mount", line.Verb);
        Assert.Equal("https://cloud.example.com", line.SingleArgument("an address"));
    }

    [Fact]
    public void AnOptionTakesTheWordAfterIt()
    {
        CommandLine line = Parse("mount", "https://cloud.example.com", "--user", "alice");

        Assert.Equal("alice", line.Value("--user"));
    }

    [Fact]
    public void AnOptionTakesWhatFollowsItsEqualsSign()
    {
        CommandLine line = Parse("mount", "https://cloud.example.com", "--user=alice");

        Assert.Equal("alice", line.Value("--user"));
    }

    [Fact]
    public void AValueKeepsTheEqualsSignsInsideIt()
    {
        // Passwords are not read from the command line, but a path or a label may well carry
        // one, and only the first sign separates a name from a value.
        CommandLine line = Parse("mount", "https://cloud.example.com", "--label=a=b");

        Assert.Equal("a=b", line.Value("--label"));
    }

    [Fact]
    public void AnOptionInFrontOfAnotherOneStandsForItself()
    {
        CommandLine line = Parse("mount", "https://cloud.example.com", "--anonymous", "--path", "/Documents");

        Assert.True(line.Flag("--anonymous"));
        Assert.Equal("/Documents", line.Value("--path"));
    }

    [Fact]
    public void WhatWasNotGivenIsNothing()
    {
        CommandLine line = Parse("mount", "https://cloud.example.com");

        Assert.Null(line.Value("--label"));
        Assert.False(line.Flag("--local"));
    }

    [Fact]
    public void AFlagWithAValueIsRefused()
    {
        // How "mount --anonymous https://cloud.example.com" arrives: the address is read as
        // the value of the flag, and saying so is more use than a missing address.
        CommandLine line = Parse("mount", "--anonymous", "https://cloud.example.com");

        UsageException refused = Assert.Throws<UsageException>(() => line.Flag("--anonymous"));

        Assert.Contains("https://cloud.example.com", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionWithoutAValueIsRefused() =>
        Assert.Throws<UsageException>(() => Parse("mount", "https://cloud.example.com", "--user").Value("--user"));

    [Fact]
    public void AnOptionGivenTwiceIsRefused() =>
        Assert.Throws<UsageException>(
            () => Parse("mount", "https://cloud.example.com", "--user", "alice", "--user", "bob"));

    [Fact]
    public void AnOptionWithoutANameIsRefused() =>
        Assert.Throws<UsageException>(() => Parse("mount", "--", "https://cloud.example.com"));

    [Fact]
    public void AnOptionTheCommandHasNoUseForIsRefused() =>
        Assert.Throws<UsageException>(
            () => Parse("mount", "https://cloud.example.com", "--colour", "red").EnsureOnlyKnown(["--user"]));

    [Fact]
    public void NothingToActOnIsRefused() =>
        Assert.Throws<UsageException>(() => Parse("mount").SingleArgument("an address"));

    [Fact]
    public void MoreThanOneThingToActOnIsRefused() =>
        Assert.Throws<UsageException>(
            () => Parse("mount", "https://cloud.example.com", "https://other.example.com")
                .SingleArgument("an address"));

    [Fact]
    public void AnEmptyCommandLineAsksForNothing()
    {
        CommandLine line = Parse();

        Assert.Null(line.Verb);
        Assert.False(line.Flag("--version"));
    }

    private static CommandLine Parse(params string[] tokens) => CommandLine.Parse(tokens);
}
