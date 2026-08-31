// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using WinDav.Core.Providers;

namespace WinDav.Cli;

/// <summary>
/// What was asked about keeping: how long a mount may go on believing what the server told it
/// about an entry.
/// </summary>
/// <remarks>
/// <para>
/// One option, <c>--attributes</c>, an environment variable as well and the command line the
/// more particular of the two, in the manner of the four of the log and the three of the read
/// path. It takes <c>off</c>, and <c>0</c> for the same thing, and off is today's behaviour:
/// a request per question, which is two of them for every file that is opened. That is not a
/// courtesy either — it is how a report about a stale directory is narrowed down to the layer
/// that caused it.
/// </para>
/// <para>
/// The default belongs to <see cref="AttributeCache"/>; what is here is only the reading of
/// what was typed. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#75-the-read-path-read-ahead-keep-attributes-briefly-and-let-the-server-set-the-width">decision 75</see>.
/// </para>
/// </remarks>
internal static class CacheSwitches
{
    /// <summary>The option that says how long an entry is believed.</summary>
    internal const string LifetimeOption = "--attributes";

    private const string Off = "off";

    /// <summary>
    /// Reads the option and takes it out of the command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="environment">
    /// Where a variable is looked up, or <see langword="null"/> for the environment of this
    /// process. A test hands its own in and leaves the process alone.
    /// </param>
    /// <returns>
    /// How long an entry is held, which is the default when nothing was asked, and nothing at
    /// all when the cache was switched off.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="UsageException">
    /// The option was given without a value, or with one that is not a length of time.
    /// </exception>
    internal static TimeSpan Read(CommandLine line, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

        return Switches.Asked(line, LifetimeOption, read, out string? lifetime)
            ? ReadLifetime(lifetime)
            : AttributeCache.DefaultLifetime;
    }

    // Seconds, or a number with s, m or h after it, in the same manner as --for takes them.
    // Seconds without a letter because seconds are what this is measured in: a value worth
    // giving in hours is one this was not built for.
    private static TimeSpan ReadLifetime(string? value)
    {
        if (value is null)
        {
            throw new UsageException($"The option {LifetimeOption} needs a value.");
        }

        string text = value.Trim();

        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        long scale = 1;

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

        // Whole seconds and no sign, the way the three sizes of the read path are read. Zero
        // is off written as a number: a cache that holds nothing is a request per question.
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
            || count > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond / scale)
        {
            throw Unreadable(value);
        }

        return TimeSpan.FromSeconds(count * scale);
    }

    private static UsageException Unreadable(string value) =>
        new($"'{value}' is not a length of time. {LifetimeOption} takes seconds, or a number with s, m or h after it, as in 10s, or {Off}.");
}
