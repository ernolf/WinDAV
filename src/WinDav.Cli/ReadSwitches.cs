// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using WinDav.Fs;

namespace WinDav.Cli;

/// <summary>
/// What was asked about reading: how far ahead of a read a mount may fetch, how much of that
/// it may hold, and how many requests it may have on the wire at once.
/// </summary>
/// <remarks>
/// <para>
/// Three options, and like the four of the log they belong to the program rather than to any
/// command: <c>--read-ahead</c>, <c>--read-ahead-total</c> and <c>--requests</c>, each an
/// environment variable as well, and the command line the more particular of the two. Every
/// one of them takes <c>off</c>, which is how a report about a wrong byte is narrowed down to
/// the layer that produced it: with all three off, every read is the one request that read
/// asked for and nothing is held between them.
/// </para>
/// <para>
/// The defaults are what was measured in
/// <see href="https://github.com/ernolf/WinDAV/issues/26">#26</see> and belong to
/// <see cref="ReadSettings"/>; what is here is only the reading of what was typed. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions">decision 75</see>.
/// </para>
/// </remarks>
internal static class ReadSwitches
{
    /// <summary>The option that says how far ahead of a read a mount may fetch.</summary>
    internal const string WindowOption = "--read-ahead";

    /// <summary>The option that says how much of it all the open handles may hold.</summary>
    internal const string TotalOption = "--read-ahead-total";

    /// <summary>The option that says how many requests may be on the wire at once.</summary>
    internal const string RequestsOption = "--requests";

    private const string Off = "off";

    /// <summary>
    /// Reads the three options and takes them out of the command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="environment">
    /// Where a variable is looked up, or <see langword="null"/> for the environment of this
    /// process. A test hands its own in and leaves the process alone.
    /// </param>
    /// <returns>What was asked, which is the default when nothing was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="UsageException">
    /// An option was given without a value, a size or a number cannot be read as one, a
    /// window is larger than one piece of memory, or larger than the ceiling over all of
    /// them.
    /// </exception>
    internal static ReadSettings Read(CommandLine line, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

        long window = Switches.Asked(line, WindowOption, read, out string? ahead)
            ? ReadSize(ahead, WindowOption)
            : ReadSettings.DefaultWindow;

        long total = Switches.Asked(line, TotalOption, read, out string? ceiling)
            ? ReadSize(ceiling, TotalOption)
            : ReadSettings.DefaultTotal;

        int requests = Switches.Asked(line, RequestsOption, read, out string? width)
            ? ReadCount(width)
            : ReadSettings.DefaultRequests;

        // A window is one array and is fetched in one piece, so what an array holds is the
        // end of it. The ceiling is a sum of several and has no such limit.
        if (window > Array.MaxLength)
        {
            throw new UsageException(
                $"{WindowOption} is at most {Array.MaxLength.ToString(CultureInfo.InvariantCulture)} bytes, which is as much as one piece of memory holds.");
        }

        if (total > 0 && window > total)
        {
            throw new UsageException(
                $"{WindowOption} is larger than {TotalOption}, so no handle could ever be given a window. Raise the one or lower the other.");
        }

        return new ReadSettings
        {
            Window = window,
            Total = total,
            Requests = requests,
        };
    }

    // Bytes, or a number with k, m or g after it, in the same manner as --for takes s, m or
    // h. The letters are the binary ones: a mebibyte, because that is what WinFsp asks in
    // and what the measurement was made with.
    private static long ReadSize(string? value, string option)
    {
        if (value is null)
        {
            throw new UsageException($"The option {option} needs a value.");
        }

        string text = value.Trim();

        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        long scale = 1;

        if (text.Length > 0 && !char.IsAsciiDigit(text[^1]))
        {
            scale = char.ToLowerInvariant(text[^1]) switch
            {
                'k' => 1024,
                'm' => 1024 * 1024,
                'g' => 1024 * 1024 * 1024,
                _ => throw UnreadableSize(value, option),
            };

            text = text[..^1].TrimEnd();
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
            || count > long.MaxValue / scale)
        {
            throw UnreadableSize(value, option);
        }

        return count * scale;
    }

    private static int ReadCount(string? value)
    {
        if (value is null)
        {
            throw new UsageException($"The option {RequestsOption} needs a value.");
        }

        string text = value.Trim();

        // One at a time is what switching this off means. There is no number below it: a
        // mount that may have no request on the wire could never read anything.
        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int requests) || requests < 1)
        {
            throw new UsageException(
                $"'{value}' is not a number of requests. {RequestsOption} takes 1 or more, or {Off}, which is one at a time.");
        }

        return requests;
    }

    private static UsageException UnreadableSize(string value, string option) =>
        new($"'{value}' is not a size. {option} takes bytes, or a number with k, m or g after it, as in 8m, or {Off}.");
}
