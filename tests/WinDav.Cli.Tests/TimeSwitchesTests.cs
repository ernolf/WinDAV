// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Fs;
using Xunit;

namespace WinDav.Cli.Tests;

// The quiet period a directory has to have had before the times it was given are set again,
// read the way every other option of the program is: the command line first, the environment
// behind it, and off among the values both of them take.
public sealed class TimeSwitchesTests
{
    [Fact]
    public void NothingAskedForIsThePeriodThatWasChosen() =>
        Assert.Equal(MountSettings.DefaultDirectoryQuiet, Read("mount", "cloud"));

    [Theory]
    [InlineData("5", 5)]
    [InlineData(" 30 ", 30)]
    [InlineData("90s", 90)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    public void APeriodIsSecondsOrANumberWithALetterAfterIt(string given, int expected) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            Read("mount", "cloud", TimeSwitches.QuietOption, given));

    // Which is the mount as it was before any of this: a directory carries whatever the copy
    // into it made of its date, and no request goes out for it.
    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("0")]
    public void OffIsNothingAtAll(string given) =>
        Assert.Equal(TimeSpan.Zero, Read("mount", "cloud", TimeSwitches.QuietOption, given));

    [Fact]
    public void WhatWasReadIsTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(["mount", "cloud", TimeSwitches.QuietOption, "5s"]);

        TimeSwitches.Read(line, _ => null);

        // A command that refuses an option it does not know must not be handed one that was
        // never for it.
        Assert.False(line.Given(TimeSwitches.QuietOption));
        Assert.Equal("mount", line.Verb);
        Assert.Equal<string>(["cloud"], line.Arguments);
    }

    // The name is published, and a variable that is renamed is a script that stops working.
    [Fact]
    public void TheOptionHasItsVariable() =>
        Assert.Equal("WINDAV_DIRECTORY_TIMES", Switches.Variable(TimeSwitches.QuietOption));

    [Fact]
    public void AVariableIsReadWhereTheOptionIsNot() =>
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_DIRECTORY_TIMES"] = "20" },
                "mount",
                "cloud"));

    // What is written on the command line is written for this one run; what is in the
    // environment was put there for whatever runs there.
    [Fact]
    public void TheOptionWinsOverItsVariable() =>
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_DIRECTORY_TIMES"] = "20" },
                "mount",
                "cloud",
                TimeSwitches.QuietOption,
                "5"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableWithNothingInItIsNoVariable(string value) =>
        Assert.Equal(
            MountSettings.DefaultDirectoryQuiet,
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_DIRECTORY_TIMES"] = value },
                "mount",
                "cloud"));

    [Fact]
    public void TheOptionWithoutAValueIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", TimeSwitches.QuietOption));

        Assert.Contains(TimeSwitches.QuietOption, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("1.5")]
    [InlineData("5d")]
    public void ALengthThatIsNoLengthIsRefusedWithWhatWasGiven(string given)
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", TimeSwitches.QuietOption, given));

        Assert.Contains(given, refused.Message, StringComparison.Ordinal);
        Assert.Contains(TimeSwitches.QuietOption, refused.Message, StringComparison.Ordinal);
    }

    // Nothing here reads the environment of the process it runs in: a machine that has the
    // variable set is not a machine where these tests say something else.
    private static TimeSpan Read(params string[] tokens) =>
        TimeSwitches.Read(CommandLine.Parse(tokens), _ => null);

    private static TimeSpan WithEnvironment(
        Dictionary<string, string> environment,
        params string[] tokens) =>
        TimeSwitches.Read(
            CommandLine.Parse(tokens),
            name => environment.TryGetValue(name, out string? value) ? value : null);
}
