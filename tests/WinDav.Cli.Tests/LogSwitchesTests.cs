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
    public void NothingAskedForIsNothingRecorded() => Assert.Null(Read("mount", "cloud"));

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
        Assert.Throws<UsageException>(() => Read("mount", "cloud", "--debug", "--for", given));

    // Trace would swallow debug, but which of the two was meant is a guess, and a recording is
    // asked for while something is going wrong.
    [Fact]
    public void AskingForBothLevelsIsRefused() =>
        Assert.Throws<UsageException>(() => Read("mount", "cloud", "--debug", "--trace"));

    [Fact]
    public void ATimeWithNothingToRecordIsRefused() =>
        Assert.Throws<UsageException>(() => Read("mount", "cloud", "--for", "5m"));

    [Fact]
    public void ATimeWithoutAValueIsRefused() =>
        Assert.Throws<UsageException>(() => Read("mount", "cloud", "--debug", "--for"));

    [Fact]
    public void AnAreaThatIsNoAreaIsRefusedByName()
    {
        UsageException refused = Assert.Throws<UsageException>(() => Read("mount", "cloud", "--trace", "fs,dav"));

        Assert.Contains("dav", refused.Message, StringComparison.Ordinal);
        Assert.Contains("provider", refused.Message, StringComparison.Ordinal);
    }

    // They belong to the program and not to any command, so the command that runs afterwards
    // refuses everything it does not know without having to know these three.
    [Fact]
    public void TheSwitchesAreTakenOutOfTheCommandLine()
    {
        CommandLine line = CommandLine.Parse(["mount", "cloud", "--trace", "fs", "--for", "5m"]);

        Assert.NotNull(LogSwitches.Read(line));

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
            using LogRecording recording = Switches("mount", "cloud", "--trace", "fs,http", "--for", "90s").Start(file);

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

    private static LogSwitches? Read(params string[] tokens) => LogSwitches.Read(CommandLine.Parse(tokens));

    private static LogSwitches Switches(params string[] tokens) => Assert.IsType<LogSwitches>(Read(tokens));
}
