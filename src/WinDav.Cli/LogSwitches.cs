// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;

namespace WinDav.Cli;

/// <summary>
/// What was asked for on top of the levels that are always on.
/// </summary>
/// <remarks>
/// <para>
/// Three options, and they belong to the program rather than to any command: <c>--debug</c>
/// and <c>--trace</c>, each taking the areas it covers, and <c>--for</c>, taking how long.
/// They are read and taken out before a command sees the command line, so that a recording
/// can be asked for in front of anything without every command having to know the three
/// names.
/// </para>
/// <para>
/// This class turns what was typed into what a <see cref="LogRecording"/> takes, and turns
/// anything else into the sentence that says what was wrong with it. It starts nothing: the
/// file the recording writes to is not open yet at the point the command line is read.
/// See decisions.md 74.
/// </para>
/// </remarks>
internal sealed class LogSwitches
{
    /// <summary>The option that asks for the lower of the two switched levels.</summary>
    internal const string DebugOption = "--debug";

    /// <summary>The option that asks for the louder one.</summary>
    internal const string TraceOption = "--trace";

    /// <summary>The option that says how long a recording runs.</summary>
    internal const string ForOption = "--for";

    private const string EveryArea = "all";
    private const char AreaSeparator = ',';

    private LogSwitches(LogLevel level, IReadOnlyList<LogArea> areas, TimeSpan duration)
    {
        Level = level;
        Areas = areas;
        Duration = duration;
    }

    /// <summary>
    /// Gets the level that was asked for.
    /// </summary>
    internal LogLevel Level { get; }

    /// <summary>
    /// Gets the areas it covers.
    /// </summary>
    internal IReadOnlyList<LogArea> Areas { get; }

    /// <summary>
    /// Gets how long it runs.
    /// </summary>
    internal TimeSpan Duration { get; }

    /// <summary>
    /// Reads the three options and takes them out of the command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <returns>What was asked for, or <see langword="null"/> when nothing was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="UsageException">
    /// Both levels were asked for at once, a time was given with nothing to record, an area
    /// has a name no area has, or the time is not one.
    /// </exception>
    internal static LogSwitches? Read(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        bool debug = line.Take(DebugOption, out string? debugAreas);
        bool trace = line.Take(TraceOption, out string? traceAreas);
        bool timed = line.Take(ForOption, out string? duration);

        if (debug && trace)
        {
            // Trace is the louder of the two and would swallow the other, but which one was
            // meant is a guess, and a recording is asked for while something is going wrong.
            throw new UsageException($"{DebugOption} and {TraceOption} ask for two recordings. Ask for one.");
        }

        if (!debug && !trace)
        {
            if (timed)
            {
                throw new UsageException(
                    $"{ForOption} says how long to record, and nothing was asked to be recorded. Add {DebugOption} or {TraceOption}.");
            }

            return null;
        }

        if (timed && duration is null)
        {
            throw new UsageException($"The option {ForOption} needs a value.");
        }

        string option = trace ? TraceOption : DebugOption;

        return new LogSwitches(
            trace ? LogLevel.Trace : LogLevel.Debug,
            ReadAreas(trace ? traceAreas : debugAreas, option),
            ReadDuration(duration));
    }

    /// <summary>
    /// Starts the recording this stands for.
    /// </summary>
    /// <param name="file">Where it writes.</param>
    /// <returns>The recording, running.</returns>
    internal LogRecording Start(LogFile file) => new(file, Level, Areas, Duration);

    private static IReadOnlyList<LogArea> ReadAreas(string? value, string option)
    {
        // Written on its own, which is the way to ask for a recording without having decided
        // yet where the trouble is.
        if (value is null)
        {
            return LogAreas.All;
        }

        List<LogArea> areas = [];

        foreach (string name in value.Split(
            AreaSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(name, EveryArea, StringComparison.OrdinalIgnoreCase))
            {
                return LogAreas.All;
            }

            if (!LogAreas.TryParse(name, out LogArea area))
            {
                throw new UsageException($"There is no area named '{name}'. {option} takes {Names()}, or {EveryArea}.");
            }

            if (!areas.Contains(area))
            {
                areas.Add(area);
            }
        }

        if (areas.Count == 0)
        {
            throw new UsageException($"{option} needs an area, or {EveryArea}. It takes {Names()}.");
        }

        return areas;
    }

    private static TimeSpan ReadDuration(string? value)
    {
        if (value is null)
        {
            return LogRecording.DefaultDuration;
        }

        string text = value.Trim();
        double scale = 1;

        if (text.Length > 0 && !char.IsAsciiDigit(text[^1]))
        {
            scale = char.ToLowerInvariant(text[^1]) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 60 * 60,
                _ => throw Unreadable(value),
            };

            text = text[..^1].TrimEnd();
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double count) || count <= 0)
        {
            throw Unreadable(value);
        }

        TimeSpan duration = TimeSpan.FromSeconds(count * scale);

        if (duration > LogRecording.MaximumDuration)
        {
            throw new UsageException(
                $"A recording runs for at most {LogRecording.MaximumDuration.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes, and '{value}' is longer.");
        }

        return duration;
    }

    private static UsageException Unreadable(string value) =>
        new($"'{value}' is not a length of time. {ForOption} takes seconds, or a number with s, m or h after it, as in 90s or 5m.");

    private static string Names() => string.Join(", ", LogAreas.All.Select(LogAreas.Name));
}
