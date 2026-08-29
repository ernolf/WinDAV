// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Cli.Tests;

public sealed class LogSwitchesTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NothingAskedForIsNothingRecordedAndTheFloorIsTheUsualOne()
    {
        LogSwitches switches = Switches("mount", "cloud");

        Assert.Null(switches.Level);
        Assert.Equal(LogLevels.Default, switches.Minimum);
        Assert.Empty(switches.Areas);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("OFF", LogLevel.None)]
    public void TheFloorIsTheLevelThatWasNamed(string given, LogLevel expected)
    {
        LogSwitches switches = Switches("mount", "cloud", "--log", given);

        Assert.Equal(expected, switches.Minimum);
        Assert.Null(switches.Level);
    }

    [Fact]
    public void ALevelThatIsNoLevelIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--log", "fatal"));

        Assert.Contains("fatal", refused.Message, StringComparison.Ordinal);
        Assert.Contains(LogLevels.OffName, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelWithoutAValueIsRefused() =>
        Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--log"));

    // The floor is what is always written, the recording is what is added to it for a while.
    // One says nothing about the other, and asking for both is not asking twice.
    [Fact]
    public void TheFloorAndARecordingAreReadApart()
    {
        LogSwitches switches = Switches("mount", "cloud", "--log", "trace", "--debug", "fs");

        Assert.Equal(LogLevel.Trace, switches.Minimum);
        Assert.Equal(LogLevel.Debug, switches.Level);
    }

    [Fact]
    public void AskingForALevelAndNothingElseAsksForEveryAreaForTheDefaultTime()
    {
        LogSwitches switches = Switches("mount", "cloud", "--debug");

        Assert.Equal(LogLevel.Debug, switches.Level);
        Assert.Equal<LogArea>(LogAreas.All, switches.Areas);
        Assert.Equal(LogRecording.DefaultDuration, switches.Duration);
    }

    [Fact]
    public void TheAreasAreTheOnesThatWereNamed()
    {
        LogSwitches switches = Switches("mount", "cloud", "--trace", "fs,http");

        Assert.Equal(LogLevel.Trace, switches.Level);
        Assert.Equal<LogArea>([LogArea.Fs, LogArea.Http], switches.Areas);
    }

    [Fact]
    public void EveryAreaCanBeAskedForByName() =>
        Assert.Equal<LogArea>(LogAreas.All, Switches("mount", "cloud", "--trace", "all").Areas);

    // The names a person types are the names they read in the file, and neither is a spelling
    // to get right twice.
    [Fact]
    public void TheNameOfAnAreaIsReadInAnyCase() =>
        Assert.Equal(LogArea.Fs, Assert.Single(Switches("mount", "cloud", "--debug", "FS").Areas));

    [Fact]
    public void TheSameAreaTwiceIsOneArea() =>
        Assert.Equal(LogArea.Http, Assert.Single(Switches("mount", "cloud", "--debug", "http,http").Areas));

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("120", 120)]
    public void TheTimeIsSecondsMinutesOrHours(string given, int seconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            Switches("mount", "cloud", "--debug", "--for", given).Duration);

    [Theory]
    [InlineData("2h")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("5x")]
    public void ATimeThatIsNotOneIsRefused(string given) =>
        Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--debug", "--for", given));

    // Trace would swallow debug, but which of the two was meant is a guess, and a recording is
    // asked for while something is going wrong.
    [Fact]
    public void AskingForBothLevelsIsRefused() =>
        Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--debug", "--trace"));

    [Fact]
    public void ATimeWithNothingToRecordIsRefused() =>
        Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--for", "5m"));

    [Fact]
    public void ATimeWithoutAValueIsRefused() =>
        Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--debug", "--for"));

    [Fact]
    public void AnAreaThatIsNoAreaIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(() => Switches("mount", "cloud", "--trace", "fs,dav"));

        Assert.Contains("dav", refused.Message, StringComparison.Ordinal);
        Assert.Contains("provider", refused.Message, StringComparison.Ordinal);
    }

    // They belong to the program and not to any command, so the command that runs afterwards
    // refuses everything it does not know without having to know these three.
    [Fact]
    public void TheSwitchesAreTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(
            ["mount", "cloud", "--log", "off", "--trace", "fs", "--for", "5m"]);

        Assert.Equal(LogLevel.None, LogSwitches.Read(line, _ => null).Minimum);

        line.EnsureOnlyKnown([]);

        Assert.Equal("mount", line.Verb);
        Assert.Equal(["cloud"], line.Arguments);
    }

    [Fact]
    public void WhatWasTypedIsWhatTheRecordingRuns()
    {
        string path;

        using (LogFile file = new(_directory, "windav mount cloud --trace fs,http --for 90s"))
        {
            using LogRecording recording = Assert.IsType<LogRecording>(
                Switches("mount", "cloud", "--trace", "fs,http", "--for", "90s").Start(file));

            Assert.Equal(LogLevel.Trace, recording.Level);
            Assert.Equal<LogArea>([LogArea.Fs, LogArea.Http], recording.Areas);
            Assert.Equal(TimeSpan.FromSeconds(90), recording.Duration);

            path = Assert.IsType<string>(file.FilePath);
        }

        Assert.Contains(
            "recording trace of fs, http for 90 s or up to 16 MB",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    // A service, a scheduled task and a script that starts twenty mounts have no command line
    // anyone edits at the moment it matters, and every one of them has an environment.
    [Fact]
    public void AVariableSaysWhatItsOptionSays()
    {
        LogSwitches switches = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WINDAV_LOG"] = "debug",
                ["WINDAV_TRACE"] = "fs,http",
                ["WINDAV_FOR"] = "5m",
            },
            "mount",
            "cloud");

        Assert.Equal(LogLevel.Debug, switches.Minimum);
        Assert.Equal(LogLevel.Trace, switches.Level);
        Assert.Equal<LogArea>([LogArea.Fs, LogArea.Http], switches.Areas);
        Assert.Equal(TimeSpan.FromMinutes(5), switches.Duration);
    }

    // What is written on the command line is written for this one run; what is in the
    // environment was put there for whatever runs there.
    [Fact]
    public void TheOptionWinsOverItsVariable()
    {
        LogSwitches switches = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_LOG"] = "trace" },
            "mount",
            "cloud",
            "--log",
            "error");

        Assert.Equal(LogLevel.Error, switches.Minimum);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableWithNothingInItIsNoVariable(string value)
    {
        LogSwitches switches = WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_DEBUG"] = value },
            "mount",
            "cloud");

        Assert.Null(switches.Level);
    }

    [Fact]
    public void AVariableIsRefusedInTheSameWordsAsItsOption()
    {
        UsageException refused = Assert.Throws<UsageException>(() => WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINDAV_LOG"] = "fatal" },
            "mount",
            "cloud"));

        Assert.Contains("fatal", refused.Message, StringComparison.Ordinal);
    }

    // The names are published, and a variable that is renamed is a script that stops working.
    [Theory]
    [InlineData(LogSwitches.LevelOption, "WINDAV_LOG")]
    [InlineData(LogSwitches.DebugOption, "WINDAV_DEBUG")]
    [InlineData(LogSwitches.TraceOption, "WINDAV_TRACE")]
    [InlineData(LogSwitches.ForOption, "WINDAV_FOR")]
    public void EachOptionHasItsVariable(string option, string expected) =>
        Assert.Equal(expected, LogSwitches.Variable(option));

    // Nothing here reads the environment of the process it runs in: a machine that has one of
    // the four variables set is not a machine where these tests say something else.
    private static LogSwitches Switches(params string[] tokens) =>
        LogSwitches.Read(CommandLine.Parse(tokens), _ => null);

    private static LogSwitches WithEnvironment(
        Dictionary<string, string> environment,
        params string[] tokens) =>
        LogSwitches.Read(
            CommandLine.Parse(tokens),
            name => environment.TryGetValue(name, out string? value) ? value : null);
}
