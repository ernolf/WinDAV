// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// The file the records go into, and the only thing in this product that writes one.
/// </summary>
/// <remarks>
/// <para>
/// One file per session, named after the moment it was opened and the process that opened
/// it, so that two mounts running at once do not write over each other and a reader can tell
/// which file is which. Nothing is created until the first record: a command that says
/// nothing leaves nothing behind.
/// </para>
/// <para>
/// A file that has grown to <see cref="MaximumFileBytes"/> is closed and the next one is
/// opened. What that leaves behind is packed by the next file this opens, and the oldest are
/// taken away so that at most <see cref="KeptFiles"/> remain. The last file of a session
/// stays as it is until the next one begins, because the one a person is about to read
/// should not have to be unpacked first.
/// </para>
/// <para>
/// Nothing here throws at the caller. A log that cannot be written is a nuisance; a log that
/// takes the program down with it is a defect. The first failure closes the file and every
/// later record is dropped in silence, while the console goes on saying what it was going to
/// say. See decisions.md 74.
/// </para>
/// </remarks>
public sealed class LogFile : IDisposable
{
    /// <summary>
    /// The directory the files live in, below the machine-local data directory.
    /// </summary>
    public const string DirectoryName = "logs";

    /// <summary>
    /// How large one file may become before the next one is opened.
    /// </summary>
    /// <remarks>
    /// The same sixteen megabytes decision 74 gives a recording, so that a trace which was
    /// asked for is one file and not two halves of one.
    /// </remarks>
    public const long MaximumFileBytes = 16L * 1024 * 1024;

    /// <summary>
    /// How many files are kept, the one being written included.
    /// </summary>
    /// <remarks>
    /// What they cost is bounded by <see cref="MaximumFileBytes"/> and by the packing; what
    /// they buy is the session before last, which is where an intermittent fault tends to be
    /// described.
    /// </remarks>
    public const int KeptFiles = 8;

    private const string Extension = ".log";
    private const string CompressedExtension = ".log.gz";
    private const string PackedSuffix = ".gz";
    private const string NameFormat = "yyyyMMdd-HHmmss";

    // Without the byte order mark. It is a file of lines, and a mark in front of the first
    // one is something every reader has to be told about.
    private static readonly UTF8Encoding s_encoding = new(encoderShouldEmitUTF8Identifier: false);

    // Held for the whole of a write, because rolling over is part of one, and because two
    // threads of the pool asking a provider for something is the ordinary case.
    private readonly Lock _gate = new();

    private readonly string _directory;
    private readonly string _command;

    private FileStream? _stream;
    private long _bytes;
    private long _records;
    private bool _broken;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance of the <see cref="LogFile"/> class. Nothing is created
    /// until the first record is written.
    /// </summary>
    /// <param name="directory">Where the files go.</param>
    /// <param name="command">
    /// What was run, with anything sensitive already taken out. It becomes the second line
    /// of every file this instance opens.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    public LogFile(string directory, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(command);

        _directory = directory;
        _command = command;
    }

    /// <summary>
    /// Gets the file being written, or the last one that was, and <see langword="null"/>
    /// while nothing has been written yet.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// Builds a log file in the product's own directory for what belongs to this machine.
    /// </summary>
    /// <param name="command">What was run, with anything sensitive already taken out.</param>
    /// <returns>
    /// A log file over <see cref="DirectoryName"/> below <see cref="ProductInfo.LocalDataDirectory"/>.
    /// </returns>
    /// <remarks>
    /// Local and not roaming, because a log describes one machine and it grows. See
    /// decisions.md 74.
    /// </remarks>
    public static LogFile Default(string command) =>
        new(Path.Combine(ProductInfo.LocalDataDirectory, DirectoryName), command);

    /// <summary>
    /// Writes one record.
    /// </summary>
    /// <param name="when">When it happened.</param>
    /// <param name="level">How loud it is.</param>
    /// <param name="area">Where it came from.</param>
    /// <param name="message">What it says, with anything sensitive already taken out.</param>
    /// <param name="exception">What went wrong, or <see langword="null"/>.</param>
    /// <returns>
    /// How many bytes it took, and zero when nothing was written because the file is closed
    /// or has failed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <remarks>
    /// The count is what a recording measures itself against. It is the size in the file and
    /// not the length of the message, because the limit a person is given is the size of what
    /// they will have to read.
    /// </remarks>
    public int Write(DateTimeOffset when, LogLevel level, LogArea area, string message, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Put(when, LogFormat.Line(when, level, area, message, exception), isRecord: true);
    }

    /// <summary>
    /// Writes one line that says something about the file rather than about the product.
    /// </summary>
    /// <param name="when">When it is being said.</param>
    /// <param name="text">What is being said, with anything sensitive already taken out.</param>
    /// <returns>How many bytes it took, and zero when nothing was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <remarks>
    /// It is not a record and is not counted as one: the line at the end of a file says how
    /// many things happened, and a note about the file is not one of them.
    /// </remarks>
    public int Note(DateTimeOffset when, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Put(when, LogFormat.Comment(when, text), isRecord: false);
    }

    /// <summary>
    /// Closes the file with the line that says how it ended. What is written afterwards is
    /// dropped.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Close(DateTimeOffset.Now);
        }

        GC.SuppressFinalize(this);
    }

    private int Put(DateTimeOffset when, string line, bool isRecord)
    {
        lock (_gate)
        {
            if (_disposed || _broken)
            {
                return 0;
            }

            byte[] payload = s_encoding.GetBytes(line);

            try
            {
                if (_stream is null)
                {
                    Open(when);
                }
                else if (_bytes + payload.Length > MaximumFileBytes)
                {
                    Close(when);
                    Open(when);
                }

                _stream!.Write(payload);
                _stream.Flush();

                _bytes += payload.Length;

                if (isRecord)
                {
                    _records++;
                }
            }
            catch (IOException)
            {
                Break();

                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                Break();

                return 0;
            }

            return payload.Length;
        }
    }

    private void Open(DateTimeOffset when)
    {
        Directory.CreateDirectory(_directory);

        // Whatever an earlier session left behind is packed now, and only now: a file some
        // other process still has open cannot be had exclusively and is passed over. Ours is
        // not open at this point either, which is how a file that was rolled over gets packed
        // through the same door as everything else.
        foreach (string leftover in Files(Extension))
        {
            Pack(leftover);
        }

        string path = Path.Combine(_directory, Name(when));

        // Append rather than create: two files of one process in the same second would be
        // the same name, and one carrying on after the other is better than a failure.
        // Shared for reading, so the file can be looked at while it is being written.
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        FilePath = path;
        _bytes = _stream.Length;
        _records = 0;

        byte[] header = s_encoding.GetBytes(LogFormat.Header(when, _command));

        _stream.Write(header);
        _stream.Flush();

        _bytes += header.Length;

        // After opening, so that the file just opened is the newest of them and is the one
        // kept whatever else goes.
        Prune();
    }

    private void Close(DateTimeOffset when)
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            _stream.Write(s_encoding.GetBytes(LogFormat.Footer(when, _records)));
            _stream.Flush();
        }
        catch (IOException)
        {
            // The line that says how the file ended is a courtesy to whoever reads it. Not
            // being able to write it is not worth a second failure on the way out.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        _stream.Dispose();
        _stream = null;
    }

    private void Break()
    {
        _broken = true;

        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
            // There is nothing left to try. The console still says what failed.
        }

        _stream = null;
    }

    private static string Name(DateTimeOffset when)
    {
        string moment = when.ToString(NameFormat, CultureInfo.InvariantCulture);
        string process = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

        return $"{ProductInfo.Slug}-{moment}-{process}{Extension}";
    }

    private string[] Files(string extension)
    {
        try
        {
            return
            [
                .. Directory
                    .EnumerateFiles(_directory, ProductInfo.Slug + "-*")
                    .Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)),
            ];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Prune()
    {
        List<string> kept =
        [
            .. Files(Extension)
                .Concat(Files(CompressedExtension))
                .OrderByDescending(File.GetLastWriteTimeUtc),
        ];

        for (int index = KeptFiles; index < kept.Count; index++)
        {
            Delete(kept[index]);
        }
    }

    private static void Pack(string path)
    {
        string target = path + PackedSuffix;

        try
        {
            // Exclusive, which is the test as much as the means: a file another process is
            // writing cannot be opened this way, and is left where it is.
            using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using FileStream sink = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            using GZipStream packed = new(sink, CompressionLevel.SmallestSize);

            source.CopyTo(packed);
        }
        catch (IOException)
        {
            Delete(target);

            return;
        }
        catch (UnauthorizedAccessException)
        {
            Delete(target);

            return;
        }

        Delete(path);
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by someone, which on Windows is what a file being written looks
            // like. The next session tries again.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
