// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Fsp;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;
using Xunit;

namespace WinDav.Fs.Tests;

// What this half of the product writes down. The seam is a plain ILoggerFactory, so no file is
// involved and what is asserted is the record itself: every operation that costs a round trip
// says what it was and what it cost, which is the half of a read that issue 26 lays beside the
// requests underneath it.
public sealed class WinDavFileSystemLoggingTests
{
    [Fact]
    public void AReadSaysWhatWasAskedForAndWhatItCost()
    {
        RecordingLoggerFactory logging = new(LogLevel.Debug);
        WinDavFileSystem fileSystem = Mount(Store(), logging);

        Read(fileSystem, OpenExisting(fileSystem, "\\note.txt"), 0, 5);

        Assert.Contains(
            "Read 5 bytes of /note.txt at 0, 5 back in ",
            Written(logging, LogLevel.Debug),
            StringComparison.Ordinal);

        // Nothing louder than what was asked for.
        Assert.Empty(Records(logging, LogLevel.Trace));
    }

    [Fact]
    public void AtTraceTheReadIsSaidBeforeItIsWaitedFor()
    {
        RecordingLoggerFactory logging = new(LogLevel.Trace);
        WinDavFileSystem fileSystem = Mount(Store(), logging);

        Read(fileSystem, OpenExisting(fileSystem, "\\note.txt"), 0, 5);

        int asked = Said(logging, LogLevel.Trace, "Reading 5 bytes of /note.txt at 0.");
        int answered = Said(logging, LogLevel.Debug, "Read 5 bytes of /note.txt at 0, 5 back in ");

        // A read that never comes back has still said what it was after, which is the whole
        // difference between the two levels on this side.
        Assert.NotEqual(-1, asked);
        Assert.InRange(answered, asked + 1, int.MaxValue);
    }

    // Windows puts this one in front of whoever asked for the file, so it is written down
    // whether a recording was asked for or not.
    [Fact]
    public void AReadThatFailedIsWrittenDownWithNothingSwitchedOn()
    {
        FakeStore store = Store();

        RecordingLoggerFactory logging = new(LogLevel.Information);
        WinDavFileSystem fileSystem = Mount(store, logging);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        store.FailWith = ProviderError.Unreachable;

        Assert.Equal(FileSystemBase.STATUS_UNEXPECTED_NETWORK_ERROR, Read(fileSystem, fileDesc, 0, 5));

        Assert.Contains(
            "Reading /note.txt at 0 failed after ",
            Written(logging, LogLevel.Warning),
            StringComparison.Ordinal);

        Assert.Empty(Records(logging, LogLevel.Debug));
    }

    // "The drive is read only" is the first thing a person asks about, and Windows' own
    // wording for it names no operation.
    [Fact]
    public void ARefusedWriteSaysWhichOperationItWas()
    {
        RecordingLoggerFactory logging = new(LogLevel.Debug);
        WinDavFileSystem fileSystem = Mount(Store(), logging);

        Assert.Equal(
            FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED,
            fileSystem.SetVolumeLabel("Anything", out _));

        Assert.Contains(
            "Refused SetVolumeLabel: everything on this volume is read only.",
            Written(logging, LogLevel.Debug),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AListingIsCountedAndEveryEntryHandedBackIsATraceOfItsOwn()
    {
        RecordingLoggerFactory logging = new(LogLevel.Trace);
        WinDavFileSystem fileSystem = Mount(Store(), logging);

        object directory = OpenExisting(fileSystem, "\\");
        object? context = null;

        while (fileSystem.ReadDirectoryEntry(null, directory, null, null, ref context, out _, out _))
        {
        }

        // One record for the request, which is what it cost, and one per entry, which is what
        // came of it. The listing is fetched once however often WinFsp asks for a next name.
        Assert.Contains("Listed 2 entries of / in ", Written(logging, LogLevel.Debug), StringComparison.Ordinal);
        Assert.Contains("Handed note.txt of / back.", Written(logging, LogLevel.Trace), StringComparison.Ordinal);
        Assert.Contains("Handed other.txt of / back.", Written(logging, LogLevel.Trace), StringComparison.Ordinal);
    }

    // A name that is not there is what the Explorer asks about several times per window, so it
    // is the ordinary answer and not something to be warned about.
    [Fact]
    public void AskingAboutANameThatIsNotThereIsNoWarning()
    {
        RecordingLoggerFactory logging = new(LogLevel.Debug);
        WinDavFileSystem fileSystem = Mount(Store(), logging);

        byte[]? descriptor = null;

        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            fileSystem.GetSecurityByName("\\nothing.txt", out _, ref descriptor));

        Assert.Empty(Records(logging, LogLevel.Warning));
        Assert.Contains("Asked about /nothing.txt in ", Written(logging, LogLevel.Debug), StringComparison.Ordinal);
    }

    private static FakeStore Store()
    {
        FakeStore store = new();

        store.AddFile("/note.txt", "hello");
        store.AddFile("/other.txt", "there");

        return store;
    }

    private static WinDavFileSystem Mount(FakeStore store, ILoggerFactory logging) =>
        new(store, new MountSettings { RemotePath = "/", VolumeLabel = "Test" }, logging);

    private static object OpenExisting(WinDavFileSystem fileSystem, string fileName)
    {
        int status = fileSystem.Open(fileName, 0, 0, out _, out object? fileDesc, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.NotNull(fileDesc);

        return fileDesc;
    }

    private static int Read(WinDavFileSystem fileSystem, object fileDesc, ulong offset, uint length)
    {
        IntPtr buffer = Marshal.AllocHGlobal((int)length);

        try
        {
            return fileSystem.Read(null, fileDesc, buffer, offset, length, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> Records(RecordingLoggerFactory logging, LogLevel level) =>
        [.. logging.Written.Where(record => record.Level == level).Select(record => record.Message)];

    private static string Written(RecordingLoggerFactory logging, LogLevel level) =>
        string.Join('\n', Records(logging, level));

    // Where a record stands among the others, so that what was written before a wait can be
    // told from what was written after it.
    private static int Said(RecordingLoggerFactory logging, LogLevel level, string beginning)
    {
        for (int index = 0; index < logging.Written.Count; index++)
        {
            (LogLevel wrote, string message) = logging.Written[index];

            if (wrote == level && message.StartsWith(beginning, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    // The seam as the program has it, with the file sink left out: what the file system writes
    // is kept in the order it was written, and how much of it is written at all is the one
    // thing a recording decides.
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly LogLevel _minimum;

        internal RecordingLoggerFactory(LogLevel minimum)
        {
            _minimum = minimum;
        }

        internal List<(LogLevel Level, string Message)> Written { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() => GC.SuppressFinalize(this);

        internal bool Enabled(LogLevel level) => level != LogLevel.None && level >= _minimum;

        internal void Add(LogLevel level, string message) => Written.Add((level, message));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerFactory _written;

        internal RecordingLogger(RecordingLoggerFactory written)
        {
            _written = written;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _written.Enabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (_written.Enabled(logLevel))
            {
                _written.Add(logLevel, formatter(state, exception));
            }
        }
    }
}
