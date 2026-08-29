// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogFileTests : IDisposable
{
    private const string Command = "windav mount cloud";

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

    // A command that says nothing leaves nothing behind, which is what keeps 'mount list' and
    // '--version' from littering a directory nobody asked for.
    [Fact]
    public void NothingIsMadeUntilThereIsSomethingToWrite()
    {
        using LogFile file = new(_directory, Command);

        Assert.Null(file.FilePath);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void TheFirstRecordOpensTheFile()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            Write(file, "It is up.");

            path = Assert.IsType<string>(file.FilePath);

            Assert.True(File.Exists(path));
        }

        string[] lines = File.ReadAllText(path).Split(LogFormat.LineEnd, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal($"{LogFormat.CommentPrefix}{Command}", lines[1]);
        Assert.Contains("info", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("It is up.", lines[3], StringComparison.Ordinal);

        // The last line is what tells a session that ended from one that was killed.
        Assert.StartsWith($"{LogFormat.CommentPrefix}ended ", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("after 1 record", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsWrittenAfterTheFileIsClosedIsDropped()
    {
        LogFile file = new(_directory, Command);

        Write(file, "It is up.");

        string path = Assert.IsType<string>(file.FilePath);

        file.Dispose();
        Write(file, "It is gone.");

        Assert.DoesNotContain("It is gone.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // What an earlier session left behind is packed by the next one that opens a file, and not
    // by the session itself: the one a person is about to read should not need unpacking.
    [Fact]
    public void AFileFromAnEarlierSessionIsPacked()
    {
        string leftover = Leftover("20200101-000000-1", DateTime.UtcNow.AddDays(-1));

        using LogFile file = new(_directory, Command);

        Write(file, "It is up.");

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(leftover + ".gz"));
    }

    [Fact]
    public void OnlyTheNewestFilesAreKept()
    {
        Directory.CreateDirectory(_directory);

        for (int index = 0; index < LogFile.KeptFiles + 3; index++)
        {
            // Packed already, so that what is counted is the pruning and not what the opening
            // did to a file it found unpacked. The ages are set apart by a day each, because
            // the oldest are what goes.
            string old = Path.Combine(_directory, $"{ProductInfo.Slug}-20200101-{index:000000}-{index}.log.gz");

            File.WriteAllText(old, "an earlier session");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-index - 1));
        }

        using LogFile file = new(_directory, Command);

        Write(file, "It is up.");

        string path = Assert.IsType<string>(file.FilePath);

        Assert.Equal(LogFile.KeptFiles, Directory.GetFiles(_directory).Length);
        Assert.True(File.Exists(path));
    }

    private static void Write(LogFile file, string message) =>
        file.Write(DateTimeOffset.Now, LogLevel.Information, LogArea.Fs, message, null);

    private string Leftover(string name, DateTime written)
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, $"{ProductInfo.Slug}-{name}.log");

        File.WriteAllText(path, $"{LogFormat.CommentPrefix}an earlier session{LogFormat.LineEnd}");
        File.SetLastWriteTimeUtc(path, written);

        return path;
    }
}
