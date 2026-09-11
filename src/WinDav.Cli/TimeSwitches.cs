// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using WinDav.Fs;

namespace WinDav.Cli;

/// <summary>
/// What was asked about a directory's times: how long the copy into it has to have stopped
/// before they are set again.
/// </summary>
/// <remarks>
/// <para>
/// One option, <c>--directory-times</c>, an environment variable as well and the command line
/// the more particular of the two, in the manner of the four of the log and the three of the
/// read path. It takes <c>off</c>, and <c>0</c> for the same thing, and off is what the mount
/// did before this was built: a directory carries whatever the copy into it made of its date.
/// </para>
/// <para>
/// The default belongs to <see cref="MountSettings"/>; what is here is only the reading of
/// what was typed. See <see href="https://github.com/ernolf/WinDAV/issues/119">#119</see>.
/// </para>
/// </remarks>
internal static class TimeSwitches
{
    /// <summary>The option that says how long a directory has to be quiet.</summary>
    internal const string QuietOption = "--directory-times";

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
    /// How long a directory has to be quiet, which is the default when nothing was asked, and
    /// nothing at all where it was switched off.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="UsageException">
    /// The option was given without a value, or with one that is not a length of time.
    /// </exception>
    internal static TimeSpan Read(CommandLine line, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

        return Switches.Asked(line, QuietOption, read, out string? quiet)
            ? ReadQuiet(quiet)
            : MountSettings.DefaultDirectoryQuiet;
    }

    // Seconds, or a number with s, m or h after it, the way --attributes is read. Seconds
    // without a letter because seconds are what this is measured in: what it waits for is the
    // pause between two files of a copy.
    private static TimeSpan ReadQuiet(string? value)
    {
        if (value is null)
        {
            throw new UsageException($"The option {QuietOption} needs a value.");
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

        // Whole seconds and no sign, the way a lifetime is read. Zero is off written as a
        // number: a directory whose times are never set again is the mount as it was.
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
            || count > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond / scale)
        {
            throw Unreadable(value);
        }

        return TimeSpan.FromSeconds(count * scale);
    }

    private static UsageException Unreadable(string value) =>
        new($"'{value}' is not a length of time. {QuietOption} takes seconds, or a number with s, m or h after it, as in 5s, or {Off}.");
}
