// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using WinDav.Core.Providers;

namespace WinDav.Cli;

/// <summary>
/// What was asked about listing: how far below an open directory a mount may list ahead, how
/// many requests one round of that may make, how many listings it may hold, and when a name
/// that is nowhere stops buying one.
/// </summary>
/// <remarks>
/// <para>
/// Four options: <c>--list-ahead</c>, <c>--list-ahead-requests</c>, <c>--listings</c> and
/// <c>--probes</c>, each an environment variable as well and the command line the more
/// particular of the two. Every one of them takes <c>off</c>, and with the first three off a
/// directory is listed when it is opened and at no other time, which is how a report about a
/// directory that showed the wrong contents is narrowed down to the layer that caused it.
/// </para>
/// <para>
/// How long a listing is believed is not asked here: it is the same length of time as an
/// entry, <c>--attributes</c>, because it is the same request that says so. Switching that off
/// switches this off with it.
/// </para>
/// <para>
/// The defaults are what was counted over a real account in
/// <see href="https://github.com/ernolf/WinDAV/issues/27">#27</see> and belong to
/// <see cref="DirectorySettings"/>; what is here is only the reading of what was typed. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#76-listings-are-kept-an-etag-says-whether-they-still-hold-and-f5-throws-them-away">decision 76</see>.
/// </para>
/// </remarks>
internal static class DirectorySwitches
{
    /// <summary>The option that says how far below an open directory a mount lists ahead.</summary>
    internal const string DepthOption = "--list-ahead";

    /// <summary>The option that says how many requests one round of that may make.</summary>
    internal const string RequestsOption = "--list-ahead-requests";

    /// <summary>The option that says how many listings may be held at once.</summary>
    internal const string DirectoriesOption = "--listings";

    /// <summary>
    /// The option that says in how many directories a name must have been looked for and not
    /// found before a question about it stops buying a listing.
    /// </summary>
    internal const string ProbesOption = "--probes";

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
    /// An option was given without a value, or with one that is not a number of the thing that
    /// option counts.
    /// </exception>
    internal static DirectorySettings Read(CommandLine line, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

        int depth = Switches.Asked(line, DepthOption, read, out string? levels)
            ? ReadCount(levels, DepthOption, "levels")
            : DirectorySettings.DefaultDepth;

        int requests = Switches.Asked(line, RequestsOption, read, out string? round)
            ? ReadCount(round, RequestsOption, "requests")
            : DirectorySettings.DefaultRequests;

        int directories = Switches.Asked(line, DirectoriesOption, read, out string? held)
            ? ReadCount(held, DirectoriesOption, "directories")
            : DirectorySettings.DefaultDirectories;

        int probes = Switches.Asked(line, ProbesOption, read, out string? nowhere)
            ? ReadCount(nowhere, ProbesOption, "directories")
            : DirectorySettings.DefaultProbes;

        return new DirectorySettings
        {
            Depth = depth,
            Requests = requests,
            Directories = directories,
            Probes = probes,
        };
    }

    // Whole numbers and no sign, the way the width of the read path is read. Zero is off
    // written as a number, and it is a real value for all four: no level below, no request in
    // a round, no listing held, and no name ever taken for a probe.
    private static int ReadCount(string? value, string option, string counted)
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

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            throw new UsageException(
                $"'{value}' is not a number of {counted}. {option} takes 0 or more, or {Off}.");
        }

        return count;
    }
}
