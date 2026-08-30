// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Fs;
using Xunit;

namespace WinDav.Cli.Tests;

// The three options of the read path, read the way the four of the log are read: the command
// line first, the environment behind it, and every one of them able to say off.
public sealed class ReadSwitchesTests
{
    [Fact]
    public void NothingAskedForIsWhatWasMeasured()
    {
        ReadSettings reads = Read("mount", "cloud");

        Assert.Equal(ReadSettings.DefaultWindow, reads.Window);
        Assert.Equal(ReadSettings.DefaultTotal, reads.Total);
        Assert.Equal(ReadSettings.DefaultRequests, reads.Requests);
    }

    [Theory]
    [InlineData("4096", 4096L)]
    [InlineData("8k", 8L * 1024)]
    [InlineData("8m", 8L * 1024 * 1024)]
    [InlineData("1g", 1024L * 1024 * 1024)]
    [InlineData("16M", 16L * 1024 * 1024)]
    [InlineData(" 2m ", 2L * 1024 * 1024)]
    public void ASizeIsBytesOrANumberWithALetterAfterIt(string given, long expected)
    {
        // The ceiling is switched off, so that what is read here is the size and not
        // whether a window of it would fit under the default ceiling.
        ReadSettings reads = Read(
            "mount", "cloud", "--read-ahead", given, "--read-ahead-total", "off");

        Assert.Equal(expected, reads.Window);
    }

    [Fact]
    public void EachOfTheThreeCanBeSwitchedOff()
    {
        ReadSettings reads = Read(
            "mount",
            "cloud",
            "--read-ahead",
            "off",
            "--read-ahead-total",
            "OFF",
            "--requests",
            "off");

        // With all three off, every read is the one request that read asked for and nothing
        // is held between them, which is what a report about a wrong byte is narrowed down
        // with. One request at a time is what off means for the third: there is no number
        // below it.
        Assert.Equal(0L, reads.Window);
        Assert.Equal(0L, reads.Total);
        Assert.Equal(1, reads.Requests);
    }

    [Fact]
    public void ACeilingOfNothingIsNotAWindowLargerThanItsCeiling()
    {
        // The window is left at its default, which is larger than the ceiling that was
        // switched off. No ceiling is not a ceiling of zero bytes.
        ReadSettings reads = Read("mount", "cloud", "--read-ahead-total", "off");

        Assert.Equal(ReadSettings.DefaultWindow, reads.Window);
        Assert.Equal(0L, reads.Total);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    public void TheRequestsAreTheNumberThatWasNamed(string given, int expected) =>
        Assert.Equal(expected, Read("mount", "cloud", "--requests", given).Requests);

    [Fact]
    public void WhatWasReadIsTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(
            ["mount", "cloud", "--read-ahead", "1m", "--requests", "3"]);

        ReadSwitches.Read(line, _ => null);

        // What is left is what the command was going to see anyway. A command that refuses
        // an option it does not know must not be handed one that was never for it.
        Assert.False(line.Given(ReadSwitches.WindowOption));
        Assert.False(line.Given(ReadSwitches.RequestsOption));
        Assert.Equal("mount", line.Verb);
        Assert.Equal<string>(["cloud"], line.Arguments);
    }

    // The names are published, and a variable that is renamed is a script that stops working.
    [Theory]
    [InlineData(ReadSwitches.WindowOption, "WINDAV_READ_AHEAD")]
    [InlineData(ReadSwitches.TotalOption, "WINDAV_READ_AHEAD_TOTAL")]
    [InlineData(ReadSwitches.RequestsOption, "WINDAV_REQUESTS")]
    public void EachOptionHasItsVariable(string option, string expected) =>
        Assert.Equal(expected, Switches.Variable(option));

    [Fact]
    public void AVariableIsReadWhereTheOptionIsNot()
    {
        ReadSettings reads = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WINDAV_READ_AHEAD"] = "2m",
                ["WINDAV_READ_AHEAD_TOTAL"] = "16m",
                ["WINDAV_REQUESTS"] = "3",
            },
            "mount",
            "cloud");

        Assert.Equal(2L * 1024 * 1024, reads.Window);
        Assert.Equal(16L * 1024 * 1024, reads.Total);
        Assert.Equal(3, reads.Requests);
    }

    // What is written on the command line is written for this one run; what is in the
    // environment was put there for whatever runs there.
    [Fact]
    public void TheOptionWinsOverItsVariable()
    {
        ReadSettings reads = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_READ_AHEAD"] = "2m" },
            "mount",
            "cloud",
            "--read-ahead",
            "4m");

        Assert.Equal(4L * 1024 * 1024, reads.Window);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableWithNothingInItIsNoVariable(string value)
    {
        ReadSettings reads = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_READ_AHEAD"] = value },
            "mount",
            "cloud");

        Assert.Equal(ReadSettings.DefaultWindow, reads.Window);
    }

    [Fact]
    public void AVariableIsRefusedInTheSameWordsAsItsOption()
    {
        UsageException refused = Assert.Throws<UsageException>(() => WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_REQUESTS"] = "none" },
            "mount",
            "cloud"));

        Assert.Contains("none", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReadSwitches.WindowOption)]
    [InlineData(ReadSwitches.TotalOption)]
    [InlineData(ReadSwitches.RequestsOption)]
    public void AnOptionWithoutAValueIsRefusedByName(string option)
    {
        UsageException refused = Assert.Throws<UsageException>(() => Read("mount", "cloud", option));

        Assert.Contains(option, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8x")]
    [InlineData("abc")]
    [InlineData("9000000000g")]
    public void ASizeThatIsNoSizeIsRefusedWithWhatOneLooksLike(string given)
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", "--read-ahead", given));

        Assert.Contains(given, refused.Message, StringComparison.Ordinal);
        Assert.Contains("8m", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowLargerThanOnePieceOfMemoryIsRefused()
    {
        // A window is one array and is fetched in one piece, so what an array holds is the
        // end of it.
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", "--read-ahead", "3g"));

        Assert.Contains(ReadSwitches.WindowOption, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowLargerThanTheCeilingOverAllOfThemIsRefused()
    {
        // Nothing would fail at run time: every handle would simply be refused a window. A
        // pair of numbers that can never mean what it says is worth saying so at the start.
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", "--read-ahead", "16m", "--read-ahead-total", "8m"));

        Assert.Contains(ReadSwitches.WindowOption, refused.Message, StringComparison.Ordinal);
        Assert.Contains(ReadSwitches.TotalOption, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("two")]
    [InlineData("2.5")]
    public void ANumberOfRequestsBelowOneOrNoNumberAtAllIsRefused(string given)
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", "--requests", given));

        Assert.Contains(given, refused.Message, StringComparison.Ordinal);
    }

    // Nothing here reads the environment of the process it runs in: a machine that has one of
    // the three variables set is not a machine where these tests say something else.
    private static ReadSettings Read(params string[] tokens) =>
        ReadSwitches.Read(CommandLine.Parse(tokens), _ => null);

    private static ReadSettings WithEnvironment(
        Dictionary<string, string> environment,
        params string[] tokens) =>
        ReadSwitches.Read(
            CommandLine.Parse(tokens),
            name => environment.TryGetValue(name, out string? value) ? value : null);
}
