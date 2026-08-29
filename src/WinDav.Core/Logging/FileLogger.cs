// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// One logger, for one area, over one file.
/// </summary>
/// <remarks>
/// The area is worked out once, when the logger is made, because the category it comes from
/// cannot change afterwards.
/// </remarks>
internal sealed class FileLogger : ILogger
{
    private readonly LogFile _file;
    private readonly LogArea _area;
    private readonly LogLevel _minimum;

    internal FileLogger(LogFile file, LogArea area, LogLevel minimum)
    {
        _file = file;
        _area = area;
        _minimum = minimum;
    }

    // Scopes are not kept. A record says what it says, and nothing about it depends on what
    // was open around it. Null is what the interface allows for that, and what callers of it
    // are written to expect.
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimum;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        _file.Write(DateTimeOffset.Now, logLevel, _area, formatter(state, exception), exception);
    }
}
