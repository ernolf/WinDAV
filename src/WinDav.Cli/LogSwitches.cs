// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.Extensions.Logging;
using WinDav.Core;
using WinDav.Core.Logging;

namespace WinDav.Cli;

/// <summary>
/// What was asked about the log: the floor of what is written, and what is recorded on top
/// of it for a while.
/// </summary>
/// <remarks>
/// <para>
/// Four options, and they belong to the program rather than to any command: <c>--log</c>,
/// taking the level the file is given at least, <c>--debug</c> and <c>--trace</c>, each
/// taking the areas it covers, and <c>--for</c>, taking how long. They are read and taken out
/// before a command sees the command line, so that any of them can be written in front of
/// anything without every command having to know the four names.
/// </para>
/// <para>
/// Each of the four is an environment variable as well, read when the option is not there. A
/// service, a scheduled task and a script that starts twenty mounts have no command line
/// anyone edits at the moment it matters, and every one of them has an environment. The
/// command line wins, being the more particular of the two.
/// </para>
/// <para>
/// This class turns what was typed into what a <see cref="LogRecording"/> takes, and turns
/// anything else into the sentence that says what was wrong with it. It starts nothing: the
/// file the recording writes to is not open yet at the point the command line is read. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#74-logging-five-levels-four-areas-and-a-switch-that-turns-itself-off">decision 74</see>.
/// </para>
/// </remarks>
internal sealed class LogSwitches
{
    /// <summary>The option that says how much of the log is always written.</summary>
    internal const string LevelOption = "--log";

    /// <summary>The option that asks for the lower of the two switched levels.</summary>
    internal const string DebugOption = "--debug";

    /// <summary>The option that asks for the louder one.</summary>
    internal const string TraceOption = "--trace";

    /// <summary>The option that says how long a recording runs.</summary>
    internal const string ForOption = "--for";

    private const string EveryArea = "all";
    private const char AreaSeparator = ',';

    private LogSwitches(
        LogLevel minimum,
        LogLevel? level,
        IReadOnlyList<LogArea> areas,
        TimeSpan duration)
    {
        Minimum = minimum;
        Level = level;
        Areas = areas;
        Duration = duration;
    }

    /// <summary>
    /// Gets the quietest level that is still written whatever else happens.
    /// </summary>
    internal LogLevel Minimum { get; }

    /// <summary>
    /// Gets the level that was asked for on top of that, or <see langword="null"/> when
    /// nothing was recorded.
    /// </summary>
    internal LogLevel? Level { get; }

    /// <summary>
    /// Gets the areas the recording covers.
    /// </summary>
    internal IReadOnlyList<LogArea> Areas { get; }

    /// <summary>
    /// Gets how long it runs.
    /// </summary>
    internal TimeSpan Duration { get; }

    /// <summary>
    /// Gets the name of the environment variable an option is also read from.
    /// </summary>
    /// <param name="option">One of the four options.</param>
    /// <returns>The name, in the shape <c>WINDAV_LOG</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="option"/> is null.</exception>
    internal static string Variable(string option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return $"{ProductInfo.Slug}_{option.TrimStart('-')}".ToUpperInvariant();
    }

    /// <summary>
    /// Reads the four options and takes them out of the command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="environment">
    /// Where a variable is looked up, or <see langword="null"/> for the environment of this
    /// process. A test hands its own in and leaves the process alone.
    /// </param>
    /// <returns>What was asked, which is the default when nothing was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="UsageException">
    /// A level or a time was given without a value, both switched levels were asked for at
    /// once, a time was given with nothing to record, a level or an area has a name none of
    /// them has, or the time is not one.
    /// </exception>
    internal static LogSwitches Read(CommandLine line, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

        bool floor = Asked(line, LevelOption, read, out string? levelName);
        bool debug = Asked(line, DebugOption, read, out string? debugAreas);
        bool trace = Asked(line, TraceOption, read, out string? traceAreas);
        bool timed = Asked(line, ForOption, read, out string? duration);

        LogLevel minimum = floor ? ReadLevel(levelName) : LogLevels.Default;

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

            // The floor on its own has neither a clock nor a budget over it. That is what it
            // is for: a mount watched across a week is watched at the level it was started at.
            return new LogSwitches(minimum, level: null, [], TimeSpan.Zero);
        }

        if (timed && duration is null)
        {
            throw new UsageException($"The option {ForOption} needs a value.");
        }

        string option = trace ? TraceOption : DebugOption;

        return new LogSwitches(
            minimum,
            trace ? LogLevel.Trace : LogLevel.Debug,
            ReadAreas(trace ? traceAreas : debugAreas, option),
            ReadDuration(duration));
    }

    /// <summary>
    /// Starts the recording this stands for.
    /// </summary>
    /// <param name="file">Where it writes.</param>
    /// <returns>
    /// The recording, running, or <see langword="null"/> when none was asked for.
    /// </returns>
    internal LogRecording? Start(LogFile file) =>
        Level is null ? null : new LogRecording(file, Level.Value, Areas, Duration);

    // The option first, and the variable only where the option is not. What is written on the
    // command line is written for this one run; what is in the environment was put there for
    // whatever runs there, and the more particular of the two wins.
    private static bool Asked(
        CommandLine line,
        string option,
        Func<string, string?> environment,
        out string? value)
    {
        if (line.Take(option, out value))
        {
            return true;
        }

        value = environment(Variable(option));

        if (string.IsNullOrWhiteSpace(value))
        {
            value = null;

            return false;
        }

        return true;
    }

    private static LogLevel ReadLevel(string? value)
    {
        if (value is null)
        {
            throw new UsageException($"The option {LevelOption} needs a value.");
        }

        if (!LogLevels.TryParse(value.Trim(), out LogLevel level))
        {
            throw new UsageException($"There is no level named '{value}'. {LevelOption} takes {LevelNames()}.");
        }

        return level;
    }

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
                throw new UsageException($"There is no area named '{name}'. {option} takes {AreaNames()}, or {EveryArea}.");
            }

            if (!areas.Contains(area))
            {
                areas.Add(area);
            }
        }

        if (areas.Count == 0)
        {
            throw new UsageException($"{option} needs an area, or {EveryArea}. It takes {AreaNames()}.");
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

    private static string AreaNames() => string.Join(", ", LogAreas.All.Select(LogAreas.Name));

    private static string LevelNames() => string.Join(", ", LogLevels.All.Select(LogLevels.Name));
}
