// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// The levels that can be asked for, and what they are called when they are.
/// </summary>
/// <remarks>
/// <para>
/// Five of them are the five a record is written under, spelt the way <see cref="LogFormat"/>
/// spells them in the file: one spelling, learnt once. The sixth is <c>off</c>, which is no
/// level to write at but the floor raised above all of them, so that nothing is written and
/// no file is made.
/// </para>
/// <para>
/// Turning the floor down is as much somebody's to decide as turning it up, and a program
/// that cannot be told to keep quiet is one that is not installed where that is the rule.
/// </para>
/// </remarks>
public static class LogLevels
{
    /// <summary>
    /// The name of the floor that lets nothing through.
    /// </summary>
    public const string OffName = "off";

    /// <summary>
    /// The floor a file is given when nobody says otherwise.
    /// </summary>
    public const LogLevel Default = LogLevel.Information;

    /// <summary>
    /// Gets every level that can be asked for, quietest first, in the order they are listed
    /// in a message to a person.
    /// </summary>
    public static IReadOnlyList<LogLevel> All { get; } =
    [
        LogLevel.None,
        LogLevel.Error,
        LogLevel.Warning,
        LogLevel.Information,
        LogLevel.Debug,
        LogLevel.Trace,
    ];

    /// <summary>
    /// Gets the name a level is asked for under.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The lower-case name, which is the name it is written under as well.</returns>
    public static string Name(LogLevel level) =>
        level == LogLevel.None ? OffName : LogFormat.Name(level);

    /// <summary>
    /// Reads a level from the name it is asked for under.
    /// </summary>
    /// <param name="name">The name, in any case.</param>
    /// <param name="level">The level, when the name is one.</param>
    /// <returns><see langword="true"/> when the name is one of the six.</returns>
    public static bool TryParse(string? name, out LogLevel level)
    {
        foreach (LogLevel candidate in All)
        {
            if (string.Equals(Name(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;

                return true;
            }
        }

        level = Default;

        return false;
    }
}
