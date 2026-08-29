// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogRecordingTests : IDisposable
{
    private const string Command = "windav mount cloud --trace fs";

    // Long enough that nothing here reaches the end of it by waiting. The test that is about
    // the clock gives itself a time of its own.
    private static readonly TimeSpan s_while = TimeSpan.FromMinutes(5);

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

    // Both limits stand in the file, so a recording that stopped in the middle of something
    // has already said what it was allowed to do.
    [Fact]
    public void ItOpensWithWhatItRecordsAndBothLimits()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using LogRecording recording = new(file, LogLevel.Trace, [LogArea.Fs, LogArea.Http], s_while);

            path = Assert.IsType<string>(file.FilePath);
        }

        // After the three lines of the header, which is where a recording asked for on the
        // command line begins.
        Assert.EndsWith("recording trace of fs, http for 300 s or up to 16 MB", Lines(path)[3], StringComparison.Ordinal);
    }

    [Fact]
    public void ItClosesWithTheReasonAndTheCount()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using (LogRecording recording = new(file, LogLevel.Debug, [LogArea.Cli], s_while))
            {
                recording.Note(120);
                recording.Note(140);
            }

            path = Assert.IsType<string>(file.FilePath);
        }

        Assert.Contains(
            "recording ended after 2 records, the session ended",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForNoAreaAsksForAllOfThem()
    {
        using LogFile file = new(_directory, Command);
        using LogRecording recording = new(file, LogLevel.Debug, [], s_while);

        Assert.Equal<LogArea>(LogAreas.All, recording.Areas);
    }

    [Fact]
    public void TheSameAreaTwiceIsOneArea()
    {
        using LogFile file = new(_directory, Command);
        using LogRecording recording = new(file, LogLevel.Trace, [LogArea.Http, LogArea.Http], s_while);

        Assert.Equal(LogArea.Http, Assert.Single(recording.Areas));
    }

    [Fact]
    public void OnlyTheAreaAndTheLevelThatWereAskedForAreCovered()
    {
        using LogFile file = new(_directory, Command);
        using LogRecording recording = new(file, LogLevel.Debug, [LogArea.Fs], s_while);

        Assert.True(recording.Covers(LogArea.Fs, LogLevel.Debug));

        // Trace is louder than what was asked for: a recording of debug is not one of trace,
        // and that is the whole difference between the two switches.
        Assert.False(recording.Covers(LogArea.Fs, LogLevel.Trace));
        Assert.False(recording.Covers(LogArea.Http, LogLevel.Debug));
    }

    [Fact]
    public void NothingIsCoveredOnceItHasStopped()
    {
        using LogFile file = new(_directory, Command);
        LogRecording recording = new(file, LogLevel.Trace, [LogArea.Fs], s_while);

        recording.Note(100);
        recording.Dispose();
        recording.Note(100);

        Assert.Equal(LogRecordingEnd.Session, recording.Ending);
        Assert.Equal(1L, recording.Records);
        Assert.False(recording.Covers(LogArea.Fs, LogLevel.Trace));
    }

    // The three lower levels are always on and are nobody's decision, so there is nothing to
    // ask for and nothing to record.
    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.None)]
    public void ARecordingIsOfOneOfTheTwoUpperLevels(LogLevel level)
    {
        using LogFile file = new(_directory, Command);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRecording(file, level, [LogArea.Fs], s_while));
    }

    [Fact]
    public void ATimeThatIsNoTimeIsRefused()
    {
        using LogFile file = new(_directory, Command);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LogRecording(file, LogLevel.Debug, [LogArea.Fs], TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LogRecording(file, LogLevel.Debug, [LogArea.Fs], TimeSpan.FromSeconds(-1)));
    }

    // A trace left on by accident is a full disk a week later, and the hour is where that is
    // settled rather than left to whoever types the command.
    [Fact]
    public void MoreThanAnHourIsRefused()
    {
        using LogFile file = new(_directory, Command);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LogRecording(
                file,
                LogLevel.Trace,
                [LogArea.Fs],
                LogRecording.MaximumDuration + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WhatItMayWriteEndsIt()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using LogRecording recording = new(file, LogLevel.Trace, [LogArea.Http], s_while);

            // Counted after the record was written, so the limit is the last thing over it
            // rather than the last thing under it.
            recording.Note((int)(LogRecording.MaximumBytes / 2));
            recording.Note((int)(LogRecording.MaximumBytes / 2));

            Assert.Equal(LogRecordingEnd.Size, recording.Ending);
            Assert.False(recording.Covers(LogArea.Http, LogLevel.Trace));

            path = Assert.IsType<string>(file.FilePath);
        }

        Assert.Contains(
            "recording ended after 2 records, it had written as much as it may",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimeRunningOutEndsIt()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using LogRecording recording = new(file, LogLevel.Debug, [LogArea.Cli], TimeSpan.FromMilliseconds(50));

            // A recording nobody writes to still ends when it said it would, and the clock
            // that ends it runs on a thread of the pool. Waited for rather than slept on, so
            // that a slow machine makes the test slower and not red.
            Assert.True(SpinWait.SpinUntil(() => recording.Ending != LogRecordingEnd.None, TimeSpan.FromSeconds(30)));
            Assert.Equal(LogRecordingEnd.Duration, recording.Ending);

            path = Assert.IsType<string>(file.FilePath);
        }

        Assert.Contains(
            "recording ended after 0 records, the time was up",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    // The seam as the program puts it together: one file, one recording, a logger per class.
    // What was not asked for stays as quiet as it is when nothing was asked for at all.
    [Fact]
    public void OnlyWhatTheRecordingCoversIsWrittenDown()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using LogRecording recording = new(file, LogLevel.Debug, [LogArea.Fs], s_while);
            using FileLoggerFactory logging = new(file, recording);

            logging.CreateLogger("WinDav.Fs.WinDavFileSystem").LogDebug("Read 65536 bytes of /music.mp3.");
            logging.CreateLogger("WinDav.Dav.LoggingHandler").LogDebug("GET /music.mp3 206.");
            logging.CreateLogger("WinDav.Dav.LoggingHandler").LogWarning("GET /music.mp3 failed.");

            // The levels that are always on are not this recording's doing, so they are not
            // counted against what it may write.
            Assert.Equal(1L, recording.Records);

            path = Assert.IsType<string>(file.FilePath);
        }

        string written = File.ReadAllText(path);

        Assert.Contains("Read 65536 bytes of /music.mp3.", written, StringComparison.Ordinal);
        Assert.Contains("GET /music.mp3 failed.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("GET /music.mp3 206.", written, StringComparison.Ordinal);
    }

    private static string[] Lines(string path) =>
        File.ReadAllText(path).Split(LogFormat.LineEnd, StringSplitOptions.RemoveEmptyEntries);
}
