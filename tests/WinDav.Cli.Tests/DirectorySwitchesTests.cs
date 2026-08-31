// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Cli.Tests;

// The three options of the listings, read the way the three of the read path are read: the
// command line first, the environment behind it, and off among the values each of them takes.
public sealed class DirectorySwitchesTests
{
    [Fact]
    public void NothingAskedForIsWhatWasMeasured()
    {
        DirectorySettings settings = Read("mount", "cloud");

        Assert.Equal(DirectorySettings.DefaultDepth, settings.Depth);
        Assert.Equal(DirectorySettings.DefaultRequests, settings.Requests);
        Assert.Equal(DirectorySettings.DefaultDirectories, settings.Directories);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData(" 3 ", 3)]
    [InlineData("0", 0)]
    public void ADepthIsAWholeNumberOfLevels(string given, int expected) =>
        Assert.Equal(expected, Read("mount", "cloud", DirectorySwitches.DepthOption, given).Depth);

    [Fact]
    public void ACeilingIsAWholeNumberOfRequests() =>
        Assert.Equal(8, Read("mount", "cloud", DirectorySwitches.RequestsOption, "8").Requests);

    [Fact]
    public void AHoldingIsAWholeNumberOfDirectories() =>
        Assert.Equal(64, Read("mount", "cloud", DirectorySwitches.DirectoriesOption, "64").Directories);

    // Which is a directory listed when it is opened and at no other time, and that is what a
    // report about a directory that showed the wrong contents is narrowed down with.
    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("0")]
    public void OffIsNothingAtAll(string given)
    {
        DirectorySettings settings = Read(
            "mount",
            "cloud",
            DirectorySwitches.DepthOption,
            given,
            DirectorySwitches.RequestsOption,
            given,
            DirectorySwitches.DirectoriesOption,
            given);

        Assert.Equal(0, settings.Depth);
        Assert.Equal(0, settings.Requests);
        Assert.Equal(0, settings.Directories);
    }

    [Fact]
    public void WhatWasReadIsTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(
            ["mount", "cloud", DirectorySwitches.DepthOption, "2", DirectorySwitches.DirectoriesOption, "64"]);

        DirectorySwitches.Read(line, _ => null);

        // A command that refuses an option it does not know must not be handed one that was
        // never for it.
        Assert.False(line.Given(DirectorySwitches.DepthOption));
        Assert.False(line.Given(DirectorySwitches.DirectoriesOption));
        Assert.Equal("mount", line.Verb);
        Assert.Equal<string>(["cloud"], line.Arguments);
    }

    // The names are published, and a variable that is renamed is a script that stops working.
    [Fact]
    public void TheOptionsHaveTheirVariables()
    {
        Assert.Equal("WINDAV_LIST_AHEAD", Switches.Variable(DirectorySwitches.DepthOption));
        Assert.Equal("WINDAV_LIST_AHEAD_REQUESTS", Switches.Variable(DirectorySwitches.RequestsOption));
        Assert.Equal("WINDAV_LISTINGS", Switches.Variable(DirectorySwitches.DirectoriesOption));
    }

    [Fact]
    public void AVariableIsReadWhereTheOptionIsNot() =>
        Assert.Equal(
            3,
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_LIST_AHEAD"] = "3" },
                "mount",
                "cloud").Depth);

    // What is written on the command line is written for this one run; what is in the
    // environment was put there for whatever runs there.
    [Fact]
    public void TheOptionWinsOverItsVariable() =>
        Assert.Equal(
            1,
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_LIST_AHEAD"] = "3" },
                "mount",
                "cloud",
                DirectorySwitches.DepthOption,
                "1").Depth);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableWithNothingInItIsNoVariable(string value) =>
        Assert.Equal(
            DirectorySettings.DefaultDepth,
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_LIST_AHEAD"] = value },
                "mount",
                "cloud").Depth);

    [Fact]
    public void TheOptionWithoutAValueIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", DirectorySwitches.RequestsOption));

        Assert.Contains(DirectorySwitches.RequestsOption, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("deep")]
    [InlineData("1.5")]
    public void ANumberThatIsNoNumberIsRefusedWithWhatWasGiven(string given)
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", DirectorySwitches.DepthOption, given));

        Assert.Contains(given, refused.Message, StringComparison.Ordinal);
        Assert.Contains(DirectorySwitches.DepthOption, refused.Message, StringComparison.Ordinal);
    }

    // Nothing here reads the environment of the process it runs in: a machine that has the
    // variable set is not a machine where these tests say something else.
    private static DirectorySettings Read(params string[] tokens) =>
        DirectorySwitches.Read(CommandLine.Parse(tokens), _ => null);

    private static DirectorySettings WithEnvironment(
        Dictionary<string, string> environment,
        params string[] tokens) =>
        DirectorySwitches.Read(
            CommandLine.Parse(tokens),
            name => environment.TryGetValue(name, out string? value) ? value : null);
}
