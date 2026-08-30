// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;

namespace WinDav.Cli;

/// <summary>
/// The two rules every option of the program itself follows: what its environment variable is
/// called, and which of the two wins.
/// </summary>
/// <remarks>
/// Options that belong to the program rather than to a command are read and taken out before
/// a command sees the command line, and each of them is an environment variable as well. A
/// service, a scheduled task and a script that starts twenty mounts have no command line
/// anyone edits at the moment it matters, and every one of them has an environment. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#74-logging-five-levels-four-areas-and-a-switch-that-turns-itself-off">decision 74</see>.
/// </remarks>
internal static class Switches
{
    /// <summary>
    /// Gets the name of the environment variable an option is also read from.
    /// </summary>
    /// <param name="option">The option, dashes and all.</param>
    /// <returns>
    /// The name, in the shape <c>WINDAV_LOG</c>. A dash inside the option becomes an
    /// underscore, because a dash is arithmetic to a shell and a variable carrying one can
    /// be set but not read.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="option"/> is null.</exception>
    internal static string Variable(string option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return $"{ProductInfo.Slug}_{option.TrimStart('-').Replace('-', '_')}".ToUpperInvariant();
    }

    /// <summary>
    /// Reads one option, from the command line where it is there and from the environment
    /// where it is not, and takes it out of the command line either way.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="option">The option, dashes and all.</param>
    /// <param name="environment">Where a variable is looked up.</param>
    /// <param name="value">What followed it, or <see langword="null"/> for nothing.</param>
    /// <returns>Whether it was asked for at all.</returns>
    /// <exception cref="UsageException">The option was written twice.</exception>
    internal static bool Asked(
        CommandLine line,
        string option,
        Func<string, string?> environment,
        out string? value)
    {
        // The option first, and the variable only where the option is not. What is written on
        // the command line is written for this one run; what is in the environment was put
        // there for whatever runs there, and the more particular of the two wins.
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
}
