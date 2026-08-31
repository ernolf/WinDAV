// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Cli.Tests;

// The one option of the attribute cache, read the way the three of the read path are read:
// the command line first, the environment behind it, and off among the values it takes.
public sealed class CacheSwitchesTests
{
    [Fact]
    public void NothingAskedForIsTheDefaultLifetime() =>
        Assert.Equal(AttributeCache.DefaultLifetime, Read("mount", "cloud"));

    [Theory]
    [InlineData("30", 30)]
    [InlineData("5s", 5)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    [InlineData(" 10S ", 10)]
    public void ALifetimeIsSecondsOrANumberWithALetterAfterIt(string given, int expected) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            Read("mount", "cloud", CacheSwitches.LifetimeOption, given));

    // Which is today's behaviour: two PROPFINDs for every file that is opened, and a listing
    // that keeps nothing for the opens after it. That is what a report about a stale
    // directory is narrowed down with.
    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("0")]
    [InlineData("0s")]
    public void OffIsARequestPerQuestion(string given) =>
        Assert.Equal(TimeSpan.Zero, Read("mount", "cloud", CacheSwitches.LifetimeOption, given));

    [Fact]
    public void WhatWasReadIsTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(["mount", "cloud", CacheSwitches.LifetimeOption, "5s"]);

        CacheSwitches.Read(line, _ => null);

        // A command that refuses an option it does not know must not be handed one that was
        // never for it.
        Assert.False(line.Given(CacheSwitches.LifetimeOption));
        Assert.Equal("mount", line.Verb);
        Assert.Equal<string>(["cloud"], line.Arguments);
    }

    // The name is published, and a variable that is renamed is a script that stops working.
    [Fact]
    public void TheOptionHasItsVariable() =>
        Assert.Equal("WINDAV_ATTRIBUTES", Switches.Variable(CacheSwitches.LifetimeOption));

    [Fact]
    public void AVariableIsReadWhereTheOptionIsNot() =>
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_ATTRIBUTES"] = "20s" },
                "mount",
                "cloud"));

    // What is written on the command line is written for this one run; what is in the
    // environment was put there for whatever runs there.
    [Fact]
    public void TheOptionWinsOverItsVariable() =>
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_ATTRIBUTES"] = "20s" },
                "mount",
                "cloud",
                CacheSwitches.LifetimeOption,
                "5s"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableWithNothingInItIsNoVariable(string value) =>
        Assert.Equal(
            AttributeCache.DefaultLifetime,
            WithEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_ATTRIBUTES"] = value },
                "mount",
                "cloud"));

    [Fact]
    public void AVariableIsRefusedInTheSameWordsAsItsOption()
    {
        UsageException refused = Assert.Throws<UsageException>(() => WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_ATTRIBUTES"] = "never" },
            "mount",
            "cloud"));

        Assert.Contains("never", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionWithoutAValueIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", CacheSwitches.LifetimeOption));

        Assert.Contains(CacheSwitches.LifetimeOption, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-5s")]
    [InlineData("soon")]
    [InlineData("10x")]
    [InlineData("0.5s")]
    public void ALifetimeThatIsNoLifetimeIsRefusedWithWhatOneLooksLike(string given)
    {
        // Whole seconds and no sign, the way the three sizes of the read path are read. Half
        // a second is below what WinFsp holds an answer for anyway.
        UsageException refused = Assert.Throws<UsageException>(
            () => Read("mount", "cloud", CacheSwitches.LifetimeOption, given));

        Assert.Contains(given, refused.Message, StringComparison.Ordinal);
        Assert.Contains("10s", refused.Message, StringComparison.Ordinal);
    }

    // Nothing here reads the environment of the process it runs in: a machine that has the
    // variable set is not a machine where these tests say something else.
    private static TimeSpan Read(params string[] tokens) =>
        CacheSwitches.Read(CommandLine.Parse(tokens), _ => null);

    private static TimeSpan WithEnvironment(
        Dictionary<string, string> environment,
        params string[] tokens) =>
        CacheSwitches.Read(
            CommandLine.Parse(tokens),
            name => environment.TryGetValue(name, out string? value) ? value : null);
}
