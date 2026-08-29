// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogFormatTests
{
    // Fixed, with an offset that is not the machine's, so that a wrong format shows up as a
    // wrong string rather than as a test that passes wherever it happens to run.
    private static readonly DateTimeOffset s_when =
        new(2026, 8, 29, 14, 5, 9, 123, TimeSpan.FromHours(2));

    [Fact]
    public void TheHeaderIsThreeCommentLines()
    {
        string[] lines = Lines(LogFormat.Header(s_when, "windav mount cloud"));

        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.StartsWith(LogFormat.CommentPrefix, line, StringComparison.Ordinal));
        Assert.Contains(ProductInfo.Version, lines[0], StringComparison.Ordinal);
        Assert.Contains("2026-08-29T14:05:09.123+02:00", lines[0], StringComparison.Ordinal);
        Assert.Equal($"{LogFormat.CommentPrefix}windav mount cloud", lines[1]);
    }

    [Theory]
    [InlineData(0, "after 0 records")]
    [InlineData(1, "after 1 record")]
    [InlineData(2, "after 2 records")]
    public void TheFooterSaysHowTheFileEnded(long records, string expected)
    {
        string footer = LogFormat.Footer(s_when, records);

        Assert.StartsWith($"{LogFormat.CommentPrefix}ended 2026-08-29T14:05:09.123+02:00 ", footer, StringComparison.Ordinal);
        Assert.EndsWith($"{expected}{LogFormat.LineEnd}", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordIsTheTimeTheLevelTheAreaAndTheSentence()
    {
        string line = LogFormat.Line(s_when, LogLevel.Information, LogArea.Http, "GET /remote.php/dav", null);

        Assert.Equal(
            $"2026-08-29T14:05:09.123+02:00  info   http      GET /remote.php/dav{LogFormat.LineEnd}",
            line);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trace")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]

    // Decision 74 has five names, not six. Critical is something that failed, and a reader
    // looking for what failed should not have to know two words for it.
    [InlineData(LogLevel.Critical, "error")]
    public void EachLevelHasOneName(LogLevel level, string expected) =>
        Assert.Equal(expected, LogFormat.Name(level));

    [Fact]
    public void AMessageOfSeveralLinesCarriesOnIndented()
    {
        string[] lines = Lines(LogFormat.Line(s_when, LogLevel.Warning, LogArea.Cli, "one\r\ntwo", null));

        Assert.Equal(2, lines.Length);
        Assert.EndsWith("one", lines[0], StringComparison.Ordinal);
        Assert.Equal($"{LogFormat.ContinuationPrefix}two", lines[1]);
    }

    [Fact]
    public void AnExceptionFollowsTheSentence()
    {
        string[] lines = Lines(
            LogFormat.Line(s_when, LogLevel.Error, LogArea.Provider, "It failed.", new InvalidOperationException("why")));

        Assert.EndsWith("It failed.", lines[0], StringComparison.Ordinal);
        Assert.StartsWith(
            $"{LogFormat.ContinuationPrefix}System.InvalidOperationException: why",
            lines[1],
            StringComparison.Ordinal);
    }

    // A comment carries the time for the same reason a record does: what a recording covers
    // is read off the two lines that open and close it.
    [Fact]
    public void ACommentIsTheTimeAndWhatIsBeingSaid() =>
        Assert.Equal(
            $"{LogFormat.CommentPrefix}2026-08-29T14:05:09.123+02:00  it began{LogFormat.LineEnd}",
            LogFormat.Comment(s_when, "it began"));

    [Fact]
    public void ARecordingSaysWhatItRecordsAndWhereFrom() =>
        Assert.Equal(
            "recording debug of fs, http for 90 s or up to 16 MB",
            LogFormat.RecordingStart(
                LogLevel.Debug,
                [LogArea.Fs, LogArea.Http],
                TimeSpan.FromSeconds(90),
                LogRecording.MaximumBytes));

    // Seconds throughout, whatever the person typed: read as 120 s a duration is one
    // subtraction away from the timestamps around it, read as 2 m it is two.
    [Theory]
    [InlineData(60, "60 s")]
    [InlineData(120, "120 s")]
    [InlineData(3600, "3600 s")]
    public void HowLongARecordingMayRunIsSaidInSeconds(int seconds, string expected) =>
        Assert.EndsWith(
            $"for {expected} or up to 16 MB",
            LogFormat.RecordingStart(LogLevel.Trace, LogAreas.All, TimeSpan.FromSeconds(seconds), LogRecording.MaximumBytes),
            StringComparison.Ordinal);

    [Theory]
    [InlineData(LogRecordingEnd.Duration, 3, "recording ended after 3 records, the time was up")]
    [InlineData(LogRecordingEnd.Size, 1, "recording ended after 1 record, it had written as much as it may")]
    [InlineData(LogRecordingEnd.Session, 0, "recording ended after 0 records, the session ended")]
    public void ARecordingClosesWithTheReasonAndTheCount(LogRecordingEnd end, long records, string expected) =>
        Assert.Equal(expected, LogFormat.RecordingEnd(end, records));

    // Every line is ended, so the last piece a split leaves is always empty.
    private static string[] Lines(string text)
    {
        Assert.EndsWith(LogFormat.LineEnd, text, StringComparison.Ordinal);

        return text[..^LogFormat.LineEnd.Length].Split(LogFormat.LineEnd);
    }
}
