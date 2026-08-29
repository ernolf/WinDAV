// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// Hands out loggers that write to one file.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what a container would otherwise be asked for. A program builds one
/// of these, asks it for a logger per class, and disposes it on the way out; the file it
/// writes to belongs to whoever made it. What is deliberately not here is the rest of the
/// stack: no host, no service collection, no configuration binder. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#74-logging-five-levels-four-areas-and-a-switch-that-turns-itself-off">decision 74</see>.
/// </para>
/// <para>
/// The seam is the one every C# programmer already knows, so a test that wants to read what
/// was logged puts its own <see cref="ILoggerFactory"/> in the way and needs nothing from
/// this file at all.
/// </para>
/// </remarks>
public sealed class FileLoggerFactory : ILoggerFactory
{
    private readonly LogFile _file;
    private readonly LogRecording? _recording;
    private readonly LogLevel _minimum;

    private bool _disposed;

    /// <summary>
    /// Initialises a new instance of the <see cref="FileLoggerFactory"/> class.
    /// </summary>
    /// <param name="file">Where the records go. It is not disposed with the factory.</param>
    /// <param name="recording">
    /// What was asked for on top of the levels that are always on, or <see langword="null"/>
    /// when nothing was. It is not disposed with the factory either.
    /// </param>
    /// <param name="minimum">
    /// The quietest level that is still written. <see cref="LogLevels.Default"/> unless
    /// another is given, and <see cref="LogLevel.None"/> to write nothing at all.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <remarks>
    /// One recording for all the loggers, because it is one spell of loud logging with one
    /// clock and one budget, however many classes write into it.
    /// </remarks>
    public FileLoggerFactory(
        LogFile file,
        LogRecording? recording = null,
        LogLevel minimum = LogLevels.Default)
    {
        ArgumentNullException.ThrowIfNull(file);

        _file = file;
        _recording = recording;
        _minimum = minimum;
    }

    /// <summary>
    /// Gets the file the records are going into, and <see langword="null"/> while nothing has
    /// been written yet.
    /// </summary>
    public string? FilePath => _file.FilePath;

    /// <summary>
    /// Makes a logger for one category, which is the full name of a type.
    /// </summary>
    /// <param name="categoryName">The category. Its namespace decides the area.</param>
    /// <returns>A logger over the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="categoryName"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(categoryName);

        return new FileLogger(_file, LogAreas.Of(categoryName), _minimum, _recording);
    }

    /// <summary>
    /// Not supported. This factory writes to one file and knows about no other sink.
    /// </summary>
    /// <param name="provider">The sink that would have been added.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// A second sink — the event log of a service, a pipe — is added by writing it here, and
    /// no call site changes for it. Taking one in from outside would mean carrying the whole
    /// of the composition the rest of this class exists to avoid.
    /// </remarks>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException($"{nameof(FileLoggerFactory)} writes to one file and takes no further sink.");

    /// <summary>
    /// Stops handing out loggers. The file is left to whoever made it.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
