// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class FileLoggerFactoryTests : IDisposable
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

    [Fact]
    public void WithoutAFloorOfItsOwnItWritesWhatIsAlwaysOn()
    {
        string written = Written(LogLevels.Default);

        Assert.Contains("A mount is up.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Read 65536 bytes.", written, StringComparison.Ordinal);
    }

    // The floor is what is written whatever happens, and there is no clock over it: what a
    // person starts a mount at is what that mount goes on writing.
    [Fact]
    public void AFloorOfItsOwnIsWrittenWithNothingAskedForAndNoEnd()
    {
        string written = Written(LogLevel.Debug);

        Assert.Contains("A mount is up.", written, StringComparison.Ordinal);
        Assert.Contains("Read 65536 bytes.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("recording", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelBelowTheFloorIsNotWritten()
    {
        string written = Written(LogLevel.Error);

        Assert.Contains("A mount failed.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("A mount is up.", written, StringComparison.Ordinal);
    }

    // Off is a floor above every level there is, and a file nothing was written to was never
    // made: the quiet a person asks for leaves nothing behind to be found later.
    [Fact]
    public void OffWritesNothingAndLeavesNoFile()
    {
        using LogFile file = new(_directory, Command);
        using FileLoggerFactory logging = new(file, recording: null, LogLevel.None);

        ILogger log = logging.CreateLogger("WinDav.Fs.WinDavFileSystem");

        log.LogError("A mount failed.");
        log.LogInformation("A mount is up.");

        Assert.Null(file.FilePath);
        Assert.Null(logging.FilePath);
        Assert.False(Directory.Exists(_directory));
    }

    // The floor and the recording are read apart from each other, so a recording asked for
    // with the floor at off is still a recording. It is what was asked for, and it ends the
    // way every recording ends.
    [Fact]
    public void ARecordingIsWrittenEvenWhenTheFloorIsOff()
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using LogRecording recording =
                new(file, LogLevel.Trace, [LogArea.Fs], TimeSpan.FromMinutes(5));

            using FileLoggerFactory logging = new(file, recording, LogLevel.None);

            logging.CreateLogger("WinDav.Fs.WinDavFileSystem").LogTrace("Read 65536 bytes.");
            logging.CreateLogger("WinDav.Cli.Program").LogError("A mount failed.");

            Assert.Equal(1L, recording.Records);

            path = Assert.IsType<string>(file.FilePath);
        }

        string written = File.ReadAllText(path);

        Assert.Contains("Read 65536 bytes.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("A mount failed.", written, StringComparison.Ordinal);
    }

    private string Written(LogLevel minimum)
    {
        string path;

        using (LogFile file = new(_directory, Command))
        {
            using FileLoggerFactory logging = new(file, recording: null, minimum);

            ILogger log = logging.CreateLogger("WinDav.Fs.WinDavFileSystem");

            log.LogError("A mount failed.");
            log.LogInformation("A mount is up.");
            log.LogDebug("Read 65536 bytes.");

            path = Assert.IsType<string>(file.FilePath);
        }

        return File.ReadAllText(path);
    }
}
