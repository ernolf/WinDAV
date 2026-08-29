// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// One logger, for one area, over one file.
/// </summary>
/// <remarks>
/// The area is worked out once, when the logger is made, because the category it comes from
/// cannot change afterwards. What is asked every time is the recording: whether one is
/// running, whether it covers this area, and whether it is still allowed to.
/// </remarks>
internal sealed class FileLogger : ILogger
{
    private readonly LogFile _file;
    private readonly LogArea _area;
    private readonly LogLevel _minimum;
    private readonly LogRecording? _recording;

    internal FileLogger(LogFile file, LogArea area, LogLevel minimum, LogRecording? recording)
    {
        _file = file;
        _area = area;
        _minimum = minimum;
        _recording = recording;
    }

    // Scopes are not kept. A record says what it says, and nothing about it depends on what
    // was open around it. Null is what the interface allows for that, and what callers of it
    // are written to expect.
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && (logLevel >= _minimum || Recorded(logLevel));

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (logLevel == LogLevel.None)
        {
            return;
        }

        bool always = logLevel >= _minimum;

        // Asked once, and the answer used twice: whether the recording is what let this
        // record through decides whether the record counts against what it may write.
        bool recorded = !always && Recorded(logLevel);

        if (!always && !recorded)
        {
            return;
        }

        int bytes = _file.Write(DateTimeOffset.Now, logLevel, _area, formatter(state, exception), exception);

        if (recorded)
        {
            _recording?.Note(bytes);
        }
    }

    private bool Recorded(LogLevel logLevel) => _recording?.Covers(_area, logLevel) == true;
}
